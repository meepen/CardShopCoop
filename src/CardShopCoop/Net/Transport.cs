using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Minimal TCP transport. Host accepts any number of clients (dad + son = 2 players,
    /// but nothing hardcodes that). All socket work happens on background threads:
    /// received messages land in a ConcurrentQueue that the Unity main thread drains,
    /// and Send() only enqueues - a dedicated writer thread per connection does the
    /// actual Stream.Write, so a stalled peer can never block the caller (Unity's
    /// main thread sends 25-30 frames/s while connected).
    /// </summary>
    public class Transport : ICoopTransport
    {
        public struct WriteStats
        {
            public long Count;
            public long TotalTicks;
            public long MaxTicks;
        }

        private long _writeCount;
        private long _writeTotalTicks;
        private long _writeMaxTicks;
        private const int MaxFrame = Msg.MaxFrameSize; // save files are ~4 MB; hard cap for sanity
        private const int IncomingCap = 10000;
        private const int ReservedTerminalFrames = 256;
        private const int OutgoingFrameCap = 4096;
        private const long OutgoingByteCap = 8L * 1024 * 1024;
        private bool _incomingOverflowWarned;

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<ConnectionEvent> Disconnects { get; } = new ConcurrentQueue<ConnectionEvent>();
        public ConcurrentQueue<ConnectionEvent> Connects { get; } = new ConcurrentQueue<ConnectionEvent>();
        public IReadOnlyList<Connection> Connections
        {
            get
            {
                lock (_connsLock)
                    return new List<Connection>(_conns.Values);
            }
        }

        public void PumpMainThread()
        {
        } // all socket work lives on background threads

        public double TimeoutSeconds => 60.0;

        // TCP is already low-latency and ordered; the fast lane is just the normal lane
        public void Send(Connection connection, INetMessage message)
        {
            SendFrame(connection, NetMessageCodec.Encode(message));
        }
        public void Broadcast(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }
        public void SendTransient(Connection connection, INetMessage message)
        {
            SendFrame(connection, NetMessageCodec.Encode(message));
        }
        public void BroadcastTransient(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }

        /// <summary>Frame sent by a transport-owned thread every 2s per connection.
        /// Keeps the link alive even while Unity's main thread is frozen in a scene load.</summary>
        // Volatile publishes replacements to the keepalive workers. Workers snapshot this
        // reference once per tick and never retain it across a transport teardown.
        public volatile INetMessage KeepaliveMessage;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _lifecycleGeneration;
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly object _threadsLock = new object();

        private readonly Dictionary<int, Conn> _conns = new Dictionary<int, Conn>();
        private readonly object _connsLock = new object();
        private int _nextConnId = 1;

        public bool IsListening
        {
            get; private set;
        }

        private class Conn : Connection
        {
            public Conn(int id) : base(id) { }
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Thread ReadThread;
            public Thread WriteThread;
            public Thread KeepaliveThread;
            // Single writer thread drains this, so frames stay atomic on the wire
            // without a write lock; keepalives are just another queued frame.
            public readonly ConcurrentQueue<byte[]> SendQueue = new ConcurrentQueue<byte[]>();
            public readonly ConcurrentQueue<byte[]> ControlQueue = new ConcurrentQueue<byte[]>();
            public readonly AutoResetEvent SendSignal = new AutoResetEvent(false);
            public readonly AutoResetEvent KeepaliveSignal = new AutoResetEvent(false);
            public readonly object QueueLock = new object();
            public int SendFrames;
            public long SendBytes;
            public volatile bool Alive = true;
            public volatile bool TerminalClaimed;
            public long LastRecvTicksUtc = DateTime.UtcNow.Ticks;
            public DisconnectInfo DisconnectInfo;
            public int TerminalQueued;
        }

        // ---------------- host ----------------

        public void StartHost(int port)
        {
            Stop();
            _incomingOverflowWarned = false;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsListening = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CoopAccept" };
            TrackThread(_acceptThread);
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            var listener = _listener;
            while (_running)
            {
                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                }
                catch (Exception e)
                {
                    // Stopping the listener is the normal way out of AcceptTcpClient. A
                    // different exception while this listener is still current is not.
                    if (_running && ReferenceEquals(listener, _listener))
                        CoopPlugin.Log.LogWarning("CoopAccept: " + e.Message);
                    break;
                }
                if (!_running || !ReferenceEquals(listener, _listener))
                {
                    try
                    {
                        tcp.Close();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    break;
                }
                ConfigureSocket(tcp);
                Conn conn;
                lock (_connsLock)
                {
                    if (!_running)
                    {
                        try
                        {
                            tcp.Close();
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                        break;
                    }
                    conn = new Conn(_nextConnId++);
                    conn.Tcp = tcp;
                    conn.Stream = tcp.GetStream();
                    _conns[conn.Id] = conn;
                }
                conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead" + conn.Id };
                TrackThread(conn.ReadThread);
                conn.ReadThread.Start();
                conn.WriteThread = new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "CoopWrite" + conn.Id };
                TrackThread(conn.WriteThread);
                conn.WriteThread.Start();
                StartKeepalive(conn);
                Connects.Enqueue(new ConnectionEvent(conn));
            }
        }

        // ---------------- client ----------------

        /// <summary>Connect to a host. Returns the connId (always 1 for a client) or throws.</summary>
        public int StartClient(string ip, int port, int timeoutMs = 6000)
        {
            Stop();
            _incomingOverflowWarned = false;
            _running = true;
            int generation = Volatile.Read(ref _lifecycleGeneration);
            var tcp = new TcpClient();
            if (!IsCurrentClientAttempt(generation))
            {
                tcp.Close();
                throw new OperationCanceledException("TCP client connection was cancelled");
            }
            var ar = tcp.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                tcp.Close();
                throw new TimeoutException($"No answer from {ip}:{port} after {timeoutMs / 1000}s");
            }
            tcp.EndConnect(ar);
            if (!IsCurrentClientAttempt(generation))
            {
                tcp.Close();
                throw new OperationCanceledException("TCP client connection was cancelled");
            }
            ConfigureSocket(tcp);
            var conn = new Conn(1) { Tcp = tcp, Stream = tcp.GetStream() };
            lock (_connsLock)
            {
                if (!IsCurrentClientAttempt(generation))
                {
                    tcp.Close();
                    throw new OperationCanceledException("TCP client connection was cancelled");
                }
                _conns[1] = conn;
            }
            if (!IsCurrentClientAttempt(generation))
            {
                CloseSocket(conn);
                throw new OperationCanceledException("TCP client connection was cancelled");
            }
            conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead1" };
            TrackThread(conn.ReadThread);
            conn.ReadThread.Start();
            conn.WriteThread = new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "CoopWrite1" };
            TrackThread(conn.WriteThread);
            conn.WriteThread.Start();
            StartKeepalive(conn);
            if (!IsCurrentClientAttempt(generation))
            {
                DropConn(conn, new DisconnectInfo("client connection cancelled", false, "cancelled", true));
                throw new OperationCanceledException("TCP client connection was cancelled");
            }
            Connects.Enqueue(new ConnectionEvent(conn));
            return conn.Id;
        }

        private bool IsCurrentClientAttempt(int generation)
        {
            return _running && generation == Volatile.Read(ref _lifecycleGeneration);
        }

        // ---------------- shared ----------------

        /// <summary>Belt-and-suspenders against a wedged peer: the writer thread absorbs
        /// short stalls, the 5s send timeout turns a long one into a disconnect instead
        /// of an ever-growing queue, and the large send buffer rides out scene loads.</summary>
        private static void ConfigureSocket(TcpClient tcp)
        {
            tcp.NoDelay = true;
            tcp.SendTimeout = 5000;
            tcp.SendBufferSize = 256 * 1024;
        }

        private void StartKeepalive(Conn conn)
        {
            var thread = new Thread(() =>
            {
                while (_running && conn.Alive && !conn.IsDisconnectingOrDisconnected)
                {
                    // Stop() signals this separately from the writer signal so teardown
                    // does not have to wait for the two-second keepalive interval.
                    conn.KeepaliveSignal.WaitOne(2000);
                    if (!_running || !conn.Alive || conn.State == ConnectionState.Disconnecting
                        || conn.State == ConnectionState.Disconnected)
                        break;
                    var message = KeepaliveMessage;
                    if (message == null)
                        continue;
                    if (conn.State == ConnectionState.Disconnecting
                        || conn.State == ConnectionState.Disconnected)
                        break;
                    EnqueueNormal(conn, NetMessageCodec.Encode(message));
                }
            })
            {
                IsBackground = true,
                Name = "CoopKeepalive" + conn.Id
            };
            conn.KeepaliveThread = thread;
            TrackThread(thread);
            thread.Start();
        }

        private void TrackThread(Thread thread)
        {
            lock (_threadsLock)
                _threads.Add(thread);
        }

        /// <summary>Drains the connection's send queue; the only thread that writes to
        /// the stream. Wakes on SendSignal, with a timeout so it notices dead connections.</summary>
        private void WriteLoop(Conn conn)
        {
            try
            {
                while (conn.Alive || !conn.ControlQueue.IsEmpty)
                {
                    byte[] frame;
                    bool controlFrame = false;
                    // One writer owns the stream. Control is a priority lane, not a
                    // second writer (concurrent NetworkStream.Write calls can interleave).
                    lock (conn.QueueLock)
                    {
                        if (conn.ControlQueue.TryDequeue(out frame))
                        {
                            // control is the only permitted lane after terminal claim
                            controlFrame = true;
                        }
                        else if (conn.Alive && !conn.TerminalClaimed
                            && conn.SendQueue.TryDequeue(out frame))
                        {
                            conn.SendFrames--;
                            conn.SendBytes -= frame.Length;
                        }
                        else
                            frame = null;
                    }
                    if (frame == null)
                    {
                        conn.SendSignal.WaitOne(500);
                        continue;
                    }
                    // Never hold QueueLock across NetworkStream.Write: a blocked peer must
                    // not block GracefulDisconnect/Stop or a Unity caller.  Terminal claim
                    // and queue selection are protected above; a normal frame selected just
                    // before the claim is discarded by this check.
                    if (!controlFrame && conn.IsDisconnectingOrDisconnected)
                        continue;
                    long writeStart = Stopwatch.GetTimestamp();
                    try
                    {
                        conn.Stream.Write(frame, 0, frame.Length);
                    }
                    finally
                    {
                        RecordWriteDuration(Stopwatch.GetTimestamp() - writeStart);
                    }
                    // Terminal ownership is claimed before the control frame is queued.
                    // The frame is best-effort; it must never keep the connection alive.
                    if (conn.State == ConnectionState.Disconnected && conn.ControlQueue.IsEmpty)
                    {
                        CloseSocket(conn);
                        return;
                    }
                    if (!_running && conn.ControlQueue.IsEmpty)
                    {
                        // CompleteDisconnect already removed the identity and published the
                        // terminal event.  The writer only owns the final socket/thread stop.
                        CloseSocket(conn);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                if (_running && conn.Alive)
                    CoopPlugin.Log.LogWarning("CoopWrite" + conn.Id + ": " + e.Message);
                else
                    CoopPlugin.Log.LogInfo("CoopWrite" + conn.Id + ": closed during shutdown");
            }
            // A write failure is a terminal transport event, not just a dead writer.
            // Route it through the same guarded path as every other disconnect.
            DropConn(conn, new DisconnectInfo("socket write failed", false, "write_failed", true));
            return;
        }

        public WriteStats DrainWriteStats()
        {
            return new WriteStats
            {
                Count = Interlocked.Exchange(ref _writeCount, 0L),
                TotalTicks = Interlocked.Exchange(ref _writeTotalTicks, 0L),
                MaxTicks = Interlocked.Exchange(ref _writeMaxTicks, 0L)
            };
        }

        private void RecordWriteDuration(long writeTicks)
        {
            Interlocked.Increment(ref _writeCount);
            Interlocked.Add(ref _writeTotalTicks, writeTicks);
            long previous;
            do
            {
                previous = Interlocked.Read(ref _writeMaxTicks);
                if (previous >= writeTicks)
                    return;
            } while (Interlocked.CompareExchange(ref _writeMaxTicks, writeTicks, previous) != previous);
        }

        private void ReadLoop(Conn conn)
        {
            try
            {
                while (_running && conn.Alive)
                {
                    // Reassemble the entire protocol frame before decoding it. The
                    // stream supplies bytes; Msg owns protocol framing.
                    var frame = Msg.ReadFrame(conn.Stream, MaxFrame);
                    conn.LastRecvTicksUtc = DateTime.UtcNow.Ticks;
                    if (!Msg.TryDecodeFrame(frame, 0, frame.Length, conn, MaxFrame, out var message))
                    {
                        continue;
                    }
                    if (message.Message is DisconnectMessage remoteDisconnect)
                    {
                        // The peer has already sent its terminal frame.  Do not route this
                        // through GracefulDisconnect: RecordRemoteDisconnect moves the
                        // connection to Disconnecting, so the graceful path can return
                        // without completing teardown (and can race DropConn).
                        var info = new DisconnectInfo(remoteDisconnect.Reason, true,
                            remoteDisconnect.Code, remoteDisconnect.Retryable,
                            (ConnectionState)remoteDisconnect.Phase);
                        if (conn.RecordRemoteDisconnect(info))
                            CompleteDisconnect(conn);
                    }
                    bool terminal = IsTerminalFrame(message.Type);
                    int normalLimit = IncomingCap - ReservedTerminalFrames;
                    if (!terminal)
                        while (Incoming.Count >= normalLimit && Incoming.TryDequeue(out _))
                        {
                            if (!_incomingOverflowWarned)
                            {
                                _incomingOverflowWarned = true;
                                CoopPlugin.Log.LogWarning("Transport: incoming queue cap reached; dropping oldest messages");
                            }
                            break;
                        }
                    if (terminal && Interlocked.Exchange(ref conn.TerminalQueued, 1) != 0)
                        continue;
                    Incoming.Enqueue(message);
                }
            }
            catch (Exception e)
            {
                if (IsMalformedFrame(e))
                    CoopPlugin.Log.LogWarning("CoopRead" + conn.Id + ": " + e.Message);
                else if (!_running || !conn.Alive || IsNormalReadClose(e))
                    CoopPlugin.Log.LogInfo("CoopRead" + conn.Id + ": connection closed");
                else
                    CoopPlugin.Log.LogWarning("CoopRead" + conn.Id + ": " + e.Message);
            }
            // A graceful close is owned by the writer.  The reader may finish first
            // (Stop sets _running=false), but must not tear down the stream and erase
            // the queued DisconnectMessage before the writer has attempted it.
            if (!conn.IsDisconnectingOrDisconnected)
                DropConn(conn);
        }

        private static bool IsNormalReadClose(Exception e)
        {
            return e is ObjectDisposedException
                || (e is IOException && string.Equals(e.Message, "Connection closed", StringComparison.Ordinal));
        }

        private static bool IsMalformedFrame(Exception e)
        {
            return e is IOException && e.Message.StartsWith("Bad frame length ", StringComparison.Ordinal);
        }

        /// <summary>Never blocks the caller: enqueues for the connection's writer thread.
        /// A write failure surfaces there as a disconnect, not here.</summary>
        private void SendFrame(Connection connection, byte[] frame)
        {
            if (connection == null)
                return;
            Conn conn;
            lock (_connsLock)
            {
                if (!_conns.TryGetValue(connection.Id, out conn) || !ReferenceEquals(conn, connection))
                    return;
            }
            if (!EnqueueNormal(conn, frame) && IsCriticalFrame(frame))
                DropConn(conn, new DisconnectInfo("send queue full", false, "queue_full", true));
        }

        private static bool IsTerminalFrame(MsgType type)
        {
            return type == MsgType.Disconnect || type == MsgType.Bye;
        }

        private static bool IsCriticalFrame(byte[] frame)
        {
            if (!Msg.TryGetType(frame, out var type))
                return false;
            return type == MsgType.Hello || type == MsgType.Welcome
                || type == MsgType.SaveChunk || type == MsgType.SaveDone
                || type == MsgType.BundleChunk || type == MsgType.BundleDone
                || type == MsgType.EnumSync || type == MsgType.FullyJoined
                || type == MsgType.FullyJoinedAck || type == MsgType.Disconnect;
        }

        private static bool EnqueueNormal(Conn conn, byte[] frame)
        {
            if (conn == null || frame == null)
                return false;
            lock (conn.QueueLock)
            {
                if (!conn.Alive || conn.TerminalClaimed
                    || conn.SendFrames >= OutgoingFrameCap
                    || conn.SendBytes + frame.Length > OutgoingByteCap)
                    return false;
                conn.SendQueue.Enqueue(frame);
                conn.SendFrames++;
                conn.SendBytes += frame.Length;
            }
            conn.SendSignal.Set();
            return true;
        }

        private void BroadcastFrame(byte[] frame)
        {
            List<Connection> connections;
            lock (_connsLock)
            {
                connections = new List<Connection>(_conns.Values);
            }
            foreach (Connection connection in connections)
                SendFrame(connection, frame);
        }

        public int ConnectionCount
        {
            get
            {
                lock (_connsLock)
                {
                    return _conns.Count;
                }
            }
        }

        public double SecondsSinceLastRecv(Connection connection)
        {
            Conn conn;
            lock (_connsLock)
            {
                if (connection == null || !_conns.TryGetValue(connection.Id, out conn) || !ReferenceEquals(conn, connection))
                    return double.MaxValue;
            }
            return TimeSpan.FromTicks(DateTime.UtcNow.Ticks - conn.LastRecvTicksUtc).TotalSeconds;
        }

        /// <summary>Forcibly drop one connection (timeout, version mismatch...).</summary>
        public void Kick(Connection connection, DisconnectInfo info = null)
        {
            GracefulDisconnect(connection, info);
        }

        public void GracefulDisconnect(Connection connection, DisconnectInfo info = null)
        {
            if (connection == null)
                return;
            Conn found;
            lock (_connsLock)
                if (!_conns.TryGetValue(connection.Id, out found) || !ReferenceEquals(found, connection))
                    return;
            var disconnect = info ?? new DisconnectInfo("connection closed");
            var current = found;
            if (!current.Alive)
                return;
            bool claimed = current.BeginDisconnect(disconnect);
            current.TerminalClaimed = true;
            if (!claimed)
            {
                if (current.State != ConnectionState.Disconnecting)
                    return;
                disconnect = current.DisconnectReason ?? disconnect;
            }
            CoopPlugin.Log.LogInfo("TCP: disconnecting " + current.Id + " (" + disconnect.Code + ")");
            current.DisconnectInfo = disconnect;
            lock (current.QueueLock)
            {
                while (current.SendQueue.TryDequeue(out _))
                {
                }
                current.SendFrames = 0;
                current.SendBytes = 0;
                while (current.ControlQueue.TryDequeue(out _))
                {
                }
                if (claimed && !disconnect.Remote)
                    current.ControlQueue.Enqueue(NetMessageCodec.Encode(new DisconnectMessage
                    {
                        Code = disconnect.Code,
                        Reason = disconnect.Reason,
                        Retryable = disconnect.Retryable,
                        Phase = (int)disconnect.Phase
                    }));
            }
            // Publish terminal state, remove the peer and emit the local event now. The
            // writer may still make one short control-lane attempt, but it is not part of
            // this lifecycle and no normal producer can enqueue after BeginDisconnect.
            CompleteDisconnect(current);
            current.SendSignal.Set();
        }

        private void CompleteDisconnect(Conn conn)
        {
            // Claim first.  DropConn, a write exception, and an explicit Kick can race;
            // only the winner may remove the peer and publish the lifecycle event.
            if (!conn.TryMarkDisconnected())
                return;
            lock (_connsLock)
            {
                if (_conns.TryGetValue(conn.Id, out var current) && ReferenceEquals(current, conn))
                    _conns.Remove(conn.Id);
            }
            Disconnects.Enqueue(new ConnectionEvent(conn,
                conn.DisconnectReason ?? conn.DisconnectInfo ?? new DisconnectInfo("connection closed")));
            // A remote disconnect has no control frame to drain. Close immediately so the
            // writer cannot remain alive in its wait loop after terminal ownership is set.
            lock (conn.QueueLock)
            {
                if (conn.ControlQueue.IsEmpty)
                    CloseSocket(conn);
            }
            conn.SendSignal.Set();
        }

        private static void CloseSocket(Conn conn)
        {
            conn.Alive = false;
            try
            {
                conn.Stream?.Close();
            }
            catch (Exception e) { Swallow.Log(e); }
            try
            {
                conn.Tcp?.Close();
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private void DropConn(Conn conn, DisconnectInfo info = null)
        {
            if (conn == null)
                return;
            var disconnect = info ?? new DisconnectInfo("connection closed");
            bool claimed = conn.BeginDisconnect(disconnect);
            if (claimed)
            {
                conn.TerminalClaimed = true;
                lock (_connsLock)
                {
                    if (_conns.TryGetValue(conn.Id, out var current) && ReferenceEquals(current, conn))
                        _conns.Remove(conn.Id);
                }
                // No post-teardown producer may leave stale keepalives or bulk data behind.
                while (conn.SendQueue.TryDequeue(out _))
                {
                }
                while (conn.ControlQueue.TryDequeue(out _))
                {
                }
            }
            // Resource ownership is the captured Conn, not the dictionary entry or the
            // lifecycle event claim. A writer can fail after another path has already
            // marked this object Disconnected and removed it from the map.
            conn.Alive = false;
            try
            {
                conn.SendSignal.Set();
            }
            catch (System.Exception e) { Swallow.Log(e); } // wake the writer so it can exit
            try
            {
                conn.KeepaliveSignal.Set();
            }
            catch (System.Exception e) { Swallow.Log(e); } // wake keepalive during teardown
            try
            {
                conn.Stream?.Close();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                conn.Tcp?.Close();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // The atomically claimed reason is authoritative. A later read/write failure
            // must never replace the first Kick or remote disconnect detail.
            if (claimed)
                CompleteDisconnect(conn);
        }

        public void Stop()
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            _running = false;
            IsListening = false;
            try
            {
                _listener?.Stop();
            }
            catch (System.Exception e) { Swallow.Log(e); }
            _listener = null;
            List<int> ids;
            lock (_connsLock)
            {
                ids = new List<int>(_conns.Keys);
            }
            // Claim every connection through the same terminal path used by Kick. Control
            // delivery is opportunistic and must not hold Stop open.
            List<Conn> stopping = new List<Conn>();
            foreach (int id in ids)
            {
                Conn conn;
                lock (_connsLock)
                    _conns.TryGetValue(id, out conn);
                if (conn != null)
                {
                    stopping.Add(conn);
                    GracefulDisconnect(conn, new DisconnectInfo("transport stopped", false, "shutdown", true));
                }
            }

            foreach (var conn in stopping)
            {
                CloseSocket(conn);
                conn.SendSignal.Set();
                conn.KeepaliveSignal.Set();
            }

            // Stop all connections before joining: closing the stream unblocks readers,
            // and SendSignal wakes writers. Join the acceptor first because it owns the
            // connection thread creation path.
            JoinThread(_acceptThread);
            List<Thread> threads;
            lock (_threadsLock)
                threads = new List<Thread>(_threads);
            foreach (var thread in threads)
                if (thread != _acceptThread)
                    JoinThread(thread);
            lock (_threadsLock)
                _threads.RemoveAll(t => !t.IsAlive);
            _acceptThread = null;
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null || thread == Thread.CurrentThread || !thread.IsAlive)
                return;
            try
            {
                if (!thread.Join(1500))
                    CoopPlugin.Log.LogWarning(thread.Name + ": did not stop within 1500ms");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning(thread.Name + ": join failed: " + e.Message);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
