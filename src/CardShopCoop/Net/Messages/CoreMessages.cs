namespace CardShopCoop.Net.Messages
{
    [NetworkMessage]
    public sealed class ByeMessage : INetMessage
    {
        public string Reason;
    }
}
