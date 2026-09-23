using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;

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

        [MessageHandler(typeof(CardDeltaRequestMessage))]
        private void HandleCardDelta(MessageContext context, CardDeltaRequestMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context))
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

        [MessageHandler(typeof(CardDeltaBatchRequestMessage))]
        private void HandleCardDeltaBatch(MessageContext context, CardDeltaBatchRequestMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, ()
                    => ApplyCardDeltaBatchCommand(context, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(GradedRemoveRequestMessage))]
        private void HandleGradedRemove(MessageContext context, GradedRemoveRequestMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, ()
                    => ApplyGradedRemoveCommand(context, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(ContainerOpMessage))]
        private void HandleContainerOp(MessageContext context, ContainerOpMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, ()
                    => ApplyContainerCommand(context, message));
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

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

        private bool ApplyContainerCommand(MessageContext context, ContainerOpMessage message)
        {
            if (_cardInteraction == null)
                return false;

            return _cardInteraction.Containers.HostApplyOp(message, context.ConnectionId,
                response => SendWorldTo(context.ConnectionId, response));
        }

    }
}
