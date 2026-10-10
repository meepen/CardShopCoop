using BepInEx;

namespace CardShopCoop.CustomTv
{
    /// <summary>
    /// Anchor plugin for RTCGO Custom TV co-op sync. CardShopCoopCommunity discovers this plugin through
    /// the BepInEx dependency graph and integrates its behaviours and messages automatically; the
    /// plugin itself intentionally owns no state.
    ///
    /// The dependency is SOFT: this plugin still loads (and does nothing) when CardShopCoopCommunity is not
    /// installed, so a player can keep Custom TV support in their mod list either way.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("dev.meepen.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class TvPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.meepen.cardshopcoop.customtv";
        public const string Name = "CardShopCoopCommunity Custom TV";
    }
}
