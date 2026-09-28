using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Settings
{
    /// <summary>Non-fabricating lookup for the scene-owned settings manager, and the narrow
    /// boundary into placement's stable identity registry for cashier counters and play tables.</summary>
    internal static class SettingsInterop
    {
        internal const int CashierKind = 4;
        internal const int PlayTableKind = 6;

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

        /// <summary>The stable placement key of a cashier counter, or false when it has none.</summary>
        internal static bool TryGetCashierKey(InteractableCashierCounter counter, out int key)
            => PlacementApi.TryMakeObjectKey(CashierKind, counter, out key);

        /// <summary>The stable placement key of a play table, or false when it has none.</summary>
        internal static bool TryGetTableKey(InteractablePlayTable table, out int key)
            => PlacementApi.TryMakeObjectKey(PlayTableKind, table, out key);

        /// <summary>Resolves a cashier key straight back to the live counter and rechecks that the
        /// object still computes the same key, mirroring the play-table intent path. A key that
        /// names nothing, or names a different object now, resolves to null.</summary>
        internal static InteractableCashierCounter ResolveCashier(int key)
            => PlacementApi.ResolveObjectByKey(key) is InteractableCashierCounter counter
                && TryGetCashierKey(counter, out var computed) && computed == key ? counter : null;

        /// <summary>Resolves a play-table key straight back to the live table and rechecks that the
        /// object still computes the same key.</summary>
        internal static InteractablePlayTable ResolveTable(int key)
            => PlacementApi.ResolveObjectByKey(key) is InteractablePlayTable table
                && TryGetTableKey(table, out var computed) && computed == key ? table : null;
    }
}
