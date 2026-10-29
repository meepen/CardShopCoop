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
        private void HandleWarehouseStore(MessageContext context, WarehouseStoreMessage message)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (Warehouse == null || message?.Shelf == null || message.Box == null)
                {
                    CoopPlugin.Log.LogWarning("[warehouse] dropped store: the shelf or box did not "
                        + "resolve to a live object.");
                    return false;
                }

                return Warehouse.HostApplyStore(message, context.ConnectionId,
                    response => SendWorldTo(context.ConnectionId, response));
            });
        }

        [MessageHandler]
        private void HandleWarehouseTake(MessageContext context, WarehouseTakeMessage message)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (Warehouse == null || message?.Source?.Shelf == null
                    || message.CreatedBox == null)
                {
                    CoopPlugin.Log.LogWarning("[warehouse] dropped take: the source shelf or created "
                        + "box descriptor did not resolve.");
                    return false;
                }

                return Warehouse.HostApplyTake(message, context.ConnectionId,
                    response => SendWorldTo(context.ConnectionId, response));
            });
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
