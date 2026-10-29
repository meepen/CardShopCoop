using System;
using CardShopCoop.Api;
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
    /// 2 = warehouse room unlock. Guests apply it directly; the host owns every mutation.
    /// <see cref="ActionId"/> retires the purchase prediction this delta answers.</summary>
    [NetworkMessage]
    public sealed class ExpansionDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public byte Area;
        public int Count;
        public bool Unlocked;
    }

    /// <summary>Client command: purchase one expansion. The host validates and applies it, then
    /// notifies every peer. The client never mutates its own expansion state; its prediction is
    /// recorded by the client's action-ledger closures, so the framework hooks stay empty.</summary>
    [NetworkMessage]
    public sealed class ExpansionPurchaseMessage : ActionMessage
    {
        public byte Kind;

        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }
}
