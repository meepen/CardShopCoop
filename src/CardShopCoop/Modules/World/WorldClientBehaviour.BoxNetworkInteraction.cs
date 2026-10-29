using System;
using System.Collections.Generic;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallBoxNetworkInteractionPatches()
        {
            // Box creation, destruction, identity and baselines are installed by the
            // GameObjectIdentity module. Only the delivery window (which box id a local vanilla
            // spawn consumes) and the guest's out-of-bounds sweep suppression belong here.
            Harmony.CreateClassProcessor(typeof(RestockUpdatePatch)).Patch();
        }

        [MessageHandler]
        private void HandleBoxState(MessageContext context, BoxStateMessage message)
        {
            _boxNetworkInteraction?.ClientApplyBoxState(message);
        }

        [MessageHandler]
        private void HandleWorldBaselineStart(MessageContext context, WorldBaselineStartMessage message)
        {
            BeginWorldBaseline(message.BaselineId);
        }

        /// <summary>The host sent the last baseline frame. This is the only point at which the
        /// identity baseline and every module baseline are guaranteed to have been dispatched in
        /// order, so descriptors whose target object did not exist yet can be retried and the
        /// warehouse/container/shelf queues that deferred them can be drained. Without this
        /// handler the old end-of-baseline signal is dropped, late descriptors stay pending for
        /// the rest of the session, and every state message for them is retained forever.</summary>
        [MessageHandler]
        private void HandleWorldBaselineComplete(MessageContext context,
            WorldBaselineCompleteMessage message)
        {
            GameObjectIdentityClientBehaviour.Active?.RetryPendingNow();
            _cardInteraction?.Containers.FlushClientState();
            _warehouseShelfInteraction?.FlushClientState();
            FlushPlayerShelfState();
        }

        /// <summary>Client: the box engine owns this local box (the identity module bound it).</summary>
        internal static bool TryGetBoxId(InteractablePackagingBox box, out Guid id)
        {
            id = Guid.Empty;
            return _instance?._boxNetworkInteraction != null
                && _instance._boxNetworkInteraction.TryGetId(box, out id);
        }

        /// <summary>Identity hook bridge: binds a locally created box to the id its checkout
        /// pre-assigned (or a fresh one) and applies any descriptor that arrived first.</summary>
        internal static Guid AssignCreatedBoxId(InteractablePackagingBox box)
            => _instance?._boxNetworkInteraction?.AssignClientCreated(box) ?? Guid.Empty;

        /// <summary>Binds the furniture a furniture checkout spawned to the Guid the buyer put
        /// on the purchase intent. The package is a view of the same identity.</summary>
        internal static bool BindClientCreatedFurniture(InteractableObject furniture, Guid id)
            => PlacementIdentityAdapter.BindClientCreated(furniture, id);

        /// <summary>Rolls back a rejected furniture purchase: the buyer's local furniture and its
        /// package are the prediction that never became authoritative.</summary>
        internal static void RollbackClientFurniture(Guid id)
        {
            if (id == Guid.Empty)
            {
                return;
            }

            if (GameObjectIdentityApi.TryResolve(GameObjectId.Furniture(id), out var entity)
                && entity is InteractableObject furniture)
            {
                PlacementObjectIdentity.Destroy(furniture);
            }
        }

        /// <summary>Identity hook bridge: this id belongs to a local delivery that has not spawned
        /// its box yet, so an authoritative descriptor must wait for that object.</summary>
        internal static bool IsClientOwnedBox(Guid id)
            => _instance?._boxNetworkInteraction?.IsClientOwned(id) == true;

        internal static bool HasPendingBoxMove(Guid id)
            => _instance?._boxNetworkInteraction?.HasPendingLocalMove(id) == true;

        internal static bool IsBoxDestroyForwardSuppressed()
            => _instance?._boxNetworkInteraction?.IsDestroyForwardSuppressed == true;

        /// <summary>Identity hook bridge: release the hand before a box the game is destroying is
        /// torn down, so it cannot stay parented to the hand as a ghost.</summary>
        internal static void ReleaseHoldForDestroyedBox(InteractablePackagingBox box)
            => _instance?._playerBoxInteraction?.ReleaseHeldObject(box);

        /// <summary>Identity hook bridge: drop both the local hold and a mirrored remote hold for
        /// a box the host removed authoritatively.</summary>
        internal static void ForgetDestroyedBox(Guid id)
            => _instance?._playerBoxInteraction?.ForgetDestroyed(id);

        internal static bool IsPreloadHeld
            => _instance?.RuntimeContext.PreloadHold?.Invoke() == true;

        /// <summary>Client: pre-assign the ids for the boxes a checkout's vanilla delivery queue
        /// will spawn and register them for the creating intent.</summary>
        internal static void PushClientDeliveryIds(IEnumerable<Guid> ids)
            => _instance?._boxNetworkInteraction?.PushClientDeliveryIds(ids);

        internal static void PushClientCreatedId(Guid id)
            => _instance?._boxNetworkInteraction?.PushClientCreatedId(id);

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
    }
}
