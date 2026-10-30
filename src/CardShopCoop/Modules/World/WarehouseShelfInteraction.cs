using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Owns warehouse box storage for the World module. Both live builds are record-backed: a
    /// stored entry is a StoredBoxRecord in a <see cref="ShelfCompartment"/>. A live
    /// <see cref="InteractablePackagingBox"/> is only a representation (held, carried, or a take
    /// replica), never the stored truth. The identity module owns the Guid/location registry that
    /// names entries on the wire; this class owns compartment state, prediction ordering, and the
    /// game apply paths.
    /// </summary>
    internal sealed class WarehouseShelfInteraction
    {
        private const int MaxWarehouseAmount = 4096;

        private static bool _probed;
        private static bool _usesRecords;
        private static Type _recordType;
        private static Type _recordItemType;
        private static MethodInfo _recordCount;
        private static MethodInfo _recordPeek;
        private static MethodInfo _recordPop;
        private static MethodInfo _recordAdd;
        private static MethodInfo _recordRebuild;
        private static MethodInfo _recordMarkDirty;
        private static MethodInfo _recordHide;
        private static PropertyInfo _batcherInstance;
        private static FieldInfo _recordItem;
        private static FieldInfo _recordAmount;
        private static FieldInfo _recordBig;
        private static FieldInfo _recordList;
        private static FieldInfo _compartmentBig;

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<ICoopPeer, INetMessage> _send;
        private readonly BoxNetworkInteraction _boxes;
        private readonly PlayerBoxInteraction _playerBox;
        private readonly Func<bool> _inGame;

        // Both peers, records backend: the item box whose OnFinishLerp is currently running. The
        // game adds its record from inside that method, so the AddStoredBoxRecord hook binds the
        // record to exactly this box. It is non-null only for the synchronous record add it owns.
        private InteractablePackagingBox_Item _currentStoreBox;
        private ShelfCompartment _currentStoreCompartment;
        private WarehouseCompartmentState _currentStoreSnapshot;
        private bool _hostApplyingCommand;
        private bool _applyingRemote;

        // Client: store predictions that have not settled yet, by action id, mapped to the entry
        // identity they own. An authoritative compartment state that predates the action may arrive
        // while its predicted record is transiently absent; the identity must survive that so the
        // box's extended information is not retired under a still-pending action.
        private readonly Dictionary<Guid, Guid> _pendingStores = new();

        // A shelf or compartment that is not loaded yet cannot be applied now. Keep the message
        // and retry on the world-ready/placement-structure signals instead of dropping it.
        private readonly List<WarehouseStateMessage> _deferredStates = new();
        private readonly List<WarehouseDeltaMessage> _deferredDeltas = new();

        internal readonly struct WarehouseBoxFacts
        {
            internal readonly EItemType ItemType;
            internal readonly int Amount;
            internal readonly bool IsBig;

            internal WarehouseBoxFacts(EItemType itemType, int amount, bool isBig)
            {
                ItemType = itemType;
                Amount = amount;
                IsBig = isBig;
            }

            internal bool IsValid => ItemType != EItemType.None && Amount > 0;
        }

        internal sealed class WarehouseTakeCapture
        {
            internal ShelfCompartment Compartment;
            internal InteractablePackagingBox PreviousTop;
            internal WarehouseShelf Shelf;
            internal int CompartmentIndex;
            internal int SourceSlot;
            internal Guid SourceId;
            internal WarehouseBoxFacts Facts;
            internal int RecordCount;
            internal bool Covered;
        }

        internal WarehouseShelfInteraction(bool host, Action<INetMessage> broadcast,
            Action<ICoopPeer, INetMessage> send, BoxNetworkInteraction boxes,
            PlayerBoxInteraction playerBox = null, Func<bool> inGame = null)
        {
            _host = host;
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _playerBox = playerBox;
            _inGame = inGame;
            Probe();
        }

        private bool InGame() => _inGame == null || _inGame();

        internal static bool Available()
        {
            Probe();
            return _usesRecords;
        }

        internal void Reset()
        {
            _deferredStates.Clear();
            _deferredDeltas.Clear();
            _pendingStores.Clear();
            _currentStoreBox = null;
            _currentStoreCompartment = null;
            _currentStoreSnapshot = null;
            _hostApplyingCommand = false;
            _applyingRemote = false;
            WarehouseBoxLocations.Reset();
        }

        /// <summary>Retries messages whose shelf/compartment was not loaded when they arrived.
        /// Structure changes (placement rebuild) and world-ready are the two lifecycle signals.</summary>
        internal void FlushClientState()
        {
            if (_host || (_deferredStates.Count == 0 && _deferredDeltas.Count == 0))
            {
                return;
            }

            if (_deferredStates.Count > 0)
            {
                var states = new List<WarehouseStateMessage>(_deferredStates);
                _deferredStates.Clear();
                for (var i = 0; i < states.Count; i++)
                {
                    ClientApplyState(states[i]);
                }
            }

            if (_deferredDeltas.Count > 0)
            {
                var deltas = new List<WarehouseDeltaMessage>(_deferredDeltas);
                _deferredDeltas.Clear();
                for (var i = 0; i < deltas.Count; i++)
                {
                    ClientApplyDelta(deltas[i]);
                }
            }
        }

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null || !Available())
            {
                return;
            }

            append(BuildFullState());
        }

        /// <summary>Captures the compartment before the game's own record store appends its new
        /// record. The rejected-store undo rebuilds from this value snapshot, never from a stored
        /// record object.</summary>
        internal void BeginLocalRecordStore(InteractablePackagingBox_Item box)
        {
            _currentStoreBox = box;
            if (_currentStoreBox != null)
            {
                CoopPlugin.Log.LogInfo("[warehouse] record store begin box=" + box.name);
                // A record store consumes the live box from inside OnFinishLerp. The store intent
                // owns that lifecycle change; suppress the competing box-destroy forward so the
                // host keeps the box it just stored instead of retiring it.
                _boxes.BeginContainerConsume();
            }

            _currentStoreCompartment = _currentStoreBox?.GetBoxStoredCompartment();
            _currentStoreSnapshot = _currentStoreCompartment == null
                ? null
                : CaptureCompartmentState(_currentStoreCompartment);
        }

        internal void EndLocalRecordStore()
        {
            var compartment = _currentStoreCompartment;
            var consuming = _currentStoreBox != null;
            _currentStoreBox = null;
            _currentStoreCompartment = null;
            _currentStoreSnapshot = null;
            if (consuming)
            {
                _boxes.EndContainerConsume();
            }
            if (compartment != null && IsWarehouse(compartment))
            {
                // Vanilla's OnFinishLerp arranges records with an unstable sort; re-assert the
                // canonical comparator here so both peers agree on the top and insertion slot.
                CanonicalizeRecords(compartment);
            }
        }

        /// <summary>Moves a taken warehouse box into the local player's hand through the shared
        /// single-hand path, so a take can never stack a second box on top of one already held.</summary>
        private void TakeIntoLocalHand(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            if (_playerBox != null)
            {
                _playerBox.TakeCoveredIntoLocalHand(box);
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                box.StartHoldBox(true, controller.m_HoldItemPos);
            }
        }

        /// <summary>Client: captures the top compartment entry before the game's own take. The
        /// values and identity are read while the entry is definitely still the top, because a
        /// later reconcile cannot re-read an order that has changed.</summary>
        internal WarehouseTakeCapture CaptureClientTake(InteractableStorageCompartment storage)
        {
            if (_host || _applyingRemote || !Available() || storage == null)
            {
                return null;
            }

            var compartment = storage.GetShelfCompartment();
            if (!IsWarehouse(compartment) || !TryGetAddress(compartment, out var shelf,
                    out var compartmentIndex))
            {
                return null;
            }

            if (!TryGetTopEntry(compartment, out var facts, out var sourceId, out var slot,
                    out var live))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take could not resolve the top entry on shelf ["
                    + (shelf == null ? "<null>" : shelf.name) + "," + compartmentIndex
                    + "]; leaving the box in place.");
                return null;
            }

            _playerBox?.BeginCoveredHold();
            return new WarehouseTakeCapture
            {
                Compartment = compartment,
                PreviousTop = live,
                Shelf = shelf,
                CompartmentIndex = compartmentIndex,
                SourceSlot = slot,
                SourceId = sourceId,
                Facts = facts,
                RecordCount = RecordCount(compartment),
                Covered = true,
            };
        }

        /// <summary>Client: after the game's own take ran, binds the newly spawned live box and
        /// queues exactly one take intent. The game already performed the local mutation.</summary>
        internal void ObserveClientTake(WarehouseTakeCapture capture)
        {
            if (capture?.Compartment == null || !DidClientTakeHappen(capture))
            {
                return;
            }

            var held = _playerBox?.LocalHeldBox as InteractablePackagingBox_Item;
            if (held == null
                && !_boxes.ClientEnsureWarehouseTake(capture.SourceId, capture.Facts.ItemType,
                    capture.Facts.Amount, capture.Facts.IsBig, out held))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take box=" + capture.SourceId
                    + " has no live box in hand and could not be materialized; not forwarding.");
                return;
            }

            // The taken box inherits the stored entry's identity. Reusing the source Guid keeps
            // extended information and every in-flight reference attached across store -> take.
            var createdId = capture.SourceId;
            if (createdId == Guid.Empty)
            {
                createdId = WorldClientBehaviour.AssignCreatedBoxId(held);
            }
            else
            {
                BoxIdentityLifecycle.BindClientBox(createdId, held);
            }

            if (createdId == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] take box=" + capture.SourceId
                    + " could not be bound to an identity; not forwarding.");
                return;
            }

            // The popped entry no longer names a shelf location; the created live box owns the
            // identity from here on.
            WarehouseBoxLocations.ClearLocation(capture.SourceId);
            var sourceRef = MakeRef(capture.Shelf, capture.CompartmentIndex, capture.SourceId,
                capture.SourceSlot);
            var descriptor = DescribeCreatedBox(createdId, held, capture.Facts);
            SendTake(new WarehouseTakeAction
            {
                Source = sourceRef,
                CreatedBox = descriptor,
                Facts = capture.Facts,
                HeldBox = held,
                Origin = this,
            });
        }

        internal void EndCoveredTake(WarehouseTakeCapture capture)
        {
            if (capture?.Covered != true)
            {
                return;
            }

            capture.Covered = false;
            _playerBox?.EndCoveredHold();
        }

        private bool DidClientTakeHappen(WarehouseTakeCapture capture)
        {
            // The record list is the only storage truth. A take always pops the top record, so a
            // lower count is proof the game's own click path consumed the entry.
            return RecordCount(capture.Compartment) < capture.RecordCount;
        }

        /// <summary>Client: a worker is carrying world box <paramref name="boxNetworkId"/>. Detach
        /// it from any warehouse compartment and park the real object out of view.</summary>
        internal void ApplyWorkerHeldBox(Guid boxNetworkId)
        {
            if (_host || boxNetworkId == Guid.Empty || !Available())
            {
                return;
            }

            if (!_boxes.TryGetBox(boxNetworkId, out var box)
                || box is not InteractablePackagingBox_Item item)
            {
                return;
            }

            var compartment = item.GetBoxStoredCompartment();
            if (compartment != null)
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                if (boxes != null && boxes.Contains(item))
                {
                    compartment.RemoveBox(item);
                }
            }

            WarehouseBoxLocations.ClearLocation(boxNetworkId);
            item.m_IsStored = false;
            item.SetPhysicsEnabled(false);
            item.transform.position = ParkedWorkerBoxPosition;
        }

        internal void RestoreWorkerDroppedBox(Guid boxNetworkId)
        {
            if (_host || boxNetworkId == Guid.Empty || !Available())
            {
                return;
            }

            if (!_boxes.TryGetBox(boxNetworkId, out var box)
                || box is not InteractablePackagingBox_Item item
                || item.m_IsStored)
            {
                return;
            }

            item.SetPhysicsEnabled(true);
        }

        private static readonly Vector3 ParkedWorkerBoxPosition = new Vector3(10000f, 10000f, 10000f);

        /// <summary>Both peers, records backend: the game appended one stored record. Only a local
        /// game-driven store has <see cref="_currentStoreBox"/> set; save-load adds and explicit
        /// authoritative applies are handled by their own paths.</summary>
        internal void OnStoredBoxRecordAdded(ShelfCompartment compartment)
        {
            if (compartment == null || !IsWarehouse(compartment)
                || _hostApplyingCommand || _applyingRemote || ReentrancyGuard.ActionApplying)
            {
                return;
            }

            if (_currentStoreBox == null)
            {
                // A save load adds records before any box exists. The baseline assigns their
                // stable identities lazily via WarehouseBoxLocations.
                return;
            }

            if (!_host && WorldClientBehaviour.IsPreloadHeld)
            {
                return;
            }

            if (!InGame())
            {
                return;
            }

            if (!TryGetAddress(compartment, out var shelf, out var compartmentIndex))
            {
                return;
            }

            var id = _host
                ? (_boxes.TryGetId(_currentStoreBox, out var hostId)
                    ? hostId : _boxes.EnsureWarehouseHostId(_currentStoreBox))
                : (_boxes.TryGetId(_currentStoreBox, out var clientId)
                    ? clientId : WorldClientBehaviour.AssignCreatedBoxId(_currentStoreBox));
            if (id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] stored record had no identity on shelf ["
                    + compartmentIndex + "]; the baseline will assign one.");
                return;
            }

            // The new record is already appended to the game list. Give any pre-existing records
            // (a save load) their identities first so the registry order stays aligned.
            WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex,
                Math.Max(0, RecordCount(compartment) - 1));
            var slot = WarehouseBoxLocations.AppendStored(id, shelf, compartmentIndex,
                WarehouseBoxLocations.NextSequence());
            CanonicalizeRecords(compartment, false);
            if (WarehouseBoxLocations.TryGetLocation(id, out var location))
            {
                slot = location.Slot;
            }

            var facts = ReadStoredFacts(compartment, id, out var recordFacts)
                ? recordFacts : DescribeLiveBox(_currentStoreBox);
            if (!facts.IsValid)
            {
                CoopPlugin.Log.LogWarning("[warehouse] stored record id=" + id
                    + " has invalid values; not announcing it.");
                return;
            }

            var sourceRef = MakeRef(shelf, compartmentIndex, id, slot);
            CoopPlugin.Log.LogInfo("[warehouse] record store observed id=" + id + " shelf=["
                + compartmentIndex + "] slot=" + slot + ".");
            if (_host)
            {
                BroadcastDelta(new WarehouseDeltaMessage
                {
                    IsStore = true,
                    Ref = sourceRef,
                    ItemType = facts.ItemType,
                    Amount = facts.Amount,
                    IsBig = facts.IsBig,
                    Box = null,
                });
                return;
            }

            var snapshot = _currentStoreSnapshot;
            var liveBox = _currentStoreBox;
            SendStore(new WarehouseStoreAction
            {
                Shelf = shelf,
                CompartmentIndex = compartmentIndex,
                Box = liveBox,
                ItemType = facts.ItemType,
                Amount = facts.Amount,
                IsBig = facts.IsBig,
                SourceRef = sourceRef,
                Facts = facts,
                Snapshot = snapshot,
                Origin = this,
            });
        }

        /// <summary>The recorded facts at the top of the compartment, read before the game pops
        /// it. The pop postfix has no record left to read, so the capture happens here.</summary>
        internal WarehouseBoxFacts CapturePoppedRecord(ShelfCompartment compartment)
            => compartment == null ? default : ReadTopRecordFacts(compartment);

        /// <summary>Both peers, records backend: the game popped the last record. The identity
        /// registry follows the pop on every peer, and the host announces a value-only take (the
        /// respawn's EntityAdded owns the new live box).</summary>
        internal void OnStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop,
            WarehouseBoxFacts removed)
        {
            if (!didPop || compartment == null)
            {
                return;
            }

            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return;
            }

            var compartmentIndex = compartment.GetIndex();
            var popped = WarehouseBoxLocations.TryPopStoredTop(shelf, compartmentIndex,
                out var id);
            if (!popped && !_applyingRemote)
            {
                CoopPlugin.Log.LogWarning("[warehouse] popped record on shelf [" + compartmentIndex
                    + "] had no identity location; the next baseline will rebuild it.");
            }

            if (!_applyingRemote && !_hostApplyingCommand && !ReentrancyGuard.ActionApplying)
            {
                CanonicalizeRecords(compartment);
            }

            if (!_host || _hostApplyingCommand || _applyingRemote || !InGame())
            {
                return;
            }

            var facts = removed.IsValid ? removed : ReadTopRecordFacts(compartment);
            if (!facts.IsValid)
            {
                CoopPlugin.Log.LogWarning("[warehouse] popped record on shelf ["
                    + compartmentIndex + "] had no readable values; take not announced.");
                return;
            }

            // The game respawns a worker/restock take immediately. Keep the record's identity so
            // the new live box (and its extended information) remains the same logical box.
            if (id != Guid.Empty)
            {
                _boxes.PushHostCreatedId(id);
            }

            BroadcastDelta(new WarehouseDeltaMessage
            {
                IsStore = false,
                Ref = MakeRef(shelf, compartmentIndex, id, RecordCount(compartment)),
                ItemType = facts.ItemType,
                Amount = facts.Amount,
                IsBig = facts.IsBig,
                Box = null,
            });
        }

        /// <summary>Host: applies a client store. The answer carries the compartment's
        /// post-action state on accept and on reject, so the requester's prediction always merges
        /// onto real state. An accepted store also returns the delta every other peer applies.</summary>
        internal ActionOutcome HostApplyStore(WarehouseStoreAction message,
            out WarehouseDeltaMessage broadcast)
        {
            broadcast = null;
            if (!_host || !Validate(message)
                || !TryGetBoxId(message.Box, out var boxNetworkId, "store"))
            {
                return RefuseStore(message, "the store payload is incomplete");
            }

            var compartment = ResolveCompartment(message.Shelf, message.CompartmentIndex);
            var storeBox = message.Box as InteractablePackagingBox_Item;
            var accepted = false;
            var reason = null as string;
            if (compartment == null)
            {
                reason = "shelf " + (message.Shelf == null ? "<null>" : message.Shelf.name)
                    + " has no compartment " + message.CompartmentIndex;
            }
            else if (storeBox == null)
            {
                reason = "box " + message.Box.name + " is not an item box";
            }
            else if (storeBox.m_ItemCompartment == null)
            {
                reason = "box " + message.Box.name + " has no item compartment";
            }
            else if (storeBox.m_ItemCompartment.GetItemType() != message.ItemType
                     || storeBox.m_ItemCompartment.GetItemCount() != message.Amount
                     || storeBox.m_IsBigBox != message.IsBig)
            {
                reason = "payload disagrees with box " + message.Box.name;
            }
            else if (IsShelvedElsewhere(message.Shelf, message.CompartmentIndex, boxNetworkId,
                         storeBox))
            {
                reason = "box " + message.Box.name + " is already stored in another compartment";
            }
            else if (CanStore(compartment, message.ItemType, message.Amount, message.IsBig))
            {
                try
                {
                    _hostApplyingCommand = true;
                    accepted = ApplyRecordStore(compartment, message.Shelf,
                        message.CompartmentIndex, boxNetworkId, message.ItemType,
                        message.Amount, message.IsBig);
                    if (!accepted)
                    {
                        reason = "the compartment already holds id=" + boxNetworkId
                            + " with different values";
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("Warehouse store failed: " + e);
                    throw;
                }
                finally
                {
                    _hostApplyingCommand = false;
                }
            }
            else
            {
                reason = "shelf " + message.Shelf.name + " compartment "
                    + message.CompartmentIndex + " will not take box " + message.Box.name + " ("
                    + message.ItemType + " x" + message.Amount + ", big=" + message.IsBig + ")";
            }

            if (!accepted)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: " + reason + ".");
                return ActionOutcome.Reject(reason, BuildResult(compartment));
            }

            broadcast = new WarehouseDeltaMessage
            {
                IsStore = true,
                Ref = MakeRef(message.Shelf, message.CompartmentIndex, boxNetworkId,
                    SlotOf(boxNetworkId)),
                ItemType = message.ItemType,
                Amount = message.Amount,
                IsBig = message.IsBig,
                // Records have no live storage object; the take respawn's EntityAdded owns the
                // representation on the observing peer.
                Box = null,
            };
            return ActionOutcome.Applied(BuildResult(compartment));
        }

        private bool ApplyRecordStore(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, Guid id, EItemType itemType, int amount, bool isBig)
        {
            if (HasStoredEntry(shelf, compartmentIndex, id))
            {
                if (TryGetStoredFacts(shelf, compartmentIndex, id, out var existing)
                    && (existing.ItemType != itemType || existing.Amount != amount
                        || existing.IsBig != isBig))
                {
                    CoopPlugin.Log.LogWarning("[warehouse] store for already-stored id=" + id
                        + " disagrees with its record; rejecting the duplicate.");
                    return false;
                }
            }
            else
            {
                WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex,
                    RecordCount(compartment));
                AddRecord(compartment, itemType, amount, isBig);
                WarehouseBoxLocations.AppendStored(id, shelf, compartmentIndex,
                    WarehouseBoxLocations.NextSequence());
                CanonicalizeRecords(compartment);
            }

            // The stored record replaces the live object on this backend. Release any mirrored
            // hold, unbind, and tear the object down through the game's own path.
            if (_boxes.TryGetBox(id, out var live) && live is InteractablePackagingBox_Item item)
            {
                _playerBox?.ReleaseRemoteHoldForBox(item);
                _playerBox?.ReleaseHeldObject(item);
                // The stored record keeps this id; preserve the box's extended information.
                BoxIdentityLifecycle.DetachBox(item);
                _boxes.DestroyWithoutNotification(item);
            }

            return true;
        }

        /// <summary>Host: applies a client take. The source's top is validated against the
        /// descriptor facts; the answer carries the compartment's post-action state on accept and
        /// reject alike, and an accepted take returns the delta every other peer applies plus the
        /// live box whose granted hold must be announced after that delta.</summary>
        internal ActionOutcome HostApplyTake(WarehouseTakeAction message,
            out WarehouseDeltaMessage broadcast, out InteractablePackagingBox grantedHold)
        {
            broadcast = null;
            grantedHold = null;
            if (!_host || message?.Source?.Shelf == null
                || message.CreatedBox is not PackagingBoxEntityDescriptor created
                || created.BoxNetworkId == Guid.Empty
                || created.BoxKind != BoxNetworkKind.Item)
            {
                return RefuseTake(message, "the take payload is incomplete");
            }

            var compartment = ResolveCompartment(message.Source.Shelf,
                message.Source.CompartmentIndex);
            if (compartment == null
                || !TryGetTopEntry(compartment, out var topFacts, out var topId, out var topSlot,
                    out var topLive))
            {
                const string missing = "the source compartment has no readable top entry";
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: " + missing + ".");
                return ActionOutcome.Reject(missing, BuildResult(compartment));
            }

            if (!topFacts.IsValid
                || created.BoxItemType != topFacts.ItemType
                || created.BoxItemCount != topFacts.Amount
                || created.BoxBig != topFacts.IsBig
                || (message.Source.Identity != Guid.Empty && topId != Guid.Empty
                    && message.Source.Identity != topId)
                || (message.Source.Slot >= 0 && message.Source.Slot != topSlot))
            {
                var mismatch = "source top (" + topFacts.ItemType + " x" + topFacts.Amount
                    + ", big=" + topFacts.IsBig + ", id=" + topId + ", slot=" + topSlot
                    + ") disagrees with created box (" + created.BoxItemType + " x"
                    + created.BoxItemCount + ", big=" + created.BoxBig + ", id="
                    + created.BoxNetworkId + ", slot=" + message.Source.Slot + ")";
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: " + mismatch + ".");
                return ActionOutcome.Reject(mismatch, BuildResult(compartment));
            }

            var createdId = created.BoxNetworkId;
            if (!_boxes.PushHostCreatedId(createdId, topLive))
            {
                var stale = "box id=" + createdId + " is not a fresh creator id";
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: " + stale + ".");
                return ActionOutcome.Reject(stale, BuildResult(compartment));
            }

            var accepted = false;
            InteractablePackagingBox_Item spawned = null;
            try
            {
                _hostApplyingCommand = true;
                accepted = TryPopTopEntry(compartment, out var takenFacts);
                if (accepted)
                {
                    spawned = RestockManager.SpawnPackageBoxItem(takenFacts.ItemType,
                        takenFacts.Amount, takenFacts.IsBig);
                    if (spawned == null)
                    {
                        CoopPlugin.Log.LogWarning("[warehouse] host take spawned no live box id="
                            + createdId + "; the take cannot name a live representation.");
                        _boxes.CancelHostCreatedId(createdId);
                    }
                }
            }
            catch (Exception e)
            {
                _boxes.CancelHostCreatedId(createdId);
                CoopPlugin.Log.LogError("Warehouse take failed: " + e);
                throw;
            }
            finally
            {
                _hostApplyingCommand = false;
            }

            if (!accepted)
            {
                _boxes.CancelHostCreatedId(createdId);
                const string failed = "the host could not pop the source top entry";
                return ActionOutcome.Reject(failed, BuildResult(compartment));
            }

            broadcast = new WarehouseDeltaMessage
            {
                IsStore = false,
                Ref = message.Source,
                ItemType = topFacts.ItemType,
                Amount = topFacts.Amount,
                IsBig = topFacts.IsBig,
                Box = spawned,
            };
            grantedHold = spawned;

            return ActionOutcome.Applied(BuildResult(compartment));
        }

        /// <summary>Host: announce a box the take handed to the acting player. Called after the
        /// take delta, so every peer already knows the box before the hold references it.</summary>
        internal void AnnounceGrantedHold(InteractablePackagingBox box, ICoopPeer peer)
            => _playerBox?.AnnounceGrantedHold(box, peer?.Player?.Id ?? 0);

        private bool TryPopTopEntry(ShelfCompartment compartment, out WarehouseBoxFacts facts)
        {
            facts = default;
            var count = RecordCount(compartment);
            if (count == 0 || !TryReadRecord(compartment, count - 1, out facts))
            {
                return false;
            }

            var args = new object[] { null };
            if (!(bool)_recordPop.Invoke(compartment, args))
            {
                return false;
            }

            CanonicalizeRecords(compartment);
            return true;
        }

        internal void ClientApplyState(WarehouseStateMessage message)
        {
            if (message == null)
            {
                throw new InvalidOperationException("Authoritative warehouse state is missing.");
            }

            if (!Available())
            {
                CoopPlugin.Log.LogWarning(
                    "[warehouse] dropped baseline: warehouse storage is unavailable.");
                return;
            }

            if (message.Compartments.Count == 0)
            {
                // The host's full state contains every warehouse compartment it has. An empty
                // list is an authoritative clear, not a no-op.
                ClearAllWarehouseState();
                return;
            }

            // Never apply a partial baseline. If one shelf/compartment is not loaded yet, defer
            // the whole message; applying the rest would risk treating the unresolved ones as
            // empty clears.
            for (var i = 0; i < message.Compartments.Count; i++)
            {
                var entry = message.Compartments[i];
                ResolveDeferredShelf(entry);
                if (entry?.Shelf == null
                    || ResolveCompartment(entry.Shelf, entry.CompartmentIndex) == null)
                {
                    CoopPlugin.Log.LogWarning("[warehouse] deferring authoritative state: shelf "
                        + (entry?.Shelf == null ? "<unresolved>" : entry.Shelf.name)
                        + " compartment " + (entry?.CompartmentIndex ?? -1)
                        + " is not loaded yet.");
                    _deferredStates.Add(message);
                    return;
                }

                if (!ValidateAuthoritativeCompartment(entry))
                {
                    CoopPlugin.Log.LogError("[warehouse] authoritative compartment state for shelf "
                        + entry.Shelf.name + " compartment " + entry.CompartmentIndex
                        + " is malformed; refusing the whole state.");
                    return;
                }
            }

            _applyingRemote = true;
            try
            {
                for (var i = 0; i < message.Compartments.Count; i++)
                {
                    ApplyAuthoritativeCompartment(message.Compartments[i]);
                }
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void ApplyAuthoritativeCompartment(WarehouseCompartmentState entry)
        {
            var compartment = ResolveCompartment(entry.Shelf, entry.CompartmentIndex);
            if (compartment == null)
            {
                return;
            }
            entry.Boxes ??= new List<WarehouseBoxEntry>();

            var keepIds = IncomingBoxIds(entry.Boxes);
            ClearLiveBoxes(compartment, null, keepIds);
            ClearStoredRecords(compartment, keepIds);
            var ids = new List<Guid>(entry.Boxes.Count);
            for (var i = 0; i < entry.Boxes.Count; i++)
            {
                var box = entry.Boxes[i];
                if (box == null || box.Amount <= 0)
                {
                    continue;
                }

                // ValidateAuthoritativeCompartment already guaranteed a unique, non-empty id.
                var id = box.Ref.Identity;
                AddRecord(compartment, entry.ItemType, box.Amount, entry.IsBig);
                ids.Add(id);
            }

            WarehouseBoxLocations.SetCompartmentOrder(entry.Shelf, entry.CompartmentIndex,
                ids, true);
            CanonicalizeRecords(compartment);
            if (ids.Count == 0)
            {
                // The authoritative header is the whole truth for an empty compartment,
                // including None; keep an old item type/price tag from surviving a clear.
                compartment.SetCompartmentBoxType(entry.IsBig);
                compartment.SetCompartmentItemType(entry.ItemType);
            }
        }

        /// <summary>Client: applies the host's post-action state for one compartment. This is the
        /// merge point of a warehouse action: the response state is authoritative on accept and
        /// reject alike, and the action's own prediction is already settled.</summary>
        private void ApplyCompartmentState(WarehouseCompartmentState entry)
        {
            if (entry == null)
            {
                return;
            }

            ResolveDeferredShelf(entry);
            if (entry.Shelf == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] dropped action state: its shelf is not "
                    + "loaded yet.");
                return;
            }

            _applyingRemote = true;
            try
            {
                ApplyAuthoritativeCompartment(entry);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        /// <summary>An authoritative empty state means the host has no warehouse contents. Clear
        /// every local compartment instead of leaving stale boxes behind.</summary>
        private void ClearAllWarehouseState()
        {
            var compartments = GetWarehouseCompartments();
            _applyingRemote = true;
            try
            {
                for (var i = 0; i < compartments.Count; i++)
                {
                    var compartment = compartments[i];
                    var shelf = compartment.GetWarehouseShelf();
                    if (shelf == null)
                    {
                        continue;
                    }

                    // No keep set: this is a permanent clear, so the live-object information goes
                    // with the boxes.
                    ClearLiveBoxes(compartment);
                    ClearStoredRecords(compartment);

                    WarehouseBoxLocations.SetCompartmentOrder(shelf, compartment.GetIndex(),
                        new List<Guid>(), true);
                    compartment.SetCompartmentBoxType(false);
                    compartment.SetCompartmentItemType(EItemType.None);
                }
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        /// <summary>Host state is trusted, but it must be internally addressable: a compartment
        /// with boxes needs a header, and every entry needs a unique non-empty identity. Refuse a
        /// malformed state instead of inventing a Guid the host has never seen.</summary>
        private static bool ValidateAuthoritativeCompartment(WarehouseCompartmentState entry)
        {
            if (entry == null)
            {
                return false;
            }
            if (entry.Boxes == null)
            {
                return true;
            }
            if (entry.ItemType == EItemType.None && entry.Boxes.Count > 0)
            {
                CoopPlugin.Log.LogError("[warehouse] authoritative header has no ItemType for "
                    + "shelf " + (entry.Shelf == null ? "<unresolved>" : entry.Shelf.name)
                    + " compartment " + entry.CompartmentIndex + ".");
                return false;
            }

            var ids = new HashSet<Guid>();
            for (var i = 0; i < entry.Boxes.Count; i++)
            {
                var box = entry.Boxes[i];
                if (box == null || box.Amount <= 0)
                {
                    continue;
                }

                var id = box.Ref?.Identity ?? Guid.Empty;
                if (id == Guid.Empty || !ids.Add(id))
                {
                    CoopPlugin.Log.LogError("[warehouse] authoritative entry "
                        + (id == Guid.Empty ? "has no identity" : "repeats identity " + id)
                        + " on shelf " + (entry.Shelf == null ? "<unresolved>" : entry.Shelf.name)
                        + " compartment " + entry.CompartmentIndex + ".");
                    return false;
                }
            }

            return true;
        }

        internal void ClientApplyDelta(WarehouseDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            ResolveDeferredShelf(message.Ref);
            if (message.Ref?.Shelf == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] deferring delta: its shelf is not loaded.");
                _deferredDeltas.Add(message);
                return;
            }

            var compartment = ResolveCompartment(message.Ref.Shelf, message.Ref.CompartmentIndex);
            if (compartment == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] deferring delta: shelf "
                    + message.Ref.Shelf.name + " has no compartment "
                    + message.Ref.CompartmentIndex + ".");
                _deferredDeltas.Add(message);
                return;
            }

            var id = message.Ref.Identity;
            if (id == Guid.Empty && message.Box != null
                && _boxes.TryGetId(message.Box, out var boxId))
            {
                id = boxId;
            }

            _applyingRemote = true;
            try
            {
                if (message.IsStore)
                {
                    ApplyStoreValues(message.Ref.Shelf, message.Ref.CompartmentIndex, id,
                        message.ItemType, message.Amount, message.IsBig);
                }
                else
                {
                    ApplyTakeValues(compartment, message.Ref.Shelf, message.Ref.CompartmentIndex,
                        id, message.Box, message.ItemType, message.Amount, message.IsBig);
                }
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private bool ApplyStoreValues(WarehouseShelf shelf, int compartmentIndex, Guid id,
            EItemType itemType, int amount, bool isBig)
        {
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null || id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] store delta had no resolvable compartment or "
                    + "identity.");
                return false;
            }

            if (!HasStoredEntry(shelf, compartmentIndex, id))
            {
                AddRecord(compartment, itemType, amount, isBig);
                WarehouseBoxLocations.AppendStored(id, shelf, compartmentIndex,
                    WarehouseBoxLocations.NextSequence());
                CanonicalizeRecords(compartment);
            }

            // The authoritative store replaces any live replica on this backend.
            if (_boxes.TryGetBox(id, out var replica)
                && replica is InteractablePackagingBox_Item item)
            {
                _playerBox?.DetachMirroredHold(item, id);
                _playerBox?.ReleaseHeldObject(item);
                if (!_host)
                {
                    _boxes.ClientConvertToStoredRecord(id);
                }
                else
                {
                    // The stored record keeps this id; preserve the box's extended
                    // information across the representation change.
                    BoxIdentityLifecycle.DetachBox(item);
                    _boxes.DestroyWithoutNotification(item);
                }
            }

            return true;
        }

        private void ApplyTakeValues(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, Guid id, InteractablePackagingBox heldBox,
            EItemType itemType, int amount, bool isBig)
        {
            var count = RecordCount(compartment);
            if (count == 0)
            {
                // The wire converter registers an unknown store identity at its wire location
                // before the handler adds the record. A take can therefore arrive while the
                // registry knows the id but the game record list is empty (the store delta never
                // added it). Treat the take as authoritative: materialize the removed live box
                // from the delta facts instead of leaving a registry-only ghost.
                if (id != Guid.Empty && IsValidBox(itemType, amount)
                    && !_boxes.TryGetBox(id, out _))
                {
                    WarehouseBoxLocations.ClearLocation(id);
                    var removed = _boxes.MaterializeWarehouseStored(itemType, amount, isBig);
                    if (removed != null)
                    {
                        // This is the peer's copy of a box the host has just taken. Bind it as a
                        // stored-live identity (physics off, stale binding replaced), not as a
                        // free physics box, while leaving m_IsStored false so the host's
                        // stored-hold guard still accepts the follow-up pickup.
                        _boxes.BindStoredLiveBox(id, removed);
                        removed.m_IsStored = false;
                        if (heldBox != null)
                        {
                            BoxNetworkInteraction.ApplyPose(removed,
                                heldBox.transform.position, heldBox.transform.rotation);
                        }

                        CoopPlugin.Log.LogInfo("[warehouse] materialized registry-only take id="
                            + id + " type=" + itemType + " amount=" + amount + ".");
                    }
                }

                return;
            }

            var topId = GetStoredId(shelf, compartmentIndex, count - 1);
            if (id != Guid.Empty && topId != Guid.Empty && id != topId)
            {
                // A take is always a top pop. A different id means this peer's view was stale;
                // the authoritative top still leaves, so log and keep top semantics.
                CoopPlugin.Log.LogWarning("[warehouse] take delta named id=" + id
                    + " but the top entry is id=" + topId + "; popping the authoritative top.");
            }

            var args = new object[] { null };
            if (!(bool)_recordPop.Invoke(compartment, args))
            {
                return;
            }

            CanonicalizeRecords(compartment);

            // The host's take respawns a live box and announces it as EntityAdded. Materialize a
            // local replica now and bind the same id so a following pickup/throw/worker snapshot
            // can resolve it; the EntityAdded then adopts this object instead of duplicating it.
            if (id != Guid.Empty && !_boxes.TryGetBox(id, out _)
                && IsValidBox(itemType, amount))
            {
                var replica = _boxes.MaterializeWarehouseStored(itemType, amount, isBig);
                if (replica != null)
                {
                    _boxes.BindStoredLiveBox(id, replica);
                    replica.m_IsStored = false;
                    WarehouseBoxLocations.ClearLocation(id);
                    if (heldBox != null)
                    {
                        BoxNetworkInteraction.ApplyPose(replica, heldBox.transform.position,
                            heldBox.transform.rotation);
                    }

                    CoopPlugin.Log.LogInfo("[warehouse] materialized live take id=" + id
                        + " type=" + itemType + " amount=" + amount + ".");
                }
            }
        }

        private void RemoveStoredRecordById(WarehouseShelf shelf, int compartmentIndex,
            ShelfCompartment compartment, Guid id, Guid keepId = default(Guid))
        {
            var count = RecordCount(compartment);
            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex, count);
            var index = ids.IndexOf(id);
            if (index < 0)
            {
                return;
            }

            var entries = CaptureStoredEntries(compartment, shelf, compartmentIndex, ids);
            entries.RemoveAt(index);
            RebuildStoredRecords(compartment, shelf, compartmentIndex, entries, keepId);
        }

        /// <summary>The entry identities owned by store predictions that have not settled yet.
        /// Authoritative compartment state may legitimately name fewer entries than the local
        /// prediction holds; those identities must survive the clear so the box's extended
        /// information is not retired under a still-pending action.</summary>
        private HashSet<Guid> PendingStoreIds()
        {
            var ids = new HashSet<Guid>();
            foreach (var pair in _pendingStores)
            {
                if (pair.Value != Guid.Empty)
                {
                    ids.Add(pair.Value);
                }
            }

            return ids;
        }

        /// <summary>Client: sends one store prediction through the single send seam. The local game
        /// path already appended the record, so the action only carries what an undo needs.</summary>
        private void SendStore(WarehouseStoreAction action)
        {
            var payload = action.Box;
            var temporary = false;
            if (payload == null)
            {
                // The game destroys a record-store's live box immediately. Materialize a
                // short-lived payload solely to name the identity on the wire; the host resolves it
                // to its own replica of the same box.
                payload = _boxes.MaterializeWarehouseStored(action.Facts.ItemType,
                    action.Facts.Amount, action.Facts.IsBig);
                if (payload != null && action.SourceRef.Identity != Guid.Empty)
                {
                    BoxIdentityLifecycle.BindClientBox(action.SourceRef.Identity, payload);
                    temporary = true;
                }
            }

            if (payload == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] store box id=" + action.SourceRef.Identity
                    + " has no live payload; the intent is not sent.");
                return;
            }

            action.Box = payload;
            try
            {
                // The client send seam ignores the peer and routes the action to the host.
                _send(null, action);
            }
            finally
            {
                if (temporary)
                {
                    BoxIdentityLifecycle.ForgetBox(payload);
                    _boxes.DestroyWithoutNotification(payload);
                    action.Box = null;
                }
            }

            if (action.ActionId != Guid.Empty && action.SourceRef.Identity != Guid.Empty)
            {
                // The framework assigned the action id while sending; only now can the artifact be
                // tracked by the id the host will answer.
                _pendingStores[action.ActionId] = action.SourceRef.Identity;
            }
        }

        /// <summary>Client: sends one take prediction. The game's own click path already popped
        /// the record and moved the box into the hand; the action keeps the exact box it owns for a
        /// faithful undo.</summary>
        private void SendTake(WarehouseTakeAction action)
        {
            // The client send seam ignores the peer and routes the action to the host.
            _send(null, action);
        }

        /// <summary>Client: reverses a rejected store prediction. The record the game appended goes
        /// back out of the compartment and the box returns to the hand; the authoritative
        /// compartment state follows in the merge.</summary>
        internal void UndoStore(WarehouseStoreAction action)
        {
            var sourceRef = action?.SourceRef;
            if (sourceRef?.Shelf == null)
            {
                return;
            }

            var shelf = sourceRef.Shelf;
            var compartmentIndex = sourceRef.CompartmentIndex;
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null)
            {
                return;
            }

            if (action.Snapshot != null)
            {
                RebuildFromSnapshot(compartment, action.Snapshot, sourceRef.Identity);
            }
            else
            {
                RemoveStoredRecordById(shelf, compartmentIndex, compartment,
                    sourceRef.Identity, sourceRef.Identity);
            }

            if (!_boxes.ClientEnsureWarehouseTake(sourceRef.Identity, action.Facts.ItemType,
                    action.Facts.Amount, action.Facts.IsBig, out var held))
            {
                CoopPlugin.Log.LogWarning("[warehouse] store rollback could not restore box id="
                    + sourceRef.Identity + " into the hand.");
                return;
            }

            BoxIdentityLifecycle.BindClientBox(sourceRef.Identity, held);
            TakeIntoLocalHand(held);
        }

        /// <summary>Client: settles a store prediction on the host's compartment state and releases
        /// the local artifact the action owned.</summary>
        internal void MergeStore(WarehouseStoreAction action, WarehouseShelfResult response)
        {
            if (action == null)
            {
                return;
            }

            _pendingStores.Remove(action.ActionId);
            action.Origin = null;
            action.Snapshot = null;
            action.Box = null;
            action.SourceRef = null;
            if (response?.Compartment == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] store action " + action.ActionId
                    + " settled without authoritative compartment state.");
                return;
            }

            ApplyCompartmentState(response.Compartment);
        }

        /// <summary>Client: reverses a rejected take prediction. The record returns to the top of
        /// the source compartment and the box the prediction put in the hand is retired.</summary>
        internal void UndoTake(WarehouseTakeAction action)
        {
            var sourceRef = action?.Source;
            if (sourceRef?.Shelf == null)
            {
                return;
            }

            var shelf = sourceRef.Shelf;
            var compartmentIndex = sourceRef.CompartmentIndex;
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null)
            {
                return;
            }

            if (HasStoredEntry(shelf, compartmentIndex, sourceRef.Identity))
            {
                return;
            }

            var createdId = action.CreatedBox is PackagingBoxEntityDescriptor descriptor
                ? descriptor.BoxNetworkId : Guid.Empty;
            var held = action.HeldBox;
            if (held != null && _boxes.TryGetId(held, out var heldId) && heldId == createdId
                && !BoxNetworkInteraction.IsStoredBox(held))
            {
                _playerBox?.ReleaseHeldObject(held);
                // The restored record keeps the id; preserve the logical box's information.
                BoxIdentityLifecycle.DetachBox(held);
                _boxes.DestroyWithoutNotification(held);
            }

            AddRecord(compartment, action.Facts.ItemType, action.Facts.Amount, action.Facts.IsBig);
            // The game appended the restored record. Give any pre-existing records their ids
            // first, then align the restored id with the append before the canonical sort.
            WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex,
                Math.Max(0, RecordCount(compartment) - 1));
            var ids = new List<Guid>();
            WarehouseBoxLocations.CopyCompartmentOrder(shelf, compartmentIndex, ids);
            ids.Add(sourceRef.Identity);
            WarehouseBoxLocations.SetCompartmentOrder(shelf, compartmentIndex, ids, false);
            CanonicalizeRecords(compartment);
        }

        /// <summary>Client: settles a take prediction on the host's compartment state and releases
        /// the local artifact the action owned. The taken box in the hand is not part of the
        /// compartment state, so it survives the merge.</summary>
        internal void MergeTake(WarehouseTakeAction action, WarehouseShelfResult response)
        {
            if (action == null)
            {
                return;
            }

            action.Origin = null;
            action.HeldBox = null;
            action.Source = null;
            if (response?.Compartment == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] take action " + action.ActionId
                    + " settled without authoritative compartment state.");
                return;
            }

            ApplyCompartmentState(response.Compartment);
        }

        private void RebuildFromSnapshot(ShelfCompartment compartment,
            WarehouseCompartmentState snapshot, Guid keepId = default(Guid))
        {
            var shelf = snapshot.Shelf ?? compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return;
            }

            var keep = IncomingBoxIds(snapshot.Boxes);
            if (keepId != Guid.Empty)
            {
                // A store rollback returns this id to the hand; the record was only a temporary
                // representation, so its extended information must survive the record teardown.
                keep.Add(keepId);
            }
            ClearStoredRecords(compartment, keep);
            var ids = new List<Guid>(snapshot.Boxes.Count);
            for (var i = 0; i < snapshot.Boxes.Count; i++)
            {
                var entry = snapshot.Boxes[i];
                if (entry == null || entry.Amount <= 0)
                {
                    continue;
                }

                var id = entry.Ref?.Identity ?? Guid.Empty;
                if (id == Guid.Empty)
                {
                    continue;
                }

                AddRecord(compartment, snapshot.ItemType, entry.Amount, snapshot.IsBig);
                ids.Add(id);
            }

            WarehouseBoxLocations.SetCompartmentOrder(shelf, snapshot.CompartmentIndex, ids, false);
            CanonicalizeRecords(compartment);
        }

        private WarehouseCompartmentState CaptureCompartmentState(ShelfCompartment compartment)
        {
            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return null;
            }

            var state = new WarehouseCompartmentState
            {
                Shelf = shelf,
                CompartmentIndex = compartment.GetIndex(),
            };
            TryGetHeader(compartment, out state.ItemType, out state.IsBig);
            var count = RecordCount(compartment);
            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf,
                state.CompartmentIndex, count);
            for (var i = 0; i < count; i++)
            {
                if (!TryReadRecord(compartment, i, out var facts))
                {
                    continue;
                }

                state.Boxes.Add(new WarehouseBoxEntry
                {
                    Ref = MakeRef(shelf, state.CompartmentIndex, ids[i], i),
                    Amount = facts.Amount,
                });
            }

            return state;
        }

        private WarehouseCompartmentState BuildCompartmentState(ShelfCompartment compartment)
        {
            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return null;
            }

            var state = new WarehouseCompartmentState
            {
                Shelf = shelf,
                CompartmentIndex = compartment.GetIndex(),
            };
            // The baseline must publish the same order the peer will canonicalize to.
            CanonicalizeRecords(compartment);
            TryGetHeader(compartment, out state.ItemType, out state.IsBig);

            var count = RecordCount(compartment);
            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf,
                state.CompartmentIndex, count);
            for (var i = 0; i < count; i++)
            {
                if (!TryReadRecord(compartment, i, out var facts))
                {
                    continue;
                }

                var id = ids[i];
                var live = _boxes.TryGetBox(id, out var liveBox) ? liveBox : null;
                state.Boxes.Add(new WarehouseBoxEntry
                {
                    Ref = MakeRef(shelf, state.CompartmentIndex, id, i),
                    Amount = facts.Amount,
                    Box = live,
                });
            }

            return state;
        }

        private WarehouseStateMessage BuildFullState()
        {
            var message = new WarehouseStateMessage();
            var compartments = GetWarehouseCompartments();
            for (var i = 0; i < compartments.Count; i++)
            {
                var state = BuildCompartmentState(compartments[i]);
                if (state != null)
                {
                    message.Compartments.Add(state);
                }
            }

            return message;
        }

        /// <summary>Builds the answer a warehouse action carries back to its actor: the compartment's
        /// authoritative post-action state, or a state-less answer when the compartment cannot be
        /// resolved on this peer.</summary>
        private WarehouseShelfResult BuildResult(ShelfCompartment compartment)
            => new()
            {
                Compartment = compartment == null ? null : BuildCompartmentState(compartment),
            };

        /// <summary>Refuses a store before host execution. The answer still carries the host's
        /// current compartment state so the client's merge lands on reality.</summary>
        internal ActionOutcome RefuseStore(WarehouseStoreAction action, string reason)
        {
            CoopPlugin.Log.LogWarning("[warehouse] store refused: " + reason + ".");
            var compartment = action?.Shelf == null
                ? null : ResolveCompartment(action.Shelf, action.CompartmentIndex);
            return ActionOutcome.Reject(reason, BuildResult(compartment));
        }

        /// <summary>Refuses a take before host execution. The answer still carries the host's
        /// current compartment state so the client's merge lands on reality.</summary>
        internal ActionOutcome RefuseTake(WarehouseTakeAction action, string reason)
        {
            CoopPlugin.Log.LogWarning("[warehouse] take refused: " + reason + ".");
            var compartment = action?.Source?.Shelf == null
                ? null : ResolveCompartment(action.Source.Shelf, action.Source.CompartmentIndex);
            return ActionOutcome.Reject(reason, BuildResult(compartment));
        }

        private void BroadcastDelta(WarehouseDeltaMessage message)
        {
            if (!_host || message == null)
            {
                return;
            }

            _broadcast(message);
        }

        private bool TryGetTopEntry(ShelfCompartment compartment, out WarehouseBoxFacts facts,
            out Guid id, out int slot, out InteractablePackagingBox live)
        {
            facts = default;
            id = Guid.Empty;
            slot = -1;
            live = null;
            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return false;
            }

            var count = RecordCount(compartment);
            if (count == 0 || !TryReadRecord(compartment, count - 1, out facts))
            {
                return false;
            }

            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf,
                compartment.GetIndex(), count);
            slot = count - 1;
            id = ids[slot];
            live = _boxes.TryGetBox(id, out var recordBox) ? recordBox : null;
            return true;
        }

        private bool TryGetHeader(ShelfCompartment compartment, out EItemType itemType,
            out bool isBig)
        {
            itemType = compartment.GetItemType();
            var count = RecordCount(compartment);
            for (var i = 0; i < count; i++)
            {
                if (!TryReadRecord(compartment, i, out var first))
                {
                    continue;
                }

                itemType = first.ItemType;
                isBig = first.IsBig;
                return true;
            }

            isBig = ReflectCompartmentIsBig(compartment);
            return true;
        }

        private static bool ReflectCompartmentIsBig(ShelfCompartment compartment)
        {
            if (_compartmentBig?.GetValue(compartment) is bool isBig)
            {
                return isBig;
            }

            CoopPlugin.Log.LogWarning("[warehouse] could not read the compartment IsBig field for "
                + (compartment == null ? "<null>" : compartment.name)
                + "; defaulting to false. Box size is never inferred from ItemType.");
            return false;
        }

        /// <summary>Records backend: rebuild the compartment list in the canonical order (amount
        /// descending, newer insertion first on ties) and publish the matching location slots.</summary>
        private void CanonicalizeRecords(ShelfCompartment compartment, bool immediate = true)
        {
            if (compartment == null)
            {
                return;
            }

            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return;
            }

            var compartmentIndex = compartment.GetIndex();
            var count = RecordCount(compartment);
            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex, count);
            if (count <= 1)
            {
                RebuildRecordVisuals(compartment, immediate);
                return;
            }

            var list = _recordList?.GetValue(compartment) as IList;
            if (list == null || list.Count != count)
            {
                CoopPlugin.Log.LogError("[warehouse] cannot canonicalize records on shelf ["
                    + compartmentIndex + "]: game list=" + (list?.Count ?? -1) + " count=" + count
                    + ".");
                return;
            }

            var pairs = new List<RecordPair>(count);
            for (var i = 0; i < count; i++)
            {
                var record = list[i];
                if (record == null)
                {
                    continue;
                }

                var amount = (int)_recordAmount.GetValue(record);
                var sequence = WarehouseBoxLocations.SequenceOf(ids[i]);
                if (sequence == 0)
                {
                    sequence = i;
                }

                pairs.Add(new RecordPair(record, ids[i], amount, sequence));
            }

            pairs.Sort((left, right) =>
            {
                var byAmount = right.Amount.CompareTo(left.Amount);
                return byAmount != 0 ? byAmount : right.Sequence.CompareTo(left.Sequence);
            });

            var reordered = new List<Guid>(pairs.Count);
            for (var i = 0; i < pairs.Count; i++)
            {
                list[i] = pairs[i].Record;
                reordered.Add(pairs[i].Id);
            }

            WarehouseBoxLocations.SetCompartmentOrder(shelf, compartmentIndex, reordered, false);
            RebuildRecordVisuals(compartment, immediate);
        }

        /// <summary>A warehouse shelf's stored boxes are drawn by the game's batcher from the
        /// compartment slot matrices, not from children that follow SetActive. Boxing the shelf
        /// must remove those matrices or the stored boxes keep rendering in mid-air.</summary>
        internal static void SetStoredBoxVisuals(InteractableObject obj, bool visible)
        {
            if (obj is not WarehouseShelf shelf)
            {
                return;
            }

            var storage = shelf.GetStorageCompartmentList();
            for (var i = 0; storage != null && i < storage.Count; i++)
            {
                var compartment = storage[i]?.GetShelfCompartment();
                if (compartment == null)
                {
                    continue;
                }

                if (visible)
                {
                    RebuildRecordVisuals(compartment, true);
                }
                else
                {
                    HideRecordVisuals(compartment);
                }

                // Waiting boxes are live children of the compartment, not records in the batcher.
                // Vanilla deactivates them with the furniture; this peer's copy is not parented,
                // so it must be toggled explicitly or it keeps rendering in mid-air.
                var waiting = new List<InteractablePackagingBox_Item>();
                AddWaitingBoxes(compartment, waiting);
                for (var w = 0; w < waiting.Count; w++)
                {
                    if (waiting[w] != null)
                    {
                        waiting[w].gameObject.SetActive(visible);
                    }
                }
            }
        }

        private static void HideRecordVisuals(ShelfCompartment compartment)
        {
            if (_recordHide == null || compartment == null)
            {
                return;
            }

            object target = null;
            if (!_recordHide.IsStatic)
            {
                target = _batcherInstance?.GetValue(null);
                if (target == null)
                {
                    return;
                }
            }

            _recordHide.Invoke(target, new object[] { compartment });
        }

        private static void RebuildRecordVisuals(ShelfCompartment compartment, bool immediate)
        {
            // The batcher's methods are static on the supported baselines, but a build may expose
            // them as instance members. Invoking a non-static method with a null target throws
            // TargetException, so pick the target from the signature.
            var method = immediate && _recordRebuild != null ? _recordRebuild : _recordMarkDirty;
            if (method == null)
            {
                return;
            }

            object target = null;
            if (!method.IsStatic)
            {
                target = _batcherInstance?.GetValue(null);
                if (target == null)
                {
                    return;
                }
            }

            method.Invoke(target, new object[] { compartment });
        }

        private readonly struct RecordPair
        {
            internal readonly object Record;
            internal readonly Guid Id;
            internal readonly int Amount;
            internal readonly long Sequence;

            internal RecordPair(object record, Guid id, int amount, long sequence)
            {
                Record = record;
                Id = id;
                Amount = amount;
                Sequence = sequence;
            }
        }

        private void ClearStoredRecords(ShelfCompartment compartment,
            ICollection<Guid> keepIds = null)
        {
            var shelf = compartment.GetWarehouseShelf();
            var compartmentIndex = compartment.GetIndex();
            var count = RecordCount(compartment);
            var ids = shelf != null && count > 0
                ? WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex, count)
                : null;

            if (_pendingStores.Count > 0)
            {
                // A pending store's record may be absent from this (older) authoritative state;
                // its extended information still belongs to the box the prediction owns.
                var protectedIds = PendingStoreIds();
                if (keepIds == null)
                {
                    keepIds = protectedIds;
                }
                else
                {
                    foreach (var id in protectedIds)
                    {
                        keepIds.Add(id);
                    }
                }
            }

            while (RecordCount(compartment) > 0)
            {
                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                {
                    throw new InvalidOperationException(
                        "Warehouse record storage could not be cleared.");
                }
            }

            // A stored record has no live object to unbind, so its extended information must be
            // retired here when the record is permanently gone. keepIds covers the records the
            // incoming authoritative state still names (a representation transition).
            if (ids == null)
            {
                return;
            }
            // The location table can retain more order entries than this compartment currently
            // holds; only the first count slots belong to the records just popped.
            var captured = Math.Min(count, ids.Count);
            for (var i = 0; i < captured; i++)
            {
                var id = ids[i];
                if (id == Guid.Empty || (keepIds != null && keepIds.Contains(id)))
                {
                    continue;
                }

                // A live representation may exist outside this compartment (the box is in a
                // player's hand or a worker is carrying it). That is the same logical box, not a
                // permanent removal, so keep its information.
                if (GameObjectIdentityApi.TryResolve(GameObjectId.Box(id), out _))
                {
                    continue;
                }

                ExtendedInformationModule.OnEntityRemoved(GameObjectId.Box(id),
                    EntityRemovalMode.Destroy);
            }
        }

        private void RebuildStoredRecords(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, List<WarehouseBoxEntry> entries, Guid keepId = default(Guid))
        {
            TryGetHeader(compartment, out var itemType, out var isBig);
            var keep = IncomingBoxIds(entries);
            if (keepId != Guid.Empty)
            {
                keep.Add(keepId);
            }
            ClearStoredRecords(compartment, keep);
            var ids = new List<Guid>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.Amount <= 0)
                {
                    continue;
                }

                var id = entry.Ref?.Identity ?? Guid.Empty;
                if (id == Guid.Empty)
                {
                    continue;
                }

                AddRecord(compartment, itemType, entry.Amount, isBig);
                ids.Add(id);
            }

            WarehouseBoxLocations.SetCompartmentOrder(shelf, compartmentIndex, ids, false);
            CanonicalizeRecords(compartment);
        }

        private List<WarehouseBoxEntry> CaptureStoredEntries(ShelfCompartment compartment,
            WarehouseShelf shelf, int compartmentIndex, List<Guid> ids)
        {
            var entries = new List<WarehouseBoxEntry>(ids.Count);
            for (var i = 0; i < ids.Count; i++)
            {
                if (!TryReadRecord(compartment, i, out var facts))
                {
                    continue;
                }

                entries.Add(new WarehouseBoxEntry
                {
                    Ref = MakeRef(shelf, compartmentIndex, ids[i], i),
                    Amount = facts.Amount,
                });
            }

            return entries;
        }

        /// <summary>The ids an authoritative compartment state names. A live box with one of
        /// these ids is being replaced by a record (or a different live representation), so its
        /// identity and extended information must survive the teardown.</summary>
        private static HashSet<Guid> IncomingBoxIds(IReadOnlyList<WarehouseBoxEntry> boxes)
        {
            var ids = new HashSet<Guid>();
            if (boxes == null)
            {
                return ids;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                var id = boxes[i]?.Ref?.Identity ?? Guid.Empty;
                if (id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// 1.03 keeps a box that was taken from a stored record as a live "waiting box" inside its
        /// compartment: <c>m_IsStored</c> is true, it is parented to the compartment, and it is
        /// deliberately NOT added to <c>m_InteractablePackagingBoxList</c>. Every sweep that only
        /// walks that list therefore misses it. This is the one resolver for those boxes.
        /// </summary>
        private static void AddWaitingBoxes(ShelfCompartment compartment,
            List<InteractablePackagingBox_Item> into)
        {
            if (compartment == null || into == null)
            {
                return;
            }

            var all = RestockManager.GetItemPackagingBoxList();
            for (var i = 0; all != null && i < all.Count; i++)
            {
                var box = all[i];
                if (box == null || !box.m_IsStored
                    || !ReferenceEquals(box.GetBoxStoredCompartment(), compartment))
                {
                    continue;
                }

                if (!into.Contains(box))
                {
                    into.Add(box);
                }
            }
        }

        private void ClearLiveBoxes(ShelfCompartment compartment,
            IReadOnlyDictionary<Guid, InteractablePackagingBox_Item> reuse = null,
            ICollection<Guid> keepIds = null)
        {
            var listed = compartment.GetInteractablePackagingBoxList();
            if (listed == null)
            {
                return;
            }

            var copy = new List<InteractablePackagingBox_Item>(listed);
            AddWaitingBoxes(compartment, copy);
            for (var i = 0; i < copy.Count; i++)
            {
                var box = copy[i];
                if (box == null)
                {
                    continue;
                }

                var id = _boxes.TryGetId(box, out var known) ? known : Guid.Empty;
                if (id != Guid.Empty)
                {
                    _playerBox?.DetachMirroredHold(box, id);
                    _playerBox?.ReleaseHeldObject(box);
                    WarehouseBoxLocations.ClearLocation(id);
                }

                if (listed.Contains(box))
                {
                    compartment.RemoveBox(box);
                }

                if (reuse != null && id != Guid.Empty
                    && reuse.TryGetValue(id, out var kept) && ReferenceEquals(kept, box))
                {
                    // The authoritative state names this exact object; keep it for the rebuild
                    // instead of destroying and materializing a replacement.
                    continue;
                }

                if (keepIds != null && id != Guid.Empty && keepIds.Contains(id))
                {
                    // The state represents the same logical box (usually as a stored record).
                    // Drop only the live object; the id and its extended information survive.
                    if (_host)
                    {
                        BoxIdentityLifecycle.DetachBox(box);
                        _boxes.DestroyWithoutNotification(box);
                    }
                    else
                    {
                        _boxes.ClientDetachPhysical(id, box);
                    }
                    continue;
                }

                if (_host || id == Guid.Empty)
                {
                    BoxIdentityLifecycle.ForgetBox(box);
                    _boxes.DestroyWithoutNotification(box);
                }
                else
                {
                    _boxes.ClientForgetPhysical(id, box);
                }
            }
        }

        private bool HasStoredEntry(WarehouseShelf shelf, int compartmentIndex, Guid id)
        {
            if (id == Guid.Empty)
            {
                return false;
            }

            // A location entry is not proof that the game record exists: the wire converter
            // pre-registers unknown store ids. The record list is the authoritative store
            // membership on this backend.
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null)
            {
                return false;
            }

            var count = RecordCount(compartment);
            if (count == 0)
            {
                return false;
            }

            var records = WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex,
                count);
            return records.Contains(id);
        }

        private bool IsShelvedElsewhere(WarehouseShelf shelf, int compartmentIndex, Guid id,
            InteractablePackagingBox_Item box)
        {
            if (WarehouseBoxLocations.TryGetLocation(id, out var location)
                && location.HasStoredLocation
                && (!ReferenceEquals(location.Shelf, shelf)
                    || location.CompartmentIndex != compartmentIndex))
            {
                return true;
            }

            if (box.m_IsStored)
            {
                var target = ResolveCompartment(shelf, compartmentIndex);
                var targetList = target?.GetInteractablePackagingBoxList();
                return targetList == null || !targetList.Contains(box);
            }

            return false;
        }

        private int SlotOf(Guid id)
        {
            return WarehouseBoxLocations.TryGetLocation(id, out var location) ? location.Slot : -1;
        }

        private Guid GetStoredId(WarehouseShelf shelf, int compartmentIndex, int slot)
        {
            if (WarehouseBoxLocations.TryGetId(shelf, compartmentIndex, slot, out var id))
            {
                return id;
            }

            var count = 0;
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment != null)
            {
                count = RecordCount(compartment);
            }

            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex, count);
            return slot >= 0 && slot < ids.Count ? ids[slot] : Guid.Empty;
        }

        private bool HasStoredFacts(ShelfCompartment compartment, Guid id,
            out WarehouseBoxFacts facts)
        {
            facts = default;
            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return false;
            }

            var count = RecordCount(compartment);
            var ids = WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartment.GetIndex(),
                count);
            var index = ids.IndexOf(id);
            return index >= 0 && index < count && TryReadRecord(compartment, index, out facts);
        }

        private bool ReadStoredFacts(ShelfCompartment compartment, Guid id,
            out WarehouseBoxFacts facts)
            => HasStoredFacts(compartment, id, out facts);

        private bool TryGetStoredFacts(WarehouseShelf shelf, int compartmentIndex, Guid id,
            out WarehouseBoxFacts facts)
        {
            facts = default;
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            return compartment != null && HasStoredFacts(compartment, id, out facts);
        }

        private WarehouseBoxFacts DescribeLiveBox(InteractablePackagingBox box)
        {
            if (box is not InteractablePackagingBox_Item item || item.m_ItemCompartment == null)
            {
                return default;
            }

            return new WarehouseBoxFacts(item.m_ItemCompartment.GetItemType(),
                item.m_ItemCompartment.GetItemCount(), item.m_IsBigBox);
        }

        private static WarehouseBoxFacts ReadTopRecordFacts(ShelfCompartment compartment)
        {
            var count = RecordCount(compartment);
            return count > 0 && TryReadRecord(compartment, count - 1, out var facts)
                ? facts : default;
        }

        private static WarehouseShelfBoxRef MakeRef(WarehouseShelf shelf, int compartmentIndex,
            Guid id, int slot)
        {
            GameObjectId shelfIdentity = default;
            if (shelf != null)
            {
                GameObjectIdentityApi.TryGetId(shelf, out shelfIdentity);
            }

            return new WarehouseShelfBoxRef
            {
                Shelf = shelf,
                CompartmentIndex = compartmentIndex,
                Slot = slot,
                Identity = id,
                ShelfIdentity = shelfIdentity,
            };
        }

        private static void ResolveDeferredShelf(WarehouseShelfBoxRef reference)
        {
            if (reference == null)
            {
                return;
            }

            if (reference.Shelf == null && reference.ShelfIdentity.IsValid
                && GameObjectIdentityApi.TryResolve(reference.ShelfIdentity, out var entity))
            {
                reference.Shelf = entity as WarehouseShelf;
            }

            if (reference.Identity != Guid.Empty
                && WarehouseBoxLocations.TryGetLocation(reference.Identity, out var known))
            {
                if (reference.Shelf == null)
                {
                    reference.Shelf = known.Shelf;
                }

                if (reference.CompartmentIndex < 0)
                {
                    reference.CompartmentIndex = known.CompartmentIndex;
                }

                if (reference.Slot < 0)
                {
                    reference.Slot = known.Slot;
                }
            }
        }

        private static void ResolveDeferredShelf(WarehouseCompartmentState state)
        {
            if (state == null || state.Shelf != null || !state.ShelfIdentity.IsValid)
            {
                return;
            }

            if (GameObjectIdentityApi.TryResolve(state.ShelfIdentity, out var entity))
            {
                state.Shelf = entity as WarehouseShelf;
            }
        }

        private PackagingBoxEntityDescriptor DescribeCreatedBox(Guid id,
            InteractablePackagingBox_Item box, WarehouseBoxFacts facts)
        {
            if (box != null)
            {
                var state = BoxNetworkInteraction.DescribeBox(id, box);
                var descriptor = BoxIdentityLifecycle.DescribeState(id, state);
                if (descriptor != null)
                {
                    return descriptor;
                }
            }

            return new PackagingBoxEntityDescriptor
            {
                BoxNetworkId = id,
                BoxKind = BoxNetworkKind.Item,
                BoxItemType = facts.ItemType,
                BoxItemCount = facts.Amount,
                BoxBig = facts.IsBig,
                Position = box == null ? Vector3.zero : box.transform.position,
                Rotation = box == null ? Quaternion.identity : box.transform.rotation,
            };
        }

        private bool TryGetBoxId(InteractablePackagingBox box, out Guid id, string context)
        {
            id = Guid.Empty;
            if (box == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] dropped " + context
                    + ": the box did not resolve to a live object.");
                return false;
            }

            if (!_boxes.TryGetId(box, out id))
            {
                CoopPlugin.Log.LogWarning("[warehouse] dropped " + context + " for " + box.name
                    + ": the live box has no network identity.");
                return false;
            }

            return true;
        }

        private static bool AddRecord(ShelfCompartment compartment, EItemType type, int amount,
            bool isBig)
        {
            var record = Activator.CreateInstance(_recordType);
            _recordItem.SetValue(record, Enum.ToObject(_recordItemType, (int)type));
            _recordAmount.SetValue(record, amount);
            _recordBig.SetValue(record, isBig);
            _recordAdd.Invoke(compartment, new[] { record });
            return true;
        }

        private static int RecordCount(ShelfCompartment compartment)
            => _recordCount == null ? 0 : (int)_recordCount.Invoke(compartment, null);

        private static bool TryReadRecord(ShelfCompartment compartment, int index,
            out WarehouseBoxFacts facts)
        {
            facts = default;
            if (compartment == null || index < 0 || _recordPeek == null)
            {
                return false;
            }

            var record = _recordPeek.Invoke(compartment, new object[] { index });
            if (record == null)
            {
                return false;
            }

            facts = new WarehouseBoxFacts(
                (EItemType)Convert.ToInt32(_recordItem.GetValue(record)),
                (int)_recordAmount.GetValue(record),
                (bool)_recordBig.GetValue(record));
            return facts.IsValid;
        }

        private bool CanStore(ShelfCompartment compartment, EItemType itemType, int amount,
            bool isBig)
        {
            return _usesRecords
                && WouldVanillaStore(compartment, itemType, amount, isBig)
                && IsWarehouse(compartment) && amount <= MaxWarehouseAmount;
        }

        private static bool WouldVanillaStore(ShelfCompartment compartment, EItemType itemType,
            int amount, bool isBig)
        {
            return itemType != EItemType.None && amount > 0 && compartment != null
                && compartment.m_CanPutBox && compartment.CheckBoxType(isBig) == isBig
                && compartment.HasEnoughSlot() && compartment.CheckBoxItemType(itemType);
        }

        private static bool IsValidBox(EItemType type, int amount)
            => type != EItemType.None && amount > 0 && amount <= MaxWarehouseAmount;

        private static bool Validate(WarehouseStoreAction message)
            => message?.Shelf != null && message.Box != null && message.CompartmentIndex >= 0
                && IsValidBox(message.ItemType, message.Amount);

        private static bool IsWarehouse(ShelfCompartment compartment)
            => compartment != null && compartment.GetWarehouseShelf() != null;

        private static bool TryGetAddress(ShelfCompartment compartment, out WarehouseShelf shelf,
            out int compartmentIndex)
        {
            shelf = null;
            compartmentIndex = 0;
            if (!IsWarehouse(compartment))
            {
                return false;
            }

            shelf = compartment.GetWarehouseShelf();
            compartmentIndex = compartment.GetIndex();
            return shelf != null;
        }

        internal static ShelfCompartment ResolveCompartment(WarehouseShelf shelf,
            int compartmentIndex)
        {
            if (shelf == null || compartmentIndex < 0)
            {
                return null;
            }

            var storage = shelf.GetStorageCompartmentList();
            for (var i = 0; storage != null && i < storage.Count; i++)
            {
                var compartment = storage[i]?.GetShelfCompartment();
                if (compartment != null && compartment.GetIndex() == compartmentIndex)
                {
                    return compartment;
                }
            }

            return null;
        }

        private static List<ShelfCompartment> GetWarehouseCompartments()
        {
            var result = new List<ShelfCompartment>();
            var manager = SceneRef<ShelfManager>.Get();
            var shelves = manager?.m_WarehouseShelfList;
            if (shelves == null)
            {
                return result;
            }

            for (var i = 0; i < shelves.Count; i++)
            {
                var storage = shelves[i]?.GetStorageCompartmentList();
                if (storage == null)
                {
                    continue;
                }

                for (var j = 0; j < storage.Count; j++)
                {
                    var compartment = storage[j]?.GetShelfCompartment();
                    if (compartment != null)
                    {
                        result.Add(compartment);
                    }
                }
            }

            return result;
        }

        /// <summary>Resolves the one-argument batcher method for this build. A build may declare
        /// overloads with a different arity; picking by name alone invoked the wrong one and threw
        /// TargetParameterCountException.</summary>
        private static MethodInfo ResolveCompartmentMethod(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }

            var exact = AccessTools.Method(type, name, new[] { typeof(ShelfCompartment) });
            if (exact != null)
            {
                return exact;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                var parameters = methods[i].GetParameters();
                if (methods[i].Name == name && parameters.Length == 1
                    && parameters[0].ParameterType.IsAssignableFrom(typeof(ShelfCompartment)))
                {
                    return methods[i];
                }
            }

            return null;
        }

        private static void Probe()
        {
            if (_probed)
            {
                return;
            }

            _probed = true;
            var assembly = typeof(ShelfCompartment).Assembly;
            _recordType = assembly.GetType("StoredBoxRecord", false);
            _recordCount = AccessTools.Method(typeof(ShelfCompartment), "GetStoredBoxRecordCount");
            _recordPeek = AccessTools.Method(typeof(ShelfCompartment), "PeekStoredBoxRecord");
            _recordPop = AccessTools.Method(typeof(ShelfCompartment), "TryPopLastStoredBoxRecord");
            _recordAdd = AccessTools.Method(typeof(ShelfCompartment), "AddStoredBoxRecord");
            _recordList = AccessTools.Field(typeof(ShelfCompartment), "m_StoredBoxRecordList");
            _compartmentBig = AccessTools.Field(typeof(ShelfCompartment), "m_IsBigBox");
            var batcher = assembly.GetType("StoredBoxVisualBatcher", false);
            _recordRebuild = ResolveCompartmentMethod(batcher, "RebuildImmediate");
            _recordMarkDirty = ResolveCompartmentMethod(batcher, "MarkDirty");
            _recordHide = ResolveCompartmentMethod(batcher, "OnCompartmentDestroyed");
            // A build may declare these as instance methods instead of static ones; resolve the
            // singleton so the invoke below always has a target when the method needs one.
            _batcherInstance = batcher?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            if (_recordType != null)
            {
                _recordItem = AccessTools.Field(_recordType, "itemType");
                _recordAmount = AccessTools.Field(_recordType, "amount");
                _recordBig = AccessTools.Field(_recordType, "isBigBox");
                _recordItemType = _recordItem?.FieldType;
            }

            _usesRecords = _recordType != null && _recordCount != null && _recordPeek != null
                && _recordPop != null && _recordAdd != null && _recordItem != null
                && _recordAmount != null && _recordBig != null && _recordItemType != null
                && _recordList != null;
            if (_usesRecords)
            {
                CoopPlugin.Log.LogInfo("World warehouse uses record-backed storage.");
            }
            else
            {
                CoopPlugin.Log.LogError("World warehouse disabled: the record storage surface is "
                    + "missing. Both live game builds use records; this DLL needs updating.");
            }
        }
    }

    /// <summary>
    /// The host's post-action answer to one warehouse action, sent to the acting client on accept
    /// and reject alike. <see cref="Compartment"/> is the host's authoritative state for the one
    /// compartment the action touched, so the prediction merges onto real state instead of
    /// inverting a snapshot authority has already moved past.
    /// </summary>
    [NetworkMessage]
    public sealed class WarehouseShelfResult : ActionResponseMessage
    {
        public WarehouseCompartmentState Compartment;
    }

    /// <summary>
    /// Client -> host: store one live item box into a warehouse compartment. The client's game path
    /// already appended the record, so the action only carries what a reversal needs; the host
    /// validates the live box against its own copy and applies the record itself.
    /// </summary>
    [NetworkMessage]
    public sealed class WarehouseStoreAction : WorldIntentMessage
    {
        public WarehouseShelf Shelf;
        public int CompartmentIndex;
        public InteractablePackagingBox Box;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;

        /// <summary>The entry and values the prediction moved. Local only; the host reads its own
        /// copy of the box and never trusts client-supplied state.</summary>
        [NonSerialized]
        internal WarehouseShelfBoxRef SourceRef;

        [NonSerialized]
        internal WarehouseShelfInteraction.WarehouseBoxFacts Facts;

        /// <summary>The compartment before the game's own store appended its record. Local only.</summary>
        [NonSerialized]
        internal WarehouseCompartmentState Snapshot;

        /// <summary>The client-side warehouse protocol that owns this prediction. Local only.</summary>
        [NonSerialized]
        internal WarehouseShelfInteraction Origin;

        public override void OnUndo(ActionResponseMessage response) => Origin?.UndoStore(this);

        public override void OnMerge(ActionResponseMessage response)
            => Origin?.MergeStore(this, response as WarehouseShelfResult);
    }

    /// <summary>
    /// Client -> host: take the top entry of a warehouse compartment. The client's game path already
    /// popped the record and moved the box into the hand; the host validates the source top against
    /// the descriptor and performs the same pop on its own state.
    /// </summary>
    [NetworkMessage]
    public sealed class WarehouseTakeAction : WorldIntentMessage
    {
        public WarehouseShelfBoxRef Source;
        public EntityDescriptor CreatedBox;

        /// <summary>The values the prediction removed. Local only.</summary>
        [NonSerialized]
        internal WarehouseShelfInteraction.WarehouseBoxFacts Facts;

        /// <summary>The exact live box the prediction put in the hand, released by the undo. Local
        /// only: the host creates its own representation.</summary>
        [NonSerialized]
        internal InteractablePackagingBox HeldBox;

        /// <summary>The client-side warehouse protocol that owns this prediction. Local only.</summary>
        [NonSerialized]
        internal WarehouseShelfInteraction Origin;

        public override void OnUndo(ActionResponseMessage response) => Origin?.UndoTake(this);

        public override void OnMerge(ActionResponseMessage response)
            => Origin?.MergeTake(this, response as WarehouseShelfResult);
    }
}
