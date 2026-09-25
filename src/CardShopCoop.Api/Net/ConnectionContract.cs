using System;

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
        public const int MaxDetailLength = 256;

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

    /// <summary>Transport identity. The object is stable for the lifetime of a peer.</summary>
    public class PeerConnection
    {
        private readonly object _stateLock = new();
        private ConnectionState _state;
        private DisconnectInfo _disconnectInfo;

        internal PeerConnection(int id)
        {
            Id = id;
            _state = ConnectionState.Handshaking;
        }

        public int Id
        {
            get;
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

    public sealed class ConnectionEvent
    {
        public PeerConnection Connection
        {
            get;
        }

        public DisconnectInfo Disconnect
        {
            get;
        }

        public ConnectionEvent(PeerConnection connection, DisconnectInfo disconnect = null)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Disconnect = disconnect;
        }
    }
}
