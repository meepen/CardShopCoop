using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>Client command: place one Purchase Overhaul cart on the host. The cart is
    /// identified by Purchase Overhaul's own stable product ids; the host resolves and prices it
    /// with its own catalogue and discounts.</summary>
    [NetworkMessage]
    public sealed class PurchaseOrderRequest : ActionMessage
    {
        public List<string> ProductIds = new();
        public List<int> Boxes = new();

        /// <summary>The client never charges its own wallet, so there is no prediction to
        /// reverse. The host's answer is settled in <see cref="OnMerge"/>.</summary>
        public override void OnMerge(ActionResponseMessage response)
            => PurchaseOverhaulClientBehaviour.Active?.CompleteOrder(response);
    }

    [NetworkMessage]
    public sealed class PurchaseOrderResult : ActionResponseMessage
    {
        public bool Success;
        public string Message = "";

        /// <summary>The accepted lines, in the same shape the request carried. The client resolves
        /// them through Purchase Overhaul's own product lookup to remove exactly what the host
        /// bought; this keeps live third-party Product objects off the wire.</summary>
        public List<string> ProductIds = new();
        public List<int> Boxes = new();
    }

    /// <summary>Client command: buy one Purchase Overhaul product licence on the host.</summary>
    [NetworkMessage]
    public sealed class PurchaseLicenseRequest : ActionMessage
    {
        public string ProductId = "";

        /// <summary>The client never unlocks its own licence, so there is no prediction to
        /// reverse. The host's answer is settled in <see cref="OnMerge"/>.</summary>
        public override void OnMerge(ActionResponseMessage response)
            => PurchaseOverhaulClientBehaviour.Active?.CompleteLicense(response);
    }

    [NetworkMessage]
    public sealed class PurchaseLicenseResult : ActionResponseMessage
    {
        public bool Success;
        public string Message = "";
    }
}
