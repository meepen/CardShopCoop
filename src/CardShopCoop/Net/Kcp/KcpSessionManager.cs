using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Datagrams;
using CardShopCoop.Net.Messages;
using CardShopCoop.Net.Protocol;
using kcp2k;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;

namespace CardShopCoop.Net.Kcp
{
    /// <summary>
    /// Main-thread-owned KCP transport over an arbitrary datagram bearer.
    ///
    /// The datagram bearer is deliberately not a protocol implementation.  This class owns the
    /// KCP sessions, the application frame envelope, queue admission, and the PeerConnection
    /// lifecycle.  Producers may call the send methods from any thread; only an owned encoded
    /// frame is placed in a bounded queue by those calls.  Polling, KCP input/ticks, decoding,
    /// and all public lifecycle output happen in <see cref="PumpNetworkThread"/>.
    /// </summary>
    public sealed class KcpSessionManager : ICoopTransport, ICoopHandshakeTransport, ICoopStartable
    {
        // The envelope is transport-private.  It is intentionally outside Msg's DTO frame so
        // Type.FullName remains the sole application identity on the wire.
        private const byte EnvelopeMagic0 = 0x4B;
        private const byte EnvelopeMagic1 = 0x43;
        private const byte EnvelopeVersion = 1;
        private const byte EnvelopeComplete = 1;
        private const byte EnvelopeFragment = 2;
        private const byte EnvelopeUnreliable = 3;
        private const int EnvelopeSize = 20;

        private readonly IDatagramTransport _datagrams;
        private readonly bool _isHost;
        private readonly KcpConfig _config;
        private readonly KcpSessionManagerOptions _options;
        private readonly object _gate = new object();
        private readonly Dictionary<DatagramPeer, SessionState> _states
            = new Dictionary<DatagramPeer, SessionState>();
        private readonly Dictionary<int, SessionState> _connections
            = new Dictionary<int, SessionState>();
        // Peer availability is replaceable until a terminal close is queued. A linked list lets
        // us replace a pending availability in place without changing global callback order;
        // terminal signals remain FIFO records and can never evict a different peer's signal.
        private readonly LinkedList<PeerSignal> _peerSignals = new LinkedList<PeerSignal>();
        private readonly Dictionary<DatagramPeer, LinkedListNode<PeerSignal>>
            _pendingPeerAvailableSignals =
                new Dictionary<DatagramPeer, LinkedListNode<PeerSignal>>();
        // This contains terminal signals that are queued or currently being processed. It is
        // intentionally cleared after processing so peer churn cannot turn lifecycle coalescing
        // into an unbounded tombstone set.
        private readonly HashSet<DatagramPeer> _peerTerminalSignals =
            new HashSet<DatagramPeer>();
        private readonly ConcurrentQueue<DisconnectRequest> _disconnectRequests
            = new ConcurrentQueue<DisconnectRequest>();

        private readonly Stopwatch _clockStopwatch = Stopwatch.StartNew();
        private readonly object _clockGate = new object();
        private readonly Func<uint> _clock;
        private uint _lastClock;
        private bool _hasClock;
        private int _pumpThreadId;
        private int _pumpActive;
        private int _disconnectRequestCount;
        private bool _pumping;
        private bool _started;
        private bool _stopRequested;
        private bool _disposeRequested;
        private bool _disposed;
        private bool _resourcesDisposed;
        private int _nextHostConnectionId = 1;
        // When a disconnect carries a reason, hold the KCP close until the terminal frame is
        // acknowledged (or this grace elapses) so the peer receives the reason before the goodbye.
        private const int TerminalFrameAckGraceMs = 1000;
        // The peer's terminal reason frame is decoded on the async worker, which can lag the KCP
        // close. Hold a reasonless disconnect this long so the decoded frame can claim it.
        private const int DisconnectDecodeGraceMs = 250;
        private int _reassemblyBytes;
        private int _incomingBytes;
        private int _pendingActivationFrames;
        private int _pendingActivationBytes;
        private IProtocolSessionLease _sessionLease;
        private ProtocolSnapshot _messageSnapshot;

        private readonly ConcurrentQueue<IncomingItem> _incoming
            = new ConcurrentQueue<IncomingItem>();
        // Inbound frames are JSON-decoded on a dedicated worker so the Unity frame pump only ever
        // touches already-decoded C# objects. The pump enqueues an owned frame copy; the worker
        // decodes it and posts the result back. Two FIFO queues keep delivery order identical to
        // the synchronous path.
        private readonly ConcurrentQueue<DecodeWork> _decodeQueue = new ConcurrentQueue<DecodeWork>();
        private readonly ConcurrentQueue<DecodeResult> _decodeResults
            = new ConcurrentQueue<DecodeResult>();
        private SemaphoreSlim _decodeSignal;
        private Thread _decodeThread;
        private bool _decodeStop;
        private int _decodePendingFrames;
        private int _decodePendingBytes;
        private int _datagramsThisPump;
        private int _framesThisPump;
        private readonly Queue<ConnectionEvent> _pendingDisconnectResults
            = new Queue<ConnectionEvent>();
        public ConcurrentQueue<ConnectionEvent> Disconnects
        {
            get;
        }
            = new ConcurrentQueue<ConnectionEvent>();

        internal int PendingPeerSignalCount
        {
            get
            {
                lock (_gate)
                    return _peerSignals.Count;
            }
        }
        internal int PendingDisconnectRequestCount
            => Math.Max(0, Volatile.Read(ref _disconnectRequestCount));
        internal int PendingDisconnectResultCount
        {
            get
            {
                lock (_gate)
                    return _pendingDisconnectResults.Count + Disconnects.Count;
            }
        }

        public event Action<PeerConnection> PeerConnected;

        public bool TryDequeueIncoming(out InMsg message)
        {
            lock (_gate)
            {
                if (!_incoming.TryDequeue(out var item))
                {
                    message = default(InMsg);
                    return false;
                }

                _incomingBytes -= item.EncodedBytes;
                if (_incomingBytes < 0)
                {
                    LogError("KCP incoming-byte accounting underflow");
                    _incomingBytes = 0;
                }
                message = item.Message;
                return true;
            }
        }

        /// <summary>Whether this manager creates responder sessions and host-side ids.</summary>
        public bool IsHost => _isHost;

        /// <summary>Configuration used to create every KCP session.</summary>
        public KcpConfig Config => CopyConfig(_config);

        public double TimeoutSeconds => _config.Timeout / 1000.0;

        public IReadOnlyList<PeerConnection> Connections
        {
            get
            {
                lock (_gate)
                {
                    var result = new List<PeerConnection>(_connections.Count);
                    foreach (var state in _connections.Values)
                        result.Add(state.Connection);
                    return result;
                }
            }
        }

        public int ConnectionCount
        {
            get
            {
                lock (_gate)
                    return _connections.Count;
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return Volatile.Read(ref _started) && !Volatile.Read(ref _stopRequested)
                        && !Volatile.Read(ref _disposed);
            }
        }

        public KcpSessionManager(IDatagramTransport datagrams, bool isHost)
            : this(datagrams, isHost, new KcpConfig(), null)
        {
        }

        public KcpSessionManager(IDatagramTransport datagrams, bool isHost, KcpConfig config)
            : this(datagrams, isHost, config, null)
        {
        }

        public KcpSessionManager(IDatagramTransport datagrams, bool isHost,
            KcpSessionManagerOptions options)
            : this(datagrams, isHost, new KcpConfig(), options)
        {
        }

        public KcpSessionManager(IDatagramTransport datagrams, bool isHost,
            KcpConfig config, KcpSessionManagerOptions options)
        {
            _datagrams = datagrams ?? throw new ArgumentNullException(nameof(datagrams));
            _isHost = isHost;
            _config = ValidateConfig(CopyConfig(config ?? new KcpConfig()), datagrams.MaxDatagramSize);
            _options = (options ?? new KcpSessionManagerOptions()).Copy();
            _options.Validate();
            _clock = _options.Clock ?? ClockNow;

            _datagrams.PeerAvailable += OnPeerAvailable;
            _datagrams.PeerClosed += OnPeerClosed;
            CoopPlugin.Log?.LogInfo("[net] datagram transport=" + _datagrams.GetType().Name
                + " host=" + isHost);
        }

        public KcpSessionManager(bool isHost, IDatagramTransport datagrams)
            : this(datagrams, isHost)
        {
        }

        public KcpSessionManager(bool isHost, IDatagramTransport datagrams, KcpConfig config,
            KcpSessionManagerOptions options = null)
            : this(datagrams, isHost, config, options)
        {
        }

        public KcpSessionManager(bool isHost, IDatagramTransport datagrams,
            KcpSessionManagerOptions options)
            : this(datagrams, isHost, new KcpConfig(), options)
        {
        }

        /// <summary>
        /// Starts the datagram bearer.  A client bearer normally publishes its one configured peer
        /// here; that signal is queued and turned into a KCP initiator by the next pump.
        /// </summary>
        public void Start()
        {
            EnsureNotDisposed();
            lock (_gate)
            {
                if (_pumpThreadId == 0)
                    _pumpThreadId = Thread.CurrentThread.ManagedThreadId;
                else if (_pumpThreadId != Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException(
                        "KCP transport lifecycle must run on the pump thread");

                if (Volatile.Read(ref _disposed) || Volatile.Read(ref _disposeRequested))
                    throw new ObjectDisposedException(nameof(KcpSessionManager));

                if (Volatile.Read(ref _started))
                    return;

                IProtocolSessionLease lease = null;
                var bearerStartAttempted = false;
                try
                {
                    // Acquire the exclusive catalog lease before starting the bearer. This keeps
                    // a synchronous PeerAvailable callback inside Start on the same snapshot.
                    lease = MessageRegistry.BeginSession();
                    _sessionLease = lease;
                    _messageSnapshot = lease.Snapshot;
                    Volatile.Write(ref _stopRequested, false);
                    Volatile.Write(ref _started, true);
                    bearerStartAttempted = true;
                    _datagrams.Start();
                }
                catch (Exception startupError)
                {
                    Volatile.Write(ref _started, false);
                    Volatile.Write(ref _stopRequested, false);
                    if (ReferenceEquals(_sessionLease, lease))
                    {
                        _sessionLease = null;
                        _messageSnapshot = null;
                    }
                    ClearPeerSignalsLocked();
                    while (_disconnectRequests.TryDequeue(out _))
                    {
                    }
                    Volatile.Write(ref _disconnectRequestCount, 0);

                    // A bearer is allowed to have entered its running state before reporting a
                    // startup error. Stop that partial start, but preserve the original failure.
                    try
                    {
                        if (bearerStartAttempted)
                            _datagrams.Stop();
                    }
                    catch (Exception cleanupError)
                    {
                        LogError("KCP bearer startup cleanup failed: " + cleanupError);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                    ClearPeerSignalsLocked();
                    while (_disconnectRequests.TryDequeue(out _))
                    {
                    }
                    Volatile.Write(ref _disconnectRequestCount, 0);

                    throw new InvalidOperationException(
                        "KCP transport startup failed after acquiring its protocol session", startupError);
                }
            }
        }

        /// <summary>Runs all bearer, KCP, decoding, and lifecycle work on one caller thread.</summary>
        public void PumpNetworkThread()
        {
            if (Interlocked.Exchange(ref _pumpActive, 1) != 0)
                throw new InvalidOperationException("KCP transport cannot be pumped concurrently");

            var threadId = Thread.CurrentThread.ManagedThreadId;
            if (_pumpThreadId == 0)
                _pumpThreadId = threadId;
            else if (_pumpThreadId != threadId)
            {
                Volatile.Write(ref _pumpActive, 0);
                throw new InvalidOperationException("KCP transport must use one pump thread");
            }

            _pumping = true;
            var disconnectResultBudget = _options.MaxDisconnectResultsPerPump;
            try
            {
                if (Volatile.Read(ref _stopRequested))
                {
                    StopOnPump();
                    var shutdownResultBudget = int.MaxValue;
                    DrainDisconnectResults(ref shutdownResultBudget);
                    return;
                }

                if (!Volatile.Read(ref _started) || Volatile.Read(ref _disposed))
                    return;

                var now = MonotonicNow();
                var peerSignalBudget = _options.MaxPeerSignalsPerPump;
                var disconnectRequestBudget = _options.MaxDisconnectRequestsPerPump;
                using (CardShopCoop.Util.PerfProbe.ThreadSample("net.signal"))
                {
                    DrainPeerSignals(ref peerSignalBudget);
                    DrainDisconnectRequests(ref disconnectRequestBudget);
                }
                _datagramsThisPump = 0;
                _framesThisPump = 0;
                using (CardShopCoop.Util.PerfProbe.ThreadSample("net.poll"))
                {
                    _datagrams.Poll(_options.MaxDatagramsPerPump, OnDatagram);
                    CardShopCoop.Util.PerfProbe.RecordQueueDepth("net.datagrams",
                        _datagramsThisPump, _options.MaxDatagramsPerPump);
                }
                using (CardShopCoop.Util.PerfProbe.ThreadSample("net.signal"))
                {
                    DrainPeerSignals(ref peerSignalBudget);
                    DrainDisconnectRequests(ref disconnectRequestBudget);
                }

                using (CardShopCoop.Util.PerfProbe.ThreadSample("net.sessions"))
                {
                    SessionState[] snapshot;
                    lock (_gate)
                    {
                        snapshot = new SessionState[_states.Count];
                        _states.Values.CopyTo(snapshot, 0);
                    }

                    for (var i = 0; i < snapshot.Length; i++)
                    {
                        var state = snapshot[i];
                        if (state.Terminal)
                            continue;

                        if (!state.MessageIdsActivated
                            && unchecked(now - state.HandshakeStarted)
                                > _options.PendingHandshakeTimeoutMs)
                        {
                            DisconnectForProtocol(state, "application handshake timed out before message-id activation");
                            continue;
                        }

                        if (state.Reassembly != null
                            && unchecked(now - state.ReassemblyLastReceive)
                                > _options.ReassemblyTimeoutMs)
                        {
                            ClearReassembly(state);
                            DisconnectForProtocol(state, "reliable frame reassembly timed out");
                            continue;
                        }

                        state.Session.TickIncoming(now);
                        if (state.Terminal)
                            continue;

                        FlushTransient(state);
                        FlushReliable(state);
                        state.Session.TickOutgoing(now);

                        if (state.DisconnectRequested && !state.Terminal)
                            DisconnectOnPump(state);

                        if (state.PendingTerminalDisconnect && !state.Terminal)
                        {
                            var acked = state.Session.PendingReliableSegments == 0;
                            // Wrap-safe deadline test: the difference is cast to a signed int, because
                            // a uint comparison against 0 is always true and would skip the hold.
                            if (acked
                                || unchecked((int)(now - state.TerminalDisconnectDeadline)) >= 0)
                            {
                                LogInfo("coop: completing held disconnect for peer "
                                    + state.Connection.Id + " (acked=" + acked + ").");
                                FinishDisconnectOnPump(state);
                            }
                        }

                        if (state.PendingDisconnectCompletion && !state.Terminal
                            && unchecked((int)(now - state.DisconnectCompletionDeadline)) >= 0)
                        {
                            // The peer's terminal DisconnectMessage is decoded on the async worker
                            // and can land after the last drain of this pass, especially when the
                            // frame pump is slow (loading screens). Harvest it before giving up on
                            // a reason so the terminal event carries the peer's own explanation
                            // instead of the generic fallback.
                            DrainDecodeResults();
                            if (!state.Terminal)
                            {
                                LogInfo("coop: completing deferred disconnect for peer "
                                    + state.Connection.Id + " with fallback reason '"
                                    + (state.DisconnectInfo?.Reason ?? "<null>") + "'.");
                                CompleteDisconnect(state, state.DisconnectInfo);
                            }
                        }

                    }
                }

                CardShopCoop.Util.PerfProbe.RecordQueueDepth("net.frames", _framesThisPump,
                    _options.MaxIncomingFrames);
                var stopping = Volatile.Read(ref _disposeRequested);
                // Harvest whatever the decode worker finished since the last pump. Runs before the
                // disconnect drain so a decoded DisconnectMessage is visible to it this frame.
                using (CardShopCoop.Util.PerfProbe.ThreadSample("net.decode-drain"))
                {
                    DrainDecodeResults();
                }
                if (stopping)
                {
                    StopOnPump();
                    var shutdownResultBudget = int.MaxValue;
                    using (CardShopCoop.Util.PerfProbe.ThreadSample("net.disconnect-drain"))
                    {
                        DrainDisconnectResults(ref shutdownResultBudget);
                    }
                }
                else
                {
                    using (CardShopCoop.Util.PerfProbe.ThreadSample("net.disconnect-drain"))
                    {
                        DrainDisconnectResults(ref disconnectResultBudget);
                    }
                }

            }
            finally
            {
                _pumping = false;
                Volatile.Write(ref _pumpActive, 0);
            }
        }

        public void Send(PeerConnection connection, INetMessage message)
        {
            SessionState state = null;
            MessageDescriptor descriptor = null;
            string failure = null;
            lock (_gate)
            {
                if (connection == null || !_connections.TryGetValue(connection.Id, out state)
                    || !ReferenceEquals(state.Connection, connection))
                {
                    failure = "connection is not owned by this transport";
                }
                else if (state.Terminal || state.Connection.State == ConnectionState.Handshaking
                    || !state.MessageIdsActivated || !Volatile.Read(ref _started)
                    || Volatile.Read(ref _stopRequested) || Volatile.Read(ref _disposed))
                {
                    failure = "connection is not ready for application traffic (phase "
                        + state.Connection.State + ")";
                }
                else if (!TryEncode(message, true, out var frame, out descriptor))
                {
                    failure = "message is not registered or exceeds the encoded frame limit";
                }
                else if (!QueueFrameLocked(state, frame, descriptor))
                {
                    failure = "bounded outbound queue is full";
                }
            }

            if (failure != null)
            {
                FailApplicationSend(connection, message, state, failure);
            }
        }

        void ICoopHandshakeTransport.SendHandshake(PeerConnection connection,
            INetMessage message)
        {
            SessionState state = null;
            string failure = null;
            lock (_gate)
            {
                if (connection == null || !_connections.TryGetValue(connection.Id, out state)
                    || !ReferenceEquals(state.Connection, connection))
                {
                    failure = "connection is not owned by this transport";
                }
                else if (!QueueHandshake(message, state, out failure))
                {
                    // QueueHandshake provides the contextual reason for an admission failure.
                }
            }

            if (failure != null)
            {
                FailApplicationSend(connection, message, state, "handshake " + failure);
            }
        }

        public void ActivateMessageIds(PeerConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (!IsPumpThread())
                throw new InvalidOperationException(
                    "KCP message-id activation must run on the pump thread");

            lock (_gate)
            {
                if (!_connections.TryGetValue(connection.Id, out var state)
                    || !ReferenceEquals(state.Connection, connection)
                    || state.Terminal || !state.Authenticated
                    || state.Session == null || !state.Session.IsAuthenticated
                    || connection.IsDisconnectingOrDisconnected
                    || _messageSnapshot == null || !Volatile.Read(ref _started)
                    || Volatile.Read(ref _stopRequested) || Volatile.Read(ref _disposed))
                {
                    throw new InvalidOperationException(
                        "Cannot activate message ids for an unknown or disconnected KCP peer: "
                        + connection);
                }

                // Activation is deliberately a one-way, idempotent state transition. Keeping
                // this under the same gate as encoding and queue admission makes the frame that
                // follows the caller's handshake transition unambiguously compact.
                if (!state.MessageIdsActivated)
                {
                    state.MessageIdsActivated = true;
                    DrainPendingCompactLocked(state);
                }
            }
        }

        public void Broadcast(INetMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            SessionState failedState = null;
            string failure = null;
            lock (_gate)
            {
                if (!Volatile.Read(ref _started) || Volatile.Read(ref _stopRequested)
                    || Volatile.Read(ref _disposed))
                {
                    failure = "transport is not running";
                }
                else
                {
                    byte[] compactFrame = null;
                    MessageDescriptor descriptor = null;
                    var admissions = new List<QueueAdmission>(_connections.Count);
                    foreach (var state in _connections.Values)
                    {
                        if (state.Terminal || state.Connection.IsDisconnectingOrDisconnected)
                        {
                            continue;
                        }

                        // A peer still completing the named handshake, or one that has not
                        // activated message ids yet (activation happens just before the world
                        // transfer), is not an application broadcast recipient. Its join baseline
                        // is sent after activation, so skipping it here is expected: a broadcast
                        // must never tear down a peer that is simply still joining.
                        if (state.Connection.State == ConnectionState.Handshaking
                            || !state.MessageIdsActivated)
                        {
                            continue;
                        }

                        if (compactFrame == null
                            && !TryEncode(message, true, out compactFrame, out descriptor))
                        {
                            failure = "message is not registered or exceeds the encoded frame limit";
                            break;
                        }

                        if (!CanQueueFrameLocked(state, compactFrame, descriptor))
                        {
                            failedState = state;
                            failure = "bounded outbound queue is full";
                            break;
                        }
                        admissions.Add(new QueueAdmission(state, compactFrame, descriptor));
                    }

                    if (failure == null)
                    {
                        for (var i = 0; i < admissions.Count; i++)
                        {
                            var admission = admissions[i];
                            EnqueueFrameLocked(admission.State, admission.Frame,
                                admission.Descriptor);
                        }
                    }
                }
            }

            if (failure != null)
            {
                FailApplicationSend(failedState?.Connection, message, failedState,
                    "broadcast " + failure);
            }
        }

        public double SecondsSinceLastRecv(PeerConnection connection)
        {
            if (connection == null)
                return double.MaxValue;

            SessionState state;
            lock (_gate)
            {
                if (!_connections.TryGetValue(connection.Id, out state)
                    || !ReferenceEquals(state.Connection, connection))
                {
                    return double.MaxValue;
                }
            }

            var now = MonotonicNow();
            return unchecked(now - state.LastReceiveTime) / 1000.0;
        }

        public void Kick(PeerConnection connection, DisconnectInfo info = null)
        {
            GracefulDisconnect(connection, info);
        }

        /// <summary>
        /// Requests the generic KCP disconnect handshake.  No application DTO is manufactured and
        /// no descriptor lane is selected for this lifecycle operation.
        /// </summary>
        public void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null)
        {
            if (connection == null)
                return;

            SessionState state;
            lock (_gate)
            {
                if (!_connections.TryGetValue(connection.Id, out state)
                    || !ReferenceEquals(state.Connection, connection)
                    || !Volatile.Read(ref _started)
                    || Volatile.Read(ref _stopRequested)
                    || Volatile.Read(ref _disposed))
                {
                    return;
                }
            }

            var disconnect = info ?? new DisconnectInfo("connection closed");
            var request = new DisconnectRequest(state, disconnect);
            if (IsPumpThread() && _pumping)
            {
                lock (_gate)
                {
                    state.DisconnectInfo = disconnect;
                }
                RequestDisconnectOnPump(request);
            }
            else
            {
                lock (_gate)
                {
                    if (state.Terminal || state.DisconnectRequested || state.DisconnectRequestQueued)
                    {
                        // A pending disconnect is replaceable. Retain the newest detail without
                        // allocating another queue entry for the same session.
                        if (!state.Terminal)
                            state.DisconnectInfo = disconnect;
                        return;
                    }

                    state.DisconnectRequestQueued = true;
                    state.DisconnectInfo = disconnect;
                }

                try
                {
                    EnqueueDisconnectRequest(request);
                }
                catch
                {
                    lock (_gate)
                    {
                        state.DisconnectRequestQueued = false;
                    }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!Volatile.Read(ref _started) || Volatile.Read(ref _disposed))
                    return;
                Volatile.Write(ref _stopRequested, true);
            }
            if (IsPumpThread())
            {
                StopOnPump();
                if (!_pumping)
                {
                    var budget = int.MaxValue;
                    DrainDisconnectResults(ref budget);
                }
            }
        }

        public void Dispose()
        {
            IProtocolSessionLease lease = null;
            var disposeNow = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed))
                    return;

                Volatile.Write(ref _disposeRequested, true);
                Volatile.Write(ref _stopRequested, true);
                if (!Volatile.Read(ref _started))
                {
                    // There is no pump to perform this work for an unstarted manager. Mark it
                    // disposed before dropping the lock so concurrent Start calls fail closed.
                    Volatile.Write(ref _disposed, true);
                    Volatile.Write(ref _stopRequested, false);
                    lease = _sessionLease;
                    _sessionLease = null;
                    _messageSnapshot = null;
                    disposeNow = true;
                }
            }

            if (disposeNow)
            {
                lease?.Dispose();
                DisposeBearerResources();
            }
            else if (IsPumpThread())
            {
                StopOnPump();
                if (!_pumping)
                {
                    var budget = int.MaxValue;
                    DrainDisconnectResults(ref budget);
                }
            }
        }

        private bool QueueHandshake(INetMessage message, SessionState state, out string failure)
        {
            failure = null;
            if (state.Terminal
                || (state.Connection.State != ConnectionState.Handshaking
                    && state.Connection.State != ConnectionState.Transferring)
                || state.MessageIdsActivated || !Volatile.Read(ref _started)
                || Volatile.Read(ref _stopRequested) || Volatile.Read(ref _disposed))
            {
                failure = "connection is not in a pre-activation phase";
                return false;
            }

            if (!TryEncode(message, false, out var frame, out var descriptor))
            {
                failure = "message is not registered or exceeds the encoded frame limit";
                return false;
            }

            if (!QueueFrameLocked(state, frame, descriptor))
            {
                failure = "bounded outbound queue is full";
                return false;
            }
            return true;
        }

        private bool TryEncode(INetMessage message, bool compactIds, out byte[] frame,
            out MessageDescriptor descriptor)
        {
            frame = null;
            descriptor = null;
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var snapshot = _messageSnapshot;
            if (snapshot == null)
                return false;
            if (!snapshot.TryGet(message.GetType(), out descriptor))
                return false;

            frame = NetMessageCodec.Encode(message, snapshot, compactIds);
            if (frame.Length < Msg.MinimumFrameSize
                || frame.Length > _options.MaxEncodedFrameBytes)
            {
                frame = null;
                return false;
            }

            return true;
        }

        private bool QueueFrameLocked(SessionState state, byte[] frame,
            MessageDescriptor descriptor)
        {
            if (!CanQueueFrameLocked(state, frame, descriptor))
                return false;

            EnqueueFrameLocked(state, frame, descriptor);
            return true;
        }

        private bool CanQueueFrameLocked(SessionState state, byte[] frame,
            MessageDescriptor descriptor)
        {
            if (frame == null || descriptor == null || state.Terminal)
                return false;

            if (!state.MessageIdsActivated
                && frame.Length > _options.MaxHandshakeFrameBytes)
            {
                LogWarning("KCP peer " + state.Connection.Id
                    + " rejected a pre-activation frame of " + frame.Length
                    + " bytes; limit is " + _options.MaxHandshakeFrameBytes);
                return false;
            }

            var reliable = descriptor.Reliability == Reliability.Reliable;
            if (reliable)
            {
                if (frame.Length > _options.MaxEncodedFrameBytes
                    || frame.Length > _options.MaxReliableBytes)
                {
                    return false;
                }

                if (state.ReliableFrameCount >= _options.MaxReliableFrames
                    || state.ReliableBytes > _options.MaxReliableBytes - frame.Length)
                    return false;

                return true;
            }

            // Unreliable application messages are complete encoded frames.  The private envelope
            // is included in this admission check; there is deliberately no fallback to KCP
            // reliability and no application-level fragmentation on this lane.
            if (frame.Length > _options.MaxEncodedFrameBytes
                || frame.Length > _options.MaxTransientBytes
                || frame.Length > state.Session.UnreliableMax - EnvelopeSize
                || frame.Length > _options.MaxTransientBytes - EnvelopeSize)
            {
                return false;
            }

            if (state.TransientFrameCount >= _options.MaxTransientFrames
                || state.TransientBytes > _options.MaxTransientBytes - frame.Length)
                return false;

            return true;
        }

        private static void EnqueueFrameLocked(SessionState state, byte[] frame,
            MessageDescriptor descriptor)
        {
            if (descriptor.Reliability == Reliability.Reliable)
            {
                state.Reliable.Enqueue(new OutboundFrame(frame, 0));
                state.ReliableFrameCount++;
                state.ReliableBytes += frame.Length;
                return;
            }

            var transient = new OutboundFrame(frame, state.NextTransientFrameId);
            state.NextTransientFrameId = NextFrameId(state.NextTransientFrameId);
            state.Transient.Enqueue(transient);
            state.TransientFrameCount++;
            state.TransientBytes += frame.Length;
        }

        private void FlushTransient(SessionState state)
        {
            if (!state.Authenticated || state.Terminal)
                return;

            while (true)
            {
                OutboundFrame frame;
                lock (_gate)
                {
                    if (state.Transient.Count == 0)
                        return;
                    frame = state.Transient.Dequeue();
                    state.TransientFrameCount--;
                    state.TransientBytes -= frame.Frame.Length;
                }

                var total = EnvelopeSize + frame.Frame.Length;
                if (total > state.Session.UnreliableMax)
                {
                    // This can only happen if a caller changes the session configuration outside
                    // this manager, but retain a fail-closed check at the bearer boundary.
                    DisconnectForProtocol(state, "unreliable frame exceeds KCP limit");
                    return;
                }

                EnsureScratch(state, total);
                WriteEnvelope(state.Scratch, 0, EnvelopeUnreliable, frame.Id,
                    frame.Frame.Length, 0, frame.Frame.Length);
                Buffer.BlockCopy(frame.Frame, 0, state.Scratch, EnvelopeSize, frame.Frame.Length);
                if (!state.Session.SendUnreliable(new ArraySegment<byte>(state.Scratch, 0, total)))
                    return;
            }
        }

        private void FlushReliable(SessionState state)
        {
            if (!state.Authenticated || state.Terminal)
                return;

            var payloadLimit = state.FragmentPayloadLimit;
            while (!state.Terminal
                && state.Session.PendingReliableSegments < _options.MaxKcpQueuedSegments)
            {
                if (state.ActiveReliable == null)
                {
                    lock (_gate)
                    {
                        if (state.Reliable.Count == 0)
                            return;
                        state.ActiveReliable = state.Reliable.Dequeue();
                        state.ActiveReliable.Id = state.NextFrameId;
                        state.NextFrameId = NextFrameId(state.NextFrameId);
                    }
                }

                var frame = state.ActiveReliable;
                var remaining = frame.Frame.Length - frame.Offset;
                var count = Math.Min(payloadLimit, remaining);
                var kind = frame.Frame.Length <= payloadLimit && frame.Offset == 0
                    ? EnvelopeComplete : EnvelopeFragment;
                var envelopeLength = EnvelopeSize + count;
                EnsureScratch(state, envelopeLength);
                WriteEnvelope(state.Scratch, 0, kind, frame.Id, frame.Frame.Length,
                    frame.Offset, count);
                Buffer.BlockCopy(frame.Frame, frame.Offset, state.Scratch, EnvelopeSize, count);

                if (!state.Session.SendReliable(new ArraySegment<byte>(state.Scratch, 0,
                    envelopeLength)))
                {
                    return;
                }

                frame.Offset += count;
                if (frame.Offset == frame.Frame.Length)
                {
                    state.ActiveReliable = null;
                    lock (_gate)
                    {
                        state.ReliableFrameCount--;
                        state.ReliableBytes -= frame.Frame.Length;
                    }
                }
            }
        }

        private void OnDatagram(DatagramPeer peer, ArraySegment<byte> datagram)
        {
            _datagramsThisPump++;
            // IDatagramTransport promises a callback-local segment. InputDatagram consumes it
            // synchronously, before the bearer can reuse its receive buffer.
            SessionState state;
            lock (_gate)
            {
                if (!_states.TryGetValue(peer, out state) || state.Terminal)
                    return;
            }

            bool consumed;
            using (CardShopCoop.Util.PerfProbe.ThreadSample("net.dgram"))
            {
                consumed = state.Session.InputDatagram(datagram);
            }
            if (consumed)
                state.LastReceiveTime = _lastClock;
        }

        private void OnPeerAvailable(DatagramPeer peer)
        {
            if (!Volatile.Read(ref _started) || Volatile.Read(ref _stopRequested)
                || Volatile.Read(ref _disposed))
            {
                return;
            }

            if (IsPumpThread() && _pumping)
                CreateSessionOnPump(peer);
            else
                EnqueuePeerSignal(new PeerSignal(peer, false, null));
        }

        private void OnPeerClosed(DatagramPeer peer, string reason)
        {
            if (!Volatile.Read(ref _started) || Volatile.Read(ref _stopRequested)
                || Volatile.Read(ref _disposed))
            {
                return;
            }

            if (IsPumpThread() && _pumping)
                CloseSessionOnPump(peer, reason);
            else
                EnqueuePeerSignal(new PeerSignal(peer, true, reason));
        }

        private void EnqueuePeerSignal(PeerSignal signal)
        {
            lock (_gate)
            {
                if (!Volatile.Read(ref _started) || Volatile.Read(ref _stopRequested)
                    || Volatile.Read(ref _disposed))
                {
                    return;
                }

                if (signal.Closed)
                {
                    // A repeated close callback is the same terminal transition. It is safe to
                    // coalesce it, but never discard a close merely to make room for another
                    // peer's signal.
                    if (_peerTerminalSignals.Contains(signal.Peer))
                        return;

                    EnsurePeerSignalCapacityLocked("terminal peer signal");
                    _peerTerminalSignals.Add(signal.Peer);
                    _peerSignals.AddLast(signal);
                    return;
                }

                // Once a terminal callback is queued, it is authoritative. A stale availability
                // callback must not be allowed to overtake it and recreate a closed peer.
                if (_peerTerminalSignals.Contains(signal.Peer))
                    return;

                if (_pendingPeerAvailableSignals.TryGetValue(signal.Peer, out var pending))
                {
                    pending.Value = signal;
                    return;
                }

                EnsurePeerSignalCapacityLocked("peer signal");
                _pendingPeerAvailableSignals.Add(signal.Peer, _peerSignals.AddLast(signal));
            }
        }

        private void DrainPeerSignals(ref int budget)
        {
            while (budget > 0)
            {
                PeerSignal signal;
                lock (_gate)
                {
                    if (_peerSignals.First == null)
                        return;

                    var node = _peerSignals.First;
                    signal = node.Value;
                    _peerSignals.RemoveFirst();
                    if (!signal.Closed
                        && _pendingPeerAvailableSignals.TryGetValue(signal.Peer, out var pending)
                        && ReferenceEquals(node, pending))
                    {
                        _pendingPeerAvailableSignals.Remove(signal.Peer);
                    }
                }

                budget--;
                if (signal.Closed)
                {
                    try
                    {
                        CloseSessionOnPump(signal.Peer, signal.Reason);
                    }
                    finally
                    {
                        lock (_gate)
                            _peerTerminalSignals.Remove(signal.Peer);
                    }
                }
                else
                    CreateSessionOnPump(signal.Peer);
            }
        }

        private void EnsurePeerSignalCapacityLocked(string kind)
        {
            if (_peerSignals.Count < _options.MaxPendingPeerSignals)
                return;

            var error = new InvalidOperationException(
                "KCP peer-signal queue exceeded its " + _options.MaxPendingPeerSignals
                + " item limit while admitting " + kind
                + "; transport cannot safely continue.");
            LogError(error.Message);
            throw error;
        }

        private void ClearPeerSignalsLocked()
        {
            _peerSignals.Clear();
            _pendingPeerAvailableSignals.Clear();
            _peerTerminalSignals.Clear();
        }

        private void EnqueueDisconnectRequest(DisconnectRequest request)
        {
            lock (_gate)
            {
                if (!Volatile.Read(ref _started) || Volatile.Read(ref _stopRequested)
                    || Volatile.Read(ref _disposed))
                {
                    request.State.DisconnectRequestQueued = false;
                    return;
                }

                if (Volatile.Read(ref _disconnectRequestCount)
                    >= _options.MaxPendingDisconnectRequests)
                {
                    var error = new InvalidOperationException(
                        "KCP disconnect-request queue exceeded its "
                        + _options.MaxPendingDisconnectRequests
                        + " item limit; transport cannot safely continue.");
                    LogError(error.Message);
                    throw error;
                }

                _disconnectRequests.Enqueue(request);
                Interlocked.Increment(ref _disconnectRequestCount);
            }
        }

        private void DrainDisconnectRequests(ref int budget)
        {
            while (budget > 0 && _disconnectRequests.TryDequeue(out var request))
            {
                budget--;
                Interlocked.Decrement(ref _disconnectRequestCount);
                RequestDisconnectOnPump(request);
            }
        }

        private void RequestDisconnectOnPump(DisconnectRequest request)
        {
            lock (_gate)
            {
                request.State.DisconnectRequestQueued = false;
                if (request.State.Terminal)
                    return;
                if (request.State.DisconnectInfo == null)
                    request.State.DisconnectInfo = request.Info;
                request.State.DisconnectRequested = true;
            }
            DisconnectOnPump(request.State);
        }

        private void CreateSessionOnPump(DatagramPeer peer)
        {
            if (!peer.IsValid || !Volatile.Read(ref _started)
                || Volatile.Read(ref _stopRequested) || Volatile.Read(ref _disposed))
                return;

            var pendingHandshakeLimitReached = false;
            lock (_gate)
            {
                if (_states.ContainsKey(peer))
                    return;

                var pendingHandshakeCount = 0;
                foreach (var existing in _states.Values)
                {
                    if (!existing.Terminal && !existing.MessageIdsActivated)
                        pendingHandshakeCount++;
                }
                pendingHandshakeLimitReached = pendingHandshakeCount
                    >= _options.MaxPendingHandshakeSessions;
            }

            if (pendingHandshakeLimitReached)
            {
                LogWarning("KCP peer " + peer + " rejected: pending application handshake limit "
                    + _options.MaxPendingHandshakeSessions + " reached");
                if (_datagrams.IsRunning)
                    _datagrams.Close(peer, "pending application handshake limit reached");
                return;
            }

            var cookie = _isHost ? NewCookie() : 0u;
            var state = new SessionState(peer, AllocateConnectionId())
            {
                LastReceiveTime = _lastClock,
                HandshakeStarted = _lastClock
            };
            state.Session = new KcpSession(_config, _isHost, cookie,
                datagram => RawSend(state, datagram),
                (message, channel) => OnSessionData(state, message, channel),
                () => OnSessionAuthenticated(state),
                () => OnSessionDisconnected(state),
                (error, detail) => OnSessionError(state, error, detail),
                _lastClock);

            state.FragmentPayloadLimit = ComputeFragmentPayloadLimit(state.Session);
            if (state.FragmentPayloadLimit <= 0)
                throw new InvalidOperationException("KCP MTU leaves no room for the private frame envelope");
            state.Scratch = new byte[Math.Max(EnvelopeSize + state.FragmentPayloadLimit,
                EnvelopeSize + state.Session.UnreliableMax)];

            var duplicate = false;
            lock (_gate)
            {
                duplicate = _states.ContainsKey(peer) || Volatile.Read(ref _stopRequested)
                    || Volatile.Read(ref _disposed);
                if (!duplicate)
                    _states.Add(peer, state);
            }

            if (duplicate)
                return;

            state.Session.Start(_lastClock);
        }

        private void CloseSessionOnPump(DatagramPeer peer, string reason)
        {
            SessionState state;
            lock (_gate)
            {
                if (!_states.TryGetValue(peer, out state) || state.Terminal)
                    return;
            }

            // Preserve a reason a graceful disconnect already recorded: a bearer close can win
            // the race against the queued disconnect request, but the peer-facing reason must not
            // be replaced by the generic peer-close text.
            if (state.DisconnectInfo == null)
            {
                state.DisconnectInfo = new DisconnectInfo(
                    string.IsNullOrWhiteSpace(reason) ? "datagram peer closed" : reason,
                    true, "peer_closed", true, state.Connection.State);
            }
            state.DisconnectRequested = true;
            DisconnectOnPump(state);
        }

        private void OnSessionAuthenticated(SessionState state)
        {
            if (state.Terminal || !state.Session.IsAuthenticated)
                return;

            state.Authenticated = true;
            lock (_gate)
            {
                if (state.Terminal)
                    return;
                _connections[state.Connection.Id] = state;
            }

            // This is the only publication point. KCP's callback is synchronous with our pump.
            PeerConnected?.Invoke(state.Connection);
        }

        private void OnSessionData(SessionState state, ArraySegment<byte> data, KcpChannel channel)
        {
            if (state.Terminal || !state.Authenticated)
                return;

            if (channel == KcpChannel.Unreliable)
            {
                if (!TryReadAndValidateEnvelope(data, EnvelopeUnreliable, out var envelope))
                {
                    DisconnectForProtocol(state, "invalid unreliable frame envelope");
                    return;
                }
                var unreliableLimit = state.MessageIdsActivated
                    ? _options.MaxEncodedFrameBytes : _options.MaxHandshakeFrameBytes;
                if (envelope.PayloadLength > unreliableLimit)
                {
                    DisconnectForProtocol(state, "unreliable application frame exceeds its size limit");
                    return;
                }
                DeliverFrame(state, data.Array, data.Offset + EnvelopeSize,
                    envelope.PayloadLength, false);
                return;
            }

            if (!TryReadEnvelope(data, out var reliable))
            {
                DisconnectForProtocol(state, "invalid reliable frame envelope");
                return;
            }

            if (reliable.Kind == EnvelopeComplete)
            {
                var completeLimit = state.MessageIdsActivated
                    ? _options.MaxEncodedFrameBytes : _options.MaxHandshakeFrameBytes;
                if (reliable.TotalLength != reliable.PayloadLength
                    || reliable.Offset != 0
                    || reliable.TotalLength > (uint)completeLimit
                    || !AcceptReliableId(state, reliable.FrameId))
                {
                    DisconnectForProtocol(state, "invalid complete reliable frame");
                    return;
                }

                // A complete frame may only follow a finished reassembly. If one lands on the
                // wire while a fragmented frame is still being rebuilt, the sender interleaved
                // frames and the frame-id sequence is no longer trustworthy. This is checked at
                // the receive point: complete frames buffered before message-id activation are
                // replayed later (DrainPendingCompactLocked) and must not be judged by the
                // reassembly state at replay time.
                if (state.Reassembly != null && state.ReassemblyOffset != 0)
                {
                    DisconnectForProtocol(state, "partial reliable frame delivery");
                    return;
                }

                DeliverFrame(state, data.Array, data.Offset + EnvelopeSize,
                    reliable.PayloadLength, true);
                state.NextInboundFrameId = NextFrameId(reliable.FrameId);
                return;
            }

            if (reliable.Kind != EnvelopeFragment || reliable.PayloadLength == 0
                || reliable.TotalLength <= reliable.PayloadLength
                || reliable.TotalLength > (uint)(state.MessageIdsActivated
                    ? _options.MaxEncodedFrameBytes : _options.MaxHandshakeFrameBytes)
                || reliable.Offset > reliable.TotalLength
                || reliable.PayloadLength > reliable.TotalLength - reliable.Offset)
            {
                DisconnectForProtocol(state, "invalid reliable fragment envelope");
                return;
            }

            if (reliable.FrameId != state.NextInboundFrameId)
            {
                DisconnectForProtocol(state, "reliable frame order violation");
                return;
            }

            if (state.Reassembly == null)
            {
                if (reliable.Offset != 0)
                {
                    DisconnectForProtocol(state, "reliable fragment did not start at zero");
                    return;
                }

                if (!TryReserveReassembly(state, reliable.TotalLength))
                {
                    DisconnectForProtocol(state, "aggregate reliable reassembly budget exceeded");
                    return;
                }

                try
                {
                    state.Reassembly = new byte[(int)reliable.TotalLength];
                }
                catch
                {
                    ReleaseReassembly(state);
                    throw;
                }
                state.ReassemblyFrameId = reliable.FrameId;
                state.ReassemblyTotal = reliable.TotalLength;
                state.ReassemblyOffset = 0;
                state.ReassemblyLastReceive = _lastClock;
            }

            if (state.ReassemblyFrameId != reliable.FrameId
                || state.ReassemblyTotal != reliable.TotalLength
                || state.ReassemblyOffset != reliable.Offset)
            {
                DisconnectForProtocol(state, "reliable fragment sequence violation");
                return;
            }

            Buffer.BlockCopy(data.Array, data.Offset + EnvelopeSize, state.Reassembly,
                (int)state.ReassemblyOffset, reliable.PayloadLength);
            state.ReassemblyOffset += (uint)reliable.PayloadLength;
            state.ReassemblyLastReceive = _lastClock;
            if (state.ReassemblyOffset == state.ReassemblyTotal)
            {
                var complete = state.Reassembly;
                var frameId = state.ReassemblyFrameId;
                ClearReassembly(state);
                DeliverFrame(state, complete, 0, complete.Length, true);
                state.NextInboundFrameId = NextFrameId(frameId);
            }
        }

        private void DeliverFrame(SessionState state, byte[] frame, int offset, int count,
            bool reliable)
        {
            _framesThisPump++;
            if (frame == null || offset < 0 || count < Msg.MinimumFrameSize
                || offset > frame.Length - count)
            {
                DisconnectForProtocol(state, "invalid application frame bounds");
                return;
            }

            var frameLimit = state.MessageIdsActivated
                ? _options.MaxEncodedFrameBytes : _options.MaxHandshakeFrameBytes;
            if (count > frameLimit)
            {
                DisconnectForProtocol(state, "application frame exceeds the pre-activation limit");
                return;
            }

            var compactFrame = IsCompactFrame(frame, offset, count);
            if (!state.MessageIdsActivated && compactFrame)
            {
                if (!TryQueuePendingCompact(state, frame, offset, count, reliable))
                {
                    if (reliable)
                    {
                        DisconnectForProtocol(state,
                            "pending compact application queue budget exhausted");
                    }
                    else
                    {
                        LogWarning("KCP transient compact frame dropped while awaiting message-id activation");
                    }
                }
                return;
            }
            if (state.MessageIdsActivated && !compactFrame)
            {
                DisconnectForProtocol(state,
                    "named application frame received after message-id activation");
                return;
            }

            // The pump no longer JSON-decodes here. It hands an owned frame copy to the decode
            // worker and re-applies the exact same validation once the decoded object comes back
            // (DrainDecodeResults), so every KCP-touching step stays on the pump thread.
            if (!TryReserveDecodeBudget(count))
            {
                if (reliable)
                    DisconnectForProtocol(state, "incoming application queue budget exhausted");
                // Never evict an older reliable message merely because a transient update arrived
                // late. The encoded frame admission is released by DrainDecodeResults.
                return;
            }

            var owned = new byte[count];
            Buffer.BlockCopy(frame, offset, owned, 0, count);
            _decodeQueue.Enqueue(new DecodeWork
            {
                Frame = owned,
                State = state,
                Reliable = reliable,
                CompactIds = state.MessageIdsActivated,
                BeforeActivation = !state.MessageIdsActivated,
                Snapshot = _messageSnapshot,
                FrameLimit = frameLimit - Msg.FrameHeaderSize,
                EncodedBytes = count,
            });
            EnsureDecodeWorker();
            _decodeSignal.Release();
        }

        private bool TryQueuePendingCompact(SessionState state, byte[] frame, int offset,
            int count, bool reliable)
        {
            lock (_gate)
            {
                if (state.Terminal || state.MessageIdsActivated
                    || state.PendingCompact.Count >= _options.MaxIncomingFrames
                    || _incoming.Count + _pendingActivationFrames + _decodePendingFrames
                        >= _options.MaxIncomingFrames
                    || _incomingBytes + _pendingActivationBytes + _decodePendingBytes
                        > _options.MaxIncomingBytes - count)
                {
                    return false;
                }

                var owned = new byte[count];
                Buffer.BlockCopy(frame, offset, owned, 0, count);
                state.PendingCompact.Enqueue(new PendingCompactFrame(owned, reliable));
                _pendingActivationFrames++;
                _pendingActivationBytes += count;
                return true;
            }
        }

        private void DrainPendingCompactLocked(SessionState state)
        {
            while (!state.Terminal && state.PendingCompact.Count > 0)
            {
                var pending = state.PendingCompact.Dequeue();
                _pendingActivationFrames--;
                _pendingActivationBytes -= pending.Frame.Length;
                if (_pendingActivationFrames < 0 || _pendingActivationBytes < 0)
                {
                    LogError("KCP pending compact-frame accounting underflow");
                    _pendingActivationFrames = Math.Max(0, _pendingActivationFrames);
                    _pendingActivationBytes = Math.Max(0, _pendingActivationBytes);
                }

                DeliverFrame(state, pending.Frame, 0, pending.Frame.Length, pending.Reliable);
            }

            if (state.Terminal)
                ClearPendingCompactLocked(state);
        }

        private static bool IsCompactFrame(byte[] frame, int offset, int count)
        {
            return frame != null && count >= Msg.MinimumFrameSize
                && offset >= 0 && offset <= frame.Length - count
                && frame[offset + Msg.FrameHeaderSize] == Msg.CompactTokenKind;
        }

        private bool TryEnqueueIncoming(InMsg message, int encodedBytes)
        {
            lock (_gate)
            {
                if (_incoming.Count + _pendingActivationFrames + _decodePendingFrames
                        >= _options.MaxIncomingFrames
                    || encodedBytes < Msg.MinimumFrameSize
                    || _incomingBytes + _pendingActivationBytes + _decodePendingBytes
                        > _options.MaxIncomingBytes - encodedBytes)
                {
                    return false;
                }

                _incoming.Enqueue(new IncomingItem(message, encodedBytes));
                _incomingBytes += encodedBytes;
                return true;
            }
        }

        /// <summary>Reserves queue budget for one frame that is about to be handed to the decode
        /// worker. The bytes stay counted until the decoded result is admitted or dropped, so the
        /// worker cannot let memory grow past the same inbound budget the synchronous path used.</summary>
        private bool TryReserveDecodeBudget(int encodedBytes)
        {
            lock (_gate)
            {
                if (_incoming.Count + _pendingActivationFrames + _decodePendingFrames
                        >= _options.MaxIncomingFrames
                    || encodedBytes < Msg.MinimumFrameSize
                    || _incomingBytes + _pendingActivationBytes + _decodePendingBytes
                        > _options.MaxIncomingBytes - encodedBytes)
                {
                    return false;
                }

                _decodePendingFrames++;
                _decodePendingBytes += encodedBytes;
                return true;
            }
        }

        private void ReleaseDecodeBudget(int encodedBytes)
        {
            lock (_gate)
            {
                _decodePendingFrames--;
                _decodePendingBytes -= encodedBytes;
                if (_decodePendingFrames < 0)
                {
                    LogError("KCP decode-queue frame accounting underflow");
                    _decodePendingFrames = 0;
                }
                if (_decodePendingBytes < 0)
                {
                    LogError("KCP decode-queue byte accounting underflow");
                    _decodePendingBytes = 0;
                }
            }
        }

        private void EnsureDecodeWorker()
        {
            if (_decodeThread != null)
            {
                return;
            }

            _decodeStop = false;
            _decodeSignal = new SemaphoreSlim(0);
            var thread = new Thread(DecodeWorkerLoop)
            {
                IsBackground = true,
                Name = "CardShopCoop.KcpDecode",
            };
            _decodeThread = thread;
            thread.Start();
        }

        private void DecodeWorkerLoop()
        {
            var signal = _decodeSignal;
            var workerThreadId = Thread.CurrentThread.ManagedThreadId;
            while (!Volatile.Read(ref _decodeStop))
            {
                signal.Wait();
                while (!Volatile.Read(ref _decodeStop) && _decodeQueue.TryDequeue(out var work))
                {
                    _decodeResults.Enqueue(Decode(work, workerThreadId));
                }
            }
        }

        private static DecodeResult Decode(DecodeWork work, int workerThreadId)
        {
            var result = new DecodeResult
            {
                State = work.State,
                Reliable = work.Reliable,
                BeforeActivation = work.BeforeActivation,
                Snapshot = work.Snapshot,
                EncodedBytes = work.EncodedBytes,
            };
            try
            {
                if (Msg.TryDecodeFrame(work.Frame, 0, work.Frame.Length, work.State.Connection,
                    work.FrameLimit, work.Snapshot, work.CompactIds, out var message))
                {
                    result.Message = message;
                    result.Ok = true;
                }
            }
            catch (Exception error)
            {
                // TryDecodeFrame already fails soft on malformed payloads; this is belt-and-braces
                // so a worker thread can never die and silently stop all inbound message flow.
                LogDecodeWorkerError(workerThreadId, error);
            }
            return result;
        }

        private static void LogDecodeWorkerError(int workerThreadId, Exception error)
        {
            try
            {
                LogError("KCP decode worker (thread " + workerThreadId + ") faulted: " + error);
            }
            catch
            {
                // Never let diagnostics take the worker down.
            }
        }

        /// <summary>Applies the exact post-decode validation the synchronous path used, on the
        /// pump thread, for every frame the worker finished since the last pass.</summary>
        private void DrainDecodeResults()
        {
            while (_decodeResults.TryDequeue(out var result))
            {
                ReleaseDecodeBudget(result.EncodedBytes);
                var state = result.State;
                if (state == null || state.Terminal)
                {
                    continue;
                }

                if (!result.Ok || result.Message.Message == null)
                {
                    DisconnectForProtocol(state, "invalid application frame");
                    continue;
                }

                var message = result.Message;
                message.ReceivedBeforeMessageIdActivation = result.BeforeActivation;

                // Compact identity is only a decoding choice. It never authenticates a peer or
                // grants a handler path; Core's central router still applies application gates.
                if (result.Snapshot == null
                    || !result.Snapshot.TryGet(message.MessageType, out var descriptor)
                    || (result.Reliable ? descriptor.Reliability != Reliability.Reliable
                        : descriptor.Reliability != Reliability.Transient))
                {
                    DisconnectForProtocol(state, "application delivery does not match descriptor");
                    continue;
                }

                // The terminal disconnect control is transport lifecycle, not feature data: record
                // the peer's reason as the connection's terminal claim before anything else so the
                // ConnectionEvent carries it. The message is still admitted below for the
                // application-level fallback path; Core drops it once the connection is terminal.
                if (message.Message is DisconnectMessage disconnectMessage)
                {
                    ApplyRemoteDisconnect(state, disconnectMessage);
                }

                if (!TryEnqueueIncoming(message, result.EncodedBytes))
                {
                    if (result.Reliable)
                        DisconnectForProtocol(state, "incoming application queue budget exhausted");
                    // Never evict an older reliable message merely because a transient update
                    // arrived late. The encoded frame admission is released by TryDequeueIncoming.
                }
            }
        }

        private void StopDecodeWorker()
        {
            var thread = _decodeThread;
            var signal = _decodeSignal;
            _decodeThread = null;
            _decodeSignal = null;
            if (thread != null)
            {
                Volatile.Write(ref _decodeStop, true);
                signal?.Release();
                try
                {
                    if (thread.IsAlive && thread != Thread.CurrentThread)
                    {
                        thread.Join(500);
                    }
                }
                catch (Exception error)
                {
                    LogError("KCP decode worker join failed: " + error);
                }
            }

            while (_decodeQueue.TryDequeue(out _))
            {
            }
            while (_decodeResults.TryDequeue(out _))
            {
            }
            _decodePendingFrames = 0;
            _decodePendingBytes = 0;
            Volatile.Write(ref _decodeStop, false);
        }

        private void OnSessionDisconnected(SessionState state)
        {
            if (state.Terminal)
                return;

            var info = state.DisconnectInfo;
            if (info == null)
            {
                var remote = !state.HasError;
                var reason = remote ? "remote disconnected"
                    : string.IsNullOrWhiteSpace(state.LastErrorDetail)
                        ? "KCP session closed"
                        : "KCP session closed: " + state.LastErrorDetail;
                info = new DisconnectInfo(reason,
                    remote, remote ? "remote_closed" : "kcp_closed", remote,
                    state.Connection.State);

                // No application reason yet: the peer's terminal DisconnectMessage may still be on
                // the async decode path. Hold completion briefly so that decoded frame can claim
                // the reason, instead of publishing the generic close.
                state.DisconnectInfo = info;
                state.PendingDisconnectCompletion = true;
                state.DisconnectCompletionDeadline = unchecked(MonotonicNow() + DisconnectDecodeGraceMs);
                LogInfo("coop: session disconnected for peer " + state.Connection.Id
                    + "; holding completion " + DisconnectDecodeGraceMs
                    + "ms for a terminal reason frame.");
                return;
            }

            CompleteDisconnect(state, info);
        }

        private void OnSessionError(SessionState state, ErrorCode error, string detail)
        {
            state.HasError = true;
            state.LastErrorDetail = detail;
            if (error == ErrorCode.InvalidReceive || error == ErrorCode.Unexpected
                || error == ErrorCode.Congestion)
            {
                LogWarning("KCP peer " + state.Connection.Id + ": " + detail);
            }
        }

        private void DisconnectForProtocol(SessionState state, string reason)
        {
            if (state.Terminal)
                return;
            state.DisconnectInfo ??= new DisconnectInfo(reason, false, "protocol_error", false,
                state.Connection.State);
            LogWarning("coop: protocol disconnect for peer " + state.Connection.Id + ": " + reason
                + ".");
            state.DisconnectRequested = true;
            DisconnectOnPump(state);
        }

        private void DisconnectOnPump(SessionState state)
        {
            if (state.Terminal)
                return;

            state.DisconnectRequested = false;
            var info = state.DisconnectInfo ?? new DisconnectInfo("connection closed");
            state.DisconnectInfo = info;
            state.Connection.BeginDisconnect(info);

            // Tell the peer why this connection is ending. The frame is flushed in this tick, but
            // KCP's own disconnect sends unreliable goodbye headers that can reach the peer BEFORE
            // the reliable terminal frame is decoded, which made the peer publish a generic
            // "remote disconnected" instead of the real reason. When the terminal frame is sent,
            // hold the KCP close until it is acknowledged (bounded by a short grace) so the peer
            // always sees the reason first.
            if (TrySendTerminalFrame(state, info))
            {
                state.PendingTerminalDisconnect = true;
                state.TerminalDisconnectDeadline = unchecked(MonotonicNow() + TerminalFrameAckGraceMs);
                LogInfo("coop: holding disconnect for peer " + state.Connection.Id
                    + " until the terminal reason frame is acknowledged.");
                return;
            }

            FinishDisconnectOnPump(state);
        }

        /// <summary>Completes a disconnect whose terminal reason frame was already sent (or could
        /// not be sent). Safe to call from the pump only.</summary>
        private void FinishDisconnectOnPump(SessionState state)
        {
            var info = state.DisconnectInfo ?? new DisconnectInfo("connection closed");
            if (!state.Session.IsDisconnected)
            {
                state.Session.Disconnect();
            }
            else
            {
                CompleteDisconnect(state, info);
            }
        }

        /// <summary>
        /// Encodes the terminal <see cref="DisconnectMessage"/> with the session's framing mode
        /// (named before message-id activation, compact after) and puts it on the wire before the
        /// KCP-level disconnect. The frame goes through the same private envelope and frame-id
        /// sequence as <see cref="FlushReliable"/>: still-queued frames (which have no id yet) are
        /// dropped, while a partially transmitted frame or an unacknowledged KCP backlog skips the
        /// goodbye rather than desynchronising that sequence. Returns false when the peer cannot
        /// receive it.
        /// </summary>
        private bool TrySendTerminalFrame(SessionState state, DisconnectInfo info)
        {
            if (info.Remote || state.Session == null || state.Session.IsDisconnected
                || !state.Session.IsAuthenticated)
            {
                return false;
            }

            // The receiver validates reliable frame ids strictly in order, and kcp2k only flushes
            // a window of segments per pass. Only emit the terminal frame when no frame id has been
            // handed to KCP and nothing is mid-transmission, so its id is exactly the next the peer
            // expects and the flush below can actually deliver it before the goodbye headers.
            if (state.ActiveReliable != null || state.Session.PendingReliableSegments != 0)
            {
                return false;
            }

            if (state.Reliable.Count != 0)
            {
                // Queued frames have no id yet and the connection is ending, so dropping them is
                // safe and keeps the terminal frame's id in sequence for the peer.
                lock (_gate)
                {
                    state.Reliable.Clear();
                    state.ReliableFrameCount = 0;
                    state.ReliableBytes = 0;
                }
            }

            var message = new DisconnectMessage
            {
                Reason = info.Reason,
                Code = info.Code,
                Retryable = info.Retryable,
                Phase = (int)info.Phase,
            };
            if (!TryEncode(message, state.MessageIdsActivated, out var frame, out _)
                || frame.Length > state.Session.ReliableMax - EnvelopeSize)
            {
                return false;
            }

            var envelopeLength = EnvelopeSize + frame.Length;
            EnsureScratch(state, envelopeLength);
            var frameId = state.NextFrameId;
            WriteEnvelope(state.Scratch, 0, EnvelopeComplete, frameId, frame.Length, 0,
                frame.Length);
            Buffer.BlockCopy(frame, 0, state.Scratch, EnvelopeSize, frame.Length);
            if (!state.Session.SendReliable(new ArraySegment<byte>(state.Scratch, 0,
                envelopeLength)))
            {
                return false;
            }
            state.NextFrameId = NextFrameId(frameId);

            // TickOutgoing is interval paced, so an explicit flush is required to guarantee the
            // frame precedes the five unreliable goodbye headers Session.Disconnect sends.
            try
            {
                state.Session.TickOutgoing(MonotonicNow());
                state.Session.FlushOutgoing();
            }
            catch (Exception error)
            {
                // The goodbye is best effort; a bearer failure must never block teardown.
                LogWarning("KCP terminal disconnect frame could not be flushed for peer "
                    + state.Connection.Id + ": " + error.Message);
            }
            return true;
        }

        /// <summary>
        /// Records a peer's terminal <see cref="DisconnectMessage"/> as the connection's first
        /// claim, then finishes the teardown so the exactly-once ConnectionEvent carries the
        /// peer's own explanation instead of a generic close.
        /// </summary>
        private void ApplyRemoteDisconnect(SessionState state, DisconnectMessage message)
        {
            var phase = Enum.IsDefined(typeof(ConnectionState), message.Phase)
                ? (ConnectionState)message.Phase : ConnectionState.Disconnecting;
            var info = new DisconnectInfo(message.Reason, true, message.Code, message.Retryable,
                phase);
            LogInfo("coop: remote terminal reason for peer " + state.Connection.Id + ": "
                + info.Code + " / " + info.Reason);
            if (!state.Connection.RecordRemoteDisconnect(info))
            {
                // A local goodbye or an earlier remote reason already owns this terminal claim.
                LogInfo("coop: ignoring remote terminal reason for peer " + state.Connection.Id
                    + " (already claimed: " + (state.Connection.DisconnectReason?.Code ?? "?") + ").");
                return;
            }

            state.DisconnectInfo = state.Connection.DisconnectReason ?? info;
            if (!state.Session.IsDisconnected)
                state.Session.Disconnect();
            else
                CompleteDisconnect(state, state.DisconnectInfo);
        }

        private void CompleteDisconnect(SessionState state, DisconnectInfo info)
        {
            // The connection atomically keeps the first terminal reason (a local goodbye or the
            // peer's recorded disconnect control). Publish that exact reason so a later default
            // can never overwrite it.
            var claimed = state.Connection.DisconnectReason ?? info;
            lock (_gate)
            {
                if (state.Terminal)
                    return;
                state.Terminal = true;
                state.DisconnectInfo = claimed;
                _states.Remove(state.DatagramPeer);
                if (state.Authenticated)
                    _connections.Remove(state.Connection.Id);
                state.Reliable.Clear();
                state.Transient.Clear();
                state.ActiveReliable = null;
                ClearReassemblyLocked(state);
                ClearPendingCompactLocked(state);
                state.ReliableBytes = 0;
                state.TransientBytes = 0;
            }

            var wasPublished = state.Authenticated;
            state.Connection.BeginDisconnect(claimed);
            state.Connection.TryMarkDisconnected();
            Exception resultFailure = null;
            if (wasPublished)
            {
                try
                {
                    EnqueueDisconnectResult(new ConnectionEvent(state.Connection, claimed));
                }
                catch (InvalidOperationException error)
                {
                    // The terminal state is already committed. Still close the bearer before
                    // surfacing the overflow so a failed result admission cannot leak a native
                    // connection while the caller receives the loud failure.
                    resultFailure = error;
                }
            }

            // Close is idempotent in the datagram contract.  The state is removed first so a
            // synchronous PeerClosed notification cannot publish a second terminal event.
            if (_datagrams.IsRunning)
                _datagrams.Close(state.DatagramPeer, claimed.Reason);

            if (resultFailure != null)
                throw resultFailure;
        }

        private void EnqueueDisconnectResult(ConnectionEvent result)
        {
            lock (_gate)
            {
                if (_pendingDisconnectResults.Count + Disconnects.Count
                    >= _options.MaxPendingDisconnectResults)
                {
                    var error = new InvalidOperationException(
                        "KCP disconnect-result queue exceeded its "
                        + _options.MaxPendingDisconnectResults
                        + " item limit; a terminal disconnect cannot be published safely.");
                    LogError(error.Message);
                    throw error;
                }

                _pendingDisconnectResults.Enqueue(result);
            }
        }

        private void DrainDisconnectResults(ref int budget)
        {
            while (budget > 0)
            {
                ConnectionEvent result;
                lock (_gate)
                {
                    if (_pendingDisconnectResults.Count == 0)
                        return;

                    if (Disconnects.Count >= _options.MaxPendingDisconnectResults)
                    {
                        var error = new InvalidOperationException(
                            "KCP public disconnect-result queue is full; a terminal disconnect "
                            + "cannot be published safely.");
                        LogError(error.Message);
                        throw error;
                    }

                    result = _pendingDisconnectResults.Dequeue();
                    Disconnects.Enqueue(result);
                }
                budget--;
            }
        }

        private void StopOnPump()
        {
            if (!Volatile.Read(ref _started) && !Volatile.Read(ref _disposeRequested))
                return;

            Volatile.Write(ref _stopRequested, true);
            IProtocolSessionLease lease = null;
            SessionState[] snapshot;
            lock (_gate)
            {
                snapshot = new SessionState[_states.Count];
                _states.Values.CopyTo(snapshot, 0);
            }

            try
            {
                for (var i = 0; i < snapshot.Length; i++)
                {
                    var state = snapshot[i];
                    if (state.Terminal)
                        continue;
                    // Preserve a reason already queued by a graceful disconnect: Stop is the
                    // normal end of CoopCore.Shutdown, and its detail is what the peer should see.
                    if (state.DisconnectInfo == null)
                    {
                        state.DisconnectInfo = new DisconnectInfo("transport stopped", false,
                            "stopped", true, state.Connection.State);
                    }
                    DisconnectOnPump(state);
                }

                _datagrams.Stop();
            }
            finally
            {
                try
                {
                    if (_datagrams.IsRunning)
                        _datagrams.Stop();
                }
                catch (Exception cleanupError)
                {
                    LogError("KCP bearer shutdown cleanup failed: " + cleanupError);
                }

                lock (_gate)
                {
                    foreach (var state in _states.Values)
                    {
                        state.Terminal = true;
                        ClearReassemblyLocked(state);
                        ClearPendingCompactLocked(state);
                    }
                    _states.Clear();
                    _connections.Clear();
                    _reassemblyBytes = 0;
                    _pendingActivationFrames = 0;
                    _pendingActivationBytes = 0;
                    while (_incoming.TryDequeue(out _))
                    {
                    }
                    _incomingBytes = 0;
                    lease = _sessionLease;
                    _sessionLease = null;
                    _messageSnapshot = null;
                    Volatile.Write(ref _started, false);
                    Volatile.Write(ref _stopRequested, false);
                }

                lock (_gate)
                {
                    ClearPeerSignalsLocked();
                    while (_disconnectRequests.TryDequeue(out _))
                    {
                    }
                    Volatile.Write(ref _disconnectRequestCount, 0);
                }

                StopDecodeWorker();

                try
                {
                    lease?.Dispose();
                }
                finally
                {
                    if (Volatile.Read(ref _disposeRequested))
                        DisposeBearerResources();
                }
            }
        }

        private void DisposeBearerResources()
        {
            lock (_gate)
            {
                if (_resourcesDisposed)
                    return;

                _resourcesDisposed = true;
                while (_incoming.TryDequeue(out _))
                {
                }
                _incomingBytes = 0;
                _pendingActivationFrames = 0;
                _pendingActivationBytes = 0;
                Volatile.Write(ref _disposed, true);
            }

            StopDecodeWorker();

            // Unsubscribe first so a late bearer notification cannot retain this manager or
            // enqueue work while its native resources are being released.
            _datagrams.PeerAvailable -= OnPeerAvailable;
            _datagrams.PeerClosed -= OnPeerClosed;
            _datagrams.Dispose();
        }

        private int AllocateConnectionId()
        {
            if (!_isHost)
                return 1;

            lock (_gate)
            {
                for (var attempts = 0; attempts < int.MaxValue; attempts++)
                {
                    var id = _nextHostConnectionId++;
                    if (id <= 0)
                    {
                        _nextHostConnectionId = 2;
                        id = 1;
                    }
                    if (!_connections.ContainsKey(id))
                        return id;
                }
            }

            throw new InvalidOperationException("No connection ids remain");
        }

        private void RawSend(SessionState state, ArraySegment<byte> datagram)
        {
            if (state.Terminal || !_datagrams.IsRunning)
                return;
            try
            {
                _datagrams.Send(state.DatagramPeer, datagram);
            }
            catch (Exception error)
            {
                var reason = "KCP bearer send failed for peer " + state.Connection.Id + ": "
                    + error.Message;
                state.HasError = true;
                state.LastErrorDetail = reason;
                LogError(reason);
                state.DisconnectInfo ??= new DisconnectInfo(
                    "transport send failed; session recovery required", false,
                    "transport_send_failed", true, state.Connection.State);
                state.DisconnectRequested = true;
                throw;
            }
        }

        private void EnsureScratch(SessionState state, int length)
        {
            if (state.Scratch == null || state.Scratch.Length < length)
                state.Scratch = new byte[length];
        }

        private static int ComputeFragmentPayloadLimit(KcpSession session)
        {
            // KcpPeer's reliable limit is the application data limit after the KCP header.  Using
            // one outer KCP segment per private fragment keeps KCP's own message-oriented queue
            // bounded and makes queue pressure directly meaningful to the manager.
            var oneSegment = session.Mtu - KcpPeer.METADATA_SIZE - kcp2k.Kcp.OVERHEAD - 1;
            return Math.Min(session.ReliableMax - EnvelopeSize, oneSegment - EnvelopeSize);
        }

        private static uint NextFrameId(uint id)
        {
            var next = unchecked(id + 1u);
            return next == 0 ? 1u : next;
        }

        private static bool AcceptReliableId(SessionState state, uint id)
        {
            return id == state.NextInboundFrameId;
        }

        private bool TryReserveReassembly(SessionState state, uint totalLength)
        {
            if (totalLength == 0 || totalLength > int.MaxValue)
                return false;

            var bytes = (int)totalLength;
            lock (_gate)
            {
                if (state.ReassemblyReservedBytes != 0
                    || _reassemblyBytes > _options.MaxReassemblyBytes - bytes)
                {
                    return false;
                }

                state.ReassemblyReservedBytes = bytes;
                _reassemblyBytes += bytes;
                return true;
            }
        }

        private void ReleaseReassembly(SessionState state)
        {
            lock (_gate)
            {
                ClearReassemblyLocked(state);
            }
        }

        private void ClearReassembly(SessionState state)
        {
            lock (_gate)
            {
                ClearReassemblyLocked(state);
            }
        }

        private void ClearReassemblyLocked(SessionState state)
        {
            if (state.ReassemblyReservedBytes != 0)
            {
                _reassemblyBytes -= state.ReassemblyReservedBytes;
                if (_reassemblyBytes < 0)
                {
                    LogError("KCP reassembly accounting underflow for peer "
                        + state.Connection.Id);
                    _reassemblyBytes = 0;
                }
                state.ReassemblyReservedBytes = 0;
            }
            state.Reassembly = null;
            state.ReassemblyTotal = 0;
            state.ReassemblyOffset = 0;
            state.ReassemblyFrameId = 0;
        }

        private void ClearPendingCompactLocked(SessionState state)
        {
            while (state.PendingCompact.Count > 0)
            {
                var pending = state.PendingCompact.Dequeue();
                _pendingActivationFrames--;
                _pendingActivationBytes -= pending.Frame.Length;
            }

            if (_pendingActivationFrames < 0 || _pendingActivationBytes < 0)
            {
                LogError("KCP pending compact-frame accounting underflow while clearing peer");
                _pendingActivationFrames = Math.Max(0, _pendingActivationFrames);
                _pendingActivationBytes = Math.Max(0, _pendingActivationBytes);
            }
        }

        private static void WriteEnvelope(byte[] buffer, int offset, byte kind, uint id,
            int totalLength, int payloadOffset, int payloadLength)
        {
            buffer[offset] = EnvelopeMagic0;
            buffer[offset + 1] = EnvelopeMagic1;
            buffer[offset + 2] = EnvelopeVersion;
            buffer[offset + 3] = kind;
            WriteUInt32(buffer, offset + 4, id);
            WriteUInt32(buffer, offset + 8, unchecked((uint)totalLength));
            WriteUInt32(buffer, offset + 12, unchecked((uint)payloadOffset));
            WriteUInt32(buffer, offset + 16, unchecked((uint)payloadLength));
        }

        private static bool TryReadEnvelope(ArraySegment<byte> data, out Envelope envelope)
        {
            envelope = default(Envelope);
            if (data.Array == null || data.Count < EnvelopeSize)
                return false;

            var offset = data.Offset;
            var buffer = data.Array;
            if (buffer[offset] != EnvelopeMagic0 || buffer[offset + 1] != EnvelopeMagic1
                || buffer[offset + 2] != EnvelopeVersion)
            {
                return false;
            }

            var payloadLength = ReadUInt32(buffer, offset + 16);
            if (payloadLength > int.MaxValue || payloadLength != data.Count - EnvelopeSize)
                return false;

            envelope = new Envelope
            {
                Kind = buffer[offset + 3],
                FrameId = ReadUInt32(buffer, offset + 4),
                TotalLength = ReadUInt32(buffer, offset + 8),
                Offset = ReadUInt32(buffer, offset + 12),
                PayloadLength = (int)payloadLength,
            };
            return envelope.FrameId != 0 && envelope.TotalLength != 0;
        }

        private static bool TryReadAndValidateEnvelope(ArraySegment<byte> data, byte kind,
            out Envelope envelope)
        {
            if (!TryReadEnvelope(data, out envelope) || envelope.Kind != kind
                || envelope.Offset != 0 || envelope.TotalLength != envelope.PayloadLength)
            {
                envelope = default(Envelope);
                return false;
            }
            return true;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | buffer[offset + 1] << 8
                | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint NewCookie()
        {
            var bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            var cookie = BitConverter.ToUInt32(bytes, 0);
            return cookie == 0 ? 1u : cookie;
        }

        private uint ClockNow()
        {
            var elapsed = _clockStopwatch.Elapsed;
            var milliseconds = elapsed.Ticks / TimeSpan.TicksPerMillisecond;
            return unchecked((uint)milliseconds);
        }

        private uint MonotonicNow()
        {
            lock (_clockGate)
            {
                var value = _clock();
                if (_hasClock)
                {
                    var backwards = unchecked(_lastClock - value);
                    if (backwards != 0 && backwards < 0x80000000u)
                        value = _lastClock;
                }
                _lastClock = value;
                _hasClock = true;
                return value;
            }
        }

        private bool IsPumpThread()
        {
            return _pumpThreadId != 0
                && _pumpThreadId == Thread.CurrentThread.ManagedThreadId;
        }

        private void EnsureNotDisposed()
        {
            if (Volatile.Read(ref _disposed) || Volatile.Read(ref _disposeRequested))
                throw new ObjectDisposedException(nameof(KcpSessionManager));
        }

        private static KcpConfig ValidateConfig(KcpConfig config, int maxDatagramSize)
        {
            if (maxDatagramSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize));
            if (config.RecvBufferSize <= 0 || config.SendBufferSize <= 0
                || config.Mtu < KcpPeer.METADATA_SIZE + kcp2k.Kcp.MTU_MIN
                || config.Interval == 0 || config.FastResend < 0
                || config.SendWindowSize == 0 || config.ReceiveWindowSize < 2
                || config.Timeout <= 0 || config.MaxRetransmits == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config),
                    "KCP configuration contains an invalid buffer, MTU, window, interval, or timeout");
            }
            if (maxDatagramSize < config.Mtu)
                throw new ArgumentException("The datagram bearer is smaller than the KCP MTU",
                    nameof(maxDatagramSize));
            return config;
        }

        private static KcpConfig CopyConfig(KcpConfig config)
        {
            return new KcpConfig
            {
                DualMode = config.DualMode,
                RecvBufferSize = config.RecvBufferSize,
                SendBufferSize = config.SendBufferSize,
                Mtu = config.Mtu,
                NoDelay = config.NoDelay,
                Interval = config.Interval,
                FastResend = config.FastResend,
                CongestionWindow = config.CongestionWindow,
                SendWindowSize = config.SendWindowSize,
                ReceiveWindowSize = config.ReceiveWindowSize,
                Timeout = config.Timeout,
                MaxRetransmits = config.MaxRetransmits,
            };
        }

        private static void LogWarning(string message)
        {
            CoopPlugin.Log?.LogWarning(message);
        }

        private static void LogInfo(string message)
        {
            CoopPlugin.Log?.LogInfo(message);
        }

        private static void LogError(string message)
        {
            CoopPlugin.Log?.LogError(message);
        }

        private void FailApplicationSend(PeerConnection connection, INetMessage message,
            SessionState state, string reason)
        {
            var text = "KCP application send failed for peer " + (connection?.Id.ToString() ?? "<none>")
                + " message " + (message?.GetType().FullName ?? "<null>") + ": " + reason;
            LogError(text);
            if (state != null)
            {
                try
                {
                    GracefulDisconnect(connection, new DisconnectInfo(
                        "application send failed; session recovery required", false,
                        "send_failed", true, connection.State));
                }
                catch (Exception disconnectError)
                {
                    throw new InvalidOperationException(text + "; disconnect failed",
                        disconnectError);
                }
            }

            throw new InvalidOperationException(text);
        }

        private sealed class SessionState
        {
            internal readonly DatagramPeer DatagramPeer;
            internal readonly PeerConnection Connection;
            internal readonly Queue<OutboundFrame> Reliable = new Queue<OutboundFrame>();
            internal readonly Queue<OutboundFrame> Transient = new Queue<OutboundFrame>();
            internal readonly Queue<PendingCompactFrame> PendingCompact
                = new Queue<PendingCompactFrame>();
            internal KcpSession Session;
            internal byte[] Scratch;
            internal int FragmentPayloadLimit;
            internal bool Authenticated;
            internal bool MessageIdsActivated;
            internal bool Terminal;
            internal bool DisconnectRequested;
            internal bool DisconnectRequestQueued;
            internal DisconnectInfo DisconnectInfo;
            internal bool PendingTerminalDisconnect;
            internal uint TerminalDisconnectDeadline;
            internal bool PendingDisconnectCompletion;
            internal uint DisconnectCompletionDeadline;
            internal bool HasError;
            internal string LastErrorDetail;
            internal uint LastReceiveTime;
            internal uint HandshakeStarted;
            internal uint ReassemblyLastReceive;
            internal uint NextFrameId = 1;
            internal uint NextTransientFrameId = 1;
            internal uint NextInboundFrameId = 1;
            internal uint ReassemblyFrameId;
            internal uint ReassemblyTotal;
            internal uint ReassemblyOffset;
            internal int ReassemblyReservedBytes;
            internal byte[] Reassembly;
            internal int ReliableFrameCount;
            internal int ReliableBytes;
            internal int TransientFrameCount;
            internal int TransientBytes;
            internal OutboundFrame ActiveReliable;

            internal SessionState(DatagramPeer peer, int id)
            {
                DatagramPeer = peer;
                Connection = new PeerConnection(id);
            }
        }

        private sealed class OutboundFrame
        {
            internal readonly byte[] Frame;
            internal uint Id;
            internal int Offset;

            internal OutboundFrame(byte[] frame, uint id)
            {
                Frame = frame;
                Id = id;
            }
        }

        private sealed class PendingCompactFrame
        {
            internal readonly byte[] Frame;
            internal readonly bool Reliable;

            internal PendingCompactFrame(byte[] frame, bool reliable)
            {
                Frame = frame;
                Reliable = reliable;
            }
        }

        private struct QueueAdmission
        {
            internal readonly SessionState State;
            internal readonly byte[] Frame;
            internal readonly MessageDescriptor Descriptor;

            internal QueueAdmission(SessionState state, byte[] frame,
                MessageDescriptor descriptor)
            {
                State = state;
                Frame = frame;
                Descriptor = descriptor;
            }
        }

        private struct IncomingItem
        {
            internal readonly InMsg Message;
            internal readonly int EncodedBytes;

            internal IncomingItem(InMsg message, int encodedBytes)
            {
                Message = message;
                EncodedBytes = encodedBytes;
            }
        }

        /// <summary>One owned frame copy waiting to be JSON-decoded off the pump thread.</summary>
        private sealed class DecodeWork
        {
            internal byte[] Frame;
            internal SessionState State;
            internal bool Reliable;
            internal bool CompactIds;
            internal bool BeforeActivation;
            internal ProtocolSnapshot Snapshot;
            internal int FrameLimit;
            internal int EncodedBytes;
        }

        /// <summary>A decoded frame (or a decode failure) handed back to the pump thread.</summary>
        private struct DecodeResult
        {
            internal SessionState State;
            internal InMsg Message;
            internal bool Reliable;
            internal bool BeforeActivation;
            internal ProtocolSnapshot Snapshot;
            internal bool Ok;
            internal int EncodedBytes;
        }

        private struct PeerSignal
        {
            internal readonly DatagramPeer Peer;
            internal readonly bool Closed;
            internal readonly string Reason;

            internal PeerSignal(DatagramPeer peer, bool closed, string reason)
            {
                Peer = peer;
                Closed = closed;
                Reason = reason;
            }
        }

        private struct DisconnectRequest
        {
            internal readonly SessionState State;
            internal readonly DisconnectInfo Info;

            internal DisconnectRequest(SessionState state, DisconnectInfo info)
            {
                State = state;
                Info = info;
            }
        }

        private struct Envelope
        {
            internal byte Kind;
            internal uint FrameId;
            internal uint TotalLength;
            internal uint Offset;
            internal int PayloadLength;
        }
    }

    /// <summary>Bounded manager policy.  Values are validated before a session can start.</summary>
    public sealed class KcpSessionManagerOptions
    {
        public int MaxDatagramsPerPump = 256;
        public int MaxPendingPeerSignals = 256;
        public int MaxPeerSignalsPerPump = 64;
        public int MaxPendingDisconnectRequests = 256;
        public int MaxDisconnectRequestsPerPump = 64;
        public int MaxPendingDisconnectResults = 256;
        public int MaxDisconnectResultsPerPump = 64;
        public int MaxReliableFrames = 256;
        public int MaxReliableBytes = 32 * 1024 * 1024;
        public int MaxTransientFrames = 256;
        public int MaxTransientBytes = 8 * 1024 * 1024;
        public int MaxIncomingFrames = 10000;
        // This is encoded-frame ownership: the manager releases exactly this many bytes when
        // TryDequeueIncoming transfers the decoded message to Core.
        public int MaxIncomingBytes = 32 * 1024 * 1024;
        public int MaxKcpQueuedSegments = 256;
        public int MaxEncodedFrameBytes = 16 * 1024 * 1024;
        public int MaxPendingHandshakeSessions = 8;
        // Hello and Welcome carry the exact extensible message catalog in addition to game data.
        // Keep their ceiling equal to an ordinary frame while the aggregate budget still limits
        // concurrent allocations from peers that have not completed application admission.
        public int MaxHandshakeFrameBytes = 16 * 1024 * 1024;
        public int MaxReassemblyBytes = 32 * 1024 * 1024;
        public uint PendingHandshakeTimeoutMs = 10000;
        public uint ReassemblyTimeoutMs = 30000;
        public Func<uint> Clock;

        internal KcpSessionManagerOptions Copy()
        {
            return new KcpSessionManagerOptions
            {
                MaxDatagramsPerPump = MaxDatagramsPerPump,
                MaxPendingPeerSignals = MaxPendingPeerSignals,
                MaxPeerSignalsPerPump = MaxPeerSignalsPerPump,
                MaxPendingDisconnectRequests = MaxPendingDisconnectRequests,
                MaxDisconnectRequestsPerPump = MaxDisconnectRequestsPerPump,
                MaxPendingDisconnectResults = MaxPendingDisconnectResults,
                MaxDisconnectResultsPerPump = MaxDisconnectResultsPerPump,
                MaxReliableFrames = MaxReliableFrames,
                MaxReliableBytes = MaxReliableBytes,
                MaxTransientFrames = MaxTransientFrames,
                MaxTransientBytes = MaxTransientBytes,
                MaxIncomingFrames = MaxIncomingFrames,
                MaxIncomingBytes = MaxIncomingBytes,
                MaxKcpQueuedSegments = MaxKcpQueuedSegments,
                MaxEncodedFrameBytes = MaxEncodedFrameBytes,
                MaxPendingHandshakeSessions = MaxPendingHandshakeSessions,
                MaxHandshakeFrameBytes = MaxHandshakeFrameBytes,
                MaxReassemblyBytes = MaxReassemblyBytes,
                PendingHandshakeTimeoutMs = PendingHandshakeTimeoutMs,
                ReassemblyTimeoutMs = ReassemblyTimeoutMs,
                Clock = Clock,
            };
        }

        internal void Validate()
        {
            if (MaxDatagramsPerPump <= 0
                || MaxPendingPeerSignals <= 0 || MaxPeerSignalsPerPump <= 0
                || MaxPendingDisconnectRequests <= 0 || MaxDisconnectRequestsPerPump <= 0
                || MaxPendingDisconnectResults <= 0 || MaxDisconnectResultsPerPump <= 0
                || MaxReliableFrames <= 0
                || MaxReliableBytes <= 0 || MaxTransientFrames <= 0 || MaxTransientBytes <= 0
                || MaxIncomingFrames <= 0 || MaxIncomingBytes < Msg.MinimumFrameSize
                || MaxKcpQueuedSegments <= 0
                || MaxEncodedFrameBytes < Msg.MinimumFrameSize
                || MaxEncodedFrameBytes > Msg.MaxFrameSize + Msg.FrameHeaderSize
                || MaxPendingHandshakeSessions <= 0 || MaxPendingHandshakeSessions > 1024
                || MaxHandshakeFrameBytes < Msg.MinimumFrameSize
                || MaxHandshakeFrameBytes > MaxEncodedFrameBytes
                || MaxReassemblyBytes < MaxHandshakeFrameBytes
                || MaxReassemblyBytes < MaxEncodedFrameBytes
                || PendingHandshakeTimeoutMs == 0 || ReassemblyTimeoutMs == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(KcpSessionManagerOptions),
                    "KCP manager limits must be positive and within the protocol frame ceiling");
            }
        }
    }
}
