using CardShopCoop.Attributes;
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
        private static DeodorantClientBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            try
            {
                _active = this;
                Harmony.CreateClassProcessor(typeof(SprayPatch)).Patch();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"Deodorant client initialization failed: {error}");
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                throw;
            }
            _initialized = true;
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

            _joined = false;
        }

        private void OnDestroy() => Shutdown();

        /// <summary>Records the spray tick the game already applied as one post-hoc prediction.
        /// The apply/undo closures re-set the spray content through the game's own surface for a
        /// rejection replay.</summary>
        private void Forward(InteractionPlayerController controller, Vector3 position, float before,
            float after)
        {
            if (!_joined || !RuntimeContext.InGame())
            {
                return;
            }

            Context.Send(new HandheldSprayIntentMessage
            {
                Position = position,
                Content = after,
                Controller = controller,
                Before = before,
            });
        }

        [MessageHandler]
        private void HandleEvent(MessageContext _, HandheldSprayEventMessage message)
        {
            if (message == null)
            {
                return;
            }

            // The host-computed customers overwrite the local spray result. The acting client
            // applies the same state from the action response instead.
            DeodorantState.ApplyCustomers(message.Customers);
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
                if (active == null || !active._joined
                    || !active.RuntimeContext.InGame())
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
