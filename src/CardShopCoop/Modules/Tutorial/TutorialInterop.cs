using System;
using System.Reflection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>Private tutorial progress and presentation access.</summary>
    internal static class TutorialInterop
    {
        internal static readonly FieldInfo FiSubgroupCurrent = AccessTools.Field(
            typeof(TutorialSubGroup), "m_CurrentValue");
        internal static readonly FieldInfo FiSubgroupFinished = AccessTools.Field(
            typeof(TutorialSubGroup), "m_IsTaskFinish");
        internal static readonly FieldInfo FiTutorialIndicator = AccessTools.Field(
            typeof(TutorialManager), "m_TutorialTargetIndicator");
        internal static readonly FieldInfo FiTutorialFinished = AccessTools.Field(
            typeof(TutorialManager), "m_FinishedTutorial");
        private static InteractionPlayerController _controller;
        private static GameUIScreen _gameUi;

        internal static void Reset()
        {
            _controller = null;
            _gameUi = null;
        }

        internal static T FindSceneObject<T>() where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.scene.IsValid())
                {
                    return all[i];
                }
            }

            return null;
        }

        internal static TutorialManager FindManager()
            => SceneRef<TutorialManager>.Get();

        internal static bool IsSceneReady(TutorialManager manager)
        {
            if (manager == null || manager.m_TutorialSubGroupList == null
                || manager.m_TutorialTargetIndicator == null)
            {
                return false;
            }

            for (var i = 0; i < manager.m_TutorialSubGroupList.Count; i++)
            {
                var group = manager.m_TutorialSubGroupList[i];
                if (group == null || group.m_TutorialData == null || group.m_ScreenGrp == null)
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool TryGetExpectedAction(out int tutorialIndex, out int action)
        {
            tutorialIndex = CPlayerData.m_TutorialIndex;
            action = (int)ETutorialTaskCondition.None;
            var manager = FindManager();
            if (!IsSceneReady(manager) || tutorialIndex <= 0
                || tutorialIndex > manager.m_TutorialSubGroupList.Count)
            {
                return false;
            }

            var group = manager.m_TutorialSubGroupList[tutorialIndex - 1];
            if (group == null || group.m_TutorialData == null)
            {
                return false;
            }

            action = (int)group.m_TutorialData.tutorialTaskCondition;
            return action != (int)ETutorialTaskCondition.None;
        }

        internal static float ValueFor(ETutorialTaskCondition condition)
        {
            var values = CPlayerData.m_TutorialDataList;
            for (var i = 0; values != null && i < values.Count; i++)
                if (values[i] != null && values[i].tutorialTaskCondition == condition)
                    return values[i].value;
            return 0f;
        }

        internal static void SyncMarker(TutorialManager manager, int tutorialIndex)
        {
            if (manager == null || FiTutorialIndicator == null)
            {
                return;
            }

            var indicator = FiTutorialIndicator.GetValue(manager) as GameObject;
            if (indicator != null)
            {
                indicator.SetActive(tutorialIndex == 0);
            }
        }

        /// <summary>The game only ever sets <c>m_FinishedTutorial</c> true and never clears it, so
        /// a guest that once saw the tutorial as finished would ignore every later
        /// <c>AddTaskValue</c>. An authoritative baseline rebuilds the flag from the host state.</summary>
        internal static void SetFinished(TutorialManager manager, bool finished)
        {
            if (manager == null || FiTutorialFinished == null)
            {
                return;
            }

            FiTutorialFinished.SetValue(manager, finished);
        }

        internal static void ClearIntroPresentation()
        {
            if (_controller == null)
            {
                _controller = FindSceneObject<InteractionPlayerController>();
            }

            var controller = _controller;
            if (controller != null)
            {
                var actions = new[]
                {
                    EGameAction.MoveForward,
                    EGameAction.MoveLeft,
                    EGameAction.MoveBackward,
                    EGameAction.MoveRight,
                    EGameAction.Jump,
                    EGameAction.Sprint,
                    EGameAction.Crouch,
                };
                for (var i = 0; i < actions.Length; i++)
                {
                    InteractionPlayerController.RemoveToolTip(actions[i]);
                }
            }

            if (_gameUi == null)
            {
                _gameUi = FindSceneObject<GameUIScreen>();
            }

            if (_gameUi != null)
            {
                GameUIScreen.SetGameUIVisible(true);
            }
        }

        internal static bool IsFiniteNonNegative(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
    }
}
