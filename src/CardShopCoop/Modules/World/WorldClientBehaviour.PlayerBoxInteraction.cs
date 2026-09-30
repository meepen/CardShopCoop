using System;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.PlayTable;
using CardShopCoop.Modules.Presence;
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

        /// <summary>Client: subscribes to remote-avatar spawns so a held box announced in the join
        /// baseline before the holder's avatar exists moves onto the skeleton once it does.</summary>
        private void InstallPlayerBoxPresenceHook()
            => PresenceApi.RemoteAvatarsChanged += OnRemoteAvatarsChanged;

        private void UninstallPlayerBoxPresenceHook()
            => PresenceApi.RemoteAvatarsChanged -= OnRemoteAvatarsChanged;

        private void OnRemoteAvatarsChanged() => _playerBoxInteraction?.ReattachRemoteHolds();

        /// <summary>The host connection dropped: release every mirrored remote hold now so no box
        /// stays parented to a departed holder's avatar while the session teardown unwinds.</summary>
        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                _playerBoxInteraction?.Reset();
            }
        }

        private void InstallPlayerBoxInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(PlayerBoxPickupPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxThrowPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxDropPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxPlacementPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxUpPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxOpenStatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxContentsPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxRemoveFromShelfPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PackOpenerDispenseFromBoxPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CleanserDispenseFromBoxPatch)).Patch();
        }

        /// <summary>Pre-action state captured for a local furniture box-up. The hook prefix only
        /// reads it; the postfix forwards the prediction once the game has performed the box-up.</summary>
        private sealed class FurnitureBoxUpCapture
        {
            internal InteractableObject Object;
            internal string EntityId;
            internal Vector3 Position;
        }

        private bool _applyingFurniturePrediction;

        private void ApplyPredictedFurnitureBoxUp(InteractableObject obj)
        {
            // A destroyed subject cannot be boxed up again; skip instead of throwing during a
            // reconcile replay (the sibling undo already guards this).
            if (obj == null)
                return;

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
            private static void Prefix(InteractableObject __instance, bool holdBox,
                out FurnitureBoxUpCapture __state)
            {
                __state = null;
                if (_instance == null || __instance == null || !holdBox
                    || _instance._applyingFurniturePrediction)
                {
                    // Our own apply/replay drives BoxUpObject with holdBox:false; the host side
                    // has no local player to forward.
                    return;
                }

                // Play tables have a stable identity and their own occupied/reservation ownership
                // path. Never let the generic nearest-object request race that intent.
                if (__instance is InteractablePlayTable)
                {
                    return;
                }

                // Boxing up supersedes a move: release the hold first so the host clears its moving
                // state before it validates the box-up request. This is local hold bookkeeping, not
                // a game-state gate; the game still runs BoxUpObject.
                _instance._placementHold?.LocalEnded(__instance);
                if (!PlacementApi.TryMakeBoxableFurnitureEntityId(__instance,
                    WorldMessageMetadata.FurnitureIdentityScope,
                    out var entityId))
                {
                    CoopPlugin.Log.LogWarning("[box-furniture] box-up has no placement identity; type="
                        + __instance.m_ObjectType + "; not forwarding.");
                    return;
                }

                __state = new FurnitureBoxUpCapture
                {
                    Object = __instance,
                    EntityId = entityId,
                    Position = __instance.transform.position,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(FurnitureBoxUpCapture __state)
            {
                if (__state == null || _instance == null)
                {
                    return;
                }

                var obj = __state.Object;
                // The client is the creator: the box the game just made gets its stable id here and
                // the host binds its counterpart to the same id.
                var package = obj.GetPackagingBoxShelf();
                var boxId = package != null && _instance._boxNetworkInteraction != null
                    ? _instance._boxNetworkInteraction.AssignClientCreated(package)
                    : Guid.Empty;
                var intent = new FurnitureBoxUpRequestMessage
                {
                    ObjectType = obj.m_ObjectType,
                    Position = __state.Position,
                    StableEntityId = __state.EntityId,
                    BoxNetworkId = boxId,
                };
                // The game already boxed the furniture up; the prediction only records how to redo
                // and undo that change through the game's own methods.
                WorldPrediction.Predict(WorldPrediction.BoxesScope, intent,
                    () => _instance.ApplyPredictedFurnitureBoxUp(obj),
                    () => _instance.UndoPredictedFurnitureBoxUp(obj));
            }
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

            // Pickup/placement/throw are only ever echoed back after the host applied the guest's
            // own intent (a rejection comes as a prediction rollback), so this confirms the
            // prediction. Applying it authoritatively would first undo the hold/move/throw and
            // replay it: the box re-lerps into the hand, or the throw is re-applied.
            WorldPrediction.AckOrApply(message,
                () => _playerBoxInteraction.ApplyIncoming(message));
            return true;
        }

        [HarmonyPatch(typeof(InteractablePackagingBox), "StartHoldBox")]
        private static class PlayerBoxPickupPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // Capture only: the game decides whether a hold is valid (for example while the
                // box is in its placement preview) and owns the mutation.
                __state = _instance == null
                    ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, false);
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
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // A box just taken or boxed up is still lerping into the hand, and ThrowBox does
                // not stop that lerp: the thrown box kept being dragged to the hand until the lerp
                // finished, which is what made the first throw after a pick-up look refused. Throw
                // from where the box actually is.
                if (isPlayer)
                {
                    __instance?.StopLerpToTransform();
                }

                // Capture only: the game decides whether a throw is valid and owns the mutation.
                __state = _instance == null
                    ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, true);
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxThrow(__instance, __state);
            }
        }

        /// <summary>The local player dropped the box for the game's placement preview. That
        /// release must stop the hold lerp for the same reason a throw does: otherwise the box is
        /// dragged back toward the hand while the moving-object logic aims it.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox), "DropBox")]
        private static class PlayerBoxDropPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    __instance?.StopLerpToTransform();
                }
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
            if (_playerBoxInteraction?.IsCoveredHold == true)
            {
                // A container take is creating this box on the host right now; its own op carries
                // the authoritative open/closed state. A request sent during that window names a
                // box the host has not created yet and would be refused.
                return;
            }

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
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance, out int __state)
                => __state = __instance?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer,
                int __state)
            {
                // DispenseItem also stores the whole box onto a box compartment (the warehouse
                // store). That changes no contents, and by the time such a request reached the
                // host the store had already run and released the hold, so it was refused and
                // looked like a rollback. Only a real item dispense (the count moved) is a
                // contents change.
                if (isPlayer && __instance?.m_ItemCompartment != null
                    && __instance.m_ItemCompartment.GetItemCount() != __state)
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

        /// <summary>A guest feeding the machine straight from an open delivery box moves the item
        /// out with <c>itemBox.m_ItemCompartment.RemoveItem</c>, a path that never fires
        /// <see cref="ItemBoxContentsPatch"/>. Publish the source box after the move so the host
        /// removes the same item instead of keeping a duplicate in its box.</summary>
        private static void PublishDispensedSourceBox(InteractablePackagingBox_Item itemBox,
            int priorCount)
        {
            if (itemBox?.m_ItemCompartment != null
                && itemBox.m_ItemCompartment.GetItemCount() != priorCount)
            {
                _instance?.PublishItemBoxState(itemBox, true);
            }
        }

        [HarmonyPatch(typeof(InteractableAutoPackOpener), "DispenseItemFromBox")]
        private static class PackOpenerDispenseFromBoxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item itemBox, out int __state)
                => __state = itemBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item itemBox, int __state)
                => PublishDispensedSourceBox(itemBox, __state);
        }

        [HarmonyPatch(typeof(InteractableAutoCleanser), "DispenseItemFromBox")]
        private static class CleanserDispenseFromBoxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item itemBox, out int __state)
                => __state = itemBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item itemBox, int __state)
                => PublishDispensedSourceBox(itemBox, __state);
        }
    }
}
