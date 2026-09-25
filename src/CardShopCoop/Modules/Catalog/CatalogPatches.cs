using System;
using HarmonyLib;

namespace CardShopCoop.Modules.Catalog
{
    internal static class CatalogPatches
    {
        internal static void ApplyClientLifecycle(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(ResetData)).Patch();
            harmony.CreateClassProcessor(typeof(CreateDefaultData)).Patch();
            harmony.CreateClassProcessor(typeof(PropagateLoadData)).Patch();
            harmony.CreateClassProcessor(typeof(SaveLoad)).Patch();
        }

        [HarmonyPatch(typeof(CPlayerData), "ResetData")]
        private static class ResetData
        {
            [HarmonyPostfix]
            private static void Postfix()
                => CatalogClientBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CPlayerData), "CreateDefaultData")]
        private static class CreateDefaultData
        {
            [HarmonyPostfix]
            private static void Postfix()
                => CatalogClientBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CGameData), "PropagateLoadData")]
        private static class PropagateLoadData
        {
            [HarmonyPostfix]
            private static void Postfix()
                => CatalogClientBehaviour.InventoryReset();
        }

        /// <summary>After the native save is deserialized into CSaveLoad.m_SavedGame, but before
        /// the caller propagates it into CPlayerData, translate the host's gated enum ids into this
        /// client's ids. One-shot: armed only when the client is about to load the borrowed world,
        /// so a local save load is never rewritten.</summary>
        [HarmonyPatch(typeof(CSaveLoad), "Load")]
        private static class SaveLoad
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!SaveEnumRemap.TryConsume())
                {
                    return;
                }

                try
                {
                    var field = typeof(CSaveLoad).GetField("m_SavedGame",
                        System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                    var saved = field?.GetValue(null);
                    if (saved != null)
                    {
                        SaveEnumRemap.Apply(saved);
                        CoopPlugin.Log.LogInfo("save enum remap: translated the borrowed world's ids.");
                    }
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogWarning("save enum remap failed: " + error.Message);
                }
            }
        }
    }
}
