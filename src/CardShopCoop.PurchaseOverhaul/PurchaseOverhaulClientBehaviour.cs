using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>
    /// Guest half of Purchase Overhaul co-op support. It intercepts the mod's own order and
    /// licence funnels before they mutate the local wallet or delivery queue, sends one command to
    /// the host, and applies the host's answer through OnMerge/OnUndo.
    /// </summary>
    [ClientBehaviour]
    public sealed class PurchaseOverhaulClientBehaviour : CoopBehaviour
    {
        private static PurchaseOverhaulClientBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;
        private bool _pending;

        internal static PurchaseOverhaulClientBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
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

            _initialized = true;
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _) => _joined = true;

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection _, DisconnectInfo __)
        {
            _joined = false;
            _pending = false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _joined = false;
            _pending = false;
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

        private static bool BuyLicensePrefix(object product, out string message,
            ref bool __result)
        {
            var active = _active;
            if (active == null || !PurchaseOverhaulInterop.ShouldRoute)
            {
                message = null;
                return true;
            }

            __result = active.TrySendLicense(product, out message);
            return false;
        }

        private bool CanSend(out string message)
        {
            if (!_joined || Context == null || !Context.InGame)
            {
                message = "Waiting for co-op to finish joining.";
                return false;
            }

            if (Context.HostConnection == null)
            {
                message = "Not connected to the host.";
                return false;
            }

            if (_pending)
            {
                message = "Waiting for the host to confirm the last purchase.";
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

            _pending = true;
            try
            {
                Context.Send(Context.HostConnection, request);
            }
            catch (Exception error)
            {
                _pending = false;
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

            _pending = true;
            try
            {
                Context.Send(Context.HostConnection, request);
            }
            catch (Exception error)
            {
                _pending = false;
                message = "The licence request could not be sent: " + error.Message;
                CoopLog.Warn(message);
                return false;
            }

            message = "Licence request sent to the host.";
            return true;
        }

        internal void CompleteOrder(ActionResponseMessage response)
        {
            _pending = false;
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
            _pending = false;
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
