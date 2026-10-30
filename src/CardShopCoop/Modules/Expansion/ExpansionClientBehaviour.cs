using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Expansion
{
    /// <summary>Guest expansion intent capture and authoritative state application. A guest forwards
    /// one purchase intent and then waits for the host's authoritative state; it never mutates its
    /// own expansion.</summary>
    [ClientBehaviour]
    public sealed class ExpansionClientBehaviour : CoopBehaviour
    {
        private static ExpansionClientBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;
        private ExpansionBaselineMessage _latestBaseline;
        private ExpansionDeltaMessage _pendingShopDelta;
        private ExpansionDeltaMessage _pendingWarehouseDelta;
        private ExpansionDeltaMessage _pendingWarehouseUnlockDelta;
        private bool _baselineNeedsManagerInitialization;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            try
            {
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                Harmony.CreateClassProcessor(typeof(RoomCheckoutPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(LotBCheckoutPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(ExpansionManagerReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Expansion client initialization failed: " + exception);
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            ApplyLatestState();
        }

        [MessageHandler]
        private void HandleBaseline(MessageContext context, ExpansionBaselineMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            _latestBaseline = message;
            ApplyLatestState();
        }

        [MessageHandler]
        private void HandleDelta(MessageContext context, ExpansionDeltaMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            switch (message.Area)
            {
                case 0:
                    ReplacePending(ref _pendingShopDelta, message);
                    break;
                case 1:
                    ReplacePending(ref _pendingWarehouseDelta, message);
                    break;
                case 2:
                    ReplacePending(ref _pendingWarehouseUnlockDelta, message);
                    break;
                default:
                    throw new InvalidOperationException("Unknown expansion delta area "
                        + message.Area + ".");
            }

            ApplyLatestState();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _latestBaseline = null;
            ClearPendingDeltas();
            _baselineNeedsManagerInitialization = false;
            _joined = false;
        }

        private void OnDestroy() => Shutdown();

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => ApplyLatestState();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => ApplyLatestState();

        private void OnManagerReady()
            => ApplyLatestState();

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                _latestBaseline = null;
                ClearPendingDeltas();
                _baselineNeedsManagerInitialization = false;
                _joined = false;
            }
        }

        private void ApplyLatestState()
        {
            if (_shutdown || !_joined || !RuntimeContext.InGame() || ExpansionInterop.ApplyingAuthoritativeState)
            {
                return;
            }

            var manager = ExpansionInterop.FindUnlockManager();
            if (_latestBaseline != null)
            {
                var baseline = _latestBaseline;
                ExpansionInterop.ApplyBaseline(manager, baseline);
                ExpansionInterop.RefreshOpenScreen(ExpansionInterop.FindExpansionScreen());
                _baselineNeedsManagerInitialization = manager == null;
                _latestBaseline = null;
            }

            if (manager == null)
            {
                ApplyPendingDelta(null, ref _pendingShopDelta);
                ApplyPendingDelta(null, ref _pendingWarehouseDelta);
                ApplyPendingDelta(null, ref _pendingWarehouseUnlockDelta);
                return;
            }

            if (_baselineNeedsManagerInitialization)
            {
                ExpansionInterop.InitializeRooms(manager);
                _baselineNeedsManagerInitialization = false;
            }

            ApplyPendingDelta(manager, ref _pendingShopDelta);
            ApplyPendingDelta(manager, ref _pendingWarehouseDelta);
            ApplyPendingDelta(manager, ref _pendingWarehouseUnlockDelta);
        }

        private static void ApplyPendingDelta(UnlockRoomManager manager,
            ref ExpansionDeltaMessage pending)
        {
            if (pending == null)
            {
                return;
            }

            var delta = pending;
            pending = null;
            // The game already applied a client purchase locally; the host echoes the absolute
            // counts, so this applies authority over the local run. A concurrent purchase
            // committed elsewhere can leave the host count ahead of the local run.
            ExpansionInterop.ApplyDelta(manager, delta);
            ExpansionInterop.RefreshOpenScreen(ExpansionInterop.FindExpansionScreen());
        }

        private static void ReplacePending(ref ExpansionDeltaMessage pending,
            ExpansionDeltaMessage replacement)
        {
            // Absolute counts make the earlier message redundant.
            pending = replacement;
        }

        private void ClearPendingDeltas()
        {
            ReplacePending(ref _pendingShopDelta, null);
            ReplacePending(ref _pendingWarehouseDelta, null);
            ReplacePending(ref _pendingWarehouseUnlockDelta, null);
        }

        /// <summary>Pre-state captured by a purchase prefix so the postfix can send one predicted
        /// purchase and the undo can restore the authoritative counts.</summary>
        internal sealed class PurchaseCapture
        {
            public UnlockRoomManager Manager;
            public ExpansionInterop.StateSnapshot Snapshot;
            public int Rooms;
            public int WarehouseRooms;
            public bool WarehouseUnlocked;
            public float TotalCost;
            public int Index;
            public bool IsShopB;
        }

        private static PurchaseCapture Capture(float totalCost, int index, bool isShopB)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined
                || !client.RuntimeContext.InGame()
                || ExpansionInterop.ApplyingAuthoritativeState)
            {
                return null;
            }

            return new PurchaseCapture
            {
                Manager = ExpansionInterop.FindUnlockManager(),
                Snapshot = ExpansionInterop.CaptureState(),
                Rooms = CPlayerData.m_UnlockRoomCount,
                WarehouseRooms = CPlayerData.m_UnlockWarehouseRoomCount,
                WarehouseUnlocked = CPlayerData.m_IsWarehouseRoomUnlocked,
                TotalCost = totalCost,
                Index = index,
                IsShopB = isShopB,
            };
        }

        /// <summary>Sends one predicted purchase for an expansion the game just completed. The
        /// game owns the local mutation; a host rejection restores the captured counts through the
        /// same <c>Init</c> path, and the response merges the host's absolute state.</summary>
        private static void Observe(PurchaseCapture capture, byte kind)
        {
            var client = _active;
            if (client == null || !client._joined
                || !client.RuntimeContext.InGame())
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[expansion] observed purchase kind=" + kind + " rooms="
                + CPlayerData.m_UnlockRoomCount + " warehouseRooms="
                + CPlayerData.m_UnlockWarehouseRoomCount + " unlocked="
                + CPlayerData.m_IsWarehouseRoomUnlocked + ".");
            if (client.Context?.Host == null)
            {
                CoopPlugin.Log.LogError("[expansion] purchase was not forwarded: no host.");
                return;
            }

            client.Context.Send(new ExpansionPurchaseMessage
            {
                Kind = kind,
                Capture = capture,
            });
        }

        /// <summary>Restores the local pre-purchase snapshot after a host refusal.</summary>
        internal static void Rollback(PurchaseCapture capture)
        {
            if (capture != null)
            {
                ExpansionInterop.RollbackState(capture.Manager, capture.Snapshot);
            }
        }

        /// <summary>Applies the host's post-purchase state for the acting client.</summary>
        internal static void ApplyResponse(ActionResponseMessage response)
        {
            var client = _active;
            if (client == null || client._shutdown || !(response is ExpansionPurchaseResponse result))
            {
                return;
            }

            var delta = new ExpansionDeltaMessage
            {
                Area = result.Area,
                Count = result.Count,
                Unlocked = result.Unlocked,
            };
            switch (delta.Area)
            {
                case 0:
                    ReplacePending(ref client._pendingShopDelta, delta);
                    break;
                case 1:
                    ReplacePending(ref client._pendingWarehouseDelta, delta);
                    break;
                case 2:
                    ReplacePending(ref client._pendingWarehouseUnlockDelta, delta);
                    break;
                default:
                    throw new InvalidOperationException("Unknown expansion delta area "
                        + delta.Area + ".");
            }

            client.ApplyLatestState();
        }

        [HarmonyPatch(typeof(ExpansionShopUIScreen), "EvaluateCartCheckout")]
        private static class RoomCheckoutPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ExpansionShopUIScreen __instance, float totalCost, int index,
                bool isShopB, out PurchaseCapture __state)
            {
                __state = Capture(totalCost, index, isShopB);
                // EvaluateCartCheckout queues the vanilla ReduceCoin/AddShopExp below. The host
                // owns that debit and grants the XP when it applies the ExpansionPurchaseMessage,
                // so suppress the Hud economy observer for the vanilla events this call produces.
                if (__state != null)
                {
                    EconomyActionScope.Enter();
                }

            }

            [HarmonyPostfix]
            private static void Postfix(ExpansionShopUIScreen __instance, float totalCost, int index,
                bool isShopB, PurchaseCapture __state)
            {
                if (__state == null)
                {
                    return;
                }

                // EvaluateCartCheckout is a no-op unless the screen's active tab matches isShopB,
                // and a failed affordability check mutates nothing. Only a real count change is the
                // purchase the host must resolve.
                var kind = isShopB ? (byte)1 : (byte)0;
                var changed = kind == 0
                    ? CPlayerData.m_UnlockRoomCount != __state.Rooms
                    : CPlayerData.m_UnlockWarehouseRoomCount != __state.WarehouseRooms;
                if (!changed)
                {
                    return;
                }

                Observe(__state, kind);
            }

            [HarmonyFinalizer]
            private static void Finalizer(PurchaseCapture __state)
            {
                if (__state != null)
                {

                    EconomyActionScope.Exit();
                }

            }
        }

        [HarmonyPatch(typeof(ExpansionShopUIScreen), "OnPressUnlockShopB")]
        private static class LotBCheckoutPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ExpansionShopUIScreen __instance, out PurchaseCapture __state)
            {
                __state = Capture(0f, 0, true);
                // OnPressUnlockShopB queues the vanilla ReduceCoin/AddShopExp. The host owns that
                // debit and grants the XP through the ExpansionPurchaseMessage, so suppress the Hud
                // economy observer for the vanilla events this call produces.
                if (__state != null)
                {
                    EconomyActionScope.Enter();
                }

            }

            [HarmonyPostfix]
            private static void Postfix(ExpansionShopUIScreen __instance, PurchaseCapture __state)
            {
                if (__state == null
                    || CPlayerData.m_IsWarehouseRoomUnlocked == __state.WarehouseUnlocked)
                {
                    return;
                }

                Observe(__state, 2);
            }

            [HarmonyFinalizer]
            private static void Finalizer(PurchaseCapture __state)
            {
                if (__state != null)
                {

                    EconomyActionScope.Exit();
                }

            }
        }

        [HarmonyPatch(typeof(UnlockRoomManager), "Init")]
        private static class ExpansionManagerReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _active?.OnManagerReady();
            }
        }
    }
}
