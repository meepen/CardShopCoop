using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using HarmonyLib;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>
    /// Guest half of Purchase Overhaul co-op support. It intercepts the mod's own order and
    /// licence funnels before they mutate the local wallet or delivery queue, sends one command to
    /// the host, and applies the host's answer in the action's OnMerge.
    /// </summary>
    [ClientBehaviour]
    public sealed class PurchaseOverhaulClientBehaviour : CoopBehaviour
    {
        private static PurchaseOverhaulClientBehaviour _active;

        internal static PurchaseOverhaulClientBehaviour Active => _active;

        private void OnEnable()
        {
            if (_active != null)
            {
                return;
            }

            try
            {
                _active = this;
                ApplyPatches();
            }
            catch
            {
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                throw;
            }
        }

        internal void Shutdown()
        {
            if (!ReferenceEquals(_active, this))
            {
                return;
            }

            _active = null;
        }

        private void OnDestroy() => Shutdown();

        private void ApplyPatches()
        {
            if (!PurchaseOverhaulInterop.Available)
            {
                return;
            }

            var order = TryPatch(PurchaseOverhaulInterop.CheckoutSurface, "Place",
                prefix: new HarmonyMethod(typeof(PurchaseOverhaulClientBehaviour),
                    nameof(PlacePrefix)),
                args: new[] { typeof(string).MakeByRefType() });
            var licence = TryPatch(PurchaseOverhaulInterop.CheckoutSurface, "BuyLicense",
                prefix: new HarmonyMethod(typeof(PurchaseOverhaulClientBehaviour),
                    nameof(BuyLicensePrefix)),
                args: new[]
                {
                    PurchaseOverhaulInterop.ProductSurface,
                    typeof(string).MakeByRefType(),
                });
            if (order && licence)
            {
                CoopLog.Info("Purchase Overhaul co-op bridge installed.");
            }
            else
            {
                CoopLog.Warn("Purchase Overhaul co-op bridge is incomplete (order=" + order
                    + ", licence=" + licence + "); some purchases may stay local.");
            }
        }

        private static bool PlacePrefix(out string message, ref bool __result)
        {
            var active = _active;
            if (active == null || !PurchaseOverhaulInterop.ShouldRoute)
            {
                message = null;
                return true;
            }

            __result = active.TrySendOrder(out message);
            return false;
        }

        private static bool BuyLicensePrefix(object p, out string message,
            ref bool __result)
        {
            var active = _active;
            if (active == null || !PurchaseOverhaulInterop.ShouldRoute)
            {
                message = null;
                return true;
            }

            __result = active.TrySendLicense(p, out message);
            return false;
        }

        private bool CanSend(out string message)
        {
            if (Context == null || !Context.InGame)
            {
                message = "Waiting for co-op to finish joining.";
                return false;
            }

            if (Context.Host == null)
            {
                message = "Not connected to the host.";
                return false;
            }

            message = null;
            return true;
        }

        private bool TrySendOrder(out string message)
        {
            if (!CanSend(out message))
            {
                return false;
            }

            if (!PurchaseOverhaulInterop.ReadOrder(out var productIds, out var boxes,
                out message))
            {
                return false;
            }

            var request = new PurchaseOrderRequest
            {
                ProductIds = productIds,
                Boxes = boxes,
            };

            try
            {
                Context.Send(request);
            }
            catch (Exception error)
            {
                message = "The order could not be sent: " + error.Message;
                CoopLog.Warn(message);
                return false;
            }

            message = "Order sent to the host.";
            return true;
        }

        private bool TrySendLicense(object product, out string message)
        {
            if (!CanSend(out message))
            {
                return false;
            }

            if (product == null)
            {
                message = "No item.";
                return false;
            }

            var id = PurchaseOverhaulInterop.ProductIdOf(product);
            if (string.IsNullOrEmpty(id))
            {
                message = PurchaseOverhaulInterop.NameOf(product)
                    + " cannot be licensed in co-op.";
                return false;
            }

            var request = new PurchaseLicenseRequest
            {
                ProductId = id,
            };

            try
            {
                Context.Send(request);
            }
            catch (Exception error)
            {
                message = "The licence request could not be sent: " + error.Message;
                CoopLog.Warn(message);
                return false;
            }

            message = "Licence request sent to the host.";
            return true;
        }

        internal void CompleteOrder(ActionResponseMessage response)
        {
            var result = response as PurchaseOrderResult;
            if (result != null && result.Success && !result.Rejected)
            {
                PurchaseOverhaulInterop.RemoveOrderedLines(result.ProductIds, result.Boxes);
                PurchaseOverhaulInterop.ShowToast(result.Message);
                return;
            }

            PurchaseOverhaulInterop.ShowToast(result?.Message ?? "The host rejected the order.");
        }

        internal void CompleteLicense(ActionResponseMessage response)
        {
            var result = response as PurchaseLicenseResult;
            if (result != null && result.Success && !result.Rejected)
            {
                PurchaseOverhaulInterop.RefreshCatalogue();
                PurchaseOverhaulInterop.ShowToast(result.Message);
                return;
            }

            PurchaseOverhaulInterop.ShowToast(result?.Message ?? "The host rejected the licence.");
        }
    }
}
