using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private void InstallPlayerShelfInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(AddItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RemoveItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TakeItemToHandPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(SetCompartmentItemTypePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PutItemOnShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TakeItemFromShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(BoxAddToShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(BoxRemoveFromShelfScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RemoveLabelPatch)).Patch();
        }

        [MessageHandler(typeof(ShelfItemAddRequestMessage))]
        private void HandleShelfItemAdd(MessageContext context, ShelfItemAddRequestMessage message)
        {
            if (_context == null || !_context.InGame() || !IsFullyJoinedSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (!ApplyShelfAction(message))
                    return false;
                BroadcastWorld(new ShelfItemAddMessage
                {
                    PredictionId = message.PredictionId,
                    ShelfKey = message.ShelfKey,
                    Compartment = message.Compartment,
                    ItemType = message.ItemType,
                    ItemCount = message.ItemCount,
                });
                return true;
            });
        }

        [MessageHandler(typeof(ShelfItemRemoveRequestMessage))]
        private void HandleShelfItemRemove(MessageContext context, ShelfItemRemoveRequestMessage message)
        {
            if (_context == null || !_context.InGame() || !IsFullyJoinedSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (!ApplyShelfAction(message))
                    return false;
                BroadcastWorld(new ShelfItemRemoveMessage
                {
                    PredictionId = message.PredictionId,
                    ShelfKey = message.ShelfKey,
                    Compartment = message.Compartment,
                    ItemType = message.ItemType,
                    ItemCount = message.ItemCount,
                });
                return true;
            });
        }

        internal void ResetPlayerShelfInteractionState()
        {
            _shelfInteraction?.Reset();
        }

        private ShelfInteraction.LocalMutation CaptureShelfMutation(ShelfCompartment compartment,
            bool add, EItemType itemType, Item item)
        {
            return _shelfInteraction == null ? default
                : _shelfInteraction.CaptureLocalMutation(compartment, add, itemType, item);
        }

        private void PublishShelfAdd(ShelfCompartment compartment, ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishAdd(compartment, mutation);
        }

        private void PublishShelfRemove(ShelfCompartment compartment,
            ShelfInteraction.LocalMutation mutation)
        {
            _shelfInteraction?.PublishRemove(compartment, mutation);
        }

        /// <summary>A shelf's label (its compartment type, shown on the price tag even with no
        /// items) was removed. Removing a label clears the type on an empty compartment, which
        /// fires neither AddItem nor RemoveItem, so it needs its own publish or the other players
        /// keep showing the label. Only the removal (type -> None) is shared: an empty compartment
        /// briefly takes the incoming type before the first item lands, and that is not a label.</summary>
        private void PublishLabelChange(ShelfCompartment compartment, EItemType previousType,
            bool allowWarehouse = false)
        {
            if (_shelfInteraction == null || compartment == null || !_context.InGame()
                || compartment.GetItemCount() > 0
                || compartment.GetItemType() != EItemType.None
                || previousType == EItemType.None)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[shelf] label removed on " + compartment.name + " ("
                + previousType + ").");
            _shelfInteraction.PublishRemove(compartment,
                _shelfInteraction.CaptureLabelChange(compartment, allowWarehouse));
        }

        private bool ApplyShelfAction(ShelfInteractionMessage message)
        {
            return _shelfInteraction != null && _shelfInteraction.ApplyIncoming(message);
        }

        private void EnterPlayerShelfMutation()
        {
            _shelfInteraction?.EnterPlayerMutation();
        }

        private void ExitPlayerShelfMutation()
        {
            _shelfInteraction?.ExitPlayerMutation();
        }

        [HarmonyPatch(typeof(ShelfCompartment), "AddItem")]
        private static class AddItemPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ShelfCompartment __instance, Item item,
                bool addToFront,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, true,
                        item == null ? EItemType.None : item.GetItemType(), item);
                return !__state.SuppressVanilla;
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance,
                ShelfInteraction.LocalMutation __state)
            {
                _instance?.PublishShelfAdd(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "EvaluatePutItemOnShelf")]
        private static class PutItemOnShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                _instance?.EnterPlayerShelfMutation();
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                _instance?.ExitPlayerShelfMutation();
            }
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "EvaluateTakeItemFromShelf")]
        private static class TakeItemFromShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                _instance?.EnterPlayerShelfMutation();
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                _instance?.ExitPlayerShelfMutation();
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnHoldStateLeftMousePress")]
        private static class BoxAddToShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool isPlayer, out bool __state)
            {
                __state = isPlayer;
                if (__state)
                {
                    _instance?.EnterPlayerShelfMutation();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnHoldStateRightMousePress")]
        private static class BoxRemoveFromShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool isPlayer, out bool __state)
            {
                __state = isPlayer;
                if (__state)
                {
                    _instance?.EnterPlayerShelfMutation();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "RemoveItem")]
        private static class RemoveItemPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ShelfCompartment __instance, Item item,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, false,
                        item == null ? EItemType.None : item.GetItemType(), item);
                return !__state.SuppressVanilla;
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance,
                ShelfInteraction.LocalMutation __state)
            {
                _instance?.PublishShelfRemove(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "TakeItemToHand")]
        private static class TakeItemToHandPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ShelfCompartment __instance, bool getLastItem,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, false, EItemType.None, null);
                return !__state.SuppressVanilla;
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, Item __result,
                ShelfInteraction.LocalMutation __state)
            {
                if (__result != null)
                {
                    _instance?.PublishShelfRemove(__instance, __state);
                }
            }
        }

        [HarmonyPatch(typeof(ShelfCompartment), "SetCompartmentItemType")]
        private static class SetCompartmentItemTypePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, out EItemType __state)
                => __state = __instance == null ? EItemType.None : __instance.GetItemType();

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, EItemType __state)
                => _instance?.PublishLabelChange(__instance, __state);
        }

        /// <summary>Warehouse labels are removed from the price tag the same way player-shelf
        /// labels are, but the warehouse compartment is outside the player-shelf inventory
        /// protocol so the compartment-type hook above ignores it. Publish only that explicit
        /// removal; box-driven type changes stay on the warehouse box protocol.</summary>
        [HarmonyPatch(typeof(ShelfCompartment), "RemoveLabel")]
        private static class RemoveLabelPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ShelfCompartment __instance, out EItemType __state)
                => __state = __instance == null ? EItemType.None : __instance.GetItemType();

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, EItemType __state)
            {
                if (__instance != null && __instance.GetWarehouseShelf() != null)
                {
                    _instance?.PublishLabelChange(__instance, __state, allowWarehouse: true);
                }
            }
        }
    }

    [NetworkMessage]
    public class ShelfItemAddMessage : ShelfInteractionMessage
    {
    }

    [NetworkMessage]
    public sealed class ShelfItemAddRequestMessage : ShelfItemAddMessage
    {
    }

    [NetworkMessage]
    public class ShelfItemRemoveMessage : ShelfInteractionMessage
    {
    }

    [NetworkMessage]
    public sealed class ShelfItemRemoveRequestMessage : ShelfItemRemoveMessage
    {
    }

    /// <summary>Wire state for one player-induced shelf stock mutation. The shelf is addressed by
    /// its host-assigned placement identity (the same furniture key card displays and placement
    /// moves already use) plus the compartment index, never by scene name or hierarchy path.</summary>
    public abstract class ShelfInteractionMessage : WorldMessage
    {
        public int ShelfKey;
        public int Compartment;
        public int ItemType;
        public int ItemCount;
    }

    /// <summary>
    /// Standalone player-shelf action protocol. It resolves and rebuilds shelf compartments
    /// directly through the game API, intentionally without a separate legacy sync layer,
    /// transfer ledgers, or
    /// hand/box escrow.
    /// </summary>
    internal sealed class ShelfInteraction
    {
        private const int MaxItemCount = 4096;
        private const int MaxCompartments = 256;

        private readonly Dictionary<string, ShelfInteractionMessage> _pendingClientStates = new();

        private static readonly FieldInfo StoredItemsField =
            AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemList");

        private static readonly FieldInfo CompartmentListField =
            AccessTools.Field(typeof(InteractableObject), "m_ItemCompartmentList");

        private static readonly FieldInfo PosListField =
            AccessTools.Field(typeof(ShelfCompartment), "m_PosList");

        private static readonly FieldInfo HoldItemListField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");

        private static readonly MethodInfo RemoveHoldItemMethod =
            AccessTools.Method(typeof(InteractionPlayerController), "RemoveHoldItem");

        private readonly Action<INetMessage> _broadcast;
        private readonly Action<int, INetMessage> _send;
        private readonly Func<bool> _inGame;
        private readonly bool _host;
        private bool _applying;
        private int _playerMutationDepth;

        internal struct LocalMutation
        {
            public bool Send;
            public bool SuppressVanilla;
            public ShelfInteractionMessage Command;
            public int ShelfKey;
            public int Compartment;
        }

        internal ShelfInteraction(Action<INetMessage> broadcast, Func<bool> inGame)
        {
            if (broadcast == null)
            {
                throw new ArgumentNullException(nameof(broadcast));
            }

            _host = true;
            _broadcast = broadcast;
            _inGame = inGame;
        }

        internal ShelfInteraction(Action<int, INetMessage> send)
        {
            if (send == null)
            {
                throw new ArgumentNullException(nameof(send));
            }

            _send = send;
        }

        internal void Reset()
        {
            _pendingClientStates.Clear();
            _applying = false;
            _playerMutationDepth = 0;
        }

        internal void EnterPlayerMutation() => _playerMutationDepth++;

        internal void ExitPlayerMutation()
        {
            if (_playerMutationDepth <= 0)
            {
                throw new InvalidOperationException("Player shelf-mutation scope ended without a matching start.");
            }

            _playerMutationDepth--;
        }

        internal LocalMutation CaptureLocalMutation(ShelfCompartment compartment, bool isAdd,
            EItemType requestedType, Item item)
        {
            // The host mirrors every in-game player-shelf mutation it performs, not only the ones
            // inside a local-player scope: NPC customers (and workers) take/return items outside
            // that scope, so gating on the player scope silently dropped their shelf updates. The
            // client still only forwards its own player mutations. _applying keeps a mirrored
            // apply (and the load-time shelf population) from being echoed back.
            if (_applying || !IsPlayerShelf(compartment)
                || (_host
                    ? _broadcast == null || _inGame == null || !_inGame()
                    : _send == null || _playerMutationDepth == 0))
            {
                return default;
            }

            if (!TryMakeKey(compartment, out var shelfKey, out var compartmentIndex))
            {
                CoopPlugin.Log.LogWarning("[shelf] no furniture id for " + compartment.name
                    + "; " + (isAdd ? "placement" : "removal") + " not synced.");
                return default;
            }

            var beforeType = compartment.GetItemType();
            var beforeCount = compartment.GetItemCount();
            var targetType = isAdd && requestedType != EItemType.None
                ? requestedType : beforeType;
            var targetCount = isAdd ? beforeCount + 1 : Math.Max(0, beforeCount - 1);
            var message = isAdd ? new ShelfItemAddRequestMessage() as ShelfInteractionMessage
                : new ShelfItemRemoveRequestMessage();
            message.ShelfKey = shelfKey;
            message.Compartment = compartmentIndex;
            message.ItemType = (int)targetType;
            message.ItemCount = targetCount;

            // An add lets the game's own AddItem move the real item, so a rejected add can hand
            // that exact item back. A remove is not capacity-limited and needs no host
            // admission: the game's own method must run so the item actually reaches the hand
            // (or a box), and the resulting count is mirrored as a plain intent. Suppressing the
            // vanilla remove here deleted the item outright.
            var suppressVanilla = false;
            if (!_host)
            {
                if (isAdd)
                {
                    WorldPrediction.Predict(WorldPrediction.ShelvesScope, message,
                        () => AddCapturedItem(compartment, item),
                        () => ReturnCapturedItem(compartment, item),
                        applyLocally: false);
                }
                else
                {
                    message = null;
                }
            }

            return new LocalMutation
            {
                Send = true,
                SuppressVanilla = suppressVanilla,
                Command = _host ? null : message,
                ShelfKey = shelfKey,
                Compartment = compartmentIndex,
            };
        }

        private void AddCapturedItem(ShelfCompartment compartment, Item item)
        {
            if (compartment == null || item == null)
            {
                return;
            }

            try
            {
                _applying = true;
                var controller = SceneRef<InteractionPlayerController>.Get();
                if (controller != null && HoldItemListField?.GetValue(controller) is List<Item> held
                    && held.Contains(item))
                {
                    RemoveHoldItemMethod?.Invoke(controller, new object[] { item });
                }

                if (compartment.GetItemCount() <= 0
                    && compartment.GetItemType() != item.GetItemType())
                {
                    compartment.SetCompartmentItemType(item.GetItemType());
                    compartment.CalculatePositionList();
                }

                compartment.AddItem(item, addToFront: false);
                PositionStored(compartment);
            }
            finally
            {
                _applying = false;
            }
        }

        private void ReturnCapturedItem(ShelfCompartment compartment, Item item)
        {
            if (compartment == null || item == null)
            {
                return;
            }

            try
            {
                _applying = true;
                var stored = StoredItemsField?.GetValue(compartment) as List<Item>;
                if (stored == null || !stored.Contains(item))
                {
                    return;
                }

                compartment.RemoveItem(item);
                var controller = SceneRef<InteractionPlayerController>.Get();
                if (controller != null)
                {
                    controller.AddHoldItemToFront(item);
                }

                PositionStored(compartment);
                CoopPlugin.Log.LogInfo("[shelf] rolled a rejected placement back to hand on "
                    + compartment.name + ".");
            }
            finally
            {
                _applying = false;
            }
        }

        internal void PublishAdd(ShelfCompartment compartment, LocalMutation mutation)
        {
            Publish(compartment, mutation, true);
        }

        internal void PublishRemove(ShelfCompartment compartment, LocalMutation mutation)
        {
            Publish(compartment, mutation, false);
        }

        /// <summary>An explicit label removal on an empty shelf. Unlike an item move this has no
        /// count change and no prediction; the compartment's resulting (type, count) is published
        /// as a plain remove, which clears the label on the far side. Warehouse compartments share
        /// the label concept with player shelves, so the caller opts them in.</summary>
        internal LocalMutation CaptureLabelChange(ShelfCompartment compartment,
            bool allowWarehouse = false)
        {
            if (_applying
                || !(allowWarehouse ? IsSyncableShelf(compartment) : IsPlayerShelf(compartment))
                || (_host ? _broadcast == null : _send == null))
            {
                return default;
            }

            if (!TryMakeKey(compartment, out var shelfKey, out var compartmentIndex))
            {
                CoopPlugin.Log.LogWarning("[shelf] no furniture id for " + compartment.name
                    + "; label removal not synced.");
                return default;
            }

            return new LocalMutation
            {
                Send = true,
                SuppressVanilla = false,
                Command = null,
                ShelfKey = shelfKey,
                Compartment = compartmentIndex,
            };
        }

        internal bool ApplyIncoming(ShelfInteractionMessage message)
        {
            if (_host && !Validate(message))
            {
                return false;
            }

            var compartment = ResolveCompartment(message.ShelfKey, message.Compartment);
            if (compartment == null)
            {
                if (!_host)
                {
                    _pendingClientStates[ShelfKey(message)] = message;
                    return true;
                }

                return false;
            }

            if (_host && !ResolveHostResult(compartment, message))
            {
                return false;
            }

            try
            {
                _applying = true;
                ApplyState(compartment, message.ItemType, message.ItemCount);
            }
            finally
            {
                _applying = false;
            }

            return true;
        }

        private static bool ResolveHostResult(ShelfCompartment compartment,
            ShelfInteractionMessage message)
        {
            var count = compartment.GetItemCount();
            var type = compartment.GetItemType();
            if (message is ShelfItemAddRequestMessage)
            {
                var requestedType = (EItemType)message.ItemType;
                if (count > 0 && type != requestedType)
                {
                    return false;
                }

                // Real slot capacity: the first request that fits wins; a later request that no
                // longer fits is rejected and the requesting client rolls its placement back.
                var capacity = Mathf.Max(compartment.GetMaxItemCount(),
                    compartment.GetItemPosListCount());
                if (count > 0 && capacity > 0 && count >= capacity)
                {
                    CoopPlugin.Log.LogInfo("[shelf] rejected placement on " + compartment.name
                        + ": full (" + count + "/" + capacity + ").");
                    return false;
                }

                message.ItemType = (int)requestedType;
                message.ItemCount = count + 1;
                return true;
            }

            if (message is ShelfItemRemoveRequestMessage)
            {
                if (count <= 0)
                {
                    // An empty shelf still carries a label (its type) on the price tag. Removing
                    // that label clears the type; there are no items to decrement.
                    if (type == EItemType.None)
                    {
                        return false;
                    }

                    message.ItemType = (int)EItemType.None;
                    message.ItemCount = 0;
                    return true;
                }

                message.ItemType = (int)type;
                message.ItemCount = count - 1;
                return true;
            }

            return false;
        }

        internal void FlushClientState()
        {
            if (_host || _pendingClientStates.Count == 0)
                return;

            var pending = new List<ShelfInteractionMessage>(_pendingClientStates.Values);
            _pendingClientStates.Clear();
            for (var i = 0; i < pending.Count; i++)
            {
                ApplyIncoming(pending[i]);
            }
        }

        private void Publish(ShelfCompartment compartment, LocalMutation mutation, bool isAdd)
        {
            if (!mutation.Send || compartment == null)
            {
                return;
            }

            if (!_host && mutation.Command != null)
                return;

            ShelfInteractionMessage message;
            if (isAdd)
            {
                message = _host ? new ShelfItemAddMessage() : new ShelfItemAddRequestMessage();
            }
            else
            {
                message = _host ? new ShelfItemRemoveMessage() : new ShelfItemRemoveRequestMessage();
            }
            message.ShelfKey = mutation.ShelfKey;
            message.Compartment = mutation.Compartment;
            message.ItemType = (int)compartment.GetItemType();
            message.ItemCount = compartment.GetItemCount();

            if (_host)
            {
                _broadcast(message);
            }
            else
            {
                _send(1, message);
            }
        }

        private static bool IsSyncableShelf(ShelfCompartment compartment)
        {
            // Include inactive parents: a closed box deactivates its item compartment, and
            // without this the box would be mistaken for a player shelf.
            return compartment != null
                && compartment.GetComponentInParent<InteractablePackagingBox>(true) == null;
        }

        private static bool IsPlayerShelf(ShelfCompartment compartment)
            => IsSyncableShelf(compartment) && compartment.GetWarehouseShelf() == null;

        private static string ShelfKey(ShelfInteractionMessage message)
            => WorldMessageMetadata.StableEntityId(message);

        /// <summary>Builds the host-assigned furniture identity for a compartment's owning shelf
        /// plus the compartment's index in that shelf's compartment list. Fails for a compartment
        /// that is not owned by a placed shelf (for example one inside a packaging box) or whose
        /// shelf has no host identity yet.</summary>
        private static bool TryMakeKey(ShelfCompartment compartment, out int shelfKey,
            out int compartmentIndex)
        {
            shelfKey = 0;
            compartmentIndex = -1;
            if (compartment == null)
            {
                return false;
            }

            // A warehouse compartment shares its GameObject with its InteractableStorageCompartment,
            // which is an InteractableObject but not a placement object, so walk up until we reach
            // the shelf whose compartment list actually owns this compartment.
            for (var node = compartment.transform; node != null;)
            {
                var owner = node.GetComponentInParent<InteractableObject>(true);
                if (owner == null)
                {
                    return false;
                }

                if (CompartmentListField?.GetValue(owner) is List<ShelfCompartment> compartments)
                {
                    var index = compartments.IndexOf(compartment);
                    if (index >= 0 && index < MaxCompartments)
                    {
                        var kind = PlacementInterop.FindKind(owner);
                        if (kind >= 0 && PlacementApi.TryMakeObjectKey(kind, owner, out shelfKey))
                        {
                            compartmentIndex = index;
                            return true;
                        }
                    }
                }

                node = owner.transform.parent;
            }

            return false;
        }

        /// <summary>Resolves the shelf the host assigned an identity to, then picks the addressed
        /// compartment. Exact, unlike the name/path/position search it replaces.</summary>
        private static ShelfCompartment ResolveCompartment(int shelfKey, int compartmentIndex)
        {
            if (compartmentIndex < 0 || compartmentIndex >= MaxCompartments
                || PlacementApi.ResolveObjectByKey(shelfKey) is not InteractableObject owner
                || CompartmentListField?.GetValue(owner) is not List<ShelfCompartment> compartments
                || compartmentIndex >= compartments.Count)
            {
                return null;
            }

            return compartments[compartmentIndex];
        }

        /// <summary>Merges an authoritative compartment state onto the live shelf by applying only
        /// the difference. Rapid deltas then add/remove single items instead of rebuilding the
        /// whole compartment, which previously flickered and could desync the stored-item list
        /// from the price-tag count.</summary>
        private void ApplyState(ShelfCompartment compartment, int itemType, int itemCount)
        {
            var type = (EItemType)itemType;
            if (itemCount <= 0 || type == EItemType.None)
            {
                Clear(compartment);
                compartment.SetCompartmentItemType(type);
                return;
            }

            if (compartment.GetItemType() != type)
            {
                Clear(compartment);
                compartment.SetCompartmentItemType(type);
                compartment.CalculatePositionList();
            }
            else if (compartment.GetItemPosListCount() == 0)
            {
                compartment.CalculatePositionList();
            }

            var current = compartment.GetItemCount();
            if (current == itemCount)
            {
                return;
            }

            if (current < itemCount)
            {
                for (var i = current; i < itemCount; i++)
                {
                    AddOne(compartment, type);
                }
            }
            else
            {
                for (var i = current; i > itemCount; i--)
                {
                    RemoveOne(compartment);
                }
            }

            CoopPlugin.Log.LogInfo("[shelf] merged " + compartment.name + " type=" + type
                + " count " + current + "->" + itemCount + ".");
        }

        private static void AddOne(ShelfCompartment compartment, EItemType itemType)
        {
            var meshData = InventoryBase.GetItemMeshData(itemType);
            if (meshData == null)
            {
                return;
            }

            var item = ItemSpawnManager.GetItem(compartment.m_StoredItemListGrp);
            item.SetMesh(meshData.mesh, meshData.material, itemType, meshData.meshSecondary,
                meshData.materialSecondary, meshData.materialList);
            item.gameObject.SetActive(true);
            compartment.AddItem(item, addToFront: false);
            PositionStored(compartment);
        }

        private static void RemoveOne(ShelfCompartment compartment)
        {
            if (StoredItemsField?.GetValue(compartment) is not List<Item> items || items.Count == 0)
            {
                return;
            }

            var item = items[items.Count - 1];
            compartment.RemoveItem(item);
            if (item != null)
            {
                ItemSpawnManager.DisableItem(item);
            }
        }

        private static void PositionStored(ShelfCompartment compartment)
        {
            if (StoredItemsField?.GetValue(compartment) is not List<Item> items
                || PosListField?.GetValue(compartment) is not List<Transform> slots)
            {
                compartment.RefreshItemPosition(true);
                return;
            }

            for (var i = 0; i < items.Count && i < slots.Count; i++)
            {
                var item = items[i];
                var slot = slots[i];
                if (item == null || slot == null)
                {
                    continue;
                }

                item.transform.position = slot.transform.position;
                item.transform.rotation = compartment.m_StartLoc.rotation;
                item.transform.localScale = slot.transform.localScale;
            }
        }

        private static void Clear(ShelfCompartment compartment)
        {
            if (StoredItemsField?.GetValue(compartment) is List<Item> items)
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
            else
            {
                while (compartment.GetItemCount() > 0)
                {
                    var item = compartment.GetLastItem();
                    if (item == null)
                    {
                        break;
                    }

                    compartment.RemoveItem(item);
                    ItemSpawnManager.DisableItem(item);
                }
            }

            compartment.PreSpawnItemUpdate(0);
        }

        private bool Validate(ShelfInteractionMessage message)
        {
            if (message == null || message.Compartment < 0 || message.Compartment >= MaxCompartments
                || message.ItemCount < 0 || message.ItemCount > MaxItemCount)
            {
                return false;
            }

            var kind = message.ShelfKey >> 24;
            return kind >= 0 && kind < PlacementApi.KindCount
                && PlacementApi.ObjectIdFromObjectKey(message.ShelfKey)
                    != PlacementIdentity.Invalid
                && CanResolveItemType(message.ItemType, message.ItemCount);
        }

        private bool CanResolveItemType(int itemType, int itemCount)
        {
            if (itemType == (int)EItemType.None)
            {
                return itemCount == 0;
            }

            try
            {
                var data = InventoryBase.GetItemData((EItemType)itemType);
                var dimension = data != null ? data.itemDimension : Vector3.zero;
                return dimension.x > 0f && dimension.y > 0f && dimension.z > 0f;
            }
            catch (Exception)
            {
                return false;
            }
        }

    }
}
