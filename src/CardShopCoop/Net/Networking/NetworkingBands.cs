namespace CardShopCoop.Net.Networking
{
    /// <summary>Reserved width of one backend's peer-id band. The runtime assigns each backend a
    /// disjoint band of this size, so a PeerConnection.Id is never handed out twice when a host
    /// runs several backends at once.</summary>
    internal static class NetworkingBands
    {
        public const int Size = 1_000_000;
    }
}
