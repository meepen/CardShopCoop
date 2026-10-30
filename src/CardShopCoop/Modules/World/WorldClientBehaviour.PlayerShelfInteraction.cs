using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallPlayerShelfInteractionPatches()
        {
            Harmony.CreateClassProcessor(typeof(AddItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(RemoveItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(TakeItemToHandPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(SetCompartmentItemTypePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PutItemOnShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(TakeItemFromShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(BoxAddToShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(BoxRemoveFromShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(RemoveLabelPatch)).Patch();
        }

        [MessageHandler]
        private void HandleShelfState(MessageContext context, ShelfStateMessage message)
        {
            // Authoritative stock from the host, describing the compartment of the peer whose
            // action the host just applied. Our own actions settle through their response instead.
            _shelfInteraction?.ApplyRemoteState(message);
        }

        internal void ResetPlayerShelfInteractionState()
        {
            _shelfInteraction?.Reset();
        }

        /// <summary>Re-sends an item box's current contents after an undo moved an item back out of
        /// it, so the host's copy of the box loses the same item the refused shelf removal kept.</summary>
        internal static void RepublishItemBoxState(InteractablePackagingBox_Item box)
            => _instance?.PublishItemBoxState(box, true);

        /// <summary>A world load or baseline complete replaced the live scene, so a prediction whose
        /// item no longer exists cannot be reversed any more. Release exactly those; a prediction
        /// whose item is still alive keeps owning it until the host answers.</summary>
        private void FlushPlayerShelfState()
        {
            _shelfInteraction?.FlushStalePredictions();
        }

        private ShelfInteraction.LocalMutation CaptureShelfMutation(ShelfCompartment compartment,
            bool add, EItemType itemType, Item item)
        {
            return _shelfInteraction == null ? default
                : _shelfInteraction.CaptureLocalMutation(compartment, add, itemType, item);
        }

        private void PublishShelfAdd(ShelfCompartment compartment,
            ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishAdd(compartment, mutation);
        }

        private void PublishShelfRemove(ShelfCompartment compartment,
            ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishRemove(compartment, mutation);
        }

        /// <summary>Resolves the placed item box whose contents compartment is
        /// <paramref name="compartment"/>, or null when it is not a box's contents.</summary>
        private InteractablePackagingBox_Item FindItemBox(ShelfCompartment compartment)
            => _boxNetworkInteraction?.FindItemBox(compartment);

        private bool IsBoxHeld(InteractablePackagingBox box)
            => _boxNetworkInteraction != null && _boxNetworkInteraction.IsBeingHeld(box);

        /// <summary>A shelf's label (its compartment type, shown on the price tag even with no
        /// items) was removed. Removing a label clears the type on an empty compartment, which
        /// fires neither AddItem nor RemoveItem, so it needs its own publish or the other players
        /// keep showing the label. Only the removal (type -> None) is shared: an empty compartment
        /// briefly takes the incoming type before the first item lands, and that is not a label.</summary>
        private void PublishLabelChange(ShelfCompartment compartment, EItemType previousType,
            bool allowWarehouse = false)
        {
            if (_shelfInteraction == null || compartment == null || !Context.InGame
                || compartment.GetItemCount() > 0
                || compartment.GetItemType() != EItemType.None
                || previousType == EItemType.None)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[shelf] label removed on " + compartment.name + " ("
                + previousType + ").");
            _shelfInteraction.PublishRemove(compartment,
                _shelfInteraction.CaptureLabelChange(compartment, previousType, allowWarehouse));
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

                // Putting an item into an open floor box: the shelf protocol cannot address a
                // box's contents (no furniture id), so publish the box state. A held box's own
                // item-move patch (RemoveItemFromShelf) owns its publish, so only a free box
                // publishes here.
                var instance = _instance;
                var box = instance?.FindItemBox(__instance);
                if (box != null && !instance.IsBoxHeld(box))
                {
                    instance.PublishItemBoxState(box, true);
                }
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
                if (__result == null)
                {
                    return;
                }

                // TakeItemToHand is the one removal whose item is only known from the result, so
                // attach the exact removed item here before the prediction is published.
                if (__state.Action != null)
                {
                    __state.Action.Item = __result;
                }

                _instance?.PublishShelfRemove(__instance, __state);

                // Taking an item out of an open floor box must go through the box channel:
                // the shelf protocol cannot address a box's contents, so without this publish
                // the host keeps counting the taken item.
                var instance = _instance;
                var box = instance?.FindItemBox(__instance);
                if (box != null)
                {
                    instance.PublishItemBoxState(box, true);
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
