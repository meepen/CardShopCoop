using CardShopCoop.Api;
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
        /// the reveal crosses as one atomic action per 200 cards instead of one delta per card -
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
        private void HandleMarketState(CoopMessageContext context, MarketStateMessage message)
        {
            _cardInteraction.Market.ClientApplyOrBuffer(message, Context.InGame);
        }

        /// <summary>Authoritative card state: the host's own card changes and every other player's
        /// accepted change. This machine's own prediction is settled by its action response, so a
        /// prediction is never applied twice here.</summary>
        [MessageHandler]
        private void HandleCardDelta(CoopMessageContext context, CardDeltaMessage message)
        {
            _cardInteraction.HandleCardDelta(context.Peer.Player.Id, message);
        }

        /// <summary>Authoritative bulk card state (a bundle, a trade movement, or one pack-opener
        /// reveal chunk). Applied as one unit, exactly as the client predicted it.</summary>
        [MessageHandler]
        private void HandleCardDeltaBatch(CoopMessageContext context, CardDeltaBatchMessage message)
        {
            _cardInteraction.HandleCardDeltaBatch(context.Peer.Player.Id, message);
        }

        /// <summary>Authoritative graded-album removal for another player's accepted binder pick.
        /// This machine's own graded pick settles through its action response instead.</summary>
        [MessageHandler]
        private void HandleGradedRemove(CoopMessageContext context, GradedRemoveMessage message)
        {
            _cardInteraction.HandleGradedRemove(context.Peer.Player.Id, message);
        }

        [MessageHandler]
        private void HandleContainerState(CoopMessageContext context, ContainerStateMessage message)
        {
            _cardInteraction.Containers.ClientApplyState(message);
        }

        /// <summary>The host's answer to an accepted pack-collect claim: the claim token and the
        /// cards that collect produced, which belong to the claimant alone. The client's optimistic
        /// collect only banked its own reveal, so this re-establishes the claim with the host's
        /// output.</summary>
        [MessageHandler]
        private void HandleContainerPackClaim(CoopMessageContext context,
            ContainerPackClaimMessage message)
        {
            _cardInteraction.Containers.ClientApplyPackClaim(message);
        }

        /// <summary>One container's authoritative record changed during normal play. A record that
        /// echoes this machine's own insert lands beside newer in-flight local inserts, so the
        /// container module owns the layering (see its <c>ClientApplyDelta</c>).</summary>
        [MessageHandler]
        private void HandleContainerDelta(CoopMessageContext context, ContainerDeltaMessage message)
        {
            _cardInteraction.Containers.ClientApplyDelta(message);
        }
    }
}
