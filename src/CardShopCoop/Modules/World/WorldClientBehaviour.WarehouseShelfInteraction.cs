using CardShopCoop.Modules.Actions;
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

            _harmony.CreateClassProcessor(typeof(WarehouseTakeObservePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WarehouseShelfOpenVisualPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WarehouseShelfPlacedVisualPatch)).Patch();
            if (WarehouseShelfInteraction.UsesRecords)
            {
                _harmony.CreateClassProcessor(typeof(BoxRecordStoreCompletePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordAddedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordPoppedPatch)).Patch();
            }
            else
            {
                _harmony.CreateClassProcessor(typeof(BoxAddedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BoxRemovedPatch)).Patch();
            }
        }

        [MessageHandler]
        private void HandleWarehouseState(MessageContext context, WarehouseStateMessage message)
        {
            if (message == null || Warehouse == null)
            {
                return;
            }

            Warehouse.ClientApplyState(message);
        }

        [MessageHandler]
        private void HandleWarehouseDelta(MessageContext context, WarehouseDeltaMessage message)
        {
            if (message == null || Warehouse == null)
            {
                return;
            }

            var pending = ActionLedger.IsPending(message.ActionId);
            if (message.IsStore)
            {
                if (pending)
                {
                    // Our own accepted store. Apply it idempotently: the game already added the
                    // record/live box, and the host may have canonicalised its order.
                    WorldAction.Confirm(message, () => Warehouse.ClientApplyDelta(message));
                    Warehouse.CompleteMutation(message.ActionId);
                }
                else
                {
                    Warehouse.ClientApplyDelta(message);
                }

                return;
            }

            if (pending)
            {
                // Our own take already moved the box into the hand. The authoritative echo only
                // retires the prediction; applying it again would pop/remove/attach twice.
                WorldAction.Ack(message);
                Warehouse.CompleteMutation(message.ActionId);
                return;
            }

            Warehouse.ClientApplyDelta(message);
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

        private void NotifyWarehouseBoxAdded(ShelfCompartment compartment)
        {
            Warehouse?.OnBoxAdded(compartment);
        }

        private void NotifyWarehouseBoxRemoved(ShelfCompartment compartment,
            InteractablePackagingBox_Item box)
        {
            Warehouse?.OnBoxRemoved(compartment, box);
        }

        /// <summary>Records backend: the box adds its own stored record from inside
        /// <c>OnFinishLerp</c>. Mark it for the span of that method so the store is observed only
        /// when a real box owns the add (save-load adds are gated by the snapshot/baseline).</summary>
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

            [HarmonyFinalizer]
            private static void Finalizer()
            {
                _instance?.Warehouse?.EndLocalRecordStore();
            }
        }

        /// <summary>Captures the top box before the game's own take, then forwards the one take
        /// prediction after it. The prefix never suppresses vanilla.</summary>
        [HarmonyPatch(typeof(InteractableStorageCompartment), "OnMouseButtonUp")]
        private static class WarehouseTakeObservePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableStorageCompartment __instance,
                out WarehouseShelfInteraction.WarehouseTakeCapture __state)
            {
                __state = _instance?.Warehouse?.CaptureClientTake(__instance);
            }

            [HarmonyPostfix]
            private static void Postfix(WarehouseShelfInteraction.WarehouseTakeCapture __state)
            {
                if (_instance == null || __state == null)
                {
                    return;
                }

                try
                {
                    _instance.Warehouse.ObserveClientTake(__state);
                }
                finally
                {
                    _instance.Warehouse.EndCoveredTake(__state);
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(WarehouseShelfInteraction.WarehouseTakeCapture __state)
            {
                _instance?.Warehouse?.EndCoveredTake(__state);
            }
        }

        /// <summary>Opening a boxed warehouse shelf re-enables the shelf but not the batched
        /// stored-box matrices. Rebuild them at the moment the package exposes the furniture.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnPressOpenBox")]
        private static class WarehouseShelfOpenVisualPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __instance)
            {
                if (__instance != null
                    && BoxNetworkInteraction.TryGetBoxedFurniture(__instance, out var furniture))
                {
                    WarehouseShelfInteraction.SetStoredBoxVisuals(furniture, true);
                }
            }
        }

        /// <summary>The local player's own place path does not go through PlacementInterop, so
        /// the actor must restore the shelf's batched stored-box matrices here.</summary>
        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class WarehouseShelfPlacedVisualPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
            {
                if (__instance != null && !__instance.GetIsBoxedUp())
                {
                    WarehouseShelfInteraction.SetStoredBoxVisuals(__instance, true);
                }
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
