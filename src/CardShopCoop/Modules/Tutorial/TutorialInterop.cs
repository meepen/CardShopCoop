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
        private static GameUIScreen _gameUi;

        internal static void Reset()
        {
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

        internal static void SyncMarker(TutorialManager manager, int tutorialIndex)
        {
            if (manager == null || FiTutorialIndicator == null)
            {
                return;
            }

            var indicator = FiTutorialIndicator.GetValue(manager) as GameObject;
            indicator?.SetActive(tutorialIndex == 0);
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

        /// <summary>
        /// Ends the intro's local presentation once the shared tutorial leaves index 0: the
        /// movement key hints and the hidden HUD. The target marker is the intro's own state - it
        /// is shown together with the key hints and hidden by the trigger/confirm path - so it
        /// tells us whether this peer still has the intro on screen. Call this before
        /// <see cref="SyncMarker"/> hides the marker, and never force the HUD back over an
        /// unrelated screen when the intro is already gone.
        /// </summary>
        internal static void ClearIntroPresentation()
        {
            var manager = FindManager();
            var indicator = manager == null || FiTutorialIndicator == null
                ? null
                : FiTutorialIndicator.GetValue(manager) as GameObject;
            if (indicator == null || !indicator.activeSelf)
            {
                return;
            }

            // The game's own clear-all, rather than replaying the movement action list: the intro
            // is the only thing showing hints while the marker is up.
            if (SceneRef<InteractionPlayerController>.Get() != null)
            {
                InteractionPlayerController.ClearToolTip();
            }

            if (_gameUi == null)
            {
                _gameUi = FindSceneObject<GameUIScreen>();
            }

            if (_gameUi != null)
            {
                GameUIScreen.SetGameUIVisible(true);
            }

            CoopPlugin.Log.LogDebug(
                "Tutorial intro presentation cleared; the shared tutorial left the intro.");
        }

        internal static bool IsFiniteNonNegative(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
    }
}
