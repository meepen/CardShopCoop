using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallCardInteractionPatches()
        {
            WorldCardInteractionPatches.Apply(Harmony);
            WorldContainerInteraction.ApplyClientPatches(Harmony);
            WorldMarketInteraction.ApplyPatches(Harmony);
            Harmony.CreateClassProcessor(typeof(CardObtainedBatchScopePatch)).Patch();
        }

        /// <summary>A pack-opener collect reveals its whole output in one synchronous call
        /// (<c>ShowCardObtained</c> adds every obtained card). Wrap it in one client card batch so
        /// the reveal crosses as one atomic intent per 200 cards instead of one delta per card -
        /// the same pattern the workbench bundle uses. The game still adds every card locally; the
        /// batch is the post-hoc record of those additions.</summary>
        [HarmonyPatch(typeof(ShowCardObtainedPage), "ShowCardObtained")]
        private static class CardObtainedBatchScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix()
                => _instance?._cardInteraction?.BeginClientCardBatch();

            [HarmonyFinalizer]
            private static void Finalizer()
                => _instance?._cardInteraction?.CommitClientCardBatch();
        }

        [MessageHandler]
        private void HandleMarketState(MessageContext context, MarketStateMessage message)
        {
            _cardInteraction.Market.ClientApplyOrBuffer(message, _context.InGame());
        }

        [MessageHandler]
        private void HandleCardDelta(MessageContext context, CardDeltaMessage message)
        {
            WorldAction.AckOrApply(message,
                () => _cardInteraction.HandleCardDelta(context.ConnectionId, message));
        }

        [MessageHandler]
        private void HandleCardDeltaBatch(MessageContext context, CardDeltaBatchMessage message)
        {
            WorldAction.AckOrApply(message,
                () => _cardInteraction.HandleCardDeltaBatch(context.ConnectionId, message));
        }

        [MessageHandler]
        private void HandleGradedRemove(MessageContext context, GradedRemoveMessage message)
        {
            WorldAction.AckOrApply(message,
                () => _cardInteraction.HandleGradedRemove(context.ConnectionId, message));
        }

        [MessageHandler]
        private void HandleContainerState(MessageContext context, ContainerStateMessage message)
        {
            _cardInteraction.Containers.ClientApplyState(message);
        }

        [MessageHandler]
        private void HandleContainerPackClaim(MessageContext context,
            ContainerPackClaimMessage message)
        {
            // Host-only answer to an accepted claim intent; the client's optimistic apply only set
            // CollectClaimed, and the authoritative apply re-establishes it with the host output.
            WorldAction.Confirm(message,
                () => _cardInteraction.Containers.ClientApplyPackClaim(message));
        }

        [MessageHandler]
        private void HandleContainerDelta(MessageContext context, ContainerDeltaMessage message)
        {
            // The container module owns this delta's prediction lifecycle: a pack-insert echo
            // reconciles in layers against this machine's newer in-flight inserts before it is
            // retired, while every other record retires-and-applies (see ClientApplyDelta).
            _cardInteraction.Containers.ClientApplyDelta(message);
        }

    }
}
