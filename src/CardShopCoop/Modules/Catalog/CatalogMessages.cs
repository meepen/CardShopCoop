using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Complete host-owned catalog entitlement state.</summary>
    [NetworkMessage]
    public sealed class CatalogLicenseStateMessage : INetMessage
    {
        public bool ScannerUnlocked;
        public List<CatalogLicenseEntry> Entries = new();
    }

    [NetworkMessage]
    public sealed class CatalogLicenseDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public bool Scanner;
        public bool Unlocked;
        public EItemType ItemType;
        public bool IsBigBox;
        public string Name = "";
    }

    public sealed class CatalogLicenseEntry
    {
        public EItemType ItemType;
        public bool IsBigBox;
        public string Name = "";
    }
}
