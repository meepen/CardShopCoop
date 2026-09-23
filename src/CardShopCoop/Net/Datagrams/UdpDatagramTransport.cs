using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CardShopCoop.Net.Datagrams
{
    /// <summary>
    /// A small, main-thread-pumped UDP transport.  This class deliberately knows nothing about
    /// the protocol carried in a datagram; framing, authentication, and retransmission belong to
    /// the layer above it.
    /// </summary>
    public sealed class UdpDatagramTransport : IDatagramTransport
    {
        public const int DefaultMaxDatagramSize = 1200;

        // 65507 is the largest UDP payload which is valid for IPv4.  Keeping the same ceiling for
        // both address families makes the contract independent of which socket the caller uses.
        private const int MaximumDatagramSize = 65507;

        // SIO_UDP_CONNRESET is not exposed by the net472 IOControlCode enum.  This is the
        // Windows Winsock _WSAIOW(IOC_VENDOR, 12) value documented for that control code.
        private const long WindowsUdpConnResetIoControlCode = 0x9800000CL;

        private readonly object _gate = new object();
        private readonly bool _isHost;
        private readonly IPEndPoint _bindEndpoint;
        private readonly IPEndPoint _remoteEndpoint;
        private readonly int _maxDatagramSize;

        // Receive happens on a dedicated background thread so the socket syscall never lands on
        // the Unity frame pump. Some environments (e.g. sandboxes that intercept Winsock) charge
        // well over 100 us per ReceiveFrom; Poll only drains this queue on the caller's thread.
        private const int ReceivePollMicroseconds = 20_000;
        private const int MaxQueuedDatagrams = 8192;
        private const int MaxQueuedBytes = 8 * 1024 * 1024;

        private readonly ConcurrentQueue<ReceivedDatagram> _received
            = new ConcurrentQueue<ReceivedDatagram>();
        private readonly byte[] _workerBuffer;
        private Thread _receiveThread;
        private bool _receiveStop;
        private int _receivedBytes;

        private readonly Dictionary<IPEndPoint, PeerState> _peersByEndpoint =
            new Dictionary<IPEndPoint, PeerState>();
        private readonly Dictionary<DatagramPeer, PeerState> _peersByHandle =
            new Dictionary<DatagramPeer, PeerState>();
        private readonly List<PeerState> _pendingClosed = new List<PeerState>();

        private Socket _socket;
        private bool _running;
        private bool _disposed;
        private long _nextToken = 1;
        private int _pendingClosedIndex;

        /// <summary>
        /// Creates a host transport.  The socket is created by <see cref="Start"/>, not by this
        /// constructor, so callers can subscribe to events before any lifecycle event occurs.
        /// Port zero is allowed and asks the operating system to choose a port.
        /// </summary>
        public UdpDatagramTransport(IPEndPoint bindEndpoint, int maxDatagramSize = DefaultMaxDatagramSize)
        {
            ValidateMaxDatagramSize(maxDatagramSize);
            _isHost = true;
            _bindEndpoint = CloneAndValidateEndpoint(bindEndpoint, nameof(bindEndpoint), allowPortZero: true);
            _maxDatagramSize = maxDatagramSize;
            _workerBuffer = new byte[maxDatagramSize + 1];
        }

        /// <summary>
        /// Creates a client transport.  The local endpoint may be null, in which case an
        /// ephemeral wildcard endpoint in the remote endpoint's address family is used.
        /// </summary>
        public UdpDatagramTransport(
            IPEndPoint localBindEndpoint,
            IPEndPoint remoteEndpoint,
            int maxDatagramSize = DefaultMaxDatagramSize)
        {
            ValidateMaxDatagramSize(maxDatagramSize);
            _isHost = false;
            _remoteEndpoint = CloneAndValidateEndpoint(remoteEndpoint, nameof(remoteEndpoint), allowPortZero: false);
            _bindEndpoint = CloneAndValidateLocalEndpoint(localBindEndpoint, _remoteEndpoint.AddressFamily);
            _maxDatagramSize = maxDatagramSize;
            _workerBuffer = new byte[maxDatagramSize + 1];
        }

        /// <summary>Creates a stopped host transport bound to <paramref name="bindEndpoint"/>.</summary>
        public static UdpDatagramTransport CreateHost(
            IPEndPoint bindEndpoint,
            int maxDatagramSize = DefaultMaxDatagramSize)
        {
            return new UdpDatagramTransport(bindEndpoint, maxDatagramSize);
        }

        /// <summary>Creates a stopped host transport bound to all IPv4 interfaces.</summary>
        public static UdpDatagramTransport CreateHost(
            int port,
            int maxDatagramSize = DefaultMaxDatagramSize)
        {
            return CreateHost(new IPEndPoint(IPAddress.Any, port), maxDatagramSize);
        }

        /// <summary>
        /// Creates a stopped client transport.  It binds an ephemeral local port in the remote
        /// endpoint's address family.
        /// </summary>
        public static UdpDatagramTransport CreateClient(
            IPEndPoint remoteEndpoint,
            int maxDatagramSize = DefaultMaxDatagramSize)
        {
            return new UdpDatagramTransport(null, remoteEndpoint, maxDatagramSize);
        }

        /// <summary>Creates a stopped client with an explicit local bind endpoint.</summary>
        public static UdpDatagramTransport CreateClient(
            IPEndPoint remoteEndpoint,
            IPEndPoint localBindEndpoint,
            int maxDatagramSize = DefaultMaxDatagramSize)
        {
            return new UdpDatagramTransport(localBindEndpoint, remoteEndpoint, maxDatagramSize);
        }

        public int MaxDatagramSize => _maxDatagramSize;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _running;
                }
            }
        }

        public event Action<DatagramPeer> PeerAvailable;
        public event Action<DatagramPeer, string> PeerClosed;

        /// <summary>
        /// Starts the nonblocking socket.  Calling Start while already running is a no-op.
        /// </summary>
        public void Start()
        {
            DrainPendingClosed();

            PeerState clientPeer = null;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_running)
                {
                    return;
                }

                var socket = CreateSocket(_bindEndpoint.AddressFamily);
                try
                {
                    socket.Bind(_bindEndpoint);
                }
                catch
                {
                    CloseSocket(socket);
                    throw;
                }

                _socket = socket;
                _running = true;

                if (!_isHost)
                {
                    clientPeer = AddPeerLocked(_remoteEndpoint);
                }
            }

            // The client has one configured peer regardless of whether a datagram has arrived.
            // Publish this outside the lock so event handlers can call back into the transport.
            if (clientPeer != null)
            {
                RaisePeerAvailable(clientPeer);
            }

            StartReceiveThread();
        }

        /// <summary>
        /// Receives at most <paramref name="maxDatagrams"/> datagrams.  Both the availability
        /// event and the receive callback execute synchronously on the caller's thread.
        /// </summary>
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

            DrainPendingClosed();
            if (maxDatagrams == 0)
            {
                return 0;
            }

            var delivered = 0;
            while (delivered < maxDatagrams && _received.TryDequeue(out var datagram))
            {
                Interlocked.Add(ref _receivedBytes, -datagram.Bytes.Length);

                Socket socket;
                PeerState peer;
                var isNewPeer = false;
                lock (_gate)
                {
                    if (!_running || _socket == null)
                    {
                        break;
                    }

                    socket = _socket;
                    if (_isHost)
                    {
                        if (!_peersByEndpoint.TryGetValue(datagram.Endpoint, out peer))
                        {
                            peer = AddPeerLocked(datagram.Endpoint);
                            isNewPeer = true;
                        }
                    }
                    else
                    {
                        // A client is intentionally restricted to the endpoint configured by its
                        // owner.  This is endpoint routing, not the protocol's anti-spoof cookie.
                        if (!_remoteEndpoint.Equals(datagram.Endpoint)
                            || !_peersByEndpoint.TryGetValue(_remoteEndpoint, out peer))
                        {
                            continue;
                        }
                    }
                }

                if (isNewPeer)
                {
                    RaisePeerAvailable(peer);
                }

                lock (_gate)
                {
                    // An availability handler may close the peer or stop the transport.  In
                    // either case this packet must not be delivered to a closed peer.
                    if (!_running
                        || !ReferenceEquals(socket, _socket)
                        || !_peersByHandle.ContainsKey(peer.Handle))
                    {
                        continue;
                    }
                }

                // datagram.Bytes is owned by the queue and stays valid for the callback.
                receive(peer.Handle, new ArraySegment<byte>(datagram.Bytes));
                delivered++;
            }

            DrainPendingClosed();
            return delivered;
        }

        /// <summary>
        /// Sends one complete datagram without retaining the caller's buffer.  UDP send errors
        /// fault the transport, but their PeerClosed events are deferred until Poll so a caller
        /// using Send from a worker cannot cause module callbacks off-thread.
        /// </summary>
        public void Send(DatagramPeer peer, ArraySegment<byte> datagram)
        {
            if (!IsValidSegment(datagram) || datagram.Count > _maxDatagramSize)
            {
                throw new ArgumentException("UDP datagram is invalid or exceeds the configured limit",
                    nameof(datagram));
            }

            Socket socket;
            PeerState state;
            lock (_gate)
            {
                if (!_running
                    || _socket == null
                    || !_peersByHandle.TryGetValue(peer, out state))
                {
                    throw new InvalidOperationException("UDP datagram send has no live peer " + peer);
                }

                socket = _socket;
            }

            try
            {
                var sent = socket.SendTo(
                    datagram.Array,
                    datagram.Offset,
                    datagram.Count,
                    SocketFlags.None,
                    state.Endpoint);
                if (sent != datagram.Count)
                {
                    var reason = "UDP send wrote " + sent + " of " + datagram.Count
                        + " bytes to peer " + peer;
                    FailSocket(socket, reason);
                    throw new InvalidOperationException(reason);
                }
            }
            catch (SocketException error)
            {
                if (error.SocketErrorCode == SocketError.WouldBlock
                    || error.SocketErrorCode == SocketError.IOPending
                    || error.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    var reason = SocketFailureReason("send", error);
                    FailSocket(socket, reason);
                    throw new InvalidOperationException(reason, error);
                }

                var failure = SocketFailureReason("send", error);
                FailSocket(socket, failure);
                throw new InvalidOperationException(failure, error);
            }
            catch (ObjectDisposedException error)
            {
                throw new InvalidOperationException("UDP socket was disposed during send", error);
            }
        }

        /// <summary>Closes one peer.  Repeating Close for the same handle is a no-op.</summary>
        public void Close(DatagramPeer peer, string reason)
        {
            lock (_gate)
            {
                if (_peersByHandle.TryGetValue(peer, out var state))
                {
                    RemovePeerLocked(state, NormalizeReason(reason, "peer closed"));
                }
            }

            // RemovePeerLocked queues the event so explicit close and socket-failure close share
            // the same exactly-once path.  Drain synchronously because Close itself is a lifecycle
            // operation. Send failures are surfaced immediately to the KCP pump.
            DrainPendingClosed();
        }

        /// <summary>Stops the socket and closes every remaining peer.  Stop is idempotent.</summary>
        public void Stop()
        {
            Socket socket;
            lock (_gate)
            {
                socket = _socket;
                _socket = null;
                _running = false;

                var peers = new List<PeerState>(_peersByHandle.Values);
                foreach (var peer in peers)
                {
                    RemovePeerLocked(peer, "transport stopped");
                }
            }

            // Signal the receive thread, unblock it by closing the socket, then join it.
            Volatile.Write(ref _receiveStop, true);
            CloseSocket(socket);
            StopReceiveThread();
            DrainPendingClosed();
        }

        public void Dispose()
        {
            Stop();
            lock (_gate)
            {
                _disposed = true;
            }
        }

        private sealed class PeerState
        {
            internal readonly DatagramPeer Handle;
            internal readonly IPEndPoint Endpoint;
            internal string CloseReason;

            internal PeerState(DatagramPeer handle, IPEndPoint endpoint)
            {
                Handle = handle;
                Endpoint = endpoint;
            }
        }

        private Socket CreateSocket(AddressFamily family)
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                DisableUdpConnReset(socket, family);
                socket.Blocking = false;

                if (family == AddressFamily.InterNetworkV6)
                {
                    // Do not silently make an IPv6 transport dual-stack.  The endpoint family is
                    // part of the transport configuration and all peer endpoints must match it.
                    socket.DualMode = false;
                }

                return socket;
            }
            catch
            {
                CloseSocket(socket);
                throw;
            }
        }

        private static void DisableUdpConnReset(Socket socket, AddressFamily family)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            try
            {
                socket.IOControl(
                    (IOControlCode)WindowsUdpConnResetIoControlCode,
                    new byte[] { 0 },
                    null);
            }
            catch (PlatformNotSupportedException error)
            {
                LogUnsupportedUdpConnReset(family, error);
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.OperationNotSupported
                || error.SocketErrorCode == SocketError.ProtocolOption)
            {
                LogUnsupportedUdpConnReset(family, error);
            }
        }

        private static void LogUnsupportedUdpConnReset(AddressFamily family, Exception error)
        {
            CoopPlugin.Log?.LogWarning(
                "UDP SIO_UDP_CONNRESET is unavailable for " + family
                + "; continuing without it: " + error.Message);
        }

        private PeerState AddPeerLocked(IPEndPoint endpoint)
        {
            var stableEndpoint = CloneAndValidateEndpoint(endpoint, nameof(endpoint), allowPortZero: false);
            if (_nextToken <= 0 || _nextToken == long.MaxValue)
            {
                throw new InvalidOperationException("UDP peer handle space is exhausted");
            }

            var peer = new PeerState(new DatagramPeer(_nextToken++), stableEndpoint);
            _peersByEndpoint.Add(stableEndpoint, peer);
            _peersByHandle.Add(peer.Handle, peer);
            return peer;
        }

        private void RemovePeerLocked(PeerState peer, string reason)
        {
            if (!_peersByHandle.Remove(peer.Handle))
            {
                return;
            }

            _peersByEndpoint.Remove(peer.Endpoint);
            peer.CloseReason = NormalizeReason(reason, "peer closed");
            _pendingClosed.Add(peer);
        }

        private void RaisePeerAvailable(PeerState peer)
        {
            Action<DatagramPeer> handler;
            lock (_gate)
            {
                // Stop can remove a peer after the caller creates it but before the event is
                // dispatched.  Membership is checked while holding the lifecycle gate so the
                // availability publication is ordered before a concurrent removal.  The handler
                // itself remains outside the lock so callbacks may safely call back into us.
                if (!_peersByHandle.TryGetValue(peer.Handle, out var current)
                    || !ReferenceEquals(current, peer))
                {
                    return;
                }

                handler = PeerAvailable;
            }

            if (handler != null)
            {
                handler(peer.Handle);
            }
        }

        private void RaisePeerClosed(PeerState peer)
        {
            var handler = PeerClosed;
            if (handler != null)
            {
                handler(peer.Handle, peer.CloseReason);
            }
        }

        private void DrainPendingClosed()
        {
            while (true)
            {
                PeerState peer;
                lock (_gate)
                {
                    if (_pendingClosedIndex >= _pendingClosed.Count)
                    {
                        _pendingClosed.Clear();
                        _pendingClosedIndex = 0;
                        return;
                    }

                    peer = _pendingClosed[_pendingClosedIndex++];
                }

                RaisePeerClosed(peer);
            }
        }

        private void StartReceiveThread()
        {
            _receiveStop = false;
            while (_received.TryDequeue(out _))
            {
            }
            _receivedBytes = 0;

            var thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "CardShopCoop.UdpReceive",
            };
            _receiveThread = thread;
            thread.Start();
        }

        private void StopReceiveThread()
        {
            var thread = _receiveThread;
            _receiveThread = null;
            Volatile.Write(ref _receiveStop, true);
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try
                {
                    thread.Join(250);
                }
                catch (ThreadStateException)
                {
                    // The thread already exited.
                }
            }

            while (_received.TryDequeue(out _))
            {
            }
            _receivedBytes = 0;
        }

        /// <summary>
        /// Owns every blocking UDP receive. Runs on its own background thread so the socket
        /// syscall and address marshaling never execute on the Unity frame pump. Datagrams are
        /// copied into owned buffers and queued for <see cref="Poll"/>.
        /// </summary>
        private void ReceiveLoop()
        {
            var family = _bindEndpoint.AddressFamily;
            while (!Volatile.Read(ref _receiveStop))
            {
                Socket socket;
                lock (_gate)
                {
                    if (!_running || _socket == null)
                    {
                        return;
                    }

                    socket = _socket;
                }

                try
                {
                    if (!socket.Poll(ReceivePollMicroseconds, SelectMode.SelectRead))
                    {
                        continue;
                    }
                }
                catch (SocketException)
                {
                    if (Volatile.Read(ref _receiveStop))
                    {
                        return;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                EndPoint remote = CreateReceiveEndpoint(family);
                int received;
                try
                {
                    received = socket.ReceiveFrom(_workerBuffer, 0, _workerBuffer.Length,
                        SocketFlags.None, ref remote);
                }
                catch (SocketException error)
                {
                    if (error.SocketErrorCode == SocketError.WouldBlock
                        || error.SocketErrorCode == SocketError.IOPending
                        || error.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }

                    if (!Volatile.Read(ref _receiveStop))
                    {
                        // FailSocket takes the lifecycle gate and queues peer-closed events for the
                        // pump to raise on its own thread, so it is safe to call from here.
                        FailSocket(socket, SocketFailureReason("receive", error));
                    }
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (received < 0 || received > _maxDatagramSize)
                {
                    continue;
                }

                if (!(remote is IPEndPoint endpoint)
                    || !IsUsableRemoteEndpoint(endpoint, family))
                {
                    continue;
                }

                var copy = new byte[received];
                Buffer.BlockCopy(_workerBuffer, 0, copy, 0, received);
                TryQueueReceived(endpoint, copy);
            }
        }

        private bool TryQueueReceived(IPEndPoint endpoint, byte[] bytes)
        {
            if (_received.Count >= MaxQueuedDatagrams
                || Volatile.Read(ref _receivedBytes) + bytes.Length > MaxQueuedBytes)
            {
                // The protocol above tolerates datagram loss (KCP retransmits reliable frames), so
                // a full queue drops rather than blocking the receive thread.
                return false;
            }

            _received.Enqueue(new ReceivedDatagram(endpoint, bytes));
            Interlocked.Add(ref _receivedBytes, bytes.Length);
            return true;
        }

        private readonly struct ReceivedDatagram
        {
            internal readonly IPEndPoint Endpoint;
            internal readonly byte[] Bytes;

            internal ReceivedDatagram(IPEndPoint endpoint, byte[] bytes)
            {
                Endpoint = endpoint;
                Bytes = bytes;
            }
        }

        private void FailSocket(Socket failedSocket, string reason)
        {
            lock (_gate)
            {
                if (!_running || !ReferenceEquals(_socket, failedSocket))
                {
                    return;
                }

                _socket = null;
                _running = false;
                var peers = new List<PeerState>(_peersByHandle.Values);
                foreach (var peer in peers)
                {
                    RemovePeerLocked(peer, reason);
                }
            }

            CloseSocket(failedSocket);
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        private static bool IsValidSegment(ArraySegment<byte> segment)
        {
            return segment.Array != null
                && segment.Offset >= 0
                && segment.Count >= 0
                && segment.Offset <= segment.Array.Length - segment.Count;
        }

        private static bool IsUsableRemoteEndpoint(IPEndPoint endpoint, AddressFamily family)
        {
            return endpoint != null
                && endpoint.AddressFamily == family
                && !endpoint.Address.IsIPv4MappedToIPv6
                && endpoint.Port > 0
                && endpoint.Port <= 65535;
        }

        private static EndPoint CreateReceiveEndpoint(AddressFamily family)
        {
            return family == AddressFamily.InterNetwork
                ? (EndPoint)new IPEndPoint(IPAddress.Any, 0)
                : new IPEndPoint(IPAddress.IPv6Any, 0);
        }

        private static IPEndPoint CloneAndValidateEndpoint(
            IPEndPoint endpoint,
            string parameterName,
            bool allowPortZero)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (endpoint.AddressFamily != AddressFamily.InterNetwork
                && endpoint.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("UDP requires an IPv4 or IPv6 endpoint", parameterName);
            }

            if (endpoint.Address.IsIPv4MappedToIPv6)
            {
                throw new ArgumentException("IPv4-mapped IPv6 endpoints are not supported", parameterName);
            }

            if (endpoint.Port < 0 || endpoint.Port > 65535 || (!allowPortZero && endpoint.Port == 0))
            {
                throw new ArgumentOutOfRangeException(parameterName, "The UDP port is outside the configured range");
            }

            return new IPEndPoint(endpoint.Address, endpoint.Port);
        }

        private static IPEndPoint CloneAndValidateLocalEndpoint(
            IPEndPoint endpoint,
            AddressFamily requiredFamily)
        {
            if (endpoint == null)
            {
                return requiredFamily == AddressFamily.InterNetwork
                    ? new IPEndPoint(IPAddress.Any, 0)
                    : new IPEndPoint(IPAddress.IPv6Any, 0);
            }

            var local = CloneAndValidateEndpoint(endpoint, nameof(endpoint), allowPortZero: true);
            if (local.AddressFamily != requiredFamily)
            {
                throw new ArgumentException(
                    "The local and remote UDP endpoints must use the same address family",
                    nameof(endpoint));
            }

            return local;
        }

        private static void ValidateMaxDatagramSize(int maxDatagramSize)
        {
            if (maxDatagramSize <= 0 || maxDatagramSize > MaximumDatagramSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDatagramSize),
                    "MaxDatagramSize must be between 1 and 65507 bytes");
            }
        }

        private static string NormalizeReason(string reason, string fallback)
        {
            return string.IsNullOrEmpty(reason) ? fallback : reason;
        }

        private static string SocketFailureReason(string operation, SocketException error)
        {
            return "UDP " + operation + " failed (" + error.SocketErrorCode + "): " + error.Message;
        }

        private static void CloseSocket(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException)
            {
                // Close is intentionally idempotent.
            }
        }
    }
}
