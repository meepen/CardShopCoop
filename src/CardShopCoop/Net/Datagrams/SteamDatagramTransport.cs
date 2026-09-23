using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Steamworks;

namespace CardShopCoop.Net.Datagrams
{
    /// <summary>
    /// Datagram transport backed by SteamNetworkingSockets P2P connections.
    ///
    /// Steamworks is initialized and its callbacks are pumped by the game. This class only
    /// registers its connection-status callback; it deliberately does not initialize Steam or
    /// call RunCallbacks.
    /// </summary>
    public sealed class SteamDatagramTransport : IDatagramTransport
    {
        public const int DefaultMaxDatagramSize = 1200;
        public const int DefaultMaxPendingStatusChanges = 256;
        public const int DefaultMaxPendingLifecycleChanges = 512;
        public const int DefaultMaxPendingChangesPerPoll = 64;

        private const int DefaultReceiveSlotCount = 64;
        private const int DefaultVirtualPort = 0;

        private readonly object _lifecycleGate = new object();
        private readonly object _nativeGate = new object();
        private readonly object _sendGate = new object();
        private readonly object _notificationGate = new object();
        private readonly object _pollGate = new object();

        private readonly bool _isHost;
        private readonly Func<ulong, bool> _hostAuthorization;
        private readonly ulong _expectedHostSteamId;
        private readonly int _virtualPort;
        private readonly int _maxDatagramSize;
        private readonly int _maxPendingStatusChanges;
        private readonly int _maxPendingLifecycleChanges;
        private readonly int _maxPendingChangesPerPoll;

        private readonly byte[] _receiveBuffer;
        private readonly IntPtr[] _receiveSlots;
        private readonly byte[] _sendBuffer;
        private GCHandle _sendBufferHandle;
        private IntPtr _sendBufferPointer;

        // Status callbacks are replaceable until a terminal status is observed. Keep only one
        // pending status per connection, while terminal statuses and our own lifecycle notices
        // remain FIFO records that cannot be evicted without making a disconnect disappear.
        private readonly Queue<PendingChange> _pendingLifecycleChanges =
            new Queue<PendingChange>();
        private readonly Dictionary<HSteamNetConnection, LinkedListNode<PendingChange>>
            _pendingStatusChanges =
                new Dictionary<HSteamNetConnection, LinkedListNode<PendingChange>>();
        private readonly LinkedList<PendingChange> _pendingStatusOrder =
            new LinkedList<PendingChange>();
        private readonly HashSet<HSteamNetConnection> _terminalStatusQueued =
            new HashSet<HSteamNetConnection>();
        private readonly Dictionary<HSteamNetConnection, PeerState> _peersByConnection =
            new Dictionary<HSteamNetConnection, PeerState>();
        private readonly Dictionary<long, PeerState> _peersByToken =
            new Dictionary<long, PeerState>();
        private readonly Dictionary<ulong, PeerState> _peersBySteamId =
            new Dictionary<ulong, PeerState>();
        private readonly HashSet<HSteamNetConnection> _acceptedConnections =
            new HashSet<HSteamNetConnection>();

        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetConnection _clientConnection = HSteamNetConnection.Invalid;
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;

        private long _nextPeerToken = 1;
        private long _generation;
        private int _pendingClosedNotifications;
        private bool _running;
        private bool _disposed;
        private bool _dispatchingNotification;
        private bool _polling;

        /// <summary>
        /// Creates a host or client without exposing Steamworks identity or handle types.
        /// Hosts must provide <paramref name="hostAuthorization"/>; it is called on the Poll
        /// thread for each incoming Steam identity while it is being authorized.
        /// </summary>
        /// <param name="isHost">Whether this instance owns a P2P listen socket.</param>
        /// <param name="hostAuthorization">
        /// Host-side lobby membership check. It receives the remote SteamID64. Pass null for a
        /// client.
        /// </param>
        /// <param name="expectedHostSteamId">
        /// Client-side expected host SteamID64. Pass zero for a host.
        /// </param>
        /// <param name="maxDatagramSize">Maximum packet payload, in bytes.</param>
        /// <param name="virtualPort">SteamNetworkingSockets P2P virtual port.</param>
        /// <param name="maxPendingStatusChanges">Maximum replaceable status changes retained per transport.</param>
        /// <param name="maxPendingLifecycleChanges">Maximum terminal and peer lifecycle changes retained.</param>
        /// <param name="maxPendingChangesPerPoll">Maximum lifecycle callbacks processed per Poll.</param>
        public SteamDatagramTransport(
            bool isHost,
            Func<ulong, bool> hostAuthorization,
            ulong expectedHostSteamId = 0,
            int maxDatagramSize = DefaultMaxDatagramSize,
            int virtualPort = DefaultVirtualPort,
            int maxPendingStatusChanges = DefaultMaxPendingStatusChanges,
            int maxPendingLifecycleChanges = DefaultMaxPendingLifecycleChanges,
            int maxPendingChangesPerPoll = DefaultMaxPendingChangesPerPoll)
        {
            if (maxDatagramSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize));
            }

            if (virtualPort < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(virtualPort));
            }

            if (maxPendingStatusChanges <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingStatusChanges));
            }

            if (maxPendingLifecycleChanges <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingLifecycleChanges));
            }

            if (maxPendingChangesPerPoll <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingChangesPerPoll));
            }

            if (isHost && hostAuthorization == null)
            {
                throw new ArgumentNullException(nameof(hostAuthorization),
                    "A host must authorize lobby members before accepting P2P connections.");
            }

            if (!isHost && expectedHostSteamId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedHostSteamId),
                    "A client must be given the expected host SteamID64.");
            }

            if (isHost && expectedHostSteamId != 0)
            {
                throw new ArgumentException(
                    "A host cannot have an expected client host identity.",
                    nameof(expectedHostSteamId));
            }

            _isHost = isHost;
            _hostAuthorization = hostAuthorization;
            _expectedHostSteamId = expectedHostSteamId;
            _virtualPort = virtualPort;
            _maxDatagramSize = maxDatagramSize;
            _maxPendingStatusChanges = maxPendingStatusChanges;
            _maxPendingLifecycleChanges = maxPendingLifecycleChanges;
            _maxPendingChangesPerPoll = maxPendingChangesPerPoll;

            _receiveBuffer = new byte[maxDatagramSize];
            _receiveSlots = new IntPtr[DefaultReceiveSlotCount];
            _sendBuffer = new byte[maxDatagramSize];
            _sendBufferHandle = GCHandle.Alloc(_sendBuffer, GCHandleType.Pinned);
            _sendBufferPointer = _sendBufferHandle.AddrOfPinnedObject();
        }

        /// <summary>Creates a host transport authorized by the supplied lobby-membership check.</summary>
        public static SteamDatagramTransport CreateHost(
            Func<ulong, bool> hostAuthorization,
            int maxDatagramSize = DefaultMaxDatagramSize,
            int virtualPort = DefaultVirtualPort,
            int maxPendingStatusChanges = DefaultMaxPendingStatusChanges,
            int maxPendingLifecycleChanges = DefaultMaxPendingLifecycleChanges,
            int maxPendingChangesPerPoll = DefaultMaxPendingChangesPerPoll)
        {
            return new SteamDatagramTransport(
                true,
                hostAuthorization,
                0,
                maxDatagramSize,
                virtualPort,
                maxPendingStatusChanges,
                maxPendingLifecycleChanges,
                maxPendingChangesPerPoll);
        }

        /// <summary>Creates a client transport that accepts only the supplied host SteamID64.</summary>
        public static SteamDatagramTransport CreateClient(
            ulong expectedHostSteamId,
            int maxDatagramSize = DefaultMaxDatagramSize,
            int virtualPort = DefaultVirtualPort,
            int maxPendingStatusChanges = DefaultMaxPendingStatusChanges,
            int maxPendingLifecycleChanges = DefaultMaxPendingLifecycleChanges,
            int maxPendingChangesPerPoll = DefaultMaxPendingChangesPerPoll)
        {
            return new SteamDatagramTransport(
                false,
                null,
                expectedHostSteamId,
                maxDatagramSize,
                virtualPort,
                maxPendingStatusChanges,
                maxPendingLifecycleChanges,
                maxPendingChangesPerPoll);
        }

        public int MaxDatagramSize => _maxDatagramSize;

        public bool IsRunning
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _running;
                }
            }
        }

        public event Action<DatagramPeer> PeerAvailable;
        public event Action<DatagramPeer, string> PeerClosed;

        internal int PendingStatusChangeCount
        {
            get
            {
                lock (_lifecycleGate)
                    return _pendingStatusChanges.Count;
            }
        }

        internal int PendingLifecycleChangeCount
        {
            get
            {
                lock (_lifecycleGate)
                    return _pendingLifecycleChanges.Count;
            }
        }

        public void Start()
        {
            Callback<SteamNetConnectionStatusChangedCallback_t> statusCallbackToDispose = null;
            ExceptionDispatchInfo startFailure = null;
            lock (_pollGate)
            {
                lock (_notificationGate)
                {
                    lock (_lifecycleGate)
                    {
                        ThrowIfDisposedLocked();
                        if (_running)
                        {
                            throw new InvalidOperationException(
                                "Steam datagram transport is already running.");
                        }

                        if (_pendingClosedNotifications != 0)
                        {
                            throw new InvalidOperationException(
                                "Poll must deliver all peer closures before the transport can restart.");
                        }

                        var generation = NextGenerationLocked();
                        DrainPendingChangesLocked();

                        _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(
                            change => OnConnectionStatusChanged(change, generation));

                        try
                        {
                            lock (_nativeGate)
                            {
                                _pollGroup = SteamNetworkingSockets.CreatePollGroup();
                                if (_pollGroup == HSteamNetPollGroup.Invalid)
                                {
                                    throw new InvalidOperationException(
                                        "SteamNetworkingSockets.CreatePollGroup failed.");
                                }

                                if (_isHost)
                                {
                                    _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
                                        _virtualPort,
                                        0,
                                        null);
                                    if (_listenSocket == HSteamListenSocket.Invalid)
                                    {
                                        throw new InvalidOperationException(
                                            "SteamNetworkingSockets.CreateListenSocketP2P failed.");
                                    }
                                }
                                else
                                {
                                    var identity = default(SteamNetworkingIdentity);
                                    identity.SetSteamID64(_expectedHostSteamId);
                                    _clientConnection = SteamNetworkingSockets.ConnectP2P(
                                        ref identity,
                                        _virtualPort,
                                        0,
                                        null);
                                    if (_clientConnection == HSteamNetConnection.Invalid)
                                    {
                                        throw new InvalidOperationException(
                                            "SteamNetworkingSockets.ConnectP2P failed.");
                                    }
                                }
                            }

                            _running = true;
                        }
                        catch (Exception error)
                        {
                            _running = false;
                            statusCallbackToDispose = _statusCallback;
                            _statusCallback = null;
                            lock (_nativeGate)
                            {
                                CloseNativeResourcesLocked("transport start failed");
                            }
                            startFailure = ExceptionDispatchInfo.Capture(error);
                        }
                    }
                }
            }

            statusCallbackToDispose?.Dispose();
            startFailure?.Throw();
        }

        public int Poll(int maxDatagrams, Action<DatagramPeer, ArraySegment<byte>> receive)
        {
            if (maxDatagrams < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDatagrams));
            }

            if (receive == null)
            {
                throw new ArgumentNullException(nameof(receive));
            }

            lock (_pollGate)
            {
                if (_polling)
                {
                    throw new InvalidOperationException(
                        "Steam datagram transport cannot be polled reentrantly.");
                }

                _polling = true;
                try
                {
                    lock (_lifecycleGate)
                    {
                        ThrowIfDisposedLocked();
                    }

                    var pendingChangeBudget = _maxPendingChangesPerPoll;
                    ProcessPendingChanges(ref pendingChangeBudget);

                    if (maxDatagrams == 0)
                    {
                        return 0;
                    }

                    int received;
                    long pollGeneration;
                    lock (_lifecycleGate)
                    {
                        if (!_running)
                        {
                            return 0;
                        }

                        pollGeneration = _generation;
                        // The slots are owned by this serialized Poll until every message is
                        // released. Payload bytes are copied only by ProcessReceivedMessages.
                        lock (_nativeGate)
                        {
                            var slotCount = Math.Min(maxDatagrams, _receiveSlots.Length);
                            received = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(
                                _pollGroup,
                                _receiveSlots,
                                slotCount);
                            if (received <= 0)
                            {
                                return 0;
                            }

                            received = Math.Min(received, slotCount);
                        }
                    }

                    var delivered = ProcessReceivedMessages(pollGeneration, received, receive);
                    ProcessPendingChanges(ref pendingChangeBudget);
                    return delivered;
                }
                finally
                {
                    _polling = false;
                }
            }
        }

        public void Send(DatagramPeer peer, ArraySegment<byte> datagram)
        {
            if (!IsValidDatagram(datagram) || datagram.Count > _maxDatagramSize)
            {
                throw new ArgumentException("Steam datagram is invalid or exceeds the configured limit",
                    nameof(datagram));
            }

            lock (_lifecycleGate)
            {
                if (_disposed || !_running || !_peersByToken.TryGetValue(peer.Token, out var state)
                    || !state.Available)
                {
                    throw new InvalidOperationException(
                        "Steam datagram send has no live peer " + peer.Token);
                }

                lock (_nativeGate)
                {
                    lock (_sendGate)
                    {
                        if (datagram.Count != 0)
                        {
                            Buffer.BlockCopy(
                                datagram.Array,
                                datagram.Offset,
                                _sendBuffer,
                                0,
                                datagram.Count);
                        }

                        long messageNumber = 0;
                        var result = SteamNetworkingSockets.SendMessageToConnection(
                            state.Connection,
                            _sendBufferPointer,
                            (uint)datagram.Count,
                            Constants.k_nSteamNetworkingSend_UnreliableNoNagle,
                            out messageNumber);
                        if (result != EResult.k_EResultOK)
                        {
                            var error = new InvalidOperationException(
                                "Steam datagram send failed for peer " + peer.Token + ": " + result);
                            CoopPlugin.Log?.LogError(error.Message);
                            throw error;
                        }
                    }
                }
            }
        }

        public void Close(DatagramPeer peer, string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "closed by transport" : reason;

            lock (_lifecycleGate)
            {
                ThrowIfDisposedLocked();
                if (!_running || !_peersByToken.TryGetValue(peer.Token, out var state)
                    || !state.Available)
                {
                    return;
                }

                try
                {
                    ClosePeerLocked(state, _generation, reason);
                }
                catch
                {
                    // Queue overflow is fatal, but it must not leave the native connection open
                    // after the caller has been told that close admission failed.
                    RemovePeerLocked(state);
                    lock (_nativeGate)
                    {
                        SteamNetworkingSockets.CloseConnection(
                            state.Connection,
                            0,
                            reason,
                            false);
                    }
                    throw;
                }

                lock (_nativeGate)
                {
                    SteamNetworkingSockets.CloseConnection(
                        state.Connection,
                        0,
                        reason,
                        false);
                }
            }
        }

        public void Stop()
        {
            lock (_pollGate)
            {
                lock (_notificationGate)
                {
                    Callback<SteamNetConnectionStatusChangedCallback_t> statusCallback = null;
                    lock (_lifecycleGate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        if (_running)
                        {
                            statusCallback = StopLocked("transport stopped");
                        }
                    }

                    // Unregister after leaving the lifecycle lock.  A callback already entering
                    // OnConnectionStatusChanged only needs that lock to observe the stopped state.
                    statusCallback?.Dispose();

                    // Stop is a lifecycle boundary.  Drain its captured closures before a caller
                    // can restart, while still keeping every handler outside lifecycle/native
                    // locks.
                    if (!_dispatchingNotification)
                    {
                        DrainPendingChangesToCompletion();
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_pollGate)
            {
                lock (_notificationGate)
                {
                    Callback<SteamNetConnectionStatusChangedCallback_t> statusCallback = null;
                    lock (_lifecycleGate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        if (_running)
                        {
                            statusCallback = StopLocked("transport disposed");
                        }

                        _disposed = true;
                        lock (_sendGate)
                        {
                            if (_sendBufferHandle.IsAllocated)
                            {
                                _sendBufferHandle.Free();
                                _sendBufferPointer = IntPtr.Zero;
                            }
                        }
                    }

                    statusCallback?.Dispose();

                    // No future Poll is possible after Dispose.  Deliver the already-captured
                    // close notifications now, outside both lifecycle and native locks.
                    if (!_dispatchingNotification)
                    {
                        DrainPendingChangesToCompletion();
                    }
                }
            }
        }

        private void OnConnectionStatusChanged(
            SteamNetConnectionStatusChangedCallback_t change,
            long callbackGeneration)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || !_running || callbackGeneration != _generation)
                {
                    return;
                }
                var info = change.m_info;
                try
                {
                    QueueStatusChangeLocked(PendingChange.Status(
                        callbackGeneration,
                        change.m_hConn,
                        info.m_identityRemote,
                        info.m_eState,
                        info.m_eEndReason,
                        info.m_szEndDebug));
                }
                catch
                {
                    CloseConnectionLocked(change.m_hConn, "Steam status queue overflow");
                    throw;
                }
            }
        }

        private void ProcessPendingChanges(ref int budget)
        {
            while (budget > 0 && TryTakePendingChange(out var change))
            {
                budget--;
                if (change.Kind == PendingChangeKind.Closed)
                {
                    ProcessClosedNotification(change);
                    continue;
                }

                if (change.Kind == PendingChangeKind.Available)
                {
                    ProcessAvailableNotification(change);
                    continue;
                }

                ProcessStatusChange(change);
            }
        }

        private bool TryTakePendingChange(out PendingChange change)
        {
            lock (_lifecycleGate)
            {
                if (_pendingLifecycleChanges.Count != 0)
                {
                    change = _pendingLifecycleChanges.Dequeue();
                    if (change.Kind == PendingChangeKind.Status
                        && IsTerminalState(change.State))
                    {
                        _terminalStatusQueued.Remove(change.Connection);
                    }
                    return true;
                }

                if (_pendingStatusOrder.First != null)
                {
                    var node = _pendingStatusOrder.First;
                    _pendingStatusOrder.RemoveFirst();
                    change = node.Value;
                    _pendingStatusChanges.Remove(change.Connection);
                    return true;
                }
            }

            change = default(PendingChange);
            return false;
        }

        private void QueueStatusChangeLocked(PendingChange change)
        {
            if (IsTerminalState(change.State))
            {
                if (_terminalStatusQueued.Contains(change.Connection))
                {
                    // Repeated terminal callbacks describe the same native closure. One queued
                    // terminal record is sufficient and, unlike a replaceable state, is never
                    // discarded before it is processed.
                    return;
                }

                // A terminal callback is never replaced or discarded. Replacing an older
                // non-terminal status is safe because the terminal transition supersedes it.
                if (_pendingLifecycleChanges.Count >= _maxPendingLifecycleChanges)
                {
                    throw PendingChangeOverflow("terminal Steam connection status");
                }

                if (_pendingStatusChanges.TryGetValue(change.Connection, out var oldStatus))
                {
                    _pendingStatusOrder.Remove(oldStatus);
                    _pendingStatusChanges.Remove(change.Connection);
                }
                _terminalStatusQueued.Add(change.Connection);
                _pendingLifecycleChanges.Enqueue(change);
                return;
            }

            if (_terminalStatusQueued.Contains(change.Connection))
            {
                // Steam may report a stale non-terminal state after the terminal state. The
                // terminal record is authoritative and must be delivered first.
                return;
            }

            if (_pendingStatusChanges.TryGetValue(change.Connection, out var pendingStatus))
            {
                pendingStatus.Value = change;
                return;
            }

            if (_pendingStatusChanges.Count >= _maxPendingStatusChanges)
            {
                throw PendingChangeOverflow("replaceable Steam connection status");
            }

            var node = _pendingStatusOrder.AddLast(change);
            _pendingStatusChanges.Add(change.Connection, node);
        }

        private static InvalidOperationException PendingChangeOverflow(string kind)
        {
            var error = new InvalidOperationException(
                "Steam datagram pending " + kind + " queue overflowed; transport cannot safely continue.");
            CoopPlugin.Log?.LogError(error.Message);
            return error;
        }

        private void ProcessAvailableNotification(PendingChange change)
        {
            Action<DatagramPeer> handler;
            lock (_notificationGate)
            {
                lock (_lifecycleGate)
                {
                    if (_disposed || !_running || change.Generation != _generation
                        || !_peersByToken.TryGetValue(change.Peer.Token, out var state)
                        || !state.Available || state.Published)
                    {
                        return;
                    }

                    // Mark this before invoking user code.  A reentrant Close/Stop must produce
                    // the matching close exactly once even if the handler changes lifecycle.
                    state.Published = true;
                    handler = PeerAvailable;
                }

                // Deliberately no lifecycle/native lock is held across external code.
                _dispatchingNotification = true;
                try
                {
                    handler?.Invoke(change.Peer);
                }
                finally
                {
                    _dispatchingNotification = false;
                }
            }
        }

        private void ProcessClosedNotification(PendingChange change)
        {
            Action<DatagramPeer, string> handler;
            lock (_notificationGate)
            {
                lock (_lifecycleGate)
                {
                    if (_pendingClosedNotifications <= 0)
                    {
                        throw new InvalidOperationException(
                            "Steam datagram close notification accounting was corrupted.");
                    }

                    // Keep the restart barrier set until this notification is selected.  Start
                    // takes _notificationGate first, so it cannot cross this delivery and later
                    // expose an old-generation close to a new session.
                    _pendingClosedNotifications--;
                    handler = PeerClosed;
                }

                // Closed notifications intentionally remain deliverable after Dispose: Dispose
                // itself drains this queue because no later Poll can be required.
                _dispatchingNotification = true;
                try
                {
                    handler?.Invoke(change.Peer, change.Reason);
                }
                finally
                {
                    _dispatchingNotification = false;
                }
            }
        }

        private void ProcessStatusChange(PendingChange change)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || !_running || change.Generation != _generation)
                {
                    return;
                }
            }

            if (change.State == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
            {
                ProcessConnecting(change);
                return;
            }

            if (change.State == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                ProcessConnected(change);
                return;
            }

            if (!IsTerminalState(change.State))
            {
                return;
            }

            lock (_lifecycleGate)
            {
                if (_disposed || !_running || change.Generation != _generation)
                {
                    return;
                }

                if (_peersByConnection.TryGetValue(change.Connection, out var state))
                {
                    ClosePeerLocked(state, change.Generation, GetCloseReason(change));
                }
                _acceptedConnections.Remove(change.Connection);
                if (_clientConnection == change.Connection)
                {
                    _clientConnection = HSteamNetConnection.Invalid;
                }

            }
        }

        private void ProcessConnecting(PendingChange change)
        {
            if (!TryGetSteamId(change.RemoteIdentity, out var remoteSteamId))
            {
                RejectConnection(
                    change.Generation,
                    change.Connection,
                    "remote Steam identity is invalid");
                return;
            }

            if (!_isHost)
            {
                lock (_lifecycleGate)
                {
                    if (_disposed || !_running || change.Generation != _generation)
                    {
                        return;
                    }

                    if (remoteSteamId != _expectedHostSteamId
                        || change.Connection != _clientConnection)
                    {
                        CloseConnectionLocked(
                            change.Connection,
                            "remote Steam identity is not the expected host connection");
                    }
                }

                return;
            }

            // Authorization is intentionally evaluated while processing the queued change, not
            // from Steam's callback. Lobby APIs and provider delegates therefore run from Poll.
            if (!_hostAuthorization(remoteSteamId))
            {
                RejectConnection(
                    change.Generation,
                    change.Connection,
                    "remote Steam identity is not a lobby member");
                return;
            }

            lock (_lifecycleGate)
            {
                if (_disposed || !_running || change.Generation != _generation)
                {
                    return;
                }

                if (_acceptedConnections.Contains(change.Connection))
                {
                    return;
                }

                EResult result;
                lock (_nativeGate)
                {
                    result = SteamNetworkingSockets.AcceptConnection(change.Connection);
                }
                if (result == EResult.k_EResultOK)
                {
                    _acceptedConnections.Add(change.Connection);
                }
                else
                {
                    CloseConnectionLocked(change.Connection, "Steam rejected connection acceptance");
                }
            }
        }

        private void ProcessConnected(PendingChange change)
        {
            if (!TryGetSteamId(change.RemoteIdentity, out var remoteSteamId))
            {
                RejectConnection(
                    change.Generation,
                    change.Connection,
                    "remote Steam identity is invalid");
                return;
            }

            lock (_lifecycleGate)
            {
                if (_disposed || !_running || change.Generation != _generation)
                {
                    return;
                }

                if (!_isHost && (remoteSteamId != _expectedHostSteamId
                    || change.Connection != _clientConnection))
                {
                    CloseConnectionLocked(change.Connection, "connected peer is not the expected host");
                    return;
                }

                if (_isHost && !_acceptedConnections.Contains(change.Connection))
                {
                    CloseConnectionLocked(change.Connection, "connection was not accepted");
                    return;
                }

                if (_peersByConnection.ContainsKey(change.Connection))
                {
                    return;
                }

                if (_peersBySteamId.ContainsKey(remoteSteamId))
                {
                    CloseConnectionLocked(change.Connection, "Steam identity already has a connection");
                    return;
                }

                bool pollGroupAssigned;
                lock (_nativeGate)
                {
                    pollGroupAssigned = SteamNetworkingSockets.SetConnectionPollGroup(
                        change.Connection,
                        _pollGroup);
                }

                if (!pollGroupAssigned)
                {
                    CloseConnectionLocked(change.Connection, "failed to assign Steam poll group");
                    return;
                }

                if (_nextPeerToken == long.MaxValue)
                {
                    CloseConnectionLocked(change.Connection, "peer token space exhausted");
                    return;
                }

                var peer = new DatagramPeer(_nextPeerToken++);
                var state = new PeerState(change.Connection, remoteSteamId, peer);
                _peersByConnection.Add(change.Connection, state);
                _peersByToken.Add(peer.Token, state);
                _peersBySteamId.Add(remoteSteamId, state);
                _acceptedConnections.Remove(change.Connection);
                state.Available = true;
                try
                {
                    QueueLifecycleChangeLocked(PendingChange.Available(_generation, peer),
                        "available peer notification");
                }
                catch
                {
                    RemovePeerLocked(state);
                    CloseConnectionLocked(change.Connection, "available peer notification overflow");
                    throw;
                }
            }
        }

        private void RejectConnection(long generation, HSteamNetConnection connection, string reason)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || !_running || generation != _generation)
                {
                    return;
                }

                CloseConnectionLocked(connection, reason);
            }
        }

        private int ProcessReceivedMessages(
            long generation,
            int received,
            Action<DatagramPeer, ArraySegment<byte>> receive)
        {
            var delivered = 0;
            var index = 0;
            try
            {
                for (; index < received; index++)
                {
                    var nativeMessage = _receiveSlots[index];
                    if (nativeMessage == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(nativeMessage);
                        if (message.m_cbSize < 0 || message.m_cbSize > _maxDatagramSize)
                        {
                            CloseMalformedConnection(
                                generation,
                                message.m_conn,
                                "Steam datagram exceeds MaxDatagramSize");
                            continue;
                        }

                        if (message.m_cbSize != 0 && message.m_pData == IntPtr.Zero)
                        {
                            CloseMalformedConnection(
                                generation,
                                message.m_conn,
                                "Steam datagram has no native payload");
                            continue;
                        }

                        PeerState state;
                        lock (_lifecycleGate)
                        {
                            if (!_running || generation != _generation
                                || !_peersByConnection.TryGetValue(message.m_conn, out state)
                                || !state.Available)
                            {
                                continue;
                            }
                        }

                        // This is the sole native-to-managed payload copy. The callback is
                        // synchronous and the segment is valid only until it returns.
                        if (message.m_cbSize != 0)
                        {
                            Marshal.Copy(
                                message.m_pData,
                                _receiveBuffer,
                                0,
                                message.m_cbSize);
                        }

                        // Receive callbacks are external code.  In particular, they may close a
                        // peer or stop the transport, so do not hold either lifecycle/native lock
                        // while they execute.
                        lock (_lifecycleGate)
                        {
                            if (!_running || generation != _generation
                                || !_peersByConnection.TryGetValue(message.m_conn, out state)
                                || !state.Available)
                            {
                                continue;
                            }
                        }

                        receive(
                            state.Peer,
                            new ArraySegment<byte>(_receiveBuffer, 0, message.m_cbSize));
                        delivered++;
                    }
                    finally
                    {
                        ReleaseNativeMessage(nativeMessage);
                        _receiveSlots[index] = IntPtr.Zero;
                    }
                }
            }
            catch
            {
                // Release every pointer returned by the native call even when a consumer
                // callback fails. The original exception is deliberately allowed to propagate.
                for (var remaining = index + 1; remaining < received; remaining++)
                {
                    var nativeMessage = _receiveSlots[remaining];
                    if (nativeMessage != IntPtr.Zero)
                    {
                        ReleaseNativeMessage(nativeMessage);
                        _receiveSlots[remaining] = IntPtr.Zero;
                    }
                }

                throw;
            }

            return delivered;
        }

        private void ReleaseNativeMessage(IntPtr nativeMessage)
        {
            if (nativeMessage == IntPtr.Zero)
            {
                return;
            }

            lock (_nativeGate)
            {
                SteamNetworkingMessage_t.Release(nativeMessage);
            }
        }

        private void CloseMalformedConnection(long generation, HSteamNetConnection connection, string reason)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || !_running || generation != _generation)
                {
                    return;
                }

                if (_peersByConnection.TryGetValue(connection, out var state))
                {
                    try
                    {
                        ClosePeerLocked(state, _generation, reason);
                    }
                    catch
                    {
                        RemovePeerLocked(state);
                        CloseConnectionLocked(connection, reason);
                        throw;
                    }
                }

                CloseConnectionLocked(connection, reason);
            }
        }

        private void CloseNativeResourcesLocked(string reason)
        {
            foreach (var state in _peersByConnection.Values)
            {
                CloseConnectionLocked(state.Connection, reason);
            }

            foreach (var connection in _acceptedConnections)
            {
                if (!_peersByConnection.ContainsKey(connection))
                {
                    CloseConnectionLocked(connection, reason);
                }
            }

            if (_clientConnection != HSteamNetConnection.Invalid
                && !_peersByConnection.ContainsKey(_clientConnection))
            {
                CloseConnectionLocked(_clientConnection, reason);
            }

            _clientConnection = HSteamNetConnection.Invalid;

            _acceptedConnections.Clear();

            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                _listenSocket = HSteamListenSocket.Invalid;
            }

            if (_pollGroup != HSteamNetPollGroup.Invalid)
            {
                SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
                _pollGroup = HSteamNetPollGroup.Invalid;
            }
        }

        private Callback<SteamNetConnectionStatusChangedCallback_t> StopLocked(string reason)
        {
            var connections = new List<PeerState>(_peersByConnection.Values);
            var publishedClosures = 0;
            for (var i = 0; i < connections.Count; i++)
            {
                if (connections[i].Available && connections[i].Published)
                    publishedClosures++;
            }
            if (_pendingLifecycleChanges.Count + publishedClosures > _maxPendingLifecycleChanges)
                throw PendingChangeOverflow("transport-stop peer closures");

            var stoppedGeneration = NextGenerationLocked();
            _running = false;
            var statusCallback = _statusCallback;
            _statusCallback = null;

            // Callback changes from the old generation must not be allowed to race with the
            // lifecycle closure notifications. A late callback still carries the old generation
            // and is discarded by ProcessPendingChanges. Existing close notifications are kept;
            // they are owned by the stopped session and are delivered before a restart.
            foreach (var state in connections)
            {
                if (state.Available)
                {
                    var published = state.Published;
                    state.Available = false;
                    if (published)
                    {
                        QueueClosedLocked(stoppedGeneration, state.Peer, reason);
                    }
                }
            }

            lock (_nativeGate)
            {
                CloseNativeResourcesLocked(reason);
            }

            _peersByConnection.Clear();
            _peersByToken.Clear();
            _peersBySteamId.Clear();
            _acceptedConnections.Clear();
            return statusCallback;
        }

        private void CloseConnectionLocked(HSteamNetConnection connection, string reason)
        {
            if (connection != HSteamNetConnection.Invalid)
            {
                lock (_nativeGate)
                {
                    SteamNetworkingSockets.CloseConnection(connection, 0, reason, false);
                }
            }
        }

        private void RemovePeerLocked(PeerState state)
        {
            _peersByConnection.Remove(state.Connection);
            _peersByToken.Remove(state.Peer.Token);
            _peersBySteamId.Remove(state.SteamId);
            _acceptedConnections.Remove(state.Connection);
            state.Available = false;
        }

        private void ClosePeerLocked(PeerState state, long generation, string reason)
        {
            if (!state.Available)
            {
                return;
            }

            var published = state.Published;
            if (published)
            {
                QueueClosedLocked(generation, state.Peer, reason);
            }
            RemovePeerLocked(state);
        }

        private void QueueClosedLocked(long generation, DatagramPeer peer, string reason)
        {
            QueueLifecycleChangeLocked(PendingChange.Closed(generation, peer, reason),
                "terminal peer notification");
            _pendingClosedNotifications++;
        }

        private void QueueLifecycleChangeLocked(PendingChange change, string kind)
        {
            if (_pendingLifecycleChanges.Count >= _maxPendingLifecycleChanges)
            {
                throw PendingChangeOverflow(kind);
            }

            _pendingLifecycleChanges.Enqueue(change);
        }

        private long NextGenerationLocked()
        {
            _generation++;
            return _generation;
        }

        private void DrainPendingChangesLocked()
        {
            _pendingLifecycleChanges.Clear();
            _pendingStatusChanges.Clear();
            _pendingStatusOrder.Clear();
            _terminalStatusQueued.Clear();
        }

        private void DrainPendingChangesToCompletion()
        {
            var budget = int.MaxValue;
            ProcessPendingChanges(ref budget);
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SteamDatagramTransport));
            }
        }

        private static bool TryGetSteamId(SteamNetworkingIdentity identity, out ulong steamId)
        {
            if (identity.m_eType != ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID)
            {
                steamId = 0;
                return false;
            }

            steamId = identity.GetSteamID64();
            return steamId != 0;
        }

        private static bool IsTerminalState(ESteamNetworkingConnectionState state)
        {
            return state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Dead;
        }

        private static string GetCloseReason(PendingChange change)
        {
            if (!string.IsNullOrEmpty(change.EndDebug))
            {
                return change.EndDebug;
            }

            return "Steam connection closed (" + change.State + ", reason "
                + change.EndReason + ")";
        }

        private static bool IsValidDatagram(ArraySegment<byte> datagram)
        {
            if (datagram.Count < 0 || datagram.Offset < 0)
            {
                return false;
            }

            if (datagram.Array == null)
            {
                return datagram.Count == 0 && datagram.Offset == 0;
            }

            return datagram.Offset <= datagram.Array.Length - datagram.Count;
        }

        private sealed class PeerState
        {
            public PeerState(HSteamNetConnection connection, ulong steamId, DatagramPeer peer)
            {
                Connection = connection;
                SteamId = steamId;
                Peer = peer;
            }

            public readonly HSteamNetConnection Connection;
            public readonly ulong SteamId;
            public readonly DatagramPeer Peer;
            public bool Available;
            public bool Published;
        }

        private enum PendingChangeKind
        {
            Status,
            Available,
            Closed,
        }

        private readonly struct PendingChange
        {
            private PendingChange(
                PendingChangeKind kind,
                long generation,
                HSteamNetConnection connection,
                SteamNetworkingIdentity remoteIdentity,
                ESteamNetworkingConnectionState state,
                int endReason,
                string endDebug,
                DatagramPeer peer,
                string reason)
            {
                Kind = kind;
                Generation = generation;
                Connection = connection;
                RemoteIdentity = remoteIdentity;
                State = state;
                EndReason = endReason;
                EndDebug = endDebug;
                Peer = peer;
                Reason = reason;
            }

            public readonly PendingChangeKind Kind;
            public readonly long Generation;
            public readonly HSteamNetConnection Connection;
            public readonly SteamNetworkingIdentity RemoteIdentity;
            public readonly ESteamNetworkingConnectionState State;
            public readonly int EndReason;
            public readonly string EndDebug;
            public readonly DatagramPeer Peer;
            public readonly string Reason;

            public static PendingChange Status(
                long generation,
                HSteamNetConnection connection,
                SteamNetworkingIdentity remoteIdentity,
                ESteamNetworkingConnectionState state,
                int endReason,
                string endDebug)
            {
                return new PendingChange(
                    PendingChangeKind.Status,
                    generation,
                    connection,
                    remoteIdentity,
                    state,
                    endReason,
                    endDebug,
                    default(DatagramPeer),
                    null);
            }

            public static PendingChange Closed(long generation, DatagramPeer peer, string reason)
            {
                return new PendingChange(
                    PendingChangeKind.Closed,
                    generation,
                    HSteamNetConnection.Invalid,
                    default(SteamNetworkingIdentity),
                    ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None,
                    0,
                    null,
                    peer,
                    reason);
            }

            public static PendingChange Available(long generation, DatagramPeer peer)
            {
                return new PendingChange(
                    PendingChangeKind.Available,
                    generation,
                    HSteamNetConnection.Invalid,
                    default(SteamNetworkingIdentity),
                    ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None,
                    0,
                    null,
                    peer,
                    null);
            }
        }
    }
}
