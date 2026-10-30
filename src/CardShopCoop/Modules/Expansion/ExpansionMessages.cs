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
    /// 2 = warehouse room unlock. Guests apply it directly; the host owns every mutation.</summary>
    [NetworkMessage]
    public sealed class ExpansionDeltaMessage : INetMessage
    {
        public byte Area;
        public int Count;
        public bool Unlocked;
    }

    /// <summary>The acting client's post-purchase expansion state. Other peers receive
    /// <see cref="ExpansionDeltaMessage"/>.</summary>
    [NetworkMessage]
    public sealed class ExpansionPurchaseResponse : ActionResponseMessage
    {
        public byte Area;
        public int Count;
        public bool Unlocked;
    }

    /// <summary>Client command: purchase one expansion. The host validates and applies it, then
    /// answers with the post-purchase state and notifies the other peers.</summary>
    [NetworkMessage]
    public sealed class ExpansionPurchaseMessage : ActionMessage
    {
        public byte Kind;

        /// <summary>The local pre-purchase snapshot. Restored when the host refuses the
        /// purchase.</summary>
        [NonSerialized]
        internal ExpansionClientBehaviour.PurchaseCapture Capture;

        public override void OnUndo(ActionResponseMessage response)
        {
            ExpansionClientBehaviour.Rollback(Capture);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            ExpansionClientBehaviour.ApplyResponse(response);
            Capture = null;
        }
    }
}
