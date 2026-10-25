using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Settings
{
    /// <summary>Non-fabricating lookup for the scene-owned settings manager. Cashier counters and
    /// play tables are addressed by their live objects; this module no longer owns a placement-key
    /// boundary.</summary>
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
