using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        /// <summary>Reason for a card or container command that arrives before the host can apply
        /// it. The sender still gets an answer, so its prediction is reversed instead of left
        /// waiting.</summary>
        private const string CardSessionNotReady = "the session is not ready for card actions";

        private void InstallCardInteractionPatches()
        {
            WorldCardInteractionPatches.Apply(Harmony);
            WorldContainerInteraction.ApplyHostPatches(Harmony);
            WorldMarketInteraction.ApplyPatches(Harmony);
        }

        /// <summary>One player left: return the shared cards their accepted removals still own, and
        /// drop the container holds and pack claims addressed to them.</summary>
        [OnClientDisconnected]
        private void ReleaseCardClaims(ICoopPeer peer, DisconnectInfo _)
        {
            var player = peer?.Player;
            if (player == null)
            {
                return;
            }

            _cardInteraction?.Containers.HostReleaseConn(player.Id);
            _cardInteraction?.ForgetGradingOwnership(player.Id);
        }

        // ---------------- card commands ----------------

        /// <summary>One client's card change: a collection-binder pick (the shared collection goes
        /// down and a physical card enters that player's hand), a deposit, or a card a pack opener
        /// produced. The host applies it to the shared collection itself and answers the sender;
        /// every other peer receives the resulting delta as ordinary card state.</summary>
        [MessageHandler]
        private ActionOutcome HandleCardDelta(CoopMessageContext context,
            CardDeltaRequestMessage message)
        {
            if (!CanApplyCardCommand(context, nameof(HandleCardDelta)))
            {
                return ActionOutcome.Reject(CardSessionNotReady);
            }

            if (message?.Card == null)
            {
                CoopPlugin.Log.LogWarning("[cards] dropped a card action: the card did not resolve "
                    + "to live card data.");
                return ActionOutcome.Reject("the card did not resolve to live card data");
            }

            return _cardInteraction.HandleCardDelta(context, message);
        }

        /// <summary>One client's bulk card movement (a bundle, a trade hand-off, or one capped
        /// pack-opener reveal chunk). The batch is atomic on the host: a refusal retracts whatever
        /// part of it was already applied, so the sender always rolls back to the same state.</summary>
        [MessageHandler]
        private ActionOutcome HandleCardDeltaBatch(CoopMessageContext context,
            CardDeltaBatchRequestMessage message)
        {
            if (!CanApplyCardCommand(context, nameof(HandleCardDeltaBatch)))
            {
                return ActionOutcome.Reject(CardSessionNotReady);
            }

            if (message?.Deltas == null || message.Deltas.Count == 0)
            {
                CoopPlugin.Log.LogWarning("[cards] dropped a card batch: it carries no deltas.");
                return ActionOutcome.Reject("the card batch carries no deltas");
            }

            return _cardInteraction.HandleCardDeltaBatch(context, message);
        }

        /// <summary>One client's graded-album removal: taking a slab out of the graded binder. The
        /// host applies it to the shared graded album and answers the sender; the removal of the
        /// certificate reaches the other peers as authoritative state.</summary>
        [MessageHandler]
        private ActionOutcome HandleGradedRemove(CoopMessageContext context,
            GradedRemoveRequestMessage message)
        {
            if (!CanApplyCardCommand(context, nameof(HandleGradedRemove)))
            {
                return ActionOutcome.Reject(CardSessionNotReady);
            }

            if (message?.Card == null)
            {
                CoopPlugin.Log.LogWarning("[cards] dropped a graded removal: the card did not "
                    + "resolve to live card data.");
                return ActionOutcome.Reject("the card did not resolve to live card data");
            }

            return _cardInteraction.HandleGradedRemove(context, message);
        }

        /// <summary>The authenticated sender's stable player identity. The card interaction keys
        /// the grading claims a player's accepted removals still own under it (player 0 is the
        /// host). Derived from the peer, never from a connection id.</summary>
        private static int ActorId(CoopMessageContext context) => context.Peer.Player.Id;

        /// <summary>The shared gate for every card and container command. Authentication is the
        /// framework's job; this only refuses a command that arrives before the world is playable,
        /// or while the card sync module is stopped.</summary>
        private bool CanApplyCardCommand(CoopMessageContext context, string what)
        {
            if (_cardInteraction != null && Context.InGame && context?.IsAuthenticated == true)
            {
                return true;
            }

            CoopPlugin.Log.LogWarning("[cards] refused " + what + ": cardSync="
                + (_cardInteraction != null) + " inGame=" + Context.InGame + " peer="
                + (context?.Peer == null ? "none" : context.Peer.State.ToString()) + ".");
            return false;
        }

        // ---------------- container commands ----------------

        [IntentValidator]
        private IntentValidation ValidateEmptyBoxTake(CoopMessageContext context,
            EmptyBoxTakeIntent message)
            => ValidateEmptyBoxIntent(context, message?.Container, take: true);

        [IntentValidator]
        private IntentValidation ValidateEmptyBoxStore(CoopMessageContext context,
            EmptyBoxStoreIntent message)
            => ValidateEmptyBoxIntent(context, message?.Container, take: false);

        private IntentValidation ValidateEmptyBoxIntent(CoopMessageContext context,
            InteractableObject container, bool take)
        {
            if (!Context.InGame || context?.Peer == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            if (!context.IsAuthenticated)
            {
                return IntentValidation.Deny("player is not authenticated");
            }

            if (container is not InteractableEmptyBoxStorage storage)
            {
                return IntentValidation.Deny("container is not empty-box storage");
            }

            if (take && storage.GetBoxStoredCount() <= 0)
            {
                return IntentValidation.Deny("empty-box storage is empty");
            }

            return IntentValidation.Allow();
        }

        [MessageHandler]
        private ActionOutcome HandleEmptyBoxTake(CoopMessageContext context,
            EmptyBoxTakeIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandleEmptyBoxStore(CoopMessageContext context,
            EmptyBoxStoreIntent message)
            => ApplyContainerIntent(context, message);

        private IntentValidation ValidateContainerCommand(CoopMessageContext context,
            InteractableObject container)
        {
            if (!Context.InGame || context?.Peer == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            if (!context.IsAuthenticated)
            {
                return IntentValidation.Deny("player is not authenticated");
            }

            return container == null
                ? IntentValidation.Deny("container is not live")
                : IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, ContainerContentSetIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, PackInsertIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, PackTurnOnIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, PackCollectIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, PackClaimIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, CleanserToggleIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, CleanserRefillIntent m)
            => ValidateContainerCommand(context, m?.Container);

        [IntentValidator]
        private IntentValidation Validate(CoopMessageContext context, WorkerTakeFlagIntent m)
            => ValidateContainerCommand(context, m?.Container);

        /// <summary>Applies one container command on the host. The container module validates the
        /// operation against its own live record, publishes the resulting record to the other
        /// peers, and returns the acting client's own hand flags in the action response.</summary>
        private ActionOutcome ApplyContainerIntent(CoopMessageContext context, ContainerIntent intent)
        {
            if (intent == null)
            {
                CoopPlugin.Log.LogWarning("[containers] dropped a container action: it carries no "
                    + "command.");
                return ActionOutcome.Reject("the container action carries no command");
            }

            if (!CanApplyCardCommand(context, intent.GetType().Name))
            {
                return ActionOutcome.Reject(CardSessionNotReady);
            }

            if (intent.Container == null)
            {
                CoopPlugin.Log.LogWarning("[containers] dropped "
                    + intent.GetType().Name + ": the container did not resolve to a live object.");
                return ActionOutcome.Reject("the container did not resolve to a live object");
            }

            return _cardInteraction.Containers.HostApplyIntent(intent, context);
        }

        [MessageHandler]
        private ActionOutcome HandleContentSet(CoopMessageContext context,
            ContainerContentSetIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandlePackInsert(CoopMessageContext context, PackInsertIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandlePackTurnOn(CoopMessageContext context, PackTurnOnIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandlePackCollect(CoopMessageContext context, PackCollectIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandlePackClaim(CoopMessageContext context, PackClaimIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandleCleanserToggle(CoopMessageContext context,
            CleanserToggleIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandleCleanserRefill(CoopMessageContext context,
            CleanserRefillIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private ActionOutcome HandleWorkerTakeFlag(CoopMessageContext context,
            WorkerTakeFlagIntent message)
            => ApplyContainerIntent(context, message);
    }
}
