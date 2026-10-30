using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host authority for who is currently holding a placed object for placement.</summary>
    public sealed partial class WorldHostBehaviour
    {
        private PlacementHoldInteraction _placementHold;
        private readonly List<InteractableObject> _releasedHolds = new();

        internal void InstallPlacementHold()
        {
            _placementHold = new PlacementHoldInteraction(true, Context.Broadcast,
                (peer, message) => peer.Send(message));
            Harmony.CreateClassProcessor(typeof(PlacementHoldStartPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldRotatePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldFlipPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldInputPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldRemoteValidityPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldGlobalPreviewPatch)).Patch();
        }

        internal void ShutdownPlacementHold()
        {
            _placementHold?.Reset();
            _placementHold = null;
        }

        internal void TickPlacementHold() => _placementHold?.Tick();

        [MessageHandler]
        private void HandlePlacementHoldBegin(CoopMessageContext context,
            PlacementHoldBeginRequestMessage message)
        {
            if (message == null || _placementHold == null || !IsPlacementPeerMessage(context))
            {
                return;
            }

            if (message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped begin request from peer="
                    + context.Peer.Name + " with no live entity.");
                return;
            }

            if (_placementHold.HostGrant(message.Entity, context.Peer, message, out var granted))
            {
                // The host is an observer of a client's hold too, so render it here as well as
                // broadcasting to the other peers.
                _placementHold.ApplyBegin(granted);
                Context.Broadcast(granted);
            }
            else
            {
                CoopPlugin.Log.LogInfo("[placement-hold] begin request refused for entity="
                    + PlacementInterop.Describe(message.Entity) + " peer="
                    + context.Peer.Name + ".");
            }
        }

        [MessageHandler]
        private void HandlePlacementHoldEnd(CoopMessageContext context,
            PlacementHoldEndRequestMessage message)
        {
            if (message == null || _placementHold == null || !IsPlacementPeerMessage(context))
            {
                return;
            }

            if (message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped end request from peer="
                    + context.Peer.Name + " with no live entity.");
                return;
            }

            if (_placementHold.HostRelease(message.Entity, context.Peer))
            {
                // The host rendered this client's hold when it granted it, so it must stop that
                // local render as well as telling the peers.
                _placementHold.ApplyEnd(message.Entity);
                Context.Broadcast(new PlacementHoldEndMessage { Entity = message.Entity });
            }
        }

        [OnClientDisconnected]
        private void ReleasePlacementHolds(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _placementHold?.ReleaseMover(PeerHandle.ForConnection(connection), _releasedHolds);
            }
        }

        [MessageHandler]
        private void HandlePlacementHoldRotation(CoopMessageContext context,
            PlacementHoldRotationRequestMessage message)
        {
            if (message == null || _placementHold == null || !IsPlacementPeerMessage(context))
            {
                return;
            }

            if (message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped rotation request from peer="
                    + context.Peer.Name + " with no live entity.");
                return;
            }

            if (!_placementHold.IsHeldBy(message.Entity, context.Peer))
            {
                CoopPlugin.Log.LogInfo("[placement-hold] ignored rotation for entity="
                    + PlacementInterop.Describe(message.Entity) + " not held by peer="
                    + context.Peer.Name + ".");
                return;
            }

            // The host renders this client's ghost too, so apply the rotation here and relay the
            // accepted value to every observer.
            var rotation = new PlacementHoldRotationMessage
            {
                Entity = message.Entity,
                Rotation = message.Rotation,
            };
            _placementHold.ApplyRotation(rotation);
            Context.Broadcast(rotation);
        }

        [MessageHandler]
        private void HandlePlacementHoldState(CoopMessageContext context,
            PlacementHoldStateRequestMessage message)
        {
            if (message == null || _placementHold == null || !IsPlacementPeerMessage(context))
            {
                return;
            }

            if (message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped state request from peer="
                    + context.Peer.Name + " with no live entity.");
                return;
            }

            if (!_placementHold.IsHeldBy(message.Entity, context.Peer))
            {
                CoopPlugin.Log.LogInfo("[placement-hold] ignored state for entity="
                    + PlacementInterop.Describe(message.Entity) + " not held by peer="
                    + context.Peer.Name + ".");
                return;
            }

            var state = new PlacementHoldStateMessage
            {
                Entity = message.Entity,
                StopSnap = message.StopSnap,
                IsFlipped = message.IsFlipped,
            };
            _placementHold.ApplyState(state);
            Context.Broadcast(state);
        }

        /// <summary>Observes the host entering placement move mode. Vanilla <c>StartMoveObject</c>
        /// always runs; the postfix records the local hold and, when the hold table already names
        /// another mover, unwinds the just-entered vanilla preview through the interaction's own
        /// cancel path, so the single-writer invariant the table exists to enforce is kept without
        /// suppressing vanilla.</summary>
        [HarmonyPatch(typeof(InteractableObject), "StartMoveObject")]
        private static class PlacementHoldStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
            {
                _instance?._placementHold?.LocalStarted(__instance);
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "AddObjectRotation")]
        private static class PlacementHoldRotatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalRotated(__instance);
        }

        [HarmonyPatch(typeof(InteractableObject), "Flip")]
        private static class PlacementHoldFlipPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalFlipped(__instance);
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "Update")]
        private static class PlacementHoldInputPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
                => _instance?._placementHold?.LocalInputChanged();
        }

        [HarmonyPatch(typeof(InteractableObject), "Update")]
        private static class PlacementHoldRemoteValidityPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out bool __state)
                => __state = _instance?._placementHold?.BeginRemoteUpdate(__instance) == true;

            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.ApplyRemoteUpdate(__instance);

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?._placementHold?.EndRemoteUpdate();
                }
            }
        }

        [HarmonyPatch(typeof(ShelfManager), "SetMoveObjectPreviewModelValidState")]
        private static class PlacementHoldGlobalPreviewPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
                => !PlacementHoldInteraction.SuppressGlobalPreview;
        }
    }
}
