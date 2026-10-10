using BepInEx;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// The whole integration footprint of a consuming mod:
    ///   1. a project reference to CardShopCoop.Api (copy-local off),
    ///   2. a SOFT BepInDependency on CardShopCoop,
    ///   3. [ServerBehaviour]/[ClientBehaviour]/[NetworkMessage]/[MessageHandler] attributes.
    ///
    /// There is no registration call, and no guard: CardShopCoopCommunity discovers this plugin through the
    /// BepInEx dependency graph. When CardShopCoopCommunity is not installed this plugin still loads and
    /// simply does nothing.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("dev.meepen.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SamplePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.example.cardshopcoopcommunity.sample";
        public const string Name = "CardShopCoopCommunity Sample Counter";
        public const string Version = "1.0.0";
    }
}
