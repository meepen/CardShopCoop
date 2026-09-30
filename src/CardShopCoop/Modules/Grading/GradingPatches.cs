using System;
using System.Reflection;
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
            // mirror); the prefix captures and the postfix registers one post-hoc prediction.
            Try(harmony, typeof(GradedCardSubmitSelectScreen), "OnPressSubmitButton",
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitCapturePrefix)),
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitObservePostfix)),
                new HarmonyMethod(typeof(GradingPatches), nameof(SubmitFinalizer)));
            // The guest does not run the day-start maturation. The host owns it and pushes the
            // matured jobs/sets; running it locally also spawned a second, guest-only delivery box
            // (bound to a fresh local id that never reconciled with the host's broadcast box) and,
            // when the host's push landed first, advanced job progress twice.
            Try(harmony, typeof(RestockManager), "OnDayStarted",
                new HarmonyMethod(typeof(GradingPatches), nameof(ClientMaturationPrefix)));
            // Grading Overhaul patches the same listener with its own priority-800 void prefix.
            // A prefix returning false only skips the original method - every other prefix still
            // runs (verified against 0Harmony: a priority-800 false prefix does not stop a
            // priority-400 one). GO's own Prefix therefore has to be wrapped as well, or a GO
            // guest still grades cards, mints certificates and spawns a second delivery box.
            TryPatchGoDayStart(harmony);
            // Never trust that the guards installed: a silently missing guard would let the
            // guest grade its own jobs again. Verify both and fail closed, loudly, if not.
            ValidateGuestDayStartSurface(harmony);
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

        /// <summary>Guest: wrap Grading Overhaul's day-start prefix. GO's prefix (priority 800,
        /// void) forces every job's minutes, grades the cards, mints certificates and removes the
        /// job from the list; its priority-0 postfix only spawns the delivery box for the
        /// completed-job list that prefix builds, so blocking the prefix neutralizes the postfix
        /// too.</summary>
        private static void TryPatchGoDayStart(Harmony harmony)
        {
            // Gate on GoDetected, not Present: the patch must be re-attempted and its result
            // re-derived on each module enable, even when a prior enable left it failing.
            if (!GradingInterop.GoDetected)
            {
                return;
            }

            if (!GradingInterop.GoDayStartMethodMatches)
            {
                CoopPlugin.Log.LogError("Grading Overhaul's day-start prefix does not match this "
                    + "CardShopCoop build (expected "
                    + "CompanyStamp_RestockManager_OnDayStartedPatch.Prefix() -> bool/void); the "
                    + "guest cannot block its local grading.");
                return;
            }

            try
            {
                harmony.Patch(GradingInterop.GoDayStartMethod,
                    prefix: new HarmonyMethod(typeof(GradingPatches),
                        nameof(ClientMaturationPrefix)));
            }
            catch (Exception patchError)
            {
                // Harmony may have installed part of a patch before reporting an error.
                // Roll back this exact patch id before the surface validation reports failure.
                harmony.Unpatch(GradingInterop.GoDayStartMethod, HarmonyPatchType.All, harmony.Id);
                CoopPlugin.Log.LogError("Grading Overhaul's day-start guard failed to install: "
                    + patchError.Message);
            }
        }

        /// <summary>Guest: verify both day-start guards are actually installed and fail closed
        /// when they are not. Without the vanilla block - and, with GO installed, the wrap of GO's
        /// prefix - the guest grades its own jobs locally (duplicate certificates and a second
        /// delivery box), so a missing guard must be loud, never a warning nobody reads.</summary>
        private static void ValidateGuestDayStartSurface(Harmony harmony)
        {
            var vanilla = ReflectionSurface.OptionalMethod(typeof(RestockManager), "OnDayStarted");
            if (!IsGuardInstalled(vanilla, harmony))
            {
                FailGuestDayStartSurface("the guest day-start block on RestockManager.OnDayStarted "
                    + "is not installed");
                return;
            }

            if (GradingInterop.GoDetected
                && (!GradingInterop.GoDayStartMethodMatches
                    || !IsGuardInstalled(GradingInterop.GoDayStartMethod, harmony)))
            {
                FailGuestDayStartSurface("Grading Overhaul's day-start prefix could not be blocked "
                    + "on the guest");
                return;
            }

            GradingInterop.SetDayStartSurface(true, null);
        }

        private static void FailGuestDayStartSurface(string reason)
        {
            // SetDayStartSurface logs the fail-closed error itself only when GO is installed.
            if (!GradingInterop.GoDetected)
            {
                CoopPlugin.Log.LogError("Grading: " + reason + "; the guest would grade its own "
                    + "jobs. Running the vanilla grading fallback.");
            }

            GradingInterop.SetDayStartSurface(false, reason);
        }

        private static bool IsGuardInstalled(MethodInfo original, Harmony harmony)
        {
            if (original == null)
            {
                return false;
            }

            try
            {
                var patches = Harmony.GetPatchInfo(original);
                if (patches?.Prefixes == null)
                {
                    return false;
                }

                foreach (var prefix in patches.Prefixes)
                {
                    if (prefix.owner == harmony.Id
                        && prefix.PatchMethod?.Name == nameof(ClientMaturationPrefix))
                    {
                        return true;
                    }
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Grading day-start guard probe failed for "
                    + original.DeclaringType?.Name + "." + original.Name + ": " + error.Message);
            }

            return false;
        }

        /// <summary>Guest: keeps the vanilla grading maturation (and Grading Overhaul's, via the
        /// wrap of its prefix) from running locally. The host owns both and pushes the matured
        /// jobs/sets plus the delivery box. A null active client means the session is gone and the
        /// game may run vanilla again.</summary>
        private static bool ClientMaturationPrefix()
            => GradingClientBehaviour.Active == null;

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
