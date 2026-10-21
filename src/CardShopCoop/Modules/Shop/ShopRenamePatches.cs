using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Role-neutral hooks into the game's shop-renaming surface. Exactly one session
    /// behaviour (host or client) installs these and supplies the endpoint; the endpoint decides
    /// whether the local player may open the screen and forwards local edits.</summary>
    internal interface IShopRenameEndpoint
    {
        bool TryOpenRenameUi(ShopRenamer renamer, bool showIntro);
        void RenameUiOpened();
        void RenameInputChanged(string text);
        void ConfirmBegin();
        void ConfirmEnd();
        void RenameUiClosed();
        void PresentationReady();
    }

    internal static class ShopRenamePatches
    {
        // Typed as a Unity object so a destroyed behaviour compares null through Unity's overload.
        private static CoopBehaviour _endpoint;

        private static IShopRenameEndpoint Endpoint
        {
            get
            {
                var behaviour = _endpoint;
                return behaviour == null ? null : behaviour as IShopRenameEndpoint;
            }
        }

        internal static void SetEndpoint(CoopBehaviour endpoint) => _endpoint = endpoint;

        internal static void ClearEndpoint(CoopBehaviour endpoint)
        {
            if (ReferenceEquals(_endpoint, endpoint))
            {
                _endpoint = null;
            }
        }

        internal static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(OpenPatch)).Patch();
            harmony.CreateClassProcessor(typeof(InputChangedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(InputUpdatedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ConfirmPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReadyPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ClosedPatch)).Patch();
        }

        /// <summary>Refuses to open the rename UI while another player owns the lock. The postfix
        /// only marks the local player as editing when the vanilla method actually ran, so a
        /// refused open can never claim the lock.</summary>
        [HarmonyPatch(typeof(ShopRenamer), nameof(ShopRenamer.ShowRenameShopScreen))]
        private static class OpenPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ShopRenamer __instance, bool showIntro, out bool __state)
            {
                var endpoint = Endpoint;
                __state = endpoint == null || endpoint.TryOpenRenameUi(__instance, showIntro);
                return __state;
            }

            [HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                if (__state)
                {
                    Endpoint?.RenameUiOpened();
                }
            }
        }

        [HarmonyPatch(typeof(ShopRenamer), nameof(ShopRenamer.OnInputChanged))]
        private static class InputChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(string text) => Endpoint?.RenameInputChanged(text);
        }

        [HarmonyPatch(typeof(ShopRenamer), nameof(ShopRenamer.OnInputTextUpdated))]
        private static class InputUpdatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(string text) => Endpoint?.RenameInputChanged(text);
        }

        [HarmonyPatch(typeof(ShopRenamer), nameof(ShopRenamer.OnPressConfirmShopName))]
        private static class ConfirmPatch
        {
            [HarmonyPrefix]
            private static void Prefix() => Endpoint?.ConfirmBegin();

            [HarmonyPostfix]
            private static void Postfix() => Endpoint?.ConfirmEnd();
        }

        [HarmonyPatch(typeof(ShopRenamer), "OnEnable")]
        private static class ReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => Endpoint?.PresentationReady();
        }

        [HarmonyPatch(typeof(ShopRenamer), "OnDisable")]
        private static class ClosedPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => Endpoint?.RenameUiClosed();
        }
    }
}
