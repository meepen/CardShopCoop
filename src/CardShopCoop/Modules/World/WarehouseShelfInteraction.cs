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
        private readonly Action<int, INetMessage> _send;
        private readonly BoxNetworkInteraction _boxes;
        private readonly Dictionary<long, List<long>> _recordIds = new();
        private readonly Dictionary<long, Queue<PendingRecordStore>> _pendingRecordStores = new();
        private readonly Queue<long> _pendingRecordTakes = new();
        private readonly Dictionary<long, WarehouseBoxState> _knownBoxes = new();
        private readonly Dictionary<long, long> _knownBoxLocations = new();
        private WarehouseStateMessage _pendingClientState;
        private readonly List<WarehouseDeltaMessage> _pendingClientDeltas = new();
        private bool _hostApplyingCommand;

        internal bool IsApplyingRemote
        {
            get; private set;
        }

        private sealed class PendingRecordStore
        {
            internal long BoxNetworkId;
            internal EItemType ItemType;
            internal int Amount;
            internal bool IsBig;
        }

        internal WarehouseShelfInteraction(bool host, Action<INetMessage> broadcast,
            Action<int, INetMessage> send, BoxNetworkInteraction boxes)
        {
            _host = host;
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            Probe();
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
            _pendingRecordStores.Clear();
            _pendingRecordTakes.Clear();
            _pendingClientState = null;
            _pendingClientDeltas.Clear();
            _hostApplyingCommand = false;
            _knownBoxes.Clear();
            _knownBoxLocations.Clear();
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

        internal bool TryForwardClientStore(InteractablePackagingBox_Item box, bool isPlayer,
            ShelfCompartment compartment)
        {
            if (_host || IsApplyingRemote || !isPlayer)
            {
                return true;
            }

            if (!IsWarehouse(compartment))
            {
                // A box compartment the warehouse protocol does not own (for example a custom
                // shelf without a WarehouseShelf). There is no wire model for it, so vanilla runs
                // locally. Warn when that local store would actually happen so it is diagnosable
                // rather than a silent, unsynced mutation.
                if (box?.m_ItemCompartment != null && WouldVanillaStore(compartment,
                        box.m_ItemCompartment.GetItemType(), box.m_ItemCompartment.GetItemCount(),
                        box.m_IsBigBox))
                {
                    CoopPlugin.Log.LogWarning("[warehouse] storing box " + box.name
                        + " on a non-warehouse compartment; this store cannot be synced.");
                }

                return true;
            }

            if (box == null || box.m_ItemCompartment == null || !TryGetAddress(compartment,
                    out var shelfIndex, out var compartmentIndex))
            {
                return false;
            }

            var amount = box.m_ItemCompartment.GetItemCount();
            var itemType = box.m_ItemCompartment.GetItemType();
            if (!WouldVanillaStore(compartment, itemType, amount, box.m_IsBigBox))
            {
                // Vanilla will refuse this and show its own player-facing popup; let it run.
                return true;
            }

            if (amount > MaxWarehouseAmount)
            {
                // Vanilla would store it locally, but the host rejects anything above the
                // warehouse amount limit (Validate/IsValidBox). Suppressing the local store keeps
                // the client from diverging from authority; the box stays in the player's hand.
                CoopPlugin.Log.LogWarning("[warehouse] refusing to store box " + box.name + " ("
                    + itemType + " x" + amount + ", big=" + box.m_IsBigBox
                    + "): amount exceeds the " + MaxWarehouseAmount + " limit the host enforces.");
                return false;
            }

            if (!_boxes.TryGetId(box, out var boxNetworkId))
                throw new InvalidOperationException("Warehouse store has no authoritative box ID.");

            var intent = new WarehouseStoreMessage
            {
                BoxNetworkId = boxNetworkId,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                ItemType = box.m_ItemCompartment.GetItemType(),
                Amount = amount,
                IsBig = box.m_IsBigBox,
            };
            var delta = new WarehouseDeltaMessage
            {
                IsStore = true,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                BoxNetworkId = boxNetworkId,
                ItemType = intent.ItemType,
                Amount = intent.Amount,
                IsBig = intent.IsBig,
            };
            WorldPrediction.Predict(WorldPrediction.WarehouseScope, intent,
                () => ClientApplyDelta(delta),
                () => ClientUndoStore(delta));
            return false;
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
                if (count == 0 || GetRecordId(compartment, count - 1, null) != delta.BoxNetworkId)
                {
                    return;
                }

                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                {
                    return;
                }

                RemoveLastRecordId(compartment);
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

            _boxes.ClientForgetStored(delta.BoxNetworkId);
            if (!_boxes.ClientEnsureWarehouseTake(delta.BoxNetworkId, delta.ItemType, delta.Amount,
                delta.IsBig, out var held))
            {
                throw new InvalidOperationException("Warehouse store rollback could not restore box ID "
                    + delta.BoxNetworkId + ".");
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            held.StartHoldBox(true, controller.m_HoldItemPos);
        }

        internal bool TryForwardClientTake(InteractableStorageCompartment storage)
        {
            if (_host || IsApplyingRemote || storage == null)
            {
                return true;
            }

            var compartment = storage.GetShelfCompartment();
            if (!IsWarehouse(compartment) || !TryGetAddress(compartment, out var shelfIndex,
                out var compartmentIndex))
            {
                return true;
            }

            if (!TryGetTopBoxNetworkId(compartment, out var boxNetworkId))
            {
                // The top box has no authoritative id on this peer. Suppress the vanilla take so
                // we do not remove a box the host will not mirror, and leave it for diagnosis.
                CoopPlugin.Log.LogWarning("[warehouse] take could not resolve the top box on shelf ["
                    + shelfIndex + "," + compartmentIndex + "]; leaving the box in place.");
                return false;
            }

            var takeIntent = new WarehouseTakeMessage
            {
                BoxNetworkId = boxNetworkId,
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
            };
            var takeDelta = new WarehouseDeltaMessage
            {
                ShelfIndex = shelfIndex,
                CompartmentIndex = compartmentIndex,
                BoxNetworkId = boxNetworkId,
            };
            WorldPrediction.Predict(WorldPrediction.WarehouseScope, takeIntent,
                () => ClientApplyTakePrediction(takeDelta, compartment),
                () => ClientUndoTakePrediction(takeDelta));
            return false;
        }

        private void ClientApplyTakePrediction(WarehouseDeltaMessage delta,
            ShelfCompartment compartment)
        {
            if (!TryReadTopClientBox(compartment, delta.BoxNetworkId, out var state))
                throw new InvalidOperationException("Warehouse prediction could not read its top box.");
            delta.ItemType = state.ItemType;
            delta.Amount = state.Amount;
            delta.IsBig = state.IsBig;
            ClientTakeIntoHand(delta, compartment);
        }

        /// <summary>Moves a taken box from the shelf into the local player's hand. The live box
        /// that was stored is the box being taken, so it is moved directly; only the record
        /// backend needs a fresh live representation.</summary>
        private void ClientTakeIntoHand(WarehouseDeltaMessage delta, ShelfCompartment compartment)
        {
            if (compartment == null)
                throw new InvalidOperationException("Warehouse take references an unknown compartment.");

            if (_usesRecords)
            {
                PopClientWarehouseRecord(compartment, delta.BoxNetworkId);
            }
            else if (_boxes.TryGetBox(delta.BoxNetworkId, out var stored)
                && stored is InteractablePackagingBox_Item live)
            {
                var boxes = compartment.GetInteractablePackagingBoxList();
                if (boxes != null && boxes.Contains(live))
                {
                    compartment.RemoveBox(live);
                }

                _boxes.ClientForgetStored(delta.BoxNetworkId);
            }

            if (!_boxes.ClientEnsureWarehouseTake(delta.BoxNetworkId, delta.ItemType, delta.Amount,
                delta.IsBig, out var box))
            {
                throw new InvalidOperationException("Warehouse take could not materialize box ID "
                    + delta.BoxNetworkId + ".");
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            box.StartHoldBox(true, controller.m_HoldItemPos);
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
                    throw new InvalidOperationException("Warehouse take could not materialize box ID "
                        + message.BoxNetworkId + ".");
                }

                _boxes.ClientForgetStored(message.BoxNetworkId);
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

            _boxes.ClientForgetStored(message.BoxNetworkId);
        }

        private static readonly Vector3 ParkedWorkerBoxPosition = new Vector3(10000f, 10000f, 10000f);

        /// <summary>Client: a worker is carrying world box <paramref name="boxNetworkId"/>. Detach
        /// it from any warehouse compartment and park the real object out of view; the Npc worker
        /// prop draws the carried box. The id binding is kept so a later store reuses this same
        /// object, and the host's warehouse removal delta still fixes the compartment listing.</summary>
        internal void ApplyWorkerHeldBox(long boxNetworkId)
        {
            if (_host || boxNetworkId <= 0 || !Available())
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

            _boxes.ClientForgetStored(boxNetworkId);
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
        internal void RestoreWorkerDroppedBox(long boxNetworkId)
        {
            if (_host || boxNetworkId <= 0 || !Available())
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
            // The optimistic take may still be lerping the box into the hand, and the game's
            // store path refuses a box that is lerping (CanPickup). Stop that lerp so the same
            // box can go straight back onto the shelf.
            if (!_usesRecords && _boxes.TryGetBox(delta.BoxNetworkId, out var box))
            {
                box.StopLerpToTransform();
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
            WarehouseBoxState state = null;
            if (_host && TryReadRecord(compartment, RecordCount(compartment) - 1, out state))
            {
                var id = TakePreparedRecordStore(compartment, state)
                    ?? _boxes.ReserveStoredId(ToDescriptor(0, state));
                SetRecordId(compartment, RecordCount(compartment) - 1, id);
                state.BoxNetworkId = id;
                RememberWarehouseBox(compartment, state);
                _boxes.HostMarkStored(id, ToDescriptor(id, state));
            }

            if (!_hostApplyingCommand)
            {
                var stored = state;
                BroadcastWarehouseDelta(compartment, true, stored);
            }
        }

        internal void OnStoredBoxRecordPopped(ShelfCompartment compartment, bool didPop)
        {
            if (didPop)
            {
                long id = 0;
                WarehouseBoxState removed = null;
                if (_host)
                {
                    id = RemoveLastRecordId(compartment);
                    if (id > 0)
                    {
                        _knownBoxes.TryGetValue(id, out removed);
                        _knownBoxes.Remove(id);
                        _knownBoxLocations.Remove(id);
                        _pendingRecordTakes.Enqueue(id);
                    }
                }

                if (!_hostApplyingCommand && id > 0 && removed != null)
                {
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
            if (!_boxes.TryGetId(box, out var id) || id <= 0)
            {
                return;
            }

            WarehouseBoxState state;
            if (!_knownBoxes.TryGetValue(id, out state) || state == null)
            {
                state = new WarehouseBoxState
                {
                    BoxNetworkId = id,
                    ItemType = box.m_ItemCompartment == null
                        ? EItemType.None : box.m_ItemCompartment.GetItemType(),
                    Amount = box.m_ItemCompartment == null ? 0 : box.m_ItemCompartment.GetItemCount(),
                    IsBig = box.m_IsBigBox,
                };
            }

            _knownBoxes.Remove(id);
            _knownBoxLocations.Remove(id);
            CoopPlugin.Log.LogInfo("[warehouse] remove id=" + id + " shelf="
                + compartment.GetWarehouseIndex() + ":" + compartment.GetIndex());
            BroadcastWarehouseDelta(compartment, false, state);
        }

        internal bool PrepareHostStore(InteractablePackagingBox_Item box, bool isPlayer,
            ShelfCompartment compartment)
        {
            if (!_host || !_usesRecords || box?.m_ItemCompartment == null
                || !IsWarehouse(compartment) || !_boxes.TryGetId(box, out var id))
            {
                return true;
            }

            var amount = box.m_ItemCompartment.GetItemCount();
            var itemType = box.m_ItemCompartment.GetItemType();
            if (!CanStore(compartment, itemType, amount, box.m_IsBigBox))
            {
                return true;
            }

            return PrepareRecordStore(compartment, id, itemType, amount, box.m_IsBigBox);
        }

        internal bool TryClaimHostRecordTake(InteractablePackagingBox_Item box)
        {
            if (!_host || box?.m_ItemCompartment == null || _pendingRecordTakes.Count == 0)
            {
                return false;
            }

            var id = _pendingRecordTakes.Dequeue();
            _boxes.HostBindWarehouseTake(id, box);
            return true;
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
            if (!_boxes.TryGetBox(message.BoxNetworkId, out var physical)
                || physical is not InteractablePackagingBox_Item box || box.m_ItemCompartment == null)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: unknown box ID "
                    + message.BoxNetworkId + ".");
            }
            else if (box.m_ItemCompartment.GetItemType() != message.ItemType
                     || box.m_ItemCompartment.GetItemCount() != message.Amount
                     || box.m_IsBigBox != message.IsBig)
            {
                CoopPlugin.Log.LogWarning("Warehouse store rejected: payload disagrees with box ID "
                    + message.BoxNetworkId + ".");
            }
            else if (CanStore(compartment, message.ItemType, message.Amount, message.IsBig))
            {
                try
                {
                    _hostApplyingCommand = true;
                    if (_usesRecords)
                    {
                        if (PrepareRecordStore(compartment, message.BoxNetworkId,
                            message.ItemType, message.Amount, message.IsBig))
                        {
                            accepted = AddRecord(compartment, message.ItemType, message.Amount,
                                message.IsBig);
                        }
                        if (accepted)
                        {
                            box.OnDestroyed();
                        }
                    }
                    else
                    {
                        // Idempotent: a box already listed in the compartment must not be added
                        // again, which would inflate the slot count with a duplicate entry.
                        var stored = compartment.GetInteractablePackagingBoxList();
                        accepted = (stored != null && stored.Contains(box))
                            || StoreLiveBox(compartment, box);
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
            if (!_host || message == null || message.BoxNetworkId <= 0)
            {
                return false;
            }

            var result = new WarehouseBoxState
            {
                BoxNetworkId = message.BoxNetworkId,
            };
            var accepted = false;
            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            if (compartment != null && TryGetTopBoxNetworkId(compartment, out var topId)
                && topId == message.BoxNetworkId)
            {
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
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("Warehouse take failed: " + e);
                    throw;
                }
                finally
                {
                    _hostApplyingCommand = false;
                }
            }

            if (!accepted)
                return false;

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
                var controller = SceneRef<InteractionPlayerController>.Get();
                controller?.OnExitHoldBoxMode();
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
            var descriptor = BoxNetworkInteraction.ItemDescriptor(message.BoxNetworkId,
                message.ItemType, message.Amount, message.IsBig, Vector3.zero, Quaternion.identity);
            IsApplyingRemote = true;
            try
            {
                if (_usesRecords)
                {
                    AddRecord(compartment, message.ItemType, message.Amount, message.IsBig);
                    SetRecordId(compartment, RecordCount(compartment) - 1, message.BoxNetworkId);
                    _boxes.ClientMarkStored(message.BoxNetworkId, descriptor);
                }
                else if (_boxes.TryGetBox(message.BoxNetworkId, out var box)
                    && box is InteractablePackagingBox_Item item)
                {
                    // Idempotent: only dispense into the compartment if it is not already there.
                    var stored = compartment.GetInteractablePackagingBoxList();
                    if (stored == null || !stored.Contains(item))
                    {
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
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }

        /// <summary>Pops the record a client take targets. Only the record backend keeps its boxes
        /// as records; the live backend moves the stored box itself (see
        /// <see cref="ClientTakeIntoHand"/> and <see cref="ClientApplyRemoteTake"/>).</summary>
        private void PopClientWarehouseRecord(ShelfCompartment compartment, long boxNetworkId)
        {
            if (compartment == null)
                return;

            IsApplyingRemote = true;
            try
            {
                var count = RecordCount(compartment);
                if (count == 0 || GetRecordId(compartment, count - 1, null) != boxNetworkId)
                    return;
                var args = new object[] { null };
                if (!(bool)_recordPop.Invoke(compartment, args))
                    throw new InvalidOperationException("Warehouse record take could not be applied.");
                RemoveLastRecordId(compartment);
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
                || state == null || state.BoxNetworkId <= 0)
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

        private void RememberWarehouseBox(ShelfCompartment compartment, WarehouseBoxState state)
        {
            if (state == null || state.BoxNetworkId <= 0)
                return;
            _knownBoxes[state.BoxNetworkId] = state;
            _knownBoxLocations[state.BoxNetworkId] = CompartmentKey(compartment);
        }

        private bool TryGetLastLiveBoxState(ShelfCompartment compartment,
            out WarehouseBoxState state)
        {
            state = null;
            var box = compartment?.GetLastInteractablePackagingBox();
            if (box?.m_ItemCompartment == null || !_boxes.TryGetId(box, out var id))
                return false;
            state = new WarehouseBoxState
            {
                BoxNetworkId = id,
                ItemType = box.m_ItemCompartment.GetItemType(),
                Amount = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
            };
            RememberWarehouseBox(compartment, state);
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
                        box.BoxNetworkId = GetRecordId(compartment, i, box);
                        state.Boxes.Add(box);
                        RememberWarehouseBox(compartment, box);
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
                                BoxNetworkId = _host
                                    ? _boxes.EnsureHostId(box)
                                    : _boxes.TryGetId(box, out var id) ? id : 0,
                                ItemType = box.m_ItemCompartment.GetItemType(),
                                Amount = amount,
                                IsBig = box.m_IsBigBox,
                            };
                            state.Boxes.Add(storedState);
                            RememberWarehouseBox(compartment, storedState);
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
                SetRecordIds(compartment, wanted);
            }

            if (_usesRecords)
            {
                while (RecordCount(compartment) > 0)
                {
                    var args = new object[] { null };
                    if (!(bool)_recordPop.Invoke(compartment, args))
                        throw new InvalidOperationException("Warehouse record storage could not be cleared.");
                }

                for (var i = 0; i < wanted.Count; i++)
                {
                    var box = wanted[i];
                    AddRecord(compartment, box.ItemType, box.Amount, box.IsBig);
                }

                _recordRebuild?.Invoke(null, new object[] { compartment });
                SetRecordIds(compartment, wanted);
                MarkClientStored(wanted);
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
                StoreLiveBox(compartment, box.ItemType, box.Amount, box.IsBig, out var spawned);
                _boxes.BindStoredLiveBox(box.BoxNetworkId, spawned, box);
            }
        }

        private void StoreLiveBox(ShelfCompartment compartment, EItemType type, int amount,
            bool isBig, out InteractablePackagingBox_Item stored)
        {
            stored = _boxes.MaterializeWarehouseStored(type, amount, isBig);
            if (stored == null)
                throw new InvalidOperationException("Could not materialize an authoritative warehouse box.");

            stored.SetPhysicsEnabled(false);
            stored.DispenseItem(false, compartment);
            if (!stored.m_IsStored)
            {
                UnityEngine.Object.Destroy(stored.gameObject);
                throw new InvalidOperationException("Materialized warehouse box was not stored.");
            }

            return;
        }

        private static bool StoreLiveBox(ShelfCompartment compartment,
            InteractablePackagingBox_Item box)
        {
            if (box == null)
            {
                return false;
            }

            box.SetPhysicsEnabled(false);
            box.DispenseItem(false, compartment);
            return box.m_IsStored;
        }

        private bool TryTakeLiveBox(ShelfCompartment compartment,
            WarehouseBoxState result)
        {
            var box = compartment.GetLastInteractablePackagingBox();
            if (box?.m_ItemCompartment == null || !box.CanPickup())
            {
                return false;
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
            _boxes.HostMarkStored(id, ToDescriptor(id, result));
            _boxes.DetachForWarehouseTake(id);
            compartment.RemoveBox(box);
            UnityEngine.Object.Destroy(box.gameObject);
            _pendingRecordTakes.Enqueue(id);
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

            var boxNetworkId = GetRecordId(compartment, count - 1, state);
            if (boxNetworkId <= 0)
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

        private long GetRecordId(ShelfCompartment compartment, int index, WarehouseBoxState state)
        {
            if (index < 0)
            {
                return 0;
            }
            var key = CompartmentKey(compartment);
            if (!_recordIds.TryGetValue(key, out var ids))
            {
                _recordIds[key] = ids = new List<long>();
            }

            while (ids.Count <= index)
            {
                if (!_host)
                {
                    return 0;
                }

                ids.Add(_boxes.ReserveStoredId(ToDescriptor(0, state)));
            }

            return ids[index];
        }

        private void SetRecordId(ShelfCompartment compartment, int index, long id)
        {
            if (index < 0 || id <= 0)
            {
                throw new InvalidOperationException("Warehouse record ID assignment requires a valid index and ID.");
            }
            var key = CompartmentKey(compartment);
            if (!_recordIds.TryGetValue(key, out var ids))
            {
                _recordIds[key] = ids = new List<long>();
            }

            while (ids.Count <= index)
            {
                ids.Add(0);
            }

            ids[index] = id;
        }

        private long RemoveLastRecordId(ShelfCompartment compartment)
        {
            var key = CompartmentKey(compartment);
            if (!_recordIds.TryGetValue(key, out var ids) || ids.Count == 0)
            {
                return 0;
            }

            var last = ids.Count - 1;
            var id = ids[last];
            ids.RemoveAt(last);
            return id;
        }

        private void SetRecordIds(ShelfCompartment compartment, List<WarehouseBoxState> states)
        {
            var ids = new List<long>(states.Count);
            for (var i = 0; i < states.Count; i++)
            {
                ids.Add(states[i].BoxNetworkId);
            }

            _recordIds[CompartmentKey(compartment)] = ids;
        }

        private bool PrepareRecordStore(ShelfCompartment compartment, long id, EItemType itemType,
            int amount, bool isBig)
        {
            if (id <= 0)
            {
                throw new InvalidOperationException("Warehouse record store requires a box network ID.");
            }

            var key = CompartmentKey(compartment);
            if (!_pendingRecordStores.TryGetValue(key, out var pending))
            {
                _pendingRecordStores[key] = pending = new Queue<PendingRecordStore>();
            }

            pending.Enqueue(new PendingRecordStore
            {
                BoxNetworkId = id,
                ItemType = itemType,
                Amount = amount,
                IsBig = isBig,
            });
            return true;
        }

        private long? TakePreparedRecordStore(ShelfCompartment compartment, WarehouseBoxState state)
        {
            var key = CompartmentKey(compartment);
            if (!_pendingRecordStores.TryGetValue(key, out var pending) || pending.Count == 0)
            {
                return null;
            }

            var prepared = pending.Peek();
            if (prepared.ItemType != state.ItemType || prepared.Amount != state.Amount
                || prepared.IsBig != state.IsBig)
            {
                return null;
            }

            pending.Dequeue();
            if (pending.Count == 0)
            {
                _pendingRecordStores.Remove(key);
            }

            return prepared.BoxNetworkId;
        }

        private bool TryGetTopBoxNetworkId(ShelfCompartment compartment, out long id)
        {
            id = 0;
            if (compartment == null)
            {
                return false;
            }

            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                return count > 0 && TryReadRecord(compartment, count - 1, out var state, _host)
                    && (id = GetRecordId(compartment, count - 1, state)) > 0;
            }

            var box = compartment.GetLastInteractablePackagingBox();
            return box != null && _boxes.TryGetId(box, out id);
        }

        private bool TryReadTopClientBox(ShelfCompartment compartment, long expectedId,
            out WarehouseBoxState state)
        {
            state = null;
            if (compartment == null)
                return false;

            if (_usesRecords)
            {
                var count = RecordCount(compartment);
                return count > 0 && TryReadRecord(compartment, count - 1, out state, false)
                    && GetRecordId(compartment, count - 1, state) == expectedId;
            }

            var box = compartment.GetLastInteractablePackagingBox();
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

        private void MarkClientStored(List<WarehouseBoxState> states)
        {
            if (_host)
            {
                return;
            }

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (_usesRecords)
                {
                    _boxes.ClientMarkStored(state.BoxNetworkId, ToDescriptor(state.BoxNetworkId, state));
                }
            }
        }

        private static long CompartmentKey(ShelfCompartment compartment)
        {
            return ((long)compartment.GetWarehouseIndex() << 32) | (uint)compartment.GetIndex();
        }

        private static BoxNetworkState ToDescriptor(long id, WarehouseBoxState state)
        {
            return BoxNetworkInteraction.ItemDescriptor(id, state.ItemType, state.Amount,
                state.IsBig, Vector3.zero, Quaternion.identity);
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
            return state != null && state.BoxNetworkId > 0
                && IsValidBox(state.ItemType, state.Amount);
        }

        private static bool IsValidBox(EItemType type, int amount)
        {
            return type != EItemType.None && amount > 0 && amount <= MaxWarehouseAmount;
        }

        private bool Validate(WarehouseStoreMessage message)
        {
            return message != null && message.BoxNetworkId > 0
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
