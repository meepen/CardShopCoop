using System;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private void InstallBoxNetworkInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(ItemBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurniturePurchaseBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostItemBoxOpenStatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostItemBoxContentsPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostItemBoxRemoveFromShelfPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostPackOpenerDispenseFromBoxPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostCleanserDispenseFromBoxPatch)).Patch();
            // The game's restock delivery queue drains inside RestockManager.Update; mark that
            // span so an accepted purchase's creator-assigned ids are consumed in spawn order.
            _harmony.CreateClassProcessor(typeof(HostRestockUpdatePatch)).Patch();
            // The delivery-id mirror is fed by the game's own enqueue and cleared by its data-load
            // rebuild, never by the caller, so an id cannot outlive the entry it was bound to.
            _harmony.CreateClassProcessor(typeof(HostRestockEnqueuePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostRestockInitPatch)).Patch();

            // Record-backed 1.00 warehouse takes use this factory, which does not exist on the
            // legacy live-box build. Resolve it at runtime so the single DLL stays loadable.
            var recordTakeFactory = AccessTools.Method(typeof(RestockManager),
                "SpawnPackageBoxItemAtTransform");
            if (recordTakeFactory != null)
            {
                _harmony.Patch(recordTakeFactory, postfix: new HarmonyMethod(
                    typeof(WorldHostBehaviour), nameof(RecordTakeBoxCreated)));
            }
        }

        [MessageHandler(typeof(BoxDestroyRequestMessage))]
        private void HandleBoxDestroyRequest(MessageContext context, BoxDestroyRequestMessage message)
        {
            if (_context.InGame() && IsJoinPhaseSender(context) && message != null)
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    return _boxNetworkInteraction != null
                        && _boxNetworkInteraction.HostDestroyRequested(message.BoxNetworkId,
                            message.PredictionId);
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(FurnitureBoxUpRequestMessage))]
        private void HandleFurnitureBoxUp(MessageContext context, FurnitureBoxUpRequestMessage message)
        {
            if (!_context.InGame() || !IsJoinPhaseSender(context)
                || message == null)
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (!PlacementApi.TryResolveBoxableFurnitureEntityId(message.StableEntityId,
                    WorldMessageMetadata.FurnitureIdentityScope, message.ObjectType,
                    out var match, false))
                {
                    CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request entity="
                        + (message.StableEntityId ?? "<missing>") + " type=" + message.ObjectType + ".");
                    return false;
                }

                // A box-up while the requester is moving the object supersedes that move. Clear
                // the hold (and the host's remote moving state) before validating, so a valid
                // box-up is not rejected as a moving object.
                if (PlacementApi.TryMakeObjectKey(PlacementInterop.FindKind(match), match,
                    out var holdKey))
                {
                    _placementHold?.EndIfHeldBy(holdKey, context.ConnectionId);
                }

                if (match.GetIsBoxedUp() || match.GetIsMovingObject())
                {
                    CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request entity="
                        + (message.StableEntityId ?? "<missing>") + " type=" + message.ObjectType + ".");
                    return false;
                }

                // The client is the creator and carried the box's stable id; the host binds its
                // counterpart to that exact id. A non-fresh id (already bound to a live box) must
                // reject the intent: adopting it would rebind that box, and minting a different id
                // would desync the creator's local box from the host's.
                if (!_boxNetworkInteraction.PushHostCreatedId(message.BoxNetworkId))
                {
                    CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request entity="
                        + (message.StableEntityId ?? "<missing>") + " type=" + message.ObjectType
                        + " id=" + message.BoxNetworkId + ": the creator id is not fresh.");
                    return false;
                }

                CoopPlugin.Log.LogInfo("[box-furniture] applying host box-up entity="
                    + message.StableEntityId + " type=" + message.ObjectType + ".");
                _boxNetworkInteraction.HostPredictionId = message.PredictionId;
                try
                {
                    // Box up without the host taking the box. The requesting player picked it up,
                    // so they must end up holding it; the host only performs the world mutation.
                    match.BoxUpObject(false);
                }
                finally
                {
                    _boxNetworkInteraction.HostPredictionId = System.Guid.Empty;
                }

                if (match.GetPackagingBoxShelf() == null)
                {
                    // The push parked the creator id for a box the game did not produce; release
                    // it so a later, unrelated creation cannot bind to it. No-op once consumed.
                    _boxNetworkInteraction.CancelHostCreatedId(message.BoxNetworkId);
                    CoopPlugin.Log.LogWarning("[box-furniture] box-up produced no package box entity="
                        + (message.StableEntityId ?? "<missing>") + "; released creator id.");
                    return false;
                }

                GrantFurnitureHold(match, context.ConnectionId);
                return true;
            });
        }

        /// <summary>After a client boxes furniture up, the box belongs in that player's hands, not
        /// the host's. Announce the pickup addressed to them: they take it into their local hand and
        /// stream the hand pose, while every other peer rides their avatar.</summary>
        private void GrantFurnitureHold(InteractableObject furniture, int holderConnectionId)
        {
            if (_playerBoxInteraction == null || _boxNetworkInteraction == null || furniture == null)
            {
                return;
            }

            var box = furniture.GetPackagingBoxShelf();
            if (box == null || !_boxNetworkInteraction.TryGetId(box, out var id))
            {
                CoopPlugin.Log.LogWarning(
                    "[box-furniture] boxed furniture has no network id; cannot hand off the hold.");
                return;
            }

            var pickup = new PlayerBoxPickupMessage
            {
                BoxNetworkId = id,
                HeldPosition = box.transform.position,
                HeldRotation = box.transform.rotation,
                HolderConnectionId = holderConnectionId,
            };
            _playerBoxInteraction.ApplyIncoming(pickup);
            BroadcastWorld(pickup);
        }

        [MessageHandler(typeof(BoxStateRequestMessage))]
        private void HandleBoxStateRequest(MessageContext context, BoxStateRequestMessage message)
        {
            if (_context.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    if (!_boxNetworkInteraction.TryGetBox(message.BoxNetworkId, out var box)
                        || box is not InteractablePackagingBox_Item item)
                    {
                        return false;
                    }

                    // A box's contents are only ever rewritten by the player holding it. Opening
                    // and closing a box on the floor is legitimate, so an open-flag-only request
                    // stays valid; a contents change from a non-holder is rejected outright. The
                    // sender must be the mirrored holder when one is known, so one peer can never
                    // rewrite a box another peer is holding.
                    if (message.ContentsChanged)
                    {
                        var holder = 0;
                        var mirrored = _playerBoxInteraction != null
                            && _playerBoxInteraction.TryGetRemoteHolder(message.BoxNetworkId,
                                out holder);
                        var accepted = mirrored
                            ? holder == context.ConnectionId
                            : _boxNetworkInteraction.IsBeingHeld(item);
                        if (!accepted)
                        {
                            CoopPlugin.Log.LogWarning("[box-id] rejected box state request id="
                                + message.BoxNetworkId + " name=" + item.name + " from conn="
                                + context.ConnectionId + ": the sender does not hold the box (isHeld="
                                + _boxNetworkInteraction.IsBeingHeld(item) + " mirroredHolder="
                                + (mirrored ? holder.ToString() : "none") + ").");
                            return false;
                        }
                    }

                    // Validates the client's contents payload against IsValidItem/MaxItemCount
                    // before ApplyItemState would spawn it.
                    if (!_boxNetworkInteraction.ApplyHostBoxState(item, message))
                    {
                        return false;
                    }

                    // The requesting player is authoritative for the box they are holding: the
                    // game's own operation already applied their local mutation and the intent
                    // carries its exact result. Echoing it back would rebuild their live
                    // compartment from an older snapshot and overwrite newer local changes
                    // (the count ping-pong), so relay the mirror only to the other peers.
                    RelayItemBoxState(item, context.ConnectionId);
                    return true;
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        /// <summary>Publishes the host's own item-box mutation to every client.</summary>
        private void BroadcastItemBoxState(InteractablePackagingBox_Item item)
        {
            var message = BuildItemBoxState(item);
            if (message != null)
                BroadcastWorld(message);
        }

        /// <summary>Publishes the mirrored state of a client's own item-box mutation to the
        /// other peers. The originating client already holds this exact state, so it is excluded
        /// and its live compartment is left untouched.</summary>
        private void RelayItemBoxState(InteractablePackagingBox_Item item, int excludeConnectionId)
        {
            var message = BuildItemBoxState(item);
            if (message != null)
                RelayWorldExcept(excludeConnectionId, message);
        }

        private BoxStateMessage BuildItemBoxState(InteractablePackagingBox_Item item)
        {
            if (_boxNetworkInteraction == null || item == null
                || !_boxNetworkInteraction.TryGetId(item, out var id))
            {
                return null;
            }

            var compartment = item.m_ItemCompartment;
            return new BoxStateMessage
            {
                BoxNetworkId = id,
                IsBoxOpened = BoxNetworkInteraction.IsBoxOpen(item),
                ContentsChanged = true,
                ItemType = compartment == null ? EItemType.None : compartment.GetItemType(),
                ItemCount = compartment == null ? 0 : compartment.GetItemCount(),
            };
        }

        private void RegisterCreatedBox(InteractablePackagingBox box)
        {
            // FurnitureShopUIScreen creates its shelf box at Vector3.zero, then its outer
            // factory moves it to the random package spawn point. Allocate the ID here, but
            // wait for that outer factory before describing the box to clients.
            if (box is InteractablePackagingBox_Shelf && _furniturePurchaseSpawnDepth > 0)
            {
                _boxNetworkInteraction?.EnsureHostId(box);
                _pendingFurniturePurchaseBoxes.Push((InteractablePackagingBox_Shelf)box);
                return;
            }

            _boxNetworkInteraction?.RegisterHostCreated(box);
            if (box is InteractablePackagingBox_Shelf furnitureBox
                && _boxNetworkInteraction?.TryGetId(furnitureBox, out var id) == true)
            {
                CoopPlugin.Log.LogInfo("[box-furniture] authoritative package id=" + id
                    + " assigned before vanilla hold transition.");

                // A player-initiated box-up holds the box before it gets an id, so the pickup was
                // ignored as unregistered. Now that the id exists, announce the hold so observers
                // put the box in this player's hands.
                if (_boxNetworkInteraction.IsBeingHeld(furnitureBox))
                {
                    CoopPlugin.Log.LogInfo("[box-furniture] announcing host furniture hold id=" + id + ".");
                    _playerBoxInteraction?.PublishHostHeld(furnitureBox, id);
                }
            }
        }

        private void BeginFurniturePurchaseBox()
        {
            _furniturePurchaseSpawnDepth++;
        }

        private void CompleteFurniturePurchaseBox(EObjectType objectType, Vector3 spawnPosition)
        {
            if (_furniturePurchaseSpawnDepth <= 0)
            {
                throw new InvalidOperationException("Furniture package spawn completed without a matching start.");
            }

            _furniturePurchaseSpawnDepth--;
            if (_furniturePurchaseSpawnDepth != 0)
            {
                return;
            }

            if (_pendingFurniturePurchaseBoxes.Count == 0)
            {
                CoopPlugin.Log.LogWarning("[box-id] furniture purchase completed without a captured package box: "
                    + objectType + " at " + spawnPosition + ".");
                return;
            }

            var finalizedBox = _pendingFurniturePurchaseBoxes.Pop();
            if (finalizedBox == null || finalizedBox.GetBoxedObjectType() != objectType)
            {
                CoopPlugin.Log.LogWarning("[box-id] furniture purchase completed with an unexpected package box: "
                    + objectType + " at " + spawnPosition + ".");
                return;
            }

            RegisterCreatedBox(finalizedBox);
        }

        private static void RecordTakeBoxCreated(InteractablePackagingBox_Item __result)
        {
            _instance?.RegisterCreatedBox(__result);
        }

        private void NotifyDestroyedBox(InteractablePackagingBox box)
        {
            // The game is tearing this box down. If this peer is holding it, release the hand first
            // or the destroyed object stays parented to the hand as a ghost.
            _playerBoxInteraction?.ReleaseHeldObject(box);
            _boxNetworkInteraction?.HostNotifyDestroyed(box);
        }

        /// <summary>Host-side mirror of the client query: true when the box engine already owns
        /// this packaging box. Lets shared placement code treat the box engine as the single
        /// owner on both roles.</summary>
        internal static bool IsKnownPackagingBox(InteractablePackagingBox box)
            => _instance?._boxNetworkInteraction?.IsKnownBox(box) == true;

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxItem")]
        private static class ItemBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __result)
            {
                _instance?.RegisterCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxCard")]
        private static class CardBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __result)
            {
                _instance?.RegisterCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxShelf")]
        private static class FurnitureBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __result)
            {
                _instance?.RegisterCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(ShelfManager), "SpawnInteractableObjectInPackageBox")]
        private static class FurniturePurchaseBoxCreatedPatch
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                _instance?.BeginFurniturePurchaseBox();
            }

            [HarmonyPostfix]
            private static void Postfix(EObjectType objType, Vector3 spawnPos)
            {
                _instance?.CompleteFurniturePurchaseBox(objType, spawnPos);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "Update")]
        private static class HostRestockUpdatePatch
        {
            [HarmonyPrefix]
            private static void Prefix()
                => _instance?._boxNetworkInteraction?.BeginDeliverySpawns();

            [HarmonyPostfix]
            private static void Postfix()
                => _instance?._boxNetworkInteraction?.EndDeliverySpawns();
        }

        /// <summary>The game appended a delivery entry that will spawn <c>count</c> boxes. Commit
        /// the creator-assigned ids staged for this call to that entry. Running after the game's
        /// own append is what keeps the mod queue in lockstep with the game waiting list.</summary>
        [HarmonyPatch(typeof(RestockManager), nameof(RestockManager.SpawnPackageBoxItemMultipleFrame))]
        private static class HostRestockEnqueuePatch
        {
            [HarmonyPostfix]
            private static void Postfix(int count)
                => _instance?._boxNetworkInteraction?.CommitHostDeliveryEntry(count);
        }

        /// <summary>The game clears and rebuilds its waiting list when game data finishes loading.
        /// Resync the mirror to the rebuilt, id-less entries so no creator-assigned id outlives its
        /// entry and positions stay aligned.</summary>
        [HarmonyPatch(typeof(RestockManager), "Init")]
        private static class HostRestockInitPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
                => _instance?._boxNetworkInteraction?.ResyncHostDeliveryEntries();
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnDestroyed")]
        private static class ItemBoxDestroyedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance)
            {
                _instance?.NotifyDestroyedBox(__instance);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Card), "OnDestroyed")]
        private static class CardBoxDestroyedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __instance)
            {
                _instance?.NotifyDestroyedBox(__instance);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnDestroyed")]
        private static class FurnitureBoxDestroyedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __instance)
            {
                _instance?.NotifyDestroyedBox(__instance);
            }
        }

        /// <summary>Republishes an item box's descriptor after the host itself opens, closes, or
        /// changes its contents so clients mirror the authoritative state.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "SetOpenCloseBox")]
        private static class HostItemBoxOpenStatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    _instance?.BroadcastItemBoxState(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "DispenseItem")]
        private static class HostItemBoxContentsPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance, out int __state)
                => __state = __instance?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer,
                int __state)
            {
                // DispenseItem also stores a whole box onto a box compartment, which changes no
                // contents; only a real item dispense publishes.
                if (isPlayer && __instance?.m_ItemCompartment != null
                    && __instance.m_ItemCompartment.GetItemCount() != __state)
                {
                    _instance?.BroadcastItemBoxState(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "RemoveItemFromShelf")]
        private static class HostItemBoxRemoveFromShelfPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    _instance?.BroadcastItemBoxState(__instance);
                }
            }
        }

        /// <summary>The host feeding a machine straight from an open box moves the item with
        /// <c>m_ItemCompartment.RemoveItem</c>, which none of the box-content patches observe, so
        /// the box silently lost a pack without a broadcast and guests kept it. Republish the
        /// source box when its count actually changed.</summary>
        private static void BroadcastDispensedSourceBox(InteractablePackagingBox_Item itemBox,
            int priorCount)
        {
            if (itemBox?.m_ItemCompartment != null
                && itemBox.m_ItemCompartment.GetItemCount() != priorCount)
            {
                _instance?.BroadcastItemBoxState(itemBox);
            }
        }

        [HarmonyPatch(typeof(InteractableAutoPackOpener), "DispenseItemFromBox")]
        private static class HostPackOpenerDispenseFromBoxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item itemBox, out int __state)
                => __state = itemBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item itemBox, int __state)
                => BroadcastDispensedSourceBox(itemBox, __state);
        }

        [HarmonyPatch(typeof(InteractableAutoCleanser), "DispenseItemFromBox")]
        private static class HostCleanserDispenseFromBoxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item itemBox, out int __state)
                => __state = itemBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item itemBox, int __state)
                => BroadcastDispensedSourceBox(itemBox, __state);
        }
    }
}
