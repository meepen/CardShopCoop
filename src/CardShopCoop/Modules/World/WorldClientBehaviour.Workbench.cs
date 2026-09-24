using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private WorldWorkbenchInteraction _workbenchInteraction;

        private void InstallWorkbench()
        {
            _workbenchInteraction = new WorldWorkbenchInteraction(false, _ => { }, SendWorldCommand);
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchAddItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchRemoveItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchTakeItemToHandPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchDispenseFromBoxPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchRemoveFromShelfPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchBundleScopePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchBundleStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ClientWorkbenchTaskCompletedPatch)).Patch();
        }

        private void ShutdownWorkbench()
        {
            _workbenchInteraction?.Reset();
            _workbenchInteraction = null;
        }

        private void PublishWorkbenchStorage(InteractableWorkbench bench)
        {
            if (WorldWorkbenchInteraction.ApplyingRemote)
            {
                return;
            }

            _workbenchInteraction?.ClientStorageChanged(bench);
        }

        [MessageHandler(typeof(WorkbenchStateMessage))]
        private void HandleWorkbenchState(MessageContext context, WorkbenchStateMessage message)
        {
            _workbenchInteraction?.ClientApplyState(message);
        }

        [MessageHandler(typeof(WorkbenchGrantMessage))]
        private void HandleWorkbenchGrant(MessageContext context, WorkbenchGrantMessage message)
        {
            _workbenchInteraction?.ClientApplyGrant(message);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "AddItem")]
        private static class ClientWorkbenchAddItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "RemoveItem")]
        private static class ClientWorkbenchRemoveItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "TakeItemToHand")]
        private static class ClientWorkbenchTakeItemToHandPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "DispenseItemFromBox")]
        private static class ClientWorkbenchDispenseFromBoxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item itemBox, out int __state)
                => __state = itemBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance,
                InteractablePackagingBox_Item itemBox, int __state)
            {
                _instance?.PublishWorkbenchStorage(__instance);
                if (!WorldWorkbenchInteraction.ApplyingRemote
                    && itemBox?.m_ItemCompartment != null
                    && itemBox.m_ItemCompartment.GetItemCount() != __state)
                {
                    _instance?.PublishItemBoxState(itemBox, true);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "RemoveItemFromShelf")]
        private static class ClientWorkbenchRemoveFromShelfPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item packageBox, out int __state)
                => __state = packageBox?.m_ItemCompartment?.GetItemCount() ?? 0;

            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance,
                InteractablePackagingBox_Item packageBox, int __state)
            {
                _instance?.PublishWorkbenchStorage(__instance);
                if (!WorldWorkbenchInteraction.ApplyingRemote
                    && packageBox?.m_ItemCompartment != null
                    && packageBox.m_ItemCompartment.GetItemCount() != __state)
                {
                    _instance?.PublishItemBoxState(packageBox, true);
                }
            }
        }

        /// <summary>A bundle runs its binder reductions inside <c>OpenBundleCardScreen</c>. Wrap
        /// the whole call in one card batch so the up-to-a-hundred deletions cross as a single
        /// atomic intent instead of a per-delta prediction fan-out.</summary>
        [HarmonyPatch(typeof(WorkbenchUIScreen), "OpenBundleCardScreen")]
        private static class ClientWorkbenchBundleScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix()
                => _instance?._cardInteraction?.BeginClientCardBatch();

            [HarmonyPostfix]
            private static void Postfix()
                => _instance?._cardInteraction?.CommitClientCardBatch();
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "PlayBundlingCardBoxSequence")]
        private static class ClientWorkbenchBundleStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?._workbenchInteraction?.ClientBundleStarted(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "OnTaskCompleted")]
        private static class ClientWorkbenchTaskCompletedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableWorkbench __instance)
            {
                var interaction = _instance?._workbenchInteraction;
                if (interaction == null)
                {
                    return true;
                }

                interaction.ClientCompleteBundle(__instance);
                return false;
            }
        }
    }
}
