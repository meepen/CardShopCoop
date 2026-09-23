using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Host-owned physical access signs and their gameplay booleans.</summary>
    [NetworkMessage]
    public sealed class StoreAccessStateMessage : INetMessage
    {
        public bool Full = true;
        public bool Animate;
        public bool IsShopOpen;
        public bool IsWarehouseDoorClosed;
    }

    [NetworkMessage]
    public sealed class StoreAccessDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool HasShopOpen;
        public bool IsShopOpen;
        public bool HasWarehouseDoorClosed;
        public bool IsWarehouseDoorClosed;
        public bool Animate;
    }

    [NetworkMessage]
    public sealed class StoreAccessToggleMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        /// <summary>Zero is the shop sign; one is the warehouse customer-entry sign.</summary>
        public byte Which;
    }

}
