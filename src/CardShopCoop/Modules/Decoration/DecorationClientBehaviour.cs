using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>Client intent hooks and host-owned decoration state application.</summary>
    [ClientBehaviour]
    public sealed class DecorationClientBehaviour : CoopBehaviour
    {
        private static DecorationClientBehaviour _active;
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


            var lifecycle = false;
            try
            {
                ResetSessionState();
                DecorationInterop.Reset();
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


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                ResetSessionState();
                DecorationInterop.Reset();
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
            // This delta only settles inventory/equip state.
            ApplyDelta(message);
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
            if (_shutdown || !RuntimeContext.InGame()
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
        /// vanilla from a postfix) as one prediction. The prediction reverses the local change and
        /// settles it against the host's response; the game owns the local mutation.</summary>
        private static void Predict(DecorationIntentMessage message,
            IDecorationPrediction prediction)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined
                || !client.RuntimeContext.InGame()
                || ReentrancyGuard.Suppressed(client))
            {
                return;
            }

            message.Prediction = prediction;
            client.Context.Send(message);
        }

        /// <summary>Reverses the wallet half of a rejected buy through the game's own coin event.</summary>
        private static void Refund(float amount)
        {
            if (amount > 0.0001f)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin(amount, true));
            }

        }

        /// <summary>One equip/buy prediction: restores the captured snapshot (and refunds the
        /// wallet for a buy) on rejection, then merges the host's authoritative response.</summary>
        private sealed class StatePrediction : IDecorationPrediction
        {
            private readonly DecorationSnapshot _before;
            private readonly float _refund;

            internal StatePrediction(DecorationSnapshot before, float refund)
            {
                _before = before;
                _refund = refund;
            }

            public void Undo()
            {
                UndoState(_before);
                Refund(_refund);
            }

            public void Merge(ActionResponseMessage response)
                => _active?.ApplyResponse(response);
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

        /// <summary>Applies an authoritative decoration response from another module's predicted
        /// action (identity placement/removal) through the same client path.</summary>
        internal static void ApplyResponseState(ActionResponseMessage response)
            => _active?.ApplyResponse(response);

        /// <summary>The host's answer: an authoritative snapshot on reject, the post-action delta
        /// on accept. The snapshot goes through the same deferred-apply path as a join state so a
        /// scene that is still loading keeps the latest value.</summary>
        private void ApplyResponse(ActionResponseMessage response)
        {
            if (_shutdown || response is not DecorationIntentResponse result)
            {
                return;
            }

            if (result.State != null)
            {
                _pendingState = result.State;
                TryApplyPending();
                return;
            }

            if (result.Delta != null)
            {
                ApplyDelta(result.Delta);
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
                && _active.RuntimeContext.InGame();

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
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


            ResetSessionState();
            DecorationInterop.Reset();
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
                    || ReentrancyGuard.Suppressed(_active))
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
                    || ReentrancyGuard.Suppressed(_active))
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
                Predict(message, new StatePrediction(__state.Before, 0f));
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
            if (client == null || client._shutdown || !client._joined
                || !client.RuntimeContext.InGame())
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
            Predict(message, new StatePrediction(before, spent));
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
