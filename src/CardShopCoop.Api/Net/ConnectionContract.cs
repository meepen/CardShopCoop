using System;
using CardShopCoop.Api;

namespace CardShopCoop.Net.Connection
{
    public enum ConnectionState
    {
        Connected,
        Handshaking,
        Transferring,
        FullyJoined,
        Disconnecting,
        Disconnected
    }

    /// <summary>Why a peer connection ended. Peer-supplied fields are bounded on the wire.</summary>
    public sealed class DisconnectInfo
    {
        public const int MaxCodeLength = 32;
        public const int MaxDetailLength = 1024;

        public string Code
        {
            get;
        }
        public string Reason
        {
            get;
        }
        public bool Remote
        {
            get;
        }
        public bool Retryable
        {
            get;
        }
        public ConnectionState Phase
        {
            get;
        }

        public DisconnectInfo(string reason, bool remote = false, string code = "closed",
            bool retryable = false, ConnectionState phase = ConnectionState.Disconnecting)
        {
            if (!Enum.IsDefined(typeof(ConnectionState), phase))
            {
                phase = ConnectionState.Disconnecting;
            }

            Code = Bound(code, MaxCodeLength, "closed");
            Reason = Bound(reason, MaxDetailLength, "PeerConnection closed");
            Remote = remote;
            Retryable = retryable;
            Phase = phase;
        }

        private static string Bound(string value, int max, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            value = value.Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }

    /// <summary>One connected peer: an internal session id for the runtime, its connection
    /// state, and the display name once the peer has identified itself. This type is an
    /// implementation detail of the runtime; public code addresses players through
    /// <see cref="CardShopCoop.Api.ICoopPeer" />.</summary>
    internal class PeerConnection : ICoopPeer
    {
        private readonly object _stateLock = new();
        private ConnectionState _state;
        private DisconnectInfo _disconnectInfo;

        internal PeerConnection(int id)
        {
            Id = id;
            _state = ConnectionState.Handshaking;
        }

        internal int Id
        {
            get;
        }

        /// <summary>The peer's display name, or null before the handshake has supplied it.</summary>
        public string Name
        {
            get; internal set;
        }

        public ConnectionState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        /// <summary>True for the authoritative host, filled from the live session by the core.
        /// Read only through <see cref="ICoopPeer.IsHost"/> by a module.</summary>
        public bool IsHost
        {
            get; internal set;
        }

        /// <summary>True for this machine's player, filled from the live session by the core.
        /// Read only through <see cref="ICoopPeer.IsSelf"/> by a module.</summary>
        public bool IsSelf
        {
            get; internal set;
        }

        /// <summary>The roster identity behind this connection, filled from the live session by
        /// the core. Null before the handshake has supplied a name.</summary>
        public PlayerRef Player
        {
            get; internal set;
        }

        /// <summary>The core's single send path. A connection is only addressable while it is
        /// owned by a live session, so the core installs this when it publishes the peer.</summary>
        internal Action<INetMessage> SendOverride
        {
            get; set;
        }

        public void Send(INetMessage message)
        {
            var send = SendOverride ?? throw new InvalidOperationException(
                "Cannot send through a peer connection that is not bound to a live co-op session.");
            send(message);
        }

        internal bool TryTransition(ConnectionState next)
        {
            lock (_stateLock)
            {
                if (_state == ConnectionState.Disconnected || _state == ConnectionState.Disconnecting)
                {
                    return false;
                }

                if (_state == next)
                {
                    return true;
                }

                var valid = (_state == ConnectionState.Handshaking && next == ConnectionState.Transferring)
                    || (_state == ConnectionState.Transferring && next == ConnectionState.FullyJoined)
                    || (_state == ConnectionState.Connected && next == ConnectionState.Handshaking)
                    || next == ConnectionState.Disconnecting;
                if (!valid)
                {
                    return false;
                }

                _state = next;
                return true;
            }
        }

        internal bool BeginDisconnect(DisconnectInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            lock (_stateLock)
            {
                if (_state == ConnectionState.Disconnecting || _state == ConnectionState.Disconnected)
                {
                    return false;
                }

                _state = ConnectionState.Disconnecting;
                _disconnectInfo = info;
                return true;
            }
        }

        internal bool RecordRemoteDisconnect(DisconnectInfo info)
        {
            if (info == null || !info.Remote)
            {
                throw new ArgumentException("Remote disconnect info is required", nameof(info));
            }

            lock (_stateLock)
            {
                if (_state == ConnectionState.Disconnected)
                {
                    return false;
                }

                if (_disconnectInfo != null)
                {
                    return false;
                }

                _disconnectInfo = info;
                _state = ConnectionState.Disconnecting;
                return true;
            }
        }

        internal bool IsDisconnectingOrDisconnected
        {
            get
            {
                lock (_stateLock)
                {
                    return _state == ConnectionState.Disconnecting || _state == ConnectionState.Disconnected;
                }
            }
        }

        internal DisconnectInfo DisconnectReason
        {
            get
            {
                lock (_stateLock)
                {
                    return _disconnectInfo;
                }
            }
        }

        internal bool TryMarkDisconnected()
        {
            lock (_stateLock)
            {
                if (_state == ConnectionState.Disconnected)
                {
                    return false;
                }

                if (_state != ConnectionState.Disconnecting)
                {
                    return false;
                }

                _state = ConnectionState.Disconnected;
                return true;
            }
        }

        public override string ToString() => "PeerConnection(" + Id + ")";
    }
}
