using BepInEx;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// The whole integration footprint of a consuming mod:
    ///   1. a project reference to CardShopCoop.Api (copy-local off),
    ///   2. a SOFT BepInDependency on CardShopCoop,
    ///   3. [ServerBehaviour]/[ClientBehaviour]/[NetworkMessage]/[MessageHandler] attributes.
    ///
    /// There is no registration call, and no guard: CardShopCoop discovers this plugin through the
    /// BepInEx dependency graph. When CardShopCoop is not installed this plugin still loads and
    /// simply does nothing.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.zwhit.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SamplePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.example.cardshopcoop.sample";
        public const string Name = "CardShopCoop Sample Counter";
        public const string Version = "1.0.0";
    }
}
