using CardShopCoop.Net;

namespace CardShopCoop.SampleMod
{
    /// <summary>Client to host: please increment the shared counter.</summary>
    [NetworkMessage]
    public sealed class SampleIncrementIntent : INetMessage
    {
    }

    /// <summary>Host to everyone: the authoritative counter value.</summary>
    [NetworkMessage]
    public sealed class SampleCounterState : INetMessage
    {
        public int Value;
    }
}
