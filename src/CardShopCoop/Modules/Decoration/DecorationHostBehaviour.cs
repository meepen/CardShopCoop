using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>Host authority for decoration inventory, purchases, equipment and placement.</summary>
    [ServerBehaviour]
    public sealed class DecorationHostBehaviour : CoopBehaviour
    {
        private static DecorationHostBehaviour _active;
        private readonly HashSet<int> _fullyJoined = new();
        private bool _shutdown;
        private bool _initialized;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }


            try
            {
                DecorationInterop.Reset();
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                Harmony.CreateClassProcessor(typeof(EquipPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BuyPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BuyItemPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration host initialization failed: " + exception);
                SceneManager.sceneLoaded -= OnSceneLoaded;


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                DecorationInterop.Reset();
                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }


            _fullyJoined.Add(connection.Id);
            if (TryBuildState(out var state))
            {
                PeerHandle.ForConnection(connection)?.Send(state);
            }
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _fullyJoined.Remove(connection.Id);
            }

        }

        [MessageHandler]
        private ActionOutcome HandleIntent(MessageContext context, DecorationIntentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
            {
                return ActionOutcome.Reject("decoration intent sender is not an admitted peer");
            }


            var accepted = false;
            try
            {
                // Identity publishes the decoration's lifecycle inside this scope. The intent
                // handler then sends the state delta with its prediction id, so the lifecycle
                // hooks suppress their own prediction-less inventory broadcast while this scope
                // is active. Identity assigns the id of a newly created piece; there is no
                // client-proposed id to adopt.
                _applyingIntent = true;
                using (DecorationIdentityAdapter.BeginHostIntent(0))
                {
                    try
                    {
                        accepted = ApplyIntent(message);
                    }
                    finally
                    {
                        _applyingIntent = false;
                    }
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration host action failed action=" + message.Action
                    + ": " + exception);
            }

            if (accepted)
            {
                var delta = BuildDelta(message);
                if (delta != null)
                {
                    BroadcastDelta(delta, message.Peer);
                }
                // The module drives the local screens with a temporarily selected category, so the
                // host's open deco screen can be left rendering another category's panels. Refresh
                // it from the authoritative values the apply just produced.

                DecorationInterop.RefreshUi();
                return ActionOutcome.Applied(new DecorationIntentResponse { Delta = delta });
            }

            CoopPlugin.Log.LogInfo("[decoration] intent rejected action=" + message.Action
                + " type=" + message.DecorationType + " prediction=" + message.ActionId + ".");
            // The requester's undo restores its captured snapshot, which may be stale if the host
            // or another guest changed the decorations during the round trip. The authoritative
            // snapshot travels in the rejection response and is applied after that undo.
            TryBuildState(out var state);
            return ActionOutcome.Reject("decoration action was refused",
                new DecorationIntentResponse { State = state });
        }

        private static bool ApplyIntent(DecorationIntentMessage message)
        {
            switch (message.Action)
            {
                case DecorationActions.Equip:
                    return message.Category >= 0 && message.Category <= 2 && message.Index >= 0
                        && DecorationInterop.IsUnlocked(message.Category, message.Index)
                        && DecorationInterop.TryEquip(message.Category, message.Index, message.LotB);
                case DecorationActions.BuyShopDecoration:
                    if (!DecorationInterop.TryGetCategoryPrice(message.Category, message.Index,
                        out var shopPrice))
                    {
                        return false;
                    }


                    if (DecorationInterop.IsUnlocked(message.Category, message.Index))
                    {
                        return false;
                    }


                    return TryApplyReservedPurchase(shopPrice,
                        "category=" + message.Category + " index=" + message.Index,
                        () => DecorationInterop.TryBuyCategory(message.Category, message.Index,
                            shopPrice),
                        () => DecorationInterop.IsUnlocked(message.Category, message.Index));
                case DecorationActions.BuyItemDecoration:
                    if (!DecorationInterop.TryGetItemPrice(message.DecorationType, out var itemPrice))
                    {
                        return false;
                    }

                    var inventoryBefore = DecorationInterop.InventoryCount(message.DecorationType);
                    return TryApplyReservedPurchase(itemPrice, "item=" + message.DecorationType,
                        () => DecorationInterop.TryBuyItem(message.DecorationType, itemPrice),
                        () => DecorationInterop.InventoryCount(message.DecorationType)
                            > inventoryBefore);
                case DecorationActions.Place:
                    return ApplyPlace(message);
                case DecorationActions.Remove:
                    return ApplyRemove(message);
                default:
                    return false;
            }
        }

        /// <summary>A placement targets an existing piece when the intent carries it. Without one
        /// the client is placing a new piece from its inventory, and identity assigns the id when
        /// the host creates the object.</summary>
        private static bool ApplyPlace(DecorationIntentMessage message)
        {
            var existing = message.Entity;
            var creates = existing == null;
            if (!DecorationInterop.ValidPose(message.Position, message.Rotation)
                || message.WallIndex < -1 || message.WallIndex > 1024
                || (creates && !DecorationInterop.HasInventory(message.DecorationType)))
            {
                return false;
            }

            if (!creates && !DecorationInterop.IsDecoration(existing))
            {
                CoopPlugin.Log.LogWarning("[decoration] place intent dropped: entity is no longer a"
                    + " live decoration.");
                return false;
            }

            var pose = new DecorationPose
            {
                DecorationType = message.DecorationType,
                Position = message.Position,
                Rotation = message.Rotation,
                Vertical = message.Vertical,
                WarehouseWallSnap = message.WarehouseWallSnap,
                Wall = message.WallIndex,
            };
            var taken = false;
            try
            {
                if (creates)
                {
                    DecorationInterop.AdjustInventory(message.DecorationType, -1);
                    taken = true;
                }

                // Identity owns the target binding and publishes the added/moved lifecycle from
                // inside the game's placement sequence.
                if (DecorationInterop.TryPlace(pose, existing, out _))
                {
                    taken = false;
                    return true;
                }
                return false;
            }
            finally
            {
                // TryPlace owns the placement itself, but the inventory take is ours:
                // give it back when placement is refused or throws (for example a
                // decoration prefab that cannot be instantiated), so a failed placement
                // cannot consume an item.
                if (taken)
                {
                    DecorationInterop.AdjustInventory(message.DecorationType, 1);
                }

            }
        }

        /// <summary>Removal runs the game's own box-up on the live entity the intent carries.
        /// A decoration is never addressed by an id here: without its object the message cannot
        /// describe anything this host can act on, so it is dropped.</summary>
        private static bool ApplyRemove(DecorationIntentMessage message)
        {
            var entity = message.Entity;
            if (entity == null || !DecorationInterop.IsDecoration(entity))
            {
                CoopPlugin.Log.LogWarning("[decoration] remove intent dropped: no live decoration"
                    + " entity.");
                return false;
            }

            return DecorationInterop.TryRemove(entity);
        }

        private static bool TryApplyReservedPurchase(float price, string context,
            Func<bool> applyVanilla, Func<bool> applied)
        {
            if (!EconomyAuthority.TryReserveHostSpend(price, out var spend))
            {
                return false;
            }

            var succeeded = false;
            try
            {
                succeeded = EconomyAuthority.RunWithVanillaSpend(spend, applyVanilla);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration purchase failed (" + context + "): " + exception);
            }

            // Judge the purchase by its effect, not only by the return path: the vanilla method
            // can complete the unlock/inventory change and then throw while refreshing its own UI.
            // Such a purchase must be accepted and stay charged, not refunded while the effect
            // remains. A return of false with no effect (missing screen, refused affordability)
            // gets the debit back below.
            if (succeeded || applied())
            {
                return true;
            }

            // RunWithVanillaSpend queues the host debit before the vanilla purchase runs. Consumed
            // tells us the debit was actually admitted; compensate exactly that amount and nothing
            // when the reservation itself failed.

            if (spend.Consumed)
            {
                CoopPlugin.Log.LogWarning("Refunded an unspent decoration purchase debit ("
                    + context + "): " + spend.Amount + ".");
                EconomyAuthority.CancelHostSpend(spend);
            }
            return false;
        }

        private void BroadcastDelta(DecorationDeltaMessage message, ICoopPeer except = null)
        {
            if (_shutdown || !RuntimeContext.InGame() || message == null)
            {
                return;
            }

            if (except != null)
            {
                Context.BroadcastExcept(except, message);
            }
            else
            {
                Context.Broadcast(message);
            }
        }

        private bool TryBuildState(out DecorationStateMessage message)
        {
            message = null;
            if (!RuntimeContext.InGame() || !DecorationInterop.IsSceneReady())
            {

                return false;
            }


            message = DecorationInterop.ToStateMessage(DecorationInterop.Snapshot());
            return message != null;
        }

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        /// <summary>Broadcasts only the inventory result of a host-originated decoration action.
        /// The lifecycle (added/moved/removed) is already published by the identity hooks.</summary>
        internal static void PublishInventoryDelta(byte action, EDecoObject type)
            => _active?.BroadcastInventory(action, type);

        private void BroadcastInventory(byte action, EDecoObject type)
        {
            if (_shutdown || !RuntimeContext.InGame()
                || type == EDecoObject.None)
            {
                return;
            }

            BroadcastDelta(new DecorationDeltaMessage
            {
                Action = action,
                DecorationType = type,
                InventoryCount = DecorationInterop.InventoryCount(type),
            });
            DecorationInterop.RefreshUi();
        }

        private static DecorationDeltaMessage BuildDelta(DecorationIntentMessage intent)
        {
            var delta = new DecorationDeltaMessage
            {
                Action = intent.Action,
                Category = intent.Category,
                Index = intent.Index,
                DecorationType = intent.DecorationType,
                LotB = intent.LotB,
            };
            if (intent.Action == DecorationActions.BuyItemDecoration
                || intent.Action == DecorationActions.Remove
                || intent.Action == DecorationActions.Place)
            {
                delta.InventoryCount = DecorationInterop.InventoryCount(intent.DecorationType);
            }
            return delta;
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            DecorationInterop.Reset();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


            _fullyJoined.Clear();
            DecorationInterop.Reset();
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(PlaceDecoUIScreen), "OnPressSwitchShopDeco")]
        private static class EquipPatch
        {
            [HarmonyPostfix]
            private static void Postfix(PlaceDecoUIScreen __instance, int shopDecoIndex,
                bool isShopLotB)
            {
                if (_active == null || _active._applyingIntent)
                {
                    return;
                }


                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.Equip,
                    Category = DecorationInterop.CategoryFor(__instance),
                    Index = shopDecoIndex,
                    LotB = isShopLotB,
                });
            }
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco")]
        private static class BuyPatch
        {
            private struct State
            {
                public int Category;
                public bool WasUnlocked;
            }

            /// <summary>Captures the category and ownership before the press. A press vanilla
            /// refuses (not enough money) returns without unlocking; publishing its index anyway
            /// made every guest believe the host owned a style it did not.</summary>
            [HarmonyPrefix]
            private static void Prefix(ShopBuyDecoUIScreen __instance, int shopDecoIndex,
                out State __state)
            {
                var category = DecorationInterop.CategoryFor(__instance);
                __state = new State
                {
                    Category = category,
                    WasUnlocked = DecorationInterop.IsUnlocked(category, shopDecoIndex),
                };
            }

            [HarmonyPostfix]
            private static void Postfix(int shopDecoIndex, State __state)
            {
                if (_active == null || _active._applyingIntent || __state.WasUnlocked
                    || !DecorationInterop.IsUnlocked(__state.Category, shopDecoIndex))
                {
                    return;
                }


                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.BuyShopDecoration,
                    Category = __state.Category,
                    Index = shopDecoIndex,
                });
            }
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDecoItem")]
        private static class BuyItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EDecoObject itemType)
            {
                if (_active == null || _active._applyingIntent)
                {
                    return;
                }


                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.BuyItemDecoration,
                    DecorationType = itemType,
                    InventoryCount = DecorationInterop.InventoryCount(itemType),
                });
            }
        }

    }
}
