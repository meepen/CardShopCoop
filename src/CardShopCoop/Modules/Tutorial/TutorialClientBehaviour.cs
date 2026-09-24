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
            TryApplyPending();
        }

        [MessageHandler(typeof(TutorialDeltaMessage))]
        private void HandleDelta(MessageContext context, TutorialDeltaMessage message)
        {
            if (_shutdown)
                return;
            PredictionApi.ApplyAuthoritative(message.PredictionId, () => ApplyDelta(message));
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingState == null || _context == null || !_context.InGame())
                return;

            var state = _pendingState;
            if (!TryApply(state))
                return;
            CoopPlugin.Log.LogDebug("tutorial baseline applied: index=" + state.TutorialIndex
                + " values=" + (state.Values == null ? 0 : state.Values.Count));
            _pendingState = null;
        }

        private static bool PredictAction(ETutorialTaskCondition condition, float increment)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return false;

            var previous = CaptureState();
            PredictionApi.Predict(
                "tutorial",
                predictionId => client._context.Send(1, new TutorialActionDeltaMessage
                {
                    PredictionId = predictionId,
                    ExpectedTutorialIndex = CPlayerData.m_TutorialIndex,
                    ExpectedCondition = (int)condition,
                    Action = (int)condition,
                    Increment = increment,
                }),
                () => ApplyLocalAction(condition, increment),
                () => TryApply(previous));
            return false;
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
                // scene that is momentarily not ready cannot silently drop the host's state.
                _pendingState = new TutorialStateMessage
                {
                    TutorialIndex = message.TutorialIndex,
                    Values = message.Values,
                };
                TryApplyPending();
                return;
            }

            if (!TutorialInterop.IsSceneReady(TutorialInterop.FindManager())
                || CPlayerData.m_TutorialDataList == null)
                return;

            var current = TutorialInterop.ValueFor((ETutorialTaskCondition)message.Condition);
            ApplyLocalAction((ETutorialTaskCondition)message.Condition, message.Value - current);
            CPlayerData.m_TutorialIndex = message.TutorialIndex;
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

        [HarmonyPatch(typeof(TutorialManager), "AddTaskValue")]
        private static class CreditPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ETutorialTaskCondition tutorialTaskCondition, float valueAdd)
            {
                if (_applyingRemote || _active == null || !_active._joined)
                    return true;

                // The host only accepts progress, so a guest's local decrement (the game calls
                // AddTaskValue(PutCardOnShelf, -1) when a displayed card is taken back off a
                // shelf) must not be applied here. Applying it would desync the guest from the
                // host and could reopen a task the host has already completed.
                if (valueAdd <= 0f)
                {
                    CoopPlugin.Log.LogDebug("tutorial decrement ignored on client: condition="
                        + tutorialTaskCondition + " value=" + valueAdd + ".");
                    return false;
                }

                PredictAction(tutorialTaskCondition, valueAdd);
                return false;
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
