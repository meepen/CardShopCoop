using CardShopCoop.Net;

namespace CardShopCoop.Modules.Expansion
{
    /// <summary>One join-time expansion baseline.</summary>
    [NetworkMessage]
    public sealed class ExpansionBaselineMessage : INetMessage
    {
        public int UnlockRoomCount;
        public int UnlockWarehouseRoomCount;
        public bool IsWarehouseRoomUnlocked;
    }

    /// <summary>Host-authoritative expansion state. 0 = shop rooms, 1 = warehouse rooms,
    /// 2 = warehouse room unlock. Guests apply it directly; the host owns every mutation.</summary>
    [NetworkMessage]
    public sealed class ExpansionDeltaMessage : INetMessage
    {
        public byte Area;
        public int Count;
        public bool Unlocked;
    }

    /// <summary>One client expansion purchase intent. The host validates and applies it, then
    /// notifies every peer. The client never mutates its own expansion state.</summary>
    [NetworkMessage]
    public sealed class ExpansionPurchaseMessage : INetMessage
    {
        public byte Kind;
    }
}
