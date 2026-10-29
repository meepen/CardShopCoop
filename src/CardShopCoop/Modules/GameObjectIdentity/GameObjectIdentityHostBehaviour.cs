using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>Host side of the generic identity lifecycle.</summary>
    [ServerBehaviour]
    public sealed partial class GameObjectIdentityHostBehaviour : CoopBehaviour
    {
        internal static GameObjectIdentityHostBehaviour Active
        {
            get; private set;
        }


        private void OnEnable()
        {
            Active = this;
            GameObjectIdentityHostHooks.Install(Harmony);
        }

        private void OnDisable()
        {
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }

        }

        internal void Broadcast(INetMessage message) => RuntimeContext.Broadcast(message);
    }

    /// <summary>
    /// Per-kind host hooks. The implementations are in the GameObjectIdentityHostBehaviour.*
    /// partial files; the feature modules no longer install any lifecycle patch.
    /// </summary>
    internal static partial class GameObjectIdentityHostHooks
    {
        internal static void Install(Harmony harmony)
        {
            InstallNpc(harmony);
            InstallPlacement(harmony);
            InstallBox(harmony);
            InstallDecoration(harmony);
        }

        private static partial void InstallNpc(Harmony harmony);

        private static partial void InstallPlacement(Harmony harmony);

        private static partial void InstallBox(Harmony harmony);

        private static partial void InstallDecoration(Harmony harmony);
    }
}
