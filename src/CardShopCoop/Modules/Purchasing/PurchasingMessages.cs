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

    /// <summary>A predicted checkout. The client already ran the vanilla checkout; a host refusal
    /// reverses that local prediction and the host's answer settles it.</summary>
    public abstract class PurchaseIntent : ActionMessage
    {
        public override void OnUndo(ActionResponseMessage response)
            => PurchasingClientBehaviour.RejectSubmission(ActionId);

        public override void OnMerge(ActionResponseMessage response)
            => PurchasingClientBehaviour.AcceptSubmission(ActionId, response);
    }

    /// <summary>A restock cart checkout, from the vanilla restock screen or the scanner.</summary>
    [NetworkMessage]
    public sealed class RestockPurchaseIntent : PurchaseIntent
    {
        public bool ScannerCheckout;
        public List<PurchaseLine> Lines = new();
    }

    [NetworkMessage]
    public sealed class FurniturePurchaseIntent : PurchaseIntent
    {
        public List<PurchaseLine> Lines = new();
    }

    [NetworkMessage]
    public sealed class ProductLicensePurchaseIntent : PurchaseIntent
    {
        public List<PurchaseLine> Lines = new();
    }

    [NetworkMessage]
    public sealed class ScannerLicensePurchaseIntent : PurchaseIntent
    {
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

        /// <summary>The ids this peer minted for the physical boxes this line's shipment spawns,
        /// in spawn order. The client's vanilla checkout creates those boxes before the host can
        /// accept the intent, so the ids ride on the shipment and the host binds its own boxes to
        /// them. The host validates every id before it delivers anything.</summary>
        public List<Guid> BoxNetworkIds = new();
    }

    /// <summary>The host's answer to one purchase intent: the accepted checkout's result, or a
    /// rejection carrying the same actor-only result so the client can settle its prediction.</summary>
    [NetworkMessage]
    public sealed class PurchaseOutcomeMessage : ActionResponseMessage
    {
        public PurchaseKind Kind;
        public bool Success;
        public string Text = "";

        /// <summary>The minted box ids the host did not create (a partial or full rejection of
        /// this shipment's boxes). The minting client destroys exactly these local boxes; boxes
        /// absent from the list were created and stay. Empty on a fully accepted shipment.</summary>
        public List<Guid> RejectedBoxIds = new();
    }
}
