using CardShopCoop.Net.Connection;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Transport contract shared by the LAN UDP/KCP transport and the Steam
    /// KCP over Steam Networking Sockets transport.
    /// Connection ids are small ints; 1 is always "the host" from a client's view.
    /// </summary>
    public interface ICoopTransport : IDisposable
    {
        /// <summary>
        /// Removes one decoded message and releases the transport's incoming-byte admission for
        /// it. Callers must use this method instead of retaining a raw producer queue.
        /// </summary>
        bool TryDequeueIncoming(out InMsg message);
        ConcurrentQueue<ConnectionEvent> Disconnects
        {
            get;
        }
        event Action<PeerConnection> PeerConnected;

        /// <summary>Activates compact message ids for an authenticated peer.</summary>
        void ActivateMessageIds(PeerConnection connection);

        /// <summary>Queues a message using its registered MessageDescriptor.Reliability.</summary>
        void Send(PeerConnection connection, INetMessage message);
        /// <summary>Queues a message for every peer using its registered MessageDescriptor.Reliability.</summary>
        void Broadcast(INetMessage message);
        int ConnectionCount
        {
            get;
        }
        double SecondsSinceLastRecv(PeerConnection connection);
        IReadOnlyList<PeerConnection> Connections
        {
            get;
        }
        void Kick(PeerConnection connection, DisconnectInfo info = null);
        /// <summary>Send a bounded protocol disconnect, then tear down the connection.</summary>
        void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null);
        void Stop();

        /// <summary>Called every frame from the Unity main thread. The LAN UDP/KCP and
        /// Steam KCP over Steam Networking Sockets transports process sends and receives here
        /// (Steamworks is main-thread only).</summary>
        void PumpMainThread();

        /// <summary>Peer-silence tolerance. The Steam KCP over Steam Networking Sockets
        /// transport needs a longer window because its keepalives also run on the
        /// (freezable) main thread.</summary>
        double TimeoutSeconds
        {
            get;
        }
    }

    /// <summary>
    /// Internal control-plane admission used only by Core for the named application handshake.
    /// Keeping this separate from the public send surface prevents external callers from
    /// bypassing the Handshaking admission gate.
    /// </summary>
    internal interface ICoopHandshakeTransport
    {
        void SendHandshake(PeerConnection connection, INetMessage message);
    }
}
