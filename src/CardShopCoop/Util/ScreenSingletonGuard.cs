using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Keeps the game's scene-lifetime UI singletons honest.
    ///
    /// <c>CSingleton&lt;T&gt;</c> latches on first use: <c>Instance</c> resolves a screen once and keeps
    /// returning that reference until <c>CSingleton.OnDestroy</c> sets <c>isSet</c> back to false. On the
    /// 1.00 build the latch survives the scene change that destroys the cached screen, so from then on
    /// every read of <c>Instance</c> hits a destroyed native object.
    ///
    /// That is not theoretical: <c>InteractionPlayerController.Update</c> reads
    /// <c>CSingleton&lt;SettingScreen&gt;.Instance.m_ScreenGrp</c> on several lines and calls
    /// <c>ManualUpdate()</c> every frame, and the dead reference makes the first of those throw a
    /// ThrowHelper NullReferenceException. The throw aborts the rest of <c>Update</c>, and because
    /// everything that opens or closes that screen also goes through the same dead singleton, the pause
    /// menu cannot be dismissed and its keybind rows never get populated. The same throw aborts
    /// <c>CGameManager.OnLevelFinishedLoading</c> before <c>m_IsGameLevel</c> is set, which leaves the
    /// level looking like it never loaded.
    ///
    /// The game means for <c>isSet</c> to be false once the screen is gone; it just never gets there. This
    /// restores exactly that state - but ONLY when a live object of that type exists to re-resolve to.
    /// With none, <c>CSingleton.Instance</c> would fabricate a bare singleton whose fields are all null,
    /// which is worse than the dead object it replaced.
    ///
    /// <c>ControllerScreenUIExtManager</c> is covered for the same reason and matters more: it is reached
    /// from the level-load path, where its throw lands before <c>m_IsGameLevel</c> is set. When that type
    /// has no live replacement at all the latch cannot be repaired, so the one call that gates the load is
    /// also contained (see <c>ContainDeadObject</c>).
    /// </summary>
    internal static class ScreenSingletonGuard
    {
        private static readonly Type[] WatchedTypes =
        {
            typeof(LoadingScreen),
            typeof(SaveLoadGameSlotSelectScreen),
            typeof(SettingScreen),
            typeof(PauseScreen),

            // Also latched, and reached from the level-load path: InputManager.Init calls
            // ControllerScreenUIExtManager.SetControllerActive (whose UI group can be a destroyed
            // menu-scene object, taking CGameManager.OnLevelFinishedLoading down before m_IsGameLevel
            // is set - i.e. the level never counts as loaded), and SetControllerActive touches
            // TouchManager. A destroyed manager still answers field reads because its C# object graph
            // is intact, which is why the throw lands inside GameObject.SetActive, not on the field.
            typeof(ControllerScreenUIExtManager),
            typeof(TouchManager)
        };

        private static readonly Dictionary<Type, SingletonFields> CachedFields = new();

        internal static void Install()
        {
            var harmony = new Harmony("dev.meepen.cardshopcoop.screen-singletons");
            TryPatch(harmony, typeof(CGameManager), "OnLevelFinishedLoading",
                nameof(ReportLevelFinishedLoading));
            TryPatch(harmony, typeof(LoadingScreen), "CloseScreen", nameof(ReportLoadingScreenClose));
            TryPatch(harmony, typeof(PauseScreen), "CloseScreen", nameof(ReportPauseScreenClose));
            TryPatch(harmony, typeof(SaveLoadGameSlotSelectScreen), "OpenScreen",
                nameof(ReportSaveScreenOpen));
            TryFinalize(harmony, typeof(ControllerScreenUIExtManager), "SetControllerActive",
                nameof(ContainDeadObject));
        }

        private static void ReportLevelFinishedLoading() => Report("level finished loading");
        private static void ReportLoadingScreenClose() => Report("loading screen close");
        private static void ReportPauseScreenClose() => Report("pause menu close");
        private static void ReportSaveScreenOpen() => Report("save/load screen open");

        // These are prefixes on the game's own entry points, so a latch is cleared before the game's
        // body runs - including OnLevelFinishedLoading, whose body is where the first throw happens.
        private static void Report(string site)
        {
            var parts = new List<string>(WatchedTypes.Length);
            for (var i = 0; i < WatchedTypes.Length; i++)
            {
                parts.Add(Describe(WatchedTypes[i]));
            }

            CoopPlugin.Log.LogInfo("[screens] " + site + ": " + string.Join(" ", parts));
        }

        private static string Describe(Type screenType)
        {
            var fields = GetFields(screenType);
            var instance = fields.Instance?.GetValue(null) as Component;
            var isSet = fields.IsSet == null ? (bool?)null : (bool)fields.IsSet.GetValue(null);
            var live = UnityEngine.Object.FindFirstObjectByType(screenType) as Component;

            // Unity's == null is true for a destroyed object, which is exactly the latched state.
            if (isSet == true && instance == null)
            {
                if (live != null)
                {
                    // Clear the latch (report the reason once); the game's next read of Instance
                    // resolves `live` instead of throwing, because the getter re-runs when !isSet.
                    if (fields.IsSet != null)
                    {
                        fields.IsSet.SetValue(null, false);
                        isSet = false;
                    }

                    CoopLog.WarnOnce("screen:repair:" + screenType.Name,
                        "[screens] " + screenType.Name + " was latched to a destroyed object while "
                        + "CSingleton<T>.isSet stayed true, so the game read a dead screen every frame "
                        + "(NullReferenceException from InteractionPlayerController.Update). Cleared "
                        + "isSet, which is what the game's own OnDestroy does; the next access resolves "
                        + "the live " + live.gameObject.name + ".");
                }
                else
                {
                    CoopLog.WarnOnce("screen:deadend:" + screenType.Name,
                        "[screens] " + screenType.Name + " is latched to a destroyed object and no live "
                        + "screen exists to point it at, so the latch is left alone - clearing it would "
                        + "make CSingleton<T>.Instance fabricate a bare singleton whose fields are all "
                        + "null.");
                }
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
                    prefix: new HarmonyMethod(typeof(ScreenSingletonGuard), prefixName));
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("[screens] probe patch failed for " + type.Name + "." + method
                    + ": " + e.Message);
            }
        }

        private static void TryFinalize(Harmony harmony, Type type, string method, string finalizerName)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("[screens] guard target missing: " + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original,
                    finalizer: new HarmonyMethod(typeof(ScreenSingletonGuard), finalizerName));
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("[screens] guard patch failed for " + type.Name + "." + method
                    + ": " + e.Message);
            }
        }

        /// <summary>Last line of defence for the level-load path.
        ///
        /// <c>InputManager.Init</c> calls <c>SetControllerActive</c>, and
        /// <c>CGameManager.OnLevelFinishedLoading</c> sets <c>m_IsGameLevel</c> only after that returns.
        /// When the manager is a latched singleton with no live replacement (see the class doc) this call
        /// dereferences a destroyed UI object and takes the whole load down with it, so the level never
        /// counts as loaded. Swallowing exactly that throw - Unity's binding-layer NullReferenceException
        /// for a dead native object, and nothing else - lets the rest of the load run.</summary>
        private static Exception ContainDeadObject(Exception __exception)
        {
            if (__exception == null || !IsDeadNativeObject(__exception))
            {
                return __exception;
            }

            CoopLog.WarnOnce("screen:contain:SetControllerActive",
                "[screens] ControllerScreenUIExtManager.SetControllerActive reached a destroyed UI object. "
                + "That throw lands before m_IsGameLevel is set, so the level would never finish loading; "
                + "it is swallowed here so InputManager.Init and CGameManager.OnLevelFinishedLoading can "
                + "complete. The controller selector UI will not have been updated.");

            return null;
        }

        /// <summary>Unity 6's binding layer throws this when the native object behind a managed wrapper is
        /// gone. That is the signature of the bug contained above, as opposed to a genuine null field,
        /// which is left to throw.</summary>
        private static bool IsDeadNativeObject(Exception e)
        {
            return e is NullReferenceException
                && e.StackTrace != null
                && e.StackTrace.Contains("ThrowHelper.ThrowNullReferenceException");
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
