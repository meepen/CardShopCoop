using System.Collections.Generic;
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

        private void PublishWorkbenchStorage(InteractableWorkbench bench, List<EItemType> before)
        {
            if (WorldWorkbenchInteraction.ApplyingRemote)
            {
                return;
            }

            _workbenchInteraction?.ClientStorageChanged(bench, before);
        }

        [MessageHandler(typeof(WorkbenchStateMessage))]
        private void HandleWorkbenchState(MessageContext context, WorkbenchStateMessage message)
        {
            // The message carries the host's ABSOLUTE stored list. Reconcile it in layers so a
            // concurrent host edit is folded (never drift) while an overlapping newer local edit on
            // this key is undone and replayed rather than transiently despawned by our echo of this
            // list; an empty/unknown id still just applies.
            WorldPrediction.ApplyAuthoritative(message,
                () => _workbenchInteraction?.ClientApplyState(message));
        }

        /// <summary>Capture-only prefix: snapshot the bench's live stored list before the game's
        /// own mutation, so the postfix can forward the exact delta without a shadow cache.</summary>
        [HarmonyPatch(typeof(InteractableWorkbench), "AddItem")]
        private static class ClientWorkbenchAddItemPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableWorkbench __instance, out List<EItemType> __state)
                => __state = _instance == null
                    ? null : WorldWorkbenchInteraction.TypesOf(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance, List<EItemType> __state)
                => _instance?.PublishWorkbenchStorage(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "RemoveItem")]
        private static class ClientWorkbenchRemoveItemPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableWorkbench __instance, out List<EItemType> __state)
                => __state = _instance == null
                    ? null : WorldWorkbenchInteraction.TypesOf(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance, List<EItemType> __state)
                => _instance?.PublishWorkbenchStorage(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractableWorkbench), "TakeItemToHand")]
        private static class ClientWorkbenchTakeItemToHandPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableWorkbench __instance, out List<EItemType> __state)
                => __state = _instance == null
                    ? null : WorldWorkbenchInteraction.TypesOf(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance, List<EItemType> __state)
                => _instance?.PublishWorkbenchStorage(__instance, __state);
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
                // The bench change itself rides the AddItem postfix (DispenseItemFromBox feeds the
                // bench through AddItem); only the source box needs publishing here.
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
                // The bench change itself rides the TakeItemToHand postfix (RemoveItemFromShelf
                // moves the item through TakeItemToHand); only the destination box needs publishing.
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
            /// <summary>Observes the local player's bundle completion. Vanilla <c>OnTaskCompleted</c>
            /// runs and mints the bundle box into the acting hand; the postfix only forwards the
            /// finished intent so the host clears its mirror of the bench. The box stays local:
            /// the host does not grant a second one back.</summary>
            [HarmonyPostfix]
            private static void Postfix(InteractableWorkbench __instance)
                => _instance?._workbenchInteraction?.ClientCompleteBundle(__instance);
        }
    }
}
