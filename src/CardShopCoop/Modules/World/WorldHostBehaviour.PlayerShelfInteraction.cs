using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Prediction;
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
            if (_context == null || !_context.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (!ApplyShelfAction(message))
                {
                    RefreshRejectedShelfAction(context, message);
                    return false;
                }

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
            if (_context == null || !_context.InGame() || !IsJoinPhaseSender(context))
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () =>
            {
                if (!ApplyShelfAction(message))
                {
                    RefreshRejectedShelfAction(context, message);
                    return false;
                }

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

        /// <summary>Answers a rejected shelf intent with the compartment's authoritative
        /// (type, count), sent to the requester before the generic rollback so its undo
        /// reconciles to reality instead of inverting a snapshot authority has moved past.</summary>
        private void RefreshRejectedShelfAction(MessageContext context,
            ShelfInteractionMessage message)
        {
            if (context?.Connection == null || _shelfInteraction == null)
            {
                return;
            }

            var refresh = _shelfInteraction.BuildRefresh(message);
            if (refresh != null)
            {
                SendWorldTo(context.Connection.Id, refresh);
            }
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
            private static void Prefix(ShelfCompartment __instance, Item item,
                bool addToFront,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, true,
                        item == null ? EItemType.None : item.GetItemType(), item);
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
            private static void Prefix(out bool __state)
            {
                __state = _instance != null;
                if (__state)
                {
                    _instance.EnterPlayerShelfMutation();
                }
            }

            /// <summary>Releases the scope from the finalizer, so a throw out of the game's own
            /// EvaluatePutItemOnShelf still balances _playerMutationDepth. The finalizer always
            /// runs, so the release lives here only and stays single-shot (ExitPlayerMutation
            /// throws on an imbalanced double exit).</summary>
            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
            }
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "EvaluateTakeItemFromShelf")]
        private static class TakeItemFromShelfScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
            {
                __state = _instance != null;
                if (__state)
                {
                    _instance.EnterPlayerShelfMutation();
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?.ExitPlayerShelfMutation();
                }
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

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
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

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
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
            private static void Prefix(ShelfCompartment __instance, Item item,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, false,
                        item == null ? EItemType.None : item.GetItemType(), item);
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
            private static void Prefix(ShelfCompartment __instance, bool getLastItem,
                out ShelfInteraction.LocalMutation __state)
            {
                __state = _instance == null ? default
                    : _instance.CaptureShelfMutation(__instance, false, EItemType.None, null);
            }

            [HarmonyPostfix]
            private static void Postfix(ShelfCompartment __instance, Item __result,
                ShelfInteraction.LocalMutation __state)
            {
                if (__result != null)
                {
                    // TakeItemToHand is the one removal whose item is only known from the result,
                    // so attach the exact removed item here before the post-hoc prediction.
                    __state.Removed = __result;
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
        public EItemType ItemType;
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

        // Client only: prediction ids of this peer's outstanding optimistic placements per
        // shelf/compartment key. An incoming delta carries the host's absolute count, which does not
        // include these, so the merge target is host count + the still-pending adds (otherwise a
        // delta for a shelf with a pending placement walks the count backwards: client 31 vs host
        // 32). Liveness is derived from PredictionApi, so retiring a prediction (ack or rollback)
        // needs no parallel count.
        private readonly Dictionary<string, List<Guid>> _pendingClientAddIds = new();

        // Client only: prediction ids of this peer's outstanding optimistic take-to-hand removals
        // per shelf/compartment key, the mirror of _pendingClientAddIds. An incoming absolute host
        // delta does not include them, so the merge target must subtract the still-outstanding ones
        // or it backfills a replacement item onto the shelf while the real one is still in the hand.
        private readonly Dictionary<string, List<Guid>> _pendingClientRemoveIds = new();

        // Client only: the exact item each live optimistic placement owns. An authoritative type
        // change clears (pools) the compartment before a rejected placement's undo can return its
        // item, so the clear must hand these back to the player first or the item is lost.
        private readonly Dictionary<Guid, Item> _pendingClientAddItems = new();

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
            public ShelfInteractionMessage Command;
            public int ShelfKey;
            public int Compartment;

            // The exact item a take-to-hand removal moved into the acting hand. Set only by the
            // TakeItemToHand postfix; a rejected take can then be undone by handing that item back
            // through the game's own AddItem. Null for an add, a label removal, and a RemoveItem
            // (held-box) transfer, which stays on the plain-intent path.
            public Item Removed;
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
            _pendingClientAddIds.Clear();
            _pendingClientRemoveIds.Clear();
            _pendingClientAddItems.Clear();
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
            message.ItemType = targetType;
            message.ItemCount = targetCount;

            // On the client, the game's own method performs the mutation: an add moves the real
            // item with AddItem (a rejected add hands that exact item back), and a remove is not
            // capacity-limited, so the game's method must run so the item actually reaches its
            // destination. The client add registers its prediction here; the client take-to-hand
            // removal registers its post-hoc prediction in PublishRemove once the removed item is
            // known. A RemoveItem (held-box) removal stays a plain intent (see PublishRemove).
            if (!_host && isAdd)
            {
                // The game's own AddItem already placed the item. Register the prediction (which
                // sends the intent) and remember its id as an outstanding optimistic placement;
                // the pending count is derived from PredictionApi, not a parallel counter.
                var predictionId = WorldPrediction.Predict(WorldPrediction.ShelvesScope, message,
                    () => AddCapturedItem(compartment, item),
                    () => ReturnCapturedItem(compartment, item));
                RecordPendingAdd(ShelfKey(message), predictionId);
                if (item != null)
                {
                    _pendingClientAddItems[predictionId] = item;
                }
            }

            return new LocalMutation
            {
                Send = true,
                // The client add's prediction owns its send; every removal (and the host) publishes
                // its intent through Publish/PublishRemove instead.
                Command = _host || !isAdd ? null : message,
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

        /// <summary>Client: before an authoritative type change clears a compartment, hand any item
        /// still owned by a live pending placement back to the local player. The placement is about
        /// to be rejected (the authority moved to a different type), and the clear would pool the
        /// item before the placement's undo could return it.</summary>
        private void ReturnPendingItems(ShelfCompartment compartment)
        {
            if (_host || compartment == null || _pendingClientAddItems.Count == 0)
            {
                return;
            }

            if (StoredItemsField?.GetValue(compartment) is not List<Item> stored || stored.Count == 0)
            {
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            for (var i = stored.Count - 1; i >= 0; i--)
            {
                var item = stored[i];
                if (item == null)
                {
                    continue;
                }

                Guid owner = Guid.Empty;
                foreach (var pair in _pendingClientAddItems)
                {
                    if (ReferenceEquals(pair.Value, item) && PredictionApi.IsPending(pair.Key))
                    {
                        owner = pair.Key;
                        break;
                    }
                }

                if (owner == Guid.Empty)
                {
                    continue;
                }

                _pendingClientAddItems.Remove(owner);
                compartment.RemoveItem(item);
                if (controller != null)
                {
                    controller.AddHoldItemToFront(item);
                }

                CoopPlugin.Log.LogInfo("[shelf] returned a pending placement item on "
                    + compartment.name + " before an authoritative clear.");
            }

            PrunePendingAddItems();
            PositionStored(compartment);
        }

        /// <summary>Drops the bookkeeping for placements whose prediction already resolved.</summary>
        private void PrunePendingAddItems()
        {
            if (_pendingClientAddItems.Count == 0)
            {
                return;
            }

            var stale = new List<Guid>();
            foreach (var pair in _pendingClientAddItems)
            {
                if (!PredictionApi.IsPending(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _pendingClientAddItems.Remove(stale[i]);
            }
        }

        internal void PublishAdd(ShelfCompartment compartment, LocalMutation mutation)
        {
            Publish(compartment, mutation, true);
        }

        internal void PublishRemove(ShelfCompartment compartment, LocalMutation mutation)
        {
            if (!mutation.Send || compartment == null)
            {
                return;
            }

            if (_host || mutation.Removed == null)
            {
                // Host mirror, or a removal that moved no hand-bound item. Only TakeItemToHand
                // attaches the removed item (see LocalMutation.Removed). The other client removal,
                // InteractablePackagingBox_Item.RemoveItemFromShelf, moves the item into an open
                // held box in the same call, and the item alone does not identify that box, so its
                // destination cannot be reversed faithfully as a single game-path undo. It keeps
                // the plain id-less intent it has always used.
                Publish(compartment, mutation, false);
                return;
            }

            // TakeItemToHand put the item into the acting hand (the controller adds it after the
            // game method returns). Now that the exact removed item is known, register exactly one
            // post-hoc prediction so a host rejection hands that item back through the game's own
            // AddItem and a replay re-takes it. The intent carries the prediction id, so a
            // hand-bound removal is never sent as an id-less request the host could not disambiguate.
            var removed = mutation.Removed;
            var request = new ShelfItemRemoveRequestMessage
            {
                ShelfKey = mutation.ShelfKey,
                Compartment = mutation.Compartment,
                ItemType = compartment.GetItemType(),
                ItemCount = compartment.GetItemCount(),
            };
            var key = ShelfKey(request);
            var predictionId = WorldPrediction.Predict(WorldPrediction.ShelvesScope, request,
                () => RemoveCapturedItem(compartment, removed),
                () => RestoreCapturedItem(compartment, removed));
            RecordPendingRemove(key, predictionId);
        }

        /// <summary>Re-applies a predicted shelf removal: take the item off the shelf and return it
        /// to the hand, the same two game calls the original take path makes.</summary>
        private void RemoveCapturedItem(ShelfCompartment compartment, Item item)
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
                if (controller != null
                    && HoldItemListField?.GetValue(controller) is List<Item> held
                    && !held.Contains(item))
                {
                    controller.AddHoldItemToFront(item);
                }

                PositionStored(compartment);
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>Undoes a predicted shelf removal: hand the exact removed item back to the
        /// compartment through the game's own AddItem, and take it out of the local hand.</summary>
        private void RestoreCapturedItem(ShelfCompartment compartment, Item item)
        {
            if (compartment == null || item == null)
            {
                return;
            }

            // A rejected removal means the host's compartment did not contain the item (that is
            // the only way a remove request is refused), and the authoritative refresh has just
            // applied that empty state. Re-adding here would resurrect a shelf entry the host
            // does not have; the item stays where the optimistic take put it.
            if (compartment.GetItemCount() <= 0 && compartment.GetItemType() == EItemType.None)
            {
                return;
            }

            try
            {
                _applying = true;
                var controller = SceneRef<InteractionPlayerController>.Get();
                if (controller != null
                    && HoldItemListField?.GetValue(controller) is List<Item> held
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

                // The player-shelf take path removes the last item, so appending restores order.
                compartment.AddItem(item, addToFront: false);
                PositionStored(compartment);
                CoopPlugin.Log.LogInfo("[shelf] rolled a rejected removal back onto "
                    + compartment.name + ".");
            }
            finally
            {
                _applying = false;
            }
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
                var target = message.ItemCount;
                if (!_host)
                {
                    // A pending local add/remove only folds into the merge while the incoming
                    // state keeps this compartment's type. A type change clears the compartment,
                    // so those items no longer exist locally and counting them would duplicate
                    // what the authority describes (and strand the rollback's item).
                    var localType = compartment.GetItemType();
                    if (localType == message.ItemType || localType == EItemType.None)
                    {
                        var key = ShelfKey(message);
                        if (message.ItemType != EItemType.None)
                        {
                            target += PendingAddCount(key);
                        }

                        // Outstanding take-to-hand removals already decremented this peer's live
                        // count; the host's absolute count still includes them, so subtract them
                        // or the merge backfills a replacement for an item still in the player's
                        // hand.
                        target -= PendingRemoveCount(key);
                    }

                    if (target < 0)
                    {
                        target = 0;
                    }
                }

                ApplyState(compartment, message.ItemType, target);
            }
            finally
            {
                _applying = false;
            }

            return true;
        }

        /// <summary>Host: the compartment's authoritative (type, count) as a state message, used to
        /// refresh a rejected add/remove request before its generic rollback. The empty prediction
        /// id makes the requester apply it as a remote state instead of consuming a prediction.</summary>
        internal ShelfItemAddMessage BuildRefresh(ShelfInteractionMessage failed)
        {
            if (failed == null)
            {
                return null;
            }

            var compartment = ResolveCompartment(failed.ShelfKey, failed.Compartment);
            if (compartment == null)
            {
                return null;
            }

            return new ShelfItemAddMessage
            {
                ShelfKey = failed.ShelfKey,
                Compartment = failed.Compartment,
                ItemType = compartment.GetItemType(),
                ItemCount = compartment.GetItemCount(),
            };
        }

        /// <summary>Client: remembers one of this peer's optimistic placements by its shelf key,
        /// so an incoming absolute host delta can add the still-outstanding ones back.</summary>
        private void RecordPendingAdd(string key, Guid predictionId)
        {
            if (!_host)
            {
                RecordPending(_pendingClientAddIds, key, predictionId);
            }
        }

        /// <summary>Client: remembers one of this peer's optimistic take-to-hand removals by its
        /// shelf key, so an incoming absolute host delta can subtract the still-outstanding ones
        /// instead of backfilling items that are still in the player's hand.</summary>
        private void RecordPendingRemove(string key, Guid predictionId)
        {
            if (!_host)
            {
                RecordPending(_pendingClientRemoveIds, key, predictionId);
            }
        }

        private static void RecordPending(Dictionary<string, List<Guid>> pending, string key,
            Guid predictionId)
        {
            if (string.IsNullOrEmpty(key) || predictionId == Guid.Empty)
            {
                return;
            }

            if (!pending.TryGetValue(key, out var ids))
            {
                ids = new List<Guid>();
                pending[key] = ids;
            }

            ids.Add(predictionId);
        }

        /// <summary>Client: how many of this peer's optimistic actions of one kind for the key are
        /// still outstanding. Liveness comes from <see cref="PredictionApi.IsPending"/>, so an ack
        /// or a rollback drops the action with no separate bookkeeping; retired ids are pruned here.</summary>
        private int PendingAddCount(string key) => PendingCount(_pendingClientAddIds, key);

        private int PendingRemoveCount(string key) => PendingCount(_pendingClientRemoveIds, key);

        private int PendingCount(Dictionary<string, List<Guid>> pending, string key)
        {
            if (_host || !pending.TryGetValue(key, out var ids))
            {
                return 0;
            }

            var live = 0;
            for (var i = ids.Count - 1; i >= 0; i--)
            {
                if (PredictionApi.IsPending(ids[i]))
                {
                    live++;
                }
                else
                {
                    ids.RemoveAt(i);
                }
            }

            if (ids.Count == 0)
            {
                pending.Remove(key);
            }

            return live;
        }

        private static bool ResolveHostResult(ShelfCompartment compartment,
            ShelfInteractionMessage message)
        {
            var count = compartment.GetItemCount();
            var type = compartment.GetItemType();
            if (message is ShelfItemAddRequestMessage)
            {
                var requestedType = message.ItemType;
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

                message.ItemType = requestedType;
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

                    message.ItemType = EItemType.None;
                    message.ItemCount = 0;
                    return true;
                }

                message.ItemType = type;
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
            message.ItemType = compartment.GetItemType();
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
        private void ApplyState(ShelfCompartment compartment, EItemType type, int itemCount)
        {
            if (itemCount <= 0 || type == EItemType.None)
            {
                Clear(compartment);
                compartment.SetCompartmentItemType(type);
                return;
            }

            if (compartment.GetItemType() != type)
            {
                // The incoming type change clears the compartment; return any item still owned by
                // a live pending placement to its player first, since the placement is about to
                // be rejected and a pooled item cannot be handed back.
                ReturnPendingItems(compartment);
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
                item.DisableItem();
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
                        items[i].DisableItem();
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
                    item.DisableItem();
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

        private bool CanResolveItemType(EItemType itemType, int itemCount)
        {
            if (itemType == EItemType.None)
            {
                return itemCount == 0;
            }

            try
            {
                var data = InventoryBase.GetItemData(itemType);
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
