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
        bool IsHost
        {
            get;
        }
        bool IsClient
        {
            get;
        }
        bool InSession
        {
            get;
        }
        bool InGame
        {
            get;
        }
        int LocalConnectionId
        {
            get;
        }
        IReadOnlyList<int> ConnectionIds
        {
            get;
        }
        string PeerName(int connectionId);
        ICoopMessageRegistry Messages
        {
            get;
        }
        void Send(int connectionId, INetMessage message);
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
