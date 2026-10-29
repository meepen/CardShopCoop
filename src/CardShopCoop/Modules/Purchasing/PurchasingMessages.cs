using System.Collections.Generic;
using System;
using CardShopCoop.Api;
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

    /// <summary>A restock cart checkout, from the vanilla restock screen or the scanner.</summary>
    [NetworkMessage]
    public sealed class RestockPurchaseIntent : ActionMessage
    {

        public bool ScannerCheckout;
        public List<PurchaseLine> Lines = new();

        /// <summary>The host's answer, produced by <see cref="OnApply"/> and published by the
        /// module's handler. Not serialized.</summary>
        [NonSerialized]
        public PurchaseOutcomeMessage Result;

        public override void OnApply()
            => Result = PurchasingHostBehaviour.Active?.ApplyRestockPurchase(this);

        /// <summary>The client registers its own checkout undo closure with the ledger, so the
        /// message itself never reverses a prediction.</summary>
        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    [NetworkMessage]
    public sealed class FurniturePurchaseIntent : ActionMessage
    {

        public List<PurchaseLine> Lines = new();

        /// <summary>The host's answer, produced by <see cref="OnApply"/> and published by the
        /// module's handler. Not serialized.</summary>
        [NonSerialized]
        public PurchaseOutcomeMessage Result;

        public override void OnApply()
            => Result = PurchasingHostBehaviour.Active?.ApplyFurniturePurchase(this);

        /// <summary>The client registers its own checkout undo closure with the ledger, so the
        /// message itself never reverses a prediction.</summary>
        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    [NetworkMessage]
    public sealed class ProductLicensePurchaseIntent : ActionMessage
    {

        public List<PurchaseLine> Lines = new();

        /// <summary>The host's answer, produced by <see cref="OnApply"/> and published by the
        /// module's handler. Not serialized.</summary>
        [NonSerialized]
        public PurchaseOutcomeMessage Result;

        public override void OnApply()
            => Result = PurchasingHostBehaviour.Active?.ApplyProductLicensePurchase(this);

        /// <summary>The client registers its own checkout undo closure with the ledger, so the
        /// message itself never reverses a prediction.</summary>
        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    [NetworkMessage]
    public sealed class ScannerLicensePurchaseIntent : ActionMessage
    {

        public List<PurchaseLine> Lines = new();

        /// <summary>The host's answer, produced by <see cref="OnApply"/> and published by the
        /// module's handler. Not serialized.</summary>
        [NonSerialized]
        public PurchaseOutcomeMessage Result;

        public override void OnApply()
            => Result = PurchasingHostBehaviour.Active?.ApplyScannerLicensePurchase(this);

        /// <summary>The client registers its own checkout undo closure with the ledger, so the
        /// message itself never reverses a prediction.</summary>
        public override void OnUndo(ActionResponseMessage response)
        {
        }
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

        /// <summary>The ids this peer minted for the physical boxes this line's shipment spawns,
        /// in spawn order. The client's vanilla checkout creates those boxes before the host can
        /// accept the intent, so the ids ride on the shipment and the host binds its own boxes to
        /// them. The host validates every id before it delivers anything.</summary>
        public List<Guid> BoxNetworkIds = new();
    }

    /// <summary>The host's answer to one accepted purchase intent. A rejection is the framework's
    /// generic rollback, so this message is only ever sent for an accepted checkout.</summary>
    [NetworkMessage]
    public sealed class PurchaseOutcomeMessage : INetMessage
    {
        /// <summary>The purchase intent this outcome answers.</summary>
        public Guid ActionId;

        public PurchaseKind Kind;
        public bool Success;
        public string Text = "";

        /// <summary>The minted box ids the host did not create (a partial or full rejection of
        /// this shipment's boxes). The minting client destroys exactly these local boxes; boxes
        /// absent from the list were created and stay. Empty on a fully accepted shipment.</summary>
        public List<Guid> RejectedBoxIds = new();
    }
}
