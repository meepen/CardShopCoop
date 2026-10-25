using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Registers and unregisters attributed handlers for one object. CardShopCoop binds the
    /// handlers of every behaviour it creates (anything marked with <c>[ServerBehaviour]</c>,
    /// <c>[ClientBehaviour]</c>, or <c>[PersistentBehaviour]</c>) before <c>OnEnable</c>, so a
    /// normal integration never calls this. Use it only for a component you create yourself.
    /// </summary>
    public interface ICoopMessageRegistry
    {
        void RegisterAttributedHandlers(object target);
        void UnregisterAttributedHandlers(object target);
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

        /// <summary>This machine's identity in the host's roster: 0 on the host (self); on a client,
        /// the id the host assigned to this client. It is NOT necessarily a valid
        /// <see cref="Send(int, INetMessage)"/> target in the client's own connection-id space.</summary>
        int LocalConnectionId
        {
            get;
        }

        /// <summary>The connection id that addresses the host from this peer's local point of view
        /// (always 1 on a client). -1 on the host, which has no remote host. Prefer
        /// <see cref="HostConnection"/> when you need the host's name or state.</summary>
        int HostConnectionId
        {
            get;
        }

        /// <summary>The host as a client sees it, or null on the host (and outside a session).
        /// The returned object carries <see cref="PeerConnection.Id"/>,
        /// <see cref="PeerConnection.Name"/> and <see cref="PeerConnection.State"/>.</summary>
        PeerConnection HostConnection
        {
            get;
        }

        /// <summary>The peers reachable from this machine, excluding this player: the host sees
        /// every client; a client sees only the host. Each entry carries Id, Name and State.
        /// Empty outside a session. This is the transport view; use <see cref="Players"/> for the
        /// full roster.</summary>
        IReadOnlyList<PeerConnection> Connections
        {
            get;
        }

        /// <summary>Every player in the session, host first, including this player. Empty outside
        /// a session. Use these values in DTOs; <see cref="PlayerRef"/> is resolved from the roster
        /// on both peers.</summary>
        IReadOnlyList<PlayerRef> Players
        {
            get;
        }

        /// <summary>This player, or null outside a session.</summary>
        PlayerRef Self
        {
            get;
        }

        /// <summary>The host player (this player on the host), or null outside a session.</summary>
        PlayerRef Host
        {
            get;
        }

        /// <summary>Prefer <see cref="Connections"/>, whose entries carry the id and name together.</summary>
        [Obsolete("Use Connections; each PeerConnection carries Id and Name.")]
        IReadOnlyList<int> ConnectionIds
        {
            get;
        }

        /// <summary>Prefer <see cref="Connections"/>; the name is on the connection object.</summary>
        [Obsolete("Use Connections; each PeerConnection carries Name.")]
        string PeerName(int connectionId);

        /// <summary>Binds and unbinds attributed message handlers for one behaviour instance
        /// (see <see cref="ICoopMessageRegistry"/>).</summary>
        ICoopMessageRegistry Messages
        {
            get;
        }

        /// <summary>Sends to one reachable peer.</summary>
        void Send(PeerConnection connection, INetMessage message);

        /// <summary>Sends to one peer by connection id; <see cref="HostConnectionId"/> addresses the
        /// host. Prefer <see cref="Send(PeerConnection, INetMessage)"/>.</summary>
        void Send(int connectionId, INetMessage message);

        /// <summary>From the host, fans out to all ready peers; from a client, sends to the host.</summary>
        void Broadcast(INetMessage message);
    }

    /// <summary>One inbound message, already decoded, handed to an external attributed handler.</summary>
    public sealed class CoopMessageContext
    {
        public PeerConnection Connection
        {
            get;
        }

        /// <summary>The sender as a session player, or null when the roster cannot resolve it
        /// yet. <see cref="Connection"/> is always the transport view.</summary>
        public PlayerRef Sender
        {
            get;
        }

        public bool InGame
        {
            get;
        }

        public int ConnectionId => Connection == null ? 0 : Connection.Id;

        public bool IsAuthenticated => Connection != null
            && (Connection.State == ConnectionState.Transferring
                || Connection.State == ConnectionState.FullyJoined);

        internal CoopMessageContext(PeerConnection connection, bool inGame, PlayerRef sender)
        {
            Connection = connection;
            InGame = inGame;
            Sender = sender;
        }
    }
}
