using CardShopCoop.Net;
using CardShopCoop.Modules.PlayTable;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private PlayerBoxInteraction.LocalAction CapturePlayerBoxAction(
            InteractablePackagingBox box, bool isPlayer, bool alignBody)
        {
            return _playerBoxInteraction == null
                ? default
                : _playerBoxInteraction.CaptureLocalAction(box, isPlayer, alignBody);
        }

        private void InstallPlayerBoxInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(PlayerBoxPickupPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxThrowPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxPlacementPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxUpPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxOpenStatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxContentsPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxRemoveFromShelfPatch)).Patch();
        }

        internal static bool ForwardFurnitureBoxUp(InteractableObject obj, bool holdBox)
        {
            if (_instance?._applyingFurniturePrediction == true)
                return true;
            // Play tables have a stable identity and their own occupied/reservation ownership
            // path. Never let the generic nearest-object request race that intent.
            if (obj is InteractablePlayTable)
                return true;

            if (!holdBox || obj == null)
                return true;
            // Boxing up supersedes a move: release the hold first so the host clears its moving
            // state before it validates the box-up request.
            _instance?._placementHold?.LocalEnded(obj);
            if (!PlacementApi.TryMakeBoxableFurnitureEntityId(obj,
                WorldMessageMetadata.FurnitureIdentityScope,
                out var entityId))
            {
                CoopPlugin.Log.LogWarning("[box-furniture] blocked box-up with no placement identity; type="
                    + obj.m_ObjectType + ".");
                return false;
            }

            CoopPlugin.Log.LogInfo("[box-furniture] forwarding box-up entity=" + entityId
                + " type=" + obj.m_ObjectType + " position=" + obj.transform.position + ".");
            var intent = new FurnitureBoxUpRequestMessage
            {
                ObjectType = obj.m_ObjectType,
                Position = obj.transform.position,
                StableEntityId = entityId,
            };
            WorldPrediction.Predict(WorldPrediction.BoxesScope, intent,
                () => _instance.ApplyPredictedFurnitureBoxUp(obj),
                () => _instance.UndoPredictedFurnitureBoxUp(obj));

            return false;
        }

        private bool _applyingFurniturePrediction;

        private void ApplyPredictedFurnitureBoxUp(InteractableObject obj)
        {
            _applyingFurniturePrediction = true;
            try
            {
                obj.BoxUpObject(false);
            }
            finally
            {
                _applyingFurniturePrediction = false;
            }
        }

        private void UndoPredictedFurnitureBoxUp(InteractableObject obj)
        {
            if (obj == null)
                return;

            _applyingFurniturePrediction = true;
            try
            {
                // Reverse the prediction through the game's own place path. EmptyBoxShelf
                // detaches the boxed object before the package tears down, so this removes
                // only the predicted package and leaves the placed furniture (and therefore
                // its placement identity) intact. Calling package.OnDestroyed() directly
                // instead routes into InteractablePackagingBox_Shelf.OnDestroyed, which
                // destroys m_BoxedObject, drops the furniture from ShelfManager and forgets
                // its placement id, stranding the host's authoritative descriptor.
                // PlaceBoxedObject also detaches the object's packaging-box reference, so the
                // authoritative apply spawns a clean package rather than adopting the one that
                // is already scheduled to disappear.
                PlacementInterop.PlaceBoxedObject(obj, obj.transform.position,
                    obj.transform.rotation);
            }
            finally
            {
                _applyingFurniturePrediction = false;
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class FurnitureBoxUpPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance, bool holdBox)
                => ForwardFurnitureBoxUp(__instance, holdBox);
        }

        internal void ResetPlayerBoxInteractionState()
        {
            _playerBoxInteraction?.Reset();
        }

        [MessageHandler(typeof(PlayerBoxPickupMessage))]
        private void HandlePlayerBoxPickup(MessageContext context, PlayerBoxPickupMessage message)
        {
            ApplyPlayerBoxAction(message);
        }

        [MessageHandler(typeof(PlayerBoxPlacementMessage))]
        private void HandlePlayerBoxPlacement(MessageContext context, PlayerBoxPlacementMessage message)
        {
            ApplyPlayerBoxAction(message);
        }

        [MessageHandler(typeof(PlayerBoxThrowMessage))]
        private void HandlePlayerBoxThrow(MessageContext context, PlayerBoxThrowMessage message)
        {
            ApplyPlayerBoxAction(message);
        }

        private void PublishPlayerBoxPickup(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action)
        {
            _playerBoxInteraction?.PublishPickup(box, action);
        }

        private void PublishPlayerBoxPlacement(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action)
        {
            _playerBoxInteraction?.PublishPlacement(box, action);
        }

        private void PublishPlayerBoxThrow(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action)
        {
            _playerBoxInteraction?.PublishThrow(box, action);
        }

        private bool ApplyPlayerBoxAction(PlayerBoxInteractionMessage message)
        {
            if (_playerBoxInteraction == null)
                return false;

            WorldPrediction.ApplyAuthoritative(message,
                () => _playerBoxInteraction.ApplyIncoming(message));
            return true;
        }

        [HarmonyPatch(typeof(InteractablePackagingBox), "StartHoldBox")]
        private static class PlayerBoxPickupPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                __state = _instance == null
                    ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, false);
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxPickup(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox), "ThrowBox")]
        private static class PlayerBoxThrowPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                __state = _instance == null
                    ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, true);
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxThrow(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class PlayerBoxPlacementPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // A packaging box finishes its placement here, not at DropBox: DropBox only drops
                // it for aiming in moving mode. Only the local player calls PlaceMovedObject on a
                // box, and CapturePlayerBoxAction ignores unregistered boxes.
                __state = _instance != null && __instance is InteractablePackagingBox box
                    && PlacementInterop.ReadMoveValidity(box)
                    ? _instance.CapturePlayerBoxAction(box, true, false)
                    : default;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                if (__instance is InteractablePackagingBox box)
                {
                    _instance?.PublishPlayerBoxPlacement(box, __state);
                }
            }
        }

        /// <summary>Sends the item box's authoritative open flag and contents after a local
        /// player mutation so the host can mirror it and republish the box descriptor.</summary>
        internal void PublishItemBoxState(InteractablePackagingBox_Item item, bool contentsChanged)
        {
            if (item == null || _boxNetworkInteraction == null
                || !_boxNetworkInteraction.TryGetId(item, out var id))
            {
                return;
            }

            var compartment = item.m_ItemCompartment;
            SendClientIntent(new BoxStateRequestMessage
            {
                BoxNetworkId = id,
                IsBoxOpened = BoxNetworkInteraction.IsBoxOpen(item),
                ContentsChanged = contentsChanged,
                ItemType = compartment == null ? EItemType.None : compartment.GetItemType(),
                ItemCount = compartment == null ? 0 : compartment.GetItemCount(),
            });
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "SetOpenCloseBox")]
        private static class ItemBoxOpenStatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    _instance?.PublishItemBoxState(__instance, false);
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "DispenseItem")]
        private static class ItemBoxContentsPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    _instance?.PublishItemBoxState(__instance, true);
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "RemoveItemFromShelf")]
        private static class ItemBoxRemoveFromShelfPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    _instance?.PublishItemBoxState(__instance, true);
                }
            }
        }
    }
}
