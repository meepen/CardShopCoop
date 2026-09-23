using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Settings
{
    /// <summary>Non-fabricating lookup for the scene-owned settings manager.</summary>
    internal static class SettingsInterop
    {
        internal static ShelfManager FindShelfManager()
            => SceneRef<ShelfManager>.Get();

        internal static bool IsSceneReady
        {
            get
            {
                var manager = FindShelfManager();
                return manager != null && manager.m_FinishLoadingObjectData
                    && manager.m_CashierCounterList != null && manager.m_PlayTableList != null
                    && CPlayerData.m_SetGameEventPriceList != null;
            }
        }

    }
}
