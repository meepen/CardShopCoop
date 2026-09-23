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
            _hostApplyingCommand = false;
            _knownBoxes.Clear();
            _knownBoxLocations.Clear();
        }

        internal void FlushClientState()
        {
            if (_pendingClientState == null)
                return;

            var pending = _pendingClientState;
            _pendingClientState = null;
            ClientApplyState(pending);
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
            if (_host || IsApplyingRemote || !isPlayer || !IsWarehouse(compartment))
            {
                return true;
            }

            if (box == null || box.m_ItemCompartment == null || !TryGetAddress(compartment,
                    out var shelfIndex, out var compartmentIndex))
            {
                return false;
            }

            var amount = box.m_ItemCompartment.GetItemCount();
            if (!CanStore(compartment, box.m_ItemCompartment.GetItemType(), amount, box.m_IsBigBox))
            {
                return true;
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
                () => ClientApplyDelta(new WarehouseDeltaMessage
                {
                    IsStore = false,
                    ShelfIndex = shelfIndex,
                    CompartmentIndex = compartmentIndex,
                    BoxNetworkId = boxNetworkId,
                    ItemType = intent.ItemType,
                    Amount = intent.Amount,
                    IsBig = intent.IsBig,
                }));
            return false;
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
            ClientApplyDelta(delta);
        }

        private void ClientUndoTakePrediction(WarehouseDeltaMessage delta)
        {
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
            if (!_hostApplyingCommand && TryGetLastLiveBoxState(compartment, out var added))
                BroadcastWarehouseDelta(compartment, true, added);
        }

        internal void OnBoxRemoved(ShelfCompartment compartment)
        {
            if (!_hostApplyingCommand)
                BroadcastLiveRemoval(compartment);
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
                        accepted = StoreLiveBox(compartment, box);
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
            if (message.IsStore)
            {
                var controller = SceneRef<InteractionPlayerController>.Get();
                controller?.OnExitHoldBoxMode();
                ClientApplyStoreDelta(message);
                return;
            }

            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            RemoveClientWarehouseTop(compartment, message.BoxNetworkId);
            if (!_boxes.ClientEnsureWarehouseTake(message.BoxNetworkId, message.ItemType,
                    message.Amount, message.IsBig, out var box))
                throw new InvalidOperationException("Warehouse take could not materialize box ID "
                    + message.BoxNetworkId + ".");

            var controllerForTake = SceneRef<InteractionPlayerController>.Get();
            box.StartHoldBox(true, controllerForTake.m_HoldItemPos);
        }

        private void ClientApplyStoreDelta(WarehouseDeltaMessage message)
        {
            var compartment = ResolveCompartment(message.ShelfIndex, message.CompartmentIndex);
            if (compartment == null)
                throw new InvalidOperationException("Warehouse store delta references an unknown compartment.");

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
                    item.DispenseItem(false, compartment);
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

        private void RemoveClientWarehouseTop(ShelfCompartment compartment, long boxNetworkId)
        {
            if (compartment == null)
                throw new InvalidOperationException("Warehouse take delta references an unknown compartment.");

            IsApplyingRemote = true;
            try
            {
                if (_usesRecords)
                {
                    var count = RecordCount(compartment);
                    if (count == 0 || GetRecordId(compartment, count - 1, null) != boxNetworkId)
                        return;
                    var args = new object[] { null };
                    if (!(bool)_recordPop.Invoke(compartment, args))
                        throw new InvalidOperationException("Warehouse record take could not be applied.");
                    RemoveLastRecordId(compartment);
                    return;
                }

                var box = compartment.GetLastInteractablePackagingBox();
                if (box == null || !_boxes.TryGetId(box, out var id) || id != boxNetworkId)
                    return;
                compartment.RemoveBox(box);
                _boxes.DetachForWarehouseTake(boxNetworkId);
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

        private void BroadcastLiveRemoval(ShelfCompartment compartment)
        {
            if (!_host || IsApplyingRemote || !Available() || !IsWarehouse(compartment))
                return;

            var key = CompartmentKey(compartment);
            var present = new HashSet<long>();
            var boxes = compartment.GetInteractablePackagingBoxList();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                if (boxes[i] != null && _boxes.TryGetId(boxes[i], out var id))
                    present.Add(id);
            }

            var removed = new List<long>();
            foreach (var pair in _knownBoxLocations)
            {
                if (pair.Value == key && !present.Contains(pair.Key))
                    removed.Add(pair.Key);
            }

            for (var i = 0; i < removed.Count; i++)
            {
                var id = removed[i];
                if (_knownBoxes.TryGetValue(id, out var state))
                    BroadcastWarehouseDelta(compartment, false, state);
                _knownBoxes.Remove(id);
                _knownBoxLocations.Remove(id);
            }
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

                    compartment.RemoveBox(copy[i]);
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

        private static bool CanStore(ShelfCompartment compartment, EItemType itemType, int amount,
            bool isBig)
        {
            return itemType != EItemType.None && amount > 0 && IsWarehouse(compartment)
                && compartment.m_CanPutBox && compartment.CheckBoxType(isBig) == isBig
                && compartment.HasEnoughSlot() && compartment.CheckBoxItemType(itemType);
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
