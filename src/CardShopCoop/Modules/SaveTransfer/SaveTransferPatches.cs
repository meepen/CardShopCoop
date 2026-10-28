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

            // The guest's borrowed world is injected here, BEFORE the game's own CSaveLoad.Load
            // runs. This is the only load entry point (CGameManager.LoadData calls it first), and
            // the ONLY cross-backend way to make it succeed: the public/Steam backend would read
            // the JSON file the transfer wrote, but the Xbox/Gamecore backend reads its native
            // store and would ignore that file, falling through to LoadBackupData ->
            // CreateDefaultData and leaving the guest in a fresh tutorial. The prefix sets
            // CSaveLoad.m_SavedGame from the received payload and reports success, so
            // PropagateLoadData runs with the host's world on every backend.
            TryPatch(harmony, typeof(CSaveLoad), "Load",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(SaveLoadPrefix)));

            TryPatch(harmony, typeof(CustomerManager), "SaveCustomerData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(CustomerSaveGuardPrefix)));

            TryPatch(harmony, typeof(WorkerManager), "SaveWorkerData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(WorkerSaveGuardPrefix)));

            // Resolve UpdateSlot reflectively. Both live builds have it; the null branch is for a
            // future build that moves it, and degrades to "no slot guard" instead of a failed patch.
            var updateSlot = SaveTransferInterop.UpdateSlotMethod;
            if (updateSlot == null)
            {
                CoopPlugin.Log.LogInfo(
                    "SaveTransfer: reflected SaveLoadGameSlotSelectScreen.UpdateSlot is absent; no slot guard is needed");
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
        /// Guest-side borrowed-world load. Runs before the game's own <c>CSaveLoad.Load(slot)</c>:
        /// when the transfer has armed the received world for this slot, deserialize it into
        /// CSaveLoad.m_SavedGame and report the load successful (skipping the backend read that
        /// would otherwise return false on the Xbox/Gamecore build). Falls through to the game's
        /// own load when no world is armed, so an ordinary local load is untouched.
        /// </summary>
        internal static bool SaveLoadPrefix(int slotIndex, ref bool __result)
        {
            if (!SaveTransferStorage.TryInjectBorrowedWorld(slotIndex, out var loaded))
            {
                return true;
            }

            __result = loaded;
            return false;
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
