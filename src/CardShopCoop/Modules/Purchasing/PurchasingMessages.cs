using System.Collections.Generic;
using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Purchasing
{
    public enum PurchaseKind : byte
    {
        Restock = 0,
        Furniture = 1,
        ProductLicense = 2,
        ScannerLicense = 3,
    }

    [NetworkMessage]
    public sealed class PurchaseIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public PurchaseKind Kind;
        public bool ScannerCheckout;
        public List<PurchaseLine> Lines = new();
    }

    public sealed class PurchaseLine
    {
        // EItemType.None and EObjectType.None are -1, not 0, so a default-initialised enum
        // field would carry a real product/object type. The host distinguishes product lines
        // (ObjectType None) from furniture lines (ItemType None), so both must default to None.
        public EItemType ItemType = EItemType.None;
        public EObjectType ObjectType = EObjectType.None;
        public bool IsBigBox;
        public string Name = "";
        public int Count;
    }

    [NetworkMessage]
    public sealed class PurchaseOutcomeMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public PurchaseKind Kind;
        public bool Success;
        public string Text = "";
    }
}
