using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private WarehouseShelfInteraction Warehouse => _warehouseShelfInteraction;

        private void InstallWarehouseShelfInteractionPatches()
        {
            if (!WarehouseShelfInteraction.Available())
            {
                return;
            }

            _harmony.CreateClassProcessor(typeof(WarehouseStorePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WarehouseTakePatch)).Patch();
            if (WarehouseShelfInteraction.UsesRecords)
            {
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordAddedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordPoppedPatch)).Patch();
            }
            else
            {
                _harmony.CreateClassProcessor(typeof(BoxAddedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BoxRemovedPatch)).Patch();
            }
        }

        [MessageHandler(typeof(WarehouseStateMessage))]
        private void HandleWarehouseState(MessageContext context, WarehouseStateMessage message)
        {
            Warehouse.ClientApplyState(message);
        }

        [MessageHandler(typeof(WarehouseDeltaMessage))]
        private void HandleWarehouseDelta(MessageContext context, WarehouseDeltaMessage message)
        {
            // An accepted host delta carries the prediction id it confirmed. The optimistic
            // store/take already equals that result, so retire the prediction instead of running
            // its inverse and replaying it (which for a store meant a take that reparented the
            // host's stored box and left the guest without it). Dealt remote actions apply direct.
            WorldPrediction.ApplyConfirmedOrRemote(message,
                () => Warehouse.ClientApplyDelta(message));
        }

        private bool ForwardWarehouseStore(InteractablePackagingBox_Item box, bool isPlayer,
            ShelfCompartment compartment)
        {
            return Warehouse?.TryForwardClientStore(box, isPlayer, compartment) ?? true;
        }

        private bool ForwardWarehouseTake(InteractableStorageCompartment storage)
        {
            return Warehouse?.TryForwardClientTake(storage) ?? true;
        }

        private void NotifyStoredBoxRecordAdded(ShelfCompartment compartment)
        {
            Warehouse?.OnStoredBoxRecordAdded(compartment);
        }

        private void NotifyStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop)
        {
            Warehouse?.OnStoredBoxRecordPopped(compartment, didPop);
        }

        private void NotifyWarehouseBoxAdded(ShelfCompartment compartment)
        {
            Warehouse?.OnBoxAdded(compartment);
        }

        private void NotifyWarehouseBoxRemoved(ShelfCompartment compartment,
            InteractablePackagingBox_Item box)
        {
            Warehouse?.OnBoxRemoved(compartment, box);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "DispenseItem")]
        private static class WarehouseStorePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox_Item __instance, bool isPlayer,
                ShelfCompartment targetItemCompartment)
            {
                return _instance == null
                    || _instance.ForwardWarehouseStore(__instance, isPlayer, targetItemCompartment);
            }
        }

        [HarmonyPatch(typeof(InteractableStorageCompartment), "OnMouseButtonUp")]
        private static class WarehouseTakePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableStorageCompartment __instance)
            {
                return _instance == null || _instance.ForwardWarehouseTake(__instance);
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
            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, bool __result)
            {
                _instance?.NotifyStoredBoxRecordPopped(__instance, __result);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "AddBox")]
        private static class BoxAddedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance)
            {
                _instance?.NotifyWarehouseBoxAdded(__instance);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "RemoveBox")]
        private static class BoxRemovedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, InteractablePackagingBox_Item __0)
            {
                _instance?.NotifyWarehouseBoxRemoved(__instance, __0);
            }
        }

    }
}
