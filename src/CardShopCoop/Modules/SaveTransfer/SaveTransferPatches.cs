using System;
using System.Threading;
using HarmonyLib;

namespace CardShopCoop.Modules.SaveTransfer
{
    internal static class SaveTransferPatches
    {
        // Logged once per co-op session generation; a process that is a guest more than once
        // must still produce the guard's evidence for every session, not just the first.
        private static int _customerLoggedGeneration = int.MinValue;
        private static int _workerLoggedGeneration = int.MinValue;

        internal static void Apply(Harmony harmony)
        {
            TryPatch(harmony, typeof(CGameManager), "SaveGameData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(SaveGuardPrefix)));

            TryPatch(harmony, typeof(CustomerManager), "SaveCustomerData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(CustomerSaveGuardPrefix)));

            TryPatch(harmony, typeof(WorkerManager), "SaveWorkerData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(WorkerSaveGuardPrefix)));

            // UpdateSlot did not exist in the legacy build. Resolve its target from the game
            // assembly instead of naming a beta-only method in a typed reference.
            var updateSlot = SaveTransferInterop.UpdateSlotMethod;
            if (updateSlot == null)
            {
                CoopPlugin.Log.LogInfo(
                    "SaveTransfer: reflected SaveLoadGameSlotSelectScreen.UpdateSlot is absent; legacy save UI needs no guard");
            }
            else
            {
                try
                {
                    harmony.Patch(updateSlot,
                        prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(UpdateSlotGuardPrefix)));
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("SaveTransfer reflected UpdateSlot patch failed: " + e.Message);
                }
            }
        }

        /// <summary>Joiners never write the host's borrowed world into their own save slots.</summary>
        internal static bool SaveGuardPrefix()
        {
            return SaveTransferRuntime.CanWriteSave;
        }

        /// <summary>
        /// On a guest the borrowed world's live customers are host-authoritative stand-ins, so the
        /// local vanilla customer list must not be rebuilt from them. Host and singleplayer keep
        /// writing it because their snapshot generation depends on it.
        /// </summary>
        internal static bool CustomerSaveGuardPrefix()
        {
            if (SaveTransferRuntime.CanWriteSave)
            {
                return true;
            }

            var generation = SaveTransferRuntime.SessionGeneration;
            if (Interlocked.Exchange(ref _customerLoggedGeneration, generation) != generation)
            {
                CoopPlugin.Log.LogInfo(
                    "[save-guard] skipped CustomerManager.SaveCustomerData on guest "
                    + "(host-derived NPC state kept out of the local save)"
                    + "; session=" + generation);
            }

            return false;
        }

        /// <summary>
        /// WorkerManager.SaveWorkerData clears and refills the list from EVERY worker, including
        /// inactive local stand-ins, so a guest must skip it to preserve the host/staff state the
        /// guest's UI reads. The host is not affected by this guard; its save path keeps writing
        /// the lists exactly as it did before.
        /// </summary>
        internal static bool WorkerSaveGuardPrefix()
        {
            if (SaveTransferRuntime.CanWriteSave)
            {
                return true;
            }

            var generation = SaveTransferRuntime.SessionGeneration;
            if (Interlocked.Exchange(ref _workerLoggedGeneration, generation) != generation)
            {
                CoopPlugin.Log.LogInfo(
                    "[save-guard] skipped WorkerManager.SaveWorkerData on guest "
                    + "(host-derived NPC state kept out of the local save)"
                    + "; session=" + generation);
            }

            return false;
        }

        /// <summary>
        /// 1.00's save path refreshes its four visible panels even when CGameManager is asked to
        /// save the out-of-band co-op snapshot slot 6. Skipping only indexes the screen cannot
        /// represent preserves normal slots and prevents the refresh from throwing after the
        /// save data itself has been written.
        /// </summary>
        internal static bool UpdateSlotGuardPrefix(int slot)
        {
            return SaveTransferInterop.IsVisibleSaveSlot(slot);
        }

        private static void TryPatch(Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("SaveTransfer patch target missing: " + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("SaveTransfer patch failed for " + type.Name + "." + method
                    + ": " + e.Message);
            }
        }
    }
}
