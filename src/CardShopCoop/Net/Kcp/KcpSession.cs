using System;
using kcp2k;
using KcpCore = kcp2k.Kcp;

namespace CardShopCoop.Net.Kcp
{
    /// <summary>
    /// A socketless kcp2k peer.  The caller owns the datagram bearer and the clock:
    /// <see cref="InputDatagram(ArraySegment{byte})"/> is fed complete datagrams and
    /// <see cref="Tick(uint)"/> is called with a monotonic millisecond timestamp.
    ///
    /// The outer MTU is the size of the complete datagram, including the one-byte channel
    /// and four-byte cookie headers.  RawSend and DataReceived are synchronous; neither this
    /// class nor the vendored peer retains their ArraySegment beyond the callback.
    /// </summary>
    public class KcpSession : KcpPeer
    {
        private readonly bool isServer;
        private readonly int mtu;
        private bool started;

        private readonly Action<ArraySegment<byte>> rawSendCallback;
        private readonly Action<ArraySegment<byte>, KcpChannel> dataCallback;
        private readonly Action authenticatedCallback;
        private readonly Action disconnectedCallback;
        private readonly Action<ErrorCode, string> errorCallback;
        private uint pendingCookie;

        /// <summary>Raised after the Hello exchange has authenticated this peer.</summary>
        public event Action Authenticated;

        /// <summary>Raised synchronously for each decoded application message.</summary>
        public event Action<ArraySegment<byte>, KcpChannel> DataReceived;

        /// <summary>Raised once when the session transitions to Disconnected.</summary>
        public event Action Disconnected;

        /// <summary>Raised for protocol, timeout, and KCP errors.</summary>
        public event Action<ErrorCode, string> Error;

        /// <summary>
        /// Creates a peer.  A client/initiator must use cookie zero; a server/responder uses
        /// the non-zero cookie it assigned to this session.  The server cookie is carried in
        /// every server datagram and is learned by the client from the server Hello.
        /// </summary>
        public KcpSession(
            KcpConfig config,
            bool isServer,
            uint cookie,
            Action<ArraySegment<byte>> rawSend,
            Action<ArraySegment<byte>, KcpChannel> onData = null,
            Action onAuthenticated = null,
            Action onDisconnected = null,
            Action<ErrorCode, string> onError = null,
            uint initialTime = 0)
            : base(ValidateConfig(config), ValidateCookie(isServer, cookie), initialTime)
        {
            if (rawSend == null)
                throw new ArgumentNullException(nameof(rawSend));

            this.isServer = isServer;
            mtu = config.Mtu;
            rawSendCallback = rawSend;
            dataCallback = onData;
            authenticatedCallback = onAuthenticated;
            disconnectedCallback = onDisconnected;
            errorCallback = onError;
        }

        /// <summary>Convenience overload for callers that subscribe to events.</summary>
        public KcpSession(KcpConfig config, bool isServer, uint cookie, Action<ArraySegment<byte>> rawSend,
            uint initialTime)
            : this(config, isServer, cookie, rawSend, null, null, null, null, initialTime)
        {
        }

        private static KcpConfig ValidateConfig(KcpConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.Mtu < KcpPeer.METADATA_SIZE + KcpCore.MTU_MIN)
                throw new ArgumentOutOfRangeException(nameof(config.Mtu),
                    $"KCP MTU must leave room for channel, cookie, and a KCP MTU of at least {KcpCore.MTU_MIN} bytes.");
            if (config.Interval == 0)
                throw new ArgumentOutOfRangeException(nameof(config.Interval));
            if (config.ReceiveWindowSize < 2)
                throw new ArgumentOutOfRangeException(nameof(config.ReceiveWindowSize));
            return config;
        }

        private static uint ValidateCookie(bool isServer, uint cookie)
        {
            if (isServer && cookie == 0)
                throw new ArgumentOutOfRangeException(nameof(cookie),
                    "A server KCP cookie must be non-zero.");
            return cookie;
        }

        /// <summary>True for the responder/server side of the cookie handshake.</summary>
        public bool IsServer => isServer;

        /// <summary>True for the initiator/client side of the cookie handshake.</summary>
        public bool IsClient => !isServer;

        public bool IsStarted => started;
        public bool IsAuthenticated => state == KcpState.Authenticated;
        public bool IsDisconnected => state == KcpState.Disconnected;
        public KcpState State => state;

        /// <summary>The currently accepted security cookie. Clients learn this from Hello.</summary>
        public uint Cookie => cookie;

        /// <summary>The complete outer datagram size, including KCP metadata.</summary>
        public int Mtu => mtu;

        /// <summary>Largest application payload on the reliable channel.</summary>
        public new int ReliableMaxMessageSize => reliableMax;

        /// <summary>Largest application payload on the unreliable channel.</summary>
        public new int UnreliableMaxMessageSize => unreliableMax;

        // Friendly aliases matching kcp2k's original terminology.
        public int ReliableMax => reliableMax;
        public int UnreliableMax => unreliableMax;

        /// <summary>Number of reliable segments waiting in KCP's send queues.</summary>
        public int PendingReliableSegments => kcp.WaitSnd;

        /// <summary>
        /// Flushes queued reliable output immediately, bypassing KCP's interval pacing. The
        /// disconnect path needs this: kcp2k's disconnect does not flush queued application data,
        /// so a terminal control frame must reach the wire before <see cref="Disconnect"/>.
        /// </summary>
        public void FlushOutgoing()
        {
            if (!IsDisconnected)
                kcp.Flush();
        }

        /// <summary>
        /// Starts the session.  An initiator sends its reliable Hello; a responder waits for
        /// the initiator's Hello and replies only after that packet authenticates it.
        /// Repeated calls do not create another handshake.
        /// </summary>
        public bool Start(uint now)
        {
            if (started || IsDisconnected)
                return false;

            SetInitialTime(now);
            started = true;
            if (IsClient)
                SendHello();
            return true;
        }

        /// <summary>Starts a session whose caller uses a zero-based monotonic clock.</summary>
        public bool Start()
        {
            return Start(0);
        }

        public override void TickIncoming(uint now)
        {
            EnsureStarted(now);
            base.TickIncoming(now);
        }

        public override void TickOutgoing(uint now)
        {
            EnsureStarted(now);
            base.TickOutgoing(now);
        }

        public override void Tick(uint now)
        {
            EnsureStarted(now);
            base.Tick(now);
        }

        private void EnsureStarted(uint now)
        {
            if (!started && !IsDisconnected)
                Start(now);
        }

        /// <summary>
        /// Feeds one complete datagram.  The input array is read synchronously and is never
        /// retained.  False means the datagram was rejected (including an oversized packet or
        /// a cookie/channel mismatch); true means it was accepted by the framing layer.
        /// KCP messages are delivered by the next TickIncoming/Tick call.
        /// </summary>
        public bool InputDatagram(ArraySegment<byte> datagram)
        {
            if (IsDisconnected || datagram.Array == null || datagram.Count <= METADATA_SIZE)
                return false;

            // Mtu is the outer transport limit, not the KCP payload limit.  In particular,
            // config.Mtu itself is valid because RawSend emits exactly this many bytes at most.
            if (datagram.Count > mtu)
            {
                ReportError(ErrorCode.InvalidReceive,
                    $"{GetType()}: datagram size {datagram.Count} exceeds outer MTU {mtu}.");
                return false;
            }

            int offset = datagram.Offset;
            byte channel = datagram.Array[offset];
            Utils.Decode32U(datagram.Array, offset + CHANNEL_HEADER_SIZE, out uint messageCookie);

            if (channel != (byte)KcpChannel.Reliable && channel != (byte)KcpChannel.Unreliable)
            {
                Log.Warning($"[KCP] {GetType()}: invalid channel header {channel}.");
                return false;
            }

            // The responder accepts the initial client Hello without a cookie.  Once either
            // side has authenticated, every packet must carry the session cookie.  A client
            // learns the responder's cookie from its first server packet.
            if (isServer)
            {
                if (state == KcpState.Authenticated && messageCookie != cookie)
                    return false;
            }
            else
            {
                // Before authentication there is no trusted cookie to compare against.
                // In particular, never let an arbitrary unreliable, malformed, or zero-cookie
                // datagram poison the value that will protect the authenticated session.
                if (cookie == 0 && messageCookie == 0)
                    return false;
                if (cookie != 0 && messageCookie != cookie)
                {
                    return false;
                }
            }

            var message = new ArraySegment<byte>(
                datagram.Array,
                offset + METADATA_SIZE,
                datagram.Count - METADATA_SIZE);

            switch (channel)
            {
                case (byte)KcpChannel.Reliable:
                    {
                        bool inputAccepted = OnRawInputReliable(message);
                        if (!isServer && cookie == 0 && messageCookie != 0 && inputAccepted &&
                            IsReliableHelloPayload(message))
                        {
                            // Do not commit yet. KCP may have accepted the wire packet while
                            // dropping it as out of window. The hook below commits only when the
                            // actual reliable Hello reaches the application receive path.
                            pendingCookie = messageCookie;
                        }
                        return true;
                    }
                case (byte)KcpChannel.Unreliable:
                    OnRawInputUnreliable(message);
                    return true;
            }

            return false;
        }

        private static bool IsReliableHelloPayload(ArraySegment<byte> message)
        {
            int offset = message.Offset;
            int remaining = message.Count;
            bool foundHello = false;

            // Hello is sent as one unfragmented KCP PUSH containing exactly its one-byte
            // reliable header. Validate every segment so trailing truncated bytes cannot turn
            // an otherwise invalid datagram into a cookie candidate.
            while (remaining > 0)
            {
                if (remaining < KcpCore.OVERHEAD)
                    return false;

                byte command = message.Array[offset + 4];
                byte fragment = message.Array[offset + 5];
                Utils.Decode32U(message.Array, offset + 20, out uint length);
                if (length > (uint)(remaining - KcpCore.OVERHEAD))
                    return false;

                if (command == KcpCore.CMD_PUSH && fragment == 0 && length == 1 &&
                    message.Array[offset + KcpCore.OVERHEAD] == (byte)KcpHeaderReliable.Hello)
                {
                    foundHello = true;
                }

                int segmentSize = KcpCore.OVERHEAD + (int)length;
                offset += segmentSize;
                remaining -= segmentSize;
            }

            return foundHello;
        }

        /// <summary>Array overload for transport adapters that already own a byte[].</summary>
        public bool InputDatagram(byte[] datagram, int offset, int count)
        {
            if (datagram == null)
                throw new ArgumentNullException(nameof(datagram));
            return InputDatagram(new ArraySegment<byte>(datagram, offset, count));
        }

        public bool InputDatagram(byte[] datagram)
        {
            if (datagram == null)
                throw new ArgumentNullException(nameof(datagram));
            return InputDatagram(new ArraySegment<byte>(datagram));
        }

        /// <summary>Queues an application payload for the requested KCP channel.</summary>
        public bool Send(ArraySegment<byte> data, KcpChannel channel)
        {
            if (!IsAuthenticated || data.Array == null)
                return false;
            if (data.Count == 0)
                return SendData(data, channel);
            if (channel == KcpChannel.Reliable && data.Count > reliableMax)
                return RejectOversize(data.Count, reliableMax, true);
            if (channel == KcpChannel.Unreliable && data.Count > unreliableMax)
                return RejectOversize(data.Count, unreliableMax, false);
            return SendData(data, channel);
        }

        public bool Send(byte[] data, KcpChannel channel)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return Send(new ArraySegment<byte>(data), channel);
        }

        public bool SendReliable(ArraySegment<byte> data)
        {
            return Send(data, KcpChannel.Reliable);
        }

        public bool SendReliable(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return SendReliable(new ArraySegment<byte>(data));
        }

        public bool SendReliable(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return SendReliable(new ArraySegment<byte>(data, offset, count));
        }

        public bool SendUnreliable(ArraySegment<byte> data)
        {
            return Send(data, KcpChannel.Unreliable);
        }

        public bool SendUnreliable(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return SendUnreliable(new ArraySegment<byte>(data));
        }

        public bool SendUnreliable(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return SendUnreliable(new ArraySegment<byte>(data, offset, count));
        }

        private bool RejectOversize(int actual, int limit, bool reliable)
        {
            ReportError(ErrorCode.InvalidSend,
                $"{GetType()}: {(reliable ? "reliable" : "unreliable")} payload size {actual} exceeds limit {limit}.");
            return false;
        }

        protected override void OnAuthenticated()
        {
            // The responder's Hello is the only server-side handshake reply.  It is sent
            // before the callback, matching kcp2k and making the callback observe a ready link.
            if (isServer)
                SendHello();
            authenticatedCallback?.Invoke();
            Authenticated?.Invoke();
        }

        protected override bool OnReliableHelloReceived()
        {
            if (isServer)
                return true;

            if (pendingCookie == 0)
            {
                OnError(ErrorCode.InvalidReceive,
                    $"[KCP] {GetType()}: reliable Hello did not arrive with a valid non-zero cookie.");
                return false;
            }

            cookie = pendingCookie;
            pendingCookie = 0;
            Log.Info($"[KCP] {GetType()}: accepted server cookie={cookie} from reliable Hello.");
            return true;
        }

        protected override void OnData(ArraySegment<byte> message, KcpChannel channel)
        {
            dataCallback?.Invoke(message, channel);
            DataReceived?.Invoke(message, channel);
        }

        protected override void OnDisconnected()
        {
            disconnectedCallback?.Invoke();
            Disconnected?.Invoke();
        }

        protected override void OnError(ErrorCode error, string message)
        {
            errorCallback?.Invoke(error, message);
            Error?.Invoke(error, message);
        }

        protected override void RawSend(ArraySegment<byte> data)
        {
            // Deliberately no copy and no queue: kcp2k's raw buffer is valid for this call
            // only, and transports must consume/copy it before returning.
            rawSendCallback(data);
        }

        private void ReportError(ErrorCode error, string message)
        {
            OnError(error, message);
        }
    }
}
