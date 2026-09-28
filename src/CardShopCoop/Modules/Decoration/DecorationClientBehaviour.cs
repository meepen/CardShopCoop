using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
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

            // Confirm, not AckOrApply: the host's pose carries the canonical id and adopts the
            // claimed preview. Retiring-only would skip ApplyDelta for the actor's own placement,
            // so its preview would never be adopted or bound to the host id (the preview was
            // already claimed above, and the retire handler no longer sees it).
            PredictionApi.Confirm(message.PredictionId,
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

        /// <summary>Records a decoration action the game already performed (the hook observes
        /// vanilla from a postfix) as one post-hoc prediction. apply/undo redo and reverse the same
        /// change through the module's game path so a rejection can be reconciled; the game owns the
        /// local mutation.</summary>
        private static void Predict(DecorationIntentMessage message, Action apply,
            Action undo, InteractableObject preview = null)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return;
            PredictionApi.Predict("decoration",
                predictionId =>
                {
                    message.PredictionId = predictionId;
                    if (preview != null)
                        client._placementPreviews[predictionId] = preview;
                    client._context.Send(1, message);
                }, apply, undo);
        }

        /// <summary>Redoes the wallet half of a replay. Queued during reconciliation, so the Hud
        /// economy observer does not forward it as a second contribution.</summary>
        private static void Charge(float amount)
        {
            if (amount > 0.0001f)
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(amount));
        }

        /// <summary>Reverses the wallet half of a rejected buy through the game's own coin event.</summary>
        private static void Refund(float amount)
        {
            if (amount > 0.0001f)
                CEventManager.QueueEvent(new CEventPlayer_AddCoin(amount, true));
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
            private sealed class State
            {
                public DecorationSnapshot Before;
                public int Category;
                public int Index;
                public bool LotB;
            }

            [HarmonyPrefix]
            private static void Prefix(PlaceDecoUIScreen __instance, int shopDecoIndex,
                bool isShopLotB, out State __state)
            {
                __state = null;
                if (!IsClientReady() || _active._applyingState != 0)
                    return;
                __state = new State
                {
                    Before = DecorationInterop.Snapshot(),
                    Category = DecorationInterop.CategoryFor(__instance),
                    Index = shopDecoIndex,
                    LotB = isShopLotB,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(State __state)
            {
                if (__state == null || !IsClientReady() || _active._applyingState != 0
                    || PredictionApi.IsReconciling)
                    return;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Equip,
                    Category = __state.Category,
                    Index = __state.Index,
                    LotB = __state.LotB,
                };
                Predict(message, () => DecorationInterop.ApplyDelta(new DecorationDeltaMessage
                {
                    Action = message.Action,
                    Category = message.Category,
                    Index = message.Index,
                    LotB = message.LotB,
                }), () => UndoState(__state.Before));
            }
        }

        private struct BuyCapture
        {
            public bool Armed;
            public DecorationSnapshot Before;
            public float UpgradeCostBefore;
            public float SupplyCostBefore;
        }

        private static void CaptureBuy(out BuyCapture state)
        {
            state = default;
            if (!IsClientReady())
                return;
            state.Armed = true;
            state.Before = DecorationInterop.Snapshot();
            // The wallet event is only queued by the vanilla buy (it runs later), but the report
            // cost is decremented synchronously, so it is the reliable "did it charge" signal.
            state.UpgradeCostBefore = CPlayerData.m_GameReportDataCollectPermanent.upgradeCost;
            state.SupplyCostBefore = CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            EconomyActionScope.Enter();
        }

        private static void ReleaseBuy(BuyCapture state)
        {
            if (state.Armed)
                EconomyActionScope.Exit();
        }

        /// <summary>Completes an observed vanilla buy: the game already purchased and charged, so
        /// register one post-hoc prediction whose replay re-charges and whose rejection refunds the
        /// mirrored wallet alongside the restored unlock/inventory state.</summary>
        private static void ObserveBuy(DecorationIntentMessage message, BuyCapture state)
        {
            if (!state.Armed)
                return;
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return;

            var spent = (state.UpgradeCostBefore
                - CPlayerData.m_GameReportDataCollectPermanent.upgradeCost)
                + (state.SupplyCostBefore
                    - CPlayerData.m_GameReportDataCollectPermanent.supplyCost);
            // The vanilla buy charges only when it succeeds; no charge means nothing happened.
            if (spent <= 0.0001f)
                return;

            var before = state.Before;
            Predict(message,
                () =>
                {
                    PredictState(message, before);
                    Charge(spent);
                },
                () =>
                {
                    UndoState(before);
                    Refund(spent);
                });
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco")]
        private static class BuyPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out BuyCapture __state) => CaptureBuy(out __state);

            [HarmonyPostfix]
            private static void Postfix(int shopDecoIndex, BuyCapture __state)
                => ObserveBuy(new DecorationIntentMessage
                {
                    Action = DecorationActions.BuyShopDecoration,
                    Category = DecorationInterop.CategoryFor((ShopBuyDecoUIScreen)null),
                    Index = shopDecoIndex,
                }, __state);

            [HarmonyFinalizer]
            private static void Finalizer(BuyCapture __state) => ReleaseBuy(__state);
        }

        [HarmonyPatch(typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDecoItem")]
        private static class BuyItemPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out BuyCapture __state) => CaptureBuy(out __state);

            [HarmonyPostfix]
            private static void Postfix(EDecoObject itemType, BuyCapture __state)
                => ObserveBuy(new DecorationIntentMessage
                {
                    Action = DecorationActions.BuyItemDecoration,
                    DecorationType = itemType,
                }, __state);

            [HarmonyFinalizer]
            private static void Finalizer(BuyCapture __state) => ReleaseBuy(__state);
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
            private sealed class State
            {
                public DecorationSnapshot Before;
                public DecorationPose Pose;
                public long ObjectId;
                public InteractableObject Instance;
                public InteractableObject Preview;
            }

            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out State __state)
            {
                __state = null;
                if (!IsClientReady() || _active._applyingState != 0 || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None || !__instance.GetIsMovingObject()
                    || !ReferenceEquals(__instance, _active._pendingPlacement))
                    return;
                var pose = DecorationInterop.ReadPose(__instance);
                var objectId = DecorationInterop.ClientIdFor(__instance);
                __state = new State
                {
                    Before = DecorationInterop.Snapshot(),
                    Pose = pose,
                    ObjectId = objectId,
                    Instance = __instance,
                    // A brand-new piece has no host id yet, so hand it to the delta as the preview
                    // it may adopt instead of spawning a second piece.
                    Preview = objectId > 0 ? null : __instance,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(State __state)
            {
                var client = _active;
                if (__state == null || client == null)
                    return;
                client._pendingPlacement = null;
                if (!IsClientReady() || client._applyingState != 0 || PredictionApi.IsReconciling
                    || __state.Instance == null || __state.Instance.GetIsMovingObject())
                    return;
                var pose = __state.Pose;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Place,
                    DecorationType = pose.DecorationType,
                    Position = pose.Position,
                    Rotation = pose.Rotation,
                    Vertical = pose.Vertical,
                    WarehouseWallSnap = pose.WarehouseWallSnap,
                    WallIndex = pose.Wall,
                    ObjectId = __state.ObjectId,
                };
                var instance = __state.Instance;
                Predict(message, () =>
                {
                    DecorationInterop.ApplyPredictedPose(instance, pose);
                    SceneRef<InteractionPlayerController>.Get()?.OnExitMoveObjectMode();
                }, () => UndoState(__state.Before), __state.Preview);
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class RemovePatch
        {
            private sealed class State
            {
                public DecorationSnapshot Before;
                public long ObjectId;
                public EDecoObject Type;
                public InteractableObject Instance;
            }

            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out State __state)
            {
                __state = null;
                if (!IsClientReady() || _active._applyingState != 0 || __instance == null
                    || __instance.m_DecoObjectType == EDecoObject.None)
                    return;
                var objectId = DecorationInterop.ClientIdFor(__instance);
                if (objectId <= 0)
                    return;
                __state = new State
                {
                    Before = DecorationInterop.Snapshot(),
                    ObjectId = objectId,
                    Type = __instance.m_DecoObjectType,
                    Instance = __instance,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(State __state)
            {
                if (__state == null || !IsClientReady() || _active._applyingState != 0
                    || PredictionApi.IsReconciling)
                    return;
                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Remove,
                    DecorationType = __state.Type,
                    ObjectId = __state.ObjectId,
                };
                var instance = __state.Instance;
                Predict(message, () =>
                {
                    // Mirror vanilla BoxUpObject on replay: settle the move lifecycle before
                    // destroying the piece. The authoritative delta carries the resulting
                    // inventory count.
                    DecorationInterop.FinalizeMovedObject(instance);
                    DecorationInterop.RemoveLocalPlacedObject(instance);
                }, () => UndoState(__state.Before));
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
