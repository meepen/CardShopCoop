using CardShopCoop.Net;

namespace CardShopCoop.Net.Messages
{
    // This is a client -> host signal, but it cannot use the generic HostOnly policy:
    // the receiver's local role is Host while the sender is the authenticated guest.
    // MessageRouter applies the connection/state/identity check for this control frame.
    [NetworkMessage(MsgType.FullyJoined, Policy = MessagePolicy.Any)]
    public sealed class FullyJoinedMessage : INetMessage
    {
        public MsgType Type
        {
            get
            {
                return MsgType.FullyJoined;
            }
        }
    }

    [NetworkMessage(MsgType.FullyJoinedAck, Policy = MessagePolicy.ClientOnly)]
    public sealed class FullyJoinedAckMessage : INetMessage
    {
        public MsgType Type
        {
            get
            {
                return MsgType.FullyJoinedAck;
            }
        }
    }

    [NetworkMessage(MsgType.Disconnect, Policy = MessagePolicy.Any)]
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
        public MsgType Type
        {
            get
            {
                return MsgType.Disconnect;
            }
        }
    }
}
