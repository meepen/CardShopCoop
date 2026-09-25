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
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                DecorationInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.decoration.host");
                _harmony.CreateClassProcessor(typeof(EquipPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyItemPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PlacePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(RemovePatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                DecorationInterop.Reset();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
                return;

            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
                _fullyJoined.Remove(connection.Id);
        }

        [MessageHandler(typeof(DecorationIntentMessage))]
        private void HandleIntent(MessageContext context, DecorationIntentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
                return;

            var accepted = false;
            try
            {
                _applyingIntent = true;
                try
                {
                    accepted = ApplyIntent(message);
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
                var delta = BuildDelta(message);
                if (delta != null)
                    BroadcastDelta(delta);
            }
            else if (message.PredictionId != Guid.Empty)
            {
                CoopPlugin.Log.LogInfo("[decoration] intent rejected action=" + message.Action
                    + " type=" + message.DecorationType + " prediction=" + message.PredictionId + ".");
                PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
            }
        }

        private bool ApplyIntent(DecorationIntentMessage message)
        {
            switch (message.Action)
            {
                case DecorationActions.Equip:
                    return message.Category >= 0 && message.Category <= 2 && message.Index >= 0
                        && DecorationInterop.IsUnlocked(message.Category, message.Index)
                        && DecorationInterop.TryEquip(message.Category, message.Index, message.LotB);
                case DecorationActions.BuyShopDecoration:
                    return DecorationInterop.TryGetCategoryPrice(message.Category, message.Index,
                        out var shopPrice)
                        && !DecorationInterop.IsUnlocked(message.Category, message.Index)
                        && TryApplyReservedPurchase(shopPrice,
                            "category=" + message.Category + " index=" + message.Index,
                            () => DecorationInterop.TryBuyCategory(message.Category, message.Index,
                                shopPrice));
                case DecorationActions.BuyItemDecoration:
                    return DecorationInterop.TryGetItemPrice(message.DecorationType, out var itemPrice)
                        && TryApplyReservedPurchase(itemPrice, "item=" + message.DecorationType,
                            () => DecorationInterop.TryBuyItem(message.DecorationType, itemPrice));
                case DecorationActions.Place:
                    if (!DecorationInterop.ValidPose(message.Position, message.Rotation)
                        || message.WallIndex < -1 || message.WallIndex > 1024
                        || (message.ObjectId <= 0 && !DecorationInterop.HasInventory(message.DecorationType)))
                        return false;
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
                    if (consumesInventory)
                        DecorationInterop.AdjustInventory(message.DecorationType, -1);
                    if (!DecorationInterop.TryPlace(pose, message.ObjectId, out _))
                    {
                        if (consumesInventory)
                            DecorationInterop.AdjustInventory(message.DecorationType, 1);
                        return false;
                    }
                    return true;
                case DecorationActions.Remove:
                    return message.ObjectId > 0 && DecorationInterop.TryRemove(message.ObjectId);
                default:
                    return false;
            }
        }

        private static bool TryApplyReservedPurchase(float price, string context, Func<bool> applyVanilla)
        {
            if (!EconomyAuthority.TryReserveHostSpend(price, out var spend))
                return false;
            try
            {
                return EconomyAuthority.RunWithVanillaSpend(spend, applyVanilla);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration purchase failed (" + context + "): " + exception);
                spend.Dispose();
                return false;
            }
        }

        private void BroadcastDelta(DecorationDeltaMessage message)
        {
            if (_shutdown || _context == null || !_context.InGame() || message == null)
                return;
            _context.Broadcast(message);
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !TryBuildState(out var state))
                return;

            _context.Send(peer, state);
        }

        private bool TryBuildState(out DecorationStateMessage message)
        {
            message = null;
            if (_context == null || !_context.InGame() || !DecorationInterop.IsSceneReady())
                return false;

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

        private static DecorationDeltaMessage BuildDelta(DecorationIntentMessage intent)
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
                var state = DecorationInterop.Snapshot();
                var best = default(DecorationPose);
                var bestDistance = float.MaxValue;
                for (var i = 0; i < state.Placed.Count; i++)
                {
                    var pose = state.Placed[i];
                    if (pose.DecorationType != intent.DecorationType)
                        continue;
                    if (intent.ObjectId > 0 && pose.Id == intent.ObjectId)
                    {
                        best = pose;
                        bestDistance = 0f;
                        break;
                    }

                    var distance = (pose.Position - intent.Position).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        best = pose;
                        bestDistance = distance;
                    }
                }

                if (best == null || bestDistance == float.MaxValue)
                    return null;
                delta.Pose = best;
                delta.ObjectId = best.Id;
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
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            _fullyJoined.Clear();
            DecorationInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(PlaceDecoUIScreen), "OnPressSwitchShopDeco")]
        private static class EquipPatch
        {
            [HarmonyPostfix]
            private static void Postfix(int shopDecoIndex, bool isShopLotB)
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.Equip,
                    Category = DecorationInterop.CategoryFor((PlaceDecoUIScreen)null),
                    Index = shopDecoIndex,
                    LotB = isShopLotB,
                    PredictionId = Guid.Empty,
                });
            }
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco")]
        private static class BuyPatch
        {
            [HarmonyPostfix]
            private static void Postfix(int shopDecoIndex)
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.BuyShopDecoration,
                    Category = DecorationInterop.CategoryFor((ShopBuyDecoUIScreen)null),
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
                    return;
                _active.BroadcastDelta(new DecorationDeltaMessage
                {
                    Action = DecorationActions.BuyItemDecoration,
                    DecorationType = itemType,
                    InventoryCount = DecorationInterop.InventoryCount(itemType),
                    PredictionId = Guid.Empty,
                });
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class PlacePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
            {
                if (_active == null || _active._applyingIntent || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None)
                    return;
                _active.BroadcastDelta(BuildDelta(new DecorationIntentMessage
                {
                    Action = DecorationActions.Place,
                    DecorationType = __instance.m_DecoObjectType,
                    Position = __instance.transform.position,
                    // A moved piece already owns a host id; pass it so the delta cannot bind to a
                    // different but nearby decoration of the same type.
                    ObjectId = DecorationInterop.HostIdFor(__instance),
                }));
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
            private static void Postfix(InteractableObject __instance, State __state)
            {
                if (_active == null || _active._applyingIntent || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None)
                    return;
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

    }
}
