using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private WorldWorkbenchInteraction _workbenchInteraction;

        private void InstallWorkbench()
        {
            _workbenchInteraction = new WorldWorkbenchInteraction(true, BroadcastWorld, SendWorldTo,
                () => _context.InGame());
            _harmony.CreateClassProcessor(typeof(HostWorkbenchAddItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchRemoveItemPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchTakeItemToHandPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchDispenseFromBoxPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchRemoveFromShelfPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchBundleStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(HostWorkbenchTaskCompletedPatch)).Patch();
        }

        private void ShutdownWorkbench()
        {
            _workbenchInteraction?.Reset();
            _workbenchInteraction = null;
        }

        private void AppendWorkbenchBaseline(int connectionId)
        {
            _workbenchInteraction?.AppendBaselineMessages(
                message => SendWorldTo(connectionId, message));
        }

        private void PublishWorkbenchStorage(InteractableWorkbench bench)
        {
            if (WorldWorkbenchInteraction.ApplyingRemote)
            {
                return;
            }

            _workbenchInteraction?.HostStorageChanged(bench);
        }

        [MessageHandler(typeof(WorkbenchStateRequestMessage))]
        private void HandleWorkbenchStateRequest(MessageContext context,
            WorkbenchStateRequestMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context) && message != null)
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    _workbenchInteraction?.HostApplyStateRequest(context.ConnectionId, message);
                    return true;
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(WorkbenchBundleMessage))]
        private void HandleWorkbenchBundle(MessageContext context, WorkbenchBundleMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context) && message != null)
            {
                ExecuteWorldCommand(context, message,
                    () => _workbenchInteraction != null
                        && _workbenchInteraction.HostCompleteBundle(context.ConnectionId,
                            message.Index));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [OnFullyJoined]
        private void ResendWorkbenchGrants(PeerConnection connection)
        {
            if (connection != null && _context.InGame())
            {
                _workbenchInteraction?.ResendGrants(connection.Id);
            }
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "AddItem")]
        private static class HostWorkbenchAddItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "RemoveItem")]
        private static class HostWorkbenchRemoveItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "TakeItemToHand")]
        private static class HostWorkbenchTakeItemToHandPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?.PublishWorkbenchStorage(__instance);
        }

        /// <summary>The host feeding a bench from an open box moves the pack with
        /// <c>m_ItemCompartment.RemoveItem</c>, a path none of the box-content patches observe.
        /// Republish both the bench and the source box after the move.</summary>
        [HarmonyPatch(typeof(InteractableWorkbench), "DispenseItemFromBox")]
        private static class HostWorkbenchDispenseFromBoxPatch
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
                    _instance?.BroadcastItemBoxState(itemBox);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "RemoveItemFromShelf")]
        private static class HostWorkbenchRemoveFromShelfPatch
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
                    _instance?.BroadcastItemBoxState(packageBox);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "PlayBundlingCardBoxSequence")]
        private static class HostWorkbenchBundleStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?._workbenchInteraction?.HostBundleStarted(__instance);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "OnTaskCompleted")]
        private static class HostWorkbenchTaskCompletedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?._workbenchInteraction?.HostBundleCompleted(__instance);
        }
    }
}
