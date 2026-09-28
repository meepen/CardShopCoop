using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Client-side shop-light intent and authoritative state receiver.</summary>
    [ClientBehaviour]
    public sealed class LightClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "light";
        private static LightClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private LightSwitchStateMessage _pendingState;

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
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.light.client");
                _harmony.CreateClassProcessor(typeof(LightSwitchPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(LightReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Light client initialization failed: " + exception);
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
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(LightSwitchStateMessage))]
        private void HandleState(MessageContext context, LightSwitchStateMessage message)
        {
            if (_shutdown)
                return;

            if (_pendingState != null)
                PredictionApi.Ack(_pendingState.PredictionId);
            _pendingState = message;
            TryApplyPending();
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingState == null || _context == null || !_context.InGame())
                return;

            if (!LightInterop.IsSceneReady(LightInterop.FindSceneManager()))
                return;

            var pending = _pendingState;
            // The host applies the guest's absolute requested bool and echoes it, so this delta
            // confirms the toggle; reconciling would flip the switch back first.
            PredictionApi.AckOrApply(pending.PredictionId,
                () => LightSwitchState.Apply(pending.IsActive));
            _pendingState = null;
        }

        /// <summary>Observes the local switch the game already toggled and records the change as
        /// one post-hoc prediction. The game owns the toggle (the client only predicts the shared
        /// shop-light state for the host to confirm), so a rejection rolls the switch back through
        /// the same game surface.</summary>
        private void ObserveToggle(bool armed, bool prior)
        {
            if (!armed || _context == null || !_joined || !_context.InGame())
                return;
            if (!LightSwitchState.TryGet(out var after) || after == prior)
                return;

            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new LightSwitchIntentMessage
                {
                    PredictionId = predictionId,
                    IsActive = after,
                }),
                () => LightSwitchState.Apply(after),
                () => LightSwitchState.Apply(prior));
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => TryApplyPending();

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
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableLightSwitch), "OnMouseButtonUp")]
        private static class LightSwitchPatch
        {
            private struct ToggleState
            {
                public bool Armed;
                public bool Prior;
            }

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(out ToggleState __state)
            {
                __state = default;
                var active = _active;
                if (active == null || !active._joined || active._context == null
                    || !active._context.InGame())
                    return;
                if (!LightSwitchState.TryGet(out var prior))
                    return;
                __state = new ToggleState { Armed = true, Prior = prior };
            }

            [HarmonyPostfix]
            private static void Postfix(ToggleState __state)
                => _active?.ObserveToggle(__state.Armed, __state.Prior);
        }

        [HarmonyPatch(typeof(LightManager), "Awake")]
        private static class LightReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }
    }
}
