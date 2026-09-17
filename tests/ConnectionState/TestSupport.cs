using System.Collections.Concurrent;

namespace CardShopCoop.Net
{
    public sealed class FakeTransport : ICoopTransport
    {
        public ConcurrentQueue<InMsg> Incoming { get; } = new ConcurrentQueue<InMsg>();
        public ConcurrentQueue<ConnectionEvent> Disconnects { get; } = new ConcurrentQueue<ConnectionEvent>();
        public ConcurrentQueue<ConnectionEvent> Connects { get; } = new ConcurrentQueue<ConnectionEvent>();
        public IReadOnlyList<Connection> Connections { get; set; } = Array.Empty<Connection>();
        public int ConnectionCount => Connections.Count;
        public double TimeoutSeconds => 30;
        public void Dispose()
        {
        }
        public void Send(Connection connection, INetMessage message)
        {
        }
        public void Broadcast(INetMessage message)
        {
        }
        public void SendTransient(Connection connection, INetMessage message)
        {
        }
        public void BroadcastTransient(INetMessage message)
        {
        }
        public double SecondsSinceLastRecv(Connection connection) => 0;
        public void Kick(Connection connection, DisconnectInfo info = null)
        {
        }
        public void GracefulDisconnect(Connection connection, DisconnectInfo info = null)
        {
        }
        public void Stop()
        {
        }
        public void PumpMainThread()
        {
        }
    }
}
