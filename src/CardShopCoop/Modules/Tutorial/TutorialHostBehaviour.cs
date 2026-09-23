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
    /// <summary>Host authority for tutorial progress and bounded action validation.</summary>
    [ServerBehaviour]
    public sealed class TutorialHostBehaviour : CoopBehaviour
    {
        private const float MaxActionIncrement = 100f;
        private const int MaxStateValues = 4096;
        private static TutorialHostBehaviour _active;
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
                TutorialInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tutorial.host");
                _harmony.CreateClassProcessor(typeof(TutorialVisibilityPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tutorial host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                TutorialInterop.Reset();
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
        private void ForgetPeer(PeerConnection connection, DisconnectInfo info)
        {
            if (connection == null)
                return;
            _fullyJoined.Remove(connection.Id);
        }

        [MessageHandler(typeof(TutorialActionDeltaMessage))]
        private void HandleAction(MessageContext context, TutorialActionDeltaMessage message)
        {
            if (!IsPeerMessage(context) || message == null
                || !ValidateAction(message))
            {
                if (message != null)
                {
                    CoopPlugin.Log.LogDebug("tutorial intent rejected from peer "
                        + (context?.ConnectionId ?? 0)
                        + ": expected index=" + message.ExpectedTutorialIndex
                        + " condition=" + message.ExpectedCondition
                        + " action=" + message.Action
                        + " increment=" + message.Increment);
                }
                if (message != null && message.PredictionId != Guid.Empty)
                    PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                return;
            }

            try
            {
                _applyingIntent = true;
                try
                {
                    TutorialManager.AddTaskValue((ETutorialTaskCondition)message.Action,
                        message.Increment);
                }
                finally
                {
                    _applyingIntent = false;
                }
                BroadcastDelta(BuildDelta(message.PredictionId,
                    (ETutorialTaskCondition)message.Action, message.ExpectedTutorialIndex));
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tutorial action failed for peer "
                    + context.Connection.Id + ": " + exception);
                if (message.PredictionId != Guid.Empty)
                    PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
            }
        }

        private bool ValidateAction(TutorialActionDeltaMessage message)
        {
            if (!Enum.IsDefined(typeof(ETutorialTaskCondition), message.Action)
                || message.Action == (int)ETutorialTaskCondition.None)
                return false;
            if (message.ExpectedCondition != message.Action)
                return false;
            if (message.Increment <= 0f || float.IsNaN(message.Increment)
                || float.IsInfinity(message.Increment) || message.Increment > MaxActionIncrement)
                return false;
            if (!TutorialInterop.TryGetExpectedAction(out var expectedIndex, out var expectedAction)
                || message.ExpectedTutorialIndex != expectedIndex || message.Action != expectedAction)
                return false;
            return true;
        }

        private void BroadcastDelta(TutorialDeltaMessage message)
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

        private bool TryBuildState(out TutorialStateMessage message)
        {
            message = null;
            if (_context == null || !_context.InGame()
                || !TutorialInterop.IsSceneReady(TutorialInterop.FindManager())
                || CPlayerData.m_TutorialDataList == null
                || CPlayerData.m_TutorialDataList.Count > MaxStateValues)
                return false;

            var list = CPlayerData.m_TutorialDataList;
            message = new TutorialStateMessage
            {
                Full = true,
                TutorialIndex = CPlayerData.m_TutorialIndex,
            };
            for (var i = 0; i < list.Count; i++)
            {
                var value = list[i];
                if (value == null || !TutorialInterop.IsFiniteNonNegative(value.value))
                    return false;
                message.Values.Add(new TutorialValue
                {
                    Condition = (int)value.tutorialTaskCondition,
                    Value = value.value,
                });
            }
            return true;
        }

        private static TutorialDeltaMessage BuildDelta(Guid predictionId,
            ETutorialTaskCondition condition, int previousIndex)
        {
            var message = new TutorialDeltaMessage
            {
                PredictionId = predictionId,
                TutorialIndex = CPlayerData.m_TutorialIndex,
                Condition = (int)condition,
                Value = TutorialInterop.ValueFor(condition),
            };
            if (previousIndex != message.TutorialIndex)
            {
                message.Values = new List<TutorialValue>();
                var values = CPlayerData.m_TutorialDataList;
                for (var i = 0; values != null && i < values.Count; i++)
                    message.Values.Add(new TutorialValue
                    {
                        Condition = (int)values[i].tutorialTaskCondition,
                        Value = values[i].value,
                    });
            }
            return message;
        }

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TutorialInterop.Reset();
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
            TutorialInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(TutorialManager), "AddTaskValue")]
        private static class TutorialVisibilityPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out int __state)
                => __state = CPlayerData.m_TutorialIndex;

            [HarmonyPostfix]
            private static void Postfix(ETutorialTaskCondition tutorialTaskCondition, int __state)
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta(Guid.Empty, tutorialTaskCondition, __state));
            }
        }

    }
}
