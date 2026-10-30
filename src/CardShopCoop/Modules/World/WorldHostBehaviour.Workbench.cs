using System;
using CardShopCoop.Api;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private WorldWorkbenchInteraction _workbenchInteraction;

        private void InstallWorkbench()
        {
            _workbenchInteraction = new WorldWorkbenchInteraction(true, Context.Broadcast,
                (peer, message) => peer.Send(message), () => Context.InGame);
            Harmony.CreateClassProcessor(typeof(HostWorkbenchAddItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchRemoveItemPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchTakeItemToHandPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchDispenseFromBoxPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchRemoveFromShelfPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchBundleStartPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(HostWorkbenchTaskCompletedPatch)).Patch();
        }

        private void ShutdownWorkbench()
        {
            _workbenchInteraction?.Reset();
            _workbenchInteraction = null;
        }

        private void AppendWorkbenchBaseline(Action<INetMessage> send)
            => _workbenchInteraction?.AppendBaselineMessages(send);

        private void PublishWorkbenchStorage(InteractableWorkbench bench)
        {
            if (WorldWorkbenchInteraction.ApplyingRemote)
            {
                return;
            }

            _workbenchInteraction?.HostStorageChanged(bench);
        }

        [MessageHandler]
        private ActionOutcome HandleWorkbenchOp(CoopMessageContext context, WorkbenchOpMessage message)
        {
            if (message?.Bench == null)
            {
                CoopPlugin.Log.LogWarning("[workbench] dropped op with no live bench object.");
                return ActionOutcome.Reject("the workbench did not resolve to a live object");
            }

            if (_workbenchInteraction == null || !Context.InGame || !context.IsAuthenticated)
            {
                return ActionOutcome.Reject("the session is not ready for world actions",
                    _workbenchInteraction?.BuildResponse(message.Bench));
            }

            var outcome = _workbenchInteraction.HostApplyOp(message);
            if (outcome.Disposition == ActionDisposition.Applied)
            {
                PublishWorkbenchState(context.Peer, message.Bench);
            }

            return outcome;
        }

        [MessageHandler]
        private ActionOutcome HandleWorkbenchBundle(CoopMessageContext context,
            WorkbenchBundleMessage message)
        {
            if (message?.Bench == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[workbench] dropped bundle completion with no live bench object.");
                return ActionOutcome.Reject("the workbench did not resolve to a live object");
            }

            if (_workbenchInteraction == null || !Context.InGame || !context.IsAuthenticated)
            {
                return ActionOutcome.Reject("the session is not ready for world actions",
                    _workbenchInteraction?.BuildResponse(message.Bench));
            }

            var outcome = _workbenchInteraction.HostCompleteBundle(message);
            if (outcome.Disposition == ActionDisposition.Applied)
            {
                PublishWorkbenchState(context.Peer, message.Bench);
            }

            return outcome;
        }

        /// <summary>Publishes the host's post-action bench state to every peer except the acting
        /// client, whose own answer carries the same state in its action response.</summary>
        private void PublishWorkbenchState(ICoopPeer actor, InteractableWorkbench bench)
        {
            var state = _workbenchInteraction?.BuildState(bench);
            if (state == null)
            {
                return;
            }

            if (actor != null)
            {
                Context.BroadcastExcept(actor, state);
            }
            else
            {
                Context.Broadcast(state);
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
                // The bench change itself rides the AddItem postfix (DispenseItemFromBox feeds the
                // bench through AddItem); only the source box needs broadcasting here.
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
                // The bench change itself rides the TakeItemToHand postfix (RemoveItemFromShelf
                // moves the item through TakeItemToHand); only the destination box needs broadcasting.
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
