using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>Client intent hooks and host-owned decoration state application.</summary>
    [ClientBehaviour]
    public sealed class DecorationClientBehaviour : CoopBehaviour
    {
        private static DecorationClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private DecorationStateMessage _pendingState;
        private InteractableObject _pendingPlacement;
        private readonly Dictionary<Guid, InteractableObject> _placementPreviews = new();
        private bool _shutdown;
        private int _applyingState;
        private bool _joined;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            var lifecycle = false;
            try
            {
                ResetSessionState();
                DecorationInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                PredictionApi.PredictionRetired += OnPredictionRetired;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.decoration.client");
                _harmony.CreateClassProcessor(typeof(EquipPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BuyItemPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(MovePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PlacePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(RemovePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ShelfReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                PredictionApi.PredictionRetired -= OnPredictionRetired;
                if (ReferenceEquals(_active, this))
                    _active = null;
                ResetSessionState();
                DecorationInterop.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(DecorationStateMessage))]
        private void HandleState(MessageContext context, DecorationStateMessage message)
        {
            if (_shutdown)
                return;

            _pendingState = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(DecorationDeltaMessage))]
        private void HandleDelta(MessageContext context, DecorationDeltaMessage message)
        {
            if (_shutdown)
                return;
            // Take the predicted preview out of the map before the authoritative apply runs. On
            // success the apply adopts it; on rollback the prediction is retired without this
            // delta and the retire handler discards it. Either way the preview is claimed once.
            InteractableObject preview = null;
            if (message.Action == DecorationActions.Place
                && _placementPreviews.TryGetValue(message.PredictionId, out var tracked))
            {
                preview = tracked;
                _placementPreviews.Remove(message.PredictionId);
            }

            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyDelta(message, preview));
        }

        private void OnPredictionRetired(Guid predictionId)
        {
            if (!_placementPreviews.TryGetValue(predictionId, out var preview))
                return;

            _placementPreviews.Remove(predictionId);
            DecorationInterop.DiscardPreview(preview);
        }

        private static void ApplyDelta(DecorationDeltaMessage message,
            InteractableObject preview = null)
        {
            if (_active != null)
                _active._applyingState++;
            try
            {
                DecorationInterop.ApplyDelta(message, preview);
            }
            finally
            {
                if (_active != null)
                    _active._applyingState--;
            }
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingState == null || _context == null || !_context.InGame()
                || !DecorationInterop.IsSceneReady())
                return;

            _applyingState++;
            try
            {
                DecorationInterop.ApplySnapshot(_pendingState);
                _pendingState = null;
            }
            finally
            {
                _applyingState--;
            }
        }

        private void Send(DecorationIntentMessage message)
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame() || message == null)
                return;

            _context.Send(1, message);
        }

        private static bool Predict(DecorationIntentMessage message, Action apply, Action undo,
            InteractableObject preview = null)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return true;
            PredictionApi.Predict("decoration",
                predictionId =>
                {
                    message.PredictionId = predictionId;
                    if (preview != null)
                        client._placementPreviews[predictionId] = preview;
                    client._context.Send(1, message);
                }, apply, undo);
            return false;
        }

        private static DecorationStateMessage SnapshotMessage(DecorationSnapshot snapshot)
            => new DecorationStateMessage
            {
                Wall = snapshot.Wall,
                WallB = snapshot.WallB,
                Floor = snapshot.Floor,
                FloorB = snapshot.FloorB,
                Ceiling = snapshot.Ceiling,
                CeilingB = snapshot.CeilingB,
                WallUnlocks = snapshot.WallUnlocks,
                FloorUnlocks = snapshot.FloorUnlocks,
                CeilingUnlocks = snapshot.CeilingUnlocks,
                Inventory = snapshot.Inventory,
                Placed = snapshot.Placed,
            };

        private static void PredictState(DecorationIntentMessage message, DecorationSnapshot before)
        {
            var predicted = new DecorationDeltaMessage
            {
                Action = message.Action,
                Category = message.Category,
                Index = message.Index,
                DecorationType = message.DecorationType,
                LotB = message.LotB,
                ObjectId = message.ObjectId,
                Pose = message.Action == DecorationActions.Place
                    ? new DecorationPose
                    {
                        Id = message.ObjectId,
                        DecorationType = message.DecorationType,
                        Position = message.Position,
                        Rotation = message.Rotation,
                        Vertical = message.Vertical,
                        WarehouseWallSnap = message.WarehouseWallSnap,
                        Wall = message.WallIndex,
                    }
                    : null,
            };
            if (message.Action == DecorationActions.BuyItemDecoration)
                predicted.InventoryCount = DecorationInterop.InventoryCount(message.DecorationType) + 1;
            ApplyDelta(predicted);
            // A local move is still in the vanilla move lifecycle. Keep the local preview visible
            // until the hook exits it; the authoritative delta will create/resolve its stable id.
            if (message.Action == DecorationActions.Place)
            {
                var moving = SceneRef<InteractionPlayerController>.Get();
                moving?.OnExitMoveObjectMode();
            }
        }

        private static void UndoState(DecorationSnapshot before)
        {
            if (_active != null)
                _active._applyingState++;
            try
            {
                // A rollback restores owned state, but must not delete pieces the host already
                // confirmed from a snapshot taken before they existed.
                DecorationInterop.ApplySnapshot(SnapshotMessage(before), removeUnlisted: false);
            }
            finally
            {
                if (_active != null)
                    _active._applyingState--;
            }
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            DecorationInterop.Reset();
            TryApplyPending();
        }

        private void OnShelfReady() => TryApplyPending();

        private static bool IsClientReady()
            => _active != null && !_active._shutdown && _active._joined
                && _active._context != null && _active._context.InGame();

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
            _pendingPlacement = null;
            _placementPreviews.Clear();
            _applyingState = 0;
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
                ResetSessionState();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            PredictionApi.PredictionRetired -= OnPredictionRetired;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            ResetSessionState();
            DecorationInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(PlaceDecoUIScreen), "OnPressSwitchShopDeco")]
        private static class EquipPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(PlaceDecoUIScreen __instance, int shopDecoIndex, bool isShopLotB)
            {
                if (!IsClientReady())
                    return true;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Equip,
                    Category = DecorationInterop.CategoryFor(__instance),
                    Index = shopDecoIndex,
                    LotB = isShopLotB,
                };
                var before = DecorationInterop.Snapshot();
                return Predict(message, () =>
                {
                    DecorationInterop.ApplyDelta(new DecorationDeltaMessage
                    {
                        Action = message.Action,
                        Category = message.Category,
                        Index = message.Index,
                        LotB = message.LotB,
                    });
                }, () => UndoState(before));
            }
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco")]
        private static class BuyPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(int shopDecoIndex, float price)
            {
                if (!IsClientReady())
                    return true;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.BuyShopDecoration,
                    Category = DecorationInterop.CategoryFor((ShopBuyDecoUIScreen)null),
                    Index = shopDecoIndex,
                };
                var before = DecorationInterop.Snapshot();
                return Predict(message, () => PredictState(message, before),
                    () => UndoState(before));
            }
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDecoItem")]
        private static class BuyItemPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(EDecoObject itemType, float price)
            {
                if (!IsClientReady())
                    return true;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.BuyItemDecoration,
                    DecorationType = (int)itemType,
                };
                var before = DecorationInterop.Snapshot();
                return Predict(message, () => PredictState(message, before),
                    () => UndoState(before));
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "StartMoveObject")]
        private static class MovePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
            {
                if (!IsClientReady() || _active._applyingState != 0 || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None)
                    return;
                _active._pendingPlacement = __instance.GetIsMovingObject() ? __instance : null;
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class PlacePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
            {
                if (!IsClientReady() || _active._applyingState != 0 || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None || !__instance.GetIsMovingObject()
                    || !ReferenceEquals(__instance, _active._pendingPlacement))
                    return true;
                var pose = DecorationInterop.ReadPose(__instance);
                var objectId = DecorationInterop.ClientIdFor(__instance);
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Place,
                    DecorationType = pose.DecorationType,
                    Position = pose.Position,
                    Rotation = pose.Rotation,
                    Vertical = pose.Vertical,
                    WarehouseWallSnap = pose.WarehouseWallSnap,
                    WallIndex = pose.Wall,
                    ObjectId = objectId,
                };
                var before = DecorationInterop.Snapshot();
                // Leave the preview mid-move: it keeps its colliders off and its layer ignored
                // while the host decides. On confirmation the delta adopts it, so no second piece
                // is ever spawned; on rejection the retire handler discards it.
                var predicted = Predict(message, () =>
                {
                    DecorationInterop.ApplyPredictedPose(__instance, pose);
                    SceneRef<InteractionPlayerController>.Get()?.OnExitMoveObjectMode();
                }, () => UndoState(before), objectId > 0 ? null : __instance);
                if (predicted)
                    return true;
                SceneRef<InteractionPlayerController>.Get()?.OnExitMoveObjectMode();
                _active._pendingPlacement = null;
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class RemovePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
            {
                if (!IsClientReady() || _active._applyingState != 0 || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None)
                    return true;
                var objectId = DecorationInterop.ClientIdFor(__instance);
                if (objectId <= 0)
                    return true;
                var decorationType = (int)__instance.m_DecoObjectType;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Remove,
                    DecorationType = decorationType,
                    ObjectId = objectId,
                };
                var before = DecorationInterop.Snapshot();
                var predicted = Predict(message, () =>
                {
                    // Mirror vanilla BoxUpObject: settle the move lifecycle before destroying the
                    // piece, otherwise the controller and the placement overlay stay active. The
                    // authoritative delta carries the resulting inventory count.
                    DecorationInterop.FinalizeMovedObject(__instance);
                    DecorationInterop.RemoveLocalPlacedObject(__instance);
                }, () => UndoState(before));
                if (predicted)
                    return true;
                SceneRef<InteractionPlayerController>.Get()?.OnExitMoveObjectMode();
                _active._pendingPlacement = null;
                return false;
            }
        }

        [HarmonyPatch(typeof(ShelfManager), "Awake")]
        private static class ShelfReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.OnShelfReady();
        }
    }
}
