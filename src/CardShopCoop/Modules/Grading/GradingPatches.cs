using System;
using CardShopCoop.Util;
using HarmonyLib;

namespace CardShopCoop.Modules.Grading
{
    internal static class GradingPatches
    {
        internal static void ApplyClient(Harmony harmony)
        {
            Try(harmony, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton",
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitPrefix)));
            Try(harmony, typeof(RestockManager), "OnDayStarted",
                new HarmonyMethod(typeof(GradingPatches), nameof(ClientMaturationPrefix)));
            TryPatchGoDayStart(harmony);
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
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.OptionalMethod(type, name);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("Grading patch target missing: " + type.Name + "." + name);
                    return;
                }
                harmony.Patch(original, prefix, postfix);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Grading patch failed for " + type.Name + "." + name + ": " + error.Message);
            }
        }

        private static void TryPatchGoDayStart(Harmony harmony)
        {
            // Gate on GoDetected, not Present: the patch must be re-attempted and its result
            // re-derived on each module enable, even when a prior enable left it failing.
            if (!GradingInterop.GoDetected)
            {
                GradingInterop.SetDayStartSurface(true, null);
                return;
            }

            try
            {
                if (!GradingInterop.GoDayStartMethodMatches)
                {
                    GradingInterop.SetDayStartSurface(false,
                        "exact type/method signature was not found: "
                        + "CompanyStamp_RestockManager_OnDayStartedPatch.Prefix() -> bool");
                    return;
                }

                try
                {
                    harmony.Patch(GradingInterop.GoDayStartMethod, prefix: new HarmonyMethod(typeof(GradingPatches),
                        nameof(ClientMaturationPrefix)));
                    GradingInterop.SetDayStartSurface(true, null);
                }
                catch (Exception patchError)
                {
                    // Harmony may have installed part of a patch before reporting an error.
                    // Roll back this exact patch id before disabling GO grading.
                    harmony.Unpatch(GradingInterop.GoDayStartMethod, HarmonyPatchType.All, harmony.Id);
                    GradingInterop.SetDayStartSurface(false,
                        "exact day-start guard could not be applied: " + patchError.Message);
                }
            }
            catch (Exception error)
            {
                GradingInterop.SetDayStartSurface(false,
                    "exact day-start guard probe failed: " + error.Message);
            }
        }

        private static bool SubmitPrefix(GradedCardSubmitSelectScreen __instance)
        {
            var active = GradingClientBehaviour.Active;
            return active == null || active.Submit(__instance);
        }

        private static bool ClientMaturationPrefix()
        {
            return GradingClientBehaviour.Active == null;
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
