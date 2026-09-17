using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Steamworks;
using UnityEngine;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Steam P2P transport: friends-list invites, no IPs, no port forwarding. Rides the
    /// game's own Steamworks.NET (initialized and pumped by its Heathen integration).
    /// Uses classic ISteamNetworking P2P with relay fallback (the protocol codec owns the
    /// 4-byte length prefix). Two outgoing lanes:
    /// transients (UnreliableNoDelay, newest-wins, dropped on refusal) drain before the
    /// stall-retried reliable lane, so a clogged bulk transfer can never delay position
    /// updates. All Steam calls happen in PumpMainThread; Send()/SendTransient() from
    /// worker threads only enqueue.
    /// </summary>
    public class SteamTransport : ICoopTransport
    {
        private const int Channel = 71; // stay clear of channel 0 (other mods)
        // Per-pump receive/decode cap. Steam reports every queued packet at once, and each
        // decode is a JSON deserialize; an uncapped burst could monopolize a frame (observed
        // net-pump spikes up to 33 ms). See the receive loop in PumpMainThread.
        private const int MaxInboundFramesPerPump = 64;
        private const int MaxControlFramesPerPump = 32;
        private const int MaxTransientBacklog = 2048;
        private const int MaxReliableFrames = 4096;
        private const long MaxReliableBytes = 16L * 1024 * 1024;
        private const long MaxTransientBytes = 2L * 1024 * 1024;
        private const int MaxInboundDecodedFrames = 4096;
        private const int ReservedInboundControlFrames = 256;
        private const int ReservedInboundTerminalFrames = 32;
        private const int MaxInboundControlFrames = ReservedInboundControlFrames - ReservedInboundTerminalFrames;
        private const int MaxInboundFramesPerPeer = 512;
        private const int MaxReliableFramesPerPeer = 1024;
        // Ordinary reliable traffic retains a bounded 2 MiB lane.  Atomic chunked
        // transfers have a separate bounded allowance: the market snapshot is about
        // 2.3 MiB on the wire, and chunk headers add a little more.
        private const long MaxReliableBytesPerPeer = 2L * 1024 * 1024;
        // Transfer messages are normally below ChunkPayloadBytes after JSON/base64 encoding,
        // so they must not be identified only by the oversized-frame path.  Keep their lane
        // bounded, but large enough for the largest permitted reassembled transfer plus chunk
        // envelope/JSON expansion. Producers wait for PumpMainThread to drain this allowance.
        private const long MaxAtomicTransferBytesPerPeer = 12L * 1024 * 1024;
        private const long MaxAtomicTransferBytes = 12L * 1024 * 1024;
        private const int ReservedReliableFrames = 256;
        private const long ReservedReliableBytes = 1024 * 1024;
        private const double DisconnectGraceSeconds = 2.0;

        // ---- oversized reliable frame chunking ----
        // Steam's reliable P2P lane refuses frames over roughly 1 MiB, and the old code
        // retried one for 30 frames before dropping it, stalling the whole pump for hundreds
        // of milliseconds (the 2.3 MB market snapshot did exactly this). Any RELIABLE frame
        // too large for Steam is now split into fixed-size chunk envelopes and reassembled on
        // the receiver before protocol decoding, so the application message layer is
        // untouched. Only the reliable lane is chunked: every transient message is far below
        // the threshold, and the transient coalescer keys on the message-type byte, which a
        // chunk envelope would not carry.
        private static readonly int ChunkMagic = unchecked((int)0xCC5A4D21); // negative: never a valid normal frame length
        private const int ChunkPayloadBytes = 256 * 1024;
        private const int ChunkHeaderInts = 6;               // magic, transfer, total, index, count, length
        private const int ChunkHeaderBytes = ChunkHeaderInts * 4;
        private const int MaxReassembledBytes = 8 * 1024 * 1024;
        private const int MaxChunkCount = (MaxReassembledBytes + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
        private const double ReassemblyTimeoutSeconds = 10.0;

        private sealed class Reassembly
        {
            public int TransferId;
            public int TotalLength;
            public int ChunkCount;
            public int ReceivedCount;
            // Chunks are held individually until the transfer completes, so a peer cannot
            // force a full-size allocation by declaring a large total and sending one chunk.
            public byte[][] Chunks;
            public bool[] Received;
            public double LastUpdate;
        }

        private readonly Dictionary<int, Reassembly> _reassembly = new Dictionary<int, Reassembly>();
        // Highest transfer id seen per connection. Chunks of an older or already-completed
        // transfer (a duplicate on the reliable lane) are ignored so they cannot displace a
        // live newer transfer or resurrect a finished one.
        private readonly Dictionary<int, int> _transferHigh = new Dictionary<int, int>();
        private readonly List<int> _expiredReassembly = new List<int>();
        private int _nextTransferId;
        private double _lastChunkWarn = -10.0;
        // A multi-chunk transfer must be enqueued as one uninterrupted run, or a concurrent
        // send on another lane/thread could interleave its chunks and make the receiver
        // supersede (and lose) one of the transfers. Send() may run on a worker thread.
        private readonly object _chunkEnqueueLock = new object();

        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<ConnectionEvent> Disconnects { get; } = new ConcurrentQueue<ConnectionEvent>();
        public ConcurrentQueue<ConnectionEvent> Connects { get; } = new ConcurrentQueue<ConnectionEvent>();
        private readonly Dictionary<int, Connection> _connections = new Dictionary<int, Connection>();
        private readonly object _connectionsLock = new object();
        public IReadOnlyList<Connection> Connections
        {
            get
            {
                lock (_connectionsLock)
                    return new List<Connection>(_connections.Values);
            }
        }
        private readonly Dictionary<int, DisconnectInfo> _pendingDisconnects = new Dictionary<int, DisconnectInfo>();
        private readonly Dictionary<int, double> _disconnectDeadlines = new Dictionary<int, double>();

        public INetMessage KeepaliveMessage;
        public double TimeoutSeconds => 180.0; // keepalives freeze with the main thread

        private readonly bool _isHost;
        private readonly Dictionary<int, CSteamID> _peers = new Dictionary<int, CSteamID>();
        private readonly Dictionary<CSteamID, int> _ids = new Dictionary<CSteamID, int>();
        private readonly Dictionary<int, double> _lastRecv = new Dictionary<int, double>();
        private readonly Dictionary<int, int> _reliableFramesByPeer = new Dictionary<int, int>();
        private readonly Dictionary<int, long> _reliableBytesByPeer = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _atomicBytesByPeer = new Dictionary<int, long>();
        private readonly HashSet<int> _terminalQueued = new HashSet<int>();
        private long _atomicBytes;
        private int _generation = 1;
        private struct Outgoing
        {
            public int ConnId; public byte[] Frame; public bool AtomicTransfer;
        }

        private readonly ConcurrentQueue<Outgoing> _transientOutbox = new ConcurrentQueue<Outgoing>();
        // Disconnects have their own control lane so a bulk reliable backlog cannot hide the
        // frame that tells the peer why this session is ending.
        private readonly ConcurrentQueue<Outgoing> _controlOutbox = new ConcurrentQueue<Outgoing>();
        private readonly ConcurrentQueue<Outgoing> _reliableOutbox = new ConcurrentQueue<Outgoing>();
        // Steamworks is main-thread-only in the Unity integration.  Transport callers may
        // disconnect from worker threads, so only this queue crosses that boundary.
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private int _reliableFrames;
        private long _reliableBytes;
        private int _transientFrames;
        private long _transientBytes;
        private Outgoing? _stalled; // reliable frame Steam refused; retried first
        // Consecutive refusals of _stalled; drop after N so one doomed (e.g. >1MB) frame
        // cannot wedge the whole lane.
        private int _stallRetries;
        private bool _inboundOverflowWarned;

        // transient coalescing scratch, reused every pump (main thread only)
        private readonly List<Outgoing> _transientScratch = new List<Outgoing>(32);
        private readonly Dictionary<int, int> _newestTransient = new Dictionary<int, int>(32);
        private double _lastTransientRefusedLog = -10.0;
        private int _nextConnId = 1;
        private byte[] _readBuf = new byte[600 * 1024];
        private float _keepaliveTimer;
        private bool _stopped;
        private volatile bool _stopping;
        private int _stopRequestQueued;
        private readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        private Callback<P2PSessionRequest_t> _cbSessionReq;
        private Callback<P2PSessionConnectFail_t> _cbSessionFail;

        /// <summary>Lobby whose members are allowed to open sessions with us (host side).</summary>
        public CSteamID LobbyId = CSteamID.Nil;

        public SteamTransport(bool isHost)
        {
            _isHost = isHost;
            SteamNetworking.AllowP2PPacketRelay(true);
            int callbackGeneration = _generation;
            _cbSessionReq = Callback<P2PSessionRequest_t>.Create(req => OnSessionRequest(req, callbackGeneration));
            _cbSessionFail = Callback<P2PSessionConnectFail_t>.Create(fail => OnSessionFail(fail, callbackGeneration));
        }

        private void OnSessionRequest(P2PSessionRequest_t req, int callbackGeneration)
        {
            if (_stopped || _stopping || callbackGeneration != _generation)
                return;
            bool allowed;
            if (_isHost)
            {
                allowed = LobbyId != CSteamID.Nil && IsLobbyMember(req.m_steamIDRemote);
            }
            else
            {
                lock (_connectionsLock)
                    allowed = _ids.ContainsKey(req.m_steamIDRemote); // only the host we connected to
            }
            if (!allowed)
            {
                CoopPlugin.Log.LogWarning($"steam: rejected session from {req.m_steamIDRemote}");
                return;
            }
            SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            bool known;
            lock (_connectionsLock)
                known = _ids.ContainsKey(req.m_steamIDRemote);
            if (!known)
                AddPeer(req.m_steamIDRemote);
        }

        private bool IsLobbyMember(CSteamID user)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == user)
                    return true;
            return false;
        }

        private void OnSessionFail(P2PSessionConnectFail_t fail, int callbackGeneration)
        {
            if (_stopped || _stopping || callbackGeneration != _generation)
                return;
            int cid;
            Connection failedConnection;
            lock (_connectionsLock)
            {
                if (!_ids.TryGetValue(fail.m_steamIDRemote, out cid))
                    return;
                _connections.TryGetValue(cid, out failedConnection);
            }
            if (failedConnection != null)
            {
                CoopPlugin.Log.LogWarning($"steam: session failed with {fail.m_steamIDRemote} (err {fail.m_eP2PSessionError})");
                Kick(failedConnection);
            }
        }

        private int AddPeer(CSteamID sid)
        {
            if (_stopping || _stopped)
                return 0;
            int cid;
            Connection connection;
            lock (_connectionsLock)
            {
                cid = _nextConnId++;
                _peers[cid] = sid;
                _ids[sid] = cid;
                _lastRecv[cid] = Time.realtimeSinceStartupAsDouble;
                connection = new Connection(cid);
                _connections[cid] = connection;
                _reliableFramesByPeer[cid] = 0;
                _reliableBytesByPeer[cid] = 0;
                _atomicBytesByPeer[cid] = 0;
                _terminalQueued.Remove(cid);
            }
            Connects.Enqueue(new ConnectionEvent(connection));
            CoopPlugin.Log.LogInfo($"steam: peer {sid} connected as {cid}");
            return cid;
        }

        /// <summary>Client: bind connId 1 to the lobby owner. The first Send opens the session.</summary>
        public void ConnectToHost(CSteamID host)
        {
            if (!_stopping && !_stopped)
                AddPeer(host);
        }

        private void SendFrame(int connId, byte[] frame)
        {
            if (frame == null)
                return;
            if (frame.Length <= ChunkPayloadBytes)
            {
                if (!EnqueueReliable(connId, frame) && IsCriticalFrame(frame))
                    FailAdmission(connId, frame.Length, 1);
                return;
            }
            // Build and enqueue under the lock so transfer ids are handed out in the same order
            // the chunks reach the outbox. A concurrent oversized send cannot invert them (the
            // receiver would then drop the older transfer as stale).
            lock (_chunkEnqueueLock)
            {
                var chunks = BuildChunks(frame);
                if (chunks == null)
                    return;
                if (!EnqueueReliableBatch(connId, chunks, true))
                    FailAdmission(connId, frame.Length, chunks.Count);
            }
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

        private static bool IsTransferFrame(byte[] frame)
        {
            if (frame == null || !Msg.TryGetType(frame, out var type))
                return false;
            return type == MsgType.SaveChunk || type == MsgType.SaveDone
                || type == MsgType.BundleChunk || type == MsgType.BundleDone;
        }

        /// <summary>Split an oversized reliable frame into chunk envelopes sharing one transfer
        /// id. The caller enqueues them contiguously on the reliable lane; Steam orders reliable
        /// delivery, so the receiver sees them in order (duplicates are still tolerated).</summary>
        private List<byte[]> BuildChunks(byte[] frame)
        {
            if (frame.Length > MaxReassembledBytes)
            {
                // Fail loud and fast: the receiver refuses a reassembly this large, so sending
                // it would just log a rejection per chunk. No current message approaches this.
                CoopPlugin.Log.LogError(
                    $"steam: reliable frame {frame.Length} bytes exceeds the {MaxReassembledBytes}-byte chunking limit - not sent");
                return null;
            }
            int chunkCount = (frame.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
            int transferId = System.Threading.Interlocked.Increment(ref _nextTransferId);
            var chunks = new List<byte[]>(chunkCount);
            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * ChunkPayloadBytes;
                int length = Math.Min(ChunkPayloadBytes, frame.Length - offset);
                var chunk = new byte[ChunkHeaderBytes + length];
                PutInt(chunk, 0, ChunkMagic);
                PutInt(chunk, 4, transferId);
                PutInt(chunk, 8, frame.Length);
                PutInt(chunk, 12, i);
                PutInt(chunk, 16, chunkCount);
                PutInt(chunk, 20, length);
                Buffer.BlockCopy(frame, offset, chunk, ChunkHeaderBytes, length);
                chunks.Add(chunk);
            }
            CoopPlugin.Log.LogInfo(
                $"steam: chunked reliable frame {frame.Length} bytes into {chunkCount} chunk(s) (transfer {transferId})");
            return chunks;
        }

        private static void PutInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public void Send(Connection connection, INetMessage message)
        {
            if (IsActive(connection))
                SendFrame(connection.Id, NetMessageCodec.Encode(message));
        }

        /// <summary>Main thread only: iterates _peers directly to avoid a per-call snapshot.</summary>
        private void BroadcastFrame(byte[] frame)
        {
            if (frame == null)
                return;
            if (frame.Length <= ChunkPayloadBytes)
            {
                foreach (int id in PeerIds())
                    if (!EnqueueReliable(id, frame) && IsCriticalFrame(frame))
                        FailAdmission(id, frame.Length, 1);
                return;
            }
            // Build the chunks once and share the arrays across peers; each peer reassembles
            // independently, so one transfer id is fine.
            lock (_chunkEnqueueLock)
            {
                var chunks = BuildChunks(frame);
                if (chunks == null)
                    return;
                foreach (int id in PeerIds())
                    if (!EnqueueReliableBatch(id, chunks, true))
                        FailAdmission(id, frame.Length, chunks.Count);
            }
        }

        public void Broadcast(INetMessage message)
        {
            BroadcastFrame(NetMessageCodec.Encode(message));
        }

        private void SendTransientFrame(int connId, byte[] frame)
        {
            lock (_connectionsLock)
            {
                if (!CanEnqueue(connId) || _transientFrames >= MaxTransientBacklog
                    || _transientBytes + frame.Length > MaxTransientBytes)
                    return;
                _transientOutbox.Enqueue(new Outgoing { ConnId = connId, Frame = frame });
                _transientFrames++;
                _transientBytes += frame.Length;
            }
        }

        private bool CanEnqueue(int connId)
        {
            return !_stopping && !_stopped && _peers.ContainsKey(connId)
                && _connections.TryGetValue(connId, out var c)
                && !c.IsDisconnectingOrDisconnected && !_pendingDisconnects.ContainsKey(connId);
        }
        private List<int> PeerIds()
        {
            lock (_connectionsLock)
                return new List<int>(_peers.Keys);
        }
        private bool EnqueueReliable(int connId, byte[] frame)
        {
            if (frame == null)
                return false;
            bool transfer = IsTransferFrame(frame);
            lock (_connectionsLock)
            {
                // Transfer production runs on a worker.  Waiting here is deliberate
                // backpressure: a healthy peer must not be disconnected merely because the
                // worker produced chunks faster than the Unity/Steam pump could drain them.
                // The predicate also makes admission all-or-nothing for each frame.
                while (CanEnqueue(connId)
                    && !HasReliableCapacity(connId, 1, frame.Length, false, transfer))
                {
                    if (!transfer)
                        return false;
                    Monitor.Wait(_connectionsLock, 100);
                }
                if (!CanEnqueue(connId)
                    || !HasReliableCapacity(connId, 1, frame.Length, false, transfer))
                    return false;
                _reliableOutbox.Enqueue(new Outgoing
                {
                    ConnId = connId,
                    Frame = frame,
                    AtomicTransfer = transfer
                });
                _reliableFrames++;
                _reliableBytes += frame.Length;
                _reliableFramesByPeer[connId]++;
                _reliableBytesByPeer[connId] += frame.Length;
                if (transfer)
                {
                    _atomicBytes += frame.Length;
                    _atomicBytesByPeer[connId] += frame.Length;
                }
                return true;
            }
        }

        private bool HasReliableCapacity(int connId, int frames, long bytes, bool controlReserve,
            bool atomicTransfer = false)
        {
            if (!_reliableFramesByPeer.ContainsKey(connId))
                return false;
            int frameLimit = MaxReliableFrames - (controlReserve ? 0 : ReservedReliableFrames);
            long byteLimit = MaxReliableBytes - (controlReserve ? 0 : ReservedReliableBytes);
            if (_reliableFrames + frames > frameLimit || _reliableBytes + bytes > byteLimit
                || _reliableFramesByPeer[connId] + frames > MaxReliableFramesPerPeer)
                return false;
            if (atomicTransfer)
            {
                return _atomicBytes + bytes <= MaxAtomicTransferBytes
                    && _atomicBytesByPeer[connId] + bytes <= MaxAtomicTransferBytesPerPeer;
            }
            return _reliableBytesByPeer[connId] + bytes <= MaxReliableBytesPerPeer;
        }

        private bool EnqueueReliableBatch(int connId, List<byte[]> frames, bool atomicTransfer)
        {
            if (frames == null || frames.Count == 0)
                return false;
            long bytes = 0;
            for (int i = 0; i < frames.Count; i++)
                bytes += frames[i].Length;
            lock (_connectionsLock)
            {
                while (CanEnqueue(connId)
                    && !HasReliableCapacity(connId, frames.Count, bytes, false, atomicTransfer))
                {
                    if (!atomicTransfer)
                        return false;
                    Monitor.Wait(_connectionsLock, 100);
                }
                if (!CanEnqueue(connId)
                    || !HasReliableCapacity(connId, frames.Count, bytes, false, atomicTransfer))
                    return false;
                for (int i = 0; i < frames.Count; i++)
                    _reliableOutbox.Enqueue(new Outgoing
                    {
                        ConnId = connId,
                        Frame = frames[i],
                        AtomicTransfer = atomicTransfer
                    });
                _reliableFrames += frames.Count;
                _reliableBytes += bytes;
                _reliableFramesByPeer[connId] += frames.Count;
                _reliableBytesByPeer[connId] += bytes;
                if (atomicTransfer)
                {
                    _atomicBytes += bytes;
                    _atomicBytesByPeer[connId] += bytes;
                }
                return true;
            }
        }

        private void FailAdmission(int connId, int originalBytes, int chunkCount)
        {
            CoopPlugin.Log.LogError($"steam: rejected atomic reliable transfer on conn {connId} ({originalBytes} bytes, {chunkCount} chunks); disconnecting to avoid partial transfer");
            if (TryGetConnection(connId, out var connection))
                GracefulDisconnect(connection, new DisconnectInfo("reliable transfer queue full", false, "queue_full", true));
        }

        private bool TryGetConnection(int connId, out Connection connection)
        {
            lock (_connectionsLock)
                return _connections.TryGetValue(connId, out connection);
        }
        private void AccountReliable(Outgoing entry)
        {
            lock (_connectionsLock)
            {
                _reliableFrames--;
                _reliableBytes -= entry.Frame.Length;
                if (_reliableFramesByPeer.TryGetValue(entry.ConnId, out int frames))
                    _reliableFramesByPeer[entry.ConnId] = Math.Max(0, frames - 1);
                if (_reliableBytesByPeer.TryGetValue(entry.ConnId, out long bytes))
                    _reliableBytesByPeer[entry.ConnId] = Math.Max(0, bytes - entry.Frame.Length);
                if (entry.AtomicTransfer)
                {
                    _atomicBytes = Math.Max(0, _atomicBytes - entry.Frame.Length);
                    if (_atomicBytesByPeer.TryGetValue(entry.ConnId, out long atomicBytes))
                        _atomicBytesByPeer[entry.ConnId] = Math.Max(0, atomicBytes - entry.Frame.Length);
                }
                Monitor.PulseAll(_connectionsLock);
            }
        }
        private void AccountTransient(int delta)
        {
            lock (_connectionsLock)
            {
                _transientFrames--;
                _transientBytes += delta;
            }
        }
        private bool IsPending(int connId)
        {
            lock (_connectionsLock)
                return _pendingDisconnects.ContainsKey(connId);
        }

        // Admission and terminal ownership use the same lock.  This closes the small race
        // where PumpMainThread checked a live peer and a worker claimed disconnect before the
        // Steam call was made.
        private bool SendPacket(int connId, byte[] frame, EP2PSend sendType, bool control)
        {
            lock (_connectionsLock)
            {
                if (!_peers.TryGetValue(connId, out var sid)
                    || !_connections.TryGetValue(connId, out var connection))
                    return false;
                bool pending = _pendingDisconnects.ContainsKey(connId);
                if (control ? !pending : pending || connection.IsDisconnectingOrDisconnected)
                    return false;
                return SteamNetworking.SendP2PPacket(sid, frame, (uint)frame.Length, sendType, Channel);
            }
        }

        private List<KeyValuePair<int, DisconnectInfo>> PendingDisconnects()
        {
            lock (_connectionsLock)
            {
                var result = new List<KeyValuePair<int, DisconnectInfo>>(_pendingDisconnects.Count);
                foreach (var pending in _pendingDisconnects)
                    result.Add(pending);
                return result;
            }
        }
        private bool TryGetPeer(int connId, out CSteamID sid)
        {
            lock (_connectionsLock)
                return _peers.TryGetValue(connId, out sid);
        }

        public void SendTransient(Connection connection, INetMessage message)
        {
            if (IsActive(connection))
                SendTransientFrame(connection.Id, NetMessageCodec.Encode(message));
        }

        private bool IsActive(Connection connection)
        {
            if (connection == null)
                return false;
            lock (_connectionsLock)
                return _connections.TryGetValue(connection.Id, out var current)
                    && ReferenceEquals(current, connection)
                    && connection.State != ConnectionState.Disconnecting
                    && connection.State != ConnectionState.Disconnected;
        }

        /// <summary>Main thread only: iterates _peers directly to avoid a per-call snapshot.</summary>
        private void BroadcastTransientFrame(byte[] frame)
        {
            if (_stopping || _stopped)
                return;
            foreach (int id in PeerIds())
                SendTransientFrame(id, frame);
        }

        public void BroadcastTransient(INetMessage message)
        {
            BroadcastTransientFrame(NetMessageCodec.Encode(message));
        }

        public void PumpMainThread()
        {
            if (_stopped)
                return;

            while (_mainThreadActions.TryDequeue(out var action))
                action();

            // ---- bounded control lane: always attempt disconnect frames before any other
            // traffic. A control backlog is bounded per pump so it cannot monopolize the frame.
            int controlBudget = MaxControlFramesPerPump;
            while (controlBudget-- > 0 && _controlOutbox.TryDequeue(out var control))
            {
                if (SendPacket(control.ConnId, control.Frame, EP2PSend.k_EP2PSendReliable, true))
                {
                }
                else
                {
                    lock (_connectionsLock)
                        if (_disconnectDeadlines.TryGetValue(control.ConnId, out var deadline)
                            && Time.realtimeSinceStartupAsDouble < deadline)
                            _controlOutbox.Enqueue(control);
                }
            }

            // ---- transient lane: drained fully every frame, after control traffic,
            // so a clogged bulk transfer can never delay position updates. States replace
            // themselves (PlayerState/NpcState), so when several frames for the same
            // (conn, MsgType) are queued only the newest is worth sending. A refused send
            // is dropped - the next state tick supersedes it - but logged (throttled) so
            // oversized packets are visible instead of a silent crowd freeze.
            _transientScratch.Clear();
            _newestTransient.Clear();
            while (_transientOutbox.TryDequeue(out var tr))
            {
                AccountTransient(-tr.Frame.Length);
                // Once a close is claimed, position/state updates are useless and can otherwise
                // consume the whole pump while the disconnect deadline is waiting.
                if (IsPending(tr.ConnId))
                    continue;
                if (!Msg.TryGetType(tr.Frame, out MsgType transientType))
                    continue; // malformed outbound data must not affect lane scheduling
                byte msgType = (byte)transientType;
                // CHUNKED transients carry a DIFFERENT slice of data per frame, so
                // newest-wins coalescing (right for a single replaceable state like
                // PlayerState) would drop every chunk but the last. NpcState splits a
                // big crowd across several packets - collapsing them capped the guest
                // at one chunk (~20 of 55 customers, trade-waiters among the lost);
                // RelayState multiplexes several senders and must survive whole too.
                if (msgType == (byte)MsgType.NpcState || msgType == (byte)MsgType.RelayState)
                {
                    _transientScratch.Add(tr);
                    continue;
                }
                int key = (tr.ConnId << 8) | msgType;
                if (_newestTransient.TryGetValue(key, out int prev))
                    _transientScratch[prev] = new Outgoing(); // superseded; null Frame skips it below
                _newestTransient[key] = _transientScratch.Count;
                _transientScratch.Add(tr);
            }
            for (int i = 0; i < _transientScratch.Count; i++)
            {
                var t = _transientScratch[i];
                if (t.Frame == null)
                    continue;
                if (!SendPacket(t.ConnId, t.Frame, EP2PSend.k_EP2PSendUnreliableNoDelay, false))
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (now - _lastTransientRefusedLog >= 10.0)
                    {
                        _lastTransientRefusedLog = now;
                        CoopPlugin.Log.LogWarning($"steam: transient packet refused (size {t.Frame.Length})");
                    }
                }
            }
            _transientScratch.Clear();

            // Expiry is checked only after the control lane has had its attempt. This is also
            // important when the reliable lane is stalled: a disconnect is never hidden behind
            // a bulk frame or removed before its dedicated control packet is considered.
            var pendingDisconnects = PendingDisconnects();
            if (pendingDisconnects.Count > 0)
            {
                double nowDisconnect = Time.realtimeSinceStartupAsDouble;
                foreach (var pending in pendingDisconnects)
                    if (IsDisconnectExpired(pending.Key, nowDisconnect))
                        CompleteDisconnect(pending.Key, pending.Value);
            }

            // ---- reliable lane (byte-budgeted per frame; a refused frame stalls until
            // Steam accepts it). Plain Reliable (NOT WithBuffering, which adds ~200ms).
            int budget = 1024 * 1024;
            while (budget > 0)
            {
                pendingDisconnects = PendingDisconnects();
                if (pendingDisconnects.Count > 0)
                {
                    double deadlineNow = Time.realtimeSinceStartupAsDouble;
                    foreach (var pending in pendingDisconnects)
                        if (IsDisconnectExpired(pending.Key, deadlineNow))
                            CompleteDisconnect(pending.Key, pending.Value);
                }
                Outgoing entry;
                bool retryingStalled = _stalled.HasValue;
                if (_stalled.HasValue)
                {
                    entry = _stalled.Value;
                    _stalled = null;
                }
                else if (!_reliableOutbox.TryDequeue(out entry))
                    break;

                if (!retryingStalled)
                    AccountReliable(entry);
                if (IsPending(entry.ConnId))
                {
                    _stallRetries = 0;
                    continue;
                } // peer gone
                if (!SendPacket(entry.ConnId, entry.Frame, EP2PSend.k_EP2PSendReliable, false))
                {
                    // Steam refused it. Transient backpressure clears in a frame or two, but a
                    // frame Steam will NEVER accept (e.g. one that exceeds its ~1MB reliable
                    // ceiling) would retry forever and strand every prices/licenses/shelf/state
                    // packet behind it. Drop it after ~30 frames; change-gated state reheals.
                    if (++_stallRetries > 30)
                    {
                        CoopPlugin.Log.LogWarning($"steam: dropping stuck reliable frame (size {entry.Frame.Length}); lane reconverges on next heal");
                        _stalled = null;
                        _stallRetries = 0;
                        continue;
                    }
                    _stalled = entry; // retry next frame
                    break;
                }
                _stallRetries = 0; // success: clear the refusal streak
                budget -= entry.Frame.Length;
            }

            // ---- keepalive ----
            _keepaliveTimer += Time.unscaledDeltaTime;
            if (!_stopping && _keepaliveTimer >= 2f && KeepaliveMessage != null && ConnectionCount > 0)
            {
                _keepaliveTimer = 0f;
                var frame = NetMessageCodec.Encode(KeepaliveMessage);
                foreach (int id in PeerIds())
                    SendPacket(id, frame, EP2PSend.k_EP2PSendReliable, false);
            }

            // ---- receives ----
            if (_stopping)
            {
                ExpireReassembly();
                return;
            }
            // Bound the receive/decode work per pump. Steam hands us every queued packet at
            // once, and each decode is a JSON deserialize, so without a cap a burst lands in
            // one frame and hitches gameplay (observed net-pump spikes up to 33 ms). Packets
            // past the budget stay queued in Steam - ReadP2PPacket is what dequeues, while
            // IsP2PPacketAvailable only reports availability - so the next pump resumes where
            // this one stopped and nothing is lost by deferral. 64 is far above the normal
            // ~15 PlayerState + ~8 NpcState chunks per second, well under the 256-unit
            // dispatch budget, and the 180 s peer timeout dwarfs any deferral this can cause.
            int inboundBudget = MaxInboundFramesPerPump;
            while (inboundBudget > 0 && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                // Never trust a peer-controlled size for an allocation.  A packet larger
                // than the protocol hard cap is malformed; read it into the bounded scratch
                // buffer when Steam permits that, then terminate the offending peer.
                if (size > Msg.MaxFrameSize)
                {
                    CoopPlugin.Log.LogWarning("steam: discarding oversized raw packet (" + size + " bytes)");
                    if (!SteamNetworking.ReadP2PPacket(_readBuf, (uint)_readBuf.Length,
                        out _, out CSteamID oversizedRemote, Channel))
                        break;
                    Connection oversizedConnection = null;
                    lock (_connectionsLock)
                        if (_ids.TryGetValue(oversizedRemote, out int oversizedId))
                            _connections.TryGetValue(oversizedId, out oversizedConnection);
                    if (oversizedConnection != null)
                        GracefulDisconnect(oversizedConnection,
                            new DisconnectInfo("oversized packet", true, "frame_too_large", false));
                    inboundBudget--;
                    continue;
                }
                if (size > _readBuf.Length)
                    _readBuf = new byte[(int)size];
                if (!SteamNetworking.ReadP2PPacket(_readBuf, (uint)_readBuf.Length, out uint msgSize, out CSteamID remote, Channel))
                    break;
                // Count every successful read, malformed and unknown frames included, so
                // invalid traffic cannot slip past the cap.
                inboundBudget--;
                if (msgSize < Msg.MinimumFrameSize)
                    continue;

                int cid;
                lock (_connectionsLock)
                    _ids.TryGetValue(remote, out cid);
                if (cid == 0)
                {
                    // packet can beat the session callback on the host side
                    if (_isHost && LobbyId != CSteamID.Nil && IsLobbyMember(remote))
                        cid = AddPeer(remote);
                    else
                        continue;
                }
                lock (_connectionsLock)
                    if (_lastRecv.ContainsKey(cid))
                        _lastRecv[cid] = Time.realtimeSinceStartupAsDouble;

                // A chunk envelope is recognised by its negative magic; a normal frame's
                // leading length is always positive. Reassemble before protocol decoding.
                if (msgSize >= ChunkHeaderBytes && BitConverter.ToInt32(_readBuf, 0) == ChunkMagic)
                {
                    HandleChunk(cid, (int)msgSize);
                    continue;
                }

                if (TryGetConnection(cid, out var connection)
                    && Msg.TryDecodeFrame(_readBuf, 0, (int)msgSize, connection,
                    Msg.MaxFrameSize, out var message))
                {
                    if (RecordRemoteDisconnect(connection, message))
                        CompleteDisconnect(connection.Id, connection.DisconnectReason);
                    EnqueueIncoming(message);
                }
            }
            ExpireReassembly();
        }

        /// <summary>Receive-side reassembly of a chunk envelope previously written by
        /// <see cref="BuildChunks"/>. Chunks arrive in order on the reliable lane, but
        /// duplicates and stale transfers are tolerated; a complete frame is handed to the
        /// normal protocol decoder exactly once.</summary>
        private void HandleChunk(int connId, int size)
        {
            int transferId = BitConverter.ToInt32(_readBuf, 4);
            int totalLength = BitConverter.ToInt32(_readBuf, 8);
            int chunkIndex = BitConverter.ToInt32(_readBuf, 12);
            int chunkCount = BitConverter.ToInt32(_readBuf, 16);
            int chunkLength = BitConverter.ToInt32(_readBuf, 20);

            // Every field must be consistent with the declared total: the chunk count and this
            // chunk's length both follow from totalLength, so an inconsistent envelope cannot
            // leave zero-filled gaps inside the reassembled frame.
            if (totalLength <= 0 || totalLength > MaxReassembledBytes
                || chunkCount <= 0 || chunkCount > MaxChunkCount
                || chunkIndex < 0 || chunkIndex >= chunkCount
                || chunkLength <= 0 || chunkLength > ChunkPayloadBytes
                || ChunkHeaderBytes + chunkLength != size
                || chunkCount != (totalLength + ChunkPayloadBytes - 1) / ChunkPayloadBytes
                || chunkLength != Math.Min(ChunkPayloadBytes, totalLength - chunkIndex * ChunkPayloadBytes))
            {
                WarnChunk("malformed or inconsistent chunk envelope", connId, transferId);
                return;
            }

            _reassembly.TryGetValue(connId, out var r);
            if (r == null || r.TransferId != transferId)
            {
                // Ignore a chunk belonging to an older or already-finished transfer (a
                // duplicate on the reliable lane); only a genuinely newer transfer may
                // supersede the in-progress one.
                if (_transferHigh.TryGetValue(connId, out int high) && transferId <= high)
                    return;
                if (r != null)
                    WarnChunk($"abandoning incomplete transfer {r.TransferId}", connId, transferId);
                r = new Reassembly
                {
                    TransferId = transferId,
                    TotalLength = totalLength,
                    ChunkCount = chunkCount,
                    Chunks = new byte[chunkCount][],
                    Received = new bool[chunkCount],
                };
                _reassembly[connId] = r;
                _transferHigh[connId] = transferId;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            r.LastUpdate = now;
            if (r.Received[chunkIndex])
                return; // duplicate of a chunk we already have
            var data = new byte[chunkLength];
            Buffer.BlockCopy(_readBuf, ChunkHeaderBytes, data, 0, chunkLength);
            r.Chunks[chunkIndex] = data;
            r.Received[chunkIndex] = true;
            r.ReceivedCount++;

            if (r.ReceivedCount < r.ChunkCount)
                return;
            _reassembly.Remove(connId);

            // Assemble only once every chunk is present, so an incomplete transfer never
            // allocates the full declared size.
            var full = new byte[r.TotalLength];
            for (int i = 0; i < r.Chunks.Length; i++)
                Buffer.BlockCopy(r.Chunks[i], 0, full, i * ChunkPayloadBytes, r.Chunks[i].Length);
            if (TryGetConnection(connId, out var connection)
                && Msg.TryDecodeFrame(full, 0, full.Length, connection, Msg.MaxFrameSize, out var message))
            {
                if (RecordRemoteDisconnect(connection, message))
                    CompleteDisconnect(connection.Id, connection.DisconnectReason);
                EnqueueIncoming(message);
            }
            else
                WarnChunk("reassembled frame failed to decode", connId, transferId);
        }

        private void WarnChunk(string what, int connId, int transferId)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastChunkWarn < 5.0)
                return;
            _lastChunkWarn = now;
            CoopPlugin.Log.LogWarning($"steam: {what} (conn {connId}, transfer {transferId})");
        }

        /// <summary>Drop reassemblies whose chunks stopped arriving (a lost/corrupt chunk, a
        /// peer that vanished mid-transfer). Runs once per pump.</summary>
        private void ExpireReassembly()
        {
            if (_reassembly.Count == 0)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            _expiredReassembly.Clear();
            foreach (var kv in _reassembly)
                if (now - kv.Value.LastUpdate > ReassemblyTimeoutSeconds)
                    _expiredReassembly.Add(kv.Key);
            for (int i = 0; i < _expiredReassembly.Count; i++)
            {
                int connId = _expiredReassembly[i];
                var r = _reassembly[connId];
                _reassembly.Remove(connId);
                CoopPlugin.Log.LogWarning(
                    $"steam: chunked transfer {r.TransferId} on conn {connId} timed out ({r.ReceivedCount}/{r.ChunkCount} chunks, {r.TotalLength} bytes expected)");
            }
        }

        public int ConnectionCount
        {
            get
            {
                lock (_connectionsLock)
                    return _peers.Count;
            }
        }

        private static bool IsInboundControl(MsgType type)
        {
            return type == MsgType.Disconnect || type == MsgType.Hello || type == MsgType.Welcome
                || type == MsgType.FullyJoined || type == MsgType.FullyJoinedAck
                || type == MsgType.SaveDone || type == MsgType.BundleDone;
        }

        private static bool IsTerminalFrame(MsgType type)
        {
            return type == MsgType.Disconnect || type == MsgType.Bye;
        }

        private static bool IsInboundDroppableTransient(MsgType type)
        {
            return type == MsgType.PlayerState || type == MsgType.NpcState
                || type == MsgType.RelayState || type == MsgType.MovePreview
                || type == MsgType.BoxMotionState;
        }

        private static bool RecordRemoteDisconnect(Connection connection, InMsg message)
        {
            if (message.Message is DisconnectMessage disconnect)
            {
                ConnectionState phase = Enum.IsDefined(typeof(ConnectionState), disconnect.Phase)
                    ? (ConnectionState)disconnect.Phase
                    : ConnectionState.Disconnecting;
                return connection.RecordRemoteDisconnect(new DisconnectInfo(disconnect.Reason, true,
                    disconnect.Code, disconnect.Retryable, phase));
            }
            return false;
        }

        private void EnqueueIncoming(InMsg message)
        {
            int count = Incoming.Count;
            bool control = IsInboundControl(message.Type);
            bool terminal = IsTerminalFrame(message.Type);
            if (terminal && !_terminalQueued.Add(message.Connection.Id))
                return;
            // Keep the terminal reserve unavailable to ordinary controls.  Counting the
            // bounded queue here avoids per-message bookkeeping that could drift when the
            // main thread drains it, while still enforcing a hard peer quota.
            var queued = Incoming.ToArray();
            int peerCount = 0;
            int controlCount = 0;
            for (int i = 0; i < queued.Length; i++)
            {
                if (queued[i].Connection.Id == message.Connection.Id)
                    peerCount++;
                if (IsInboundControl(queued[i].Type))
                    controlCount++;
            }
            if (!terminal && (peerCount >= MaxInboundFramesPerPeer
                || (control && controlCount >= MaxInboundControlFrames)))
            {
                if (!control && TryGetConnection(message.Connection.Id, out var overLimit))
                    GracefulDisconnect(overLimit,
                        new DisconnectInfo("decoded inbound peer queue full", false, "queue_full", true));
                return;
            }
            int limit = control ? MaxInboundDecodedFrames - ReservedInboundTerminalFrames
                : MaxInboundDecodedFrames - ReservedInboundControlFrames;
            // Terminal reasons have a reserved lane.  The transport has already
            // atomically claimed the peer for Disconnect, and Bye is handled once;
            // admitting this one frame is bounded and cannot be displaced by state spam.
            if (count >= limit && !terminal)
            {
                if (!_inboundOverflowWarned)
                {
                    _inboundOverflowWarned = true;
                    CoopPlugin.Log.LogWarning($"steam: decoded inbound queue cap reached ({MaxInboundDecodedFrames}); dropping {(control ? "control" : "non-control")} frame");
                }
                // A decoded transient is replaceable and will be reasserted by the
                // normal state sweep.  Reliable transfer admission is atomic at the
                // outbound boundary; never silently fabricate a partial inbound
                // transfer.  For a non-transient reliable message, tear down rather
                // than acknowledging a frame that the application never sees.
                if (!control && !IsInboundDroppableTransient(message.Type)
                    && TryGetConnection(message.Connection.Id, out var connection))
                    GracefulDisconnect(connection,
                        new DisconnectInfo("decoded inbound queue full", false, "queue_full", true));
                return;
            }
            Incoming.Enqueue(message);
        }

        // Leak diagnostics: point-in-time backlog snapshots (approximate; ConcurrentQueue).
        internal int ReassemblyCount => _reassembly.Count;
        internal int ReliableOutboxCount => _reliableOutbox.Count;
        internal int TransientOutboxCount => _transientOutbox.Count;

        public double SecondsSinceLastRecv(Connection connection)
        {
            if (connection == null)
                return double.MaxValue;
            int connId = connection.Id;
            lock (_connectionsLock)
            {
                if (!_connections.TryGetValue(connId, out var current) || !ReferenceEquals(current, connection))
                    return double.MaxValue;
                return _lastRecv.TryGetValue(connId, out double t)
                    ? Time.realtimeSinceStartupAsDouble - t
                    : double.MaxValue;
            }
        }

        /// <summary>Returns a snapshot that is never mutated (callers Kick mid-iteration);
        /// membership changes swap in a fresh list instead of touching the old one.</summary>
        public IReadOnlyList<Connection> ConnectionList
        {
            get
            {
                return Connections;
            }
        }

        public void Kick(Connection connection, DisconnectInfo info = null)
        {
            GracefulDisconnect(connection, info);
        }

        public void GracefulDisconnect(Connection connection, DisconnectInfo info = null)
        {
            if (connection == null)
                return;
            int connId = connection.Id;
            lock (_connectionsLock)
                if (!_connections.TryGetValue(connId, out var current) || !ReferenceEquals(current, connection)
                    || !_peers.ContainsKey(connId))
                    return;
            var disconnect = info ?? new DisconnectInfo("connection closed");
            bool claimed = connection.BeginDisconnect(disconnect);
            if (!claimed)
            {
                if (connection.State != ConnectionState.Disconnecting)
                    return;
                disconnect = connection.DisconnectReason ?? disconnect;
            }
            lock (_connectionsLock)
                Monitor.PulseAll(_connectionsLock);
            if (!claimed)
                return;

            var packet = NetMessageCodec.Encode(new DisconnectMessage
            {
                Code = disconnect.Code,
                Reason = disconnect.Reason,
                Retryable = disconnect.Retryable,
                Phase = (int)disconnect.Phase
            });
            // Do not call Steamworks here: this method is also called by queue workers.
            // Terminal ownership is already claimed, preventing any later normal admission.
            Action populate = () =>
            {
                lock (_connectionsLock)
                {
                    if (!_connections.TryGetValue(connId, out var current)
                        || !ReferenceEquals(current, connection))
                        return;
                    _pendingDisconnects[connId] = disconnect;
                    _disconnectDeadlines[connId] = Time.realtimeSinceStartupAsDouble
                        + DisconnectGraceSeconds;
                    if (!disconnect.Remote && _peers.ContainsKey(connId))
                        _controlOutbox.Enqueue(new Outgoing { ConnId = connId, Frame = packet });
                }
            };
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                populate();
            else
                _mainThreadActions.Enqueue(populate);
        }

        private void CompleteDisconnect(int connId, DisconnectInfo disconnect)
        {
            if (!TryGetPeer(connId, out var sid))
                return;
            if (!TryGetConnection(connId, out var connection))
                return;
            // The terminal claim is the event de-duplication gate.  It must happen before
            // touching Steam or removing maps so every teardown route has exactly one event.
            if (!connection.TryMarkDisconnected())
                return;
            SteamNetworking.CloseP2PSessionWithUser(sid);
            lock (_connectionsLock)
            {
                _peers.Remove(connId);
                _ids.Remove(sid);
                _lastRecv.Remove(connId);
                _reliableFramesByPeer.Remove(connId);
                _reliableBytesByPeer.Remove(connId);
                _atomicBytesByPeer.Remove(connId);
                _terminalQueued.Remove(connId);
                _pendingDisconnects.Remove(connId);
                _disconnectDeadlines.Remove(connId);
                _connections.Remove(connId);
            }
            _reassembly.Remove(connId);
            _transferHigh.Remove(connId);
            // BeginDisconnect atomically captured the first reason. Never publish a second
            // terminal event if a transport callback races the shutdown sweep.
            Disconnects.Enqueue(new ConnectionEvent(connection, connection.DisconnectReason ?? disconnect));
        }

        private bool IsDisconnectExpired(int connId, double now)
        {
            lock (_connectionsLock)
                return _disconnectDeadlines.TryGetValue(connId, out double deadline) && deadline <= now;
        }

        public void Stop()
        {
            // Steamworks is main-thread-only.  A worker may request teardown, but must not
            // remove peers or call Steam while the Unity thread is pumping the transport.
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                // Do not let a worker-owned Dispose abandon the Steam transport.  The
                // owner remains alive until the main-thread action runs; waiting briefly
                // also makes the Stop contract honest for callers that need teardown to
                // be complete before releasing their owner.
                if (Interlocked.Exchange(ref _stopRequestQueued, 1) != 0)
                    return;
                var completed = new ManualResetEventSlim(false);
                _mainThreadActions.Enqueue(() =>
                {
                    try
                    {
                        Stop();
                    }
                    finally
                    {
                        Volatile.Write(ref _stopRequestQueued, 0);
                        completed.Set();
                    }
                });
                bool finished = completed.Wait(5000);
                completed.Dispose();
                if (!finished)
                    CoopPlugin.Log.LogWarning("steam: worker Stop is waiting for the main-thread pump; transport remains owned and active");
                return;
            }
            if (_stopped || _stopping)
                return;
            // Publish this before PumpMainThread and before any Steam callback can add a peer.
            _stopping = true;
            lock (_connectionsLock)
                _generation++;
            List<Connection> connections;
            lock (_connectionsLock)
                connections = new List<Connection>(_connections.Values);
            foreach (var connection in connections)
                GracefulDisconnect(connection, new DisconnectInfo("transport stopped", false, "shutdown", true));
            // GracefulDisconnect populated the control lane synchronously on this thread.
            // Drain it before identities are removed, so shutdown actually gets one bounded
            // terminal attempt instead of enqueueing a frame and immediately deleting peers.
            int controlBudget = MaxControlFramesPerPump;
            while (controlBudget-- > 0 && _controlOutbox.TryDequeue(out var control))
                SendPacket(control.ConnId, control.Frame, EP2PSend.k_EP2PSendReliable, true);

            // Complete every peer explicitly after the control attempt.
            foreach (int peerId in PeerIds())
            {
                if (TryGetConnection(peerId, out var connection))
                {
                    if (connection.State != ConnectionState.Disconnecting
                        && connection.State != ConnectionState.Disconnected)
                        connection.BeginDisconnect(new DisconnectInfo("transport stopped", false, "shutdown", true));
                    CompleteDisconnect(peerId, connection.DisconnectReason
                        ?? new DisconnectInfo("transport stopped", false, "shutdown", true));
                }
            }
            _stopped = true;
            lock (_connectionsLock)
            {
                foreach (var kv in _peers)
                    SteamNetworking.CloseP2PSessionWithUser(kv.Value);
                _peers.Clear();
                _ids.Clear();
                _pendingDisconnects.Clear();
                _disconnectDeadlines.Clear();
                _reliableFramesByPeer.Clear();
                _reliableBytesByPeer.Clear();
                _lastRecv.Clear();
            }
            lock (_connectionsLock)
                _connections.Clear();
            _reassembly.Clear();
            _transferHigh.Clear();
            _terminalQueued.Clear();
            while (_controlOutbox.TryDequeue(out _))
            {
            }
            while (_transientOutbox.TryDequeue(out _))
            {
            }
            while (_reliableOutbox.TryDequeue(out _))
            {
            }
            _stalled = null;
            _reliableFrames = 0;
            _reliableBytes = 0;
            _atomicBytes = 0;
            _transientFrames = 0;
            _transientBytes = 0;
            if (LobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(LobbyId);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                LobbyId = CSteamID.Nil;
            }
            _cbSessionReq?.Dispose();
            _cbSessionReq = null;
            _cbSessionFail?.Dispose();
            _cbSessionFail = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// Lobby lifecycle + invites. One long-lived instance: the GameLobbyJoinRequested
    /// callback must be listening from startup so accepting a Steam invite at any moment
    /// (or launching the game via an invite: +connect_lobby) starts the join flow.
    /// </summary>
    public class SteamLobby
    {
        private CallResult<LobbyCreated_t> _createLobby;
        private Callback<LobbyEnter_t> _cbEnter;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private CallResult<LobbyMatchList_t> _lobbyList;

        public CSteamID LobbyId = CSteamID.Nil;
        private bool _joining;
        private CSteamID _pendingLobbyId = CSteamID.Nil;
        private long _operationGeneration;
        private long _pendingOperationGeneration;
        private long _listOperationGeneration;
        private bool _pendingPublic;
        private string _pendingName = "";
        private bool _pendingHasPw;

        public Action<CSteamID> OnLobbyCreated;   // host: lobby is live
        public Action<CSteamID> OnEnteredLobby;   // client: joined; arg = lobby owner
        public Action<CSteamID, long, string> OnJoinFailed;
        public Action<CSteamID> OnInviteAccepted; // local player accepted someone's invite
        public Action<string> OnError;
        public Action OnListUpdated;

        public struct LobbyRow
        {
            public CSteamID Id;
            public string Name;
            public int Players;
            public int Max;
            public bool HasPw;
            public string Ver;
        }

        public readonly List<LobbyRow> Lobbies = new List<LobbyRow>();
        public bool ListRefreshing
        {
            get; private set;
        }

        public void Init()
        {
            _lobbyList = CallResult<LobbyMatchList_t>.Create((e, ioFail) =>
            {
                if (_listOperationGeneration == 0 || _listOperationGeneration != _operationGeneration)
                    return; // stale list result must not clear or publish a newer operation
                ListRefreshing = false;
                Lobbies.Clear();
                if (ioFail)
                {
                    OnError?.Invoke("Steam lobby list failed");
                    return;
                }
                for (int i = 0; i < e.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == CSteamID.Nil)
                        continue;
                    Lobbies.Add(new LobbyRow
                    {
                        Id = id,
                        Name = SteamMatchmaking.GetLobbyData(id, "name"),
                        Players = SteamMatchmaking.GetNumLobbyMembers(id),
                        Max = SteamMatchmaking.GetLobbyMemberLimit(id),
                        HasPw = SteamMatchmaking.GetLobbyData(id, "pw") == "1",
                        Ver = SteamMatchmaking.GetLobbyData(id, "coopver"),
                    });
                }
                OnListUpdated?.Invoke();
            });
            _cbEnter = Callback<LobbyEnter_t>.Create(e =>
            {
                CSteamID enteredLobby = new CSteamID(e.m_ulSteamIDLobby);
                if (!IsCurrentJoin(enteredLobby, out long operation))
                    return; // our own host-side enter

                // LobbyEnter is also delivered for rejected joins.  Do not expose a
                // rejected lobby to the bridge: that would start P2P and send Hello as
                // though the join had succeeded.  Re-check the token in the failure
                // path so an old callback cannot tear down a newer join.
                if (e.m_EChatRoomEnterResponse != 1u) // EChatRoomEnterResponseSuccess
                {
                    FailJoin(operation, enteredLobby, e.m_EChatRoomEnterResponse);
                    return;
                }
                _joining = false;
                _pendingLobbyId = CSteamID.Nil;
                _pendingOperationGeneration = 0;
                LobbyId = enteredLobby;
                var owner = SteamMatchmaking.GetLobbyOwner(LobbyId);
                OnEnteredLobby?.Invoke(owner);
            });
            _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(e =>
            {
                OnInviteAccepted?.Invoke(e.m_steamIDLobby);
            });
        }

        public bool SteamAvailable()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword, int maxPlayers)
        {
            if (maxPlayers < SteamLobbyLimits.MinPlayers || maxPlayers > SteamLobbyLimits.MaxPlayers)
                throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers,
                    "Steam lobby player count must be between 2 and 250.");
            AbortPendingOperation();
            _pendingPublic = isPublic;
            _pendingName = lobbyName ?? "";
            _pendingHasPw = hasPassword;
            long operation = ++_operationGeneration;
            _pendingOperationGeneration = operation;
            // CreateLobby is asynchronous. A process-wide Callback<LobbyCreated_t> cannot
            // identify which request produced a result, so bind this CallResult to this
            // exact SteamAPICall and also retain the immutable operation token.
            string name = _pendingName;
            bool password = _pendingHasPw;
            string ownerName = CoopCore.Instance == null
                ? CoopPlugin.PlayerName.Value
                : CoopCore.Instance.EffectivePlayerName;
            _createLobby = CallResult<LobbyCreated_t>.Create((e, ioFail) =>
            {
                if (operation != _operationGeneration || _pendingOperationGeneration != operation || _joining)
                    return; // result from an abandoned CreateLobby operation
                _createLobby = null;
                if (ioFail || e.m_eResult != EResult.k_EResultOK)
                {
                    OnError?.Invoke("Steam lobby creation failed: " + e.m_eResult);
                    return;
                }
                LobbyId = new CSteamID(e.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyData(LobbyId, "coopmod", "communitymultiplayer");
                SteamMatchmaking.SetLobbyData(LobbyId, "coopver", CoopPlugin.Version);
                SteamMatchmaking.SetLobbyData(LobbyId, "name",
                    string.IsNullOrEmpty(name) ? (ownerName + "'s shop") : name);
                SteamMatchmaking.SetLobbyData(LobbyId, "pw", password ? "1" : "0");
                OnLobbyCreated?.Invoke(LobbyId);
            });
            var call = SteamMatchmaking.CreateLobby(
                isPublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            _createLobby.Set(call);
        }

        /// <summary>Fetch public lobbies of THIS mod (server-side filtered by our key).</summary>
        public void RefreshList()
        {
            if (ListRefreshing)
                return;
            ListRefreshing = true;
            _listOperationGeneration = ++_operationGeneration;
            SteamMatchmaking.AddRequestLobbyListStringFilter("coopmod", "communitymultiplayer", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            var call = SteamMatchmaking.RequestLobbyList();
            _lobbyList.Set(call);
        }

        public long Join(CSteamID lobby)
        {
            AbortPendingOperation();
            _joining = true;
            _pendingLobbyId = lobby;
            _pendingOperationGeneration = ++_operationGeneration;
            SteamMatchmaking.JoinLobby(lobby);
            return _pendingOperationGeneration;
        }

        public void OpenInviteDialog()
        {
            if (LobbyId != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public void Leave()
        {
            AbortPendingOperation();
        }

        private bool IsCurrentJoin(CSteamID lobby, out long operation)
        {
            operation = _pendingOperationGeneration;
            return _joining && operation != 0 && operation == _operationGeneration
                && (_pendingLobbyId == CSteamID.Nil || _pendingLobbyId == lobby);
        }

        private void FailJoin(long operation, CSteamID lobby, uint response)
        {
            if (!_joining || operation == 0 || operation != _operationGeneration
                || _pendingOperationGeneration != operation || _pendingLobbyId != lobby)
                return;

            CoopPlugin.Log.LogWarning("Steam lobby join rejected (" + DescribeJoinFailure(response) + ").");
            _joining = false;
            _pendingLobbyId = CSteamID.Nil;
            _pendingOperationGeneration = 0;
            ++_operationGeneration;

            // Steam can report an enter response after creating a local lobby
            // membership.  Always leave that exact lobby, but never touch a newer
            // operation's LobbyId.
            try
            {
                SteamMatchmaking.LeaveLobby(lobby);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (LobbyId == lobby)
                LobbyId = CSteamID.Nil;
            OnJoinFailed?.Invoke(lobby, operation,
                "Unable to join Steam lobby: " + DescribeJoinFailure(response) + ".");
        }

        private static string DescribeJoinFailure(uint response)
        {
            switch (response)
            {
                case 2u:
                    return "the lobby no longer exists";
                case 3u:
                    return "you are not allowed to join this lobby";
                case 4u:
                    return "the lobby is full";
                case 6u:
                    return "you are banned from this lobby";
                case 7u:
                    return "the lobby is temporarily unavailable";
                default:
                    return "Steam rejected the join (response " + response + ")";
            }
        }

        private void AbortPendingOperation()
        {
            ++_operationGeneration;
            _createLobby?.Dispose();
            _createLobby = null;
            _pendingOperationGeneration = 0;
            _listOperationGeneration = 0;
            ListRefreshing = false;
            _joining = false;
            _pendingLobbyId = CSteamID.Nil;
            if (LobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(LobbyId);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                LobbyId = CSteamID.Nil;
            }
        }
    }

    /// <summary>
    /// The one and only <see cref="ISteamBridge"/> implementation, and the designated
    /// FAILURE ZONE. This type owns the SteamLobby and the SteamTransport it hands out,
    /// does every CSteamID&lt;-&gt;ulong conversion, and is allowed to fail to load: on a
    /// build with no com.rlabrecque.steamworks.net.dll (Game Pass, DRM-free) nothing ever
    /// reaches it, because <see cref="SteamBridge.TryCreate"/> checks
    /// <see cref="PlatformProbe.SteamworksPresent"/> first and is the only caller.
    ///
    /// Since 1.0.39 it also fails DELIBERATELY: the constructor probes the native Steam
    /// runtime and throws when there isn't one, so a Game Pass install carrying a stray but
    /// COMPLETE Steamworks dll (which loads perfectly, being pure managed code) degrades to
    /// the same null bridge as a build with no dll at all. See the constructor.
    ///
    /// NOTHING OUTSIDE THIS FILE MAY NAME THIS TYPE except SteamBridge.Create(). Referencing
    /// it from CoopCore/CoopUI/CoopPlugin would drag the Steamworks metadata straight back
    /// into an always-loaded type, which is exactly the bug the bridge exists to prevent.
    ///
    /// It also absorbs the lobby-callback wiring that used to live in CoopCore.Awake: the
    /// transport plumbing (which is Steam-typed) stays here, and only the Steam-free
    /// outcome - "lobby is live", "we're connected" - is raised to CoopCore.
    /// </summary>
    internal sealed class SteamBridgeImpl : ISteamBridge
    {
        private readonly SteamLobby _lobby = new SteamLobby();
        private SteamTransport _tx;

        /// <summary>Reused across calls: CoopUI's browser polls Lobbies every OnGUI frame
        /// (which runs 2+ times a frame), and a fresh list each time is pure garbage.</summary>
        private readonly List<LobbyRow> _rows = new List<LobbyRow>();

        /// <summary>
        /// RUNTIME VIABILITY PROBE (1.0.39). Constructing this type is NOT proof that Steam
        /// can work here. The field turned up two different Game Pass cohorts, and only one
        /// of them was already handled:
        ///
        ///  (1) STRIPPED ASSEMBLY - the stock Game Pass 0.70 build ships a cut-down
        ///      com.rlabrecque.steamworks.net where Steamworks.SteamAPI still exists (so
        ///      PlatformProbe.SteamworksPresent answers TRUE) but Callback`1 is gone. Every
        ///      field of this type and of SteamLobby is Steam-typed, so `new SteamBridgeImpl()`
        ///      dies at TYPE LOAD and SteamBridge.TryCreate already turns that into a null
        ///      bridge. Field-proven, verbatim: "Steam bridge unavailable: Could not load
        ///      type ... Callback`1". Nothing below is needed for this cohort.
        ///
        ///  (2) COMPLETE STRAY COPY - a Game Pass install where ANOTHER MOD bundled a full,
        ///      unstripped Steamworks.NET dll. Nothing faults, because the whole wrapper is
        ///      pure managed code: the bridge constructs, Init() wires its callbacks, and the
        ///      Steam UI comes alive on a build where Steam can never run. That is strictly
        ///      worse than hiding it - a dead "Host via Steam" button, a lobby browser that
        ///      P/Invokes unguarded and latches ListRefreshing forever, and "Steam isn't
        ///      running" messages that tell the player to go fix something they cannot fix.
        ///
        /// So ask the NATIVE layer, which is precisely what cohort (2) is missing.
        ///
        /// WHY SteamAPI.IsSteamRunning - VERIFIED, NOT ASSUMED. Checked against the shipped
        /// com.rlabrecque.steamworks.net.dll rather than taken on faith, because a wrapper that
        /// could answer from managed state would probe nothing at all. Its IL body is 11 bytes:
        /// `call InteropHelp.TestIfPlatformSupported; call NativeMethods.SteamAPI_IsSteamRunning;
        /// ret` - and that second method carries [DllImport("steam_api64", EntryPoint =
        /// "SteamAPI_IsSteamRunning", CallingConvention = Cdecl)]. It therefore CANNOT return
        /// without binding steam_api64.dll, which is the property we want. (SteamAPI.Init would
        /// bind it too, and is exactly the wrong call to make here: we RIDE the game's own
        /// Heathen-initialized Steamworks and must never fight it for ownership - see the
        /// SteamTransport remark at the top of this file.)
        ///
        /// ANY exception becomes a plain InvalidOperationException - DllNotFoundException (no
        /// steam_api64.dll beside the executable: the Game Pass case), EntryPointNotFoundException,
        /// a TypeLoadException out of a half-stripped InteropHelp, anything at all. The thrown
        /// type names nothing Steam-shaped, so SteamBridge.TryCreate's EXISTING catch handles it
        /// unchanged and every stray-dll machine degrades to the same clean LAN-only state the
        /// stripped-assembly machines already get, carrying the same one warning line.
        ///
        /// A NORMAL RETURN OF false IS DELIBERATELY NOT AN ERROR. That means "Steam is installed
        /// but the client isn't running" - a state the player can actually fix, and the exact
        /// distinction ISteamBridge documents and SteamAvailable() exists to report. Only a
        /// THROW proves the runtime isn't there at all. (Residual, and accepted: a Game Pass
        /// install carrying both the stray managed wrapper AND a stray steam_api64.dll, on a PC
        /// where the Steam client happens to be running, still answers true and still gets the
        /// Steam UI. No probe short of SteamAPI.Init can separate that case, and Init is the one
        /// call we are not allowed to make.)
        ///
        /// THIS MUST STAY IN THE CONSTRUCTOR AND MUST NOT MOVE INTO Init(). TryCreate's
        /// try/catch wraps only Create() - that is, `new SteamBridgeImpl()`. CoopCore calls
        /// Init() afterwards, outside any catch, so a throw from there would take CoopCore.Awake
        /// down with it and kill the whole mod instead of degrading it.
        /// </summary>
        public SteamBridgeImpl()
        {
            try
            {
                SteamAPI.IsSteamRunning();
            }
            catch (Exception e)
            {
                // include the real reason: TryCreate logs this message, and "which exception"
                // is what separates a Game Pass stray dll from a genuinely broken install.
                throw new InvalidOperationException(
                    "Steamworks runtime is not functional on this install (" +
                    e.GetType().Name + ": " + e.Message + ")");
            }
        }

        public Action<string> OnError
        {
            get; set;
        }
        public Action<ulong> OnLobbyLive
        {
            get; set;
        }
        public Action OnConnectedToHost
        {
            get; set;
        }
        public Action<ulong> OnInviteAccepted
        {
            get; set;
        }
        public Action<ulong, long, ICoopTransport, string> OnJoinFailed
        {
            get; set;
        }

        public void Init()
        {
            _lobby.Init();
            _lobby.OnError = e => OnError?.Invoke(e);
            _lobby.OnJoinFailed = (lobby, operation, error) =>
                OnJoinFailed?.Invoke(lobby.m_SteamID, operation, _tx, error);
            _lobby.OnLobbyCreated = id =>
            {
                // Host side. The transport is always created BEFORE Host() is called
                // (see CreateTransport's contract), so _tx is non-null here in every
                // real flow; the guard is only for a Leave() racing the callback.
                if (_tx != null)
                    _tx.LobbyId = id;
                // .m_SteamID, not the struct: the boundary is what keeps CoopCore's handler
                // free of Steamworks metadata (see ISteamBridge).
                OnLobbyLive?.Invoke(id.m_SteamID);
            };
            _lobby.OnEnteredLobby = owner =>
            {
                // Client side. NOTE: the Role != Client guard that used to sit here now
                // lives on CoopCore's OnConnectedToHost handler - the bridge has no idea
                // what a CoopRole is. SteamLobby._joining already filters the host's own
                // lobby-enter, so this only fires on a real join.
                if (_tx == null)
                    return;
                _tx.LobbyId = _lobby.LobbyId;
                _tx.ConnectToHost(owner);
                OnConnectedToHost?.Invoke();
            };
            _lobby.OnInviteAccepted = id => OnInviteAccepted?.Invoke(id.m_SteamID);
            _lobby.OnListUpdated = () => { };
        }

        public bool SteamAvailable()
        {
            return _lobby.SteamAvailable();
        }

        public string LocalPersonaName
        {
            get
            {
                try
                {
                    return SteamAvailable() ? (SteamFriends.GetPersonaName() ?? "") : "";
                }
                catch (System.Exception e) { Swallow.Log(e); return ""; }
            }
        }

        public ulong LocalSteamId
        {
            get
            {
                try
                {
                    if (!SteamAvailable())
                        return 0;
                    return SteamUser.GetSteamID().m_SteamID;
                }
                catch (System.Exception e) { Swallow.Log(e); return 0; }
            }
        }

        public string FriendNickname(ulong steamId)
        {
            if (steamId == 0)
                return "";
            try
            {
                if (!SteamAvailable())
                    return "";
                var friend = new CSteamID(steamId);
                if (!SteamFriends.HasFriend(friend, EFriendFlags.k_EFriendFlagImmediate))
                    return "";
                return SteamFriends.GetFriendPersonaName(friend) ?? "";
            }
            catch (System.Exception e) { Swallow.Log(e); return ""; }
        }

        public ICoopTransport CreateTransport(bool isHost, INetMessage keepalive)
        {
            _tx = new SteamTransport(isHost) { KeepaliveMessage = keepalive };
            return _tx;
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword, int maxPlayers)
        {
            _lobby.Host(isPublic, lobbyName, hasPassword, maxPlayers);
        }

        public long Join(ulong lobbyId)
        {
            return _lobby.Join(new CSteamID(lobbyId));
        }

        public void Leave()
        {
            _lobby.Leave();
            // Drop the transport reference too: CoopCore disposes it separately, and a
            // stale one here would let a late lobby callback write into a dead transport.
            _tx = null;
        }

        public void OpenInviteDialog()
        {
            _lobby.OpenInviteDialog();
        }
        public void RefreshList()
        {
            _lobby.RefreshList();
        }
        public bool ListRefreshing
        {
            get
            {
                return _lobby.ListRefreshing;
            }
        }

        public List<LobbyRow> Lobbies
        {
            get
            {
                _rows.Clear();
                foreach (var r in _lobby.Lobbies)
                    _rows.Add(new LobbyRow
                    {
                        Id = r.Id.m_SteamID,
                        Name = r.Name,
                        Players = r.Players,
                        Max = r.Max,
                        HasPw = r.HasPw,
                        Ver = r.Ver,
                    });
                return _rows;
            }
        }
    }
}
