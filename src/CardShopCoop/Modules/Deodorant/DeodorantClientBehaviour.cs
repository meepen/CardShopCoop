using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
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

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            try
            {
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.deodorant.client");
                _harmony.CreateClassProcessor(typeof(SprayPatch)).Patch();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"Deodorant client initialization failed: {error}");
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection?.Id == 1)
            {
                _joined = false;
            }
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

            _harmony?.UnpatchSelf();
            _harmony = null;
            _joined = false;
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
            {
                return;
            }

            ActionLedger.Submit(
                PredictionScope,
                actionId => _context.Send(1, new HandheldSprayIntentMessage
                {
                    ActionId = actionId,
                    Position = position,
                    Content = after,
                }),
                () => DeodorantInterop.TrySetContent(controller, after),
                () => DeodorantInterop.TrySetContent(controller, before));
        }

        [MessageHandler]
        private void HandleEvent(MessageContext _, HandheldSprayEventMessage message)
        {
            if (message == null)
            {
                return;
            }

            // The spray content is echoed verbatim, but the customer state is host-computed and the
            // client's optimistic spray never produced it. Retire our prediction, then always apply
            // the host state so the host-computed customers overwrite the local spray.
            ActionLedger.Confirm(message.ActionId,
                () => ApplyEvent(message));
        }

        private void ApplyEvent(HandheldSprayEventMessage message)
        {
            if (ActionLedger.IsReconciling)
            {
                DeodorantInterop.TrySetContent(SceneRef<InteractionPlayerController>.Get(),
                    message.Content);
            }

            ApplyCustomers(message.Customers);
        }

        private void ApplyCustomers(System.Collections.Generic.IList<DeodorantCustomerState> states)
        {
            if (states == null)
            {
                return;
            }

            for (var i = 0; i < states.Count; i++)
            {
                ApplyCustomerState(states[i]);
            }
        }

        /// <summary>Applies one host-computed smell result to the live customer the wire named.
        /// The converter already resolved the exact pooled incarnation, so a null customer means
        /// the entity is not live here and the entry is logged and dropped.</summary>
        private static void ApplyCustomerState(DeodorantCustomerState state)
        {
            if (state?.Customer == null)
            {
                CoopPlugin.Log.LogWarning("[deodorant] dropped smell state with no live customer.");
                return;
            }

            DeodorantInterop.ApplyCustomerState(state.Customer, state.IsSmelly, state.SmellyMeter);
        }

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
                {
                    return;
                }

                // Capture the pre-tick content and muzzle position. The game performs the spray
                // itself; the postfix observes the content drop and records the prediction.
                if (!DeodorantInterop.TryGetSpray(__instance, out var position)
                    || !DeodorantInterop.TryGetContent(__instance, out var before))
                {
                    return;
                }

                __state = new SprayState { Armed = true, Position = position, Before = before };
            }

            [HarmonyPostfix]
            private static void Postfix(InteractionPlayerController __instance, SprayState __state)
            {
                var active = _active;
                if (active == null || !__state.Armed)
                {
                    return;
                }

                if (!DeodorantInterop.TryGetContent(__instance, out var after)
                    || after >= __state.Before)
                {
                    return;
                }

                active.Forward(__instance, __state.Position, __state.Before, after);
            }
        }

    }
}
