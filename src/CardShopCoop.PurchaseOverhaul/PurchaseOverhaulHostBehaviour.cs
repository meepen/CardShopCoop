using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>
    /// Host half of Purchase Overhaul co-op support. The host runs the mod's own remote order and
    /// licence paths, so its catalogue, prices, discounts, wallet, deliveries and entitlement
    /// side effects stay authoritative.
    /// </summary>
    [ServerBehaviour]
    public sealed class PurchaseOverhaulHostBehaviour : CoopBehaviour
    {
        [MessageHandler]
        private ActionOutcome HandleOrder(CoopMessageContext context, PurchaseOrderRequest message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("order is missing");
            }

            if (!PurchaseOverhaulInterop.Available)
            {
                return ActionOutcome.Reject(
                    "Purchase Overhaul is not installed on the host.",
                    new PurchaseOrderResult
                    {
                        Success = false,
                        Message = "Purchase Overhaul is not installed on the host.",
                    });
            }

            if (!PurchaseOverhaulInterop.TryPlaceOrder(message.ProductIds, message.Boxes,
                out var resultText))
            {
                resultText = string.IsNullOrEmpty(resultText)
                    ? "The host refused the order."
                    : resultText;
                return ActionOutcome.Reject(resultText, new PurchaseOrderResult
                {
                    Success = false,
                    Message = resultText,
                });
            }

            resultText = string.IsNullOrEmpty(resultText) ? "Order placed." : resultText;
            CoopLog.Info("[purchaseoverhaul] host placed order: " + resultText);
            return ActionOutcome.Applied(new PurchaseOrderResult
            {
                Success = true,
                Message = resultText,
                ProductIds = message.ProductIds,
                Boxes = message.Boxes,
            });
        }

        [MessageHandler]
        private ActionOutcome HandleLicense(CoopMessageContext context,
            PurchaseLicenseRequest message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("licence request is missing");
            }

            if (!PurchaseOverhaulInterop.Available)
            {
                return ActionOutcome.Reject(
                    "Purchase Overhaul is not installed on the host.",
                    new PurchaseLicenseResult
                    {
                        Success = false,
                        Message = "Purchase Overhaul is not installed on the host.",
                    });
            }

            if (!PurchaseOverhaulInterop.TryBuyLicense(message.ProductId, out var resultText))
            {
                resultText = string.IsNullOrEmpty(resultText)
                    ? "The host refused the licence."
                    : resultText;
                return ActionOutcome.Reject(resultText, new PurchaseLicenseResult
                {
                    Success = false,
                    Message = resultText,
                });
            }

            resultText = string.IsNullOrEmpty(resultText) ? "Licence bought." : resultText;
            CoopLog.Info("[purchaseoverhaul] host bought licence: " + resultText);
            return ActionOutcome.Applied(new PurchaseLicenseResult
            {
                Success = true,
                Message = resultText,
            });
        }
    }
}
