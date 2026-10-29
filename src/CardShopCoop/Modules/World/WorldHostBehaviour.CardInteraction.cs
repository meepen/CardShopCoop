using CardShopCoop.Attributes;
using CardShopCoop.Api;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private void InstallCardInteractionPatches()
        {
            WorldCardInteractionPatches.Apply(_harmony);
            WorldContainerInteraction.ApplyHostPatches(_harmony);
            WorldMarketInteraction.ApplyPatches(_harmony);
        }

        [OnClientDisconnected]
        private void ReleaseCardClaims(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _cardInteraction?.Containers.HostReleaseConn(connection.Id);
                _cardInteraction?.ForgetGradingOwnership(connection.Id);
            }
        }

        [MessageHandler]
        private void HandleCardDelta(MessageContext context, CardDeltaRequestMessage message)
        {
            if (RuntimeContext.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                    _cardInteraction != null
                        && _cardInteraction.HandleCardDelta(context.ConnectionId, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler]
        private void HandleCardDeltaBatch(MessageContext context, CardDeltaBatchRequestMessage message)
        {
            if (RuntimeContext.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, ()
                    => ApplyCardDeltaBatchCommand(context, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler]
        private void HandleGradedRemove(MessageContext context, GradedRemoveRequestMessage message)
        {
            if (RuntimeContext.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, ()
                    => ApplyGradedRemoveCommand(context, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }
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
            if (!RuntimeContext.InGame() || context?.Connection == null)
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
        private void HandleEmptyBoxTake(MessageContext context, EmptyBoxTakeIntent message)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context) || message?.Container == null)
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () => ApplyContainerCommand(context, message));
        }

        [MessageHandler]
        private void HandleEmptyBoxStore(MessageContext context, EmptyBoxStoreIntent message)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context) || message?.Container == null)
            {
                RejectWorldIntent(context, message);
                return;
            }

            ExecuteWorldCommand(context, message, () => ApplyContainerCommand(context, message));
        }

        private IntentValidation ValidateContainerCommand(CoopMessageContext context,
            InteractableObject container)
        {
            if (!RuntimeContext.InGame() || context?.Connection == null)
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

        private void ApplyContainerIntent(MessageContext context, ContainerIntent intent)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context) || intent?.Container == null)
            {
                RejectWorldIntent(context, intent);
                return;
            }

            ExecuteWorldCommand(context, intent, () => ApplyContainerCommand(context, intent));
        }

        [MessageHandler]
        private void HandleContentSet(MessageContext context, ContainerContentSetIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandlePackInsert(MessageContext context, PackInsertIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandlePackTurnOn(MessageContext context, PackTurnOnIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandlePackCollect(MessageContext context, PackCollectIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandlePackClaim(MessageContext context, PackClaimIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandleCleanserToggle(MessageContext context, CleanserToggleIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandleCleanserRefill(MessageContext context, CleanserRefillIntent message)
            => ApplyContainerIntent(context, message);

        [MessageHandler]
        private void HandleWorkerTakeFlag(MessageContext context, WorkerTakeFlagIntent message)
            => ApplyContainerIntent(context, message);

        private bool ApplyCardDeltaBatchCommand(MessageContext context,
            CardDeltaBatchRequestMessage message)
        {
            return _cardInteraction != null
                && _cardInteraction.HandleCardDeltaBatch(context.ConnectionId, message);
        }

        private bool ApplyGradedRemoveCommand(MessageContext context,
            GradedRemoveRequestMessage message)
        {
            return _cardInteraction != null
                && _cardInteraction.HandleGradedRemove(context.ConnectionId, message);
        }

        private bool ApplyContainerCommand(MessageContext context, ContainerIntent message)
        {
            if (_cardInteraction == null)
            {

                return false;
            }


            return _cardInteraction.Containers.HostApplyIntent(message, context.ConnectionId,
                response => SendWorldTo(context.ConnectionId, response));
        }

    }
}
