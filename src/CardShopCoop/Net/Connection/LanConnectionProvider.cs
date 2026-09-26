using System;
using System.Net;
using System.Net.Sockets;
using CardShopCoop.Net.Datagrams;
using CardShopCoop.Net.Kcp;

namespace CardShopCoop.Net.Connection
{
    /// <summary>Connection establishment over the UDP/KCP transport.</summary>
    public sealed class LanConnectionProvider : IConnectionProvider
    {
        private KcpSessionManager _manager;

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
            try
            {
                EnsureUnused();
                var datagrams = UdpDatagramTransport.CreateHost(port);
                PublishAndStart(datagrams, true);
            }
            catch (Exception error)
            {
                Failed?.Invoke(error.Message);
                throw;
            }
        }

        public void Join(string address, int port, string password = "")
        {
            try
            {
                EnsureUnused();
                var endpoint = ResolveEndpoint(address, port);
                var datagrams = UdpDatagramTransport.CreateClient(endpoint);
                PublishAndStart(datagrams, false);
            }
            catch (Exception error)
            {
                Failed?.Invoke(error.Message);
                throw;
            }
        }

        private void EnsureUnused()
        {
            if (Transport != null || _manager != null)
            {
                throw new InvalidOperationException("LAN provider already has an active transport.");
            }
        }

        public void Dispose()
        {
            var manager = _manager;
            if (manager == null && Transport == null)
            {
                return;
            }

            if (manager != null)
            {
                manager.PeerConnected -= OnPeerConnected;
            }

            try
            {
                Transport?.Dispose();
            }
            finally
            {
                _manager = null;
                Transport = null;
                TransportChanged?.Invoke();
            }
        }

        private void OnPeerConnected(PeerConnection connection)
        {
            PeerConnected?.Invoke(connection);
        }

        private void PublishAndStart(UdpDatagramTransport datagrams, bool isHost)
        {
            KcpSessionManager manager = null;
            NetworkPump pump = null;
            var published = false;
            try
            {
                manager = new KcpSessionManager(datagrams, isHost);
                manager.PeerConnected += OnPeerConnected;
                pump = (NetworkPump)NetworkPump.Wrap(LagTransport.Wrap(manager));
                _manager = manager;
                Transport = pump;
                TransportChanged?.Invoke();
                published = true;
                pump.Start();
                if (!isHost)
                {
                    Connected?.Invoke();
                }
            }
            catch
            {
                if (manager != null)
                {
                    manager.PeerConnected -= OnPeerConnected;
                    try
                    {
                        if (pump != null)
                        {
                            pump.Dispose();
                        }
                        else
                        {
                            manager.Dispose();
                        }
                    }
                    finally
                    {
                        datagrams.Dispose();
                    }
                }
                else
                {
                    datagrams.Dispose();
                }

                if (ReferenceEquals(_manager, manager))
                {
                    _manager = null;
                    Transport = null;
                }

                if (published)
                {
                    TransportChanged?.Invoke();
                }

                throw;
            }
        }

        private static IPEndPoint ResolveEndpoint(string address, int port)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("LAN address must not be empty.", nameof(address));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "The LAN port is outside the valid range.");
            }

            var host = address.Trim();
            IPAddress parsed;
            IPAddress[] candidates;
            if (IPAddress.TryParse(host, out parsed))
            {
                candidates = new[] { parsed };
            }
            else
            {
                candidates = Dns.GetHostAddresses(host);
            }

            foreach (var candidate in candidates)
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    return new IPEndPoint(candidate, port);
                }
            }

            throw new ArgumentException(
                "LAN address must be an IPv4 address or resolve to one.", nameof(address));
        }
    }
}


