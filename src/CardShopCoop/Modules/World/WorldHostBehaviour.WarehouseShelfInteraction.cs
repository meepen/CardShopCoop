using CardShopCoop.Api;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private WarehouseShelfInteraction Warehouse => _warehouseShelfInteraction;

        private void InstallWarehouseShelfInteractionPatches()
        {
            if (!WarehouseShelfInteraction.Available())
            {
                return;
            }

            Harmony.CreateClassProcessor(typeof(BoxRecordStoreCompletePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(StoredBoxRecordAddedPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(StoredBoxRecordPoppedPatch)).Patch();
        }

        [MessageHandler]
        private ActionOutcome HandleWarehouseStore(CoopMessageContext context,
            WarehouseStoreAction message)
        {
            if (Warehouse == null)
            {
                return ActionOutcome.Reject("warehouse sync is not running");
            }

            if (message?.Shelf == null || message.Box == null || !Context.InGame
                || !context.IsAuthenticated)
            {
                return Warehouse.RefuseStore(message,
                    "the session is not ready for world actions");
            }

            var outcome = Warehouse.HostApplyStore(message, out var broadcast);
            if (broadcast != null)
            {
                // The actor merges the same state from its response, so it is excluded here. A
                // take must never be applied twice on the peer that already performed it.
                Context.BroadcastExcept(context.Peer, broadcast);
            }

            return outcome;
        }

        [MessageHandler]
        private ActionOutcome HandleWarehouseTake(CoopMessageContext context,
            WarehouseTakeAction message)
        {
            if (Warehouse == null)
            {
                return ActionOutcome.Reject("warehouse sync is not running");
            }

            if (message?.Source?.Shelf == null || message.CreatedBox == null || !Context.InGame
                || !context.IsAuthenticated)
            {
                return Warehouse.RefuseTake(message,
                    "the session is not ready for world actions");
            }

            var outcome = Warehouse.HostApplyTake(message, out var broadcast, out var grantedHold);
            if (broadcast != null)
            {
                Context.BroadcastExcept(context.Peer, broadcast);
            }

            if (grantedHold != null)
            {
                // The delta above already named the box to every peer; the hold must follow it.
                Warehouse.AnnounceGrantedHold(grantedHold, context.Peer);
            }

            return outcome;
        }

        private void NotifyStoredBoxRecordAdded(ShelfCompartment compartment)
        {
            Warehouse?.OnStoredBoxRecordAdded(compartment);
        }

        private void NotifyStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop,
            WarehouseShelfInteraction.WarehouseBoxFacts removed)
        {
            Warehouse?.OnStoredBoxRecordPopped(compartment, didPop, removed);
        }

        /// <summary>Records backend: the box adds its own stored record from inside
        /// <c>OnFinishLerp</c>. Mark it for the span of that method so the AddStoredBoxRecord hook
        /// binds the new record to exactly this box's identity.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnFinishLerp")]
        private static class BoxRecordStoreCompletePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance)
            {
                _instance?.Warehouse?.BeginLocalRecordStore(__instance);
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                _instance?.Warehouse?.EndLocalRecordStore();
            }

            /// <summary>A throw out of the game's own OnFinishLerp would otherwise leave
            /// <c>_currentStoreBox</c> set and bind the following record to the wrong box.</summary>
            [HarmonyFinalizer]
            private static void Finalizer()
            {
                _instance?.Warehouse?.EndLocalRecordStore();
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "AddStoredBoxRecord")]
        private static class StoredBoxRecordAddedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance)
            {
                _instance?.NotifyStoredBoxRecordAdded(__instance);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "TryPopLastStoredBoxRecord")]
        private static class StoredBoxRecordPoppedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance,
                out WarehouseShelfInteraction.WarehouseBoxFacts __state)
            {
                __state = _instance?.Warehouse?.CapturePoppedRecord(__instance) ?? default;
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, bool __result,
                WarehouseShelfInteraction.WarehouseBoxFacts __state)
            {
                _instance?.NotifyStoredBoxRecordPopped(__instance, __result, __state);
            }
        }

    }
}
