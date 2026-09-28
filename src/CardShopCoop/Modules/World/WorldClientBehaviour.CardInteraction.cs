using GradedRemoveMessage = CardShopCoop.Modules.World.GradedRemoveMessage;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private void InstallCardInteractionPatches()
        {
            WorldCardInteractionPatches.Apply(_harmony);
            WorldContainerInteraction.ApplyClientPatches(_harmony);
            WorldMarketInteraction.ApplyPatches(_harmony);
            _harmony.CreateClassProcessor(typeof(CardObtainedBatchScopePatch)).Patch();
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

        [MessageHandler(typeof(MarketStateMessage))]
        private void HandleMarketState(MessageContext context, MarketStateMessage message)
        {
            _cardInteraction.Market.ClientApplyOrBuffer(message, _context.InGame());
        }

        [MessageHandler(typeof(CardDeltaMessage))]
        private void HandleCardDelta(MessageContext context, CardDeltaMessage message)
        {
            WorldPrediction.AckOrApply(message,
                () => _cardInteraction.HandleCardDelta(context.ConnectionId, message));
        }

        [MessageHandler(typeof(CardDeltaBatchMessage))]
        private void HandleCardDeltaBatch(MessageContext context, CardDeltaBatchMessage message)
        {
            WorldPrediction.AckOrApply(message,
                () => _cardInteraction.HandleCardDeltaBatch(context.ConnectionId, message));
        }

        [MessageHandler(typeof(GradedRemoveMessage))]
        private void HandleGradedRemove(MessageContext context, GradedRemoveMessage message)
        {
            WorldPrediction.AckOrApply(message,
                () => _cardInteraction.HandleGradedRemove(context.ConnectionId, message));
        }

        [MessageHandler(typeof(ContainerStateMessage))]
        private void HandleContainerState(MessageContext context, ContainerStateMessage message)
        {
            _cardInteraction.Containers.ClientApplyState(message);
        }

        [MessageHandler(typeof(ContainerPackClaimMessage))]
        private void HandleContainerPackClaim(MessageContext context,
            ContainerPackClaimMessage message)
        {
            // Host-only answer to an accepted claim intent; the client's optimistic apply only set
            // CollectClaimed, and the authoritative apply re-establishes it with the host output.
            WorldPrediction.Confirm(message,
                () => _cardInteraction.Containers.ClientApplyPackClaim(message));
        }

        [MessageHandler(typeof(ContainerDeltaMessage))]
        private void HandleContainerDelta(MessageContext context, ContainerDeltaMessage message)
        {
            // Accepted container ops are echoed as the host's full record state for that
            // compartment (rejections roll the prediction back), so the receiver's apply is a
            // state-set, not a second mutation. The host's record state supersedes the client's
            // optimistic run (which only set a claimed flag), so always apply it.
            WorldPrediction.Confirm(message,
                () => _cardInteraction.Containers.ClientApplyDelta(message));
        }

    }
}
