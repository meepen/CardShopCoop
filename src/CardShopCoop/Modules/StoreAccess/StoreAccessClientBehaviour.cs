using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Guest access-sign intents and authoritative mesh/state application.</summary>
    [ClientBehaviour]
    public sealed class StoreAccessClientBehaviour : CoopBehaviour
    {
        private static StoreAccessClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private StoreAccessStateMessage _pendingState;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            var lifecycle = false;
            try
            {
                ResetSessionState();
                StoreAccessInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.store-access.client");
                _harmony.CreateClassProcessor(typeof(OpenSignPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(OpenSignReadyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Store access client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                ResetSessionState();
                StoreAccessInterop.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(StoreAccessStateMessage))]
        private void HandleState(MessageContext context, StoreAccessStateMessage message)
        {
            if (_shutdown)
                return;

            _pendingState = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(StoreAccessDeltaMessage))]
        private void HandleDelta(MessageContext context, StoreAccessDeltaMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            // The local click already ran the vanilla sign path (toggle + animation), so the echo
            // of our own prediction must retire without undoing it - undoing would snap the sign
            // back and then re-animate it. ApplyDelta skips whatever already matches.
            PredictionApi.ApplyConfirmed(message.PredictionId, () => ApplyDelta(message));
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingState == null || _context == null || !_context.InGame()
                || !StoreAccessInterop.IsSceneReady())
                return;

            Apply(_pendingState);
            _pendingState = null;
        }

        private static void Apply(StoreAccessStateMessage message)
        {
            CPlayerData.m_IsShopOpen = message.IsShopOpen;
            CPlayerData.m_IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;
            var openSign = StoreAccessInterop.FindOpenSign();
            if (!message.Animate || !StoreAccessInterop.PlayOpenAnimation(openSign))
                StoreAccessInterop.RefreshOpenMesh(openSign);

            var warehouseSign = StoreAccessInterop.FindWarehouseSign();
            if (warehouseSign != null && (!message.Animate
                || !StoreAccessInterop.PlayWarehouseAnimation(warehouseSign)))
                StoreAccessInterop.RefreshWarehouseMesh(warehouseSign);
            else if (warehouseSign == null)
                StoreAccessInterop.RefreshWarehouseAccess(SceneRef<UnlockRoomManager>.Get());
        }

        private static void ApplyDelta(StoreAccessDeltaMessage message)
        {
            // Skip a sign whose authoritative state the local game already reached. The player's
            // own click applied it through the vanilla path (which is still animating), so
            // re-animating or refreshing here would fight that animation.
            var shopChanged = message.HasShopOpen && CPlayerData.m_IsShopOpen != message.IsShopOpen;
            var warehouseChanged = message.HasWarehouseDoorClosed
                && CPlayerData.m_IsWarehouseDoorClosed != message.IsWarehouseDoorClosed;

            if (message.HasShopOpen)
                CPlayerData.m_IsShopOpen = message.IsShopOpen;
            if (message.HasWarehouseDoorClosed)
                CPlayerData.m_IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;

            if (shopChanged)
            {
                var openSign = StoreAccessInterop.FindOpenSign();
                if (!message.Animate || !StoreAccessInterop.PlayOpenAnimation(openSign))
                    StoreAccessInterop.RefreshOpenMesh(openSign);
            }

            if (warehouseChanged)
            {
                var warehouseSign = StoreAccessInterop.FindWarehouseSign();
                if (!message.Animate || !StoreAccessInterop.PlayWarehouseAnimation(warehouseSign))
                    StoreAccessInterop.RefreshWarehouseMesh(warehouseSign);
            }
        }

        /// <summary>The vanilla sign click already toggled the state and started its animation;
        /// forward the intent so the host applies and echoes it. The prediction carries no local
        /// apply (vanilla did it) and only restores the pre-click state on a rollback.</summary>
        private void ForwardToggle(byte which, bool before, bool after)
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame() || after == before)
                return;

            var previousShop = which == 0 ? before : CPlayerData.m_IsShopOpen;
            var previousWarehouse = which == 1 ? before : CPlayerData.m_IsWarehouseDoorClosed;
            PredictionApi.Predict(
                "store-access",
                predictionId => _context.Send(1, new StoreAccessToggleMessage
                {
                    PredictionId = predictionId,
                    Which = which,
                }),
                () => { },
                () => ApplyLocal(previousShop, previousWarehouse));
        }

        private static void ApplyLocal(bool shopOpen, bool warehouseClosed)
        {
            CPlayerData.m_IsShopOpen = shopOpen;
            CPlayerData.m_IsWarehouseDoorClosed = warehouseClosed;
            StoreAccessInterop.RefreshOpenMesh(StoreAccessInterop.FindOpenSign());
            StoreAccessInterop.RefreshWarehouseMesh(StoreAccessInterop.FindWarehouseSign());
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            StoreAccessInterop.Reset();
            TryApplyPending();
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
                ResetSessionState();
        }

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            ResetSessionState();
            StoreAccessInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnMouseButtonUp")]
        private static class OpenSignPatch
        {
            // Let vanilla run so the sign plays its own animation; forward the result afterwards.
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
                => __state = CPlayerData.m_IsShopOpen;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(0, __state, CPlayerData.m_IsShopOpen);
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp")]
        private static class WarehouseSignPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
                => __state = CPlayerData.m_IsWarehouseDoorClosed;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(1, __state, CPlayerData.m_IsWarehouseDoorClosed);
        }

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnEnable")]
        private static class OpenSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnEnable")]
        private static class WarehouseSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }
    }
}
