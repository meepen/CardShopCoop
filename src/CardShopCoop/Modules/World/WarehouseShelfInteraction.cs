using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Owns warehouse box storage for the World module. A live warehouse entry is a Unity
    /// <see cref="InteractablePackagingBox"/>; a stored entry is a value in a
    /// <see cref="ShelfCompartment"/> and has no live object. The identity module owns the
    /// Guid/location registry that names entries on the wire; this class owns only compartment
    /// state, prediction ordering, and the game apply paths.
    /// </summary>
    internal sealed class WarehouseShelfInteraction
    {
        private const int MaxWarehouseAmount = 4096;

        private static bool _probed;
        private static bool _usesRecords;
        private static bool _usesLiveBoxes;
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

        // Only one warehouse mutation may be in flight per compartment. Later take/store actions
        // have already run the game's local path, but their intents are queued and sent only after
        // the previous decision. A rejection drops the dependent queue.
        private sealed class WarehouseMutation
        {
            internal long Key;
            internal Guid ActionId;
            internal Func<Guid> Send;
            internal Action Cancel;
        }

        private readonly Dictionary<long, Queue<WarehouseMutation>> _queuedMutations = new();
        private readonly Dictionary<long, WarehouseMutation> _inFlightMutations = new();
        private readonly Dictionary<long, Guid> _inFlightPrediction = new();
        private readonly Dictionary<Guid, long> _predictionCompartments = new();

        // Incremented when an authoritative compartment state-set is applied. A prediction undo
        // captured the previous value, so a rejection refresh that already installed authority
        // makes the later generic rollback a no-op instead of inverting a newer state.
        private readonly Dictionary<long, long> _authoritativeEpoch = new();

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
            Action<int, INetMessage> send, BoxNetworkInteraction boxes,
            PlayerBoxInteraction playerBox = null, Func<bool> inGame = null)
        {
            _host = host;
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            _ = send ?? throw new ArgumentNullException(nameof(send));
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _playerBox = playerBox;
            _inGame = inGame;
            Probe();
        }

        private bool InGame() => _inGame == null || _inGame();

        internal static bool Available()
        {
            Probe();
            return _usesRecords || _usesLiveBoxes;
        }

        internal static bool UsesRecords
        {
            get
            {
                Probe();
                return _usesRecords;
            }
        }

        internal static bool UsesLiveBoxes
        {
            get
            {
                Probe();
                return _usesLiveBoxes;
            }
        }

        internal void Reset()
        {
            AbandonAllMutations();
            _deferredStates.Clear();
            _deferredDeltas.Clear();
            _queuedMutations.Clear();
            _inFlightMutations.Clear();
            _inFlightPrediction.Clear();
            _predictionCompartments.Clear();
            _authoritativeEpoch.Clear();
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
            _currentStoreBox = _usesRecords ? box : null;
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
                // 1.00's own OnFinishLerp arranges records with an unstable sort; re-assert the
                // 1.02 comparator here so both backends agree on the top and insertion slot.
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

            // The popped entry no longer names a shelf location. The created box owns the identity
            // when the game reused the stored live object (live backend) or a fresh one otherwise.
            WarehouseBoxLocations.ClearLocation(capture.SourceId);
            var sourceRef = MakeRef(capture.Shelf, capture.CompartmentIndex, capture.SourceId,
                capture.SourceSlot);
            var key = CompartmentKey(capture.Shelf, capture.CompartmentIndex);
            var epoch = EpochOf(key);
            var descriptor = DescribeCreatedBox(createdId, held, capture.Facts);
            var delta = new WarehouseDeltaMessage
            {
                IsStore = false,
                Ref = sourceRef,
                ItemType = capture.Facts.ItemType,
                Amount = capture.Facts.Amount,
                IsBig = capture.Facts.IsBig,
                Box = held,
            };

            EnqueueMutation(key,
                () => SendTakePrediction(key, epoch, sourceRef, descriptor, capture.Facts, held),
                () => UndoTake(key, epoch, sourceRef, capture.Facts, descriptor, held));
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
            if (_usesRecords)
            {
                return RecordCount(capture.Compartment) < capture.RecordCount;
            }

            var held = _playerBox?.LocalHeldBox;
            return held != null
                && (ReferenceEquals(held, capture.PreviousTop)
                    || (_boxes.TryGetId(held, out var heldId) && heldId == capture.SourceId));
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
            if (!_usesRecords || compartment == null || !IsWarehouse(compartment)
                || _hostApplyingCommand || _applyingRemote || ActionLedger.IsReconciling)
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
            var epoch = EpochOf(CompartmentKey(shelf, compartmentIndex));
            EnqueueMutation(CompartmentKey(shelf, compartmentIndex),
                () => SendStorePrediction(CompartmentKey(shelf, compartmentIndex), epoch, shelf,
                    compartmentIndex, sourceRef, facts, liveBox, snapshot),
                () => UndoStore(CompartmentKey(shelf, compartmentIndex), epoch, shelf,
                    compartmentIndex, sourceRef, facts, liveBox, snapshot));
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

            if (!_applyingRemote && !_hostApplyingCommand && !ActionLedger.IsReconciling)
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

        /// <summary>Live backend: the game added a live box to a compartment.</summary>
        internal void OnBoxAdded(ShelfCompartment compartment)
        {
            if (!_usesLiveBoxes || compartment == null || !IsWarehouse(compartment)
                || _hostApplyingCommand || _applyingRemote || ActionLedger.IsReconciling)
            {
                return;
            }

            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null
                || !TryGetAddress(compartment, out var shelf, out var compartmentIndex))
            {
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

            var id = _host
                ? _boxes.EnsureWarehouseHostId(box)
                : (_boxes.TryGetId(box, out var known) ? known
                    : WorldClientBehaviour.AssignCreatedBoxId(box));
            if (id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] added live box has no identity; not syncing.");
                return;
            }

            var slot = LiveSlotOf(compartment, box);
            WarehouseBoxLocations.RegisterLive(id, box, shelf, compartmentIndex, slot);
            var facts = DescribeLiveBox(box);
            if (!facts.IsValid)
            {
                return;
            }

            var sourceRef = MakeRef(shelf, compartmentIndex, id, slot);
            if (_host)
            {
                BroadcastDelta(new WarehouseDeltaMessage
                {
                    IsStore = true,
                    Ref = sourceRef,
                    ItemType = facts.ItemType,
                    Amount = facts.Amount,
                    IsBig = facts.IsBig,
                    Box = box,
                });
                return;
            }

            var key = CompartmentKey(shelf, compartmentIndex);
            var epoch = EpochOf(key);
            EnqueueMutation(key,
                () => SendStorePrediction(key, epoch, shelf, compartmentIndex, sourceRef, facts, box,
                    null),
                () => UndoStore(key, epoch, shelf, compartmentIndex, sourceRef, facts, box, null));
        }

        /// <summary>Live backend host: a live box left a compartment. The removed object itself
        /// continues as the player/worker's live representation (same id), so the delta still names
        /// it and observers keep their replica. The record backend uses the value-only form because
        /// its respawn creates a fresh live box.</summary>
        internal void OnBoxRemoved(ShelfCompartment compartment, InteractablePackagingBox_Item box)
        {
            if (!_host || !_usesLiveBoxes || compartment == null || !IsWarehouse(compartment)
                || _hostApplyingCommand || _applyingRemote || !InGame() || box == null)
            {
                return;
            }

            if (!_boxes.TryGetId(box, out var id) || id == Guid.Empty)
            {
                id = _boxes.EnsureWarehouseHostId(box);
                if (id == Guid.Empty)
                {
                    return;
                }
            }

            var shelf = compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return;
            }

            var compartmentIndex = compartment.GetIndex();
            var facts = DescribeLiveBox(box);
            WarehouseBoxLocations.ClearLocation(id);
            if (!facts.IsValid)
            {
                return;
            }

            var localTake = ReferenceEquals(_playerBox?.LocalHeldBox, box);
            CoopPlugin.Log.LogInfo("[warehouse] live remove id=" + id + " shelf="
                + compartmentIndex + " localTake=" + localTake + ".");
            BroadcastDelta(new WarehouseDeltaMessage
            {
                IsStore = false,
                Ref = MakeRef(shelf, compartmentIndex, id, -1),
                ItemType = facts.ItemType,
                Amount = facts.Amount,
                IsBig = facts.IsBig,
                // Live backend: the removed object itself continues as the worker/player's live
                // representation (same id), so observers keep their replica. The record backend
                // sends the value-only form because its respawn creates a fresh live box.
                Box = box,
            });

            if (localTake)
            {
                // The hold broadcast raced the removal. Re-announce it now that observers remove
                // the top entry first, so the taken box attaches to this avatar.
                _playerBox?.PublishHostHeld(box, id);
            }
        }

        /// <summary>Host: applies a client store. On any refusal the requester receives the
        /// authoritative compartment state before the generic rollback.</summary>
        internal bool HostApplyStore(WarehouseStoreMessage message, int connectionId,
            Action<INetMessage> response = null)
        {
            if (!_host || !Validate(message) || !TryGetBoxId(message.Box, out var boxNetworkId,
                    "store"))
            {
                return false;
            }

            var compartment = ResolveCompartment(message.Shelf, message.CompartmentIndex);
            var storeBox = message.Box as InteractablePackagingBox_Item;
            var accepted = false;
            if (compartment == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: shelf "
                    + (message.Shelf == null ? "<null>" : message.Shelf.name) + " has no "
                    + "compartment " + message.CompartmentIndex + ".");
            }
            else if (storeBox == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: box " + message.Box.name
                    + " is not an item box.");
            }
            else if (storeBox.m_ItemCompartment == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: box " + message.Box.name
                    + " has no item compartment.");
            }
            else if (storeBox.m_ItemCompartment.GetItemType() != message.ItemType
                     || storeBox.m_ItemCompartment.GetItemCount() != message.Amount
                     || storeBox.m_IsBigBox != message.IsBig)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: payload disagrees with box "
                    + message.Box.name + ".");
            }
            else if (IsShelvedElsewhere(message.Shelf, message.CompartmentIndex, boxNetworkId,
                         storeBox))
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: box " + message.Box.name
                    + " is already stored in another compartment.");
            }
            else if (CanStore(compartment, message.ItemType, message.Amount, message.IsBig))
            {
                try
                {
                    _hostApplyingCommand = true;
                    if (_usesRecords)
                    {
                        accepted = ApplyRecordStore(compartment, message.Shelf,
                            message.CompartmentIndex, boxNetworkId, message.ItemType,
                            message.Amount, message.IsBig);
                    }
                    else
                    {
                        accepted = ApplyLiveStore(compartment, message.Shelf,
                            message.CompartmentIndex, boxNetworkId, storeBox);
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
                CoopPlugin.Log.LogWarning("Warehouse store rejected: shelf "
                    + message.Shelf.name + " compartment " + message.CompartmentIndex
                    + " will not take box " + message.Box.name + " (" + message.ItemType + " x"
                    + message.Amount + ", big=" + message.IsBig + ").");
            }

            if (!accepted)
            {
                SendCompartmentState(response, compartment);
                return false;
            }

            var slot = _usesRecords
                ? SlotOf(boxNetworkId)
                : LiveSlotOf(compartment, message.Box as InteractablePackagingBox_Item);
            var liveBox = _usesRecords ? null : message.Box;
            BroadcastDelta(new WarehouseDeltaMessage
            {
                ActionId = message.ActionId,
                IsStore = true,
                Ref = MakeRef(message.Shelf, message.CompartmentIndex, boxNetworkId, slot),
                ItemType = message.ItemType,
                Amount = message.Amount,
                IsBig = message.IsBig,
                Box = liveBox,
            });
            return true;
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
                BoxIdentityLifecycle.ForgetBox(item);
                _boxes.DestroyWithoutNotification(item);
            }

            return true;
        }

        private bool ApplyLiveStore(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, Guid id, InteractablePackagingBox_Item box)
        {
            var stored = compartment.GetInteractablePackagingBoxList();
            if (stored == null || !stored.Contains(box))
            {
                _playerBox?.DetachMirroredHold(box, id);
                _playerBox?.ReleaseHeldObject(box);
                RemoveListedBox(compartment, id, box);
                if (!StoreLiveBox(compartment, box))
                {
                    return false;
                }
            }

            _boxes.BindStoredLiveBox(id, box);
            WarehouseBoxLocations.RegisterLive(id, box, shelf, compartmentIndex,
                LiveSlotOf(compartment, box));
            return true;
        }

        /// <summary>Host: applies a client take. The source's top is validated against the
        /// descriptor facts; a mismatch refreshes the requester with the authoritative compartment
        /// state and rejects the intent.</summary>
        internal bool HostApplyTake(WarehouseTakeMessage message, int connectionId,
            Action<INetMessage> response = null)
        {
            if (!_host || message?.Source?.Shelf == null
                || message.CreatedBox is not PackagingBoxEntityDescriptor created
                || created.BoxNetworkId == Guid.Empty
                || created.BoxKind != BoxNetworkKind.Item)
            {
                return false;
            }

            var compartment = ResolveCompartment(message.Source.Shelf,
                message.Source.CompartmentIndex);
            if (compartment == null
                || !TryGetTopEntry(compartment, out var topFacts, out var topId, out var topSlot,
                    out var topLive))
            {
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: the source compartment "
                    + "has no readable top entry.");
                SendCompartmentState(response, compartment);
                return false;
            }

            if (!topFacts.IsValid
                || created.BoxItemType != topFacts.ItemType
                || created.BoxItemCount != topFacts.Amount
                || created.BoxBig != topFacts.IsBig
                || (message.Source.Identity != Guid.Empty && topId != Guid.Empty
                    && message.Source.Identity != topId)
                || (message.Source.Slot >= 0 && message.Source.Slot != topSlot))
            {
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: source top ("
                    + topFacts.ItemType + " x" + topFacts.Amount + ", big=" + topFacts.IsBig
                    + ", id=" + topId + ", slot=" + topSlot + ") disagrees with created box ("
                    + created.BoxItemType + " x" + created.BoxItemCount + ", big="
                    + created.BoxBig + ", id=" + created.BoxNetworkId + ", slot="
                    + message.Source.Slot + ").");
                SendCompartmentState(response, compartment);
                return false;
            }

            var createdId = created.BoxNetworkId;
            if (!_boxes.PushHostCreatedId(createdId, topLive))
            {
                CoopPlugin.Log.LogWarning("[warehouse] host take rejected: box id=" + createdId
                    + " is not a fresh creator id.");
                SendCompartmentState(response, compartment);
                return false;
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
                SendCompartmentState(response, compartment);
                return false;
            }

            BroadcastDelta(new WarehouseDeltaMessage
            {
                ActionId = message.ActionId,
                IsStore = false,
                Ref = message.Source,
                ItemType = topFacts.ItemType,
                Amount = topFacts.Amount,
                IsBig = topFacts.IsBig,
                Box = spawned,
            });
            if (spawned != null)
            {
                _playerBox?.AnnounceGrantedHold(spawned, connectionId);
            }

            return true;
        }

        private bool TryPopTopEntry(ShelfCompartment compartment, out WarehouseBoxFacts facts)
        {
            facts = default;
            if (_usesRecords)
            {
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

            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null)
            {
                facts = default;
                return false;
            }

            if (!box.CanPickup())
            {
                box.StopLerpToTransform();
            }

            facts = DescribeLiveBox(box);
            if (!facts.IsValid)
            {
                return false;
            }

            var id = _boxes.TryGetId(box, out var known) ? known : Guid.Empty;
            compartment.RemoveBox(box);
            if (id != Guid.Empty)
            {
                WarehouseBoxLocations.ClearLocation(id);
                BoxIdentityLifecycle.ForgetBox(box);
            }

            _boxes.DestroyWithoutNotification(box);
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
                CoopPlugin.Log.LogWarning("[warehouse] ignored an empty authoritative state; "
                    + "an empty list is not an authoritative clear.");
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

            if (entry.ItemType == EItemType.None && entry.Boxes.Count > 0)
            {
                CoopPlugin.Log.LogError("[warehouse] authoritative header has no ItemType for "
                    + "shelf " + entry.Shelf.name + " compartment " + entry.CompartmentIndex
                    + "; the compartment state is refused rather than inferred from its entries.");
                return;
            }

            if (_usesRecords)
            {
                ClearLiveBoxes(compartment);
                ClearStoredRecords(compartment);
                var ids = new List<Guid>(entry.Boxes.Count);
                for (var i = 0; i < entry.Boxes.Count; i++)
                {
                    var box = entry.Boxes[i];
                    if (box == null || box.Amount <= 0)
                    {
                        continue;
                    }

                    var id = box.Ref?.Identity ?? Guid.Empty;
                    if (id == Guid.Empty || ids.Contains(id))
                    {
                        id = Guid.NewGuid();
                    }

                    AddRecord(compartment, entry.ItemType, box.Amount, entry.IsBig);
                    ids.Add(id);
                }

                WarehouseBoxLocations.SetCompartmentOrder(entry.Shelf, entry.CompartmentIndex,
                    ids, true);
                CanonicalizeRecords(compartment);
                if (ids.Count == 0)
                {
                    compartment.SetCompartmentBoxType(entry.IsBig);
                    if (entry.ItemType != EItemType.None)
                    {
                        compartment.SetCompartmentItemType(entry.ItemType);
                    }
                }
            }
            else
            {
                var reuse = new Dictionary<Guid, InteractablePackagingBox_Item>();
                for (var i = 0; i < entry.Boxes.Count; i++)
                {
                    var candidate = entry.Boxes[i];
                    var candidateId = candidate?.Ref?.Identity ?? Guid.Empty;
                    if (candidateId == Guid.Empty)
                    {
                        continue;
                    }

                    var live = candidate.Box;
                    if (live == null && _boxes.TryGetBox(candidateId, out var known))
                    {
                        live = known;
                    }

                    if (live is InteractablePackagingBox_Item item)
                    {
                        reuse[candidateId] = item;
                    }
                }

                ClearLiveBoxes(compartment, reuse);
                for (var i = 0; i < entry.Boxes.Count; i++)
                {
                    var box = entry.Boxes[i];
                    if (box == null || box.Amount <= 0)
                    {
                        continue;
                    }

                    var id = box.Ref?.Identity ?? Guid.Empty;
                    if (id == Guid.Empty)
                    {
                        id = Guid.NewGuid();
                    }

                    InteractablePackagingBox_Item spawned;
                    if (reuse.TryGetValue(id, out var reused) && reused != null
                        && (reused.m_IsStored || StoreLiveBox(compartment, reused)))
                    {
                        spawned = reused;
                    }
                    else if (!StoreLiveBox(compartment, entry.ItemType, box.Amount, entry.IsBig,
                                 out spawned))
                    {
                        CoopPlugin.Log.LogWarning("[warehouse] baseline could not materialize "
                            + entry.ItemType + " x" + box.Amount + " on shelf "
                            + entry.Shelf.name + " compartment " + entry.CompartmentIndex + ".");
                        continue;
                    }

                    _boxes.BindStoredLiveBox(id, spawned);
                    WarehouseBoxLocations.RegisterLive(id, spawned, entry.Shelf,
                        entry.CompartmentIndex, i);
                }

                if (entry.Boxes.Count == 0)
                {
                    compartment.SetCompartmentBoxType(entry.IsBig);
                    if (entry.ItemType != EItemType.None)
                    {
                        compartment.SetCompartmentItemType(entry.ItemType);
                    }
                }
            }

            var key = CompartmentKey(entry.Shelf, entry.CompartmentIndex);
            BumpEpoch(key);
            AbandonMutation(key);
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
                        message.ItemType, message.Amount, message.IsBig, message.Box);
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
            EItemType itemType, int amount, bool isBig, InteractablePackagingBox liveBox)
        {
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null || id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] store delta had no resolvable compartment or "
                    + "identity.");
                return false;
            }

            if (_usesRecords)
            {
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
                        BoxIdentityLifecycle.ForgetBox(item);
                        _boxes.DestroyWithoutNotification(item);
                    }
                }

                return true;
            }

            var existing = FindLiveBox(compartment, id);
            if (existing != null)
            {
                WarehouseBoxLocations.RegisterLive(id, existing, shelf, compartmentIndex,
                    LiveSlotOf(compartment, existing));
                return true;
            }

            if (liveBox is InteractablePackagingBox_Item itemBox)
            {
                _playerBox?.DetachMirroredHold(itemBox, id);
                _playerBox?.ReleaseHeldObject(itemBox);
                RemoveListedBox(compartment, id, itemBox);
                if (!StoreLiveBox(compartment, itemBox)
                    && !StoreLiveBox(compartment, itemType, amount, isBig, out itemBox))
                {
                    return false;
                }

                _boxes.BindStoredLiveBox(id, itemBox);
                WarehouseBoxLocations.RegisterLive(id, itemBox, shelf, compartmentIndex,
                    LiveSlotOf(compartment, itemBox));
                _playerBox?.ReleaseRemoteHoldForBox(itemBox);
                return true;
            }

            if (!StoreLiveBox(compartment, itemType, amount, isBig, out var spawned))
            {
                return false;
            }

            _boxes.BindStoredLiveBox(id, spawned);
            WarehouseBoxLocations.RegisterLive(id, spawned, shelf, compartmentIndex,
                LiveSlotOf(compartment, spawned));
            return true;
        }

        private void ApplyTakeValues(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, Guid id, InteractablePackagingBox heldBox,
            EItemType itemType, int amount, bool isBig)
        {
            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                if (count == 0)
                {
                    // The wire converter registers an unknown store identity at its wire location
                    // before the handler adds the record. A take can therefore arrive while the
                    // registry knows the id but the game record list is empty (the store delta
                    // never added it). Treat the take as authoritative: materialize the removed
                    // live box from the delta facts instead of leaving a registry-only ghost.
                    if (id != Guid.Empty && IsValidBox(itemType, amount)
                        && !_boxes.TryGetBox(id, out _))
                    {
                        WarehouseBoxLocations.ClearLocation(id);
                        var removed = _boxes.MaterializeWarehouseStored(itemType, amount, isBig);
                        if (removed != null)
                        {
                            BoxIdentityLifecycle.BindClientBox(id, removed);
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

                // The host may be on the live backend: its removed box continues as a live
                // representation (a player's hand or a worker's carry), but this record-backed
                // peer has no live object for the id. Materialize one and bind the same id so the
                // following pickup/throw/worker snapshot can resolve it. A record-backed host
                // announces the respawn as an EntityAdded; that then adopts this object instead of
                // creating a duplicate.
                if (id != Guid.Empty && !_boxes.TryGetBox(id, out _)
                    && IsValidBox(itemType, amount))
                {
                    var replica = _boxes.MaterializeWarehouseStored(itemType, amount, isBig);
                    if (replica != null)
                    {
                        BoxIdentityLifecycle.BindClientBox(id, replica);
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

                return;
            }

            var stored = FindStoredLiveBox(compartment, id);
            if (stored == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] take delta for id=" + id
                    + " had no stored live box on this peer.");
                return;
            }

            var storedId = _boxes.TryGetId(stored, out var known) ? known : id;
            var keep = heldBox != null && _boxes.TryGetId(heldBox, out var heldId)
                && heldId == storedId;
            compartment.RemoveBox(stored);
            if (keep)
            {
                // The same identity is the representation the observer will keep using; it is no
                // longer a stored entry, so it has no shelf location.
                BoxIdentityLifecycle.BindClientBox(storedId, stored);
                stored.m_IsStored = false;
                WarehouseBoxLocations.RegisterLive(storedId, stored, null, -1, -1);
                return;
            }

            BoxIdentityLifecycle.ForgetBox(stored);
            _boxes.DestroyWithoutNotification(stored);
            WarehouseBoxLocations.Forget(storedId);
        }

        private void RemoveStoredRecordById(WarehouseShelf shelf, int compartmentIndex,
            ShelfCompartment compartment, Guid id)
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
            RebuildStoredRecords(compartment, shelf, compartmentIndex, entries);
        }

        private void OnMutationRejected(long key)
        {
            var wasApplying = _applyingRemote;
            _applyingRemote = true;
            try
            {
                if (_queuedMutations.TryGetValue(key, out var queue))
                {
                    // These local actions already ran the game path but were never sent. Undo them
                    // in reverse order so a generic rejection restores the pre-action compartment
                    // state; they are never replayed as followers.
                    var pending = queue.ToArray();
                    for (var i = pending.Length - 1; i >= 0; i--)
                    {
                        try
                        {
                            pending[i].Cancel?.Invoke();
                        }
                        catch (Exception error)
                        {
                            CoopPlugin.Log.LogWarning("[warehouse] cancelling a dependent queued "
                                + "mutation failed: " + error);
                        }
                    }
                }

                DropQueuedMutations(key);
                if (_inFlightMutations.TryGetValue(key, out var mutation))
                {
                    // Re-assert the rejected action's pre-state after the dependent cancels. The
                    // prediction framework already ran its undo once; the closure is idempotent and
                    // restores a whole value snapshot rather than layering another inverse.
                    try
                    {
                        mutation.Cancel?.Invoke();
                    }
                    catch (Exception error)
                    {
                        CoopPlugin.Log.LogWarning("[warehouse] re-applying the rejected mutation's "
                            + "pre-state failed: " + error);
                    }

                    if (mutation.ActionId != Guid.Empty)
                    {
                        _predictionCompartments.Remove(mutation.ActionId);
                    }

                    _inFlightPrediction.Remove(key);
                    _inFlightMutations.Remove(key);
                    _queuedMutations.Remove(key);
                }
            }
            finally
            {
                _applyingRemote = wasApplying;
            }
        }

        private void EnqueueMutation(long key, Func<Guid> send, Action cancel)
        {
            var mutation = new WarehouseMutation { Key = key, Send = send, Cancel = cancel };
            if (!_inFlightMutations.ContainsKey(key))
            {
                StartMutation(key, mutation);
                return;
            }

            if (!_queuedMutations.TryGetValue(key, out var queue))
            {
                _queuedMutations[key] = queue = new Queue<WarehouseMutation>();
            }

            queue.Enqueue(mutation);
        }

        private void StartMutation(long key, WarehouseMutation mutation)
        {
            _inFlightMutations[key] = mutation;
            Guid actionId;
            try
            {
                actionId = mutation.Send();
            }
            catch
            {
                _inFlightMutations.Remove(key);
                throw;
            }

            mutation.ActionId = actionId;
            if (actionId == Guid.Empty)
            {
                // The mutation could not be sent (for example its live payload was unavailable).
                // Do not leave the compartment blocked; the local game path has run, and the next
                // authoritative state settles it.
                _inFlightMutations.Remove(key);
                AdvanceQueue(key);
                return;
            }

            _predictionCompartments[actionId] = key;
            _inFlightPrediction[key] = actionId;
        }

        private void AdvanceQueue(long key)
        {
            while (!_inFlightMutations.ContainsKey(key)
                && _queuedMutations.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                StartMutation(key, queue.Dequeue());
            }

            if (!_inFlightMutations.ContainsKey(key))
            {
                _queuedMutations.Remove(key);
            }
        }

        internal void CompleteMutation(Guid actionId)
        {
            if (actionId == Guid.Empty
                || !_predictionCompartments.TryGetValue(actionId, out var key))
            {
                return;
            }

            _predictionCompartments.Remove(actionId);
            _inFlightPrediction.Remove(key);
            _inFlightMutations.Remove(key);
            AdvanceQueue(key);
        }

        private void AbandonMutation(long key)
        {
            if (_inFlightMutations.TryGetValue(key, out var mutation)
                && mutation.ActionId != Guid.Empty)
            {
                _predictionCompartments.Remove(mutation.ActionId);
                ActionLedger.Ack(mutation.ActionId);
            }

            _inFlightPrediction.Remove(key);
            _inFlightMutations.Remove(key);
            _queuedMutations.Remove(key);
        }

        private void AbandonAllMutations()
        {
            foreach (var pair in _inFlightMutations)
            {
                if (pair.Value.ActionId != Guid.Empty)
                {
                    ActionLedger.Ack(pair.Value.ActionId);
                }
            }
        }

        private void DropQueuedMutations(long key)
        {
            if (_queuedMutations.TryGetValue(key, out var queue))
            {
                queue.Clear();
            }
        }

        private long CompartmentKey(WarehouseShelf shelf, int compartmentIndex)
            => ((long)shelf.GetInstanceID() << 32) | (uint)compartmentIndex;

        private long EpochOf(long key)
            => _authoritativeEpoch.TryGetValue(key, out var epoch) ? epoch : 0L;

        private void BumpEpoch(long key) => _authoritativeEpoch[key] = EpochOf(key) + 1;

        private Guid SendStorePrediction(long key, long epoch, WarehouseShelf shelf,
            int compartmentIndex, WarehouseShelfBoxRef sourceRef, WarehouseBoxFacts facts,
            InteractablePackagingBox liveBox, WarehouseCompartmentState snapshot)
        {
            var payload = liveBox;
            var temporary = false;
            if (payload == null && _usesRecords)
            {
                // The game destroys a record-store's live box immediately. A queued store's intent
                // is sent later, so materialize a short-lived payload solely to name the identity on
                // the wire; the host resolves it to its own replica of the same box.
                payload = _boxes.MaterializeWarehouseStored(facts.ItemType, facts.Amount, facts.IsBig);
                if (payload != null && sourceRef.Identity != Guid.Empty)
                {
                    BoxIdentityLifecycle.BindClientBox(sourceRef.Identity, payload);
                    temporary = true;
                }
            }

            if (payload == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] store box id=" + sourceRef.Identity
                    + " has no live payload; the intent is not sent.");
                return Guid.Empty;
            }

            var intent = new WarehouseStoreMessage
            {
                Shelf = shelf,
                CompartmentIndex = compartmentIndex,
                Box = payload,
                ItemType = facts.ItemType,
                Amount = facts.Amount,
                IsBig = facts.IsBig,
            };
            try
            {
                return WorldAction.Submit(PredictionScope(key), intent,
                    () => ApplyStoreValues(shelf, compartmentIndex, sourceRef.Identity,
                        facts.ItemType, facts.Amount, facts.IsBig, liveBox),
                    () => UndoStore(key, epoch, shelf, compartmentIndex, sourceRef, facts, liveBox,
                        snapshot),
                    () => OnMutationRejected(key));
            }
            finally
            {
                if (temporary && payload != null)
                {
                    BoxIdentityLifecycle.ForgetBox(payload);
                    _boxes.DestroyWithoutNotification(payload);
                }
            }
        }

        private Guid SendTakePrediction(long key, long epoch, WarehouseShelfBoxRef sourceRef,
            EntityDescriptor createdBox, WarehouseBoxFacts facts, InteractablePackagingBox held)
        {
            var intent = new WarehouseTakeMessage
            {
                Source = sourceRef,
                CreatedBox = createdBox,
            };
            return WorldAction.Submit(PredictionScope(key), intent,
                () => { },
                () => UndoTake(key, epoch, sourceRef, facts, createdBox, held),
                () => OnMutationRejected(key));
        }

        private void UndoStore(long key, long epoch, WarehouseShelf shelf, int compartmentIndex,
            WarehouseShelfBoxRef sourceRef, WarehouseBoxFacts facts, InteractablePackagingBox liveBox,
            WarehouseCompartmentState snapshot)
        {
            if (EpochOf(key) != epoch)
            {
                return;
            }

            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null)
            {
                return;
            }

            if (_usesRecords)
            {
                if (snapshot != null)
                {
                    RebuildFromSnapshot(compartment, snapshot);
                }
                else
                {
                    RemoveStoredRecordById(shelf, compartmentIndex, compartment,
                        sourceRef.Identity);
                }
            }
            else if (FindStoredLiveBox(compartment, sourceRef.Identity) is { } stored)
            {
                compartment.RemoveBox(stored);
                stored.m_IsStored = false;
                WarehouseBoxLocations.ClearLocation(sourceRef.Identity);
            }

            if (!_boxes.ClientEnsureWarehouseTake(sourceRef.Identity, facts.ItemType, facts.Amount,
                    facts.IsBig, out var held))
            {
                CoopPlugin.Log.LogWarning("[warehouse] store rollback could not restore box id="
                    + sourceRef.Identity + " into the hand.");
                return;
            }

            BoxIdentityLifecycle.BindClientBox(sourceRef.Identity, held);
            TakeIntoLocalHand(held);
        }

        private void UndoTake(long key, long epoch, WarehouseShelfBoxRef sourceRef,
            WarehouseBoxFacts facts, EntityDescriptor createdBox, InteractablePackagingBox held)
        {
            if (EpochOf(key) != epoch)
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

            if (_usesRecords && HasStoredEntry(shelf, compartmentIndex, sourceRef.Identity))
            {
                return;
            }

            if (!_usesRecords && FindLiveBox(compartment, sourceRef.Identity) != null)
            {
                return;
            }

            var createdId = createdBox is PackagingBoxEntityDescriptor descriptor
                ? descriptor.BoxNetworkId : Guid.Empty;
            if (_usesRecords)
            {
                if (held != null && _boxes.TryGetId(held, out var heldId) && heldId == createdId
                    && !BoxNetworkInteraction.IsStoredBox(held))
                {
                    _playerBox?.ReleaseHeldObject(held);
                    BoxIdentityLifecycle.ForgetBox(held);
                    _boxes.DestroyWithoutNotification(held);
                }

                AddRecord(compartment, facts.ItemType, facts.Amount, facts.IsBig);
                // The game appended the restored record. Give any pre-existing records their ids
                // first, then align the restored id with the append before the canonical sort.
                WarehouseBoxLocations.EnsureCompartmentSize(shelf, compartmentIndex,
                    Math.Max(0, RecordCount(compartment) - 1));
                var ids = new List<Guid>();
                WarehouseBoxLocations.CopyCompartmentOrder(shelf, compartmentIndex, ids);
                ids.Add(sourceRef.Identity);
                WarehouseBoxLocations.SetCompartmentOrder(shelf, compartmentIndex, ids, false);
                CanonicalizeRecords(compartment);
                return;
            }

            var stored = _boxes.TryGetBox(sourceRef.Identity, out var live)
                ? live as InteractablePackagingBox_Item : null;
            if (stored == null && held is InteractablePackagingBox_Item heldItem)
            {
                stored = heldItem;
            }

            if (stored == null
                && !_boxes.ClientEnsureWarehouseTake(sourceRef.Identity, facts.ItemType,
                    facts.Amount, facts.IsBig, out stored))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take rollback could not restore id="
                    + sourceRef.Identity + ".");
                return;
            }

            _playerBox?.ReleaseLocalHold(sourceRef.Identity);
            if (!StoreLiveBox(compartment, stored))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take rollback could not re-store id="
                    + sourceRef.Identity + ".");
                return;
            }

            _boxes.BindStoredLiveBox(sourceRef.Identity, stored);
            WarehouseBoxLocations.RegisterLive(sourceRef.Identity, stored, shelf, compartmentIndex,
                LiveSlotOf(compartment, stored));
        }

        private void RebuildFromSnapshot(ShelfCompartment compartment,
            WarehouseCompartmentState snapshot)
        {
            var shelf = snapshot.Shelf ?? compartment.GetWarehouseShelf();
            if (shelf == null)
            {
                return;
            }

            ClearStoredRecords(compartment);
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
                    id = Guid.NewGuid();
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
            if (_usesRecords)
            {
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

            var boxes = compartment.GetInteractablePackagingBoxList();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box?.m_ItemCompartment == null)
                {
                    continue;
                }

                var id = _boxes.TryGetId(box, out var known) ? known : Guid.Empty;
                state.Boxes.Add(new WarehouseBoxEntry
                {
                    Ref = MakeRef(shelf, state.CompartmentIndex, id, i),
                    Amount = box.m_ItemCompartment.GetItemCount(),
                    Box = box,
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
            if (_usesRecords)
            {
                // The baseline must publish the same order the peer will canonicalize to.
                CanonicalizeRecords(compartment);
            }

            TryGetHeader(compartment, out state.ItemType, out state.IsBig);

            if (_usesRecords)
            {
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

            var boxes = compartment.GetInteractablePackagingBoxList();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box?.m_ItemCompartment == null)
                {
                    continue;
                }

                var id = _host ? _boxes.EnsureWarehouseHostId(box)
                    : (_boxes.TryGetId(box, out var known) ? known : Guid.Empty);
                if (id == Guid.Empty)
                {
                    continue;
                }

                WarehouseBoxLocations.RegisterLive(id, box, shelf, state.CompartmentIndex, i);
                state.Boxes.Add(new WarehouseBoxEntry
                {
                    Ref = MakeRef(shelf, state.CompartmentIndex, id, i),
                    Amount = box.m_ItemCompartment.GetItemCount(),
                    Box = box,
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

        /// <summary>Builds one compartment's authoritative state message (used as a per-requester
        /// rejection refresh before the generic rollback).</summary>
        internal WarehouseStateMessage BuildCompartmentStateMessage(ShelfCompartment compartment)
        {
            var state = BuildCompartmentState(compartment);
            return state == null ? null : new WarehouseStateMessage { Compartments = { state } };
        }

        private void SendCompartmentState(Action<INetMessage> response,
            ShelfCompartment compartment)
        {
            if (response == null || compartment == null)
            {
                return;
            }

            var message = BuildCompartmentStateMessage(compartment);
            if (message != null)
            {
                response(message);
            }
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

            if (_usesRecords)
            {
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

            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null)
            {
                return false;
            }

            facts = DescribeLiveBox(box);
            slot = LiveSlotOf(compartment, box);
            live = box;
            id = _boxes.TryGetId(box, out var liveId) ? liveId : Guid.Empty;
            return facts.IsValid;
        }

        private bool TryGetHeader(ShelfCompartment compartment, out EItemType itemType,
            out bool isBig)
        {
            itemType = compartment.GetItemType();
            if (_usesRecords)
            {
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
            }
            else
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                for (var i = 0; boxes != null && i < boxes.Count; i++)
                {
                    if (boxes[i]?.m_ItemCompartment != null)
                    {
                        itemType = boxes[i].m_ItemCompartment.GetItemType();
                        isBig = boxes[i].m_IsBigBox;
                        return true;
                    }
                }
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

        /// <summary>Records backend: rebuild the compartment list in the 1.02 order (amount
        /// descending, newer insertion first on ties) and publish the matching location slots.</summary>
        private void CanonicalizeRecords(ShelfCompartment compartment, bool immediate = true)
        {
            if (!_usesRecords || compartment == null)
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
            if (!_usesRecords || obj is not WarehouseShelf shelf)
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

        private void ClearStoredRecords(ShelfCompartment compartment)
        {
            while (RecordCount(compartment) > 0)
            {
                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                {
                    throw new InvalidOperationException(
                        "Warehouse record storage could not be cleared.");
                }
            }
        }

        private void RebuildStoredRecords(ShelfCompartment compartment, WarehouseShelf shelf,
            int compartmentIndex, List<WarehouseBoxEntry> entries)
        {
            TryGetHeader(compartment, out var itemType, out var isBig);
            ClearStoredRecords(compartment);
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
                    id = Guid.NewGuid();
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

        private void ClearLiveBoxes(ShelfCompartment compartment,
            IReadOnlyDictionary<Guid, InteractablePackagingBox_Item> reuse = null)
        {
            var existing = compartment.GetInteractablePackagingBoxList();
            if (existing == null)
            {
                return;
            }

            var copy = new List<InteractablePackagingBox_Item>(existing);
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

                if (existing.Contains(box))
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

        private void RemoveListedBox(ShelfCompartment compartment, Guid boxNetworkId,
            InteractablePackagingBox_Item bound)
        {
            var boxes = compartment?.GetInteractablePackagingBoxList();
            if (boxes == null)
            {
                return;
            }

            for (var i = boxes.Count - 1; i >= 0; i--)
            {
                var entry = boxes[i];
                if (entry == null)
                {
                    continue;
                }

                if (ReferenceEquals(entry, bound)
                    || (_boxes.TryGetId(entry, out var listedId) && listedId == boxNetworkId))
                {
                    compartment.RemoveBox(entry);
                }
            }
        }

        private InteractablePackagingBox_Item FindLiveBox(ShelfCompartment compartment, Guid id)
        {
            if (_boxes.TryGetBox(id, out var box) && box is InteractablePackagingBox_Item item)
            {
                var stored = compartment.GetInteractablePackagingBoxList();
                if (stored != null && stored.Contains(item))
                {
                    return item;
                }
            }

            var boxes = compartment.GetInteractablePackagingBoxList();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                if (boxes[i] != null && _boxes.TryGetId(boxes[i], out var listedId)
                    && listedId == id)
                {
                    return boxes[i];
                }
            }

            return null;
        }

        private InteractablePackagingBox_Item FindStoredLiveBox(ShelfCompartment compartment,
            Guid id)
        {
            var found = FindLiveBox(compartment, id);
            if (found != null)
            {
                return found;
            }

            var top = GetLastBoxOrNull(compartment);
            if (top == null)
            {
                return null;
            }

            if (id == Guid.Empty
                || (_boxes.TryGetId(top, out var topId) && topId == id)
                || !_boxes.TryGetId(top, out _))
            {
                return top;
            }

            return null;
        }

        private bool HasStoredEntry(WarehouseShelf shelf, int compartmentIndex, Guid id)
        {
            if (id == Guid.Empty)
            {
                return false;
            }

            if (_usesRecords)
            {
                // A location entry is not proof that the game record exists: the wire converter
                // pre-registers unknown store ids. The record list is the authoritative store
                // membership on this backend; without this check a store delta was treated as
                // already present and never added a record.
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

            if (WarehouseBoxLocations.TryGetLocation(id, out var location)
                && location.HasStoredLocation && ReferenceEquals(location.Shelf, shelf)
                && location.CompartmentIndex == compartmentIndex)
            {
                return true;
            }

            var ids = new List<Guid>();
            WarehouseBoxLocations.CopyCompartmentOrder(shelf, compartmentIndex, ids);
            return ids.Contains(id);
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

        private int LiveSlotOf(ShelfCompartment compartment, InteractablePackagingBox box)
        {
            var boxes = compartment?.GetInteractablePackagingBoxList();
            if (boxes == null)
            {
                return -1;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                if (ReferenceEquals(boxes[i], box))
                {
                    return i;
                }
            }

            return -1;
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

        private static string PredictionScope(long key)
            => WorldAction.WarehouseScope + ":" + key;

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

        private bool StoreLiveBox(ShelfCompartment compartment, EItemType type, int amount,
            bool isBig, out InteractablePackagingBox_Item stored)
        {
            stored = _boxes.MaterializeWarehouseStored(type, amount, isBig);
            if (stored == null)
            {
                CoopPlugin.Log.LogWarning("Could not materialize an authoritative warehouse box (type="
                    + type + " amount=" + amount + "); skipping the store.");
                return false;
            }

            stored.SetPhysicsEnabled(false);
            stored.DispenseItem(false, compartment);
            if (!stored.m_IsStored)
            {
                _boxes.DestroyWithoutNotification(stored);
                stored = null;
                CoopPlugin.Log.LogWarning("Materialized warehouse box was not stored; skipping.");
                return false;
            }

            return true;
        }

        private static bool StoreLiveBox(ShelfCompartment compartment,
            InteractablePackagingBox_Item box)
        {
            if (box == null)
            {
                return false;
            }

            box.StopLerpToTransform();
            box.SetPhysicsEnabled(false);
            box.DispenseItem(false, compartment);
            return box.m_IsStored;
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

        private static InteractablePackagingBox_Item GetLastBoxOrNull(
            ShelfCompartment compartment)
        {
            if (compartment == null)
            {
                return null;
            }

            var boxes = compartment.GetInteractablePackagingBoxList();
            return boxes == null || boxes.Count == 0 ? null : boxes[boxes.Count - 1];
        }

        private bool CanStore(ShelfCompartment compartment, EItemType itemType, int amount,
            bool isBig)
        {
            return _usesRecords || _usesLiveBoxes
                ? WouldVanillaStore(compartment, itemType, amount, isBig)
                    && IsWarehouse(compartment) && amount <= MaxWarehouseAmount
                : false;
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

        private static bool Validate(WarehouseStoreMessage message)
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
            _usesLiveBoxes = AccessTools.Method(typeof(ShelfCompartment),
                "GetInteractablePackagingBoxList") != null
                && AccessTools.Method(typeof(ShelfCompartment), "GetLastInteractablePackagingBox") != null
                && AccessTools.Method(typeof(ShelfCompartment), "AddBox") != null
                && AccessTools.Method(typeof(ShelfCompartment), "RemoveBox") != null
                && AccessTools.Method(typeof(WarehouseShelf), "GetStorageCompartmentList") != null;
            if (_usesRecords)
            {
                CoopPlugin.Log.LogInfo("World warehouse uses record-backed storage.");
            }
            else if (_recordType == null && _usesLiveBoxes)
            {
                CoopPlugin.Log.LogInfo("World warehouse uses live-box storage.");
            }
            else
            {
                CoopPlugin.Log.LogError("World warehouse disabled: its storage surface is incomplete.");
            }
        }
    }
}
