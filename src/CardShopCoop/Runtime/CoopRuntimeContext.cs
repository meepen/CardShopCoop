using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Api;

namespace CardShopCoop.Runtime
{
    public sealed class CoopRuntimeContext : ICoopContext
    {
        public MessageRouter Messages
        {
            get;
        }
        public Func<bool> InGame
        {
            get;
        }
        public Action<INetMessage> Broadcast
        {
            get;
        }
        public Action<int, INetMessage> Send
        {
            get;
        }
        /// <summary>Fans a host message out to every ready peer except one, using the same
        /// admission as a broadcast. Null on contexts that predate a live transport.</summary>
        public Action<int, INetMessage> Relay
        {
            get;
        }
        /// <summary>The live reachable peers, shared with the public context view. Null on
        /// contexts that predate a live transport.</summary>
        public Func<IReadOnlyList<PeerConnection>> Connections
        {
            get;
        }
        public Func<IReadOnlyList<int>> ConnectionIds
        {
            get;
        }
        public Func<int, string> PeerName
        {
            get;
        }
        public Action<string, float> SetStatusLine
        {
            get;
        }
        public Func<bool> PreloadHold
        {
            get;
        }
        public Action<int, DisconnectInfo> Disconnect
        {
            get;
        }
        public IPeerPresence PeerPresence
        {
            get;
        }

        public CoopRuntimeContext(MessageRouter messages, Func<bool> inGame,
            Action<INetMessage> broadcast, Action<int, INetMessage> send,
            Func<IReadOnlyList<int>> connectionIds = null,
            Func<int, string> peerName = null,
            Action<string, float> setStatusLine = null,
            Func<bool> preloadHold = null,
            Action<int, DisconnectInfo> disconnect = null,
            Action<int, INetMessage> relay = null,
            Func<IReadOnlyList<PeerConnection>> connections = null)
        {
            Messages = messages ?? throw new ArgumentNullException(nameof(messages));
            InGame = inGame ?? throw new ArgumentNullException(nameof(inGame));
            Broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            Send = send ?? throw new ArgumentNullException(nameof(send));
            Connections = connections;
            ConnectionIds = connectionIds;
            PeerName = peerName;
            SetStatusLine = setStatusLine;
            PreloadHold = preloadHold;
            Disconnect = disconnect;
            Relay = relay;
            PeerPresence = new PeerPresenceProvider(TimeSpan.FromSeconds(10));
        }

        // ICoopContext is the public view of this live session. Explicit implementations keep the
        // built-in rich members (Func/Action fields, MessageRouter) untouched for internal modules.
        bool ICoopContext.IsHost => CoopCore.Role == CoopRole.Host;
        bool ICoopContext.IsClient => CoopCore.Role == CoopRole.Client;
        bool ICoopContext.InSession => CoopCore.Role != CoopRole.None;
        bool ICoopContext.InGame => InGame != null && InGame();
        int ICoopContext.LocalConnectionId => CoopCore.LocalConnectionId;
        int ICoopContext.HostConnectionId => CoopCore.Role == CoopRole.Client ? 1 : -1;
        PeerConnection ICoopContext.HostConnection => CoopCore.Role == CoopRole.Client
            ? FindConnection(1) : null;
        IReadOnlyList<PeerConnection> ICoopContext.Connections => Connections == null
            ? Array.Empty<PeerConnection>() : Connections();
        IReadOnlyList<PlayerRef> ICoopContext.Players => BuildPlayers();
        PlayerRef ICoopContext.Self => FindPlayer(player => player.IsSelf);
        PlayerRef ICoopContext.Host => FindPlayer(player => player.IsHost);
        IReadOnlyList<int> ICoopContext.ConnectionIds => ConnectionIds == null
            ? Array.Empty<int>() : ConnectionIds();
        string ICoopContext.PeerName(int connectionId)
            => PeerName == null ? null : PeerName(connectionId);
        ICoopMessageRegistry ICoopContext.Messages => Messages;
        void ICoopContext.Send(PeerConnection connection, INetMessage message)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            Send(connection.Id, message);
        }
        void ICoopContext.Send(int connectionId, INetMessage message) => Send(connectionId, message);
        void ICoopContext.Broadcast(INetMessage message) => Broadcast(message);

        private PeerConnection FindConnection(int connectionId)
        {
            var connections = Connections?.Invoke();
            if (connections != null)
            {
                for (var i = 0; i < connections.Count; i++)
                {
                    if (connections[i] != null && connections[i].Id == connectionId)
                    {
                        return connections[i];
                    }
                }
            }

            return null;
        }

        private static IReadOnlyList<PlayerRef> BuildPlayers()
        {
            var players = PresenceApi.SessionPlayers;
            var result = new List<PlayerRef>(players.Count);
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                result.Add(new PlayerRef(player.IsHost ? 0 : player.Id, player.Name,
                    player.IsHost, player.IsSelf));
            }

            return result;
        }

        private static PlayerRef FindPlayer(Func<PlayerRef, bool> match)
        {
            var players = BuildPlayers();
            for (var i = 0; i < players.Count; i++)
            {
                if (match(players[i]))
                {
                    return players[i];
                }
            }

            return null;
        }
    }
}
