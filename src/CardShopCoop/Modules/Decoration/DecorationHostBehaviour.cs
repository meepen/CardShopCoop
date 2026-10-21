using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>Host authority for decoration inventory, purchases, equipment and placement.</summary>
    [ServerBehaviour]
    public sealed class DecorationHostBehaviour : CoopBehaviour
    {
        private static DecorationHostBehaviour _active;
        /// <summary>The placement attempt currently inside the game's PlaceMovedObject frame. The
        /// commit callback also runs for box-up and the World hold cancel, so only a call made
        /// inside this frame is a placement this host must publish.</summary>
        private static InteractableObject _placingInstance;
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }


            _context = RuntimeContext;
            var registered = false;
            try
            {
                DecorationInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("dev.meepen.cardshopcoop.decoration.host");
                _harmony.CreateClassProcessor(typeof(EquipPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyItemPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PlaceAttemptPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PlacedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(RemovePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(LockedRoomHiddenPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                DecorationInterop.Reset();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }


            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _fullyJoined.Remove(connection.Id);
            }

        }

        [MessageHandler(typeof(DecorationIntentMessage))]
        private void HandleIntent(MessageContext context, DecorationIntentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
            {
                return;
            }


            var accepted = false;
            InteractableObject placed = null;
            try
            {
                _applyingIntent = true;
                try
                {
                    accepted = ApplyIntent(message, out placed);
                }
                finally
                {
                    _applyingIntent = false;
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration host action failed action=" + message.Action
                    + ": " + exception);
            }

            if (accepted)
            {
                // The place path hands back the exact object it mutated, so the delta is keyed by
                // that object's id instead of being searched for by type and position.
                var delta = BuildDelta(message, placed);
                if (delta != null)
                {
                    BroadcastDelta(delta);
                }
                // The module drives the local screens with a temporarily selected category, so the
                // host's open deco screen can be left rendering another category's panels. Refresh
                // it from the authoritative values the apply just produced.

                DecorationInterop.RefreshUi();
            }
            else if (message.PredictionId != Guid.Empty)
            {
                CoopPlugin.Log.LogInfo("[decoration] intent rejected action=" + message.Action
                    + " type=" + message.DecorationType + " prediction=" + message.PredictionId + ".");
                // The rollback's undo restores the requester's captured snapshot, which may be
                // stale if the host or another guest changed the decorations during the round
                // trip. DecorationStateMessage cannot carry the prediction id, so the
                // authoritative snapshot follows the rollback: the undo runs first and the state
                // then overrides it on the same reliable lane.
                PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                SendState(context.Connection.Id);
            }
        }

        private bool ApplyIntent(DecorationIntentMessage message, out InteractableObject placed)
        {
            placed = null;
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
                    if (!DecorationInterop.ValidPose(message.Position, message.Rotation)
                        || message.WallIndex < -1 || message.WallIndex > 1024
                        || (message.ObjectId <= 0 && !DecorationInterop.HasInventory(message.DecorationType)))
                    {
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
                    var consumesInventory = message.ObjectId <= 0;
                    var taken = false;
                    try
                    {
                        if (consumesInventory)
                        {
                            DecorationInterop.AdjustInventory(message.DecorationType, -1);
                            taken = true;
                        }
                        if (DecorationInterop.TryPlace(pose, message.ObjectId, out placed))
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
                case DecorationActions.Remove:
                    return message.ObjectId > 0 && DecorationInterop.TryRemove(message.ObjectId);
                default:
                    return false;
            }
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

        private void BroadcastDelta(DecorationDeltaMessage message)
        {
            if (_shutdown || _context == null || !_context.InGame() || message == null)
            {
                return;
            }


            _context.Broadcast(message);
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !TryBuildState(out var state))
            {
                return;
            }


            _context.Send(peer, state);
        }

        private bool TryBuildState(out DecorationStateMessage message)
        {
            message = null;
            if (_context == null || !_context.InGame() || !DecorationInterop.IsSceneReady())
            {

                return false;
            }


            var state = DecorationInterop.Snapshot();
            message = new DecorationStateMessage
            {
                Wall = state.Wall,
                WallB = state.WallB,
                Floor = state.Floor,
                FloorB = state.FloorB,
                Ceiling = state.Ceiling,
                CeilingB = state.CeilingB,
                WallUnlocks = state.WallUnlocks,
                FloorUnlocks = state.FloorUnlocks,
                CeilingUnlocks = state.CeilingUnlocks,
                Inventory = state.Inventory,
                Placed = state.Placed,
            };
            return true;
        }

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        private static DecorationDeltaMessage BuildDelta(DecorationIntentMessage intent,
            InteractableObject placed)
        {
            var delta = new DecorationDeltaMessage
            {
                PredictionId = intent.PredictionId,
                Action = intent.Action,
                Category = intent.Category,
                Index = intent.Index,
                DecorationType = intent.DecorationType,
                LotB = intent.LotB,
                ObjectId = intent.ObjectId,
            };
            if (intent.Action == DecorationActions.BuyItemDecoration)
            {
                delta.InventoryCount = DecorationInterop.InventoryCount(intent.DecorationType);
            }
            else if (intent.Action == DecorationActions.Remove)
            {
                delta.InventoryCount = DecorationInterop.InventoryCount(intent.DecorationType);
            }
            else if (intent.Action == DecorationActions.Place)
            {
                // The applied object is the authoritative piece itself; read its real pose and its
                // id directly instead of scanning the snapshot for a type/position match.
                if (placed == null)
                {

                    return null;
                }


                var pose = DecorationInterop.ReadPose(placed);
                pose.Id = DecorationInterop.EnsureHostId(placed);
                delta.Pose = pose;
                delta.ObjectId = pose.Id;
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
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


            _fullyJoined.Clear();
            DecorationInterop.Reset();
            _context = null;
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
                    PredictionId = Guid.Empty,
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
                    PredictionId = Guid.Empty,
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
                    PredictionId = Guid.Empty,
                });
            }
        }

        /// <summary>Marks the game's placement attempt for a decoration. The marker exists only
        /// while the synchronous PlaceMovedObject frame runs; an attempt vanilla refuses (invalid
        /// aim) never commits and therefore publishes nothing.</summary>
        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class PlaceAttemptPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance)
            {
                if (_active == null || _active._applyingIntent || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None
                    || !__instance.GetIsMovingObject())
                {
                    return;
                }


                _placingInstance = __instance;
            }

            [HarmonyFinalizer]
            private static void Finalizer() => _placingInstance = null;
        }

        /// <summary>The game's placement commit callback. Box-up and the World hold cancel share
        /// it, so only a call made inside this host's PlaceMovedObject frame is published. A moved
        /// piece already owns a host id; the delta carries that exact object.</summary>
        [HarmonyPatch(typeof(InteractableObject), "OnPlacedMovedObject")]
        private static class PlacedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
            {
                var host = _active;
                if (host == null || host._applyingIntent || __instance == null
                    || !ReferenceEquals(_placingInstance, __instance)
                    || __instance.m_DecoObjectType == EDecoObject.None)
                {
                    return;
                }


                var delta = BuildDelta(new DecorationIntentMessage
                {
                    Action = DecorationActions.Place,
                    DecorationType = __instance.m_DecoObjectType,
                    ObjectId = DecorationInterop.HostIdFor(__instance),
                }, __instance);
                if (delta != null)
                {
                    CoopPlugin.Log.LogInfo("[decoration] host placed id=" + delta.ObjectId
                        + " type=" + delta.DecorationType + ".");
                    host.BroadcastDelta(delta);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class RemovePatch
        {
            private struct State
            {
                public long Id;
                public EDecoObject Type;
            }

            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out State __state)
                => __state = new State
                {
                    Id = DecorationInterop.HostIdFor(__instance),
                    Type = __instance.m_DecoObjectType,
                };

            [HarmonyPostfix]
            private static void Postfix(State __state)
            {
                if (_active == null || _active._applyingIntent || __state.Type == EDecoObject.None)
                {
                    return;
                }
                // A zero id means this piece was never published: a decoration taken from the
                // inventory and boxed again before it was placed. The object half of the delta is
                // a no-op for guests, but the count must still go out - a join or rejection
                // baseline taken while the host was holding the piece carried the reduced count,
                // so the cancel's +1 has to reach them.

                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.Remove,
                    ObjectId = __state.Id,
                    DecorationType = __state.Type,
                    InventoryCount = DecorationInterop.InventoryCount(__state.Type),
                    PredictionId = Guid.Empty,
                });
            }
        }

        /// <summary>Room and warehouse expansion hide the blocker and return every vertical
        /// decoration on it to the inventory through OnLockedRoomHidden, which bypasses
        /// BoxUpObject. Publish that removal exactly like a box-up so every guest loses the piece
        /// and gains the item instead of keeping a copy the host no longer has.</summary>
        [HarmonyPatch(typeof(InteractableObject), "OnLockedRoomHidden")]
        private static class LockedRoomHiddenPatch
        {
            private struct State
            {
                public long Id;
                public EDecoObject Type;
            }

            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out State __state)
                => __state = new State
                {
                    Id = DecorationInterop.HostIdFor(__instance),
                    Type = __instance == null ? EDecoObject.None : __instance.m_DecoObjectType,
                };

            [HarmonyPostfix]
            private static void Postfix(State __state)
            {
                if (_active == null || _active._applyingIntent
                    || __state.Type == EDecoObject.None)
                {
                    return;
                }


                var count = DecorationInterop.InventoryCount(__state.Type);
                CoopPlugin.Log.LogInfo("[decoration] host locked-room removed id=" + __state.Id
                    + " type=" + __state.Type + " count=" + count + ".");
                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.Remove,
                    ObjectId = __state.Id,
                    DecorationType = __state.Type,
                    InventoryCount = count,
                    PredictionId = Guid.Empty,
                });
            }
        }

    }
}
