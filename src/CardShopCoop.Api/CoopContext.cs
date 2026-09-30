using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Api
{
    /// <summary>Registers and unregisters this object's attributed message handlers with the
    /// live co-op session. Mirrors the internal router surface for external behaviours.</summary>
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
        /// the id the host assigned to this client. It is NOT necessarily a valid <see cref="Send"/>
        /// target in the client's own connection-id space.</summary>
        int LocalConnectionId
        {
            get;
        }

        /// <summary>The connection id that addresses the host from this peer's local point of view
        /// (always 1 on a client). -1 on the host, which has no remote host. Use this instead of a
        /// literal 1 to send intents to the host.</summary>
        int HostConnectionId
        {
            get;
        }

        /// <summary>The peers reachable from this machine: the host sees every client; a client sees
        /// only the host (id 1).</summary>
        IReadOnlyList<int> ConnectionIds
        {
            get;
        }

        /// <summary>Display name for a peer id, null if unknown.</summary>
        string PeerName(int connectionId);

        /// <summary>Binds and unbinds attributed message handlers for one behaviour instance
        /// (see <see cref="ICoopMessageRegistry"/>).</summary>
        ICoopMessageRegistry Messages
        {
            get;
        }

        /// <summary>Sends to one peer by connection id; <see cref="HostConnectionId"/> addresses the
        /// host.</summary>
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

        public bool InGame
        {
            get;
        }

        public int ConnectionId => Connection == null ? 0 : Connection.Id;

        public bool IsAuthenticated => Connection != null
            && (Connection.State == ConnectionState.Transferring
                || Connection.State == ConnectionState.FullyJoined);

        internal CoopMessageContext(PeerConnection connection, bool inGame)
        {
            Connection = connection;
            InGame = inGame;
        }
    }
}
