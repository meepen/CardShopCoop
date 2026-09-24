using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Owns the host-assigned identity of every packaging box. IDs are allocated only by the
    /// host, announced with a complete creation descriptor, and retained while an item box is
    /// represented by a warehouse record. No interaction protocol is allowed to fall back to a
    /// name, hierarchy path, or transform signature.
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
        private readonly Dictionary<long, InteractablePackagingBox> _boxesById = new();
        private readonly Dictionary<InteractablePackagingBox, long> _idsByBox = new();
        private readonly Dictionary<long, BoxNetworkState> _storedBoxes = new();
        private readonly Dictionary<long, string> _furnitureEntityIds = new();
        private readonly Dictionary<long, BoxCreatedMessage> _pendingFurniture = new();
        // Package-box factories run locally on clients as part of vanilla furniture/item
        // flows. Keep those objects alive until the host's descriptor gives us their identity.
        private readonly HashSet<InteractablePackagingBox> _unboundCandidates = new();
        private Transform _cardSpawnAnchor;
        private long _nextId = 1;
        private int _materializing;
        private int _applyingRemote;
        internal Guid HostPredictionId;
        private static readonly System.Reflection.FieldInfo OutOfBoundsTimer =
            AccessTools.Field(typeof(RestockManager), "m_OutofBoundCheckTimer");
        private static readonly System.Reflection.FieldInfo BoxedFurniture =
            AccessTools.Field(typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        private static readonly System.Reflection.FieldInfo BeingHoldField =
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
            _storedBoxes.Clear();
            _furnitureEntityIds.Clear();
            _pendingFurniture.Clear();
            _unboundCandidates.Clear();
            _nextId = 1;
            _materializing = 0;
            _applyingRemote = 0;
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
            _storedBoxes.Clear();
            _furnitureEntityIds.Clear();
            _pendingFurniture.Clear();
            _unboundCandidates.Clear();
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

            var id = EnsureHostId(box);
            var created = CreateMessage(id, box);
            created.PredictionId = HostPredictionId;
            _broadcast(created);
            CoopPlugin.Log.LogInfo("[box-id] created id=" + id + " kind=" + box.GetType().Name);
        }

        internal void RejectClientCreated(InteractablePackagingBox box)
        {
            if (_host || IsMaterializing || box == null)
            {
                return;
            }

            // Vanilla creates the physical object before the host's authoritative purchase/box
            // message can arrive. It is a candidate, not an authoritative object yet.
            if (!_unboundCandidates.Add(box))
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[box-id] holding unbound client candidate " + box.name
                + " kind=" + box.GetType().Name + ".");
        }

        internal long EnsureHostId(InteractablePackagingBox box)
        {
            if (!_host)
            {
                throw new InvalidOperationException("Only the host may allocate a box network ID.");
            }

            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            if (_idsByBox.TryGetValue(box, out var known))
            {
                return known;
            }

            var id = AllocateId();
            Bind(id, box);
            return id;
        }

        internal bool TryGetId(InteractablePackagingBox box, out long id)
        {
            if (box != null && _idsByBox.TryGetValue(box, out id))
            {
                return true;
            }

            id = 0;
            return false;
        }

        internal void HostRefresh(long id)
        {
            if (!_host || id <= 0 || !TryGetBox(id, out var box))
            {
                return;
            }

            _broadcast(CreateMessage(id, box));
        }

        /// <summary>Applies a client-reported item-box state to the host's live box. Unchanged
        /// contents are left alone; only the open flag is touched when contents did not change.</summary>
        internal void ApplyHostBoxState(InteractablePackagingBox_Item item,
            BoxStateRequestMessage message)
        {
            var compartment = item.m_ItemCompartment;
            ApplyItemState(item, new BoxNetworkState
            {
                Kind = BoxNetworkKind.Item,
                ItemType = message.ContentsChanged || compartment == null
                    ? message.ItemType : compartment.GetItemType(),
                ItemCount = message.ContentsChanged || compartment == null
                    ? message.ItemCount : compartment.GetItemCount(),
                IsBoxOpened = message.IsBoxOpened,
            });
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

        internal BoxNetworkState DescribeAuthoritative(long id)
        {
            return _host && id > 0 && TryGetBox(id, out var box) ? Describe(id, box) : null;
        }

        internal bool TryGetBox(long id, out InteractablePackagingBox box)
        {
            box = null;
            return id > 0 && _boxesById.TryGetValue(id, out box) && box != null;
        }

        internal long ReserveStoredId(BoxNetworkState state)
        {
            if (!_host)
            {
                throw new InvalidOperationException("Only the host may reserve a stored box ID.");
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var id = AllocateId();
            state.BoxNetworkId = id;
            _storedBoxes[id] = CloneState(state);
            return id;
        }

        internal void HostMarkStored(long id, BoxNetworkState state)
        {
            if (!_host || id <= 0 || state == null)
            {
                throw new InvalidOperationException("A valid host box ID and descriptor are required to store a box.");
            }

            state.BoxNetworkId = id;
            _storedBoxes[id] = CloneState(state);
        }

        internal void ClientMarkStored(long id, BoxNetworkState state)
        {
            if (_host)
                throw new InvalidOperationException("The host cannot apply a client stored-box state.");
            if (id <= 0 || state == null)
                throw new InvalidOperationException("A client stored-box state requires an ID and descriptor.");

            state.BoxNetworkId = id;
            _storedBoxes[id] = CloneState(state);
            if (TryGetBox(id, out var box))
            {
                Unbind(id, box);
                DestroyWithoutNotification(box);
            }
        }

        /// <summary>Clears a stored-box descriptor without touching the live object or its binding.
        /// Used when a rejected prediction returns a still-live box to the player's hand.</summary>
        internal void ClientForgetStored(long id)
        {
            if (_host || id <= 0)
            {
                return;
            }

            _storedBoxes.Remove(id);
        }

        internal void BindStoredLiveBox(long id, InteractablePackagingBox_Item box,
            WarehouseBoxState state)
        {
            if (id <= 0 || box == null || state == null)
            {
                throw new InvalidOperationException("A live warehouse box requires its ID and state.");
            }

            var descriptor = ItemDescriptor(id, state.ItemType, state.Amount, state.IsBig,
                box.transform.position, box.transform.rotation);
            if (!_host && TryGetBox(id, out var previous) && !ReferenceEquals(previous, box))
            {
                Unbind(id, previous);
                DestroyWithoutNotification(previous);
            }

            // A stored box is not simulated: the game leaves it frozen from the held state, but a
            // box materialized for the network arrives with physics on and would sag or fall.
            box.SetPhysicsEnabled(false);
            _storedBoxes[id] = descriptor;
            Bind(id, box);
        }

        internal void DetachForWarehouseTake(long id)
        {
            if (id <= 0)
            {
                return;
            }

            if (TryGetBox(id, out var box))
            {
                Unbind(id, box);
            }
        }

        internal InteractablePackagingBox_Item HostMaterializeWarehouseTake(long id,
            EItemType itemType, int amount, bool isBig)
        {
            if (!_host || id <= 0 || !IsValidItem(itemType, amount))
            {
                return null;
            }

            var descriptor = ItemDescriptor(id, itemType, amount, isBig, Vector3.zero,
                Quaternion.identity);
            var box = MaterializeItem(descriptor);
            if (box == null)
            {
                return null;
            }

            _storedBoxes.Remove(id);
            Bind(id, box);
            _broadcast(CreateMessage(id, box));
            return box;
        }

        internal void HostBindWarehouseTake(long id, InteractablePackagingBox_Item box)
        {
            if (!_host || id <= 0 || box == null)
            {
                throw new InvalidOperationException("A host warehouse take requires its reserved ID and box.");
            }

            _storedBoxes.Remove(id);
            Bind(id, box);
            _broadcast(CreateMessage(id, box));
        }

        internal bool ClientEnsureWarehouseTake(long id, EItemType itemType, int amount, bool isBig,
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
                    throw new InvalidOperationException("Warehouse take ID is not an item box: " + id);
                return true;
            }

            var descriptor = ItemDescriptor(id, itemType, amount, isBig, Vector3.zero,
                Quaternion.identity);
            box = MaterializeItem(descriptor);
            if (box == null)
                throw new InvalidOperationException("Could not materialize warehouse take box " + id + ".");

            _storedBoxes.Remove(id);
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

            Unbind(id, box);
            _furnitureEntityIds.Remove(id);
            // A record-backed warehouse destroys the live object after the ID has moved into
            // record state. That is a representation change, not a network destruction.
            if (_storedBoxes.ContainsKey(id))
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

        internal bool ClientNotifyDestroyed(InteractablePackagingBox box)
        {
            if (_host || _applyingRemote > 0 || box == null
                || (!_idsByBox.TryGetValue(box, out var id)
                    && !_unboundCandidates.Remove(box)))
            {
                return true;
            }

            if (id <= 0)
            {
                return true;
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
                return true;
            }

            var descriptor = DescribeKnown(id, box);
            var intent = new BoxDestroyRequestMessage { BoxNetworkId = id };
            WorldPrediction.Predict(WorldPrediction.BoxesScope, intent,
                () =>
                {
                    Unbind(id, box);
                    DestroyWithoutNotification(box);
                },
                () => ClientApplyCreated(descriptor));
            return false;
        }

        internal BoxCreatedMessage DescribeKnown(long id, InteractablePackagingBox box)
            => CreateMessage(id, box);

        internal bool HostDestroyRequested(long id, Guid predictionId)
        {
            if (!_host || id <= 0)
            {
                return false;
            }

            if (TryGetBox(id, out var box))
            {
                HostPredictionId = predictionId;
                try
                {
                    box.OnDestroyed();
                }
                finally
                {
                    HostPredictionId = Guid.Empty;
                }
                return true;
            }

            if (_storedBoxes.Remove(id))
            {
                _furnitureEntityIds.Remove(id);
                _broadcast(new BoxDestroyedMessage
                {
                    PredictionId = predictionId,
                    BoxNetworkId = id,
                });
                return true;
            }
            return false;
        }

        internal void ClientApplyCreated(BoxCreatedMessage message)
        {
            var state = message.Box;
            if (state.Kind == BoxNetworkKind.Furniture
                && !PlacementApi.IsPlacementIdentityReady)
            {
                _pendingFurniture[state.BoxNetworkId] = message;
                return;
            }

            if (TryGetBox(state.BoxNetworkId, out var existing))
            {
                // A held box's pose belongs to its hand/remote anchor, not to the host's spawn
                // pose. Applying it here yanked a just-taken box out of the hand (a take
                // announces the replacement box's spawn pose before the take itself).
                if (!IsBeingHeld(existing))
                {
                    ApplyPose(existing, state.Position, state.Rotation);
                }

                _storedBoxes.Remove(state.BoxNetworkId);
                _unboundCandidates.Remove(existing);
                if (state.Kind == BoxNetworkKind.Furniture)
                {
                    _furnitureEntityIds[state.BoxNetworkId] = message.StableEntityId;
                }

                CoopPlugin.Log.LogInfo("[box-id] refreshed authoritative box id="
                    + state.BoxNetworkId + " kind=" + state.Kind + ".");
                return;
            }

            var box = FindAdoptable(state, message.StableEntityId) ?? Materialize(state,
                message.StableEntityId);
            if (box == null)
            {
                throw new InvalidOperationException("Could not materialize authoritative box id="
                    + state.BoxNetworkId + " kind=" + state.Kind + ".");
            }

            _storedBoxes.Remove(state.BoxNetworkId);
            Bind(state.BoxNetworkId, box);
            _unboundCandidates.Remove(box);
            if (state.Kind == BoxNetworkKind.Furniture)
            {
                _furnitureEntityIds[state.BoxNetworkId] = message.StableEntityId;
            }

            CoopPlugin.Log.LogInfo("[box-id] adopted candidate id=" + state.BoxNetworkId
                + " kind=" + state.Kind + " object=" + box.name + ".");
            ApplyPose(box, state.Position, state.Rotation);
            if (box is InteractablePackagingBox_Item itemBox)
            {
                ApplyItemState(itemBox, state);
            }
        }

        internal void ClientApplyDestroyed(BoxDestroyedMessage message)
        {
            var wasPending = _pendingFurniture.Remove(message.BoxNetworkId);
            _furnitureEntityIds.Remove(message.BoxNetworkId);
            var wasStored = _storedBoxes.Remove(message.BoxNetworkId);
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
                // (unboxing) while the host's BoxCreated is still deferred or the local object was
                // only ever an unbound candidate, so the authoritative destroy can legitimately
                // name an id this peer never bound. Recognizing it as already applied is the
                // correct client behavior; throwing here used to tear the session down.
                if (message.BoxNetworkId > 0)
                {
                    CoopPlugin.Log.LogInfo("[box-id] authoritative destroy for already-gone box id="
                        + message.BoxNetworkId + " pending=" + wasPending + " stored=" + wasStored
                        + ".");
                }

                return;
            }

            Unbind(message.BoxNetworkId, box);
            DestroyWithoutNotification(box);
        }

        internal void ClientCompleteBaseline()
        {
            RetryPendingFurniture();

            // Candidates may have been created while the ordered baseline was in flight. Keep
            // them until their host descriptor arrives; deleting them at the marker races the
            // deferred BoxCreated event.
        }

        internal void ClientForgetPhysical(long id, InteractablePackagingBox box)
        {
            if (_host || id <= 0 || box == null)
            {
                return;
            }

            _unboundCandidates.Remove(box);
            Unbind(id, box);
            _storedBoxes.Remove(id);
            DestroyWithoutNotification(box);
        }

        /// <summary>
        /// Appends a frozen creation snapshot to a host-owned baseline work item. The caller
        /// owns send and completion ordering; this method must not send or emit the completion
        /// marker itself.
        /// </summary>
        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null)
            {
                return;
            }

            foreach (var pair in _boxesById)
            {
                if (pair.Value != null && !(pair.Value is InteractablePackagingBox_Item item
                    && item.m_IsStored))
                {
                    append(CreateMessage(pair.Key, pair.Value));
                }
            }
        }

        private BoxCreatedMessage CreateMessage(long id, InteractablePackagingBox box)
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
                _furnitureEntityIds[id] = entityId;
            }
            else
            {
                _furnitureEntityIds.Remove(id);
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

        private long AllocateId()
        {
            if (_nextId <= 0 || _nextId == long.MaxValue)
            {
                throw new InvalidOperationException("Box network ID space exhausted.");
            }

            return _nextId++;
        }

        private void Bind(long id, InteractablePackagingBox box)
        {
            if (id <= 0 || box == null)
            {
                throw new InvalidOperationException("Cannot bind an invalid box network ID.");
            }

            if (_boxesById.TryGetValue(id, out var previous) && previous != null
                && !ReferenceEquals(previous, box))
            {
                _idsByBox.Remove(previous);
            }

            if (_idsByBox.TryGetValue(box, out var previousId) && previousId != id)
            {
                _boxesById.Remove(previousId);
            }

            _boxesById[id] = box;
            _idsByBox[box] = id;
        }

        private void Unbind(long id, InteractablePackagingBox box)
        {
            _boxesById.Remove(id);
            if (box != null)
            {
                _idsByBox.Remove(box);
            }
        }

        private static BoxNetworkState Describe(long id, InteractablePackagingBox box)
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

        internal static BoxNetworkState ItemDescriptor(long id, EItemType itemType, int itemCount,
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
                        throw new InvalidOperationException("Unsupported authoritative box kind "
                            + state.Kind + ".");
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
                throw new InvalidOperationException("Authoritative furniture identity could not be resolved or created: "
                    + furnitureEntityId);
            }

            CoopPlugin.Log.LogInfo("[box-furniture] created host-spawned furniture for box id="
                + state.BoxNetworkId + " entity=" + furnitureEntityId + ".");
            return created.GetPackagingBoxShelf();
        }

        private InteractablePackagingBox FindAdoptable(BoxNetworkState state, string furnitureEntityId)
        {
            switch (state.Kind)
            {
                case BoxNetworkKind.Item:
                    return FindItem(state);
                case BoxNetworkKind.Card:
                    return FindCard(state);
                case BoxNetworkKind.Furniture:
                    // Prefer the exact placement identity. If it cannot be resolved yet (a
                    // play-table/other furniture box whose entity id has not been bound, or a
                    // locally spawned delivery box the host is only now announcing), adopt this
                    // peer's matching unbound candidate instead of materializing a duplicate.
                    return ResolveFurniturePackage(furnitureEntityId, state.FurnitureObjectType)
                        ?? FindUnboundFurnitureCandidate(state.FurnitureObjectType, state.Position);
                default:
                    return null;
            }
        }

        private InteractablePackagingBox_Item FindItem(BoxNetworkState state)
        {
            var boxes = RestockManager.GetItemPackagingBoxList();
            InteractablePackagingBox_Item closest = null;
            var closestDistance = float.MaxValue;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var candidate = boxes[i];
                if (candidate == null || _idsByBox.ContainsKey(candidate)
                    || candidate.m_IsBigBox != state.IsBig || candidate.m_ItemCompartment == null
                    || candidate.m_ItemCompartment.GetItemType() != state.ItemType
                    || candidate.m_ItemCompartment.GetItemCount() != state.ItemCount)
                {
                    continue;
                }

                var distance = (candidate.transform.position - state.Position).sqrMagnitude;
                if (distance < closestDistance)
                {
                    closest = candidate;
                    closestDistance = distance;
                }
            }

            return closest;
        }

        private InteractablePackagingBox_Card FindCard(BoxNetworkState state)
        {
            var boxes = RestockManager.GetCardPackagingBoxList();
            InteractablePackagingBox_Card closest = null;
            var closestDistance = float.MaxValue;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var candidate = boxes[i];
                if (candidate == null || _idsByBox.ContainsKey(candidate)
                    || candidate.GetCardDataList()?.Count != state.Cards.Count)
                {
                    continue;
                }

                var distance = (candidate.transform.position - state.Position).sqrMagnitude;
                if (distance < closestDistance)
                {
                    closest = candidate;
                    closestDistance = distance;
                }
            }

            return closest;
        }

        /// <summary>Finds this peer's not-yet-authoritative furniture package of the expected type,
        /// nearest the authoritative pose. Used when the placement identity is not resolvable, so a
        /// runtime-purchased furniture box (play tables included) can still adopt its local wrapper
        /// instead of failing the authoritative creation.</summary>
        private InteractablePackagingBox FindUnboundFurnitureCandidate(EObjectType furnitureObjectType,
            Vector3 position)
        {
            InteractablePackagingBox_Shelf best = null;
            var bestDistance = float.MaxValue;
            foreach (var candidate in _unboundCandidates)
            {
                if (candidate is not InteractablePackagingBox_Shelf shelf
                    || !TryGetBoxedFurniture(shelf, out var furniture)
                    || furniture.m_ObjectType != furnitureObjectType)
                {
                    continue;
                }

                var distance = (shelf.transform.position - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = shelf;
                }
            }

            return best;
        }

        private InteractablePackagingBox_Shelf ResolveFurniturePackage(string furnitureEntityId,
            EObjectType furnitureObjectType)
        {
            if (!PlacementApi.TryResolveBoxableFurnitureEntityId(furnitureEntityId,
                WorldMessageMetadata.FurnitureIdentityScope,
                furnitureObjectType, out var objectComponent, false))
            {
                return null;
            }

            var package = objectComponent.GetPackagingBoxShelf();
            if (package == null)
            {
                objectComponent.BoxUpObject(false);
                package = objectComponent.GetPackagingBoxShelf();
            }

            return package;
        }

        private void DestroyWithoutNotification(InteractablePackagingBox box)
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

                box.OnDestroyed();
            }
            finally
            {
                _applyingRemote--;
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
                        ItemSpawnManager.DisableItem(items[i]);
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

        private static BoxNetworkState CloneState(BoxNetworkState source)
        {
            var clone = new BoxNetworkState
            {
                BoxNetworkId = source.BoxNetworkId,
                Kind = source.Kind,
                Position = source.Position,
                Rotation = source.Rotation,
                ItemType = source.ItemType,
                ItemCount = source.ItemCount,
                IsBig = source.IsBig,
                FurnitureObjectType = source.FurnitureObjectType,
            };
            if (source.Cards != null)
            {
                for (var i = 0; i < source.Cards.Count; i++)
                {
                    var card = source.Cards[i];
                    if (card != null)
                    {
                        clone.Cards.Add(new BoxCardState
                        {
                            ExpansionType = card.ExpansionType,
                            MonsterType = card.MonsterType,
                            BorderType = card.BorderType,
                            IsFoil = card.IsFoil,
                            IsDestiny = card.IsDestiny,
                            IsChampionCard = card.IsChampionCard,
                            IsNew = card.IsNew,
                            CardGrade = card.CardGrade,
                            GradedCardIndex = card.GradedCardIndex,
                        });
                    }
                }
            }

            return clone;
        }
    }
}
