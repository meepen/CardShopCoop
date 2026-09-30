using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Modules.Prediction;
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
            if (placementId != PlacementIdentity.Invalid)
            {
                _placementSlots[(kind << 24) | placementId] = slot;
            }
        }

        internal bool TryGetPlacementSlot(int kind, ushort placementId, out int slot)
            => _placementSlots.TryGetValue((kind << 24) | placementId, out slot);
    }

    /// <summary>
    /// Owns the stable identity of every packaging box. The peer that CREATES a box assigns a
    /// fresh <see cref="Guid"/> at creation; a creation intent carries that id so the counterpart
    /// binds to it rather than allocating. Ids are announced with a complete creation descriptor
    /// and retained while an item box is represented by a warehouse record. No interaction
    /// protocol is allowed to fall back to a name, hierarchy path, content, or transform
    /// signature. "No id" is <see cref="Guid.Empty"/>.
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
        private readonly Dictionary<Guid, InteractablePackagingBox> _boxesById = new();
        private readonly Dictionary<InteractablePackagingBox, Guid> _idsByBox = new();
        private readonly Dictionary<Guid, BoxCreatedMessage> _pendingFurniture = new();
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
        private readonly Dictionary<Guid, BoxCreatedMessage> _pendingClientDescriptors = new();
        // Client: a locally predicted throw or placement still awaiting its host decision, keyed by
        // box id. A host message that crossed the move on the wire - a granted hold or a creation
        // descriptor - describes the box's earlier state, so it must not re-grab the box or drag it
        // back to an old pose; that is what made the first throw after a take or box-up look
        // refused, with the next attempt then working. The entry is kept until the prediction
        // resolves, then pruned lazily.
        private readonly Dictionary<Guid, Guid> _pendingLocalMoves = new();
        private int _deliverySpawns;
        // Guest: the boxes recovered from the transferred save, indexed exactly as the host's
        // frozen snapshot slots. Captured at baseline start, before any runtime box is materialized.
        private readonly Dictionary<byte, List<InteractablePackagingBox>> _sceneSlots = new();
        // Guest: the same save boxes, flattened, so the end-of-baseline sweep can destroy the ones
        // the host no longer represents without walking the per-kind lists twice.
        private readonly List<InteractablePackagingBox> _baselineSaveBoxes = new();
        private bool _sceneSlotsCaptured;
        private Transform _cardSpawnAnchor;
        private int _materializing;
        private int _applyingRemote;
        // Set while a container operation consumes a box through the game's own store path (for
        // example InteractableEmptyBoxStorage.StoreBox). The container op already carries the box
        // ID and owns the authoritative result, so the box engine must not also emit a competing
        // BoxDestroyRequest for the box the store destroyed.
        private int _suppressDestroyForward;
        internal Guid HostPredictionId;
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
                throw new ArgumentNullException(nameof(send));
            if (!_host)
            {
                PlacementApi.StructureChanged += RetryPendingFurniture;
            }
        }

        internal bool IsMaterializing => _materializing > 0;

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

        internal bool IsKnownBox(InteractablePackagingBox box)
            => box != null && _idsByBox.ContainsKey(box);

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
            _boxesById.Clear();
            _idsByBox.Clear();
            _pendingFurniture.Clear();
            _pendingHostCreatedId = Guid.Empty;
            ClearHostDeliveryEntries();
            _pendingClientDeliveryIds.Clear();
            _clientOwnedIds.Clear();
            _pendingClientDescriptors.Clear();
            _pendingLocalMoves.Clear();
            _deliverySpawns = 0;
            _sceneSlots.Clear();
            _baselineSaveBoxes.Clear();
            _sceneSlotsCaptured = false;
            _materializing = 0;
            _applyingRemote = 0;
            _suppressDestroyForward = 0;
            HostPredictionId = Guid.Empty;
            if (_cardSpawnAnchor != null)
            {
                UnityEngine.Object.Destroy(_cardSpawnAnchor.gameObject);
                _cardSpawnAnchor = null;
            }
        }

        internal void Dispose()
        {
            if (!_host)
            {
                PlacementApi.StructureChanged -= RetryPendingFurniture;
            }

            Reset();
        }

        internal void ClientBeginBaseline()
        {
            if (_host)
            {
                return;
            }

            _boxesById.Clear();
            _idsByBox.Clear();
            _pendingFurniture.Clear();
            // A baseline re-sends every authoritative box, so any client-owned box still awaiting
            // its descriptor is resolved by the baseline itself. Retaining an un-drained
            // client-owned id (or its queued delivery slot) would leave the authoritative box
            // parked: the baseline binds a fresh object to the same id and the stale ownership
            // keeps the descriptor from being applied.
            _pendingClientDeliveryIds.Clear();
            _pendingClientCreatedId = Guid.Empty;
            _clientOwnedIds.Clear();
            _pendingClientDescriptors.Clear();
            _pendingLocalMoves.Clear();
            _deliverySpawns = 0;
            CaptureSceneSlots();
        }

        /// <summary>Guest: forget the scene-box slots on a scene reload so pre-baseline box
        /// traffic is not bound against a stale list.</summary>
        internal void ClientInvalidateSceneSlots()
        {
            if (_host)
            {
                return;
            }

            _sceneSlots.Clear();
            _baselineSaveBoxes.Clear();
            _sceneSlotsCaptured = false;
        }

        /// <summary>Guest: snapshot the boxes recovered from the transferred save, in the exact
        /// order the host serialized them. The box baseline's slots index into these lists.</summary>
        private void CaptureSceneSlots()
        {
            _sceneSlots.Clear();
            _sceneSlots[(byte)BoxNetworkKind.Item] = SnapshotBoxes(
                RestockManager.GetItemPackagingBoxList(), PlacementInterop.IsRecordWarehouse);
            _sceneSlots[(byte)BoxNetworkKind.Card] =
                SnapshotBoxes(RestockManager.GetCardPackagingBoxList(), false);
            _sceneSlotsCaptured = true;

            // The same objects, flattened for the end-of-baseline sweep. A save box the box
            // baseline never binds is an entity the host dropped while this guest was loading.
            _baselineSaveBoxes.Clear();
            foreach (var boxes in _sceneSlots.Values)
            {
                _baselineSaveBoxes.AddRange(boxes);
            }
        }

        private static List<InteractablePackagingBox> SnapshotBoxes<T>(IList<T> boxes,
            bool skipStored)
            where T : InteractablePackagingBox
        {
            // Mirror the game's own save enumeration, which appends only non-null entries (and,
            // on the records backend, omits stored live boxes): the guest's slot index must line
            // up with the host's filtered manifest index, not with a raw list position that a
            // destroyed or stored entry could shift.
            var list = new List<InteractablePackagingBox>();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null)
                {
                    continue;
                }

                if (skipStored && box is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    continue;
                }

                list.Add(box);
            }

            return list;
        }

        /// <summary>Host: freeze each packaging box's slot for one connection's snapshot manifest.
        /// Captured at the instant the save is written, in the SAME filter/order the game's own
        /// save uses, so the guest's loaded list lines up index for index. The records backend
        /// writes stored live boxes as warehouse records rather than list entries, so they are
        /// skipped there; the legacy backend persists them as live boxes and includes them.</summary>
        internal void CaptureTransferManifest(WorldTransferManifest manifest)
        {
            if (!_host || manifest == null)
            {
                return;
            }

            manifest.Reset();
            CaptureTransferKindSlots(RestockManager.GetItemPackagingBoxList(),
                PlacementInterop.IsRecordWarehouse, manifest);
            CaptureTransferKindSlots(RestockManager.GetCardPackagingBoxList(), false, manifest);
        }

        private void CaptureTransferKindSlots<T>(IList<T> boxes, bool skipStored,
            WorldTransferManifest manifest)
            where T : InteractablePackagingBox
        {
            var slot = 0;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null)
                {
                    continue;
                }

                if (skipStored && box is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    // The records backend does not save a live stored box in the list; it saves
                    // the warehouse record instead. Counting it would shift every later slot.
                    continue;
                }

                if (!TryGetId(box, out var id))
                {
                    id = EnsureHostId(box);
                }

                manifest.SetBoxSlot(id, slot);
                slot++;
            }
        }

        /// <summary>Host: the slot a box would occupy right now in the save's own enumeration.
        /// Used only when a connection has no frozen manifest (a scene-reload re-baseline), and
        /// logged by the caller so a missing snapshot is visible rather than silent.</summary>
        private bool TryGetLiveBoxSlot(InteractablePackagingBox box, out int slot)
        {
            slot = -1;
            if (box == null)
            {
                return false;
            }

            if (box is InteractablePackagingBox_Card)
            {
                return TrySlotInList(RestockManager.GetCardPackagingBoxList(), box, false,
                    out slot);
            }

            return TrySlotInList(RestockManager.GetItemPackagingBoxList(), box,
                PlacementInterop.IsRecordWarehouse, out slot);
        }

        private static bool TrySlotInList<T>(IList<T> boxes, InteractablePackagingBox box,
            bool skipStored, out int slot)
            where T : InteractablePackagingBox
        {
            slot = 0;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var candidate = boxes[i];
                if (candidate == null)
                {
                    continue;
                }

                if (skipStored && candidate is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    continue;
                }

                if (ReferenceEquals(candidate, box))
                {
                    return true;
                }

                slot++;
            }

            slot = -1;
            return false;
        }

        internal void RegisterHostSceneBoxes()
        {
            if (!_host)
            {
                return;
            }

            RegisterHostBoxes(RestockManager.GetItemPackagingBoxList());
            RegisterHostBoxes(RestockManager.GetCardPackagingBoxList());
            RegisterHostBoxes(RestockManager.GetShelfPackagingBoxList());
        }

        internal void RegisterHostCreated(InteractablePackagingBox box)
        {
            if (!_host || box == null || IsMaterializing)
            {
                return;
            }

            var id = ResolveHostCreatedId(box);
            var created = CreateMessage(id, box);
            created.PredictionId = HostPredictionId;
            _broadcast(created);
            CoopPlugin.Log.LogInfo("[box-id] created id=" + id + " kind=" + box.GetType().Name);
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

            if (_idsByBox.TryGetValue(box, out var known))
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

            Bind(assigned, box);
            return assigned;
        }

        private Guid ResolveHostCreatedId(InteractablePackagingBox box) => EnsureHostId(box);

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

            if (_idsByBox.TryGetValue(box, out var known))
            {
                return known;
            }

            if (IsMaterializing)
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

            Bind(id, box);
            _clientOwnedIds.Remove(id);
            if (_pendingClientDescriptors.TryGetValue(id, out var pending))
            {
                _pendingClientDescriptors.Remove(id);
                ApplyCreatedToBox(box, pending);
            }

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
                    Unbind(id, box);
                    DestroyWithoutNotification(box);
                }

                _clientOwnedIds.Remove(id);
                _pendingClientDescriptors.Remove(id);
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

                _pendingClientDescriptors.Remove(id);
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
            if (box != null && _idsByBox.TryGetValue(box, out id))
            {
                return true;
            }

            id = Guid.Empty;
            return false;
        }

        internal void HostRefresh(Guid id)
        {
            if (!_host || id == Guid.Empty || !TryGetBox(id, out var box))
            {
                return;
            }

            _broadcast(CreateMessage(id, box));
        }

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

            _broadcast(CreateMessage(id, box));
        }

        /// <summary>Applies a client-reported item-box state to the host's live box. Unchanged
        /// contents are left alone; only the open flag is touched when contents did not change.
        /// Returns false when a contents change carries an invalid payload, so the caller can
        /// reject the intent. <see cref="ApplyItemState"/> would otherwise spawn as many items as
        /// the client asked for; the count is bounded by <see cref="MaxItemCount"/>.</summary>
        internal bool ApplyHostBoxState(InteractablePackagingBox_Item item,
            BoxStateRequestMessage message)
        {
            if (item == null || message == null)
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
                CoopPlugin.Log.LogWarning("[box-id] rejected box state request id="
                    + message.BoxNetworkId + " type=" + message.ItemType + " count="
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

        /// <summary>Applies the host's authoritative item-box state to a client's box.</summary>
        internal void ClientApplyBoxState(BoxStateMessage message)
        {
            if (!TryGetBox(message.BoxNetworkId, out var box)
                || box is not InteractablePackagingBox_Item item)
            {
                return;
            }

            ApplyItemState(item, new BoxNetworkState
            {
                Kind = BoxNetworkKind.Item,
                ItemType = message.ItemType,
                ItemCount = message.ItemCount,
                IsBoxOpened = message.IsBoxOpened,
            });
        }

        internal BoxNetworkState DescribeAuthoritative(Guid id)
        {
            return _host && id != Guid.Empty && TryGetBox(id, out var box) ? Describe(id, box) : null;
        }

        internal bool TryGetBox(Guid id, out InteractablePackagingBox box)
        {
            box = null;
            return id != Guid.Empty && _boxesById.TryGetValue(id, out box) && box != null;
        }

        /// <summary>True while this peer models the box as stored in a warehouse compartment (and
        /// therefore not carried). A stored box must never be re-attached to a hand: a hold that
        /// arrives for it raced the store that put it there. The state is read from the game
        /// object itself, not a shadow copy.</summary>
        internal bool IsStored(Guid id)
            => id != Guid.Empty && _boxesById.TryGetValue(id, out var box) && IsStoredBox(box);

        /// <summary>Records that this peer has a locally predicted throw or placement for
        /// <paramref name="boxNetworkId"/> awaiting the host. A granted hold or creation descriptor
        /// that arrives until the prediction resolves crossed that move on the wire and must not
        /// override it - re-holding pulled the just-thrown box back into the hand (the first throw
        /// after a take or box-up looked refused) and re-posing teleported it.</summary>
        internal void NotePendingLocalMove(Guid boxNetworkId, Guid predictionId)
        {
            if (boxNetworkId != Guid.Empty && predictionId != Guid.Empty)
            {
                _pendingLocalMoves[boxNetworkId] = predictionId;
            }
        }

        /// <summary>True while a locally predicted throw/placement for this box has not yet been
        /// resolved by the host. Resolved entries are pruned here.</summary>
        internal bool HasPendingLocalMove(Guid boxNetworkId)
        {
            if (!_pendingLocalMoves.TryGetValue(boxNetworkId, out var predictionId))
            {
                return false;
            }

            if (PredictionApi.IsPending(predictionId))
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
        private static bool IsStoredBox(InteractablePackagingBox box)
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
                throw new InvalidOperationException("The host cannot apply a client stored-box state.");
            if (id == Guid.Empty)
                throw new InvalidOperationException("A client stored-box state requires an ID.");

            if (TryGetBox(id, out var box))
            {
                Unbind(id, box);
                DestroyWithoutNotification(box);
            }
        }

        internal void BindStoredLiveBox(Guid id, InteractablePackagingBox_Item box,
            WarehouseBoxState state)
        {
            if (id == Guid.Empty || box == null || state == null)
            {
                throw new InvalidOperationException("A live warehouse box requires its ID and state.");
            }

            if (!_host && TryGetBox(id, out var previous) && !ReferenceEquals(previous, box))
            {
                Unbind(id, previous);
                DestroyWithoutNotification(previous);
            }

            // A stored box is not simulated: the game leaves it frozen from the held state, but a
            // box materialized for the network arrives with physics on and would sag or fall.
            box.SetPhysicsEnabled(false);
            Bind(id, box);
        }

        internal void DetachForWarehouseTake(Guid id)
        {
            if (id == Guid.Empty)
            {
                return;
            }

            if (TryGetBox(id, out var box))
            {
                Unbind(id, box);
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

            var descriptor = ItemDescriptor(id, itemType, amount, isBig, Vector3.zero,
                Quaternion.identity);
            box = MaterializeItem(descriptor);
            if (box == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] could not materialize warehouse take box id=" + id
                    + " (missing prefab or interrupted object); ignoring the delta.");
                return false;
            }

            Bind(id, box);
            return true;
        }

        internal InteractablePackagingBox_Item MaterializeWarehouseStored(EItemType itemType,
            int amount, bool isBig)
        {
            _materializing++;
            try
            {
                return RestockManager.SpawnPackageBoxItem(itemType, amount, isBig);
            }
            finally
            {
                _materializing--;
            }
        }

        internal void HostNotifyDestroyed(InteractablePackagingBox box)
        {
            if (!_host || box == null || !_idsByBox.TryGetValue(box, out var id))
            {
                return;
            }

            // A record-backed warehouse destroys the live box after its ID has moved into record
            // state, but the game object itself still reports that it is stored. That is a
            // representation change, not a network destruction.
            var wasStored = IsStoredBox(box);
            Unbind(id, box);
            CoopPlugin.Log.LogInfo("[box-id] host box destroyed id=" + id + " name=" + box.name
                + " stored=" + wasStored + ".");
            if (wasStored)
            {
                return;
            }

            _broadcast(new BoxDestroyedMessage
            {
                PredictionId = HostPredictionId,
                BoxNetworkId = id,
            });
            CoopPlugin.Log.LogInfo("[box-id] destroyed id=" + id + ".");
        }

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

        /// <summary>Pre-destroy capture for a client box teardown. Built in the hook prefix while
        /// the object is still alive; the postfix forwards the destroy prediction when a capture
        /// is returned. A <c>null</c> capture means "do not forward".</summary>
        internal sealed class ClientDestroyCapture
        {
            internal Guid Id;
            internal BoxCreatedMessage Descriptor;
        }

        /// <summary>Captures the client-side decision to forward a box destroy to the host. Runs in
        /// the hook prefix, before vanilla destroys the object, so identity and descriptor are still
        /// readable. It never suppresses vanilla; it only decides whether to forward.</summary>
        internal ClientDestroyCapture CaptureClientDestroyed(InteractablePackagingBox box)
        {
            if (_host || _applyingRemote > 0 || box == null)
            {
                return null;
            }

            if (!_idsByBox.TryGetValue(box, out var id))
            {
                // A client-local box the host never bound has no authoritative identity to
                // forward a destroy for.
                return null;
            }

            if (id == Guid.Empty)
            {
                return null;
            }

            if (_suppressDestroyForward > 0)
            {
                // The box is being consumed by a container operation that already carries its ID
                // and owns the authoritative result (the host will run the same store and echo a
                // BoxDestroyed). Drop the local binding and let vanilla finish the destroy; sending
                // a competing BoxDestroyRequest would be rejected as an unknown box and its
                // rollback would resurrect this box on the client only.
                Unbind(id, box);
                return null;
            }

            // A furniture package whose boxed object is already detached is being unboxed: the
            // game empties the package (EmptyBoxShelf) before destroying it, and the host box
            // engine retires the box as part of the placement flow. Predicting a box destroy
            // here would tell the host to destroy the furniture instead of placing it, and the
            // descriptor no longer has the boxed object to describe.
            if (box is InteractablePackagingBox_Shelf shelf
                && !TryGetBoxedFurniture(shelf, out _))
            {
                CoopPlugin.Log.LogInfo("[box-id] unbox teardown for id=" + id
                    + "; the placement flow retires the box.");
                return null;
            }

            var descriptor = DescribeKnown(id, box);
            // The game owns the actual destroy and runs it right after this prefix. Mirror the
            // cleanup the engine used to perform in its own apply closure, so a stored box leaves
            // its compartment and a boxed furniture package detaches its object instead of
            // destroying it: without this the local vanilla destroy would strand a ghost slot or
            // tear down furniture the placement flow still owns.
            if (box is InteractablePackagingBox_Shelf furnitureShelf)
            {
                furnitureShelf.EmptyBoxShelf();
            }

            DetachStoredBox(box);
            // Retire the binding now so the engine does not retain the object vanilla is about to
            // destroy.
            Unbind(id, box);
            CoopPlugin.Log.LogInfo("[box-id] captured client destroy-forward id=" + id
                + " kind=" + descriptor.Box.Kind + ".");
            return new ClientDestroyCapture { Id = id, Descriptor = descriptor };
        }

        /// <summary>Registers the destroy prediction after the game's own <c>OnDestroyed</c> ran.
        /// The host either confirms it (the prediction is retired; the game already applied the
        /// change) or rejects it, and a rejection respawns the box from the captured descriptor.</summary>
        internal void ForwardClientDestroyed(ClientDestroyCapture capture)
        {
            if (capture == null)
            {
                return;
            }

            var id = capture.Id;
            var descriptor = capture.Descriptor;
            CoopPlugin.Log.LogInfo("[box-id] forwarding client destroy id=" + id + ".");
            var intent = new BoxDestroyRequestMessage { BoxNetworkId = id };
            WorldPrediction.Predict(WorldPrediction.BoxesScope, intent,
                () =>
                {
                    if (TryGetBox(id, out var live))
                    {
                        Unbind(id, live);
                        DestroyWithoutNotification(live);
                    }
                },
                () => ClientApplyCreated(descriptor));
        }

        internal BoxCreatedMessage DescribeKnown(Guid id, InteractablePackagingBox box)
            => CreateMessage(id, box);

        internal bool HostDestroyRequested(Guid id, Guid predictionId)
        {
            if (!_host || id == Guid.Empty)
            {
                return false;
            }

            if (TryGetBox(id, out var box))
            {
                HostPredictionId = predictionId;
                try
                {
                    // A stored box destroyed in place would leave a ghost in its compartment.
                    DetachStoredBox(box);
                    box.OnDestroyed();
                }
                finally
                {
                    HostPredictionId = Guid.Empty;
                }
                return true;
            }

            // The box has no live object: either the ID is already retired or it names a
            // warehouse record, which the warehouse channel owns. This channel has nothing to
            // destroy, so the request is not a valid target here.
            return false;
        }

        internal void ClientApplyCreated(BoxCreatedMessage message)
        {
            if (!_host && !_sceneSlotsCaptured)
            {
                // Pre-baseline traffic. The ordered baseline that follows resends every box, so
                // drop this rather than binding it against a list we have not captured yet.
                return;
            }

            var state = message.Box;
            if (state.Kind == BoxNetworkKind.Furniture
                && !PlacementApi.IsPlacementIdentityReady)
            {
                _pendingFurniture[state.BoxNetworkId] = message;
                return;
            }

            if (TryGetBox(state.BoxNetworkId, out var existing))
            {
                ApplyCreatedToBox(existing, message);
                CoopPlugin.Log.LogInfo("[box-id] refreshed authoritative box id="
                    + state.BoxNetworkId + " kind=" + state.Kind + ".");
                return;
            }

            // A box this client owns (its own checkout pre-assigned the id) must not be
            // materialized as a duplicate before the local vanilla spawn happens. Hold the
            // descriptor and apply it when the real object is created and bound.
            if (_clientOwnedIds.Contains(state.BoxNetworkId))
            {
                _pendingClientDescriptors[state.BoxNetworkId] = message;
                return;
            }

            // A snapshot slot names a box this peer recovered from the transferred save; its id
            // is the one the host's baseline carries, and the guest binds the slot's box to it.
            // A runtime box (slot < 0) was created by a creator-assigned id: if this peer is the
            // creator its local object is already bound, otherwise it materializes the descriptor.
            var box = message.SnapshotSlot >= 0
                ? ResolveSceneSlot(state.Kind, message.SnapshotSlot, state.BoxNetworkId)
                : null;
            if (box == null)
            {
                if (message.SnapshotSlot >= 0)
                {
                    // A slot is a binding hint, not identity. The guest's transferred save can
                    // disagree with the host's live-list index (a box consumed, merged, or swept
                    // during load, a list removal, or a build-specific count), so a miss falls
                    // back to the authoritative descriptor instead of throwing the session down.
                    CoopPlugin.Log.LogWarning("[box-id] snapshot slot " + message.SnapshotSlot
                        + " did not resolve (kind=" + state.Kind + "); materializing box id="
                        + state.BoxNetworkId + " from its descriptor.");
                }

                box = Materialize(state, message.StableEntityId);
            }

            if (box == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] could not materialize authoritative box id="
                    + state.BoxNetworkId + " kind=" + state.Kind + " slot="
                    + message.SnapshotSlot + "; descriptor dropped.");
                return;
            }

            Bind(state.BoxNetworkId, box);
            CoopPlugin.Log.LogInfo("[box-id] bound authoritative box id=" + state.BoxNetworkId
                + " kind=" + state.Kind + " slot=" + message.SnapshotSlot + " object=" + box.name
                + ".");
            ApplyCreatedToBox(box, message);
        }

        /// <summary>Applies a creation descriptor to a box already bound to its id: adopts the
        /// host's placement identity for boxed furniture, then mirrors pose (unless the box is
        /// held) and contents.</summary>
        private void ApplyCreatedToBox(InteractablePackagingBox box, BoxCreatedMessage message)
        {
            var state = message.Box;
            // The descriptor names the host's placement entity for the furniture inside one of
            // this peer's boxes. Adopt that identity onto the local boxed object: the peer that
            // created the box already holds it, and without this the placement channel would
            // materialize a second object - a client-only ghost box the host cannot address.
            var identityBound = state.Kind == BoxNetworkKind.Furniture
                && AdoptFurnitureIdentity(box, message);

            if (box is InteractablePackagingBox_Item itemBox)
            {
                ApplyItemState(itemBox, state);
            }

            if (identityBound)
            {
                // A placement delta for this entity may have been deferred while the box channel
                // had not yet bound the key; apply it now that the local object is the entity.
                WorldClientBehaviour.RetryDeferredPlacementDeltas();
            }

            // The descriptor carries the newest authoritative pose for this box, so its pose is
            // applied after any replayed delta: a deferred snapshot can describe the package
            // before the host moved it to its delivery spot (a purchase's box-up precedes the
            // spawn pose), and replaying that snapshot must not leave the box at the raw spawn.
            // A local throw or placement still awaiting the host is newer than this descriptor
            // though, and posing on top of it teleported the just-thrown box.
            if (!IsBeingHeld(box) && !HasPendingLocalMove(state.BoxNetworkId))
            {
                ApplyPose(box, state.Position, state.Rotation);
            }
        }

        /// <summary>Client: binds the host placement identity the descriptor names onto the local
        /// boxed furniture object. Returns true when the object holds that identity afterwards.</summary>
        private static bool AdoptFurnitureIdentity(InteractablePackagingBox box, BoxCreatedMessage message)
        {
            return box is InteractablePackagingBox_Shelf shelf
                && TryGetBoxedFurniture(shelf, out var furniture)
                && PlacementIdentity.TryAdoptBoxableFurnitureIdentity(furniture,
                    message.StableEntityId, WorldMessageMetadata.FurnitureIdentityScope,
                    message.Box.FurnitureObjectType);
        }

        /// <summary>Guest: the box the host's snapshot slot names, captured at baseline start.
        /// A slot is only a binding hint: a destroyed entry, or a box already bound to a different
        /// network id, does not resolve, so the caller falls back to the authoritative descriptor.</summary>
        private InteractablePackagingBox ResolveSceneSlot(BoxNetworkKind kind, int slot,
            Guid boxNetworkId)
        {
            if (!_sceneSlots.TryGetValue((byte)kind, out var boxes) || slot < 0
                || slot >= boxes.Count)
            {
                return null;
            }

            var box = boxes[slot];
            if (box == null)
            {
                return null;
            }

            if (_idsByBox.TryGetValue(box, out var boundId) && boundId != boxNetworkId)
            {
                if (_boxesById.ContainsKey(boundId))
                {
                    // The box at this slot is genuinely bound to another live network id, so it is
                    // not the box the snapshot slot names. Let the caller materialize the
                    // authoritative descriptor (a duplicate beats losing the session).
                    CoopPlugin.Log.LogWarning("[box-id] snapshot slot " + slot + " for kind=" + kind
                        + " is bound to live id=" + boundId + " (wanted " + boxNetworkId
                        + "); materializing the authoritative box.");
                    return null;
                }

                // A mapping whose id is no longer live is stale: fall through so the authoritative
                // Bind rebinds this box instead of materializing a duplicate.
                CoopPlugin.Log.LogWarning("[box-id] snapshot slot " + slot + " for kind=" + kind
                    + " had stale id=" + boundId + "; rebinding to " + boxNetworkId + ".");
            }

            return box;
        }

        internal void ClientApplyDestroyed(BoxDestroyedMessage message)
        {
            var wasPending = _pendingFurniture.Remove(message.BoxNetworkId);
            _clientOwnedIds.Remove(message.BoxNetworkId);
            _pendingClientDescriptors.Remove(message.BoxNetworkId);
            if (!TryGetBox(message.BoxNetworkId, out var box))
            {
                // The live package may already be gone because the player unboxed it locally and
                // the host is only now confirming the destruction. Drop any stale binding instead
                // of treating our own already-applied destroy as a protocol error.
                if (_boxesById.TryGetValue(message.BoxNetworkId, out var stale))
                {
                    Unbind(message.BoxNetworkId, stale);
                    return;
                }

                // Destruction is idempotent. A furniture delivery box can be torn down locally
                // (unboxing) while the host's BoxCreated is still deferred, so the authoritative
                // destroy can legitimately name an id this peer never bound. Recognizing it as
                // already applied is the correct client behavior; throwing here used to tear the
                // session down.
                if (message.BoxNetworkId != Guid.Empty)
                {
                    CoopPlugin.Log.LogInfo("[box-id] authoritative destroy for already-gone box id="
                        + message.BoxNetworkId + " pending=" + wasPending + ".");
                }

                return;
            }

            Unbind(message.BoxNetworkId, box);
            DestroyWithoutNotification(box);
        }

        internal void ClientCompleteBaseline()
        {
            SweepUnrepresentedSaveBoxes();
            RetryPendingFurniture();
        }

        /// <summary>Guest: after the whole baseline has been applied, destroy every box this peer
        /// loaded from the transferred save that the host no longer represents. The save is a
        /// snapshot taken while the host kept playing: a box the host destroyed, or moved into a
        /// warehouse record, while this guest was loading is absent from the box baseline, so its
        /// loaded object would otherwise linger as a ghost. A box the baseline bound to a live
        /// host id is authoritative and kept; everything else is removed through the game path.
        /// Idempotent, so the box-complete and world-complete markers can both call it.</summary>
        private void SweepUnrepresentedSaveBoxes()
        {
            if (_host || _baselineSaveBoxes.Count == 0)
            {
                return;
            }

            var swept = 0;
            for (var i = 0; i < _baselineSaveBoxes.Count; i++)
            {
                var box = _baselineSaveBoxes[i];
                if (box == null)
                {
                    continue;
                }

                var bound = _idsByBox.TryGetValue(box, out var boundId);
                if (bound && _boxesById.ContainsKey(boundId))
                {
                    continue;
                }

                if (bound)
                {
                    Unbind(boundId, box);
                }

                CoopPlugin.Log.LogInfo("[box-id] sweeping save box " + box.name
                    + " the host no longer represents.");
                DestroyWithoutNotification(box);
                swept++;
            }

            _baselineSaveBoxes.Clear();
            if (swept > 0)
            {
                CoopPlugin.Log.LogInfo("[box-id] swept " + swept
                    + " unbound save box(es) after the world baseline.");
            }
        }

        internal void ClientForgetPhysical(Guid id, InteractablePackagingBox box)
        {
            if (_host || id == Guid.Empty || box == null)
            {
                return;
            }

            Unbind(id, box);
            DestroyWithoutNotification(box);
        }

        /// <summary>
        /// Appends a frozen creation snapshot to a host-owned baseline work item. The caller
        /// owns send and completion ordering; this method must not send or emit the completion
        /// marker itself.
        /// </summary>
        internal void AppendBaselineMessages(WorldTransferManifest manifest,
            Action<INetMessage> append)
        {
            if (!_host || append == null)
            {
                return;
            }

            foreach (var pair in _boxesById)
            {
                var box = pair.Value;
                if (box == null || (box is InteractablePackagingBox_Item item && item.m_IsStored))
                {
                    continue;
                }

                var message = CreateMessage(pair.Key, box);
                if (manifest != null)
                {
                    // A box created after the snapshot has no slot; leaving it -1 makes the guest
                    // materialize the descriptor instead of binding a save box by a live index.
                    if (manifest.TryGetBoxSlot(pair.Key, out var slot))
                    {
                        message.SnapshotSlot = slot;
                    }
                }
                else
                {
                    if (TryGetLiveBoxSlot(box, out var liveSlot))
                    {
                        message.SnapshotSlot = liveSlot;
                    }

                    CoopPlugin.Log.LogWarning("[box-id] no frozen snapshot manifest for box id="
                        + pair.Key + "; this connection's baseline is using live slot "
                        + message.SnapshotSlot + ".");
                }

                append(message);
            }
        }

        private BoxCreatedMessage CreateMessage(Guid id, InteractablePackagingBox box)
        {
            var message = new BoxCreatedMessage { Box = Describe(id, box) };

            if (box is not InteractablePackagingBox_Shelf shelf)
            {
                return message;
            }

            if (TryGetBoxedFurniture(shelf, out var furniture)
                && PlacementApi.TryMakeBoxableFurnitureEntityId(furniture,
                    WorldMessageMetadata.FurnitureIdentityScope,
                    out var entityId))
            {
                message.StableEntityId = entityId;
            }
            else
            {
                CoopPlugin.Log.LogWarning("[box-furniture] could not assign placement identity to box id="
                    + id + ".");
            }

            return message;
        }

        private static bool TryGetBoxedFurniture(InteractablePackagingBox_Shelf box,
            out InteractableObject furniture)
        {
            furniture = BoxedFurniture?.GetValue(box) as InteractableObject;
            return furniture != null;
        }

        /// <summary>True while this box is in a player's hand. The host's own furniture box-up
        /// holds the box before it has a network id, so the hold is announced when it registers.</summary>
        internal bool IsBeingHeld(InteractablePackagingBox box)
            => box != null && BeingHoldField?.GetValue(box) is bool held && held;

        private void RetryPendingFurniture(int _)
        {
            RetryPendingFurniture();
        }

        private void RetryPendingFurniture()
        {
            if (_host || _pendingFurniture.Count == 0 || !PlacementApi.IsPlacementIdentityReady)
            {
                return;
            }

            var pending = new List<BoxCreatedMessage>(_pendingFurniture.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                var message = pending[i];
                _pendingFurniture.Remove(message.Box.BoxNetworkId);
                ClientApplyCreated(message);
            }
        }

        private void RegisterHostBoxes<T>(IList<T> boxes) where T : InteractablePackagingBox
        {
            if (boxes == null)
            {
                return;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] != null)
                {
                    EnsureHostId(boxes[i]);
                }
            }
        }

        /// <summary>True when <paramref name="id"/> is already claimed by a live box. Creator-assigned
        /// ids must be fresh GUIDs; adopting one that names a live box would rebind that box (a
        /// cross-entity morph/ghost). <c>_idsByBox</c> is scanned as well so a dead entry (a
        /// destroyed object Unity still reports as non-null this frame) is treated conservatively
        /// as in use.</summary>
        private bool IsIdInUse(Guid id)
            => IsIdInUseByOtherThan(id, null);

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

            if (_boxesById.TryGetValue(id, out var box) && box != null
                && !ReferenceEquals(box, reassigningFrom))
            {
                return true;
            }

            foreach (var pair in _idsByBox)
            {
                if (pair.Value == id && !ReferenceEquals(pair.Key, reassigningFrom))
                {
                    return true;
                }
            }

            return false;
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

        private void Bind(Guid id, InteractablePackagingBox box)
        {
            if (id == Guid.Empty || box == null)
            {
                throw new InvalidOperationException("Cannot bind an invalid box network ID.");
            }

            if (_boxesById.TryGetValue(id, out var previous) && previous != null
                && !ReferenceEquals(previous, box))
            {
                // A creator-supplied id is rejected before it reaches Bind, so a live rebind here
                // is the intentional representation handoff (warehouse record -> live box, a stale
                // snapshot slot). Log it so an unexpected steal is visible instead of silent.
                CoopPlugin.Log.LogWarning("[box-id] rebinding live id=" + id + " from "
                    + previous.name + " to " + box.name + ".");
                _idsByBox.Remove(previous);
            }

            if (_idsByBox.TryGetValue(box, out var previousId) && previousId != id)
            {
                _boxesById.Remove(previousId);
            }

            _boxesById[id] = box;
            _idsByBox[box] = id;
        }

        private void Unbind(Guid id, InteractablePackagingBox box)
        {
            _boxesById.Remove(id);
            if (box != null)
            {
                _idsByBox.Remove(box);
            }
        }

        private static BoxNetworkState Describe(Guid id, InteractablePackagingBox box)
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

            if (box is InteractablePackagingBox_Shelf shelf)
            {
                return new BoxNetworkState
                {
                    BoxNetworkId = id,
                    Kind = BoxNetworkKind.Furniture,
                    Position = shelf.transform.position,
                    Rotation = shelf.transform.rotation,
                    FurnitureObjectType = shelf.GetBoxedObjectType(),
                };
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

        private InteractablePackagingBox Materialize(BoxNetworkState state, string furnitureEntityId)
        {
            _materializing++;
            try
            {
                switch (state.Kind)
                {
                    case BoxNetworkKind.Item:
                        return MaterializeItem(state);
                    case BoxNetworkKind.Card:
                        return MaterializeCard(state);
                    case BoxNetworkKind.Furniture:
                        return MaterializeFurniture(state, furnitureEntityId);
                    default:
                        // An unknown kind must not throw down the reliable lane; the descriptor is
                        // dropped and the caller logs it.
                        CoopPlugin.Log.LogWarning("[box-id] unsupported authoritative box kind "
                            + state.Kind + " id=" + state.BoxNetworkId + ".");
                        return null;
                }
            }
            finally
            {
                _materializing--;
            }
        }

        private InteractablePackagingBox_Item MaterializeItem(BoxNetworkState state)
        {
            _materializing++;
            try
            {
                return RestockManager.SpawnPackageBoxItem(state.ItemType, state.ItemCount,
                    state.IsBig);
            }
            finally
            {
                _materializing--;
            }
        }

        private InteractablePackagingBox_Card MaterializeCard(BoxNetworkState state)
        {
            if (_cardSpawnAnchor == null)
            {
                _cardSpawnAnchor = new GameObject("CardShopCoop.BoxNetworkCardSpawn").transform;
            }

            _cardSpawnAnchor.SetPositionAndRotation(state.Position, state.Rotation);
            var cards = new List<CardData>(state.Cards.Count);
            for (var i = 0; i < state.Cards.Count; i++)
            {
                cards.Add(FromState(state.Cards[i]));
            }

            return RestockManager.SpawnPackageBoxCard(cards, _cardSpawnAnchor);
        }

        private InteractablePackagingBox_Shelf MaterializeFurniture(BoxNetworkState state,
            string furnitureEntityId)
        {
            if (PlacementApi.TryResolveBoxableFurnitureEntityId(furnitureEntityId,
                WorldMessageMetadata.FurnitureIdentityScope,
                state.FurnitureObjectType, out var existing, false))
            {
                // The client suppresses the local hold-box input, so the exact placement object is
                // still present when the authoritative descriptor arrives. Box it in-place instead
                // of choosing another object with the same type or a nearby position.
                var package = existing.GetPackagingBoxShelf();
                if (package == null)
                {
                    CoopPlugin.Log.LogInfo("[box-furniture] applying authoritative box-up to exact "
                        + state.FurnitureObjectType + " placement for box id=" + state.BoxNetworkId
                        + ".");
                    existing.BoxUpObject(false);
                    package = existing.GetPackagingBoxShelf();
                }

                return package;
            }

            // The host spawned this furniture and named its placement identity, but this peer
            // never saw a placement delta for it. The box descriptor is self-sufficient: create
            // the exact object through the game's factory and bind the identity it names, rather
            // than adopting a same-type local object.
            if (!PlacementApi.TryCreateBoxableFurnitureForIdentity(furnitureEntityId,
                WorldMessageMetadata.FurnitureIdentityScope, state.FurnitureObjectType,
                state.Position, state.Rotation, out var created))
            {
                // The subject may have been destroyed or interrupted. Do not throw down the
                // reliable lane; the caller drops the descriptor.
                CoopPlugin.Log.LogWarning("[box-furniture] could not resolve or create authoritative "
                    + "furniture identity " + furnitureEntityId + " for box id=" + state.BoxNetworkId
                    + "; descriptor dropped.");
                return null;
            }

            CoopPlugin.Log.LogInfo("[box-furniture] created host-spawned furniture for box id="
                + state.BoxNetworkId + " entity=" + furnitureEntityId + ".");
            return created.GetPackagingBoxShelf();
        }

        /// <summary>Tears a box down through the game's own <c>OnDestroyed</c> path - which is
        /// what removes it from <c>RestockManager</c>'s packaging-box lists - while suppressing
        /// this peer's own destroy forwarding (the caller owns the id's next life). A raw
        /// <c>Object.Destroy</c> skips that cleanup: the dead entry stays in
        /// <c>RestockManager.m_ItemPackagingBoxList</c> and its <c>Update</c> throws on it every
        /// out-of-bounds tick, which also kills the loop's box rescue.</summary>
        internal void DestroyWithoutNotification(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            _applyingRemote++;
            try
            {
                // A furniture package's OnDestroyed destroys its boxed object too. This path is the
                // box ceasing to exist (unbox, warehouse move, authoritative destroy), not the
                // furniture dying, so detach first and let the placement channel own the item.
                if (box is InteractablePackagingBox_Shelf shelf)
                {
                    shelf.EmptyBoxShelf();
                }

                // A stored item box must leave its warehouse compartment explicitly: the game's
                // OnDestroyed does not, and a destroyed entry left in the list still occupies the
                // slot, so the shelf shows a box that is not there and refuses new boxes.
                DetachStoredBox(box);

                box.OnDestroyed();
            }
            finally
            {
                _applyingRemote--;
            }
        }

        /// <summary>Removes a stored item box from its warehouse compartment, if it is in one.
        /// Idempotent: a box already detached (or not stored) is left alone.</summary>
        private static void DetachStoredBox(InteractablePackagingBox box)
        {
            if (box is not InteractablePackagingBox_Item item)
            {
                return;
            }

            var compartment = item.GetBoxStoredCompartment();
            if (compartment == null)
            {
                return;
            }

            var stored = compartment.GetInteractablePackagingBoxList();
            if (stored != null && stored.Contains(item))
            {
                CoopPlugin.Log.LogInfo("[warehouse] detaching destroyed stored box " + item.name
                    + " from shelf=" + compartment.GetWarehouseIndex() + " comp="
                    + compartment.GetIndex() + "; the slot would otherwise stay blocked.");
                compartment.RemoveBox(item);
            }
        }

        private static void ApplyPose(InteractablePackagingBox box, Vector3 position,
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
        private static void ApplyItemState(InteractablePackagingBox_Item item, BoxNetworkState state)
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
                    if (items[i] != null)
                    {
                        items[i].DisableItem();
                    }
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
