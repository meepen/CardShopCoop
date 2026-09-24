using System;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
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
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private ExpansionBaselineMessage _latestBaseline;
        private ExpansionDeltaMessage _pendingShopDelta;
        private ExpansionDeltaMessage _pendingWarehouseDelta;
        private ExpansionDeltaMessage _pendingWarehouseUnlockDelta;
        private bool _baselineNeedsManagerInitialization;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.expansion.client");
                _harmony.CreateClassProcessor(typeof(RoomCheckoutPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(LotBCheckoutPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ExpansionManagerReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Expansion client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            ApplyLatestState();
        }

        [MessageHandler(typeof(ExpansionBaselineMessage))]
        private void HandleBaseline(MessageContext context, ExpansionBaselineMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            _latestBaseline = message;
            ApplyLatestState();
        }

        [MessageHandler(typeof(ExpansionDeltaMessage))]
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
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
            _harmony = null;
            _latestBaseline = null;
            ClearPendingDeltas();
            _baselineNeedsManagerInitialization = false;
            _joined = false;
            _context = null;
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
            if (_shutdown || !_joined || !_context.InGame() || ExpansionInterop.ApplyingAuthoritativeState)
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
            ExpansionInterop.ApplyDelta(manager, delta);
            ExpansionInterop.RefreshOpenScreen(ExpansionInterop.FindExpansionScreen());
        }

        private static void ReplacePending(ref ExpansionDeltaMessage pending,
            ExpansionDeltaMessage replacement)
        {
            pending = replacement;
        }

        private void ClearPendingDeltas()
        {
            ReplacePending(ref _pendingShopDelta, null);
            ReplacePending(ref _pendingWarehouseDelta, null);
            ReplacePending(ref _pendingWarehouseUnlockDelta, null);
        }

        private static bool Send(byte kind)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || !client._context.InGame()
                || ExpansionInterop.ApplyingAuthoritativeState)
            {
                return true;
            }

            if (ExpansionInterop.FindUnlockManager() == null)
            {
                CoopPlugin.Log.LogWarning(
                    "Expansion purchase ignored: the unlock manager is unavailable.");
                return false;
            }

            client._context.Send(1, new ExpansionPurchaseMessage { Kind = kind });
            // The host owns the mutation and will broadcast the resulting state. Never run the
            // vanilla local purchase on a guest.
            return false;
        }

        [HarmonyPatch(typeof(ExpansionShopUIScreen), "EvaluateCartCheckout")]
        private static class RoomCheckoutPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool isShopB)
            {
                if (ExpansionInterop.ApplyingAuthoritativeState)
                {
                    return true;
                }

                return Send(isShopB ? (byte)1 : (byte)0);
            }
        }

        [HarmonyPatch(typeof(ExpansionShopUIScreen), "OnPressUnlockShopB")]
        private static class LotBCheckoutPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (ExpansionInterop.ApplyingAuthoritativeState
                    || CPlayerData.m_IsWarehouseRoomUnlocked)
                {
                    return true;
                }

                return Send(2);
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
