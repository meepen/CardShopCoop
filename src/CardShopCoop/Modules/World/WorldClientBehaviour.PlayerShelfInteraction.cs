using CardShopCoop.Net;
using CardShopCoop.Modules.Prediction;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallPlayerShelfInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(AddItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RemoveItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TakeItemToHandPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(SetCompartmentItemTypePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PutItemOnShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TakeItemFromShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(BoxAddToShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(BoxRemoveFromShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RemoveLabelPatch)).Patch();
        }

        [MessageHandler(typeof(ShelfItemAddMessage))]
        private void HandleShelfItemAdd(MessageContext context, ShelfItemAddMessage message)
        {
            // Our own optimistic placement already moved the real item, so acknowledging the
            // prediction keeps it in place instead of undoing and respawning it. Retiring it also
            // drops it from the derived pending-add count, so later deltas no longer treat it as
            // outstanding.
            if (PredictionApi.IsPending(message.PredictionId))
            {
                PredictionApi.Ack(message.PredictionId);
                return;
            }

            ApplyShelfAction(message);
        }

        [MessageHandler(typeof(ShelfItemRemoveMessage))]
        private void HandleShelfItemRemove(MessageContext context, ShelfItemRemoveMessage message)
        {
            ApplyShelfAction(message);
        }

        internal void ResetPlayerShelfInteractionState()
        {
            _shelfInteraction?.Reset();
        }

        private void FlushPlayerShelfState()
        {
            _shelfInteraction?.FlushClientState();
        }

        private ShelfInteraction.LocalMutation CaptureShelfMutation(ShelfCompartment compartment,
            bool add, EItemType itemType, Item item)
        {
            return _shelfInteraction == null ? default
                : _shelfInteraction.CaptureLocalMutation(compartment, add, itemType, item);
        }

        private void PublishShelfAdd(ShelfCompartment compartment, ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishAdd(compartment, mutation);
        }

        private void PublishShelfRemove(ShelfCompartment compartment,
            ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishRemove(compartment, mutation);
        }

        /// <summary>A shelf's label (its compartment type, shown on the price tag even with no
        /// items) was removed. Removing a label clears the type on an empty compartment, which
        /// fires neither AddItem nor RemoveItem, so it needs its own publish or the other players
        /// keep showing the label. Only the removal (type -> None) is shared: an empty compartment
        /// briefly takes the incoming type before the first item lands, and that is not a label.</summary>
        private void PublishLabelChange(ShelfCompartment compartment, EItemType previousType,
            bool allowWarehouse = false)
        {
            if (_shelfInteraction == null || compartment == null || !_context.InGame()
                || compartment.GetItemCount() > 0
                || compartment.GetItemType() != EItemType.None
                || previousType == EItemType.None)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[shelf] label removed on " + compartment.name + " ("
                + previousType + ").");
            _shelfInteraction.PublishRemove(compartment,
                _shelfInteraction.CaptureLabelChange(compartment, allowWarehouse));
        }

        private bool ApplyShelfAction(ShelfInteractionMessage message)
        {
            if (_shelfInteraction == null)
                return false;

            // AckOrApply: the actor's optimistic add/take already moved the real item and produced
            // the host's absolute (type, count); the host echoes that same inventory, and applying
            // ApplyIncoming for the actor would only re-derive it (respawning generic items through
            // ApplyState). A remote/host delta has no pending id and still applies.
            WorldPrediction.AckOrApply(message,
                () => _shelfInteraction.ApplyIncoming(message));
            return true;
        }

        private void EnterPlayerShelfMutation()
        {
            _shelfInteraction?.EnterPlayerMutation();
        }

        private void ExitPlayerShelfMutation()
        {
            _shelfInteraction?.ExitPlayerMutation();
        }

        [HarmonyPatch(typeof(ShelfCompartment), "AddItem")]
        private static class AddItemPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, Item item, bool addToFront,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default : _instance.CaptureShelfMutation(__instance,
                    true, item == null ? EItemType.None : item.GetItemType(), item);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance,
                ShelfInteraction.LocalMutation __state)
            {
                _instance?.PublishShelfAdd(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "EvaluatePutItemOnShelf")]
        private static class PutItemOnShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
            {
                __state = _instance != null;
                if (__state)
                {
                    _instance.EnterPlayerShelfMutation();
                }
            }

            /// <summary>Releases the scope from the finalizer, so a throw out of the game's own
            /// EvaluatePutItemOnShelf still balances _playerMutationDepth. The finalizer always
            /// runs, so the release lives here only and stays single-shot (ExitPlayerMutation
            /// throws on an imbalanced double exit).</summary>
            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "EvaluateTakeItemFromShelf")]
        private static class TakeItemFromShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
            {
                __state = _instance != null;
                if (__state)
                {
                    _instance.EnterPlayerShelfMutation();
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnHoldStateLeftMousePress")]
        private static class BoxAddToShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool isPlayer, out bool __state)
            {
                __state = isPlayer;
                if (__state)
                {
                    _instance?.EnterPlayerShelfMutation();
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnHoldStateRightMousePress")]
        private static class BoxRemoveFromShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool isPlayer, out bool __state)
            {
                __state = isPlayer;
                if (__state)
                {
                    _instance?.EnterPlayerShelfMutation();
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "RemoveItem")]
        private static class RemoveItemPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, Item item,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default : _instance.CaptureShelfMutation(__instance,
                    false, item == null ? EItemType.None : item.GetItemType(), item);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance,
                ShelfInteraction.LocalMutation __state)
            {
                _instance?.PublishShelfRemove(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "TakeItemToHand")]
        private static class TakeItemToHandPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, bool getLastItem,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default : _instance.CaptureShelfMutation(__instance,
                    false, EItemType.None, null);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, Item __result,
                ShelfInteraction.LocalMutation __state)
            {
                if (__result != null)
                {
                    // TakeItemToHand is the one removal whose item is only known from the result,
                    // so attach the exact removed item here before the post-hoc prediction.
                    __state.Removed = __result;
                    _instance?.PublishShelfRemove(__instance, __state);
                }
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "SetCompartmentItemType")]
        private static class SetCompartmentItemTypePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, out EItemType __state)
                => __state = __instance == null ? EItemType.None : __instance.GetItemType();

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, EItemType __state)
                => _instance?.PublishLabelChange(__instance, __state);
        }

        /// <summary>Warehouse labels are removed from the price tag the same way player-shelf
        /// labels are, but the warehouse compartment is outside the player-shelf inventory
        /// protocol so the compartment-type hook above ignores it. Publish only that explicit
        /// removal; box-driven type changes stay on the warehouse box protocol.</summary>
        [HarmonyPatch(typeof(ShelfCompartment), "RemoveLabel")]
        private static class RemoveLabelPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, out EItemType __state)
                => __state = __instance == null ? EItemType.None : __instance.GetItemType();

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, EItemType __state)
            {
                if (__instance != null && __instance.GetWarehouseShelf() != null)
                {
                    _instance?.PublishLabelChange(__instance, __state, allowWarehouse: true);
                }
            }
        }
    }
}
