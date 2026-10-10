using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Purchasing
{
    /// <summary>
    /// Guest checkout capture. A checkout is one intent and one send. The client plays the vanilla
    /// checkout like any other player; the postfix observes the one purchase it performed and
    /// forwards it. The host owns the authoritative wallet, items and entitlements, so it accepts
    /// (the optimistic local change already matches) or rejects (the prediction is rolled back).
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
            internal PurchaseIntentMessage Message;
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
            // The creator-assigned ids for the physical boxes this checkout's vanilla flow will
            // spawn (carried in the intent), and the delivery-count window used to cancel the
            // not-yet-spawned tail on a rejection.
            internal List<Guid> BoxIds = new();
            internal int StartDeliveries;
            internal int AppendedDeliveries;
            // The report cost before vanilla runs, captured so the postfix can recover the exact
            // wallet spend the local checkout queued. A rejected prediction refunds this; a replay
            // re-charges it.
            internal float SupplyCostBefore;
            internal float UpgradeCostBefore;
            internal double Spent;
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
                _harmony = new Harmony("dev.meepen.cardshopcoop.purchasing.client");
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
            // The host accepted the purchase: its descriptors carry the very ids this checkout
            // pre-assigned, so the local boxes bind to them as they spawn.
            // Confirm, not AckOrApply: retire the prediction AND always apply the local
            // bookkeeping (clearing the cart / closing the confirmation). Vanilla normally did
            // that already, and the apply is idempotent, but if this peer's checkout charged
            // without clearing (or a later rollback restored the cart), skipping it strands a
            // cart that every later click would charge again. The wallet charge stays separate:
            // vanilla already charged, so only a replay after a rollback re-charges.
            PredictionApi.Confirm(outcome.PredictionId, () =>
                ApplyAccepted(pending));
            // The client runs the vanilla checkout like any other player, so the game itself
            // already applied the product-license entitlement side effects (achievement and the
            // UnlockBasicCardBox tutorial task) and played SFX_CustomerBuy for its local
            // purchase. Re-applying the side effects here would double-count the tutorial credit.
            // A license the host accepted for a checkout vanilla refused locally is applied by
            // the authoritative CatalogLicenseDeltaMessage, which owns the once-guard.
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

        /// <summary>Marks the vanilla checkout's wallet/experience side effects as owned by the
        /// forwarded purchase intent: the host charges the accepted intent exactly once, so the Hud
        /// economy observer must not also forward the guest's local vanilla events.</summary>
        private static void EnterChargeScope(PendingRequest state)
        {
            if (state != null)
                EconomyActionScope.Enter();
        }

        private static void ReleaseChargeScope(PendingRequest state)
        {
            if (state != null)
                EconomyActionScope.Exit();
        }

        private PendingRequest CaptureRestockCheckout(RestockItemCheckoutScreen checkout)
        {
            if (!CanCapture("restock checkout"))
                return null;

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

            return BuildPending(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Restock,
                ScannerCheckout = false,
                Lines = lines,
            }, PendingSurface.Restock, screen, null, null, null, -1);
        }

        private PendingRequest CaptureScanner(ScannerRestockScreen screen)
        {
            if (!CanCapture("scanner checkout"))
                return null;

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

            return BuildPending(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Restock,
                ScannerCheckout = true,
                Lines = lines,
            }, PendingSurface.Scanner, null, screen, null, null, -1);
        }

        /// <summary>Captures one furniture checkout at the engine, the game's single mutation
        /// point for a furniture purchase. Vanilla reaches the engine only from the game's own
        /// confirmation screen (which is still open when it calls in and closes itself
        /// afterwards), so when that screen is open for this index the capture records it for the
        /// close/reopen bookkeeping. A replacement shop UI - Furniture Overhaul's page - calls
        /// the engine directly and never opens the confirmation screen, and the same capture
        /// forwards it.</summary>
        private PendingRequest CaptureFurniture(FurnitureShopUIScreen owner, int index)
        {
            if (!CanCapture("furniture checkout"))
                return null;

            var data = index >= 0 ? InventoryBase.GetFurniturePurchaseData(index) : null;
            if (data == null)
                return null;

            var confirmation = PurchasingInterop.OpenFurnitureConfirmation(owner, index);
            if (confirmation == null)
            {
                CoopPlugin.Log.LogInfo("Purchasing captured a furniture checkout that bypassed "
                    + "the confirmation screen (index " + index + ", type "
                    + (int)data.objectType + ").");
            }

            return BuildPending(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.Furniture,
                Lines = new List<PurchaseLine>
                {
                    new PurchaseLine { ObjectType = data.objectType, Count = 1 },
                },
            }, PendingSurface.Furniture, null, null, confirmation, owner, index);
        }

        private PendingRequest CaptureProductLicense(RestockItemPanelUI panel)
        {
            if (!CanCapture("product license checkout"))
                return null;

            var index = PurchasingInterop.PanelIndex(panel);
            var line = PurchasingInterop.RestockLine(index, 1);
            return BuildPending(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.ProductLicense,
                Lines = new List<PurchaseLine> { line },
            }, PendingSurface.ProductLicense, null, null, null, null, index);
        }

        private PendingRequest CaptureScannerLicense(ScannerRestockScreen screen)
        {
            if (!CanCapture("scanner unlock"))
                return null;

            return BuildPending(new PurchaseIntentMessage
            {
                Kind = PurchaseKind.ScannerLicense,
                Lines = new List<PurchaseLine> { new PurchaseLine { Count = 1 } },
            }, PendingSurface.ScannerLicense, null, screen, null, null, -1);
        }

        private PendingRequest BuildPending(PurchaseIntentMessage message, PendingSurface surface,
            RestockItemScreen restock, ScannerRestockScreen scanner,
            FurnitureShopConfirmPurchaseScreen furniture, FurnitureShopUIScreen owner, int localIndex)
        {
            var pending = new PendingRequest
            {
                Message = message,
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

            // Every checkout surface decrements a report cost synchronously (supplyCost for
            // restock, upgradeCost for furniture/licenses) before queueing its ReduceCoin, so this
            // snapshot is the reliable "did it charge and how much" baseline.
            pending.SupplyCostBefore = CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            pending.UpgradeCostBefore = CPlayerData.m_GameReportDataCollectPermanent.upgradeCost;

            // Restock, scanner, and furniture checkouts spawn physical package boxes through the
            // game's own factory before the host can accept or reject the intent. The client is
            // the creator: it assigns each box's stable id now, carries them in the intent, and
            // consumes them as the boxes spawn. Licenses do not create a package.
            if (surface == PendingSurface.Restock || surface == PendingSurface.Scanner
                || surface == PendingSurface.Furniture)
            {
                pending.StartDeliveries = WorldClientBehaviour.PendingClientDeliveryCount();
                for (var i = 0; i < message.Lines.Count; i++)
                {
                    var line = message.Lines[i];
                    var count = surface == PendingSurface.Furniture ? 1 : Math.Max(line.Count, 0);
                    for (var b = 0; b < count; b++)
                    {
                        var id = Guid.NewGuid();
                        line.BoxNetworkIds.Add(id);
                        pending.BoxIds.Add(id);
                    }
                }

                if (surface == PendingSurface.Furniture)
                {
                    // A furniture checkout spawns its single package synchronously.
                    if (pending.BoxIds.Count > 0)
                    {
                        WorldClientBehaviour.PushClientCreatedId(pending.BoxIds[0]);
                    }
                }
                else
                {
                    // A restock/scanner checkout queues deliveries that RestockManager.Update drains.
                    WorldClientBehaviour.PushClientDeliveryIds(pending.BoxIds);
                }
            }

            return pending;
        }

        /// <summary>Registers the one purchase the vanilla checkout already performed. The local
        /// wallet/items/licenses changed through the game's own path, so the prediction sends the
        /// intent without applying anything; the host confirms it (Ack) or rolls it back with the
        /// generic rollback, which reopens the cart/confirmation.</summary>
        private void Observe(PendingRequest pending)
        {
            if (pending == null || _context?.InGame() != true)
                return;

            // Close the checkout's synchronous enqueue window before anything can drain the
            // delivery queue, so the rejection knows exactly what this checkout appended.
            pending.AppendedDeliveries = Math.Max(
                WorldClientBehaviour.PendingClientDeliveryCount() - pending.StartDeliveries, 0);

            // If the checkout was refused locally (or queued no delivery), its pre-assigned ids
            // will never spawn; release them so the host's descriptors materialize normally.
            if (pending.Surface == PendingSurface.Furniture || pending.AppendedDeliveries == 0)
            {
                WorldClientBehaviour.ReleaseUnspawnedClientIds(pending.BoxIds);
            }

            // The vanilla checkout already spent the guest's mirror before this postfix. Recover
            // the exact amount so a rejection can refund it and a replay can re-charge it.
            pending.Spent = (pending.SupplyCostBefore
                - CPlayerData.m_GameReportDataCollectPermanent.supplyCost)
                + (pending.UpgradeCostBefore
                    - CPlayerData.m_GameReportDataCollectPermanent.upgradeCost);

            // A checkout vanilla REFUSED locally (not enough money, empty cart, already
            // unlocked) charged nothing and cleared nothing. Forwarding it would let the host
            // charge and create boxes for a purchase the player never made - and because the
            // cart stayed full, every later click would charge again. There is nothing to
            // reconcile: release the pre-assigned ids that will never spawn and stop.
            if (pending.Spent <= 0.0001d)
            {
                WorldClientBehaviour.ReleaseUnspawnedClientIds(pending.BoxIds);
                return;
            }

            var message = pending.Message;
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
                        WorldClientBehaviour.RollbackClientCreated(pending.BoxIds);
                        WorldClientBehaviour.CancelPendingClientDeliveries(pending.AppendedDeliveries);
                        throw;
                    }
                },
                () => ReplayAccepted(pending),
                () => Undo(pending));
        }

        /// <summary>A reconcile replay of an accepted purchase: redo the local bookkeeping and
        /// mirror the wallet debit, because <see cref="Undo"/> refunded it while rolling the
        /// layer back. The accepted-outcome path must NOT charge (vanilla already did).</summary>
        private static void ReplayAccepted(PendingRequest pending)
        {
            ApplyAccepted(pending);
            Charge(pending.Spent);
        }

        private bool CanCapture(string action)
        {
            if (_context?.InGame() == true)
                return true;

            CoopPlugin.Log.LogWarning("Purchasing skipped " + action + ": the shop is not ready");
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

            // No wallet charge here: vanilla already charged the local mirror for the accepted
            // purchase, and the idempotent cart/screen bookkeeping above is all that may still be
            // needed. A replay after a rollback charges explicitly in ReplayAccepted.
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
                    // A rejected checkout reopens the game's confirmation screen so the player can
                    // retry; a checkout that bypassed it (a replacement shop UI) has nothing to
                    // reopen.
                    if (pending.FurnitureScreen != null)
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

            // The vanilla checkout already debited the guest's mirror (the Hud economy observer was
            // suppressed by EconomyActionScope), and the host owns the authoritative charge. A
            // rejection must refund that local debit through the game's own coin event so the
            // balance is right immediately rather than until the next authoritative wallet delta.
            Refund(pending.Spent);

            // The host sent no descriptor, so the checkout's own package boxes will never bind.
            // Destroy them and cancel their not-yet-spawned deliveries through the game path.
            WorldClientBehaviour.RollbackClientCreated(pending.BoxIds);
            WorldClientBehaviour.CancelPendingClientDeliveries(pending.AppendedDeliveries);
        }

        /// <summary>Re-applies the wallet debit of a replayed checkout through the game's own coin
        /// event. Queued during reconciliation, so the Hud economy observer does not forward it as a
        /// second contribution.</summary>
        private static void Charge(double amount)
        {
            if (amount > 0.0001d)
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)amount));
        }

        /// <summary>Reverses the wallet debit of a rejected checkout through the game's own coin
        /// event.</summary>
        private static void Refund(double amount)
        {
            if (amount > 0.0001d)
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)amount, true));
        }

        private void ShowStatus(string text)
        {
            _context?.SetStatusLine?.Invoke(text, 8f);
        }

        [HarmonyPatch(typeof(RestockItemCheckoutScreen), "OnPressConfirmCheckout")]
        private static class RestockCheckoutPatch
        {
            // Capture the cart before vanilla clears it; the postfix observes the one checkout the
            // game already performed.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(RestockItemCheckoutScreen __instance, out PendingRequest __state)
            {
                __state = _active?.CaptureRestockCheckout(__instance);
                EnterChargeScope(__state);
            }

            [HarmonyPostfix]
            private static void Postfix(PendingRequest __state) => _active?.Observe(__state);

            [HarmonyFinalizer]
            private static void Finalizer(PendingRequest __state) => ReleaseChargeScope(__state);
        }

        [HarmonyPatch(typeof(ScannerRestockScreen), "OnPressCheckoutButton")]
        private static class ScannerCheckoutPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ScannerRestockScreen __instance, out PendingRequest __state)
            {
                __state = _active?.CaptureScanner(__instance);
                EnterChargeScope(__state);
            }

            [HarmonyPostfix]
            private static void Postfix(PendingRequest __state) => _active?.Observe(__state);

            [HarmonyFinalizer]
            private static void Finalizer(PendingRequest __state) => ReleaseChargeScope(__state);
        }

        /// <summary>The furniture checkout engine, the game's single mutation point for a
        /// furniture purchase. Vanilla reaches it only from <c>FurnitureShopConfirmPurchaseScreen</c>
        /// (still open when it calls in; it closes itself afterwards), and a replacement shop UI
        /// calls it directly. One capture covers both; <see cref="CaptureFurniture"/> records the
        /// open confirmation screen so an acceptance can close it and a rejection can reopen it,
        /// and both stay absent for a bypassing UI.</summary>
        [HarmonyPatch(typeof(FurnitureShopUIScreen), "EvaluateCartCheckout")]
        private static class FurnitureCheckoutPatch
        {
            // Capture before vanilla charges and spawns; the scope suppresses the Hud economy
            // observer so the host's accepted intent is the only charge.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(FurnitureShopUIScreen __instance, int index,
                out PendingRequest __state)
            {
                __state = _active?.CaptureFurniture(__instance, index);
                EnterChargeScope(__state);
            }

            [HarmonyPostfix]
            private static void Postfix(PendingRequest __state) => _active?.Observe(__state);

            [HarmonyFinalizer]
            private static void Finalizer(PendingRequest __state) => ReleaseChargeScope(__state);
        }

        [HarmonyPatch(typeof(RestockItemPanelUI), "OnPressPurchaseButton")]
        private static class ProductLicensePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(RestockItemPanelUI __instance, out PendingRequest __state)
            {
                __state = _active?.CaptureProductLicense(__instance);
                EnterChargeScope(__state);
            }

            [HarmonyPostfix]
            private static void Postfix(PendingRequest __state) => _active?.Observe(__state);

            [HarmonyFinalizer]
            private static void Finalizer(PendingRequest __state) => ReleaseChargeScope(__state);
        }

        [HarmonyPatch(typeof(ScannerRestockScreen), "OnPressUnlockButton")]
        private static class ScannerLicensePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ScannerRestockScreen __instance, out PendingRequest __state)
            {
                __state = _active?.CaptureScannerLicense(__instance);
                EnterChargeScope(__state);
            }

            [HarmonyPostfix]
            private static void Postfix(PendingRequest __state) => _active?.Observe(__state);

            [HarmonyFinalizer]
            private static void Finalizer(PendingRequest __state) => ReleaseChargeScope(__state);
        }
    }
}
