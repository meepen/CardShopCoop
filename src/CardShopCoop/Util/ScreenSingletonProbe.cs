using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Read-only report of the game's scene-lifetime UI singletons.
    ///
    /// The 1.00 build caches every CSingleton&lt;T&gt; behind an isSet latch: once Instance resolves
    /// a screen, it keeps returning that reference until CSingleton.OnDestroy clears the latch.
    /// When a scene change destroys the cached screen and the latch survives, every later read
    /// hits a destroyed native object (a ThrowHelper NullReferenceException), and the first of
    /// those aborts CGameManager.OnLevelFinishedLoading before m_IsGameLevel is set. The probe
    /// observes and reports that state only; it deliberately changes nothing in the game.
    /// </summary>
    internal static class ScreenSingletonProbe
    {
        private static readonly Type[] ScreenTypes =
        {
            typeof(LoadingScreen),
            typeof(SaveLoadGameSlotSelectScreen),
            typeof(SettingScreen),
            typeof(PauseScreen)
        };

        private static readonly Dictionary<Type, SingletonFields> CachedFields = new();
        private static readonly HashSet<string> LatchesReported = new();

        internal static void Install()
        {
            var harmony = new Harmony("com.zwhit.cardshopcoop.screen-singletons");
            TryPatch(harmony, typeof(CGameManager), "OnLevelFinishedLoading",
                nameof(ReportLevelFinishedLoading));
            TryPatch(harmony, typeof(LoadingScreen), "CloseScreen", nameof(ReportLoadingScreenClose));
            TryPatch(harmony, typeof(PauseScreen), "CloseScreen", nameof(ReportPauseScreenClose));
            TryPatch(harmony, typeof(SaveLoadGameSlotSelectScreen), "OpenScreen",
                nameof(ReportSaveScreenOpen));
        }

        private static void ReportLevelFinishedLoading() => Report("level finished loading");
        private static void ReportLoadingScreenClose() => Report("loading screen close");
        private static void ReportPauseScreenClose() => Report("pause menu close");
        private static void ReportSaveScreenOpen() => Report("save/load screen open");

        private static void Report(string site)
        {
            var parts = new List<string>(ScreenTypes.Length);
            for (var i = 0; i < ScreenTypes.Length; i++)
            {
                parts.Add(Describe(ScreenTypes[i]));
            }

            CoopPlugin.Log.LogInfo("[screens] " + site + ": " + string.Join(" ", parts));
        }

        private static string Describe(Type screenType)
        {
            var fields = GetFields(screenType);
            var instance = fields.Instance?.GetValue(null) as Component;
            var isSet = fields.IsSet == null ? (bool?)null : (bool)fields.IsSet.GetValue(null);
            var live = UnityEngine.Object.FindObjectOfType(screenType) as Component;

            if (isSet == true && instance == null && LatchesReported.Add(screenType.Name))
            {
                CoopPlugin.Log.LogWarning(
                    "[screens] " + screenType.Name + " is latched to a destroyed object: "
                    + "CSingleton<T>.isSet is still true while the native screen is gone, so the game "
                    + "keeps reading the dead scene object. CardShopCoop does not modify game state.");
            }

            var instanceText = instance == null
                ? (isSet == true ? "destroyed" : "unresolved")
                : instance.gameObject.name;
            var liveText = live == null ? "none" : live.gameObject.name;
            return screenType.Name + "[isSet="
                + (isSet.HasValue ? (isSet.Value ? "true" : "false") : "n/a")
                + " instance=" + instanceText + " live=" + liveText + "]";
        }

        private static SingletonFields GetFields(Type screenType)
        {
            if (CachedFields.TryGetValue(screenType, out var fields))
            {
                return fields;
            }

            var singletonType = typeof(CSingleton<>).MakeGenericType(screenType);
            fields = new SingletonFields(
                singletonType.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic),
                singletonType.GetField("isSet", BindingFlags.Static | BindingFlags.NonPublic));
            CachedFields.Add(screenType, fields);
            return fields;
        }

        private static void TryPatch(Harmony harmony, Type type, string method, string prefixName)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("[screens] probe target missing: " + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(ScreenSingletonProbe), prefixName));
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("[screens] probe patch failed for " + type.Name + "." + method
                    + ": " + e.Message);
            }
        }

        private sealed class SingletonFields
        {
            private readonly FieldInfo _instance;
            private readonly FieldInfo _isSet;

            internal SingletonFields(FieldInfo instance, FieldInfo isSet)
            {
                _instance = instance;
                _isSet = isSet;
            }

            internal FieldInfo Instance => _instance;

            internal FieldInfo IsSet => _isSet;
        }
    }
}
