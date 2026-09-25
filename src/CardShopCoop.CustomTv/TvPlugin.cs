using BepInEx;

namespace CardShopCoop.CustomTv
{
    /// <summary>
    /// Anchor plugin for RTCGO Custom TV co-op sync. CardShopCoop discovers this plugin through
    /// the BepInEx dependency graph and integrates its behaviours and messages automatically; the
    /// plugin itself intentionally owns no state.
    ///
    /// The dependency is SOFT: this plugin still loads (and does nothing) when CardShopCoop is not
    /// installed, so a player can keep Custom TV support in their mod list either way.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.zwhit.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class TvPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.zwhit.cardshopcoop.customtv";
        public const string Name = "CardShopCoop Custom TV";
    }
}
