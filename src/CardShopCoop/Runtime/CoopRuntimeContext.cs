using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// The live session context. Built-in modules use the rich members while the public
    /// <see cref="ICoopContext"/> view exposes only peer handles and sends, so a module never
    /// handles connection ids or transport connections.
    /// </summary>
    public sealed class CoopRuntimeContext : ICoopContext
    {
        public MessageRouter Messages
        {
            get;
        }

        /// <summary>True once the local peer has finished joining and gameplay traffic is allowed.</summary>
        public Func<bool> InGame
        {
            get;
        }

        /// <summary>The current display name for a host-canonical player id. Internal because a
        /// module talks to players through <see cref="ICoopPeer"/>.</summary>
        internal Func<int, string> PeerName
        {
            get;
        }

        /// <summary>The live transport connections, used only to build peer handles. Internal
        /// because a module never addresses a connection directly.</summary>
        internal Func<IReadOnlyList<PeerConnection>> Connections
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

        internal Action<int, DisconnectInfo> Disconnect
        {
            get;
        }

        internal IPeerPresence PeerPresence
        {
            get;
        }

        internal CoopRuntimeContext(MessageRouter messages, Func<bool> inGame,
            Action<string, float> setStatusLine = null,
            Func<bool> preloadHold = null,
            Action<int, DisconnectInfo> disconnect = null,
            Func<int, string> peerName = null,
            Func<IReadOnlyList<PeerConnection>> connections = null)
        {
            Messages = messages ?? throw new ArgumentNullException(nameof(messages));
            InGame = inGame ?? throw new ArgumentNullException(nameof(inGame));
            SetStatusLine = setStatusLine;
            PreloadHold = preloadHold;
            Disconnect = disconnect;
            PeerName = peerName;
            Connections = connections;
            PeerPresence = new PeerPresenceProvider(TimeSpan.FromSeconds(10));
        }

        bool ICoopContext.IsHost => CoopCore.Role == CoopRole.Host;

        bool ICoopContext.IsClient => CoopCore.Role == CoopRole.Client;

        bool ICoopContext.InSession => CoopCore.Role != CoopRole.None;

        bool ICoopContext.InGame => InGame != null && InGame();

        /// <summary>This machine's player handle.</summary>
        public ICoopPeer Self => PeerHandle.Self();

        /// <summary>The host's player handle: this player on the host, the connected host on a
        /// client.</summary>
        public ICoopPeer Host => PeerHandle.Host();

        /// <summary>Every reachable peer except this player, as handles.</summary>
        public IReadOnlyList<ICoopPeer> Peers
        {
            get
            {
                var connections = Connections?.Invoke();
                if (connections == null || connections.Count == 0)
                {
                    return Array.Empty<ICoopPeer>();
                }

                if (CoopCore.Role == CoopRole.Client)
                {
                    for (var i = 0; i < connections.Count; i++)
                    {
                        var connection = connections[i];
                        if (connection != null && connection.Id == 1)
                        {
                            return new ICoopPeer[] { PeerHandle.ForConnection(connection) };
                        }
                    }

                    return Array.Empty<ICoopPeer>();
                }

                var peers = new List<ICoopPeer>(connections.Count);
                for (var i = 0; i < connections.Count; i++)
                {
                    var peer = PeerHandle.ForConnection(connections[i]);
                    if (peer != null)
                    {
                        peers.Add(peer);
                    }
                }

                return peers;
            }
        }

        /// <summary>Every player in the session, host first, including this player.</summary>
        IReadOnlyList<PlayerRef> ICoopContext.Players
        {
            get
            {
                var players = PresenceApi.SessionPlayers;
                if (players == null || players.Count == 0)
                {
                    return Array.Empty<PlayerRef>();
                }

                var result = new List<PlayerRef>(players.Count);
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    result.Add(new PlayerRef(player.IsHost ? 0 : player.Id, player.Name,
                        player.IsHost, player.IsSelf));
                }

                return result;
            }
        }

        /// <summary>A client sends to the host. The host fans out with
        /// <see cref="Broadcast"/>.</summary>
        public void Send(INetMessage message)
        {
            if (CoopCore.Role != CoopRole.Client)
            {
                throw new InvalidOperationException(
                    "Only a client sends to the host; the host uses Broadcast/BroadcastExcept.");
            }

            var core = CoopCore.Instance ?? throw new InvalidOperationException(
                "Cannot send because the co-op core is not running.");
            core.SendMessage(PeerHandle.Host(), message);
        }

        /// <summary>From the host, fans out to every ready peer; from a client, sends to the host.</summary>
        public void Broadcast(INetMessage message)
        {
            var core = CoopCore.Instance ?? throw new InvalidOperationException(
                "Cannot broadcast because the co-op core is not running.");
            core.BroadcastMessage(message);
        }

        /// <summary>From the host, fans out to every ready peer except <paramref name="peer"/>.
        /// Uses the transport's relay admission, never a raw connection loop.</summary>
        public void BroadcastExcept(ICoopPeer peer, INetMessage message)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            if (CoopCore.Role == CoopRole.Client)
            {
                // A client has exactly one recipient, so the exclusion does not change the
                // destination: the message goes to the host.
                Send(message);
                return;
            }

            var core = CoopCore.Instance ?? throw new InvalidOperationException(
                "Cannot broadcast because the co-op core is not running.");
            core.BroadcastExceptMessage(peer, message);
        }
    }
}
