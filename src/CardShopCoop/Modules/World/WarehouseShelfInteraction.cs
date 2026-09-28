using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Owns warehouse box storage for the World module. The game has two incompatible storage
    /// backends: 1.00 stores records, while the public build stores live box objects. Every
    /// record-only symbol is resolved by reflection so one plugin can run on both builds.
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
        private static FieldInfo _recordItem;
        private static FieldInfo _recordAmount;
        private static FieldInfo _recordBig;

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly BoxNetworkInteraction _boxes;
        private readonly PlayerBoxInteraction _playerBox;
        // The stable id of each stored record, indexed exactly as the compartment's record stack.
        // It is a pure mirror of the game list: only the AddStoredBoxRecord / TryPopLastStoredBoxRecord
        // / ArrangeBoxItemBasedOnItemCount hooks change it (plus the host-only lazy fill for
        // save-loaded records). The id is assigned when the record is created - the box's
        // creator-assigned id on the host, the store intent / authoritative delta id on the client.
        private readonly Dictionary<long, List<Guid>> _recordIds = new();
        // Both peers, records backend: the item box whose OnFinishLerp is currently running. A local
        // (game-driven) store adds its record from inside that method, so this is the exact box that
        // owns the next AddStoredBoxRecord. Binding here - rather than staging an id before the
        // game's own gates - means an id can never be stranded on a store the game refused and then
        // handed to the following store. The prefix sets it and the postfix clears it, so it is
        // non-null only for the synchronous record add it belongs to, never across frames.
        private InteractablePackagingBox_Item _currentStoreBox;
        // Programmatic add: authoritative ids for the records the next AddStoredBoxRecord calls add
        // while applying a host delta/baseline (client) or a store command (host). Kept apart from a
        // local store so a delayed local store can never hand its id to a programmatic add that
        // lands first.
        private readonly Dictionary<long, Queue<Guid>> _pendingApplyRecordIds = new();
        private WarehouseStateMessage _pendingClientState;
        private readonly List<WarehouseDeltaMessage> _pendingClientDeltas = new();
        private bool _hostApplyingCommand;

        internal bool IsApplyingRemote
        {
            get; private set;
        }

        internal WarehouseShelfInteraction(bool host, Action<INetMessage> broadcast,
            Action<int, INetMessage> send, BoxNetworkInteraction boxes,
            PlayerBoxInteraction playerBox = null)
        {
            _host = host;
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            // Warehouse state is broadcast, never sent to one connection, so the send channel is
            // validated for symmetry with the other transports but is not retained.
            _ = send ?? throw new ArgumentNullException(nameof(send));
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _playerBox = playerBox;
            Probe();
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
                // The warehouse take prediction owns this hold; do not create a second one.
                _playerBox.TakeCoveredIntoLocalHand(box);
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                box.StartHoldBox(true, controller.m_HoldItemPos);
            }
        }

        internal static bool Available()
        {
            Probe();
            return _recordType != null ? _usesRecords : _usesLiveBoxes;
        }

        internal static bool UsesRecords
        {
            get
            {
                Probe();
                return _usesRecords;
            }
        }

        internal void Reset()
        {
            IsApplyingRemote = false;
            _recordIds.Clear();
            _currentStoreBox = null;
            _pendingApplyRecordIds.Clear();
            _pendingClientState = null;
            _pendingClientDeltas.Clear();
            _hostApplyingCommand = false;
        }

        internal void FlushClientState()
        {
            if (_pendingClientState != null)
            {
                var pending = _pendingClientState;
                _pendingClientState = null;
                ClientApplyState(pending);
            }

            if (_pendingClientDeltas.Count == 0)
            {
                return;
            }

            // A delta can arrive before the world streams the compartment it names. Keep it (in
            // order) and replay once the scene is ready, instead of throwing and forcing the host
            // to drop the guest from the session.
            var deltas = new List<WarehouseDeltaMessage>(_pendingClientDeltas);
            _pendingClientDeltas.Clear();
            for (var i = 0; i < deltas.Count; i++)
            {
                ClientApplyDelta(deltas[i]);
            }
        }

        /// <summary>Builds one frozen warehouse snapshot for a resumable host baseline.</summary>
        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null || !Available())
            {
                return;
            }

            append(BuildFullState());
        }

        /// <summary>Client: registers a store intent for an action the game's own
        /// <c>DispenseItem</c> already performed. The task tracking has already been updated by
        /// the caller; the prediction only records how to replay/undo the change.</summary>
        private void RegisterClientStorePrediction(ShelfCompartment compartment,
            WarehouseBoxState state)
        {
            if (state == null || state.BoxNetworkId == Guid.Empty
                || !TryGetAddress(compartment, out var shelfIndex, out var compartmentIndex))
            {
                return;
            }

            var intent = new WarehouseStoreMessage
            {
                BoxNetworkId = state.BoxNetworkId,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                ItemType = state.ItemType,
                Amount = state.Amount,
                IsBig = state.IsBig,
            };
            var delta = new WarehouseDeltaMessage
            {
                IsStore = true,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                BoxNetworkId = state.BoxNetworkId,
                ItemType = state.ItemType,
                Amount = state.Amount,
                IsBig = state.IsBig,
            };
            WorldPrediction.Predict(WorldPrediction.WarehouseScope, intent,
                () => ClientApplyDelta(delta),
                () => ClientUndoStore(delta));
        }

        /// <summary>Both peers, records backend: marks the box whose <c>OnFinishLerp</c> is running.
        /// The game adds that box's stored record from inside the method, so the AddStoredBoxRecord
        /// hook binds this box's network id to the new record. Runs at the very start of the method,
        /// before the record is added, and is cleared by <see cref="EndLocalRecordStore"/> at the end.</summary>
        internal void BeginLocalRecordStore(InteractablePackagingBox_Item box)
        {
            _currentStoreBox = _usesRecords ? box : null;
        }

        internal void EndLocalRecordStore()
        {
            _currentStoreBox = null;
        }

        /// <summary>Reverts a rejected store prediction locally: the optimistic store moved the
        /// player's own live box into the compartment, so the inverse removes that same box and
        /// returns it to the player's hand. It must not run the warehouse take path, which
        /// materializes a duplicate and emits a player-box pickup the host never asked for.
        /// The prediction rollback runs inside the prediction reconcile, so the game's own hold
        /// cannot be mistaken for a fresh local pickup.</summary>
        private void ClientUndoStore(WarehouseDeltaMessage delta)
        {
            var compartment = ResolveCompartment(delta.ShelfIndex, delta.CompartmentIndex);
            if (compartment == null)
            {
                return;
            }

            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                if (count == 0 || GetRecordId(compartment, count - 1) != delta.BoxNetworkId)
                {
                    return;
                }

                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                {
                    return;
                }
            }
            else if (_boxes.TryGetBox(delta.BoxNetworkId, out var stored)
                && stored is InteractablePackagingBox_Item item)
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                if (boxes != null && boxes.Contains(item))
                {
                    compartment.RemoveBox(item);
                }
            }

            if (!_boxes.ClientEnsureWarehouseTake(delta.BoxNetworkId, delta.ItemType, delta.Amount,
                delta.IsBig, out var held))
            {
                CoopPlugin.Log.LogWarning("[warehouse] store rollback could not restore box ID "
                    + delta.BoxNetworkId + "; skipping the hold.");
                return;
            }

            TakeIntoLocalHand(held);
        }

        /// <summary>Pre-action state captured before the game's own
        /// <c>InteractableStorageCompartment.OnMouseButtonUp</c> takes a warehouse box. The state
        /// is read while the box is definitely the top box, because a rollback/replay later cannot
        /// re-read a stack order that has changed.</summary>
        internal sealed class WarehouseTakeCapture
        {
            internal ShelfCompartment Compartment;
            internal int ShelfIndex;
            internal int CompartmentIndex;
            internal Guid BoxNetworkId;
            internal WarehouseBoxState State;
            internal int RecordCount;
            internal bool Covered;
        }

        /// <summary>Client: captures the top warehouse box before the game's own take and covers
        /// the hold the vanilla take produces, so one physical take maps to exactly one
        /// prediction. Returns null for anything the warehouse protocol does not own (or cannot
        /// resolve), which leaves vanilla to run alone with no forwarding.</summary>
        internal WarehouseTakeCapture CaptureClientTake(InteractableStorageCompartment storage)
        {
            if (_host || IsApplyingRemote || !Available() || storage == null)
            {
                return null;
            }

            var compartment = storage.GetShelfCompartment();
            if (!IsWarehouse(compartment) || !TryGetAddress(compartment, out var shelfIndex,
                    out var compartmentIndex))
            {
                return null;
            }

            if (!TryGetTopBoxNetworkId(compartment, out var boxNetworkId)
                || !TryReadTopClientBox(compartment, boxNetworkId, out var state))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take could not resolve the top box on shelf ["
                    + shelfIndex + "," + compartmentIndex + "]; leaving the box in place. top="
                    + (GetLastBoxOrNull(compartment)?.name ?? "none") + " localHeld="
                    + (_playerBox?.LocalHeldBoxId ?? Guid.Empty) + ".");
                return null;
            }

            _playerBox?.BeginCoveredHold();
            return new WarehouseTakeCapture
            {
                Compartment = compartment,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                BoxNetworkId = boxNetworkId,
                State = state,
                RecordCount = RecordCount(compartment),
                Covered = true,
            };
        }

        /// <summary>Client: after the game's own take ran, registers the take intent when the top
        /// box actually left the shelf. The game already removed it and put it in the hand, so
        /// only the mod's bookkeeping is updated here; the apply/undo closures cover replays.</summary>
        internal void ObserveClientTake(WarehouseTakeCapture capture)
        {
            if (capture?.Compartment == null)
            {
                return;
            }

            if (!DidClientTakeHappen(capture))
            {
                return;
            }

            if (_usesRecords)
            {
                // The game's own take already popped the record, and the TryPopLastStoredBoxRecord
                // hook trimmed the mirror for it; the take must not trim again.
                if (_playerBox?.LocalHeldBox is InteractablePackagingBox_Item spawned)
                {
                    // The records backend spawns a fresh live box for the take. Bind it to the
                    // record's id so later place/throw actions forward under it.
                    _boxes.BindStoredLiveBox(capture.BoxNetworkId, spawned, capture.State);
                }
                else
                {
                    CoopPlugin.Log.LogWarning("[warehouse] records take box=" + capture.BoxNetworkId
                        + " has no live box in hand; later place/throw may not sync.");
                }
            }

            var takeIntent = new WarehouseTakeMessage
            {
                BoxNetworkId = capture.BoxNetworkId,
                ShelfIndex = capture.ShelfIndex,
                CompartmentIndex = capture.CompartmentIndex,
            };
            var takeDelta = new WarehouseDeltaMessage
            {
                ShelfIndex = capture.ShelfIndex,
                CompartmentIndex = capture.CompartmentIndex,
                BoxNetworkId = capture.BoxNetworkId,
                ItemType = capture.State.ItemType,
                Amount = capture.State.Amount,
                IsBig = capture.State.IsBig,
            };
            WorldPrediction.Predict(WorldPrediction.WarehouseScope, takeIntent,
                () => ClientTakeIntoHand(takeDelta, capture.Compartment),
                () => ClientUndoTakePrediction(takeDelta));
        }

        /// <summary>Releases the covered hold the take prefix began. Safe to call once; the
        /// capture records that the cover was taken.</summary>
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

            return _playerBox != null && _playerBox.LocalHeldBoxId == capture.BoxNetworkId;
        }

        /// <summary>Moves a taken box from the shelf into the local player's hand. The live box
        /// that was stored is the box being taken, so it is moved directly; only the record
        /// backend needs a fresh live representation.</summary>
        private void ClientTakeIntoHand(WarehouseDeltaMessage delta, ShelfCompartment compartment)
        {
            // A destroyed/unstreamed compartment (Unity's == on a destroyed object) has no shelf to
            // take from; skip instead of throwing, which would tear the session down on a race.
            if (compartment == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] take for box=" + delta.BoxNetworkId
                    + " references a destroyed compartment; skipping.");
                return;
            }

            if (_usesRecords)
            {
                PopClientWarehouseRecord(compartment, delta.BoxNetworkId);
            }
            else if (_boxes.TryGetBox(delta.BoxNetworkId, out var stored)
                && stored is InteractablePackagingBox_Item live)
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                var inCompartment = boxes != null && boxes.Contains(live);
                if (inCompartment)
                {
                    compartment.RemoveBox(live);
                }
            }
            else
            {
                CoopPlugin.Log.LogWarning("[warehouse] client take box=" + delta.BoxNetworkId
                    + " had no bound live box; materializing a replacement (the shelf may keep its "
                    + "original, which then fails later takes).");
            }

            if (!_boxes.ClientEnsureWarehouseTake(delta.BoxNetworkId, delta.ItemType, delta.Amount,
                delta.IsBig, out var box))
            {
                CoopPlugin.Log.LogWarning("[warehouse] take could not materialize box ID "
                    + delta.BoxNetworkId + "; skipping the hold.");
                return;
            }

            TakeIntoLocalHand(box);
        }

        /// <summary>Applies the authoritative result of OUR OWN accepted take during a queue
        /// reconcile. The box belongs in our hand, so unlike <see cref="ClientApplyRemoteTake"/>
        /// (another player's take) this attaches it locally. That is what restores the hold when a
        /// faster later action forced the reconcile to undo the optimistic take first.</summary>
        internal void ClientTakeOwnConfirmed(WarehouseDeltaMessage message)
        {
            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            if (compartment == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] own take confirm could not resolve shelf ["
                    + message.ShelfIndex + "," + message.CompartmentIndex + "] box="
                    + message.BoxNetworkId + ".");
                return;
            }

            ClientTakeIntoHand(message, compartment);
        }

        /// <summary>Applies another player's take on this peer: the box leaves our shelf, but only
        /// the taker ends up holding it. Their pickup broadcast attaches the live box to their
        /// avatar, so we must not start a local hold here.</summary>
        private void ClientApplyRemoteTake(WarehouseDeltaMessage message,
            ShelfCompartment compartment)
        {
            if (_usesRecords)
            {
                PopClientWarehouseRecord(compartment, message.BoxNetworkId);
                if (!_boxes.ClientEnsureWarehouseTake(message.BoxNetworkId, message.ItemType,
                        message.Amount, message.IsBig, out _))
                {
                    // The box left the shelf already; failing to materialize its live replacement
                    // is logged and skipped rather than thrown (a throw here disconnects the guest).
                    CoopPlugin.Log.LogWarning("[warehouse] remote take could not materialize box ID "
                        + message.BoxNetworkId + ".");
                }

                return;
            }

            if (_boxes.TryGetBox(message.BoxNetworkId, out var stored)
                && stored is InteractablePackagingBox_Item live)
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                if (boxes != null && boxes.Contains(live))
                {
                    compartment.RemoveBox(live);
                }
            }
        }

        private static readonly Vector3 ParkedWorkerBoxPosition = new Vector3(10000f, 10000f, 10000f);

        /// <summary>Client: a worker is carrying world box <paramref name="boxNetworkId"/>. Detach
        /// it from any warehouse compartment and park the real object out of view; the Npc worker
        /// prop draws the carried box. The id binding is kept so a later store reuses this same
        /// object, and the host's warehouse removal delta still fixes the compartment listing.</summary>
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

            // The worker now carries it, exactly as if StartHoldBox had run on the host: the box
            // is no longer stored. Leaving this true kept OnFinishLerp arranging a compartment it
            // had left and made the drop-restore skip the box.
            item.m_IsStored = false;
            item.SetPhysicsEnabled(false);
            item.transform.position = ParkedWorkerBoxPosition;
        }

        /// <summary>Client: the worker put this box down. Its authoritative pose has already been
        /// re-announced and applied by the box channel; make it a live physics object again so it
        /// rests where it was dropped instead of staying parked off-map.</summary>
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

        private void ClientUndoTakePrediction(WarehouseDeltaMessage delta)
        {
            if (delta == null)
                return;

            // The optimistic take may still be lerping the box into the hand, and the game's
            // store path refuses a box that is lerping (CanPickup). Stop that lerp so the same
            // box can go straight back onto the shelf.
            if (!_usesRecords && _boxes.TryGetBox(delta.BoxNetworkId, out var box))
            {
                box.StopLerpToTransform();
            }

            // A destroyed compartment has nowhere to restore the box, and ClientApplyDelta would
            // otherwise keep the store deferred forever. Log and drop it.
            if (ResolveCompartment(delta.ShelfIndex, delta.CompartmentIndex) == null)
            {
                CoopPlugin.Log.LogWarning("[warehouse] take undo for box=" + delta.BoxNetworkId
                    + " has no live compartment [" + delta.ShelfIndex + "," + delta.CompartmentIndex
                    + "]; skipping.");
                return;
            }

            ClientApplyDelta(new WarehouseDeltaMessage
            {
                IsStore = true,
                ShelfIndex = delta.ShelfIndex,
                CompartmentIndex = delta.CompartmentIndex,
                BoxNetworkId = delta.BoxNetworkId,
                ItemType = delta.ItemType,
                Amount = delta.Amount,
                IsBig = delta.IsBig,
            });
        }

        internal void OnStoredBoxRecordAdded(ShelfCompartment compartment)
        {
            if (compartment == null)
            {
                return;
            }

            // Pure mirror: every AddStoredBoxRecord appends exactly one id, on both peers. The id
            // comes from the box whose record was just added (a local store) or from the prepared
            // authoritative queue (a programmatic apply); it is never minted positionally here.
            var id = AppendAddedRecordId(compartment);

            if (_host)
            {
                WarehouseBoxState state = null;
                if (TryReadRecord(compartment, RecordCount(compartment) - 1, out state))
                {
                    state.BoxNetworkId = id;
                }

                if (!_hostApplyingCommand)
                {
                    BroadcastWarehouseDelta(compartment, true, state);
                }

                return;
            }

            if (!IsApplyingRemote && Available() && IsWarehouse(compartment))
            {
                ClientObserveRecordStore(compartment, id);
            }
        }

        /// <summary>Client records backend: the game stored the local player's box as a record.
        /// Pair the new record with the captured box id, update mod tracking, and register the
        /// store intent. The live box is unbound (and its destroy suppressed) by
        /// <see cref="BoxNetworkInteraction.ClientConvertToStoredRecord"/>.</summary>
        private void ClientObserveRecordStore(ShelfCompartment compartment, Guid id)
        {
            var count = RecordCount(compartment);
            if (count == 0 || !TryReadRecord(compartment, count - 1, out var state, false))
            {
                return;
            }

            if (id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[warehouse] client record store had no captured box for shelf ["
                    + compartment.GetWarehouseIndex() + "," + compartment.GetIndex() + "].");
                return;
            }

            state.BoxNetworkId = id;
            _boxes.ClientConvertToStoredRecord(id);
            RegisterClientStorePrediction(compartment, state);
        }

        /// <summary>Host: the state of the record at the top of the compartment, read before the
        /// game pops it. The pop postfix has no record left to read, so the state must be captured
        /// here from the game rather than from a cached copy.</summary>
        internal WarehouseBoxState CapturePoppedRecord(ShelfCompartment compartment)
        {
            if (!_host || !_usesRecords || compartment == null)
            {
                return null;
            }

            var count = RecordCount(compartment);
            return count > 0 && TryReadRecord(compartment, count - 1, out var state, false)
                ? state : null;
        }

        internal void OnStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop,
            WarehouseBoxState removed)
        {
            if (!didPop || compartment == null)
            {
                return;
            }

            // Pure mirror: remove the popped id on BOTH peers, whatever triggered the pop (player
            // take, worker/restock, or a remote apply). The old host-only guard let a client-side
            // worker pop drift the list.
            var id = RemovePoppedRecordId(compartment);

            if (_host && !_hostApplyingCommand && id != Guid.Empty)
            {
                // The game only ever pops a stored record in order to respawn it as a live box in
                // the same call: a player take in InteractableStorageCompartment.OnMouseButtonUp,
                // or a worker take via RestockManager.MaterializeStoredCandidate. Push the popped
                // record's id so that spawn binds it instead of minting a second id; otherwise the
                // client materializes the take delta's box AND the fresh BoxCreated, giving it two
                // boxes. HostApplyTake already pushed before it ran and is excluded by
                // _hostApplyingCommand.
                if (!_boxes.PushHostCreatedId(id))
                {
                    // No client intent to reject here: this pop is observed host-side (a host
                    // take, a worker take, or a save load). A non-fresh id means the record mirror
                    // and the live box engine disagree; the game's own respawn will mint a fresh
                    // host id via EnsureHostId rather than rebind a live box, so the box still
                    // comes back. Logged so the disagreement is visible instead of a silent remint.
                    CoopPlugin.Log.LogWarning("[warehouse] popped record id=" + id
                        + " is not a fresh creator id; the respawned box will mint a new host id.");
                }

                if (removed != null)
                {
                    removed.BoxNetworkId = id;
                    BroadcastWarehouseDelta(compartment, false, removed);
                }
            }
        }

        internal void OnBoxAdded(ShelfCompartment compartment)
        {
            // Always record the box so a later removal (for example the host taking it off the
            // shelf) is detectable and broadcast. Only a command being applied suppresses the
            // store broadcast, because HostApplyStore already sends that delta.
            if (!TryGetLastLiveBoxState(compartment, out var added))
                return;
            if (!_host && !IsApplyingRemote && Available() && IsWarehouse(compartment))
            {
                // The local player's own store: the game already placed the box. Bind the live
                // object and forward the intent; never re-run the game mutation. The host's own
                // store needs no stored marker: the box is a member of the compartment now, and
                // PlayerBoxInteraction's guard reads that directly from the game.
                var box = GetLastBoxOrNull(compartment);
                if (box != null)
                {
                    _boxes.BindStoredLiveBox(added.BoxNetworkId, box, added);
                }

                RegisterClientStorePrediction(compartment, added);
            }

            if (!_hostApplyingCommand)
                BroadcastWarehouseDelta(compartment, true, added);
        }

        internal void OnBoxRemoved(ShelfCompartment compartment, InteractablePackagingBox_Item box)
        {
            if (_hostApplyingCommand || !_host || IsApplyingRemote || !Available()
                || !IsWarehouse(compartment) || box == null)
            {
                return;
            }

            // Broadcast exactly the box that left the compartment. The previous diff-based lookup
            // could report a stale, already-removed id (for example after a suppressed player
            // take), and the client then removed the wrong object from its shelf.
            if (!_boxes.TryGetId(box, out var id) || id == Guid.Empty)
            {
                return;
            }

            var state = new WarehouseBoxState
            {
                BoxNetworkId = id,
                ItemType = box.m_ItemCompartment == null
                    ? EItemType.None : box.m_ItemCompartment.GetItemType(),
                Amount = box.m_ItemCompartment == null ? 0 : box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
            };

            CoopPlugin.Log.LogInfo("[warehouse] remove id=" + id + " shelf="
                + compartment.GetWarehouseIndex() + ":" + compartment.GetIndex());
            BroadcastWarehouseDelta(compartment, false, state);
        }

        internal bool HostApplyStore(WarehouseStoreMessage message, int connectionId,
            Action<INetMessage> response = null)
        {
            if (!_host || !Validate(message))
            {
                return false;
            }

            var accepted = false;
            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            var storeBox = _boxes.TryGetBox(message.BoxNetworkId, out var storePhysical)
                ? storePhysical as InteractablePackagingBox_Item : null;
            if (storeBox == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: unknown box ID "
                    + message.BoxNetworkId + ".");
            }
            else if (storeBox.m_ItemCompartment == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: box ID " + message.BoxNetworkId
                    + " has no item compartment.");
            }
            else if (storeBox.m_ItemCompartment.GetItemType() != message.ItemType
                     || storeBox.m_ItemCompartment.GetItemCount() != message.Amount
                     || storeBox.m_IsBigBox != message.IsBig)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: payload disagrees with box ID "
                    + message.BoxNetworkId + ".");
            }
            else if (IsAlreadyShelved(compartment, storeBox, message.BoxNetworkId))
            {
                // The box is already on a warehouse shelf (or already stored by our own model), so
                // it is not in a hand. A store for it is a stale intent from a guest whose
                // optimistic state lagged the host; re-dispensing would list the SAME box in a
                // second compartment and inflate the slot (the host showed 128 where the guest
                // showed 64). Rejecting keeps the host authoritative and the slot count correct.
                CoopPlugin.Log.LogWarning("Warehouse store rejected: box ID " + message.BoxNetworkId
                    + " is already stored.");
            }
            else if (CanStore(compartment, message.ItemType, message.Amount, message.IsBig))
            {
                try
                {
                    _hostApplyingCommand = true;
                    if (_usesRecords)
                    {
                        PrepareAppliedRecordId(compartment, message.BoxNetworkId);
                        accepted = AddRecord(compartment, message.ItemType, message.Amount,
                            message.IsBig);
                        if (accepted)
                        {
                            storeBox.OnDestroyed();
                        }
                    }
                    else
                    {
                        // Idempotent: a box already listed in the compartment must not be added
                        // again, which would inflate the slot count with a duplicate entry. Leave
                        // its in-flight lerp alone - stopping it here without re-dispensing froze
                        // the box mid-flight on the host too.
                        var stored = compartment.GetInteractablePackagingBoxList();
                        if (stored != null && stored.Contains(storeBox))
                        {
                            accepted = true;
                        }
                        else
                        {
                            // The guest's own hold can leave the box lerping into a hand; the
                            // game's store refuses a lerping box, so settle it before storing.
                            accepted = StoreLiveBox(compartment, storeBox);
                        }
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
                CoopPlugin.Log.LogWarning("Warehouse store rejected: shelf [" + message.ShelfIndex
                    + "," + message.CompartmentIndex + "] will not take box "
                    + message.BoxNetworkId + " (" + message.ItemType + " x" + message.Amount
                    + ", big=" + message.IsBig + ").");
            }

            if (!accepted)
                return false;

            BroadcastDelta(new WarehouseDeltaMessage
            {
                PredictionId = message.PredictionId,
                IsStore = true,
                ShelfIndex = message.ShelfIndex,
                CompartmentIndex = message.CompartmentIndex,
                BoxNetworkId = message.BoxNetworkId,
                ItemType = message.ItemType,
                Amount = message.Amount,
                IsBig = message.IsBig,
            });
            return accepted;
        }

        internal bool HostApplyTake(WarehouseTakeMessage message, int connectionId,
            Action<INetMessage> response = null)
        {
            if (!_host || message == null || message.BoxNetworkId == Guid.Empty)
            {
                return false;
            }

            var result = new WarehouseBoxState
            {
                BoxNetworkId = message.BoxNetworkId,
            };
            var accepted = false;
            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            var topId = Guid.Empty;
            var hasTop = compartment != null && TryGetTopBoxNetworkId(compartment, out topId);
            var topBox = GetLastBoxOrNull(compartment);
            if (hasTop && topId == message.BoxNetworkId)
            {
                // The record/live box's id moves to the freshly spawned live box. Validate and
                // park it BEFORE the game's take mutates the shelf: a non-fresh id (already bound
                // to another live box or parked for another creation) must reject the intent
                // instead of letting the host remint, which would desync the taker's locally
                // pre-bound box. topBox is the stored live box the id is moving off of (null on
                // the record backend), which the freshness check must tolerate. Parking first also
                // means a refused take can release the id again rather than strand it on a later,
                // unrelated creation.
                if (!_boxes.PushHostCreatedId(message.BoxNetworkId, topBox))
                {
                    CoopPlugin.Log.LogWarning("[warehouse] host take rejected: box id "
                        + message.BoxNetworkId + " is not a fresh creator id.");
                    return false;
                }

                try
                {
                    _hostApplyingCommand = true;
                    accepted = _usesRecords
                        ? TryTakeRecord(compartment, result)
                        : TryTakeLiveBox(compartment, result);
                    if (accepted)
                    {
                        RestockManager.SpawnPackageBoxItem(result.ItemType, result.Amount,
                            result.IsBig);
                    }
                    else
                    {
                        _boxes.CancelHostCreatedId(message.BoxNetworkId);
                    }
                }
                catch (Exception e)
                {
                    _boxes.CancelHostCreatedId(message.BoxNetworkId);
                    CoopPlugin.Log.LogError("Warehouse take failed: " + e);
                    throw;
                }
                finally
                {
                    _hostApplyingCommand = false;
                }
            }

            if (!accepted)
            {
                CoopPlugin.Log.LogWarning("[warehouse] host take REJECTED pred=" + message.PredictionId
                    + " box=" + message.BoxNetworkId + " hasTop=" + hasTop + " top=" + topId
                    + " canPickup=" + (topBox != null && topBox.CanPickup()) + ".");
                return false;
            }

            BroadcastDelta(new WarehouseDeltaMessage
            {
                PredictionId = message.PredictionId,
                IsStore = false,
                ShelfIndex = message.ShelfIndex,
                CompartmentIndex = message.CompartmentIndex,
                BoxNetworkId = result.BoxNetworkId,
                ItemType = result.ItemType,
                Amount = result.Amount,
                IsBig = result.IsBig,
            });
            // The taker's own pickup for this take is a covered forward that raced ahead of the
            // box's creation and was dropped; announce the grant so the host mirrors the hold and
            // every observer attaches the taken box to the taker's avatar.
            _playerBox?.AnnounceGrantedHold(result.BoxNetworkId, connectionId);
            return accepted;
        }

        internal void ClientApplyState(WarehouseStateMessage message)
        {
            if (message == null)
                throw new InvalidOperationException("Authoritative warehouse state is missing.");

            if (!Available())
            {
                _pendingClientState = message;
                return;
            }

            IsApplyingRemote = true;
            try
            {
                for (var i = 0; i < message.Compartments.Count; i++)
                {
                    var entry = message.Compartments[i];
                    var compartment = ResolveCompartment(entry.ShelfIndex, entry.CompartmentIndex);
                    if (compartment == null)
                    {
                        _pendingClientState = message;
                        return;
                    }
                }

                for (var i = 0; i < message.Compartments.Count; i++)
                {
                    var entry = message.Compartments[i];
                    var compartment = ResolveCompartment(entry.ShelfIndex, entry.CompartmentIndex);

                    ApplyState(compartment, entry.Boxes);
                }
            }
            finally
            {
                IsApplyingRemote = false;
            }

        }

        internal void ClientApplyDelta(WarehouseDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            if (compartment == null)
            {
                // The world has not streamed this compartment yet. Defer and replay on the world
                // ready hook; throwing here would disconnect the guest through the reliable
                // handler failure path on a transient timing mismatch.
                CoopPlugin.Log.LogWarning("[warehouse] deferring delta for unresolved compartment ["
                    + message.ShelfIndex + "," + message.CompartmentIndex + "] box="
                    + message.BoxNetworkId + " isStore=" + message.IsStore + ".");
                _pendingClientDeltas.Add(message);
                return;
            }

            if (message.IsStore)
            {
                ClientApplyStoreDelta(message, compartment);
                return;
            }

            // A take that did not confirm our own prediction belongs to another player. The
            // origin already moved the box into its own hand optimistically, so here we only
            // make our shelf match; taking it into our own hand would publish a pickup request
            // from every observer and misattribute the holder.
            ClientApplyRemoteTake(message, compartment);
        }

        private void ClientApplyStoreDelta(WarehouseDeltaMessage message,
            ShelfCompartment compartment)
        {
            IsApplyingRemote = true;
            try
            {
                if (_usesRecords)
                {
                    // Our own accepted store already added this record optimistically; the
                    // authoritative echo only confirms it, so re-adding would duplicate the box.
                    // The mirror is the game's record list by id, so a present id means it is
                    // already there.
                    if (HasRecordId(compartment, message.BoxNetworkId))
                        return;

                    // Hand the authoritative id to the AddStoredBoxRecord hook, which appends it
                    // to the mirror as the record is added.
                    PrepareAppliedRecordId(compartment, message.BoxNetworkId);
                    AddRecord(compartment, message.ItemType, message.Amount, message.IsBig);
                    _boxes.ClientConvertToStoredRecord(message.BoxNetworkId);
                }
                else if (_boxes.TryGetBox(message.BoxNetworkId, out var box)
                    && box is InteractablePackagingBox_Item item)
                {
                    // Idempotent: our own accepted store already ran the game's own DispenseItem,
                    // which started this box's lerp into the slot. Stopping the lerp without a
                    // second DispenseItem (which would early-return for a box already in the
                    // compartment) froze the box in mid-air - the "not fully put onto the shelf"
                    // look. Only settle and store when this peer has not placed the box yet.
                    var stored = compartment.GetInteractablePackagingBoxList();
                    var already = stored != null && stored.Contains(item);
                    if (!already)
                    {
                        // A just-taken box may still be lerping into the hand; the game's store
                        // path refuses a lerping box, so settle it first, exactly as the take
                        // rollback does.
                        item.StopLerpToTransform();
                        item.DispenseItem(false, compartment);
                    }

                    _boxes.BindStoredLiveBox(message.BoxNetworkId, item,
                        new WarehouseBoxState
                        {
                            BoxNetworkId = message.BoxNetworkId,
                            ItemType = message.ItemType,
                            Amount = message.Amount,
                            IsBig = message.IsBig,
                        });
                }
                else
                {
                    CoopPlugin.Log.LogWarning("[warehouse] client store box=" + message.BoxNetworkId
                        + " had no bound live box; it could not be placed on the shelf.");
                }
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }

        /// <summary>Pops the record a client take targets. Only the record backend keeps its boxes
        /// as records; the live backend moves the stored box itself (see
        /// <see cref="ClientTakeIntoHand"/> and <see cref="ClientApplyRemoteTake"/>).</summary>
        private void PopClientWarehouseRecord(ShelfCompartment compartment, Guid boxNetworkId)
        {
            if (compartment == null)
                return;

            IsApplyingRemote = true;
            try
            {
                var count = RecordCount(compartment);
                if (count == 0 || GetRecordId(compartment, count - 1) != boxNetworkId)
                    return;
                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                    throw new InvalidOperationException("Warehouse record take could not be applied.");
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }


        private WarehouseStateMessage BuildFullState()
        {
            var message = new WarehouseStateMessage();
            var compartments = GetWarehouseCompartments();
            for (var i = 0; i < compartments.Count; i++)
            {
                message.Compartments.Add(BuildState(compartments[i]));
            }

            return message;
        }

        private void BroadcastDelta(WarehouseDeltaMessage message)
        {
            if (!_host || message == null)
                return;

            _broadcast(message);
        }

        private void BroadcastWarehouseDelta(ShelfCompartment compartment, bool isStore,
            WarehouseBoxState state)
        {
            if (!_host || IsApplyingRemote || !Available() || !IsWarehouse(compartment)
                || state == null || state.BoxNetworkId == Guid.Empty)
            {
                return;
            }

            BroadcastDelta(new WarehouseDeltaMessage
            {
                IsStore = isStore,
                ShelfIndex = compartment.GetWarehouseIndex(),
                CompartmentIndex = compartment.GetIndex(),
                BoxNetworkId = state.BoxNetworkId,
                ItemType = state.ItemType,
                Amount = state.Amount,
                IsBig = state.IsBig,
            });
        }

        private bool TryGetLastLiveBoxState(ShelfCompartment compartment,
            out WarehouseBoxState state)
        {
            state = null;
            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null || !_boxes.TryGetId(box, out var id))
                return false;
            state = new WarehouseBoxState
            {
                BoxNetworkId = id,
                ItemType = box.m_ItemCompartment.GetItemType(),
                Amount = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
            };
            return true;
        }

        private WarehouseCompartmentState BuildState(ShelfCompartment compartment)
        {
            var state = new WarehouseCompartmentState
            {
                StableEntityId = WorldMessageMetadata.WarehouseEntityId(
                    compartment.GetWarehouseIndex(), compartment.GetIndex()),
                ShelfIndex = compartment.GetWarehouseIndex(),
                CompartmentIndex = compartment.GetIndex(),
            };
            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                for (var i = 0; i < count; i++)
                {
                    if (TryReadRecord(compartment, i, out var box))
                    {
                        box.BoxNetworkId = GetRecordId(compartment, i);
                        state.Boxes.Add(box);
                    }
                }
            }
            else
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                if (boxes != null)
                {
                    for (var i = 0; i < boxes.Count; i++)
                    {
                        var box = boxes[i];
                        if (box?.m_ItemCompartment == null)
                        {
                            continue;
                        }

                        var amount = box.m_ItemCompartment.GetItemCount();
                        if (amount > 0)
                        {
                            var storedState = new WarehouseBoxState
                            {
                                BoxNetworkId = _boxes.EnsureHostId(box),
                                ItemType = box.m_ItemCompartment.GetItemType(),
                                Amount = amount,
                                IsBig = box.m_IsBigBox,
                            };
                            state.Boxes.Add(storedState);
                        }
                    }
                }
            }

            return state;
        }

        private void ApplyState(ShelfCompartment compartment,
            List<WarehouseBoxState> wanted)
        {
            if (_usesRecords)
            {
                // Clearing pops every record, and the pop hook trims the mirror; re-adding then
                // appends the authoritative ids through the add hook. The mirror is therefore
                // rebuilt from the hooks alone, never overwritten wholesale.
                while (RecordCount(compartment) > 0)
                {
                    var args = new object[] { null };
                    if (!(bool)_recordPop.Invoke(compartment, args))
                        throw new InvalidOperationException("Warehouse record storage could not be cleared.");
                }

                for (var i = 0; i < wanted.Count; i++)
                {
                    var box = wanted[i];
                    PrepareAppliedRecordId(compartment, box.BoxNetworkId);
                    AddRecord(compartment, box.ItemType, box.Amount, box.IsBig);
                }

                _recordRebuild?.Invoke(null, new object[] { compartment });
                for (var i = 0; i < wanted.Count; i++)
                {
                    _boxes.ClientConvertToStoredRecord(wanted[i].BoxNetworkId);
                }

                return;
            }

            var existing = compartment.GetInteractablePackagingBoxList();
            if (existing != null)
            {
                var copy = new List<InteractablePackagingBox_Item>(existing);
                for (var i = 0; i < copy.Count; i++)
                {
                    if (_boxes.TryGetId(copy[i], out var previousId))
                    {
                        _boxes.ClientForgetPhysical(previousId, copy[i]);
                    }

                    // ClientForgetPhysical destroys through the box engine, which detaches a
                    // stored box; only remove here if the compartment still lists it.
                    if (existing.Contains(copy[i]))
                    {
                        compartment.RemoveBox(copy[i]);
                    }

                    if (copy[i] != null)
                    {
                        UnityEngine.Object.Destroy(copy[i].gameObject);
                    }
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                var box = wanted[i];
                if (StoreLiveBox(compartment, box.ItemType, box.Amount, box.IsBig, out var spawned))
                {
                    _boxes.BindStoredLiveBox(box.BoxNetworkId, spawned, box);
                }
            }
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
                UnityEngine.Object.Destroy(stored.gameObject);
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

            // A box still lerping (a take immediately followed by a store) makes the game's own
            // DispenseItem early-return, which silently refused a valid store; settle it first so
            // the store really runs and the box lerps into its slot from here.
            box.StopLerpToTransform();
            box.SetPhysicsEnabled(false);
            box.DispenseItem(false, compartment);
            return box.m_IsStored;
        }

        /// <summary>True when this box is already on a warehouse shelf (so it is not in a hand and
        /// a store for it is stale), unless it already sits in the target compartment - in which
        /// case the store is idempotent and may pass.</summary>
        private bool IsAlreadyShelved(ShelfCompartment target, InteractablePackagingBox_Item box,
            Guid id)
        {
            if (box == null || (!box.m_IsStored && !_boxes.IsStored(id)))
            {
                return false;
            }

            var targetList = target?.GetInteractablePackagingBoxList();
            return targetList == null || !targetList.Contains(box);
        }

        private bool TryTakeLiveBox(ShelfCompartment compartment,
            WarehouseBoxState result)
        {
            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null)
            {
                return false;
            }

            if (!box.CanPickup())
            {
                // The top box may still be settling from its own store/hold animation (a cosmetic
                // lerp). That must not refuse a legitimate authoritative take - refusing it forced
                // a client rollback, and the refused/replayed pair churned the box and its shelf
                // label. Settle the lerp and take the box.
                box.StopLerpToTransform();
            }

            var amount = box.m_ItemCompartment.GetItemCount();
            if (amount <= 0)
            {
                return false;
            }

            result.ItemType = box.m_ItemCompartment.GetItemType();
            result.Amount = amount;
            result.IsBig = box.m_IsBigBox;
            if (!_boxes.TryGetId(box, out var id))
            {
                return false;
            }

            result.BoxNetworkId = id;
            CoopPlugin.Log.LogInfo("[warehouse] host live take destroying stored box id=" + id
                + " name=" + box.name + " amount=" + amount + ".");
            _boxes.DetachForWarehouseTake(id);
            compartment.RemoveBox(box);
            UnityEngine.Object.Destroy(box.gameObject);
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

        private bool TryTakeRecord(ShelfCompartment compartment,
            WarehouseBoxState result)
        {
            var count = RecordCount(compartment);
            if (count == 0 || !TryReadRecord(compartment, count - 1, out var state))
            {
                return false;
            }

            var boxNetworkId = GetRecordId(compartment, count - 1);
            if (boxNetworkId == Guid.Empty)
            {
                return false;
            }

            var args = new object[] { null };
            if (!(bool)_recordPop.Invoke(compartment, args))
            {
                return false;
            }

            result.ItemType = state.ItemType;
            result.Amount = state.Amount;
            result.IsBig = state.IsBig;
            result.BoxNetworkId = boxNetworkId;
            return true;
        }

        private static int RecordCount(ShelfCompartment compartment)
        {
            return _recordCount == null ? 0 : (int)_recordCount.Invoke(compartment, null);
        }

        private static bool TryReadRecord(ShelfCompartment compartment, int index,
            out WarehouseBoxState state, bool validate = true)
        {
            state = null;
            var record = _recordPeek.Invoke(compartment, new object[] { index });
            if (record == null)
            {
                return false;
            }

            var amount = (int)_recordAmount.GetValue(record);
            var type = (EItemType)Convert.ToInt32(_recordItem.GetValue(record));
            if (validate && !IsValidBox(type, amount))
            {
                return false;
            }

            state = new WarehouseBoxState
            {
                ItemType = type,
                Amount = amount,
                IsBig = (bool)_recordBig.GetValue(record),
            };
            return true;
        }

        /// <summary>The positional id list for a compartment, created empty on first use.</summary>
        private List<Guid> RecordIdsFor(ShelfCompartment compartment)
        {
            var key = CompartmentKey(compartment);
            if (!_recordIds.TryGetValue(key, out var ids))
            {
                _recordIds[key] = ids = new List<Guid>();
            }

            return ids;
        }

        /// <summary>Host only: pads the id list with fresh ids up to and including index, covering
        /// records that predate the hooks (save-loaded records read lazily by
        /// <see cref="GetRecordId"/>). The client never mints ids, so it is left with a gap.</summary>
        private void EnsureRecordIds(ShelfCompartment compartment, int index)
        {
            if (!_host)
            {
                return;
            }

            var ids = RecordIdsFor(compartment);
            while (ids.Count <= index)
            {
                ids.Add(Guid.NewGuid());
            }
        }

        /// <summary>True when the positional id mirror already holds <paramref name="id"/> for this
        /// compartment. Used to keep an own accepted store from re-adding a record the game (and
        /// the mirror) already has.</summary>
        private bool HasRecordId(ShelfCompartment compartment, Guid id)
        {
            if (id == Guid.Empty
                || !_recordIds.TryGetValue(CompartmentKey(compartment), out var ids))
            {
                return false;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id)
                    return true;
            }

            return false;
        }

        private Guid GetRecordId(ShelfCompartment compartment, int index)
        {
            if (index < 0)
            {
                return Guid.Empty;
            }

            var ids = RecordIdsFor(compartment);
            if (ids.Count <= index)
            {
                if (!_host)
                {
                    return Guid.Empty;
                }

                // A record that predates any store command (a save-loaded record) has no
                // creator-assigned id: the host stamps a fresh one now.
                EnsureRecordIds(compartment, index);
            }

            return ids[index];
        }

        /// <summary>Pure mirror: appends the id of the record the game just added. A programmatic add
        /// (host store command, or client delta/baseline) carries its authoritative id; a local
        /// game-driven store binds the id of the box whose <c>OnFinishLerp</c> is adding the record, so
        /// the id is never staged ahead of a store the game may refuse. The host stamps a fresh id
        /// only for a save-loaded record that has no bound box; the client uses a placeholder, which
        /// the authoritative baseline replaces.</summary>
        private Guid AppendAddedRecordId(ShelfCompartment compartment)
        {
            var index = RecordCount(compartment) - 1;
            // Host: pad pre-existing records with fresh ids so the new id lands at its true index.
            EnsureRecordIds(compartment, index - 1);
            var ids = RecordIdsFor(compartment);
            // Client: records that predate the hooks have no id yet; placeholders keep the list
            // aligned until the host baseline arrives.
            while (ids.Count < index)
            {
                ids.Add(Guid.Empty);
            }

            var id = TakePreparedRecordId(compartment);
            if (id == Guid.Empty)
            {
                if (_host)
                {
                    id = Guid.NewGuid();
                }
                else
                {
                    CoopPlugin.Log.LogWarning("[warehouse] client stored record had no captured id on "
                        + "shelf [" + compartment.GetWarehouseIndex() + ","
                        + compartment.GetIndex() + "]; a placeholder keeps the mirror aligned until "
                        + "the host baseline arrives.");
                }
            }

            if (ids.Count > index)
            {
                // The mirror already has more ids than the game has records: a hard invariant
                // violation. Truncate so phantom ids cannot survive, and fail loud.
                CoopPlugin.Log.LogError("[warehouse] record id mirror longer than game list on add: ids="
                    + ids.Count + " game=" + RecordCount(compartment) + " shelf ["
                    + compartment.GetWarehouseIndex() + "," + compartment.GetIndex() + "].");
                ids.RemoveRange(index, ids.Count - index);
            }

            ids.Add(id);
            VerifyRecordMirror(compartment, "add");
            return id;
        }

        /// <summary>Pure mirror: removes the id of the record the game just popped, on both peers
        /// and whatever triggered the pop. Returns the removed id, or Guid.Empty when the mirror
        /// had no entry (which the host treats as an untracked record). A gap left by a record that
        /// predates the hooks is filled first - fresh id on the host, placeholder on the client -
        /// so the list stays aligned.</summary>
        private Guid RemovePoppedRecordId(ShelfCompartment compartment)
        {
            // After the pop, the popped record sat at this index.
            var recordCount = RecordCount(compartment);
            EnsureRecordIds(compartment, recordCount);
            var ids = RecordIdsFor(compartment);
            while (ids.Count <= recordCount)
            {
                ids.Add(Guid.Empty);
            }

            var id = ids[recordCount];
            ids.RemoveAt(recordCount);
            VerifyRecordMirror(compartment, "pop");
            return id;
        }

        /// <summary>Programmatic add: records the authoritative id for the record the very next
        /// AddStoredBoxRecord will add, whether the client is applying a host delta/baseline or the
        /// host is applying a store command. The add hook consumes it to mirror the record.</summary>
        private void PrepareAppliedRecordId(ShelfCompartment compartment, Guid id)
        {
            if (id == Guid.Empty)
            {
                throw new InvalidOperationException("Authoritative warehouse store requires a box network ID.");
            }

            var key = CompartmentKey(compartment);
            if (!_pendingApplyRecordIds.TryGetValue(key, out var pending))
            {
                _pendingApplyRecordIds[key] = pending = new Queue<Guid>();
            }

            pending.Enqueue(id);
        }

        /// <summary>Dequeues the id prepared for the next programmatic add (a client delta/baseline
        /// or a host store command). A game-driven local store does not use this queue: its id comes
        /// from the box whose record is being added, via <see cref="TakePreparedRecordId"/>.</summary>
        private Guid TakePendingApplyRecordId(ShelfCompartment compartment)
        {
            var key = CompartmentKey(compartment);
            if (!_pendingApplyRecordIds.TryGetValue(key, out var pending) || pending.Count == 0)
            {
                return Guid.Empty;
            }

            var id = pending.Dequeue();
            if (pending.Count == 0)
            {
                _pendingApplyRecordIds.Remove(key);
            }

            return id;
        }

        /// <summary>The stable id for the record the game is adding right now. A programmatic apply
        /// (host store command or client delta/baseline) draws from its prepared queue. A game-driven
        /// local store binds the id at the exact moment its box adds the record, so no id can be
        /// staged ahead of a store the game then refuses and later handed to a different store.</summary>
        private Guid TakePreparedRecordId(ShelfCompartment compartment)
        {
            if (IsApplyingRemote || _hostApplyingCommand)
            {
                return TakePendingApplyRecordId(compartment);
            }

            var box = _currentStoreBox;
            if (box != null && box.GetBoxStoredCompartment() == compartment
                && _boxes.TryGetId(box, out var localId) && localId != Guid.Empty)
            {
                return localId;
            }

            return Guid.Empty;
        }

        /// <summary>Hard invariant: the positional id list mirrors the game's record list exactly.
        /// A disagreement means a store/pop/reorder path mutated one but not the other; fail loud.</summary>
        private void VerifyRecordMirror(ShelfCompartment compartment, string context)
        {
            var gameCount = RecordCount(compartment);
            var mirrorCount = _recordIds.TryGetValue(CompartmentKey(compartment), out var ids)
                ? ids.Count : 0;
            if (mirrorCount != gameCount)
            {
                CoopPlugin.Log.LogError("[warehouse] record id mirror drifted on " + context
                    + ": ids=" + mirrorCount + " game=" + gameCount + " shelf ["
                    + compartment.GetWarehouseIndex() + "," + compartment.GetIndex() + "].");
            }
        }

        /// <summary>The observable fields of a stored box record. Two records with the same key are
        /// interchangeable to every consumer, so a permutation that only swaps such records' stable
        /// ids is exact on both peers.</summary>
        internal readonly struct StoredRecordKey : IEquatable<StoredRecordKey>
        {
            internal readonly EItemType ItemType;
            internal readonly int Amount;
            internal readonly bool IsBig;

            internal StoredRecordKey(EItemType itemType, int amount, bool isBig)
            {
                ItemType = itemType;
                Amount = amount;
                IsBig = isBig;
            }

            public bool Equals(StoredRecordKey other)
                => ItemType == other.ItemType && Amount == other.Amount && IsBig == other.IsBig;

            public override bool Equals(object obj)
                => obj is StoredRecordKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)ItemType;
                    hash = (hash * 397) ^ Amount;
                    hash = (hash * 397) ^ (IsBig ? 1 : 0);
                    return hash;
                }
            }
        }

        /// <summary>Records backend: snapshots the stored records' observable fields before the
        /// game re-sorts the compartment list. The arrange postfix replays that sort onto the
        /// positional id list using this before image. Returns null when there is nothing to
        /// reorder (one or zero records) or the record surface is unavailable.</summary>
        internal List<StoredRecordKey> CaptureRecordOrder(ShelfCompartment compartment)
        {
            if (!_usesRecords || compartment == null)
            {
                return null;
            }

            var count = RecordCount(compartment);
            if (count <= 1)
            {
                return null;
            }

            var before = new List<StoredRecordKey>(count);
            for (var i = 0; i < count; i++)
            {
                before.Add(ReadRecordKey(compartment, i));
            }

            return before;
        }

        /// <summary>Records backend: the game just re-sorted <c>m_StoredBoxRecordList</c> by amount
        /// descending. A sort only reorders, so the multiset is unchanged; match each after-list
        /// record back to its before-list position by value, consuming occurrences in order, and
        /// apply that exact permutation to the positional id list. Identical records are
        /// interchangeable, so the mapping is exact and identical on both peers.</summary>
        internal void ApplyRecordOrder(ShelfCompartment compartment, List<StoredRecordKey> before)
        {
            if (!_usesRecords || compartment == null || before == null || before.Count <= 1)
            {
                return;
            }

            var count = RecordCount(compartment);
            var key = CompartmentKey(compartment);
            if (!_recordIds.TryGetValue(key, out var ids))
            {
                CoopPlugin.Log.LogError("[warehouse] record reorder had no id mirror for shelf ["
                    + compartment.GetWarehouseIndex() + "," + compartment.GetIndex() + "].");
                return;
            }

            if (count != before.Count || ids.Count != before.Count)
            {
                CoopPlugin.Log.LogError("[warehouse] record id mirror drifted on reorder: after="
                    + count + " before=" + before.Count + " ids=" + ids.Count + " on shelf ["
                    + compartment.GetWarehouseIndex() + "," + compartment.GetIndex() + "].");
                return;
            }

            // Index the before image by observable value, so each after record consumes the next
            // interchangeable before position in O(1) instead of scanning the whole before list.
            var positions = new Dictionary<StoredRecordKey, Queue<int>>(before.Count);
            for (var i = 0; i < before.Count; i++)
            {
                var value = before[i];
                if (!positions.TryGetValue(value, out var queue))
                {
                    positions[value] = queue = new Queue<int>();
                }

                queue.Enqueue(i);
            }

            var reordered = new List<Guid>(count);
            for (var after = 0; after < count; after++)
            {
                var wanted = ReadRecordKey(compartment, after);
                if (!positions.TryGetValue(wanted, out var queue) || queue.Count == 0)
                {
                    CoopPlugin.Log.LogError("[warehouse] record reorder found no before image for "
                        + "after index " + after + " on shelf [" + compartment.GetWarehouseIndex()
                        + "," + compartment.GetIndex() + "]; leaving ids unchanged.");
                    return;
                }

                reordered.Add(ids[queue.Dequeue()]);
            }

            _recordIds[key] = reordered;
        }

        private static StoredRecordKey ReadRecordKey(ShelfCompartment compartment, int index)
        {
            var record = _recordPeek.Invoke(compartment, new object[] { index });
            return new StoredRecordKey(
                (EItemType)Convert.ToInt32(_recordItem.GetValue(record)),
                (int)_recordAmount.GetValue(record),
                (bool)_recordBig.GetValue(record));
        }

        /// <summary>Vanilla <c>ShelfCompartment.GetLastInteractablePackagingBox</c> indexes
        /// <c>m_InteractablePackagingBoxList[Count - 1]</c> and throws on an empty compartment.
        /// Warehouse applies routinely target a compartment that a racing take just emptied, so
        /// read the list safely instead of letting an authoritative apply crash the handler.</summary>
        private static InteractablePackagingBox_Item GetLastBoxOrNull(ShelfCompartment compartment)
        {
            if (compartment == null)
            {
                return null;
            }

            var boxes = compartment.GetInteractablePackagingBoxList();
            if (boxes == null || boxes.Count == 0)
            {
                return null;
            }

            return boxes[boxes.Count - 1];
        }

        private bool TryGetTopBoxNetworkId(ShelfCompartment compartment, out Guid id)
        {
            id = Guid.Empty;
            if (compartment == null)
            {
                return false;
            }

            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                return count > 0 && TryReadRecord(compartment, count - 1, out _, _host)
                    && (id = GetRecordId(compartment, count - 1)) != Guid.Empty;
            }

            var box = GetLastBoxOrNull(compartment);
            return box != null && _boxes.TryGetId(box, out id);
        }

        private bool TryReadTopClientBox(ShelfCompartment compartment, Guid expectedId,
            out WarehouseBoxState state)
        {
            state = null;
            if (compartment == null)
                return false;

            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                return count > 0 && TryReadRecord(compartment, count - 1, out state, false)
                    && GetRecordId(compartment, count - 1) == expectedId;
            }

            var box = GetLastBoxOrNull(compartment);
            if (box?.m_ItemCompartment == null || !_boxes.TryGetId(box, out var id)
                || id != expectedId)
                return false;
            state = new WarehouseBoxState
            {
                BoxNetworkId = id,
                ItemType = box.m_ItemCompartment.GetItemType(),
                Amount = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
            };
            return true;
        }

        private static long CompartmentKey(ShelfCompartment compartment)
        {
            return ((long)compartment.GetWarehouseIndex() << 32) | (uint)compartment.GetIndex();
        }

        private static List<ShelfCompartment> GetWarehouseCompartments()
        {
            var result = new List<ShelfCompartment>();
            var manager = FindShelfManager();
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

        private static ShelfManager FindShelfManager()
            => SceneRef<ShelfManager>.Get();

        private static ShelfCompartment ResolveCompartment(int shelfIndex, int compartmentIndex)
        {
            var compartments = GetWarehouseCompartments();
            for (var i = 0; i < compartments.Count; i++)
            {
                var compartment = compartments[i];
                if (compartment.GetWarehouseIndex() == shelfIndex
                    && compartment.GetIndex() == compartmentIndex)
                {
                    return compartment;
                }
            }

            return null;
        }

        private static bool TryGetAddress(ShelfCompartment compartment, out int shelfIndex,
            out int compartmentIndex)
        {
            shelfIndex = 0;
            compartmentIndex = 0;
            if (!IsWarehouse(compartment))
            {
                return false;
            }

            shelfIndex = compartment.GetWarehouseIndex();
            compartmentIndex = compartment.GetIndex();
            return true;
        }

        private static bool IsWarehouse(ShelfCompartment compartment)
        {
            return compartment != null && compartment.GetWarehouseShelf() != null;
        }

        /// <summary>The same admission checks the game's own <c>DispenseItem</c> makes before it
        /// stores a box, without any warehouse/authority condition. This lets the client tell
        /// "vanilla will refuse and show its own popup" apart from "vanilla would store it, but the
        /// host would reject it".</summary>
        private static bool WouldVanillaStore(ShelfCompartment compartment, EItemType itemType,
            int amount, bool isBig)
        {
            return itemType != EItemType.None && amount > 0 && compartment != null
                && compartment.m_CanPutBox && compartment.CheckBoxType(isBig) == isBig
                && compartment.HasEnoughSlot() && compartment.CheckBoxItemType(itemType);
        }

        private static bool CanStore(ShelfCompartment compartment, EItemType itemType, int amount,
            bool isBig)
        {
            return WouldVanillaStore(compartment, itemType, amount, isBig) && IsWarehouse(compartment)
                && amount <= MaxWarehouseAmount;
        }

        private static bool IsValidBox(WarehouseBoxState state)
        {
            return state != null && state.BoxNetworkId != Guid.Empty
                && IsValidBox(state.ItemType, state.Amount);
        }

        private static bool IsValidBox(EItemType type, int amount)
        {
            return type != EItemType.None && amount > 0 && amount <= MaxWarehouseAmount;
        }

        private bool Validate(WarehouseStoreMessage message)
        {
            return message != null && message.BoxNetworkId != Guid.Empty
                && message.ShelfIndex >= 0
                && message.CompartmentIndex >= 0 && IsValidBox(message.ItemType, message.Amount);
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
            var batcher = assembly.GetType("StoredBoxVisualBatcher", false);
            _recordRebuild = batcher == null ? null : AccessTools.Method(batcher, "RebuildImmediate");
            if (_recordType != null)
            {
                _recordItem = AccessTools.Field(_recordType, "itemType");
                _recordAmount = AccessTools.Field(_recordType, "amount");
                _recordBig = AccessTools.Field(_recordType, "isBigBox");
                _recordItemType = _recordItem?.FieldType;
            }

            _usesRecords = _recordType != null && _recordCount != null && _recordPeek != null
                && _recordPop != null && _recordAdd != null && _recordItem != null
                && _recordAmount != null && _recordBig != null && _recordItemType != null;
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
