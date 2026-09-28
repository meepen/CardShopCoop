using System.Collections.Generic;
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

        [MessageHandler(typeof(WarehouseStoreMessage))]
        private void HandleWarehouseStore(MessageContext context, WarehouseStoreMessage message)
        {
            if (!_context.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (Warehouse == null)
                    return false;
                if (!Warehouse.HostApplyStore(message, context.ConnectionId,
                    response => SendWorldTo(context.ConnectionId, response)))
                {
                    return false;
                }

                // The box is on the shelf now, not carried: drop the mirrored hand hold so a
                // later take re-attaches it to the avatar's carry anchor.
                _playerBoxInteraction?.ReleaseRemoteHoldForBox(message.BoxNetworkId);
                return true;
            });
        }

        [MessageHandler(typeof(WarehouseTakeMessage))]
        private void HandleWarehouseTake(MessageContext context, WarehouseTakeMessage message)
        {
            if (!_context.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (Warehouse == null)
                    return false;
                return Warehouse.HostApplyTake(message, context.ConnectionId,
                    response => SendWorldTo(context.ConnectionId, response));
            });
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
