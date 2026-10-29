using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Actions;
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
        private DecorationStateMessage _pendingState;
        private bool _shutdown;
        private bool _initialized;
        private int _applyingState;
        private bool _joined;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }


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
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                Harmony.CreateClassProcessor(typeof(EquipPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BuyPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BuyItemPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration client initialization failed: " + exception);
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                ResetSessionState();
                DecorationInterop.Reset();
                _context = null;
                throw;
            }
            _initialized = true;
        }

        [MessageHandler]
        private void HandleState(MessageContext context, DecorationStateMessage message)
        {
            if (_shutdown)
            {
                return;
            }


            _pendingState = message;
            TryApplyPending();
        }

        [MessageHandler]
        private void HandleDelta(MessageContext context, DecorationDeltaMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            // Decoration lifecycle (added/moved/removed) is applied by the identity messages.
            // This delta only settles inventory/equip state, including the actor's prediction.
            ActionLedger.Confirm(message.ActionId, () => ApplyDelta(message));
        }

        private static void ApplyDelta(DecorationDeltaMessage message)
        {
            if (_active != null)
            {
                _active._applyingState++;
            }


            try
            {
                DecorationInterop.ApplyDelta(message);
            }
            finally
            {
                if (_active != null)
                {
                    _active._applyingState--;
                }

            }
        }

        private void TryApplyPending()
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !DecorationInterop.IsSceneReady() || _pendingState == null)
            {
                return;
            }


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

        /// <summary>Records a decoration action the game already performed (the hook observes
        /// vanilla from a postfix) as one post-hoc prediction. apply/undo redo and reverse the same
        /// change through the module's game path so a rejection can be reconciled; the game owns the
        /// local mutation.</summary>
        private static void Predict(DecorationIntentMessage message, Action apply, Action undo)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame() || !ActionLedger.IsActive
                || ActionLedger.IsReconciling)
            {
                return;
            }


            ActionLedger.Submit("decoration",
                actionId =>
                {
                    message.ActionId = actionId;
                    client._context.Send(1, message);
                }, apply, undo);
        }

        /// <summary>Redoes the wallet half of a replay. Queued during reconciliation, so the Hud
        /// economy observer does not forward it as a second contribution.</summary>
        private static void Charge(float amount)
        {
            if (amount > 0.0001f)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(amount));
            }

        }

        /// <summary>Reverses the wallet half of a rejected buy through the game's own coin event.</summary>
        private static void Refund(float amount)
        {
            if (amount > 0.0001f)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin(amount, true));
            }

        }

        private static void PredictState(DecorationIntentMessage message)
        {
            var predicted = new DecorationDeltaMessage
            {
                Action = message.Action,
                Category = message.Category,
                Index = message.Index,
                DecorationType = message.DecorationType,
                LotB = message.LotB,
            };
            if (message.Action == DecorationActions.BuyItemDecoration)
            {
                predicted.InventoryCount = DecorationInterop.InventoryCount(message.DecorationType) + 1;
            }


            ApplyDelta(predicted);
        }

        private static void UndoState(DecorationSnapshot before)
        {
            if (before == null || before.Generation != DecorationInterop.Generation)
            {
                // A guest reload can cross this action: the snapshot describes the outgoing world,
                // so its inventory must not be applied to the new one. The authoritative state
                // that follows the rollback is the current world's truth.
                CoopPlugin.Log.LogInfo("decoration rollback skipped: its scene is gone.");
                return;
            }

            if (_active != null)
            {
                _active._applyingState++;
            }


            try
            {
                // Decoration objects are owned by the identity rollback hook; this restores the
                // settings and inventory the equip/buy action touched.
                DecorationInterop.Restore(before);
            }
            finally
            {
                if (_active != null)
                {
                    _active._applyingState--;
                }

            }
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            DecorationInterop.Reset();
            TryApplyPending();
        }

        /// <summary>Called by the identity decoration lifecycle when the scene's shelf manager is
        /// awake, so a state message received during load is applied at a usable lifecycle point.</summary>
        internal static void NotifyShelfReady() => _active?.TryApplyPending();

        private static bool IsClientReady()
            => _active != null && !_active._shutdown && _active._joined
                && _active._context != null && _active._context.InGame();

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
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
            {
                ResetSessionState();
            }

        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


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
                if (!IsClientReady() || _active._applyingState != 0
                    || ActionLedger.IsReconciling)
                {
                    return;
                }


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
                    || ActionLedger.IsReconciling)
                {
                    return;
                }


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
            {
                return;
            }


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
            {
                EconomyActionScope.Exit();
            }

        }

        /// <summary>Completes an observed vanilla buy: the game already purchased and charged, so
        /// register one post-hoc prediction whose replay re-charges and whose rejection refunds the
        /// mirrored wallet alongside the restored unlock/inventory state.</summary>
        private static void ObserveBuy(DecorationIntentMessage message, BuyCapture state)
        {
            if (!state.Armed)
            {
                return;
            }


            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
            {
                return;
            }


            var spent = (state.UpgradeCostBefore
                - CPlayerData.m_GameReportDataCollectPermanent.upgradeCost)
                + (state.SupplyCostBefore
                    - CPlayerData.m_GameReportDataCollectPermanent.supplyCost);
            // The vanilla buy charges only when it succeeds; no charge means nothing happened.
            if (spent <= 0.0001f)
            {
                return;
            }


            var before = state.Before;
            Predict(message,
                () =>
                {
                    PredictState(message);
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
            private static void Postfix(ShopBuyDecoUIScreen __instance, int shopDecoIndex,
                BuyCapture __state)
                => ObserveBuy(new DecorationIntentMessage
                {
                    Action = DecorationActions.BuyShopDecoration,
                    Category = DecorationInterop.CategoryFor(__instance),
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

    }
}
