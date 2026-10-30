using BepInEx;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>
    /// Anchor plugin for Purchase Overhaul co-op support. CardShopCoop discovers this plugin
    /// through the BepInEx dependency graph and integrates its behaviours and messages.
    ///
    /// Both dependencies are soft: this plugin still loads and does nothing when either
    /// CardShopCoop or Purchase Overhaul is absent.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("dev.meepen.cardshopcoop",
        BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.qolmods.purchaseoverhaul",
        BepInDependency.DependencyFlags.SoftDependency)]
    public partial class PurchaseOverhaulPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.meepen.cardshopcoop.purchaseoverhaul";
        public const string Name = "CardShopCoopCommunity Purchase Overhaul";
    }
}
