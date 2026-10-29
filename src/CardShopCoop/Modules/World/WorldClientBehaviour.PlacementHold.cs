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
            Harmony.CreateClassProcessor(typeof(PlacementHoldBoxUpPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldRotatePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldFlipPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldInputPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldRemoteValidityPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlacementHoldGlobalPreviewPatch)).Patch();
        }

        internal void ShutdownPlacementHold()
        {
            _placementHold?.Reset();
            _placementHold = null;
        }

        internal void TickPlacementHold() => _placementHold?.Tick();

        [MessageHandler]
        private void HandlePlacementHoldBegin(MessageContext _, PlacementHoldBeginMessage message)
            => _placementHold?.ApplyBegin(message);

        [MessageHandler]
        private void HandlePlacementHoldEnd(MessageContext _, PlacementHoldEndMessage message)
        {
            if (message == null || message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped end with no live entity.");
                return;
            }

            _placementHold?.ApplyEnd(message.Entity);
        }

        [MessageHandler]
        private void HandlePlacementHoldRotation(MessageContext _, PlacementHoldRotationMessage message)
            => _placementHold?.ApplyRotation(message);

        [MessageHandler]
        private void HandlePlacementHoldState(MessageContext _, PlacementHoldStateMessage message)
            => _placementHold?.ApplyState(message);

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
                => _instance?._placementHold?.LocalFlipped(__instance);
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "Update")]
        private static class PlacementHoldInputPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
                => _instance?._placementHold?.LocalInputChanged();
        }

        /// <summary>A remote ghost's real object still runs the game's UpdateMoveState on this
        /// peer. That method writes the one global preview material color, so a remote invalid
        /// ghost would turn the local player's green placement preview red. Mark the remote
        /// update and ignore its global write.</summary>
        [HarmonyPatch(typeof(InteractableObject), "Update")]
        private static class PlacementHoldRemoteValidityPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, out bool __state)
                => __state = _instance?._placementHold?.BeginRemoteUpdate(__instance) == true;

            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.ApplyRemoteUpdate(__instance);

            [HarmonyFinalizer]
            private static void Finalizer(bool __state)
            {
                if (__state)
                {
                    _instance?._placementHold?.EndRemoteUpdate();
                }
            }
        }

        [HarmonyPatch(typeof(ShelfManager), "SetMoveObjectPreviewModelValidState")]
        private static class PlacementHoldGlobalPreviewPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
                => !PlacementHoldInteraction.SuppressGlobalPreview;
        }
    }
}
