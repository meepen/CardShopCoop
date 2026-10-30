using System;
using CardShopCoop.Api;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Net
{
    /// <summary>
    /// The core implementation of the public <see cref="ICoopPeer"/>. It is the only bridge
    /// between a public peer handle and a transport connection: a module can address a peer but
    /// can never reach the connection id or the socket itself.
    /// </summary>
    internal sealed class PeerHandle : ICoopPeer
    {
        private readonly PeerConnection _connection;
        private readonly int _canonicalId;
        private readonly bool _isHost;
        private readonly bool _isSelf;

        private PeerHandle(PeerConnection connection, int canonicalId, bool isHost, bool isSelf)
        {
            _connection = connection;
            _canonicalId = canonicalId;
            _isHost = isHost;
            _isSelf = isSelf;
        }

        internal PeerConnection Connection => _connection;

        internal int CanonicalId => _canonicalId;

        public string Name => ResolveName(_connection, _canonicalId, IsHost, IsSelf);

        public ConnectionState State
            => _connection?.State ?? ConnectionState.FullyJoined;

        public bool IsHost => _connection != null ? _connection.IsHost : _isHost;

        public bool IsSelf => _connection != null ? _connection.IsSelf : _isSelf;

        public PlayerRef Player
            => _connection?.Player
                ?? PresenceApi.ResolvePlayerRef(_canonicalId)
                ?? new PlayerRef(_canonicalId, Name, _isHost, _isSelf);

        public void Send(INetMessage message)
        {
            var core = CoopCore.Instance ?? throw new InvalidOperationException(
                "Cannot send to a co-op peer because the co-op core is not running.");
            core.SendMessage(this, message);
        }

        /// <summary>
        /// Fills a live connection's <see cref="ICoopPeer"/> identity from the session roster,
        /// so a lifecycle callback typed <see cref="PeerConnection"/>, the same callback typed
        /// <see cref="ICoopPeer"/>, and <see cref="ICoopContext.Peers"/> all report the same
        /// player, host flag, and self flag. Returns the connection for call chaining.
        /// </summary>
        internal static PeerConnection BindIdentity(PeerConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            var canonicalId = CanonicalIdFor(connection);
            var isHost = CoopCore.Role == CoopRole.Client && connection.Id == 1;
            var isSelf = CoopCore.Role == CoopRole.Client
                && CoopCore.LocalConnectionId >= 0
                && connection.Id == CoopCore.LocalConnectionId;
            connection.IsHost = isHost;
            connection.IsSelf = isSelf;
            connection.Player = PresenceApi.ResolvePlayerRef(canonicalId)
                ?? new PlayerRef(canonicalId,
                    ResolveName(connection, canonicalId, isHost, isSelf), isHost, isSelf);
            // One send path for every way of addressing a peer: the connection object hands the
            // message to the same CoopCore.SendMessage the peer handle uses.
            connection.SendOverride ??= message =>
            {
                var core = CoopCore.Instance ?? throw new InvalidOperationException(
                    "Cannot send to a co-op peer because the co-op core is not running.");
                core.SendMessage(connection, message);
            };
            return connection;
        }

        private static int CanonicalIdFor(PeerConnection connection)
            => CoopCore.Role == CoopRole.Client && connection.Id == 1 ? 0 : connection.Id;

        private static string ResolveName(PeerConnection connection, int canonicalId, bool isHost,
            bool isSelf)
        {
            if (isSelf)
            {
                return PresenceApi.LocalPlayerName;
            }

            var name = PresenceApi.PeerName(canonicalId);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (!string.IsNullOrEmpty(connection?.Name))
            {
                return connection.Name;
            }

            return isHost ? "Host" : "Player " + canonicalId;
        }

        /// <summary>Wraps a live transport connection. On a client the host link is connection 1
        /// locally but player 0 canonically.</summary>
        internal static PeerHandle ForConnection(PeerConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            BindIdentity(connection);
            return new PeerHandle(connection, CanonicalIdFor(connection), connection.IsHost, false);
        }

        /// <summary>This machine's player handle. The host has no connection object for itself;
        /// a client's own player id is the one the host assigned at join.</summary>
        internal static PeerHandle Self()
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                return new PeerHandle(null, 0, true, true);
            }

            var id = CoopCore.LocalConnectionId;
            return new PeerHandle(null, id >= 0 ? id : 0, false, true);
        }

        /// <summary>The host's player handle: self on the host, the connection-1 peer on a
        /// client.</summary>
        internal static PeerHandle Host()
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                return Self();
            }

            return ForConnection(FindConnection(1)) ?? new PeerHandle(null, 0, true, false);
        }

        private static PeerConnection FindConnection(int id)
        {
            var connections = CoopCore.Instance?.Net?.Connections;
            if (connections == null)
            {
                return null;
            }

            for (var i = 0; i < connections.Count; i++)
            {
                var connection = connections[i];
                if (connection != null && connection.Id == id)
                {
                    return connection;
                }
            }

            return null;
        }

        public override bool Equals(object obj)
            => obj is PeerHandle other
                && other._canonicalId == _canonicalId
                && other._isSelf == _isSelf
                && other._isHost == _isHost;

        public override int GetHashCode() => _canonicalId;

        public override string ToString() => Name;
    }
}
