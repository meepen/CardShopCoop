using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallBoxNetworkInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(ItemBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxCreatedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ItemBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(FurnitureBoxDestroyedPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(RestockUpdatePatch)).Patch();
        }

        [MessageHandler(typeof(BoxCreatedMessage))]
        private void HandleBoxCreated(MessageContext context, BoxCreatedMessage message)
        {
            WorldPrediction.ApplyAuthoritative(message,
                () => _boxNetworkInteraction.ClientApplyCreated(message));
        }

        [MessageHandler(typeof(BoxDestroyedMessage))]
        private void HandleBoxDestroyed(MessageContext context, BoxDestroyedMessage message)
        {
            // A destroy echo names exactly the box the guest asked to destroy (a rejected request
            // comes back as a prediction rollback), so it confirms the prediction; reconciling
            // would first respawn the optimistic descriptor for no reason.
            WorldPrediction.ApplyConfirmed(message,
                () => _boxNetworkInteraction.ClientApplyDestroyed(message));
        }

        [MessageHandler(typeof(BoxStateMessage))]
        private void HandleBoxState(MessageContext context, BoxStateMessage message)
        {
            _boxNetworkInteraction?.ClientApplyBoxState(message);
        }

        [MessageHandler(typeof(BoxBaselineCompleteMessage))]
        private void HandleBoxBaselineComplete(MessageContext context, BoxBaselineCompleteMessage message)
        {
            _boxNetworkInteraction.ClientCompleteBaseline();
        }

        [MessageHandler(typeof(WorldBaselineCompleteMessage))]
        private void HandleWorldBaselineComplete(MessageContext context,
            WorldBaselineCompleteMessage message)
        {
            CompleteWorldBaseline(message.BaselineId);
        }

        [MessageHandler(typeof(WorldBaselineStartMessage))]
        private void HandleWorldBaselineStart(MessageContext context, WorldBaselineStartMessage message)
        {
            BeginWorldBaseline(message.BaselineId);
        }

        private void RejectCreatedBox(InteractablePackagingBox box)
        {
            _boxNetworkInteraction?.RejectClientCreated(box);
        }

        private bool NotifyDestroyedBox(InteractablePackagingBox box)
        {
            return _boxNetworkInteraction == null || _boxNetworkInteraction.ClientNotifyDestroyed(box);
        }

        [HarmonyPatch(typeof(RestockManager), "Update")]
        private static class RestockUpdatePatch
        {
            [HarmonyPrefix]
            private static void Prefix(RestockManager __instance)
            {
                _instance?._boxNetworkInteraction?.SuppressOutOfBoundsSweep(__instance);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxItem")]
        private static class ItemBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxCard")]
        private static class CardBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxShelf")]
        private static class FurnitureBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __result)
            {
                _instance?.RejectCreatedBox(__result);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnDestroyed")]
        private static class ItemBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox_Item __instance)
                => _instance == null || _instance.NotifyDestroyedBox(__instance);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Card), "OnDestroyed")]
        private static class CardBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox_Card __instance)
                => _instance == null || _instance.NotifyDestroyedBox(__instance);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnDestroyed")]
        private static class FurnitureBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox_Shelf __instance)
                => _instance == null || _instance.NotifyDestroyedBox(__instance);
        }
    }
}
