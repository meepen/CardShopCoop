using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Modules.Actions;
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
            Harmony.CreateClassProcessor(typeof(AddItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(RemoveItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(TakeItemToHandPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(SetCompartmentItemTypePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PutItemOnShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(TakeItemFromShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(BoxAddToShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(BoxRemoveFromShelfScopePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(RemoveLabelPatch)).Patch();
        }

        [MessageHandler]
        private ActionOutcome HandleShelfAdd(CoopMessageContext context, ShelfAddAction message)
        {
            if (_shelfInteraction == null)
            {
                return ActionOutcome.Reject("shelf sync is not running");
            }

            if (message?.Shelf == null || !Context.InGame || !context.IsAuthenticated)
            {
                return _shelfInteraction.Refuse(message,
                    "the session is not ready for world actions");
            }

            return _shelfInteraction.HostAdd(message, message.Peer);
        }

        [MessageHandler]
        private ActionOutcome HandleShelfTake(CoopMessageContext context, ShelfTakeAction message)
        {
            if (_shelfInteraction == null)
            {
                return ActionOutcome.Reject("shelf sync is not running");
            }

            if (message?.Shelf == null || !Context.InGame || !context.IsAuthenticated)
            {
                return _shelfInteraction.Refuse(message,
                    "the session is not ready for world actions");
            }

            return _shelfInteraction.HostTake(message, message.Peer);
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

        /// <summary>Resolves the placed item box whose contents compartment is
        /// <paramref name="compartment"/>, or null when it is not a box's contents.</summary>
        private InteractablePackagingBox_Item FindItemBox(ShelfCompartment compartment)
            => _boxNetworkInteraction?.FindItemBox(compartment);

        private bool IsBoxHeld(InteractablePackagingBox box)
            => _boxNetworkInteraction != null && _boxNetworkInteraction.IsBeingHeld(box);

        /// <summary>A shelf's label (its compartment type, shown on the price tag even with no
        /// items) was removed. Removing a label clears the type on an empty compartment, which
        /// fires neither AddItem nor RemoveItem, so it needs its own publish or the other players
        /// keep showing the label. Only the removal (type -> None) is shared: an empty compartment
        /// briefly takes the incoming type before the first item lands, and that is not a label.</summary>
        private void PublishLabelChange(ShelfCompartment compartment, EItemType previousType,
            bool allowWarehouse = false)
        {
            if (_shelfInteraction == null || compartment == null || !Context.InGame
                || compartment.GetItemCount() > 0
                || compartment.GetItemType() != EItemType.None
                || previousType == EItemType.None)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[shelf] label removed on " + compartment.name + " ("
                + previousType + ").");
            _shelfInteraction.PublishRemove(compartment,
                _shelfInteraction.CaptureLabelChange(compartment, previousType, allowWarehouse));
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

                // Putting an item into an open floor box: the shelf protocol cannot address a
                // box's contents (no furniture id), so republish the box. A held box's own
                // item-move patch (RemoveItemFromShelf) owns its broadcast, so only a free box
                // broadcasts here.
                var instance = _instance;
                var box = instance?.FindItemBox(__instance);
                if (box != null && !instance.IsBoxHeld(box))
                {
                    instance.BroadcastItemBoxState(box);
                }
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
                if (__result == null)
                {
                    return;
                }

                // TakeItemToHand is the one removal whose item is only known from the result, so
                // attach the exact removed item here before the prediction is published.
                if (__state.Action != null)
                {
                    __state.Action.Item = __result;
                }

                _instance?.PublishShelfRemove(__instance, __state);

                // Taking an item out of an open floor box: the shelf protocol cannot address
                // a box's contents, so republish the box or clients keep counting the taken
                // item.
                var instance = _instance;
                var box = instance?.FindItemBox(__instance);
                if (box != null)
                {
                    instance.BroadcastItemBoxState(box);
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

    /// <summary>One shelf compartment's authoritative stock: the live placed shelf plus the
    /// compartment index, never a scene name, hierarchy path or key. Host -> other clients after
    /// the host applied a shelf mutation; the acting client gets the same values in its
    /// <see cref="ShelfStateResponse"/> instead.</summary>
    [NetworkMessage]
    public sealed class ShelfStateMessage : INetMessage
    {
        public InteractableObject Shelf;
        public int Compartment = -1;
        public EItemType ItemType = EItemType.None;
        public int ItemCount;
    }

    /// <summary>The host's post-action answer to one shelf action, sent to the acting client on
    /// accept and reject alike so its prediction merges onto real state instead of inverting a
    /// snapshot authority has already moved past.</summary>
    [NetworkMessage]
    public sealed class ShelfStateResponse : ActionResponseMessage
    {
        public InteractableObject Shelf;
        public int Compartment = -1;
        public EItemType ItemType = EItemType.None;
        public int ItemCount;
    }

    /// <summary>
    /// One predicted player shelf mutation. The client's own game path has already performed it -
    /// an add moved the real item onto the compartment, a take moved it into the hand or into an
    /// open box - so the action carries the exact local item for a faithful undo and delegates the
    /// authoritative merge to the client's <see cref="ShelfInteraction"/>.
    /// </summary>
    public abstract class ShelfActionMessage : ActionMessage
    {
        /// <summary>The shelf the mutation targets: the live placed object plus the compartment
        /// index in that shelf's compartment list.</summary>
        public InteractableObject Shelf;

        public int Compartment = -1;

        /// <summary>The exact item this prediction moved. Local only: the host never trusts a
        /// client-supplied item object, it creates or frees its own.</summary>
        [NonSerialized]
        internal Item Item;

        /// <summary>The client-side shelf protocol that owns this prediction. Local only.</summary>
        [NonSerialized]
        internal ShelfInteraction Origin;

        /// <summary>Client: reverses this prediction's game change only. The authoritative stock
        /// arrives in <see cref="OnMerge"/> behind it.</summary>
        public override void OnUndo(ActionResponseMessage response)
            => Origin?.Undo(this);

        /// <summary>Client: applies the host's stock and releases the item this action owned.</summary>
        public override void OnMerge(ActionResponseMessage response)
            => Origin?.Merge(this, response as ShelfStateResponse);
    }

    /// <summary>Client -> host: place one item on a shelf compartment.</summary>
    [NetworkMessage]
    public sealed class ShelfAddAction : ShelfActionMessage
    {
        /// <summary>The item type being placed. The host validates it against its own compartment
        /// and never trusts a client count.</summary>
        public EItemType ItemType = EItemType.None;
    }

    /// <summary>Client -> host: take the last item off a shelf compartment, or clear the label of
    /// an empty one. Which of the two it was is the client's prediction; the host applies whatever
    /// its own compartment supports.</summary>
    [NetworkMessage]
    public sealed class ShelfTakeAction : ShelfActionMessage
    {
        /// <summary>The type the client's prediction removed, or <see cref="EItemType.None"/> when
        /// it cleared a label. Only a hint for whether the host's compartment supports the same
        /// removal; it is never applied as state.</summary>
        public EItemType RemovedType = EItemType.None;

        /// <summary>Where the predicted removal moved the item, when vanilla moved it into the open
        /// box the player is holding. Local only: the undo takes the item back out of that box and
        /// the box channel republishes its contents so the host loses it again.</summary>
        [NonSerialized]
        internal ShelfCompartment Destination;

        /// <summary>The compartment type before a label removal cleared it, for the undo. Local
        /// only; <see cref="EItemType.None"/> for an item removal.</summary>
        [NonSerialized]
        internal EItemType PreviousType = EItemType.None;
    }

    /// <summary>
    /// Player shelf protocol. One predicted <see cref="ShelfActionMessage"/> per player shelf
    /// mutation: the client's game path performs the prediction and sends it, the host validates it
    /// and mutates its own compartment, and the answer carries the host's post-action (type, count)
    /// so every peer converges on the same stock.
    ///
    /// One instance per behaviour. The host mirrors every local shelf mutation it performs (its own
    /// player, customers and workers) and broadcasts the resulting stock. The client predicts its
    /// own player's mutations, applies everyone else's authoritative stock, and owns the exact item
    /// each unresolved prediction moved until the host's answer settles it.
    /// </summary>
    internal sealed class ShelfInteraction
    {
        /// <summary>Vanilla's hand cap for normal items. A full hand parks the item in front of the
        /// player instead of indexing past <c>m_HoldCardPackPosList</c>.</summary>
        private const int MaxHandItems = 8;

        private const int MaxCompartments = 256;

        private static readonly FieldInfo StoredItemsField =
            AccessTools.Field(typeof(ShelfCompartment), "m_StoredItemList");

        private static readonly FieldInfo CompartmentListField =
            AccessTools.Field(typeof(InteractableObject), "m_ItemCompartmentList");

        private static readonly FieldInfo PosListField =
            AccessTools.Field(typeof(ShelfCompartment), "m_PosList");

        private static readonly FieldInfo HoldItemListField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");

        private static readonly FieldInfo HoldItemSlotListField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_HoldCardPackPosList");

        private static readonly FieldInfo HoldingItemBoxField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingItemBox");

        private static readonly MethodInfo RemoveHoldItemMethod =
            AccessTools.Method(typeof(InteractionPlayerController), "RemoveHoldItem");

        private readonly ICoopContext _context;
        private readonly bool _host;

        /// <summary>Client only: the exact item each unresolved prediction owns, by action id. An
        /// authoritative state that reaches the client before that decision crossed the local
        /// action on the wire describes the older stock, so it must never pool, hand back or count
        /// an item a pending action owns. The action's merge releases the entry.</summary>
        private readonly Dictionary<Guid, Item> _pendingItems = new();

        private int _applyDepth;
        private int _playerMutationDepth;

        /// <summary>What one completed local shelf mutation needs to publish. The action is the
        /// client's prediction (null on the host, which publishes state instead).</summary>
        internal struct LocalMutation
        {
            internal bool Send;

            internal ShelfActionMessage Action;

            internal InteractableObject Shelf;

            internal int Compartment;
        }

        internal ShelfInteraction(ICoopContext context, bool host)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _host = host;
        }

        internal void Reset()
        {
            // Predictions are settled by the ledger, not here: the map only owns the items those
            // predictions still hold, and a session restart re-baselines the shelves anyway.
            _pendingItems.Clear();
            _playerMutationDepth = 0;
        }

        internal void EnterPlayerMutation() => _playerMutationDepth++;

        internal void ExitPlayerMutation()
        {
            if (_playerMutationDepth <= 0)
            {
                throw new InvalidOperationException(
                    "Player shelf-mutation scope ended without a matching start.");
            }

            _playerMutationDepth--;
        }

        /// <summary>Captures the local mutation a game method is about to perform. The client turns
        /// it into the prediction it sends once the game path finished; the host just remembers
        /// which compartment it has to publish.</summary>
        internal LocalMutation CaptureLocalMutation(ShelfCompartment compartment, bool isAdd,
            EItemType requestedType, Item item)
        {
            // The host mirrors every in-game player-shelf mutation it performs, not only its own
            // player's: NPC customers (and workers) take and return items outside the local player
            // scope, so gating on that scope silently dropped their shelf updates. The client
            // forwards only its own player's mutations.
            if (_applyDepth > 0 || !IsPlayerShelf(compartment) || !_context.InGame
                || (!_host && _playerMutationDepth == 0))
            {
                return default;
            }

            if (!TryGetShelf(compartment, out var shelf, out var compartmentIndex))
            {
                CoopPlugin.Log.LogWarning("[shelf] no live owning shelf for " + compartment.name
                    + "; " + (isAdd ? "placement" : "removal") + " not synced.");
                return default;
            }

            var action = default(ShelfActionMessage);
            if (!_host)
            {
                action = isAdd
                    ? new ShelfAddAction
                    {
                        Shelf = shelf,
                        Compartment = compartmentIndex,
                        ItemType = requestedType,
                        Item = item,
                        Origin = this,
                    }
                    : new ShelfTakeAction
                    {
                        Shelf = shelf,
                        Compartment = compartmentIndex,
                        Item = item,
                        Destination = HeldBoxCompartment(),
                        Origin = this,
                    };
            }

            return new LocalMutation
            {
                Send = true,
                Action = action,
                Shelf = shelf,
                Compartment = compartmentIndex,
            };
        }

        /// <summary>An explicit label removal on an empty shelf. Unlike an item move this has no
        /// count change, so its prediction is only the type going to None and its undo is the type
        /// coming back. Warehouse compartments share the label concept with player shelves, so the
        /// caller opts them in.</summary>
        internal LocalMutation CaptureLabelChange(ShelfCompartment compartment,
            EItemType previousType, bool allowWarehouse = false)
        {
            if (_applyDepth > 0
                || !(allowWarehouse ? IsSyncableShelf(compartment) : IsPlayerShelf(compartment))
                || !_context.InGame
                || _playerMutationDepth > 0)
            {
                // A type reset inside a player mutation scope (taking the last item clears the
                // label) belongs to that mutation's own action. Publishing it as a second label
                // removal would decrement the same take twice on the host.
                return default;
            }

            if (!TryGetShelf(compartment, out var shelf, out var compartmentIndex))
            {
                CoopPlugin.Log.LogWarning("[shelf] no live owning shelf for " + compartment.name
                    + "; label removal not synced.");
                return default;
            }

            return new LocalMutation
            {
                Send = true,
                Action = _host ? null : new ShelfTakeAction
                {
                    Shelf = shelf,
                    Compartment = compartmentIndex,
                    PreviousType = previousType,
                    Origin = this,
                },
                Shelf = shelf,
                Compartment = compartmentIndex,
            };
        }

        internal void PublishAdd(ShelfCompartment compartment, LocalMutation mutation)
            => Publish(compartment, mutation);

        internal void PublishRemove(ShelfCompartment compartment, LocalMutation mutation)
            => Publish(compartment, mutation);

        /// <summary>Publishes one completed local mutation. The host broadcasts the resulting stock
        /// to every peer; the client sends its prediction to the host and registers the exact item
        /// it owns until the answer settles it.</summary>
        private void Publish(ShelfCompartment compartment, LocalMutation mutation)
        {
            if (!mutation.Send || compartment == null)
            {
                return;
            }

            if (_host)
            {
                _context.Broadcast(new ShelfStateMessage
                {
                    Shelf = mutation.Shelf,
                    Compartment = mutation.Compartment,
                    ItemType = compartment.GetItemType(),
                    ItemCount = compartment.GetItemCount(),
                });
                return;
            }

            var action = mutation.Action;
            if (action == null)
            {
                return;
            }

            if (action is ShelfAddAction add && add.Item == null)
            {
                CoopPlugin.Log.LogWarning("[shelf] placement on " + compartment.name
                    + " has no item to predict; not sent.");
                return;
            }

            if (action is ShelfTakeAction take && take.Item == null
                && take.PreviousType == EItemType.None)
            {
                CoopPlugin.Log.LogWarning("[shelf] removal from " + compartment.name
                    + " moved no item; not sent.");
                return;
            }

            if (action is ShelfTakeAction removed)
            {
                // The exact removed item is only known by the time the game path returns (a
                // take-to-hand reports it), so fill the hint here, right before sending.
                removed.RemovedType = removed.Item == null
                    ? EItemType.None : removed.Item.GetItemType();
            }

            _context.Send(action);
            if (action.Item != null)
            {
                // The framework assigned the action id while sending; only now can the artifact be
                // tracked by the id the host will answer.
                _pendingItems[action.ActionId] = action.Item;
            }
        }

        // ---------------- host ----------------

        /// <summary>The handler outcome for a shelf action refused before host execution. The
        /// answer still carries the host's current stock so the client's merge lands on reality.</summary>
        internal ActionOutcome Refuse(ShelfActionMessage action, string reason)
        {
            CoopPlugin.Log.LogWarning("[shelf] refused " + (action == null
                ? "<null action>" : action.GetType().Name) + ": " + reason + ".");
            return ActionOutcome.Reject(reason, BuildResponse(action));
        }

        /// <summary>Applies one predicted placement to the host's compartment. The host never trusts
        /// a client count: it validates against its own stock, creates its own item, and answers
        /// with the resulting (type, count) on accept and reject alike.</summary>
        internal ActionOutcome HostAdd(ShelfAddAction message, ICoopPeer actor)
        {
            var compartment = message == null
                ? null : ResolveCompartment(message.Shelf, message.Compartment);
            if (compartment == null)
            {
                return Refuse(message, "the shelf compartment is not loaded");
            }

            var rejected = BuildResponse(message);
            if (message.ItemType == EItemType.None || !CanResolveItemType(message.ItemType))
            {
                return ActionOutcome.Reject("the placement names no known item type", rejected);
            }

            var count = compartment.GetItemCount();
            if (count > 0 && compartment.GetItemType() != message.ItemType)
            {
                return ActionOutcome.Reject("the compartment holds a different item type", rejected);
            }

            // Real slot capacity: the first placement that fits wins. A later one that no longer
            // fits is refused and the requesting client takes its item back.
            var capacity = Mathf.Max(compartment.GetMaxItemCount(),
                compartment.GetItemPosListCount());
            if (count > 0 && capacity > 0 && count >= capacity)
            {
                CoopPlugin.Log.LogInfo("[shelf] refused a placement on " + compartment.name
                    + ": full (" + count + "/" + capacity + ").");
                return ActionOutcome.Reject("the shelf compartment is full", rejected);
            }

            var placed = false;
            RunApplying(() => placed = AddOne(compartment, message.ItemType));
            if (!placed)
            {
                return ActionOutcome.Reject("the host could not create the item", rejected);
            }

            var applied = BuildState(message.Shelf, message.Compartment, compartment);
            BroadcastExcept(actor, applied);
            return ActionOutcome.Applied(applied);
        }

        /// <summary>Applies one predicted removal to the host's compartment. An empty compartment
        /// that still carries a label loses the label instead, exactly as the game's own removal
        /// does.</summary>
        internal ActionOutcome HostTake(ShelfTakeAction message, ICoopPeer actor)
        {
            var compartment = message == null
                ? null : ResolveCompartment(message.Shelf, message.Compartment);
            if (compartment == null)
            {
                return Refuse(message, "the shelf compartment is not loaded");
            }

            var rejected = BuildResponse(message);
            if (compartment.GetItemCount() > 0)
            {
                RunApplying(() => RemoveOne(compartment));
            }
            else if (compartment.GetItemType() != EItemType.None
                && message.RemovedType == EItemType.None)
            {
                // An empty compartment still carries its label; taking it clears the type. A
                // prediction that removed an item cannot be honoured here - the compartment holds
                // none - so it is refused and the client takes its item back instead of keeping a
                // phantom copy.
                RunApplying(() => compartment.SetCompartmentItemType(EItemType.None));
            }
            else
            {
                return ActionOutcome.Reject("the shelf compartment holds no item to take", rejected);
            }

            var applied = BuildState(message.Shelf, message.Compartment, compartment);
            BroadcastExcept(actor, applied);
            return ActionOutcome.Applied(applied);
        }

        private ShelfStateResponse BuildResponse(ShelfActionMessage action)
            => action == null ? null : BuildResponse(action.Shelf, action.Compartment);

        private ShelfStateResponse BuildResponse(InteractableObject shelf, int compartmentIndex)
        {
            var compartment = ResolveCompartment(shelf, compartmentIndex);
            return compartment == null ? null : BuildState(shelf, compartmentIndex, compartment);
        }

        private static ShelfStateResponse BuildState(InteractableObject shelf, int compartmentIndex,
            ShelfCompartment compartment)
            => new()
            {
                Shelf = shelf,
                Compartment = compartmentIndex,
                ItemType = compartment.GetItemType(),
                ItemCount = compartment.GetItemCount(),
            };

        /// <summary>Sends the host's post-action stock to every other peer. The acting client gets
        /// the same values in its response, so it is excluded here.</summary>
        private void BroadcastExcept(ICoopPeer actor, ShelfStateResponse state)
        {
            if (state == null)
            {
                return;
            }

            var message = new ShelfStateMessage
            {
                Shelf = state.Shelf,
                Compartment = state.Compartment,
                ItemType = state.ItemType,
                ItemCount = state.ItemCount,
            };
            if (actor == null)
            {
                CoopPlugin.Log.LogWarning("[shelf] no sender for the action; broadcasting the stock "
                    + "to every peer.");
                _context.Broadcast(message);
                return;
            }

            _context.BroadcastExcept(actor, message);
        }

        // ---------------- client ----------------

        /// <summary>Client: applies the host's post-action stock for one compartment.</summary>
        internal void ApplyRemoteState(ShelfStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            ApplyAuthoritativeState(message.Shelf, message.Compartment, message.ItemType,
                message.ItemCount);
        }

        /// <summary>Client: reverses this action's prediction only. Called once, before the merge,
        /// and only when the host refused the action.</summary>
        internal void Undo(ShelfActionMessage action)
        {
            if (action == null)
            {
                return;
            }

            var compartment = action.Shelf == null
                ? null : ResolveCompartment(action.Shelf, action.Compartment);
            switch (action)
            {
                case ShelfAddAction add:
                    ReturnCapturedItem(compartment, add.Item);
                    break;

                case ShelfTakeAction take:
                    RestoreCapturedItem(compartment, take.Item, take.Destination,
                        take.PreviousType);
                    break;
            }
        }

        /// <summary>Client: settles this action on the host's stock, then releases the item it owned.
        /// The entry is released before the stock is applied because an accepted answer already
        /// includes the action's effect and a refused one was just reversed by the undo.</summary>
        internal void Merge(ShelfActionMessage action, ShelfStateResponse response)
        {
            if (action == null)
            {
                return;
            }

            _pendingItems.Remove(action.ActionId);
            action.Item = null;
            if (action is ShelfTakeAction take)
            {
                take.Destination = null;
                take.PreviousType = EItemType.None;
            }

            action.Origin = null;

            if (response == null)
            {
                CoopPlugin.Log.LogWarning("[shelf] action " + action.ActionId
                    + " settled without authoritative stock; the local shelf keeps its prediction.");
                return;
            }

            ApplyAuthoritativeState(response.Shelf, response.Compartment, response.ItemType,
                response.ItemCount);
        }

        /// <summary>Client: releases predictions whose item was destroyed by a world (re)load. The
        /// destroyed item can never be handed back or re-added, and the owning action still settles
        /// its shelf on the merge. Returns how many entries were released.</summary>
        internal int FlushStalePredictions()
        {
            if (_pendingItems.Count == 0)
            {
                return 0;
            }

            List<Guid> stale = null;
            foreach (var pair in _pendingItems)
            {
                // A destroyed Unity object compares equal to null; a pooled (disabled) item does
                // not, so a prediction that still owns a pooled item keeps it.
                if (pair.Value != null)
                {
                    continue;
                }

                stale ??= new List<Guid>();
                stale.Add(pair.Key);
            }

            if (stale == null)
            {
                return 0;
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _pendingItems.Remove(stale[i]);
            }

            CoopPlugin.Log.LogWarning("[shelf] released " + stale.Count
                + " prediction(s) whose item no longer exists.");
            return stale.Count;
        }

        private void ApplyAuthoritativeState(InteractableObject shelf, int compartmentIndex,
            EItemType itemType, int itemCount)
        {
            if (shelf == null)
            {
                CoopPlugin.Log.LogWarning("[shelf] dropped shelf state: the shelf did not resolve to "
                    + "a live object.");
                return;
            }

            var compartment = ResolveCompartment(shelf, compartmentIndex);
            if (compartment == null)
            {
                CoopPlugin.Log.LogWarning("[shelf] dropped shelf state: " + shelf.name
                    + " has no compartment " + compartmentIndex + ".");
                return;
            }

            RunApplying(() => ApplyState(compartment, itemType, itemCount));
        }

        /// <summary>Undoes a predicted placement: the host refused it, so the exact item leaves the
        /// compartment again and goes back to the local player.</summary>
        private void ReturnCapturedItem(ShelfCompartment compartment, Item item)
        {
            if (item == null)
            {
                return;
            }

            RunApplying(() =>
            {
                if (compartment != null
                    && StoredItemsField?.GetValue(compartment) is List<Item> stored
                    && stored.Contains(item))
                {
                    compartment.RemoveItem(item);
                    PositionStored(compartment);
                }
            });

            HandItem(item, "the host refused the placement");
            CoopPlugin.Log.LogInfo("[shelf] rolled a refused placement back on "
                + (compartment == null ? "<unloaded shelf>" : compartment.name) + ".");
        }

        /// <summary>Undoes a predicted removal: the host refused it, so the exact item leaves
        /// wherever the prediction put it (an open box or the hand) and returns to the compartment.
        /// A refused label removal only puts the compartment's type back.</summary>
        private void RestoreCapturedItem(ShelfCompartment compartment, Item item,
            ShelfCompartment destination, EItemType previousType)
        {
            if (item == null)
            {
                if (compartment == null || previousType == EItemType.None)
                {
                    return;
                }

                RunApplying(() =>
                {
                    compartment.SetCompartmentItemType(previousType);
                    compartment.CalculatePositionList();
                });
                CoopPlugin.Log.LogInfo("[shelf] restored the label on " + compartment.name + ".");
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            RunApplying(() =>
            {
                ReturnFromDestination(destination, item);
                if (controller != null
                    && HoldItemListField?.GetValue(controller) is List<Item> held
                    && held.Contains(item))
                {
                    RemoveHoldItemMethod?.Invoke(controller, new object[] { item });
                }

                if (compartment == null)
                {
                    return;
                }

                item.gameObject.SetActive(true);
                item.enabled = true;
                if (compartment.GetItemCount() <= 0
                    && compartment.GetItemType() != item.GetItemType())
                {
                    compartment.SetCompartmentItemType(item.GetItemType());
                    compartment.CalculatePositionList();
                }

                // The player-shelf take path removes the last item, so appending restores order.
                if (StoredItemsField?.GetValue(compartment) is List<Item> stored
                    && !stored.Contains(item))
                {
                    compartment.AddItem(item, addToFront: false);
                }

                PositionStored(compartment);
            });

            if (compartment == null)
            {
                HandItem(item, "the shelf unloaded before the host refused the removal");
            }

            if (destination != null)
            {
                // The box half of the transfer already reached the host, so its reversal has to be
                // published too or the host's box keeps an item the shelf kept as well.
                RepublishBoxState(destination);
            }

            CoopPlugin.Log.LogInfo("[shelf] rolled a refused removal back on "
                + (compartment == null ? "<unloaded shelf>" : compartment.name) + ".");
        }

        /// <summary>Takes the item back out of the compartment the predicted removal moved it into,
        /// when that destination is still loaded.</summary>
        private static void ReturnFromDestination(ShelfCompartment destination, Item item)
        {
            if (destination == null
                || StoredItemsField?.GetValue(destination) is not List<Item> stored
                || !stored.Contains(item))
            {
                return;
            }

            destination.RemoveItem(item);
            PositionStored(destination);
        }

        private static void RepublishBoxState(ShelfCompartment destination)
            => WorldClientBehaviour.RepublishItemBoxState(destination == null
                ? null : destination.GetComponentInParent<InteractablePackagingBox_Item>(true));

        // ---------------- authoritative stock ----------------

        /// <summary>Merges an authoritative compartment stock onto the live shelf by applying only
        /// the difference, so rapid deltas add/remove single items instead of rebuilding the whole
        /// compartment. Items owned by unresolved predictions are taken out before a clear: a pooled
        /// item can never be handed back, and the owning action settles right behind this call.</summary>
        private void ApplyState(ShelfCompartment compartment, EItemType type, int itemCount)
        {
            if (itemCount <= 0 || type == EItemType.None)
            {
                ReleasePendingItems(compartment, EItemType.None, retainMatching: false);
                Clear(compartment);
                compartment.SetCompartmentItemType(type);
                return;
            }

            if (compartment.GetItemType() != type)
            {
                var retained = ReleasePendingItems(compartment, type, retainMatching: true);
                Clear(compartment);
                compartment.SetCompartmentItemType(type);
                compartment.CalculatePositionList();
                if (StoredItemsField?.GetValue(compartment) is List<Item> stored)
                {
                    for (var i = 0; i < retained.Count; i++)
                    {
                        if (!stored.Contains(retained[i]))
                        {
                            compartment.AddItem(retained[i], addToFront: false);
                        }
                    }
                }

                PositionStored(compartment);
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
                    if (!AddOne(compartment, type))
                    {
                        break;
                    }
                }
            }
            else
            {
                for (var i = current; i > itemCount; i--)
                {
                    if (!RemoveOneAuthoritative(compartment))
                    {
                        break;
                    }
                }
            }

            CoopPlugin.Log.LogInfo("[shelf] merged " + compartment.name + " type=" + type
                + " count " + current + "->" + itemCount + ".");
        }

        /// <summary>Takes every item this compartment holds that is owned by an unresolved local
        /// prediction out of the authored state before the compartment is cleared. Items of the
        /// incoming type are returned for re-adding; mismatches are handed back to the player and
        /// released, because the authoritative stock cannot contain them.</summary>
        private List<Item> ReleasePendingItems(ShelfCompartment compartment, EItemType incomingType,
            bool retainMatching)
        {
            var retained = new List<Item>();
            if (_host || _pendingItems.Count == 0
                || StoredItemsField?.GetValue(compartment) is not List<Item> stored)
            {
                return retained;
            }

            List<Guid> released = null;
            for (var i = stored.Count - 1; i >= 0; i--)
            {
                var item = stored[i];
                if (item == null)
                {
                    continue;
                }

                var owner = FindPendingOwner(item);
                if (owner == Guid.Empty)
                {
                    continue;
                }

                compartment.RemoveItem(item);
                if (retainMatching && item.GetItemType() == incomingType)
                {
                    retained.Add(item);
                    continue;
                }

                released ??= new List<Guid>();
                released.Add(owner);
                HandItem(item, "the authoritative shelf state no longer contains this item");
            }

            if (released != null)
            {
                for (var i = 0; i < released.Count; i++)
                {
                    _pendingItems.Remove(released[i]);
                }
            }

            if (released != null || retained.Count > 0)
            {
                PositionStored(compartment);
            }

            return retained;
        }

        /// <summary>Removes one authoritative item from the end of the compartment, skipping any
        /// item a pending prediction owns. Returns false when only pending items remain.</summary>
        private bool RemoveOneAuthoritative(ShelfCompartment compartment)
        {
            if (StoredItemsField?.GetValue(compartment) is not List<Item> items)
            {
                return false;
            }

            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item == null || FindPendingOwner(item) != Guid.Empty)
                {
                    continue;
                }

                compartment.RemoveItem(item);
                item.DisableItem();
                return true;
            }

            return false;
        }

        private Guid FindPendingOwner(Item item)
        {
            foreach (var pair in _pendingItems)
            {
                if (ReferenceEquals(pair.Value, item))
                {
                    return pair.Key;
                }
            }

            return Guid.Empty;
        }

        // ---------------- game paths ----------------

        /// <summary>Runs one game-path change the protocol owns, so the module's own AddItem /
        /// RemoveItem / SetCompartmentItemType patches do not publish it as a new local action.</summary>
        private void RunApplying(Action apply)
        {
            _applyDepth++;
            try
            {
                apply();
            }
            finally
            {
                _applyDepth--;
            }
        }

        private static bool AddOne(ShelfCompartment compartment, EItemType itemType)
        {
            if (compartment.GetItemCount() <= 0 && compartment.GetItemType() != itemType)
            {
                compartment.SetCompartmentItemType(itemType);
                compartment.CalculatePositionList();
            }

            var item = Runtime.ItemVisuals.Spawn(itemType, compartment.m_StoredItemListGrp);
            if (item == null)
            {
                return false;
            }

            compartment.AddItem(item, addToFront: false);
            PositionStored(compartment);

            // 1.03 caches half-visibility and only re-applies it when the shelf's near/far state
            // changes. A remote add into a far/facing shelf therefore leaves the new mesh fully
            // visible until the local player moves.
            var shelf = compartment.GetShelf();
            if (shelf != null)
            {
                compartment.ApplyHalfVisibility(shelf.m_IsHalfVisible);
            }

            return true;
        }

        private static void RemoveOne(ShelfCompartment compartment)
        {
            if (StoredItemsField?.GetValue(compartment) is not List<Item> items || items.Count == 0)
            {
                return;
            }

            var item = items[items.Count - 1];
            compartment.RemoveItem(item);
            item?.DisableItem();
        }

        /// <summary>Puts an item into the local player's hand without ever indexing past the
        /// vanilla hand transform list or the vanilla hand cap. A full hand parks the item in front
        /// of the player instead of throwing or losing it.</summary>
        private static bool HandItem(Item item, string reason)
        {
            if (item == null)
            {
                return false;
            }

            item.gameObject.SetActive(true);
            item.enabled = true;

            var controller = SceneRef<InteractionPlayerController>.Get();
            var held = controller == null
                ? null : HoldItemListField?.GetValue(controller) as List<Item>;
            if (held == null)
            {
                ParkItem(item, reason + "; the local player controller is not loaded");
                return false;
            }

            if (held.Contains(item))
            {
                return true;
            }

            var slots = HoldItemSlotListField?.GetValue(controller) as List<Transform>;
            var capacity = slots == null || slots.Count == 0
                ? 0 : Mathf.Min(slots.Count, MaxHandItems);
            if (held.Count >= capacity)
            {
                ParkItem(item, reason + "; the hand is full (" + held.Count + " items)");
                return false;
            }

            controller.AddHoldItemToFront(item);
            return true;
        }

        /// <summary>Drops an item in front of the local player as a loose, pickable world object
        /// when the hand cannot hold it. Nothing is pooled or destroyed.</summary>
        private static void ParkItem(Item item, string reason)
        {
            if (item == null)
            {
                return;
            }

            item.gameObject.SetActive(true);
            item.enabled = true;
            item.transform.SetParent(null, true);

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                item.transform.position = controller.transform.position
                    + controller.transform.forward * 0.5f + Vector3.up * 1.1f;
            }

            item.GetOrCreateRigidbody().isKinematic = false;
            CoopPlugin.Log.LogWarning("[shelf] parked an item in front of the player: " + reason
                + ".");
        }

        /// <summary>The compartment of the item box the local player is holding while a shelf
        /// removal runs. Vanilla's <c>InteractablePackagingBox_Item.RemoveItemFromShelf</c> moves
        /// the shelf's last item into that open box in the same call, so it is the destination a
        /// refused removal must take the item back out of.</summary>
        private static ShelfCompartment HeldBoxCompartment()
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null
                || HoldingItemBoxField?.GetValue(controller) is not InteractablePackagingBox_Item box
                || box.m_ItemCompartment == null)
            {
                return null;
            }

            return BoxNetworkInteraction.IsBoxOpen(box) ? box.m_ItemCompartment : null;
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
                    items[i]?.DisableItem();
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

        // ---------------- resolution ----------------

        private static bool IsSyncableShelf(ShelfCompartment compartment)
        {
            // Include inactive parents: a closed box deactivates its item compartment, and
            // without this the box would be mistaken for a player shelf.
            return compartment != null
                && compartment.GetComponentInParent<InteractablePackagingBox>(true) == null;
        }

        private static bool IsPlayerShelf(ShelfCompartment compartment)
            => IsSyncableShelf(compartment) && compartment.GetWarehouseShelf() == null;

        /// <summary>Resolves the live shelf that owns a compartment and the compartment's index in
        /// that shelf's compartment list. Fails for a compartment that is not owned by a placed
        /// shelf (for example one inside a packaging box).</summary>
        private static bool TryGetShelf(ShelfCompartment compartment, out InteractableObject shelf,
            out int compartmentIndex)
        {
            shelf = null;
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
                        shelf = owner;
                        compartmentIndex = index;
                        return true;
                    }
                }

                node = owner.transform.parent;
            }

            return false;
        }

        /// <summary>Picks the addressed compartment out of an already-live shelf object.</summary>
        private static ShelfCompartment ResolveCompartment(InteractableObject shelf,
            int compartmentIndex)
        {
            if (shelf == null || compartmentIndex < 0 || compartmentIndex >= MaxCompartments
                || CompartmentListField?.GetValue(shelf) is not List<ShelfCompartment> compartments
                || compartmentIndex >= compartments.Count)
            {
                return null;
            }

            return compartments[compartmentIndex];
        }

        private static bool CanResolveItemType(EItemType itemType)
        {
            if (itemType == EItemType.None)
            {
                return false;
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
