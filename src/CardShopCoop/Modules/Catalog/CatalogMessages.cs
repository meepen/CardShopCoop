using System;
using System.Collections.Generic;
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

    /// <summary>Host-authoritative catalog entitlement delta. State, not a command: the host
    /// owns every entitlement. <see cref="ActionId"/> retires the purchase prediction this delta
    /// answers.</summary>
    [NetworkMessage]
    public sealed class CatalogLicenseDeltaMessage : INetMessage
    {
        public Guid ActionId;
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
