using CardShopCoop.Net;

namespace CardShopCoop.Modules.SaveTransfer
{
    // These names intentionally live in the feature namespace.  Save transfer is the first
    // migrated feature whose wire contract is owned by a module rather than Core/Net.  The
    // handshake remains Core-owned; these are the reliable post-handshake bulk frames.
    [NetworkMessage]
    public sealed class SaveChunkMessage : INetMessage
    {
        public int Offset;
        public byte[] Data = new byte[0];
    }

    [NetworkMessage]
    public sealed class SaveDoneMessage : INetMessage
    {
        public int TotalLength;
    }

    [NetworkMessage]
    public sealed class BundleChunkMessage : INetMessage
    {
        public int Offset;
        public byte[] Data = new byte[0];
    }

    [NetworkMessage]
    public sealed class BundleDoneMessage : INetMessage
    {
        public int TotalLength;
    }

    [NetworkMessage]
    public sealed class TransferFailureMessage : INetMessage
    {
        private const int MaxReasonLength = 256;
        private string _reason;

        public string Reason
        {
            get
            {
                return _reason;
            }
            set
            {
                var reason = string.IsNullOrWhiteSpace(value) ? "world transfer failed" : value.Trim();
                _reason = reason.Substring(0, System.Math.Min(MaxReasonLength, reason.Length));
            }
        }
    }
}
