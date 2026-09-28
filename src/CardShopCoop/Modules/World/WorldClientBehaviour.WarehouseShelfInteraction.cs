using System.Collections.Generic;
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
            if (WarehouseShelfInteraction.UsesRecords)
            {
                _harmony.CreateClassProcessor(typeof(BoxRecordStoreCompletePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordAddedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordPoppedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoredBoxRecordArrangePatch)).Patch();
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
            if (!message.IsStore && !Modules.Prediction.PredictionApi.IsPending(message.PredictionId))
            {
                // Another player's take. The box leaves our shelf, but only the taker ends up
                // holding it, so take the remote path and never attach it to our own hand.
                Warehouse.ClientApplyDelta(message);
                return;
            }

            if (message.IsStore)
            {
                // Our own accepted store, or a remote store. The host delta is the authoritative
                // result, so retire our matching prediction and always apply it - retiring without
                // applying (what AckOrApply did when pending) dropped the host's edit.
                WorldPrediction.Confirm(message, () => Warehouse.ClientApplyDelta(message));
                return;
            }

            // Our own accepted take. Retire the prediction and apply the authoritative result
            // through the own-take path, which puts the box in our hand; the remote path would
            // leave it on the shelf with nobody holding it.
            WorldPrediction.Confirm(message, () => Warehouse.ClientTakeOwnConfirmed(message));
        }

        private void NotifyStoredBoxRecordAdded(ShelfCompartment compartment)
        {
            Warehouse?.OnStoredBoxRecordAdded(compartment);
        }

        private void NotifyStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop,
            WarehouseBoxState removed)
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
        /// <c>OnFinishLerp</c>. Mark it for the span of that method so the AddStoredBoxRecord hook
        /// binds the new record to exactly this box's network id - the store has already passed
        /// every game gate by the time this runs, so a refused store can no longer strand an id.
        /// The box instance is the hook argument; this method exists on both builds, but only the
        /// records build actually adds a record here.</summary>
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

            /// <summary>Even when the game's own take throws, the covered hold armed in the prefix
            /// must be released or prediction stays disabled for every later box action. The
            /// release is idempotent through the capture's Covered flag, so it is safe beside the
            /// postfix.</summary>
            [HarmonyFinalizer]
            private static void Finalizer(WarehouseShelfInteraction.WarehouseTakeCapture __state)
            {
                _instance?.Warehouse?.EndCoveredTake(__state);
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

        /// <summary>The game re-sorts a compartment's stored records by amount; the positional id
        /// list must follow the very same permutation or it drifts. The prefix snapshots the
        /// records' values and the postfix replays the multiset permutation onto the id list.</summary>
        [HarmonyPatch(typeof(ShelfCompartment), "ArrangeBoxItemBasedOnItemCount")]
        private static class StoredBoxRecordArrangePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance,
                out List<WarehouseShelfInteraction.StoredRecordKey> __state)
            {
                __state = _instance?.Warehouse?.CaptureRecordOrder(__instance);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance,
                List<WarehouseShelfInteraction.StoredRecordKey> __state)
            {
                _instance?.Warehouse?.ApplyRecordOrder(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "TryPopLastStoredBoxRecord")]
        private static class StoredBoxRecordPoppedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, out WarehouseBoxState __state)
            {
                __state = _instance?.Warehouse?.CapturePoppedRecord(__instance);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, bool __result,
                WarehouseBoxState __state)
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
