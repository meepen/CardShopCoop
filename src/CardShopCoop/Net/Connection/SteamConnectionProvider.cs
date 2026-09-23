using System;
using CardShopCoop.Net.Kcp;

namespace CardShopCoop.Net.Connection
{
    /// <summary>Connection establishment through the Steam lobby/KCP bridge.</summary>
    public sealed class SteamConnectionProvider : IConnectionProvider
    {
        private readonly ISteamBridge _steam;
        private long _joinOperation;
        private ICoopTransport _joinTransport;

        public SteamConnectionProvider(ISteamBridge steam)
        {
            _steam = steam ?? throw new ArgumentNullException(nameof(steam));
            _steam.OnConnectedToHost = transport =>
            {
                if (ReferenceEquals(transport, Transport))
                {
                    Connected?.Invoke();
                }
            };
            _steam.OnJoinFailed = (lobby, operation, transport, error) =>
            {
                if (operation == _joinOperation && ReferenceEquals(transport, _joinTransport))
                {
                    Failed?.Invoke(error);
                }
            };
            _steam.OnSessionFailed = (transport, error) =>
            {
                if (ReferenceEquals(transport, Transport))
                {
                    Failed?.Invoke(error);
                }
            };
        }

        public ICoopTransport Transport
        {
            get; private set;
        }
        public event Action Connected;
        public event Action TransportChanged;
        public event Action<PeerConnection> PeerConnected;
        public event Action<string> Failed;
        public void Host(int port, bool isPublic = false, string lobbyName = "", string password = "", int maxPlayers = 0)
        {
            EnsureUnused();
            Transport = _steam.CreateTransport(true);
            Transport.PeerConnected += OnPeerConnected;
            // Publish first so Core can register the role's module handlers before Start freezes
            // the process-wide protocol snapshot for this session (the LAN provider uses the
            // same ordering). No Steam peer can arrive until the deferred bearer is bound.
            TransportChanged?.Invoke();
            StartKcpTransport(Transport);
            _steam.Host(isPublic, lobbyName, !string.IsNullOrEmpty(password), maxPlayers);
        }

        public void Join(string address, int port, string password = "")
        {
            EnsureUnused();
            if (!ulong.TryParse(address, out var lobby))
            {
                throw new ArgumentException("Steam lobby id must be numeric.", nameof(address));
            }

            Transport = _steam.CreateTransport(false);
            Transport.PeerConnected += OnPeerConnected;
            TransportChanged?.Invoke();
            StartKcpTransport(Transport);
            _joinTransport = Transport;
            _joinOperation = _steam.Join(lobby);
        }

        private void EnsureUnused()
        {
            if (Transport != null)
            {
                throw new InvalidOperationException("Steam provider already has an active transport.");
            }
        }

        public void Dispose()
        {
            var transport = Transport;
            Transport = null;
            _joinTransport = null;
            _joinOperation = 0;

            _steam.Leave();
            if (transport != null)
            {
                transport.PeerConnected -= OnPeerConnected;
            }
            transport?.Dispose();
            TransportChanged?.Invoke();
        }

        private void OnPeerConnected(PeerConnection connection)
        {
            PeerConnected?.Invoke(connection);
        }

        private static void StartKcpTransport(ICoopTransport transport)
        {
            if (transport is KcpSessionManager manager)
            {
                manager.Start();
                return;
            }

            throw new InvalidOperationException(
                "Steam bridge returned a transport that is not a KCP session manager.");
        }
    }
}
