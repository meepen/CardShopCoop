using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.PlayTable;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private void InstallBoxNetworkInteractionPatches()
        {
            // Box creation, destruction, identity and baselines are installed by the
            // GameObjectIdentity module. Only contents/open state and delivery bookkeeping
            // belong to the world box engine.
            Harmony.CreateClassProcessor(typeof(HostItemBoxOpenStatePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostItemBoxContentsPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostItemBoxRemoveFromShelfPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostPackOpenerDispenseFromBoxPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostCleanserDispenseFromBoxPatch)).Patch();
            // The game's restock delivery queue drains inside RestockManager.Update; mark that
            // span so an accepted purchase's creator-assigned ids are consumed in spawn order.
            Harmony.CreateClassProcessor(typeof(HostRestockUpdatePatch)).Patch();
            // The delivery-id mirror is fed by the game's own enqueue and cleared by its data-load
            // rebuild, never by the caller, so an id cannot outlive the entry it was bound to.
            Harmony.CreateClassProcessor(typeof(HostRestockEnqueuePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostRestockInitPatch)).Patch();
        }

        /// <summary>True when an authenticated, fully joined peer may act on shared world state.
        /// A guest in the transfer window already plays from the transferred save, so it counts
        /// as joined for these actions.</summary>
        private static bool CanApplyWorldAction(CoopMessageContext context)
            => context != null && context.InGame && context.IsAuthenticated;

        /// <summary>The host's canonical player id for the sender. A peer handle is the only
        /// identity the world protocol is allowed to carry; the box interaction DTO still names
        /// its holder with the same canonical id.</summary>
        private static int PlayerIdOf(ICoopPeer peer) => peer?.Player?.Id ?? 0;

        [MessageHandler]
        private ActionOutcome HandleBoxDestroyRequest(CoopMessageContext context,
            BoxDestroyRequestMessage message)
        {
            if (!CanApplyWorldAction(context) || message == null
                || message.BoxNetworkId == Guid.Empty)
            {
                return ActionOutcome.Reject("box destroy request has no actionable identity");
            }

            if (!_boxNetworkInteraction.TryGetBox(message.BoxNetworkId, out var box))
            {
                // The creator destroyed the box before the host materialized it. Retire the
                // staged id instead of rejecting: the client's local destroy already happened,
                // and a rollback would resurrect the box.
                _boxNetworkInteraction.CancelHostCreatedId(message.BoxNetworkId);
                CancelStagedDeliveryId(message.BoxNetworkId);
                CoopPlugin.Log.LogInfo("[box-id] destroy for id=" + message.BoxNetworkId
                    + " has no live host box; staged id canceled.");
                return ActionOutcome.Applied();
            }

            return _boxNetworkInteraction.HostDestroyRequested(box)
                ? ActionOutcome.Applied()
                : ActionOutcome.Reject("the host could not destroy the box");
        }

        [MessageHandler]
        private ActionOutcome HandleFurnitureDestroy(CoopMessageContext context,
            FurnitureDestroyIntent message)
        {
            if (!CanApplyWorldAction(context) || message?.Entity == null)
            {
                return ActionOutcome.Reject("furniture destroy request has no live furniture");
            }

            var furniture = message.Entity;
            if (PlacementInterop.FindKind(furniture) < 0)
            {
                CoopPlugin.Log.LogWarning("[box-furniture] dropped destroy request: "
                    + furniture.name + " is not a live placement object.");
                return ActionOutcome.Reject("the furniture is not a live placement object");
            }

            // The game's own destroy path retires the furniture and the package view; the
            // placement removal publishes the entity removal to every peer.
            return PlacementObjectIdentity.Destroy(furniture)
                ? ActionOutcome.Applied()
                : ActionOutcome.Reject("the host could not destroy the furniture");
        }

        [MessageHandler]
        private ActionOutcome HandleFurnitureBoxUp(CoopMessageContext context,
            FurnitureBoxUpRequestMessage message)
        {
            if (!CanApplyWorldAction(context) || message == null)
            {
                return ActionOutcome.Reject("box-up request has no authenticated sender");
            }

            var match = message.Entity;
            if (match == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[box-furniture] dropped box-up request: the furniture did not resolve to a live object.");
                return ActionOutcome.Reject("the furniture did not resolve to a live object");
            }

            var requester = message.Peer;
            if (requester == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[box-furniture] dropped box-up request: the sender peer is unknown.");
                return ActionOutcome.Reject("box-up request has no authenticated sender");
            }

            if (match.m_ObjectType != message.ObjectType)
            {
                CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request name="
                    + match.name + " type=" + message.ObjectType + ": the live object is "
                    + match.m_ObjectType + ".");
                return ActionOutcome.Reject("the live object type does not match the box-up request");
            }

            // A box-up while the requester is moving the object supersedes that move. Clear the
            // hold (and the host's remote moving state) before validating, so a valid box-up is
            // not rejected as a moving object.
            var requesterId = PlayerIdOf(requester);
            _placementHold?.EndIfHeldBy(match, requester);

            if (match is InteractablePlayTable table
                && !PlayTableHostBehaviour.IsBoxUpAllowed(table))
            {
                CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request name="
                    + match.name + " type=" + message.ObjectType
                    + ": table is occupied, reserved, playing, boxed, or tournament-numbered.");
                return ActionOutcome.Reject("the play table cannot be boxed up");
            }

            if (match.GetIsBoxedUp() || match.GetIsMovingObject())
            {
                CoopPlugin.Log.LogWarning("[box-furniture] rejected box-up request name="
                    + match.name + " type=" + message.ObjectType + ".");
                return ActionOutcome.Reject("the furniture is already boxed or moving");
            }

            CoopPlugin.Log.LogInfo("[box-furniture] applying host box-up name="
                + match.name + " type=" + message.ObjectType + ".");
            // Box up without the host taking the box. The requesting player picked it up, so they
            // must end up holding it; the host only performs the world mutation. The package is a
            // view of this furniture and shares its session Guid; no new box identity is minted
            // or staged.
            match.BoxUpObject(false);

            if (match.GetPackagingBoxShelf() == null)
            {
                CoopPlugin.Log.LogWarning("[box-furniture] box-up produced no package box name="
                    + match.name + ".");
                return ActionOutcome.Reject("the box-up produced no package box");
            }

            // The host's BoxUpObject mutation postfix publishes the boxed placement state
            // (IsBoxed + boxed pose); now hand the box to the requesting player.
            GrantFurnitureHold(match, requesterId, message.ActionId);
            return ActionOutcome.Applied();
        }

        /// <summary>After a client boxes furniture up, the box belongs in that player's hands, not
        /// the host's. Announce the pickup addressed to them: they take it into their local hand
        /// and stream the hand pose, while every other peer rides their avatar. The pickup carries
        /// the box-up action id for diagnostics; the action's own response is what settles the
        /// prediction.</summary>
        private void GrantFurnitureHold(InteractableObject furniture, int holderConnectionId,
            Guid actionId)
        {
            if (_playerBoxInteraction == null || _boxNetworkInteraction == null || furniture == null)
            {
                return;
            }

            var box = furniture.GetPackagingBoxShelf();
            if (box == null || !_boxNetworkInteraction.TryGetId(box, out _))
            {
                CoopPlugin.Log.LogWarning(
                    "[box-furniture] boxed furniture has no network id; cannot hand off the hold.");
                return;
            }

            var pickup = new PlayerBoxPickupMessage
            {
                ActionId = actionId,
                Box = box,
                HeldPosition = box.transform.position,
                HeldRotation = box.transform.rotation,
                HolderConnectionId = holderConnectionId,
            };
            _playerBoxInteraction.ApplyIncoming(pickup);
            Context.Broadcast(pickup);
        }

        [MessageHandler]
        private ActionOutcome HandleBoxStateRequest(CoopMessageContext context,
            BoxStateRequestMessage message)
        {
            if (!CanApplyWorldAction(context) || message?.Box == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[box-id] dropped box state request: the box did not resolve to a live object.");
                return ActionOutcome.Reject("the box did not resolve to a live object");
            }

            if (message.Box is not InteractablePackagingBox_Item item
                || !_boxNetworkInteraction.TryGetId(item, out var boxId))
            {
                CoopPlugin.Log.LogWarning(
                    "[box-id] dropped box state request: the live box has no identity.");
                return ActionOutcome.Reject("the live box has no network identity");
            }

            var requester = message.Peer;
            if (requester == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[box-id] dropped box state request: the sender peer is unknown.");
                return ActionOutcome.Reject("box state request has no authenticated sender");
            }

            // A box's contents are rewritten by the player acting on it: the holder whose hand
            // mirrors this box, or any player using an open box that nobody carries (an open box
            // on the floor). The host's replica also reports itself as held for a mirrored remote
            // hold, so the holder must be resolved through the mirror rather than the local hold
            // flag; the flag only distinguishes "free box" from "box in somebody's hand". A box
            // the host itself carries belongs to the host, so a client can never rewrite it.
            var requesterId = PlayerIdOf(requester);
            var held = _boxNetworkInteraction.IsBeingHeld(item);
            var holder = 0;
            var mirrored = _playerBoxInteraction != null
                && _playerBoxInteraction.TryGetRemoteHolder(boxId, out holder);
            var open = BoxNetworkInteraction.IsBoxOpen(item);
            var requesterHolds = mirrored && holder == requesterId;
            var freeOpenBox = !held && open;
            if (message.ContentsChanged && !requesterHolds && !freeOpenBox)
            {
                CoopPlugin.Log.LogWarning("[box-id] rejected box state request id="
                    + boxId + " name=" + item.name + " from " + requester.Name
                    + ": the sender is not the box holder (isHeld=" + held + " open=" + open
                    + " mirroredHolder=" + (mirrored ? holder.ToString() : "none") + ").");
                return ActionOutcome.Reject("the sender is not the box holder",
                    BuildItemBoxResult(item));
            }

            // Validates the client's contents payload against IsValidItem/MaxItemCount before
            // ApplyItemState would spawn it.
            if (!_boxNetworkInteraction.ApplyHostBoxState(item, message))
            {
                return ActionOutcome.Reject("the box contents payload is invalid",
                    BuildItemBoxResult(item));
            }

            // The requesting player is authoritative for the box they are holding: the game's own
            // operation already applied their local mutation and the intent carries its exact
            // result. Echoing it back would rebuild their live compartment from an older snapshot
            // and overwrite newer local changes (the count ping-pong), so relay the mirror only to
            // the other peers.
            RelayItemBoxState(item, requester);
            return ActionOutcome.Applied(BuildItemBoxResult(item));
        }

        /// <summary>Publishes the host's own item-box mutation to every client.</summary>
        private void BroadcastItemBoxState(InteractablePackagingBox_Item item)
        {
            var message = BuildItemBoxState(item);
            if (message != null)
            {
                Context.Broadcast(message);
            }

        }

        /// <summary>Publishes the mirrored state of a client's own item-box mutation to the
        /// other peers. The originating client already holds this exact state, so it is excluded
        /// and its live compartment is left untouched.</summary>
        private void RelayItemBoxState(InteractablePackagingBox_Item item, ICoopPeer except)
        {
            var message = BuildItemBoxState(item);
            if (message == null)
            {
                return;
            }

            if (except == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] item-box state for " + item.name
                    + " has no sender to exclude; broadcasting it instead.");
                Context.Broadcast(message);
                return;
            }

            Context.BroadcastExcept(except, message);
        }

        private BoxStateMessage BuildItemBoxState(InteractablePackagingBox_Item item)
        {
            if (_boxNetworkInteraction == null || item == null
                || !_boxNetworkInteraction.TryGetId(item, out _))
            {
                return null;
            }

            var compartment = item.m_ItemCompartment;
            return new BoxStateMessage
            {
                Box = item,
                IsBoxOpened = BoxNetworkInteraction.IsBoxOpen(item),
                ContentsChanged = true,
                ItemType = compartment == null ? EItemType.None : compartment.GetItemType(),
                ItemCount = compartment == null ? 0 : compartment.GetItemCount(),
            };
        }

        /// <summary>The actor-only post-action state returned by a box-state action on accept and
        /// on reject. It mirrors the broadcast state so the acting client's live box lands on the
        /// host's contents/open flag without receiving its own mutation echoed back.</summary>
        private BoxStateResult BuildItemBoxResult(InteractablePackagingBox_Item item)
        {
            if (item == null)
            {
                return null;
            }

            var compartment = item.m_ItemCompartment;
            return new BoxStateResult
            {
                Box = item,
                IsBoxOpened = BoxNetworkInteraction.IsBoxOpen(item),
                ContentsChanged = true,
                ItemType = compartment == null ? EItemType.None : compartment.GetItemType(),
                ItemCount = compartment == null ? 0 : compartment.GetItemCount(),
            };
        }

        /// <summary>Host-side mirror of the client query: true when the identity module already
        /// owns this packaging box. Lets shared placement code treat the box as a single owner on
        /// both roles.</summary>
        internal static bool IsKnownPackagingBox(InteractablePackagingBox box)
            => _instance?._boxNetworkInteraction?.IsKnownBox(box) == true;

        /// <summary>Host: the stable id for a newly created box. The delivery/creator-id choice
        /// stays with the world box engine; the identity module owns the binding.</summary>
        internal static Guid EnsureHostBoxId(InteractablePackagingBox box)
            => box == null ? Guid.Empty : _instance?._boxNetworkInteraction?.EnsureHostId(box)
                ?? Guid.Empty;

        /// <summary>Host: the next synchronously-created box binds to this creator-assigned id
        /// (furniture box-up, furniture purchase, play-table box-up). Returns false when the id is
        /// not fresh (already bound to a live box or parked for another creation); the caller must
        /// reject the originating intent rather than let the host mint a new id, which would desync
        /// the creator's pre-bound box. An empty id is accepted: the host mints one.</summary>
        internal static bool PushCreatedBoxId(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.PushHostCreatedId(boxNetworkId) == true;

        /// <summary>Host: stage the creator-assigned ids for the delivery entry the purchase is
        /// about to enqueue. The game's enqueue hook commits them to that entry, so no id outlives
        /// the entry it belongs to. Returns false when any id is not fresh; the caller must reject
        /// the originating intent rather than remint, which would desync the creator's boxes.</summary>
        internal static BoxNetworkInteraction.DeliveryQueueState CaptureHostDeliveryQueueState()
            => _instance?._boxNetworkInteraction?.CaptureDeliveryQueueState();

        internal static void RestoreHostDeliveryQueueState(
            BoxNetworkInteraction.DeliveryQueueState state)
            => _instance?._boxNetworkInteraction?.RestoreDeliveryQueueState(state);

        internal static bool StageCreatedBoxIds(IReadOnlyList<Guid> boxNetworkIds)
            => _instance?._boxNetworkInteraction?.StageHostDeliveryIds(boxNetworkIds) == true;

        /// <summary>Host: release a creator id parked for a creation that did not happen, so a
        /// later, unrelated box cannot bind to it.</summary>
        internal static void CancelCreatedBoxId(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.CancelHostCreatedId(boxNetworkId);

        /// <summary>Host: drop ids staged for a delivery entry that was never enqueued, so a later
        /// enqueue cannot commit them to an unrelated order.</summary>
        internal static void CancelStagedDeliveryId(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.CancelStagedDeliveryId(boxNetworkId);

        internal static void CancelStagedDeliveryIds()
            => _instance?._boxNetworkInteraction?.CancelStagedHostDeliveryIds();

        /// <summary>Host: releases a local or mirrored hold for a box the game is destroying.</summary>
        internal static void ReleaseHoldForDestroyedBox(InteractablePackagingBox box)
            => _instance?._playerBoxInteraction?.ReleaseHeldObject(box);

        /// <summary>Host: a box-up already put this package in the host's hand before it had an
        /// id; announce the hold now that identity assigned one.</summary>
        internal static void PublishCreatedBoxHold(InteractablePackagingBox box, Guid id)
        {
            var host = _instance;
            if (host?._playerBoxInteraction == null || box == null || id == Guid.Empty
                || !BoxNetworkInteraction.IsHeld(box))
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[box-furniture] announcing host furniture hold id=" + id + ".");
            host._playerBoxInteraction.PublishHostHeld(box, id);
        }

        /// <summary>Host: stable box id for a live package (placement descriptors reference it).</summary>
        internal static bool TryGetBoxId(InteractablePackagingBox box, out Guid boxNetworkId)
        {
            boxNetworkId = Guid.Empty;
            return box != null
                && GameObjectIdentityApi.TryGetId(box, out var identity)
                && identity.Kind == GameObjectKind.PackagingBox
                && (boxNetworkId = identity.GuidValue) != Guid.Empty;
        }

        /// <summary>True when this connection has a frozen save manifest, so a box absent from
        /// it was created after the snapshot and must be materialized (not live-slot bound).</summary>
        internal static bool HasBoxSnapshotManifest(int connectionId)
            => connectionId > 0 && _instance?.TransferManifestFor(connectionId) != null;

        /// <summary>Placement descriptor stamping: the frozen box slot for one connection.</summary>
        internal static bool TryGetBoxSnapshotSlot(int connectionId, Guid boxNetworkId, out int slot)
        {
            slot = -1;
            var host = _instance;
            return host != null && connectionId > 0 && boxNetworkId != Guid.Empty
                && host.TransferManifestFor(connectionId) is { } manifest
                && manifest.TryGetBoxSlot(boxNetworkId, out slot);
        }

        /// <summary>Placement descriptor stamping: the frozen placement slot for one connection.</summary>
        internal static bool TryGetPlacementSnapshotSlot(int connectionId, int kind,
            ushort placementId, out int slot)
        {
            slot = -1;
            var host = _instance;
            return host != null && connectionId > 0 && placementId != 0
                && host.TransferManifestFor(connectionId) is { } manifest
                && manifest.TryGetPlacementSlot(kind, placementId, out slot);
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

        /// <summary>Republishes an item box's state after the host itself opens, closes, or
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

    /// <summary>
    /// Host -> the acting client: the authoritative contents/open state of one item box after a
    /// box-state action. It is returned on accept and on reject so the actor's live box converges
    /// on the host's state instead of the inverse of its prediction. A rejection never carries a
    /// pose or hold; those are owned by the box/warehouse channels.
    /// </summary>
    [NetworkMessage]
    public sealed class BoxStateResult : ActionResponseMessage
    {
        public InteractablePackagingBox Box;
        public bool IsBoxOpened;
        public bool ContentsChanged;
        public EItemType ItemType;
        public int ItemCount;
    }
}
