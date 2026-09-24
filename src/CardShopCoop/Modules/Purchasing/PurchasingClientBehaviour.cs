using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Purchasing
{
    /// <summary>
    /// Guest checkout capture. A checkout is one intent and one send. The host owns every game
    /// mutation while locally visible UI changes are predicted and reconciled by the outcome.
    /// </summary>
    [ClientBehaviour]
    public sealed class PurchasingClientBehaviour : CoopBehaviour
    {
        private enum PendingSurface : byte
        {
            Restock,
            Scanner,
            Furniture,
            ProductLicense,
            ScannerLicense,
        }

        private sealed class PendingRequest
        {
            internal PendingSurface Surface;
            internal RestockItemScreen RestockScreen;
            internal ScannerRestockScreen ScannerScreen;
            internal FurnitureShopConfirmPurchaseScreen FurnitureScreen;
            internal FurnitureShopUIScreen FurnitureOwner;
            internal int LocalIndex = -1;
            internal Dictionary<int, int> RestockQuantities;
            internal List<KeyValuePair<int, int>> ScannerQuantities;
            internal Dictionary<int, int> RestockBefore;
            internal List<KeyValuePair<int, int>> ScannerBefore;
        }

        private static PurchasingClientBehaviour _active;
        private readonly Dictionary<Guid, PendingRequest> _pendingRequests = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                PredictionApi.PredictionRetired += RetirePendingRequest;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.purchasing.client");
                Patch(typeof(RestockCheckoutPatch));
                Patch(typeof(ScannerCheckoutPatch));
                Patch(typeof(FurnitureCheckoutPatch));
                Patch(typeof(ProductLicensePatch));
                Patch(typeof(ScannerLicensePatch));
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                PredictionApi.PredictionRetired -= RetirePendingRequest;
                if (ReferenceEquals(_active, this))
                    _active = null;
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(PurchaseOutcomeMessage))]
        private void HandleOutcome(MessageContext context, PurchaseOutcomeMessage outcome)
        {
            if (_shutdown)
                return;

            if (!outcome.Success || !_pendingRequests.TryGetValue(outcome.PredictionId,
                out var pending))
            {
                return;
            }

            _pendingRequests.Remove(outcome.PredictionId);
            // Reached only for an accepted purchase (failures come back as a rollback), so this
            // confirms the prediction; reconciling would reopen the cart/confirmation first.
            PredictionApi.ApplyConfirmed(outcome.PredictionId, () =>
                ApplyAccepted(pending));
            if (pending.Surface == PendingSurface.ProductLicense)
                CatalogApi.ApplyClientProductEntitlementSideEffects(pending.LocalIndex);
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            ShowStatus(outcome.Text);
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            PredictionApi.PredictionRetired -= RetirePendingRequest;
            _pendingRequests.Clear();
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void RetirePendingRequest(Guid predictionId)
            => _pendingRequests.Remove(predictionId);

        private void Patch(Type type)
            => _harmony.CreateClassProcessor(type).Patch();

        private bool InterceptRestockCheckout(RestockItemCheckoutScreen checkout)
        {
            if (!CanCapture("restock checkout"))
                return false;

            var screen = PurchasingInterop.RestockScreen(checkout);
            if (screen == null)
                throw new InvalidOperationException("Purchasing could not read the restock checkout screen.");

            var cart = PurchasingInterop.RestockCart(screen);
            var lines = new List<PurchaseLine>();
            if (cart != null)
            {
                foreach (var entry in cart)
                    lines.Add(PurchasingInterop.RestockLine(entry.Key, entry.Value));
            }

            Queue(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Restock,
                ScannerCheckout = false,
                Lines = lines,
            }, PendingSurface.Restock, screen, null, null, null, -1);
            return false;
        }

        private bool InterceptScanner(ScannerRestockScreen screen)
        {
            if (!CanCapture("scanner checkout"))
                return false;

            var indexes = PurchasingInterop.ScannerIndexes(screen);
            var counts = PurchasingInterop.ScannerCounts(screen);
            var lines = new List<PurchaseLine>();
            var length = Math.Max(indexes?.Count ?? 0, counts?.Count ?? 0);
            for (var i = 0; i < length; i++)
            {
                var index = indexes != null && i < indexes.Count ? indexes[i] : -1;
                var count = counts != null && i < counts.Count ? counts[i] : -1;
                lines.Add(PurchasingInterop.RestockLine(index, count));
            }

            Queue(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Restock,
                ScannerCheckout = true,
                Lines = lines,
            }, PendingSurface.Scanner, null, screen, null, null, -1);
            return false;
        }

        private bool InterceptFurniture(FurnitureShopConfirmPurchaseScreen screen)
        {
            if (!CanCapture("furniture checkout"))
                return false;

            var index = PurchasingInterop.FurnitureIndex(screen);
            var owner = PurchasingInterop.FurnitureOwner(screen);
            var data = index >= 0 ? InventoryBase.GetFurniturePurchaseData(index) : null;
            Queue(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Furniture,
                Lines = new List<PurchaseLine>
                {
                    new PurchaseLine { ObjectType = data?.objectType ?? EObjectType.None, Count = 1 },
                },
            }, PendingSurface.Furniture, null, null, screen, owner, index);
            return false;
        }

        private bool InterceptProductLicense(RestockItemPanelUI panel)
        {
            if (!CanCapture("product license checkout"))
                return false;

            var index = PurchasingInterop.PanelIndex(panel);
            var line = PurchasingInterop.RestockLine(index, 1);
            Queue(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.ProductLicense,
                Lines = new List<PurchaseLine> { line },
            }, PendingSurface.ProductLicense, null, null, null, null, index);
            return false;
        }

        private bool InterceptScannerLicense(ScannerRestockScreen screen)
        {
            if (!CanCapture("scanner unlock"))
                return false;

            Queue(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.ScannerLicense,
                Lines = new List<PurchaseLine> { new PurchaseLine { Count = 1 } },
            }, PendingSurface.ScannerLicense, null, screen, null, null, -1);
            return false;
        }

        private void Queue(PurchaseIntentMessage message, PendingSurface surface, RestockItemScreen restock,
            ScannerRestockScreen scanner, FurnitureShopConfirmPurchaseScreen furniture,
            FurnitureShopUIScreen owner, int localIndex)
        {
            var pending = new PendingRequest
            {
                Surface = surface,
                RestockScreen = restock,
                ScannerScreen = scanner,
                FurnitureScreen = furniture,
                FurnitureOwner = owner,
                LocalIndex = localIndex,
            };

            if (surface == PendingSurface.Restock && restock != null)
            {
                var cart = PurchasingInterop.RestockCart(restock);
                pending.RestockBefore = cart == null ? null : new Dictionary<int, int>(cart);
                pending.RestockQuantities = cart == null ? null : new Dictionary<int, int>(cart);
            }
            else if (surface == PendingSurface.Scanner && scanner != null)
            {
                var indexes = PurchasingInterop.ScannerIndexes(scanner);
                var counts = PurchasingInterop.ScannerCounts(scanner);
                pending.ScannerBefore = new List<KeyValuePair<int, int>>();
                pending.ScannerQuantities = new List<KeyValuePair<int, int>>();
                var length = Math.Max(indexes?.Count ?? 0, counts?.Count ?? 0);
                for (var i = 0; i < length; i++)
                {
                    var pair = new KeyValuePair<int, int>(
                        indexes != null && i < indexes.Count ? indexes[i] : -1,
                        counts != null && i < counts.Count ? counts[i] : -1);
                    pending.ScannerBefore.Add(pair);
                    pending.ScannerQuantities.Add(pair);
                }
            }

            if (_context?.InGame() != true)
                return;

            PredictionApi.Predict(
                "purchasing",
                predictionId =>
                {
                    message.PredictionId = predictionId;
                    _pendingRequests[predictionId] = pending;
                    try
                    {
                        _context.Send(1, message);
                    }
                    catch
                    {
                        _pendingRequests.Remove(predictionId);
                        throw;
                    }
                },
                () => ApplyAccepted(pending),
                () => Undo(pending));
        }

        private bool CanCapture(string action)
        {
            if (_context?.InGame() == true)
                return true;

            CoopPlugin.Log.LogWarning("Purchasing blocked " + action + ": the shop is not ready");
            return false;
        }

        private static void ApplyAccepted(PendingRequest pending)
        {
            switch (pending.Surface)
            {
                case PendingSurface.Restock:
                    if (pending.RestockScreen != null)
                        PurchasingInterop.RemoveRestockQuantities(pending.RestockScreen,
                            pending.RestockQuantities);
                    break;
                case PendingSurface.Scanner:
                    if (pending.ScannerScreen != null)
                        PurchasingInterop.RemoveScannerQuantities(pending.ScannerScreen,
                            pending.ScannerQuantities);
                    break;
                case PendingSurface.Furniture:
                    if (PurchasingInterop.IsSameFurniturePurchase(pending.FurnitureScreen,
                        pending.FurnitureOwner, pending.LocalIndex))
                        PurchasingInterop.CloseFurnitureConfirmation(pending.FurnitureScreen);
                    break;
                case PendingSurface.ProductLicense:
                    CatalogApi.ApplyClientProductLicense(pending.LocalIndex, true);
                    break;
                case PendingSurface.ScannerLicense:
                    CatalogApi.ApplyClientScannerLicense(true);
                    break;
            }
        }

        private static void Undo(PendingRequest pending)
        {
            switch (pending.Surface)
            {
                case PendingSurface.Restock:
                    if (pending.RestockScreen != null)
                        PurchasingInterop.RestoreRestockCart(pending.RestockScreen,
                            pending.RestockBefore);
                    break;
                case PendingSurface.Scanner:
                    if (pending.ScannerScreen != null)
                        PurchasingInterop.RestoreScannerCart(pending.ScannerScreen,
                            pending.ScannerBefore);
                    break;
                case PendingSurface.Furniture:
                    PurchasingInterop.ReopenFurnitureConfirmation(pending.FurnitureOwner,
                        pending.LocalIndex);
                    break;
                case PendingSurface.ProductLicense:
                    CatalogApi.ApplyClientProductLicense(pending.LocalIndex, false);
                    break;
                case PendingSurface.ScannerLicense:
                    CatalogApi.ApplyClientScannerLicense(false);
                    break;
            }
        }

        private void ShowStatus(string text)
        {
            _context?.SetStatusLine?.Invoke(text, 8f);
        }

        [HarmonyPatch(typeof(RestockItemCheckoutScreen), "OnPressConfirmCheckout")]
        private static class RestockCheckoutPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(RestockItemCheckoutScreen __instance)
                => _active == null || _active.InterceptRestockCheckout(__instance);
        }

        [HarmonyPatch(typeof(ScannerRestockScreen), "OnPressCheckoutButton")]
        private static class ScannerCheckoutPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ScannerRestockScreen __instance)
                => _active == null || _active.InterceptScanner(__instance);
        }

        [HarmonyPatch(typeof(FurnitureShopConfirmPurchaseScreen), "OnPressConfirmCheckout")]
        private static class FurnitureCheckoutPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(FurnitureShopConfirmPurchaseScreen __instance)
                => _active == null || _active.InterceptFurniture(__instance);
        }

        [HarmonyPatch(typeof(RestockItemPanelUI), "OnPressPurchaseButton")]
        private static class ProductLicensePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(RestockItemPanelUI __instance)
                => _active == null || _active.InterceptProductLicense(__instance);
        }

        [HarmonyPatch(typeof(ScannerRestockScreen), "OnPressUnlockButton")]
        private static class ScannerLicensePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ScannerRestockScreen __instance)
                => _active == null || _active.InterceptScannerLicense(__instance);
        }
    }
}
