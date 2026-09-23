using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Guest-side placement hold mirror: renders a peer's held object with the vanilla
    /// move preview and releases the local hold when another player wins the object.</summary>
    public sealed partial class WorldClientBehaviour
    {
        private PlacementHoldInteraction _placementHold;

        internal void InstallPlacementHold()
        {
            _placementHold = new PlacementHoldInteraction(false, _ => { }, _context.Send);
            _harmony.CreateClassProcessor(typeof(PlacementHoldBoxUpPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlacementHoldRotatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlacementHoldFlipPatch)).Patch();
        }

        internal void ShutdownPlacementHold()
        {
            _placementHold?.Reset();
            _placementHold = null;
        }

        internal void TickPlacementHold() => _placementHold?.Tick();

        [MessageHandler(typeof(PlacementHoldBeginMessage))]
        private void HandlePlacementHoldBegin(MessageContext _, PlacementHoldBeginMessage message)
            => _placementHold?.ApplyBegin(message);

        [MessageHandler(typeof(PlacementHoldEndMessage))]
        private void HandlePlacementHoldEnd(MessageContext _, PlacementHoldEndMessage message)
            => _placementHold?.ApplyEnd(message.HoldKey);

        [MessageHandler(typeof(PlacementHoldRotationMessage))]
        private void HandlePlacementHoldRotation(MessageContext _, PlacementHoldRotationMessage message)
            => _placementHold?.ApplyRotation(message);

        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class PlacementHoldBoxUpPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalEnded(__instance);
        }

        [HarmonyPatch(typeof(InteractableObject), "AddObjectRotation")]
        private static class PlacementHoldRotatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalRotated(__instance);
        }

        [HarmonyPatch(typeof(InteractableObject), "Flip")]
        private static class PlacementHoldFlipPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalRotated(__instance);
        }
    }
}
