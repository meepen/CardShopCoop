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
    }
}
