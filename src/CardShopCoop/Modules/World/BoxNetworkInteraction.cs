using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// A frozen identity manifest captured for one connection at the instant the host's save is
    /// serialized. The save is a snapshot: the guest loads exactly this order, so that
    /// connection's baseline must stamp the slot each entity occupied in this snapshot rather
    /// than an index from the host's live lists, which keep moving while the host plays. Box
    /// slots are keyed by the stable box network id; placement slots by the stable placement
    /// identity key. It is per connection so a later join can never clobber an earlier one.
    /// </summary>
    internal sealed class WorldTransferManifest
    {
        // Box network id -> its kind-local slot in the save's packaging-box enumeration.
        private readonly Dictionary<Guid, int> _boxSlots = new();
        // (kind << 24) | placement id -> its slot in the host's save-time live list.
        private readonly Dictionary<int, int> _placementSlots = new();

        internal int BoxSlotCount => _boxSlots.Count;

        internal int PlacementSlotCount => _placementSlots.Count;

        internal void Reset()
        {
            _boxSlots.Clear();
            _placementSlots.Clear();
        }

        internal void SetBoxSlot(Guid boxId, int slot)
        {
            if (boxId != Guid.Empty)
            {
                _boxSlots[boxId] = slot;
            }
        }

        internal bool TryGetBoxSlot(Guid boxId, out int slot)
            => _boxSlots.TryGetValue(boxId, out slot);

        internal void SetPlacementSlot(int kind, ushort placementId, int slot)
        {
            if (placementId != 0)
            {
                _placementSlots[(kind << 24) | placementId] = slot;
            }
        }

        internal bool TryGetPlacementSlot(int kind, ushort placementId, out int slot)
            => _placementSlots.TryGetValue((kind << 24) | placementId, out slot);
    }

    /// <summary>
    /// Packaging-box contents, open state, holds, warehouse representation and delivery-id
    /// bookkeeping. Box identity, descriptors, materialization and baselines live in
    /// <see cref="GameObjectIdentity"/>; this class only describes and applies box state.
    /// </summary>
    internal sealed class BoxNetworkInteraction
    {
        private const int MaxItemCount = 4096;

        private static readonly FieldInfo CompartmentItemsField =
            AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemList");

        private static readonly FieldInfo ItemAmountToSpawnField =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_ItemAmountToSpawn");

        private static readonly FieldInfo BoxOpenedField =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_IsBoxOpened");

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        // Host: creator-assigned ids carried by an accepted intent. A synchronous creation (an
        // empty-box take, a furniture box-up) pushes a single one-shot id; an asynchronous delivery
        // binds a per-entry id batch. The batch is committed by the game's own enqueue hook and
        // drained by its spawn hook, so an id is only ever queued beside the delivery entry that
        // owns it and a dropped entry takes its ids with it.
        private Guid _pendingHostCreatedId;
        private readonly List<HostDeliveryEntry> _hostDeliveryEntries = new();
        private readonly List<Guid> _stagedHostDeliveryIds = new();
        // Client: the ids its own checkout pre-assigned for the boxes its vanilla delivery queue
        // will spawn, plus the delivery scope flag set while RestockManager.Update drains it.
        private readonly Queue<Guid> _pendingClientDeliveryIds = new();
        private Guid _pendingClientCreatedId;
        private readonly HashSet<Guid> _clientOwnedIds = new();
        // Client: a locally predicted throw or placement still awaiting its host decision, keyed by
        // box id. A host message that crossed the move on the wire - a granted hold or a box-state
        // refresh - describes the box's earlier state, so it must not re-grab the box or drag it
        // back to an old pose.
        private readonly Dictionary<Guid, Guid> _pendingLocalMoves = new();
        private int _deliverySpawns;
        // Set while a container operation consumes a box through the game's own store path (for
        // example InteractableEmptyBoxStorage.StoreBox). The container op already carries the box
        // ID and owns the authoritative result, so the identity destroy hook must not also emit a
        // competing BoxDestroyRequest for the box the store destroyed.
        private int _suppressDestroyForward;
        private static readonly FieldInfo OutOfBoundsTimer =
            AccessTools.Field(typeof(RestockManager), "m_OutofBoundCheckTimer");
        // Client restock deliveries are queued as an instance list on RestockManager with two
        // parallel CPlayerData lists. Cancelling a rejected checkout's tail entries keeps the
        // three in lockstep without the delivery update indexing a stale row.
        private static readonly FieldInfo PendingRestockRows =
            AccessTools.Field(typeof(RestockManager), "m_SpawnBoxItemWaitingList");
        private static readonly FieldInfo BoxedFurniture =
            AccessTools.Field(typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        private static readonly FieldInfo BeingHoldField =
            AccessTools.Field(typeof(InteractableObject), "m_IsBeingHold");

        internal BoxNetworkInteraction(bool host, Action<INetMessage> broadcast,
            Action<int, INetMessage> send)
        {
            _host = host;
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            if (send == null)
            {
                throw new ArgumentNullException(nameof(send));
            }
        }

        /// <summary>Host: one game delivery entry's worth of creator-assigned ids, kept parallel to
        /// <c>RestockManager.m_SpawnBoxItemWaitingList</c>. <see cref="Remaining"/> mirrors the
        /// game's count-down so the entry is retired at the same spawn the game retires it at.</summary>
        private sealed class HostDeliveryEntry
        {
            internal int Remaining;
            internal readonly Queue<Guid> Ids = new();
        }

        /// <summary>An immutable capture of the host delivery mirror, taken before a purchase so a
        /// rolled-back game waiting list restores the ids that went with it.</summary>
        internal sealed class DeliveryQueueState
        {
            internal int[] Remaining;
            internal Guid[][] Ids;
        }

        internal bool IsKnownBox(InteractablePackagingBox box) => TryGetId(box, out _);

        internal bool IsClientOwned(Guid id) => !_host && id != Guid.Empty
            && _clientOwnedIds.Contains(id);

        internal bool IsDestroyForwardSuppressed => _suppressDestroyForward > 0;

        /// <summary>The guest never owns the game's random out-of-bounds box sweep. Keep this
        /// suppression beside the box lifecycle rather than in the generic placement module.</summary>
        internal void SuppressOutOfBoundsSweep(RestockManager manager)
        {
            if (!_host && manager != null)
            {
                OutOfBoundsTimer?.SetValue(manager, 0f);
            }
        }

        internal void Reset()
        {
            _pendingHostCreatedId = Guid.Empty;
            ClearHostDeliveryEntries();
            _pendingClientDeliveryIds.Clear();
            _pendingClientCreatedId = Guid.Empty;
            _clientOwnedIds.Clear();
            _pendingLocalMoves.Clear();
            _deliverySpawns = 0;
            _suppressDestroyForward = 0;
        }

        internal void Dispose() => Reset();

        /// <summary>Host: freeze this connection's save-time box slots into the identity
        /// descriptor baseline.</summary>
        internal void CaptureTransferManifest(WorldTransferManifest manifest)
        {
            if (_host)
            {
                BoxIdentityLifecycle.CaptureTransferManifest(manifest);
            }
        }

        /// <summary>Host: the stable id a host-side creation must bind. A creator-assigned id
        /// carried by the creating intent takes precedence: a synchronous intent push (empty-box
        /// take, furniture box-up) consumes the one-shot, an accepted purchase consumes its
        /// queued delivery ids front-first. With neither pending, the host is itself the creator
        /// and assigns a fresh id.</summary>
        internal Guid EnsureHostId(InteractablePackagingBox box)
        {
            if (!_host)
            {
                throw new InvalidOperationException("Only the host may assign a box network ID.");
            }

            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            if (TryGetId(box, out var known))
            {
                return known;
            }

            Guid assigned;
            if (_pendingHostCreatedId != Guid.Empty)
            {
                assigned = _pendingHostCreatedId;
                _pendingHostCreatedId = Guid.Empty;
            }
            else
            {
                // A spawn inside RestockManager.Update drains the front delivery entry. The front
                // entry was committed by the game's own enqueue hook, so it names exactly the box
                // this spawn produces.
                assigned = _deliverySpawns > 0 ? ConsumeHostDeliveryId() : Guid.Empty;
            }

            // The intake points already reject a non-fresh creator id, but an id can also become
            // claimed between staging and this spawn. Never let a stale id bind over a live box:
            // mint a fresh host id instead, which drops the collision rather than stealing it.
            if (assigned == Guid.Empty || IsIdInUse(assigned))
            {
                if (assigned != Guid.Empty)
                {
                    CoopPlugin.Log.LogWarning("[box-id] creator id=" + assigned
                        + " was claimed before spawn; minting a fresh host id.");
                }

                assigned = Guid.NewGuid();
            }

            BoxIdentityLifecycle.BindHostBox(assigned, box);
            return assigned;
        }

        /// <summary>Host: a stable id for a warehouse box that predates any creator event (a stored
        /// live box from a save). It mints a fresh id without consuming a pending creator/delivery
        /// id that belongs to a different creation.</summary>
        internal Guid EnsureWarehouseHostId(InteractablePackagingBox box)
        {
            if (!_host)
            {
                throw new InvalidOperationException("Only the host may assign a box network ID.");
            }

            if (box == null)
            {
                return Guid.Empty;
            }

            if (TryGetId(box, out var known))
            {
                return known;
            }

            var id = Guid.NewGuid();
            BoxIdentityLifecycle.BindHostBox(id, box);
            return id;
        }

        /// <summary>Host: the next synchronously-created host box binds to this creator-assigned
        /// id (one box per intent). Returns false, and parks nothing, when the id is not a fresh
        /// GUID (already bound to a live box or already pending). A caller that validated a client
        /// intent must treat this as a rejection rather than fall back to a host-minted id, or the
        /// creator's local box would desync from the host's.
        /// <paramref name="reassigningFrom"/> is the live box the id is legitimately moving off of
        /// (a warehouse take destroys the stored box and spawns its replacement under the same id);
        /// a binding that points at a different live box is still a rejection.</summary>
        internal bool PushHostCreatedId(Guid id, InteractablePackagingBox reassigningFrom = null)
        {
            if (!_host)
            {
                return false;
            }

            if (id == Guid.Empty)
            {
                // No creator id: the host mints a fresh one when the box is created.
                return true;
            }

            if (!IsFreshHostCreatorId(id, reassigningFrom))
            {
                CoopPlugin.Log.LogWarning("[box-id] rejected creator-supplied host box id=" + id
                    + ": it is already bound to a live box or already pending for another creation.");
                return false;
            }

            _pendingHostCreatedId = id;
            return true;
        }

        /// <summary>Host: release a one-shot creator id parked by <see cref="PushHostCreatedId"/>
        /// when the intended creation did not happen, so it cannot bind to a later, unrelated box.</summary>
        internal void CancelStagedDeliveryId(Guid id)
            => _stagedHostDeliveryIds.Remove(id);

        internal void CancelHostCreatedId(Guid id)
        {
            if (_host && id != Guid.Empty && _pendingHostCreatedId == id)
            {
                _pendingHostCreatedId = Guid.Empty;
            }
        }

        /// <summary>Host: stage the creator-assigned ids for the delivery entry the caller is about
        /// to enqueue. The ids are committed to the entry by the game's own enqueue hook, so no id
        /// is ever queued without an entry. The whole delivered line's ids are staged at once; the
        /// hook consumes them in spawn order. Returns false, staging nothing, when any id is not a
        /// fresh GUID: the caller should reject the whole intent, because the creating peer bound
        /// its own boxes to these exact ids and remapping on the host would desync them.</summary>
        internal bool StageHostDeliveryIds(IReadOnlyList<Guid> ids)
        {
            _stagedHostDeliveryIds.Clear();
            if (!_host || ids == null)
            {
                return _host;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] == Guid.Empty)
                {
                    continue;
                }

                if (!IsFreshHostCreatorId(ids[i], null))
                {
                    CoopPlugin.Log.LogWarning("[box-id] rejected staged delivery id=" + ids[i]
                        + ": it is already bound to a live box or already pending; the intent must "
                        + "not adopt it.");
                    _stagedHostDeliveryIds.Clear();
                    return false;
                }

                _stagedHostDeliveryIds.Add(ids[i]);
            }

            return true;
        }

        /// <summary>Host: the game just appended a delivery entry that will spawn <paramref
        /// name="count"/> boxes. Bind the staged ids to that entry, capped at the box count so a
        /// client cannot park surplus ids ahead of a later order's spawns. Runs in the game's
        /// enqueue hook, so an entry and its ids are always created together. An entry is added for
        /// every game enqueue, even an id-less or zero-count one, to keep the mirror aligned.</summary>
        internal void CommitHostDeliveryEntry(int count)
        {
            if (!_host)
            {
                return;
            }

            var entry = new HostDeliveryEntry { Remaining = count };
            for (var i = 0; i < count && i < _stagedHostDeliveryIds.Count; i++)
            {
                entry.Ids.Enqueue(_stagedHostDeliveryIds[i]);
            }

            _stagedHostDeliveryIds.Clear();
            _hostDeliveryEntries.Add(entry);
            if (count > 0)
            {
                CoopPlugin.Log.LogInfo("[box-id] delivery entry queued: boxes=" + count
                    + " creatorIds=" + entry.Ids.Count + " entries="
                    + _hostDeliveryEntries.Count + ".");
            }
        }

        /// <summary>Host: drains one id from the front delivery entry for a spawn the game just
        /// made, retiring the entry on its final box exactly as the game retires its own entry.
        /// Returns <see cref="Guid.Empty"/> when the entry carried no id, so the caller mints one.</summary>
        private Guid ConsumeHostDeliveryId()
        {
            if (_hostDeliveryEntries.Count == 0)
            {
                return Guid.Empty;
            }

            var entry = _hostDeliveryEntries[0];
            var id = entry.Ids.Count > 0 ? entry.Ids.Dequeue() : Guid.Empty;
            if (--entry.Remaining <= 0)
            {
                _hostDeliveryEntries.RemoveAt(0);
            }

            return id;
        }

        /// <summary>Host: drop every queued and staged delivery id. Used on a full teardown (the
        /// session reset) rather than a game-side rebuild, which resyncs instead.</summary>
        internal void ClearHostDeliveryEntries()
        {
            _hostDeliveryEntries.Clear();
            _stagedHostDeliveryIds.Clear();
        }

        /// <summary>Host: drop ids staged for a delivery entry that was never enqueued, so a later
        /// enqueue cannot commit them to an unrelated order.</summary>
        internal void CancelStagedHostDeliveryIds()
        {
            _stagedHostDeliveryIds.Clear();
        }

        /// <summary>Host: the game cleared and rebuilt its waiting list (a save/game load). Mirror
        /// the rebuilt shape with id-less entries so the mirror stays positionally aligned with the
        /// game queue and each surviving entry simply mints fresh ids as it spawns.</summary>
        internal void ResyncHostDeliveryEntries()
        {
            ClearHostDeliveryEntries();
            // Init rebuilds m_SpawnBoxItemWaitingList from the persisted index list, so that list
            // is the entry count; the parallel count list supplies each entry's remaining spawns.
            var indexes = CPlayerData.m_SpawnBoxRestockIndexWaitingList;
            var counts = CPlayerData.m_SpawnBoxItemCountWaitingList;
            for (var i = 0; indexes != null && i < indexes.Count; i++)
            {
                _hostDeliveryEntries.Add(new HostDeliveryEntry
                {
                    Remaining = counts != null && i < counts.Count ? counts[i] : 0,
                });
            }
        }

        /// <summary>Host: capture the delivery mirror before a purchase so a rolled-back game
        /// waiting list restores exactly the ids that went with it.</summary>
        internal DeliveryQueueState CaptureDeliveryQueueState()
        {
            var state = new DeliveryQueueState
            {
                Remaining = new int[_hostDeliveryEntries.Count],
                Ids = new Guid[_hostDeliveryEntries.Count][],
            };
            for (var i = 0; i < _hostDeliveryEntries.Count; i++)
            {
                state.Remaining[i] = _hostDeliveryEntries[i].Remaining;
                state.Ids[i] = _hostDeliveryEntries[i].Ids.ToArray();
            }

            return state;
        }

        internal void RestoreDeliveryQueueState(DeliveryQueueState state)
        {
            ClearHostDeliveryEntries();
            if (state?.Ids == null || state.Remaining == null)
            {
                return;
            }

            for (var i = 0; i < state.Ids.Length; i++)
            {
                var entry = new HostDeliveryEntry { Remaining = state.Remaining[i] };
                var ids = state.Ids[i];
                for (var j = 0; ids != null && j < ids.Length; j++)
                {
                    entry.Ids.Enqueue(ids[j]);
                }

                _hostDeliveryEntries.Add(entry);
            }
        }

        /// <summary>Host/client: marks the span in which the game's own restock delivery queue is
        /// draining, so a host spawn consumes a queued delivery id and a client spawn consumes one
        /// of the ids its checkout pre-assigned.</summary>
        internal void BeginDeliverySpawns()
        {
            _deliverySpawns++;
        }

        internal void EndDeliverySpawns()
        {
            if (_deliverySpawns > 0)
            {
                _deliverySpawns--;
            }
        }

        /// <summary>Client: registers a package box this peer's own vanilla flow just created and
        /// returns its creator-assigned stable id. During a checkout delivery the id is one the
        /// checkout pre-assigned; a synchronous furniture checkout pushes a one-shot; otherwise the
        /// client mints a fresh id for the box. If the host's descriptor for this id already
        /// arrived, it is applied to the bound box now.</summary>
        internal Guid AssignClientCreated(InteractablePackagingBox box)
        {
            if (_host)
            {
                throw new InvalidOperationException("Only a client assigns a client-created box id.");
            }

            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            if (TryGetId(box, out var known))
            {
                return known;
            }

            if (BoxIdentityLifecycle.IsMaterializing)
            {
                return Guid.Empty;
            }

            Guid id;
            if (_pendingClientCreatedId != Guid.Empty)
            {
                id = _pendingClientCreatedId;
                _pendingClientCreatedId = Guid.Empty;
            }
            else if (_deliverySpawns > 0 && _pendingClientDeliveryIds.Count > 0)
            {
                id = _pendingClientDeliveryIds.Dequeue();
            }
            else
            {
                id = Guid.NewGuid();
            }

            BoxIdentityLifecycle.BindClientBox(id, box);
            _clientOwnedIds.Remove(id);
            BoxIdentityLifecycle.ApplyPendingClientDescriptor(id, box);
            return id;
        }

        /// <summary>Client: pre-assign the ids for the boxes a checkout's vanilla delivery queue
        /// will spawn, in order. They are carried in the creating intent and consumed by
        /// <see cref="AssignClientCreated"/> as the delivery update spawns each box.</summary>
        internal void PushClientDeliveryIds(IEnumerable<Guid> ids)
        {
            if (_host || ids == null)
            {
                return;
            }

            foreach (var id in ids)
            {
                if (id != Guid.Empty)
                {
                    _pendingClientDeliveryIds.Enqueue(id);
                    _clientOwnedIds.Add(id);
                }
            }
        }

        /// <summary>Client: the next synchronously-created client box (a furniture purchase)
        /// binds to this creator-assigned id.</summary>
        internal void PushClientCreatedId(Guid id)
        {
            if (_host || id == Guid.Empty)
            {
                return;
            }

            _pendingClientCreatedId = id;
            _clientOwnedIds.Add(id);
        }

        internal int PendingDeliveryCount()
            => CPlayerData.m_SpawnBoxItemCountWaitingList?.Count ?? 0;

        /// <summary>Client: destroys the boxes bound to a rejected checkout's pre-assigned ids
        /// (those the game already spawned) and drops the rest of those ids from the delivery
        /// queue (those it has not). The boxes are removed through the game's own teardown.</summary>
        internal void RollbackClientCreated(IReadOnlyList<Guid> ids)
        {
            if (_host || ids == null)
            {
                return;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (id == Guid.Empty)
                {
                    continue;
                }

                if (TryGetBox(id, out var box))
                {
                    CoopPlugin.Log.LogInfo("[box-id] discarding rejected-checkout box id=" + id
                        + " name=" + box.name + ".");
                    BoxIdentityLifecycle.ForgetBox(box);
                    DestroyWithoutNotification(box);
                }

                _clientOwnedIds.Remove(id);
                BoxIdentityLifecycle.ReleasePendingClientDescriptors(new[] { id });
                if (_pendingClientCreatedId == id)
                {
                    _pendingClientCreatedId = Guid.Empty;
                }

                RemoveQueued(_pendingClientDeliveryIds, id);
            }
        }

        /// <summary>Client: drops ownership of pre-assigned ids whose local box never spawned (the
        /// checkout was refused locally, or no delivery was queued), so the host's descriptors for
        /// them materialize normally instead of waiting forever.</summary>
        internal void ReleaseUnspawnedClientIds(IReadOnlyList<Guid> ids)
        {
            if (_host || ids == null)
            {
                return;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (id == Guid.Empty || !_clientOwnedIds.Remove(id))
                {
                    continue;
                }

                BoxIdentityLifecycle.ReleasePendingClientDescriptors(new[] { id });
                if (_pendingClientCreatedId == id)
                {
                    _pendingClientCreatedId = Guid.Empty;
                }

                RemoveQueued(_pendingClientDeliveryIds, id);
            }
        }

        private static void RemoveQueued(Queue<Guid> queue, Guid id)
        {
            if (queue.Count == 0)
            {
                return;
            }

            var kept = new List<Guid>(queue.Count);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next != id)
                {
                    kept.Add(next);
                }
            }

            for (var i = 0; i < kept.Count; i++)
            {
                queue.Enqueue(kept[i]);
            }
        }

        /// <summary>Client: removes a rejected checkout's not-yet-spawned delivery entries from the
        /// tail of the game's restock spawn queue. Its entries were appended last and the queue
        /// drains strictly front-first, so all surviving tail entries belong to it; trimming the
        /// smaller of the appended count and the queue length removes exactly those and never an
        /// earlier order's entry.</summary>
        internal void CancelPendingClientDeliveries(int appendedDeliveries)
        {
            if (_host || appendedDeliveries <= 0)
            {
                return;
            }

            var counts = CPlayerData.m_SpawnBoxItemCountWaitingList;
            var indexes = CPlayerData.m_SpawnBoxRestockIndexWaitingList;
            var manager = SceneRef<RestockManager>.Get();
            var rows = manager == null ? null : PendingRestockRows?.GetValue(manager) as IList;
            if (counts == null || indexes == null || rows == null)
            {
                // The three lists drive the delivery update together; trimming only some of them
                // would desync the queue and make the update index past the short one. Leave it
                // whole rather than corrupt it.
                CoopPlugin.Log.LogWarning("[box-id] could not cancel " + appendedDeliveries
                    + " pending client deliveries; the restock queue is unavailable.");
                return;
            }

            var remove = Math.Min(appendedDeliveries, counts.Count);
            if (remove <= 0)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[box-id] cancelling " + remove + " pending client deliveries "
                + "for a rejected purchase.");
            for (var i = 0; i < remove; i++)
            {
                RemoveLast(counts);
                RemoveLast(indexes);
                RemoveLast(rows);
            }
        }

        private static void RemoveLast(IList list)
        {
            if (list != null && list.Count > 0)
            {
                list.RemoveAt(list.Count - 1);
            }
        }

        internal bool TryGetId(InteractablePackagingBox box, out Guid id)
        {
            id = Guid.Empty;
            if (box == null || !GameObjectIdentityApi.TryGetId(box, out var identity))
            {
                return false;
            }

            // A furniture package shares the furniture's session Guid; a Guid box carries its own.
            return (identity.Kind == GameObjectKind.PackagingBox
                    || identity.Kind == GameObjectKind.Furniture)
                && (id = identity.GuidValue) != Guid.Empty;
        }

        /// <summary>Host: publishes the authoritative contents/open/pose of an already-created
        /// box. Creation itself is the identity module's descriptor.</summary>
        internal void HostRefresh(Guid id) => HostBroadcastRefresh(id);

        /// <summary>Host: re-announce a box a worker just put down. Stored boxes keep their
        /// compartment slot (the warehouse channel owns them), so only a free box is refreshed.</summary>
        internal void HostRefreshReleasedWorkerBox(Guid id)
        {
            if (!_host || id == Guid.Empty || !TryGetBox(id, out var box))
            {
                return;
            }

            if (box is InteractablePackagingBox_Item item && item.m_IsStored)
            {
                return;
            }

            HostBroadcastRefresh(id);
        }

        private void HostBroadcastRefresh(Guid id)
        {
            if (!_host || id == Guid.Empty || !TryGetBox(id, out var box)
                || box is InteractablePackagingBox_Shelf)
            {
                return;
            }

            var state = new BoxStateMessage
            {
                Box = box,
                PoseChanged = true,
                Position = box.transform.position,
                Rotation = box.transform.rotation,
            };
            if (box is InteractablePackagingBox_Item item)
            {
                var compartment = item.m_ItemCompartment;
                state.ContentsChanged = true;
                state.IsBoxOpened = IsBoxOpen(item);
                state.ItemType = compartment == null ? EItemType.None : compartment.GetItemType();
                state.ItemCount = compartment == null ? 0 : compartment.GetItemCount();
            }

            _broadcast(state);
        }

        /// <summary>Applies a client-reported item-box state to the host's live box. Unchanged
        /// contents are left alone; only the open flag is touched when contents did not change.
        /// Returns false when a contents change carries an invalid payload, so the caller can
        /// reject the intent. <see cref="ApplyItemState"/> would otherwise spawn as many items as
        /// the client asked for; the count is bounded by <see cref="MaxItemCount"/>.</summary>
        internal bool ApplyHostBoxState(InteractablePackagingBox_Item item,
            BoxStateRequestMessage message)
        {
            if (item == null || message == null || message.Box == null)
            {
                return false;
            }

            var compartment = item.m_ItemCompartment;
            // A client's ItemType/ItemCount is only authoritative for a contents change (or when
            // the host has no compartment to keep); otherwise the host's own contents are kept and
            // only the open flag is mirrored. Validate the payload that will actually be applied.
            var applyContents = message.ContentsChanged || compartment == null;
            if (applyContents && !IsValidItem(message.ItemType, message.ItemCount))
            {
                CoopPlugin.Log.LogWarning("[box-id] rejected box state request name="
                    + item.name + " type=" + message.ItemType + " count="
                    + message.ItemCount + ": the content payload is not a valid item.");
                return false;
            }

            ApplyItemState(item, new BoxNetworkState
            {
                Kind = BoxNetworkKind.Item,
                ItemType = applyContents ? message.ItemType : compartment.GetItemType(),
                ItemCount = applyContents ? message.ItemCount : compartment.GetItemCount(),
                IsBoxOpened = message.IsBoxOpened,
            });
            return true;
        }

        /// <summary>Applies the host's authoritative box state to a client's box. A pose refresh
        /// is skipped while the box is held or a newer local move is still in flight.</summary>
        internal void ClientApplyBoxState(BoxStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            var box = message.Box;
            if (box == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[box-id] dropped box state: the box did not resolve to a live object.");
                return;
            }

            if (!TryGetId(box, out var id))
            {
                CoopPlugin.Log.LogWarning("[box-id] dropped box state for " + box.name
                    + ": the live box has no network identity.");
                return;
            }

            if (message.PoseChanged && !IsHeld(box) && !HasPendingLocalMove(id))
            {
                ApplyPose(box, message.Position, message.Rotation);
            }

            if (box is InteractablePackagingBox_Item item)
            {
                ApplyItemState(item, new BoxNetworkState
                {
                    Kind = BoxNetworkKind.Item,
                    ItemType = message.ItemType,
                    ItemCount = message.ItemCount,
                    IsBoxOpened = message.IsBoxOpened,
                });
            }
        }

        internal BoxNetworkState DescribeAuthoritative(Guid id)
        {
            // Furniture state is a placement descriptor/delta, never box content.
            return _host && id != Guid.Empty && TryGetBox(id, out var box)
                && box is not InteractablePackagingBox_Shelf
                ? DescribeBox(id, box)
                : null;
        }

        internal bool TryGetBox(Guid id, out InteractablePackagingBox box)
        {
            box = null;
            return id != Guid.Empty && GameObjectIdentityApi.TryResolveBox(id, out box)
                && box != null;
        }

        /// <summary>Resolves the placed item box whose contents compartment is
        /// <paramref name="compartment"/>. A box's contents are not a shelf, so the shelf
        /// protocols cannot address them; the box owns its compartment as a child (the same
        /// hierarchy fact <c>IsSyncableShelf</c> relies on to exclude box compartments).
        /// Returns null for any other compartment.</summary>
        internal InteractablePackagingBox_Item FindItemBox(ShelfCompartment compartment)
            => compartment?.GetComponentInParent<InteractablePackagingBox_Item>(true);

        /// <summary>True while this peer models the box as stored in a warehouse compartment (and
        /// therefore not carried). A stored box must never be re-attached to a hand: a hold that
        /// arrives for it raced the store that put it there. The state is read from the game
        /// object itself, not a shadow copy.</summary>
        internal bool IsStored(Guid id) => TryGetBox(id, out var box) && IsStoredBox(box);

        /// <summary>Records that this peer has a locally predicted throw or placement for
        /// <paramref name="boxNetworkId"/> awaiting the host. A granted hold or creation descriptor
        /// that arrives until the prediction resolves crossed that move on the wire and must not
        /// override it - re-holding pulled the just-thrown box back into the hand (the first throw
        /// after a take or box-up looked refused) and re-posing teleported it.</summary>
        internal void NotePendingLocalMove(Guid boxNetworkId, Guid actionId)
        {
            if (boxNetworkId != Guid.Empty && actionId != Guid.Empty)
            {
                _pendingLocalMoves[boxNetworkId] = actionId;
            }
        }

        /// <summary>True while a locally predicted throw/placement for this box has not yet been
        /// resolved by the host. Resolved entries are pruned here.</summary>
        internal bool HasPendingLocalMove(Guid boxNetworkId)
        {
            if (!_pendingLocalMoves.TryGetValue(boxNetworkId, out var actionId))
            {
                return false;
            }

            if (ActionLedger.IsPending(actionId))
            {
                return true;
            }

            _pendingLocalMoves.Remove(boxNetworkId);
            return false;
        }

        /// <summary>Reads the game's own stored flag rather than a shadow copy. A box is stored
        /// when the game set <c>m_IsStored</c>, or while it is listed in the compartment it was
        /// stored into (the list already holds it before <c>m_BoxStoredCompartment</c> is set
        /// during <c>DispenseItem</c>).</summary>
        internal static bool IsStoredBox(InteractablePackagingBox box)
        {
            if (box is not InteractablePackagingBox_Item item)
            {
                return false;
            }

            if (item.m_IsStored)
            {
                return true;
            }

            var stored = item.GetBoxStoredCompartment()?.GetInteractablePackagingBoxList();
            return stored != null && stored.Contains(item);
        }

        /// <summary>Client: the game has turned this live box into a warehouse record. Release the
        /// binding and destroy the representation through the engine so the game's own teardown is
        /// not forwarded to the host as a competing box destruction.</summary>
        internal void ClientConvertToStoredRecord(Guid id)
        {
            if (_host)
            {
                throw new InvalidOperationException("The host cannot apply a client stored-box state.");
            }

            if (id == Guid.Empty)
            {
                throw new InvalidOperationException("A client stored-box state requires an ID.");
            }

            if (TryGetBox(id, out var box))
            {
                BoxIdentityLifecycle.ForgetBox(box);
                DestroyWithoutNotification(box);
            }
        }

        internal void BindStoredLiveBox(Guid id, InteractablePackagingBox_Item box)
        {
            if (id == Guid.Empty || box == null)
            {
                throw new InvalidOperationException("A live warehouse box requires its ID and object.");
            }

            if (!_host && TryGetBox(id, out var previous) && !ReferenceEquals(previous, box))
            {
                BoxIdentityLifecycle.ForgetBox(previous);
                DestroyWithoutNotification(previous);
            }

            // A stored box is not simulated: the game leaves it frozen from the held state, but a
            // box materialized for the network arrives with physics on and would sag or fall.
            box.SetPhysicsEnabled(false);
            if (_host)
            {
                BoxIdentityLifecycle.BindHostBox(id, box);
            }
            else
            {
                BoxIdentityLifecycle.BindClientBox(id, box);
            }
        }

        internal void DetachForWarehouseTake(Guid id)
        {
            if (id != Guid.Empty && TryGetBox(id, out var box))
            {
                BoxIdentityLifecycle.ForgetBox(box);
            }
        }

        internal bool ClientEnsureWarehouseTake(Guid id, EItemType itemType, int amount, bool isBig,
            out InteractablePackagingBox_Item box)
        {
            box = null;
            if (_host)
            {
                return false;
            }

            if (TryGetBox(id, out var existing))
            {
                box = existing as InteractablePackagingBox_Item;
                if (box == null)
                {
                    // The id names a live box of the wrong kind. The reliable lane must not tear
                    // down over a destroyed/interrupted warehouse take; log and drop the delta.
                    CoopPlugin.Log.LogWarning("[box-id] warehouse take id=" + id
                        + " is already a " + existing.GetType().Name + ", not an item box; ignoring.");
                    return false;
                }

                return true;
            }

            if (!BoxIdentityLifecycle.TryMaterializeWarehouseTake(id, itemType, amount, isBig,
                out box))
            {
                CoopPlugin.Log.LogWarning("[box-id] could not materialize warehouse take box id=" + id
                    + " (missing prefab or interrupted object); ignoring the delta.");
                return false;
            }

            return true;
        }

        internal InteractablePackagingBox_Item MaterializeWarehouseStored(EItemType itemType,
            int amount, bool isBig)
            => BoxIdentityLifecycle.MaterializeWarehouseStored(itemType, amount, isBig);

        /// <summary>Suppresses generic box-destroy forwarding while a container operation consumes
        /// a box through the game's own store path. The caller must pair this with
        /// <see cref="EndContainerConsume"/> in a finally.</summary>
        internal void BeginContainerConsume()
        {
            _suppressDestroyForward++;
        }

        internal void EndContainerConsume()
        {
            if (_suppressDestroyForward > 0)
            {
                _suppressDestroyForward--;
            }
        }

        internal bool HostDestroyRequested(InteractablePackagingBox box)
        {
            if (!_host || box == null)
            {
                return false;
            }

            // A stored box destroyed in place would leave a ghost in its compartment.
            DetachStoredBox(box);
            box.OnDestroyed();
            return true;
        }

        internal void ClientForgetPhysical(Guid id, InteractablePackagingBox box)
        {
            if (_host || id == Guid.Empty || box == null)
            {
                return;
            }

            BoxIdentityLifecycle.ForgetBox(box);
            DestroyWithoutNotification(box);
        }

        internal static bool TryGetBoxedFurniture(InteractablePackagingBox_Shelf box,
            out InteractableObject furniture)
        {
            furniture = BoxedFurniture?.GetValue(box) as InteractableObject;
            return furniture != null;
        }

        /// <summary>True while this box is in a player's hand. The host's own furniture box-up
        /// holds the box before it has a network id, so the hold is announced when it registers.</summary>
        internal bool IsBeingHeld(InteractablePackagingBox box) => IsHeld(box);

        internal static bool IsHeld(InteractablePackagingBox box)
            => box != null && BeingHoldField?.GetValue(box) is bool held && held;

        /// <summary>True when <paramref name="id"/> is already claimed by a live box. Creator-assigned
        /// ids must be fresh GUIDs; adopting one that names a live box would rebind that box (a
        /// cross-entity morph/ghost).</summary>
        private bool IsIdInUse(Guid id) => IsIdInUseByOtherThan(id, null);

        /// <summary>Same as <see cref="IsIdInUse"/> but tolerates the id being bound to
        /// <paramref name="reassigningFrom"/>: an operation that legitimately moves an id from one
        /// live representation to another (a warehouse take re-spawns the stored box) may adopt the
        /// id it is currently bound to. Any other live box still counts as a collision.</summary>
        private bool IsIdInUseByOtherThan(Guid id, InteractablePackagingBox reassigningFrom)
        {
            if (id == Guid.Empty)
            {
                return false;
            }

            return GameObjectIdentityApi.TryResolve(GameObjectId.Box(id), out var entity)
                && entity != null
                && !ReferenceEquals(entity, reassigningFrom);
        }

        /// <summary>Host: whether a creator-supplied host box id may be adopted. It must be a fresh
        /// GUID that is neither bound to another live box nor already queued/staged for another
        /// creation, so one id can never be parked for two boxes. See
        /// <see cref="IsIdInUseByOtherThan"/> for the reassignment allowance.</summary>
        private bool IsFreshHostCreatorId(Guid id, InteractablePackagingBox reassigningFrom)
        {
            if (id == Guid.Empty || IsIdInUseByOtherThan(id, reassigningFrom)
                || _pendingHostCreatedId == id)
            {
                return false;
            }

            for (var i = 0; i < _stagedHostDeliveryIds.Count; i++)
            {
                if (_stagedHostDeliveryIds[i] == id)
                {
                    return false;
                }
            }

            for (var i = 0; i < _hostDeliveryEntries.Count; i++)
            {
                foreach (var queued in _hostDeliveryEntries[i].Ids)
                {
                    if (queued == id)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal static BoxNetworkState DescribeBox(Guid id, InteractablePackagingBox box)
        {
            if (box is InteractablePackagingBox_Item item)
            {
                var compartment = item.m_ItemCompartment;
                return ItemDescriptor(id, compartment == null ? EItemType.None : compartment.GetItemType(),
                    compartment == null ? 0 : compartment.GetItemCount(), item.m_IsBigBox,
                    item.transform.position, item.transform.rotation, IsBoxOpen(item));
            }

            if (box is InteractablePackagingBox_Card card)
            {
                var state = new BoxNetworkState
                {
                    BoxNetworkId = id,
                    Kind = BoxNetworkKind.Card,
                    Position = card.transform.position,
                    Rotation = card.transform.rotation,
                };
                var cards = card.GetCardDataList();
                if (cards != null)
                {
                    for (var i = 0; i < cards.Count; i++)
                    {
                        if (cards[i] != null)
                        {
                            state.Cards.Add(ToState(cards[i]));
                        }
                    }
                }

                return state;
            }

            throw new InvalidOperationException("Unsupported packaging box type: " + box.GetType().FullName);
        }

        internal static BoxNetworkState ItemDescriptor(Guid id, EItemType itemType, int itemCount,
            bool isBig, Vector3 position, Quaternion rotation, bool isBoxOpened = false)
        {
            return new BoxNetworkState
            {
                BoxNetworkId = id,
                Kind = BoxNetworkKind.Item,
                ItemType = itemType,
                ItemCount = itemCount,
                IsBig = isBig,
                IsBoxOpened = isBoxOpened,
                Position = position,
                Rotation = rotation,
            };
        }

        /// <summary>Tears a box down through the game's own <c>OnDestroyed</c> path while
        /// suppressing this peer's own destroy forwarding (the caller owns the id's next life).
        /// Identity owns the teardown sequence.</summary>
        internal void DestroyWithoutNotification(InteractablePackagingBox box)
            => BoxIdentityLifecycle.DestroyWithoutNotification(box);

        private static void DetachStoredBox(InteractablePackagingBox box)
            => BoxIdentityLifecycle.DetachStoredBoxForTeardown(box);

        internal static void ApplyPose(InteractablePackagingBox box, Vector3 position,
            Quaternion rotation)
        {
            if (box == null)
            {
                return;
            }

            if (box.m_Rigidbody != null)
            {
                box.m_Rigidbody.position = position;
                box.m_Rigidbody.rotation = rotation;
            }

            box.transform.SetPositionAndRotation(position, rotation);
        }

        private static bool IsValidItem(EItemType type, int count)
        {
            // Empty boxes are real packaging boxes and must receive IDs too.
            return count >= 0 && count <= MaxItemCount
                && (count == 0 || type != EItemType.None);
        }

        /// <summary>The box's committed open/closed flag. <see cref="InteractablePackagingBox_Item
        /// .IsBoxOpened"/> deliberately reports <c>false</c> for the whole 0.85 s open/close
        /// animation, so reading it for shared state made an opened box look closed to the other
        /// peer and left them rendering the wrong mesh group.</summary>
        internal static bool IsBoxOpen(InteractablePackagingBox_Item item)
        {
            if (item == null)
            {
                return false;
            }

            return BoxOpenedField?.GetValue(item) is bool open ? open : item.IsBoxOpened();
        }

        /// <summary>Mirrors an authoritative item-box open flag and contents onto a live box.
        /// Contents are only rebuilt when they actually differ so a pose/refresh of an already
        /// matching box is a no-op.</summary>
        internal static void ApplyItemState(InteractablePackagingBox_Item item, BoxNetworkState state)
        {
            var compartment = item.m_ItemCompartment;
            if (compartment == null)
            {
                return;
            }

            var contentsDiffer = compartment.GetItemType() != state.ItemType
                || compartment.GetItemCount() != state.ItemCount;
            var openDiffers = IsBoxOpen(item) != state.IsBoxOpened;
            if (!contentsDiffer && !openDiffers)
            {
                return;
            }

            if (openDiffers)
            {
                item.ForceSetOpenCloseInstant(state.IsBoxOpened);
            }

            if (contentsDiffer || (openDiffers && state.IsBoxOpened))
            {
                ApplyItemBoxContents(item, state.ItemType, state.ItemCount);
            }
        }

        private static void ApplyItemBoxContents(InteractablePackagingBox_Item item,
            EItemType itemType, int itemCount)
        {
            var compartment = item.m_ItemCompartment;
            if (compartment == null)
            {
                return;
            }

            // Return the old visuals to the pool and clear the stored list so SpawnItem appends
            // into an empty list. This mirrors FillBoxWithItem without its price-tag world UI
            // (which deactivates and leaks a new UI group on every refresh).
            if (CompartmentItemsField?.GetValue(compartment) is List<Item> items)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    items[i]?.DisableItem();
                }

                items.Clear();
            }

            compartment.PreSpawnItemUpdate(0);

            if (itemType == EItemType.None || itemCount <= 0)
            {
                compartment.SetCompartmentItemType(EItemType.None);
                compartment.CalculatePositionList();
                item.SetItemType(EItemType.None);
            }
            else
            {
                compartment.SetCompartmentItemType(itemType);
                compartment.CalculatePositionList();
                if (IsBoxOpen(item))
                {
                    compartment.SpawnItem(itemCount, spawnFromFront: false);
                }
                else
                {
                    compartment.PreSpawnItemUpdate(itemCount);
                }

                item.SetItemType(itemType);
            }

            // Keep the pending-open population in step so a later close/reopen cannot restore a
            // stale item count.
            ItemAmountToSpawnField?.SetValue(item, compartment.GetItemCount());
            compartment.gameObject.SetActive(IsBoxOpen(item));
            CoopPlugin.Log.LogInfo("[box-id] applied box contents id=" + item.name
                + " type=" + itemType + " count=" + itemCount + ".");
        }

        internal static BoxCardState ToState(CardData card)
        {
            return new BoxCardState
            {
                ExpansionType = card.expansionType,
                MonsterType = card.monsterType,
                BorderType = card.borderType.ToString(),
                IsFoil = card.isFoil,
                IsDestiny = card.isDestiny,
                IsChampionCard = card.isChampionCard,
                IsNew = card.isNew,
                CardGrade = card.cardGrade,
                GradedCardIndex = card.gradedCardIndex,
            };
        }

        internal static CardData FromState(BoxCardState state)
        {
            var border = (ECardBorderType)Enum.Parse(typeof(ECardBorderType), state.BorderType);

            return new CardData
            {
                expansionType = state.ExpansionType,
                monsterType = state.MonsterType,
                borderType = border,
                isFoil = state.IsFoil,
                isDestiny = state.IsDestiny,
                isChampionCard = state.IsChampionCard,
                isNew = state.IsNew,
                cardGrade = state.CardGrade,
                gradedCardIndex = state.GradedCardIndex,
            };
        }
    }
}
