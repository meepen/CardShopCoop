using System;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Util;
using HarmonyLib;

namespace CardShopCoop.Modules.Grading
{
    internal static class GradingPatches
    {
        internal static void ApplyClient(Harmony harmony)
        {
            // The client runs the vanilla submission (the game mints the job and charges the
            // mirror); the prefix captures and the postfix registers one post-hoc prediction. The
            // day-start maturation is no longer suppressed: the guest runs it vanilla and the host
            // pushes the matured jobs/sets as deltas, which overwrite the local reroll.
            Try(harmony, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton",
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitCapturePrefix)),
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitObservePostfix)),
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitFinalizer)));
            InstallDataLifecyclePatches(harmony);
        }

        internal static void ApplyHost(Harmony harmony)
        {
            ValidateGoDayStartSurface();
            Try(harmony, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton", null,
                new HarmonyMethod(typeof(GradingPatches), nameof(HostSubmitPostfix)));
            Try(harmony, typeof(RestockManager), "OnDayStarted", null,
                new HarmonyMethod(typeof(GradingPatches), nameof(HostDayStartedPostfix)));
            InstallDataLifecyclePatches(harmony);
        }

        private static void ValidateGoDayStartSurface()
        {
            // Gate on GoDetected, not Present: a previously-known-bad day-start surface must be
            // re-derived on each module enable, never flipped ready by the "GO absent" shortcut.
            if (!GradingInterop.GoDetected)
            {
                GradingInterop.SetDayStartSurface(true, null);
                return;
            }

            if (!GradingInterop.GoDayStartMethodMatches)
            {
                GradingInterop.SetDayStartSurface(false,
                    "exact type/method signature was not found: "
                    + "CompanyStamp_RestockManager_OnDayStartedPatch.Prefix() -> bool");
                return;
            }

            GradingInterop.SetDayStartSurface(true, null);
        }

        private static void Try(Harmony harmony, Type type, string name,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null)
        {
            try
            {
                var original = ReflectionSurface.OptionalMethod(type, name);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("Grading patch target missing: " + type.Name + "." + name);
                    return;
                }
                harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Grading patch failed for " + type.Name + "." + name + ": " + error.Message);
            }
        }

        private static void SubmitCapturePrefix(GradedCardSubmitSelectScreen __instance,
            out GradingClientBehaviour.SubmissionCapture __state)
        {
            __state = default;
            var active = GradingClientBehaviour.Active;
            if (active == null)
                return;
            active.CaptureSubmission(__instance, out __state);
        }

        private static void SubmitObservePostfix(GradingClientBehaviour.SubmissionCapture __state)
            => GradingClientBehaviour.Active?.ObserveSubmission(__state);

        private static void SubmitFinalizer(GradingClientBehaviour.SubmissionCapture __state)
        {
            if (__state.Armed)
                EconomyActionScope.Exit();
        }

        private static void HostDayStartedPostfix()
        {
            GradingHostBehaviour.Active?.PushCurrentSets();
        }

        private static void HostSubmitPostfix()
        {
            GradingHostBehaviour.Active?.PushCurrentSets();
        }

        private static void InstallDataLifecyclePatches(Harmony harmony)
        {
            Try(harmony, typeof(CPlayerData), "ResetData", null,
                new HarmonyMethod(typeof(GradingPatches), nameof(DataLifecyclePostfix)));
            Try(harmony, typeof(CPlayerData), "CreateDefaultData", null,
                new HarmonyMethod(typeof(GradingPatches), nameof(DataLifecyclePostfix)));
            Try(harmony, typeof(CGameData), "PropagateLoadData", null,
                new HarmonyMethod(typeof(GradingPatches), nameof(DataLifecyclePostfix)));
        }

        private static void DataLifecyclePostfix()
        {
            GradingHostBehaviour.InventoryReset();
            GradingClientBehaviour.InventoryReset();
        }
    }
}
