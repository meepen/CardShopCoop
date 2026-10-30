using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Api
{
    /// <summary>
    /// One player in the live session, as a message sender or recipient. Peers are identities,
    /// not connection ids: address a peer by the object itself.
    /// </summary>
    public interface ICoopPeer
    {
        /// <summary>Display name, or null before the handshake has supplied it.</summary>
        string Name
        {
            get;
        }

        /// <summary>The transport state of this peer's connection.</summary>
        ConnectionState State
        {
            get;
        }

        /// <summary>True for the authoritative host.</summary>
        bool IsHost
        {
            get;
        }

        /// <summary>True for this machine's player.</summary>
        bool IsSelf
        {
            get;
        }

        /// <summary>The roster player behind this peer, or null when the roster cannot resolve it
        /// yet. This is the authenticated identity for an inbound message.</summary>
        PlayerRef Player
        {
            get;
        }

        /// <summary>Sends one message to this peer. From a client only <see cref="ICoopContext.Host"/>
        /// (or self) is reachable; from the host any reachable peer is.</summary>
        void Send(INetMessage message);
    }

    /// <summary>
    /// The live co-op session, as seen by an integrating mod. A behaviour receives one through
    /// <see cref="CoopBehaviour.Context"/>. All members are safe to call only while a session is
    /// active; <see cref="InSession"/> tells you.
    /// </summary>
    public interface ICoopContext
    {
        /// <summary>True on the authoritative host.</summary>
        bool IsHost
        {
            get;
        }

        /// <summary>True on a joining client.</summary>
        bool IsClient
        {
            get;
        }

        /// <summary>True while a host or client session is established.</summary>
        bool InSession
        {
            get;
        }

        /// <summary>True once the local peer has finished joining and gameplay traffic is allowed.</summary>
        bool InGame
        {
            get;
        }

        /// <summary>This machine's player, or null outside a session.</summary>
        ICoopPeer Self
        {
            get;
        }

        /// <summary>The host's player (this player on the host), or null outside a session.</summary>
        ICoopPeer Host
        {
            get;
        }

        /// <summary>
        /// The other players reachable from this machine, excluding this player: the host sees
        /// every client, a client sees only the host. Empty outside a session. Use
        /// <see cref="ICoopPeer.Player"/> for the rostered identity and
        /// <see cref="ICoopPeer.Send"/> on a peer to address it.
        /// </summary>
        IReadOnlyList<ICoopPeer> Peers
        {
            get;
        }

        /// <summary>
        /// Every player in the session, host first, including this player. Empty outside a
        /// session. Use these values when a DTO must name a player other than its sender;
        /// <see cref="PlayerRef"/> is resolved from the roster on both peers.
        /// </summary>
        IReadOnlyList<PlayerRef> Players
        {
            get;
        }

        /// <summary>Client: sends to the host. Host: sends to this machine's own player.</summary>
        void Send(INetMessage message);

        /// <summary>Host: fans out to every reachable peer. Client: sends to the host.</summary>
        void Broadcast(INetMessage message);

        /// <summary>Host: fans out to every reachable peer except <paramref name="peer"/>.
        /// Client: sends to the host.</summary>
        void BroadcastExcept(ICoopPeer peer, INetMessage message);
    }

    /// <summary>One inbound message, already decoded, handed to an external attributed handler.</summary>
    public sealed class CoopMessageContext
    {
        /// <summary>The sender. <see cref="ICoopPeer.Player"/> is the authenticated player.</summary>
        public ICoopPeer Peer
        {
            get;
        }

        public bool InGame
        {
            get;
        }

        /// <summary>The action id of the <see cref="ActionMessage"/> being dispatched, or
        /// <see cref="Guid.Empty"/> for a plain message. Filled by the framework.</summary>
        public Guid ActionId
        {
            get;
        }

        /// <summary>True when the sender has completed identity transfer and joined the session.</summary>
        public bool IsAuthenticated => Peer?.State is ConnectionState.Transferring
            or ConnectionState.FullyJoined;

        internal CoopMessageContext(ICoopPeer peer, bool inGame, Guid actionId = default)
        {
            Peer = peer;
            InGame = inGame;
            ActionId = actionId;
        }
    }
}
