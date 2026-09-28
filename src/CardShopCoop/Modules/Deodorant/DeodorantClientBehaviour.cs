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
            _pendingCustomerStates.Clear();
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection?.Id == 1)
            {
                _joined = false;
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
            _pendingCustomerStates.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        /// <summary>Records the spray tick the game already applied as one post-hoc prediction.
        /// The apply/undo closures re-set the spray content through the game's own surface for a
        /// rejection replay.</summary>
        private void Forward(InteractionPlayerController controller, Vector3 position, float before,
            float after)
        {
            if (!_joined || _context == null || !_context.InGame())
                return;

            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new HandheldSprayIntentMessage
                {
                    PredictionId = predictionId,
                    Position = position,
                    Content = after,
                }),
                () => DeodorantInterop.TrySetContent(controller, after),
                () => DeodorantInterop.TrySetContent(controller, before));
        }

        [MessageHandler(typeof(HandheldSprayEventMessage))]
        private void HandleEvent(MessageContext _, HandheldSprayEventMessage message)
        {
            if (message == null)
                return;

            // The spray content is echoed verbatim, but the customer state is host-computed and the
            // client's optimistic spray never produced it. Retire our prediction, then always apply
            // the host state so the host-computed customers overwrite the local spray.
            PredictionApi.Confirm(message.PredictionId,
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

            DeodorantInterop.ApplyCustomerState(customers[state.Index], state.IsSmelly,
                state.SmellyMeter);
            return true;
        }

        private static string CustomerKey(DeodorantCustomerState state)
            => state.Index + ":" + state.Generation;

        [HarmonyPatch(typeof(InteractionPlayerController), "RaycastHoldSprayState")]
        private static class SprayPatch
        {
            private struct SprayState
            {
                public bool Armed;
                public Vector3 Position;
                public float Before;
            }

            [HarmonyPrefix]
            private static void Prefix(InteractionPlayerController __instance, out SprayState __state)
            {
                __state = default;
                var active = _active;
                if (active == null || !active._joined || active._context == null
                    || !active._context.InGame())
                    return;

                // Capture the pre-tick content and muzzle position. The game performs the spray
                // itself; the postfix observes the content drop and records the prediction.
                if (!DeodorantInterop.TryGetSpray(__instance, out var position)
                    || !DeodorantInterop.TryGetContent(__instance, out var before))
                    return;
                __state = new SprayState { Armed = true, Position = position, Before = before };
            }

            [HarmonyPostfix]
            private static void Postfix(InteractionPlayerController __instance, SprayState __state)
            {
                var active = _active;
                if (active == null || !__state.Armed)
                    return;
                if (!DeodorantInterop.TryGetContent(__instance, out var after)
                    || after >= __state.Before)
                    return;
                active.Forward(__instance, __state.Position, __state.Before, after);
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
