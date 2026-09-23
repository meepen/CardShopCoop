using System;
using CardShopCoop.Modules.Prediction;
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

    /// <summary>0 = shop rooms, 1 = warehouse rooms, 2 = warehouse room unlock.</summary>
    [NetworkMessage]
    public sealed class ExpansionDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Area;
        public int Count;
        public bool Unlocked;
    }

    /// <summary>One client expansion purchase intent.</summary>
    [NetworkMessage]
    public sealed class ExpansionPurchaseMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Kind;
    }
}
