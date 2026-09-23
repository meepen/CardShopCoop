using System;

namespace CardShopCoop.Net.Connection
{
    /// <summary>Platform-neutral session establishment boundary.</summary>
    public interface IConnectionProvider : IDisposable
    {
        ICoopTransport Transport
        {
            get;
        }
        void Host(int port, bool isPublic = false, string lobbyName = "", string password = "", int maxPlayers = 0);
        void Join(string address, int port, string password = "");
        event Action TransportChanged;
        event Action<PeerConnection> PeerConnected;
        event Action Connected;
        event Action<string> Failed;
    }
}


