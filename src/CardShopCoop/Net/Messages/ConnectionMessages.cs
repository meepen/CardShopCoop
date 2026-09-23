namespace CardShopCoop.Net.Messages
{
    // This is a client -> host signal. The explicit descriptor direction is intentionally
    // separate from the receiver's local role: the sender is the authenticated guest.
    [NetworkMessage]
    public sealed class FullyJoinedMessage : INetMessage
    {
    }

    [NetworkMessage]
    public sealed class FullyJoinedAckMessage : INetMessage
    {
    }

    [NetworkMessage]
    public sealed class DisconnectMessage : INetMessage
    {
        private const int MaxReasonLength = 256;
        private const int MaxCodeLength = 32;
        private string _reason;
        public string Reason
        {
            get
            {
                return _reason;
            }
            set
            {
                _reason = string.IsNullOrWhiteSpace(value) ? "connection closed" : value.Trim().Substring(0, System.Math.Min(MaxReasonLength, value.Trim().Length));
            }
        }
        private string _code;
        public string Code
        {
            get
            {
                return _code;
            }
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? "closed" : value.Trim();
                _code = v.Substring(0, System.Math.Min(MaxCodeLength, v.Length));
            }
        }
        public bool Retryable
        {
            get; set;
        }
        public int Phase
        {
            get; set;
        }
    }
}
