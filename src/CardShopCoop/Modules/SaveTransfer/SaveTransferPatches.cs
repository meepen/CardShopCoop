using System;
using HarmonyLib;

namespace CardShopCoop.Modules.SaveTransfer
{
    internal static class SaveTransferPatches
    {
        internal static void Apply(Harmony harmony)
        {
            TryPatch(harmony, typeof(CGameManager), "SaveGameData",
                prefix: new HarmonyMethod(typeof(SaveTransferPatches), nameof(SaveGuardPrefix)));

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
