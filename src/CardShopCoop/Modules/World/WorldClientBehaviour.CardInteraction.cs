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
        }

        [MessageHandler(typeof(MarketStateMessage))]
        private void HandleMarketState(MessageContext context, MarketStateMessage message)
        {
            _cardInteraction.Market.ClientApplyOrBuffer(message, _context.InGame());
        }

        [MessageHandler(typeof(CardDeltaMessage))]
        private void HandleCardDelta(MessageContext context, CardDeltaMessage message)
        {
            WorldPrediction.ApplyAuthoritative(message,
                () => _cardInteraction.HandleCardDelta(context.ConnectionId, message));
        }

        [MessageHandler(typeof(CardDeltaBatchMessage))]
        private void HandleCardDeltaBatch(MessageContext context, CardDeltaBatchMessage message)
        {
            WorldPrediction.ApplyAuthoritative(message,
                () => _cardInteraction.HandleCardDeltaBatch(context.ConnectionId, message));
        }

        [MessageHandler(typeof(GradedRemoveMessage))]
        private void HandleGradedRemove(MessageContext context, GradedRemoveMessage message)
        {
            WorldPrediction.ApplyAuthoritative(message,
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
            WorldPrediction.ApplyConfirmed(message,
                () => _cardInteraction.Containers.ClientApplyPackClaim(message));
        }

        [MessageHandler(typeof(ContainerDeltaMessage))]
        private void HandleContainerDelta(MessageContext context, ContainerDeltaMessage message)
        {
            // Accepted container ops are echoed as the host's full record state for that
            // compartment (rejections roll the prediction back), so the receiver's apply is a
            // state-set, not a second mutation.
            WorldPrediction.ApplyConfirmed(message,
                () => _cardInteraction.Containers.ClientApplyDelta(message));
        }

    }
}
