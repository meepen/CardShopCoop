using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallBoxNetworkInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(ItemBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RestockUpdatePatch)).Patch();
        }

        [MessageHandler(typeof(BoxCreatedMessage))]
        private void HandleBoxCreated(MessageContext context, BoxCreatedMessage message)
        {
            // A BoxCreated is host-assigned identity, not a pure prediction ack: only the host
            // allocates the box id, so the descriptor must always reach the box engine even when
            // it also confirms a prediction of ours. A player box-up predicts the local box-up
            // mutation (already applied by vanilla) but cannot predict the id, so AckOrApply's
            // "ack = skip apply" would leave the local package unbound and unsynced. Retire any
            // matching prediction first, then apply the descriptor under the prediction apply
            // guard. Do NOT reconcile the queue here: a queued container op replayed by the
            // reconcile can reference a box this creation already replaced, and StoreBox on a
            // destroyed object tripped the reliable-handler recovery.
            WorldPrediction.Ack(message);
            WorldPrediction.AckOrApply(message,
                () => _boxNetworkInteraction.ClientApplyCreated(message));
        }

        [MessageHandler(typeof(BoxDestroyedMessage))]
        private void HandleBoxDestroyed(MessageContext context, BoxDestroyedMessage message)
        {
            // A destroy echo names exactly the box the guest asked to destroy (a rejected request
            // comes back as a prediction rollback), so it confirms the prediction; reconciling
            // would first respawn the optimistic descriptor for no reason.
            WorldPrediction.AckOrApply(message,
                () => _boxNetworkInteraction.ClientApplyDestroyed(message));
            // If this peer still modelled the box as held, drop that: a destroyed box must not
            // leave it "carrying" an invisible one.
            _playerBoxInteraction?.ForgetDestroyed(message.BoxNetworkId);
        }

        [MessageHandler(typeof(BoxStateMessage))]
        private void HandleBoxState(MessageContext context, BoxStateMessage message)
        {
            _boxNetworkInteraction?.ClientApplyBoxState(message);
        }

        [MessageHandler(typeof(BoxBaselineCompleteMessage))]
        private void HandleBoxBaselineComplete(MessageContext context, BoxBaselineCompleteMessage message)
        {
            _boxNetworkInteraction.ClientCompleteBaseline();
        }

        [MessageHandler(typeof(WorldBaselineCompleteMessage))]
        private void HandleWorldBaselineComplete(MessageContext context,
            WorldBaselineCompleteMessage message)
        {
            CompleteWorldBaseline(message.BaselineId);
        }

        [MessageHandler(typeof(WorldBaselineStartMessage))]
        private void HandleWorldBaselineStart(MessageContext context, WorldBaselineStartMessage message)
        {
            BeginWorldBaseline(message.BaselineId);
        }

        private void RejectCreatedBox(InteractablePackagingBox box)
        {
            // While the transferred world is loading, every spawn is the save population, not a
            // player creation. Those boxes take their identity from the host's baseline slot, so
            // minting a client id here lets the save-load's own AddBox/store observation and any
            // early player action forward intents under an id the host has never seen - the
            // join-time "unknown box id" store/placement refusals. Leaving them unbound until the
            // baseline binds the host id means those actions are simply not forwarded.
            if (_context?.PreloadHold?.Invoke() == true)
            {
                return;
            }

            _boxNetworkInteraction?.AssignClientCreated(box);
        }

        private BoxNetworkInteraction.ClientDestroyCapture CaptureDestroyedBox(
            InteractablePackagingBox box)
        {
            // The game is tearing this box down. If we are holding it, release the hand first or the
            // destroyed object stays parented to the hand as a ghost. Capture the forward decision
            // (and descriptor) before vanilla destroys the object.
            _playerBoxInteraction?.ReleaseHeldObject(box);
            return _boxNetworkInteraction?.CaptureClientDestroyed(box);
        }

        private void ForwardDestroyedBox(BoxNetworkInteraction.ClientDestroyCapture capture)
        {
            _boxNetworkInteraction?.ForwardClientDestroyed(capture);
        }

        /// <summary>Client: pre-assign the ids for the boxes a checkout's vanilla delivery queue
        /// will spawn and register them for the creating intent.</summary>
        internal static void PushClientDeliveryIds(IEnumerable<Guid> ids)
            => _instance?._boxNetworkInteraction?.PushClientDeliveryIds(ids);

        internal static void PushClientCreatedId(Guid id)
            => _instance?._boxNetworkInteraction?.PushClientCreatedId(id);

        /// <summary>Client: the stable id this peer assigned to one of its locally-created boxes.</summary>
        internal static bool TryGetBoxId(InteractablePackagingBox box, out Guid id)
        {
            id = Guid.Empty;
            return _instance?._boxNetworkInteraction != null
                && _instance._boxNetworkInteraction.TryGetId(box, out id);
        }

        /// <summary>Client: destroys a rejected checkout's locally-created boxes and drops the
        /// ids of its not-yet-spawned deliveries.</summary>
        internal static void RollbackClientCreated(IReadOnlyList<Guid> ids)
            => _instance?._boxNetworkInteraction?.RollbackClientCreated(ids);

        internal static void ReleaseUnspawnedClientIds(IReadOnlyList<Guid> ids)
            => _instance?._boxNetworkInteraction?.ReleaseUnspawnedClientIds(ids);

        internal static int PendingClientDeliveryCount()
            => _instance?._boxNetworkInteraction?.PendingDeliveryCount() ?? 0;

        internal static void CancelPendingClientDeliveries(int appended)
            => _instance?._boxNetworkInteraction?.CancelPendingClientDeliveries(appended);

        [HarmonyPatch(typeof(RestockManager), "Update")]
        private static class RestockUpdatePatch
        {
            [HarmonyPrefix]
            private static void Prefix(RestockManager __instance)
            {
                _instance?._boxNetworkInteraction?.SuppressOutOfBoundsSweep(__instance);
                _instance?._boxNetworkInteraction?.BeginDeliverySpawns();
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                _instance?._boxNetworkInteraction?.EndDeliverySpawns();
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxItem")]
        private static class ItemBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxCard")]
        private static class CardBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxShelf")]
        private static class FurnitureBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnDestroyed")]
        private static class ItemBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance,
                out BoxNetworkInteraction.ClientDestroyCapture __state)
                => __state = _instance?.CaptureDestroyedBox(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxNetworkInteraction.ClientDestroyCapture __state)
                => _instance?.ForwardDestroyedBox(__state);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Card), "OnDestroyed")]
        private static class CardBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Card __instance,
                out BoxNetworkInteraction.ClientDestroyCapture __state)
                => __state = _instance?.CaptureDestroyedBox(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxNetworkInteraction.ClientDestroyCapture __state)
                => _instance?.ForwardDestroyedBox(__state);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnDestroyed")]
        private static class FurnitureBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Shelf __instance,
                out BoxNetworkInteraction.ClientDestroyCapture __state)
                => __state = _instance?.CaptureDestroyedBox(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxNetworkInteraction.ClientDestroyCapture __state)
                => _instance?.ForwardDestroyedBox(__state);
        }
    }
}
