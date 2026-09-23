using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using System;

namespace CardShopCoop.Modules.Deodorant
{
    [ClientBehaviour]
    public sealed class DeodorantClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "deodorant.spray";
        private static DeodorantClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private float _contentBefore;
        private bool _hadContentBefore;
        private bool _predictionStarted;
        private bool _applyingAuthoritative;
        private readonly System.Collections.Generic.Dictionary<string, DeodorantCustomerState>
            _pendingCustomerStates = new();

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;
            _context = RuntimeContext;
            try
            {
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.deodorant.client");
                _harmony.CreateClassProcessor(typeof(SprayPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(CustomerSprayPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(CustomerManagerReadyPatch)).Patch();
                NpcClientBehaviour.CustomerPoolChanged += ApplyPendingStates;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"Deodorant client initialization failed: {error}");
                _harmony?.UnpatchSelf();
                _harmony = null;
                NpcClientBehaviour.CustomerPoolChanged -= ApplyPendingStates;
                if (ReferenceEquals(_active, this))
                    _active = null;
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
            _hadContentBefore = false;
            _predictionStarted = false;
            _pendingCustomerStates.Clear();
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection?.Id == 1)
            {
                _joined = false;
                _hadContentBefore = false;
                _predictionStarted = false;
                _pendingCustomerStates.Clear();
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            if (ReferenceEquals(_active, this))
                _active = null;
            _harmony?.UnpatchSelf();
            _harmony = null;
            NpcClientBehaviour.CustomerPoolChanged -= ApplyPendingStates;
            _joined = false;
            _hadContentBefore = false;
            _predictionStarted = false;
            _pendingCustomerStates.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void Forward(InteractionPlayerController controller, float before, float content,
            Vector3 position = default)
        {
            if (!_joined || !_context.InGame())
                return;
            if (position == default && !DeodorantInterop.TryGetSpray(controller, out position))
                return;

            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new HandheldSprayIntentMessage
                {
                    PredictionId = predictionId,
                    Position = position,
                    Content = content,
                }),
                () => { },
                () => DeodorantInterop.TrySetContent(controller, before));
        }

        [MessageHandler(typeof(HandheldSprayEventMessage))]
        private void HandleEvent(MessageContext _, HandheldSprayEventMessage message)
        {
            if (message == null)
                return;

            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyEvent(message));
        }

        private void ApplyEvent(HandheldSprayEventMessage message)
        {
            if (PredictionApi.IsReconciling)
            {
                DeodorantInterop.TrySetContent(SceneRef<InteractionPlayerController>.Get(),
                    message.Content);
            }

            ApplyCustomers(message.Customers);
        }

        private void ApplyPendingStates()
        {
            if (_pendingCustomerStates.Count == 0 || SceneRef<CustomerManager>.Get() == null)
                return;

            var pending = new System.Collections.Generic.List<DeodorantCustomerState>(
                _pendingCustomerStates.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                if (ApplyCustomerState(pending[i]))
                    _pendingCustomerStates.Remove(CustomerKey(pending[i]));
            }
        }

        private void ApplyCustomers(System.Collections.Generic.IList<DeodorantCustomerState> states)
        {
            if (states == null)
                return;

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (!ApplyCustomerState(state))
                {
                    if (state != null)
                    {
                        var key = CustomerKey(state);
                        _pendingCustomerStates[key] = state;
                    }
                }
            }
        }

        private bool ApplyCustomerState(DeodorantCustomerState state)
        {
            var customers = SceneRef<CustomerManager>.Get()?.GetCustomerList();
            if (state == null || customers == null || state.Index < 0 || state.Index >= customers.Count
                || customers[state.Index] == null)
            {
                return false;
            }

            if (!NpcClientBehaviour.TryGetCustomerGeneration(state.Index, out var generation))
            {
                return false;
            }

            // A pooled customer index can be reused. Drop an older result instead of applying
            // its smell state to the newer incarnation, but retain a future result until the
            // NPC identity reaches the client.
            if (generation != state.Generation)
            {
                return generation > state.Generation;
            }

            _applyingAuthoritative = true;
            try
            {
                DeodorantInterop.ApplyCustomerState(customers[state.Index], state.IsSmelly,
                    state.SmellyMeter);
            }
            finally
            {
                _applyingAuthoritative = false;
            }

            return true;
        }

        private static string CustomerKey(DeodorantCustomerState state)
            => state.Index + ":" + state.Generation;

        [HarmonyPatch(typeof(InteractionPlayerController), "RaycastHoldSprayState")]
        private static class SprayPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractionPlayerController __instance)
            {
                if (_active == null)
                    return true;
                if (!_active._joined || !_active._context.InGame())
                    return false;

                if (DeodorantInterop.TryGetSprayTick(__instance, out var position,
                    out var before, out var predictedAfter))
                {
                    _active._predictionStarted = true;
                    _active.Forward(__instance, before, predictedAfter, position);
                    return true;
                }

                _active._predictionStarted = false;
                _active._hadContentBefore = DeodorantInterop.TryGetContent(__instance,
                    out _active._contentBefore);
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractionPlayerController __instance)
            {
                if (_active == null)
                    return;

                if (_active._predictionStarted)
                {
                    _active._predictionStarted = false;
                    _active._hadContentBefore = false;
                    return;
                }

                if (!_active._hadContentBefore)
                    return;

                var before = _active._contentBefore;
                _active._hadContentBefore = false;
                if (!DeodorantInterop.TryGetContent(__instance, out var after)
                    || after >= before)
                    return;
                _active.Forward(__instance, before, after);
            }
        }

        [HarmonyPatch(typeof(Customer), "DeodorantSprayCheck")]
        private static class CustomerSprayPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                var active = _active;
                return active == null || active._context == null || !active._context.InGame()
                    || active._applyingAuthoritative;
            }
        }

        [HarmonyPatch(typeof(CustomerManager), "OnEnable")]
        private static class CustomerManagerReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.ApplyPendingStates();
        }
    }
}
