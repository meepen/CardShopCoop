using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>Guest tutorial action forwarding and host-state application.</summary>
    [ClientBehaviour]
    public sealed class TutorialClientBehaviour : CoopBehaviour
    {
        private static TutorialClientBehaviour _active;
        private static bool _applyingRemote;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private TutorialStateMessage _pendingState;
        // Retained single-condition deltas keyed by condition, in a stable (condition-ordered)
        // order. A later delta for the same condition supersedes the earlier one; distinct
        // conditions all survive so one cannot overwrite another while the scene is not ready.
        private readonly SortedDictionary<int, PendingTutorialDelta> _pendingDeltas = new();

        /// <summary>A single-condition authoritative delta retained while the tutorial scene is
        /// not ready. It records the absolute host value and index so it can be applied from the
        /// same lifecycle hooks as the full-state baseline. Applying it is idempotent: the value
        /// is re-set to the absolute host value and the index to the host index.</summary>
        private readonly struct PendingTutorialDelta
        {
            internal PendingTutorialDelta(int condition, float value, int tutorialIndex)
            {
                Condition = condition;
                Value = value;
                TutorialIndex = tutorialIndex;
            }

            internal int Condition
            {
                get;
            }
            internal float Value
            {
                get;
            }
            internal int TutorialIndex
            {
                get;
            }
        }

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
                TutorialInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tutorial.client");
                _harmony.CreateClassProcessor(typeof(CreditPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(TutorialReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tutorial client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                ResetSessionState();
                TutorialInterop.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(TutorialStateMessage))]
        private void HandleState(MessageContext context, TutorialStateMessage message)
        {
            if (_shutdown)
                return;

            _pendingState = message;
            // A full baseline supersedes any single-condition deltas retained before it.
            _pendingDeltas.Clear();
            TryApplyPending();
        }

        [MessageHandler(typeof(TutorialDeltaMessage))]
        private void HandleDelta(MessageContext context, TutorialDeltaMessage message)
        {
            if (_shutdown)
                return;

            if (message.Values != null)
            {
                // The host sends the WHOLE TutorialDataList when the index advances. That snapshot
                // supersedes the actor's optimistic run (the host may have continued past what the
                // local AddTaskValue produced), so AckOrApply would retire the actor's prediction
                // and drop the snapshot, leaving the guest behind and stalling its next intent.
                // Confirm retires the prediction and still folds the authoritative state-set.
                PredictionApi.Confirm(message.PredictionId, () => ApplyDelta(message));
                return;
            }

            PredictionApi.AckOrApply(message.PredictionId, () => ApplyDelta(message));
        }

        private void TryApplyPending()
        {
            if (_shutdown || _context == null || !_context.InGame())
                return;

            if (_pendingState != null)
            {
                var state = _pendingState;
                if (!TryApply(state))
                    return;
                CoopPlugin.Log.LogDebug("tutorial baseline applied: index=" + state.TutorialIndex
                    + " values=" + (state.Values == null ? 0 : state.Values.Count));
                _pendingState = null;
            }

            TryApplyPendingDelta();
        }

        /// <summary>Applies the retained single-condition deltas once the tutorial scene is ready.
        /// The full-state baseline, if any, is applied first so the deltas land on top of it. The
        /// whole set is snapshotted and cleared before applying so a re-entrant apply cannot
        /// observe a partially-consumed queue.</summary>
        private void TryApplyPendingDelta()
        {
            if (_pendingDeltas.Count == 0)
                return;
            if (!TutorialInterop.IsSceneReady(TutorialInterop.FindManager())
                || CPlayerData.m_TutorialDataList == null)
                return;

            var deltas = new List<PendingTutorialDelta>(_pendingDeltas.Values);
            _pendingDeltas.Clear();
            for (var i = 0; i < deltas.Count; i++)
            {
                var delta = deltas[i];
                var condition = (ETutorialTaskCondition)delta.Condition;
                ApplyLocalAction(condition, delta.Value - TutorialInterop.ValueFor(condition));
                CPlayerData.m_TutorialIndex = delta.TutorialIndex;
            }
        }

        /// <summary>The game already applied this tutorial change through its own
        /// <c>AddTaskValue</c>; register one post-hoc prediction whose apply is only used to
        /// replay after an earlier rollback, and whose undo reverts to the captured state. The
        /// host validates and echoes the action, and a rejection rolls it back. A local decrement
        /// (a displayed card taken back off a shelf) is observed the same way even though the host
        /// accepts progress only: the host rejects it and the rollback restores the pre-decrement
        /// state, after which the host's own authoritative value arrives through the normal delta.</summary>
        private static void ForwardAction(ETutorialTaskCondition condition, float increment,
            TutorialStateMessage previous)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
            {
                return;
            }

            PredictionApi.Predict(
                "tutorial",
                predictionId => client._context.Send(1, new TutorialActionDeltaMessage
                {
                    PredictionId = predictionId,
                    // The host validates against the index it held before the increment, so send
                    // the pre-apply capture rather than the already-advanced local index.
                    ExpectedTutorialIndex = previous.TutorialIndex,
                    ExpectedCondition = (int)condition,
                    Action = (int)condition,
                    Increment = increment,
                }),
                () => ApplyLocalAction(condition, increment),
                () => TryApply(previous));
        }

        private static TutorialStateMessage CaptureState()
        {
            var result = new TutorialStateMessage
            {
                TutorialIndex = CPlayerData.m_TutorialIndex,
            };
            var values = CPlayerData.m_TutorialDataList;
            for (var i = 0; values != null && i < values.Count; i++)
                result.Values.Add(new TutorialValue
                {
                    Condition = (int)values[i].tutorialTaskCondition,
                    Value = values[i].value,
                });
            return result;
        }

        private static void ApplyLocalAction(ETutorialTaskCondition condition, float increment)
        {
            _applyingRemote = true;
            try
            {
                TutorialManager.AddTaskValue(condition, increment);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        /// <summary>Applies an authoritative tutorial state. Returns false while the scene's
        /// tutorial surface is not ready; the caller retains the state and retries from its
        /// lifecycle hooks instead of failing the reliable message (which would drop the guest).</summary>
        private static bool TryApply(TutorialStateMessage message)
        {
            var manager = TutorialInterop.FindManager();
            if (!TutorialInterop.IsSceneReady(manager) || CPlayerData.m_TutorialDataList == null)
                return false;
            _applyingRemote = true;
            try
            {
                ApplyInner(manager, message);
            }
            finally
            {
                _applyingRemote = false;
            }

            return true;
        }

        private void ApplyDelta(TutorialDeltaMessage message)
        {
            if (message.Values != null)
            {
                // A full-state delta is a snapshot. Retain it exactly like the baseline so a
                // scene that is momentarily not ready cannot silently drop the host's state. It
                // supersedes any single-condition delta queued before it.
                _pendingState = new TutorialStateMessage
                {
                    TutorialIndex = message.TutorialIndex,
                    Values = message.Values,
                };
                _pendingDeltas.Clear();
                TryApplyPending();
                return;
            }

            // A single-condition delta is host-authoritative absolute state too. Retaining it (and
            // applying it from the lifecycle hooks once the scene is ready) keeps a momentarily
            // unavailable tutorial surface from dropping the host's progress instead of leaving the
            // guest behind. Keying by condition keeps distinct conditions from evicting one another;
            // a later value for the same condition supersedes the earlier one.
            _pendingDeltas[message.Condition] = new PendingTutorialDelta(message.Condition,
                message.Value, message.TutorialIndex);
            TryApplyPending();
        }

        private static void ApplyInner(TutorialManager manager, TutorialStateMessage message)
        {
            var count = message.Values.Count;
            var current = CPlayerData.m_TutorialDataList;
            var leavingIntro = message.TutorialIndex != 0 && CPlayerData.m_TutorialIndex == 0;
            TutorialInterop.SyncMarker(manager, message.TutorialIndex);
            if (leavingIntro)
                TutorialInterop.ClearIntroPresentation();

            var incomingValues = new List<TutorialData>(count);
            for (var i = 0; i < count; i++)
            {
                var value = message.Values[i];
                incomingValues.Add(new TutorialData
                {
                    tutorialTaskCondition = (ETutorialTaskCondition)value.Condition,
                    value = value.Value,
                });
            }
            current.Clear();
            current.AddRange(incomingValues);
            CPlayerData.m_TutorialIndex = message.TutorialIndex;

            // m_FinishedTutorial is sticky in the game: once set, AddTaskValue becomes a no-op
            // forever. Rebuild it from the authoritative state instead of inheriting whatever the
            // local machine happened to reach (an early guest once finished the intro by mistake).
            TutorialInterop.SetFinished(manager, false);

            for (var groupIndex = 0; groupIndex < manager.m_TutorialSubGroupList.Count; groupIndex++)
            {
                var group = manager.m_TutorialSubGroupList[groupIndex];
                if (TutorialInterop.FiSubgroupCurrent != null)
                    TutorialInterop.FiSubgroupCurrent.SetValue(group, 0f);
                if (TutorialInterop.FiSubgroupFinished != null)
                    TutorialInterop.FiSubgroupFinished.SetValue(group, false);
                group.m_TutorialData.value = 0f;
                for (var valueIndex = 0; valueIndex < incomingValues.Count; valueIndex++)
                    group.AddTaskValue(incomingValues[valueIndex].value,
                        incomingValues[valueIndex].tutorialTaskCondition);

                // The game's completion flag is sticky, and a task's stored value can drop below
                // its max when a displayed card is removed. Rebuilding the flag from the value
                // alone would therefore reopen a task the host has already moved past (the guest
                // would show "9/10" forever while the host rejects every further placement).
                // Honour the authoritative index: every group before it is complete. The group on
                // the index keeps its value-derived flag so a genuinely finished last task still
                // ends the tutorial.
                if (TutorialInterop.FiSubgroupFinished != null
                    && groupIndex < message.TutorialIndex - 1)
                    TutorialInterop.FiSubgroupFinished.SetValue(group, true);
            }

            if (message.TutorialIndex == 0)
            {
                // EvaluateTaskVisibility treats index 0 as "no task open": it closes every group
                // and then marks the whole tutorial finished. Index 0 is the intro, not the end of
                // the tutorial, so keep it alive with every group closed.
                for (var groupIndex = 0; groupIndex < manager.m_TutorialSubGroupList.Count; groupIndex++)
                    manager.m_TutorialSubGroupList[groupIndex].CloseScreen();
                TutorialInterop.SyncMarker(manager, 0);
                return;
            }

            manager.EvaluateTaskVisibility();
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TutorialInterop.Reset();
            TryApplyPending();
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

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
            _pendingDeltas.Clear();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            ResetSessionState();
            TutorialInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        /// <summary>
        /// True for the tutorial conditions the HOST itself produces when it applies the guest's
        /// action intent. The guest still runs vanilla <c>AddTaskValue</c> locally (its own UI
        /// advances immediately), but it must NOT forward a tutorial intent for these: the host's
        /// apply path already calls <c>TutorialManager.AddTaskValue</c> for the same condition, so
        /// forwarding would advance authoritative progress twice. The guest's local value is then
        /// overwritten by the host's authoritative delta.
        ///
        /// Host-derived conditions and their call sites:
        /// <list type="bullet">
        /// <item><description><c>ShopLevel</c> - <c>CPlayerData.CPlayer_OnAddShopExp</c>
        /// (CPlayerData.cs:2261), reached when the host applies the guest's XP-bearing purchase
        /// intents (e.g. PurchasingHostBehaviour ApplyRestockSideEffects / ApplyFurnitureSideEffects,
        /// ExpansionHostBehaviour).</description></item>
        /// <item><description><c>CheckoutCustomer</c> -
        /// <c>RegisterHostBehaviour.CreditGuestCheckout</c>, run when the host resolves the guest's
        /// register Complete/CardPayment intent (the game's own credit is gated on
        /// <c>m_IsMannedByPlayer</c>, which is false on a host applying a guest's intent).</description></item>
        /// <item><description><c>RestockItem</c> -
        /// <c>PurchasingHostBehaviour.ApplyRestockSideEffects</c> (PurchasingHostBehaviour.cs:946),
        /// run when the host applies the guest's restock/scanner intent. The guest-side call sites
        /// are RestockItemScreen.EvaluateCartCheckout and ScannerRestockScreen.EvaluateCartCheckout
        /// (decompiled RestockItemScreen.cs:272, ScannerRestockScreen.cs:326).</description></item>
        /// <item><description><c>UnlockBasicCardBox</c> -
        /// <c>CatalogHostBehaviour.ApplyProductEntitlementSideEffects</c>
        /// (CatalogHostBehaviour.cs:228), reached from the guest's product-license purchase
        /// (PurchasingHostBehaviour.cs:497 via CatalogApi.HostApplyProductEntitlementSideEffects)
        /// and from the catalog entitlement path (CatalogHostBehaviour.cs:122).</description></item>
        /// <item><description><c>SellCard</c> and <c>CustomerPlay</c> - the host's own customer
        /// simulation (<c>Customer.ExitShop</c>, decompiled Customer.cs:2798; and
        /// <c>Customer.PlayTableGameEnded</c>, Customer.cs:3205). Customers are host-simulated
        /// (the guest skips Customer.Start), so these are host-derived by construction.</description></item>
        /// </list>
        /// Every other condition is a client-player interaction the host does not re-execute with
        /// the same method (pack opening, shelf/card placement, pricing, boxes, the open/close sign,
        /// the play table, cashier entry), so the guest keeps forwarding those.
        /// </summary>
        private static bool IsHostDerived(ETutorialTaskCondition condition)
        {
            switch (condition)
            {
                case ETutorialTaskCondition.ShopLevel:
                case ETutorialTaskCondition.CheckoutCustomer:
                case ETutorialTaskCondition.RestockItem:
                case ETutorialTaskCondition.UnlockBasicCardBox:
                case ETutorialTaskCondition.SellCard:
                case ETutorialTaskCondition.CustomerPlay:
                    return true;
                default:
                    return false;
            }
        }

        [HarmonyPatch(typeof(TutorialManager), "AddTaskValue")]
        private static class CreditPatch
        {
            // Capture-only: vanilla always applies the change, then the postfix forwards one
            // post-hoc prediction for the action the game just performed. A local decrement runs
            // vanilla too; the host rejects it against its own progress and the rollback restores
            // the captured state. Host-derived conditions are not forwarded at all - the host's
            // apply path already credits the same condition, so forwarding would double it.
            [HarmonyPrefix]
            private static void Prefix(ETutorialTaskCondition tutorialTaskCondition, float valueAdd,
                out TutorialStateMessage __state)
            {
                __state = null;
                if (_applyingRemote || _active == null || !_active._joined)
                    return;

                if (_active._context == null || !_active._context.InGame())
                    return;

                if (IsHostDerived(tutorialTaskCondition))
                    return;

                __state = CaptureState();
            }

            [HarmonyPostfix]
            private static void Postfix(ETutorialTaskCondition tutorialTaskCondition, float valueAdd,
                TutorialStateMessage __state)
            {
                if (__state == null)
                    return;
                ForwardAction(tutorialTaskCondition, valueAdd, __state);
            }
        }

        [HarmonyPatch(typeof(TutorialManager), "Awake")]
        private static class TutorialReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }
    }
}
