using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

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
        private const int MaxFrame = Msg.MaxFrameSize; // save files are ~4 MB; hard cap for sanity

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<int> Disconnects { get; } = new ConcurrentQueue<int>();
        public ConcurrentQueue<int> Connects { get; } = new ConcurrentQueue<int>();

        public void PumpMainThread() { } // all socket work lives on background threads

        public double TimeoutSeconds => 60.0;

        // TCP is already low-latency and ordered; the fast lane is just the normal lane
        public void Send(int connId, INetMessage message)
        {
            SendFrame(connId, NetMessageCodec.Encode(message));
        }
        public void Broadcast(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }
        public void SendTransient(int connId, INetMessage message)
        {
            SendFrame(connId, NetMessageCodec.Encode(message));
        }
        public void BroadcastTransient(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }

        /// <summary>Frame sent by a transport-owned thread every 2s per connection.
        /// Keeps the link alive even while Unity's main thread is frozen in a scene load.</summary>
        public INetMessage KeepaliveMessage;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private readonly Dictionary<int, Conn> _conns = new Dictionary<int, Conn>();
        private readonly object _connsLock = new object();
        private int _nextConnId = 1;

        public bool IsListening { get; private set; }

        private class Conn
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Thread ReadThread;
            public Thread WriteThread;
            // Single writer thread drains this, so frames stay atomic on the wire
            // without a write lock; keepalives are just another queued frame.
            public readonly ConcurrentQueue<byte[]> SendQueue = new ConcurrentQueue<byte[]>();
            public readonly AutoResetEvent SendSignal = new AutoResetEvent(false);
            public volatile bool Alive = true;
            public long LastRecvTicksUtc = DateTime.UtcNow.Ticks;
        }

        // ---------------- host ----------------

        public void StartHost(int port)
        {
            Stop();
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsListening = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CoopAccept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped
                ConfigureSocket(tcp);
                var conn = new Conn { Tcp = tcp, Stream = tcp.GetStream() };
                lock (_connsLock)
                {
                    conn.Id = _nextConnId++;
                    _conns[conn.Id] = conn;
                }
                conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead" + conn.Id };
                conn.ReadThread.Start();
                conn.WriteThread = new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "CoopWrite" + conn.Id };
                conn.WriteThread.Start();
                StartKeepalive(conn);
                Connects.Enqueue(conn.Id);
            }
        }

        // ---------------- client ----------------

        /// <summary>Connect to a host. Returns the connId (always 1 for a client) or throws.</summary>
        public int StartClient(string ip, int port, int timeoutMs = 6000)
        {
            Stop();
            _running = true;
            var tcp = new TcpClient();
            var ar = tcp.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                tcp.Close();
                throw new TimeoutException($"No answer from {ip}:{port} after {timeoutMs / 1000}s");
            }
            tcp.EndConnect(ar);
            ConfigureSocket(tcp);
            var conn = new Conn { Id = 1, Tcp = tcp, Stream = tcp.GetStream() };
            lock (_connsLock) { _conns[1] = conn; }
            conn.ReadThread = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "CoopRead1" };
            conn.ReadThread.Start();
            conn.WriteThread = new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "CoopWrite1" };
            conn.WriteThread.Start();
            StartKeepalive(conn);
            return conn.Id;
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
            new Thread(() =>
            {
                while (_running && conn.Alive)
                {
                    Thread.Sleep(2000);
                    var message = KeepaliveMessage;
                    if (message == null || !conn.Alive) continue;
                    conn.SendQueue.Enqueue(NetMessageCodec.Encode(message));
                    conn.SendSignal.Set();
                }
            }) { IsBackground = true, Name = "CoopKeepalive" + conn.Id }.Start();
        }

        /// <summary>Drains the connection's send queue; the only thread that writes to
        /// the stream. Wakes on SendSignal, with a timeout so it notices dead connections.</summary>
        private void WriteLoop(Conn conn)
        {
            try
            {
                while (_running && conn.Alive)
                {
                    if (!conn.SendQueue.TryDequeue(out var frame))
                    {
                        conn.SendSignal.WaitOne(500);
                        continue;
                    }
                    conn.Stream.Write(frame, 0, frame.Length);
                }
            }
            catch
            {
                // fallthrough to disconnect
            }
            DropConn(conn.Id);
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
                    if (!Msg.TryDecodeFrame(frame, 0, frame.Length, conn.Id, MaxFrame, out var message))
                        throw new IOException("Bad message frame");
                    Incoming.Enqueue(message);
                }
            }
            catch
            {
                // fallthrough to disconnect
            }
            DropConn(conn.Id);
        }

        /// <summary>Never blocks the caller: enqueues for the connection's writer thread.
        /// A write failure surfaces there as a disconnect, not here.</summary>
        private void SendFrame(int connId, byte[] frame)
        {
            Conn conn;
            lock (_connsLock) { if (!_conns.TryGetValue(connId, out conn)) return; }
            if (!conn.Alive) return;
            conn.SendQueue.Enqueue(frame);
            conn.SendSignal.Set();
        }

        private void BroadcastFrame(byte[] frame)
        {
            List<int> ids;
            lock (_connsLock) { ids = new List<int>(_conns.Keys); }
            foreach (int id in ids) SendFrame(id, frame);
        }

        public int ConnectionCount
        {
            get { lock (_connsLock) { return _conns.Count; } }
        }

        public double SecondsSinceLastRecv(int connId)
        {
            Conn conn;
            lock (_connsLock) { if (!_conns.TryGetValue(connId, out conn)) return double.MaxValue; }
            return TimeSpan.FromTicks(DateTime.UtcNow.Ticks - conn.LastRecvTicksUtc).TotalSeconds;
        }

        public List<int> ConnIds()
        {
            lock (_connsLock) { return new List<int>(_conns.Keys); }
        }

        /// <summary>Forcibly drop one connection (timeout, version mismatch...).</summary>
        public void Kick(int connId)
        {
            DropConn(connId);
        }

        private void DropConn(int connId)
        {
            Conn conn;
            lock (_connsLock)
            {
                if (!_conns.TryGetValue(connId, out conn)) return;
                _conns.Remove(connId);
            }
            if (!conn.Alive) return;
            conn.Alive = false;
            try { conn.SendSignal.Set(); } catch { } // wake the writer so it can exit
            try { conn.Stream?.Close(); } catch { }
            try { conn.Tcp?.Close(); } catch { }
            Disconnects.Enqueue(connId);
        }

        public void Stop()
        {
            _running = false;
            IsListening = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            List<int> ids;
            lock (_connsLock) { ids = new List<int>(_conns.Keys); }
            foreach (int id in ids) DropConn(id);
        }

        public void Dispose() { Stop(); }
    }
}
