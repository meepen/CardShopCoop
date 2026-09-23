using System;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Messages;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        [MessageHandler(typeof(FullyJoinedMessage))]
        private void HandleFullyJoinedMessage(MessageContext context, FullyJoinedMessage message)
        {
            if (Role != CoopRole.Host || context.Connection == null)
            {
                return;
            }

            // FullyJoined is the client's ordered acknowledgement that it consumed the
            // transferred world. It is valid only after the host entered Transferring.
            if (context.Connection.State != ConnectionState.Transferring)
            {
                CoopPlugin.Log.LogWarning("Ignoring late/duplicate FullyJoined from connection "
                    + context.Connection.Id + " in phase " + context.Connection.State);
                return;
            }
            if (!Modules.SaveTransfer.SaveTransferApi.TryConsumeHostTransferAcknowledgement(
                context.Connection))
            {
                CoopPlugin.Log.LogWarning("Ignoring premature FullyJoined from connection "
                    + context.Connection.Id + " before its world transfer was fully queued");
                return;
            }

            try
            {
                _runtime?.FullyJoined(context.Connection);
            }
            catch (Exception error)
            {
                // Feature callbacks are isolated from the control transition so one module
                // cannot strand an authenticated peer in the transfer phase.
                CoopPlugin.Log.LogError("Feature FullyJoined callbacks failed: " + error);
            }

            Send(context.ConnectionId, new FullyJoinedAckMessage());
            if (!context.Connection.TryTransition(ConnectionState.FullyJoined))
            {
                CoopPlugin.Log.LogWarning("Could not complete FullyJoined transition for connection "
                    + context.Connection.Id + " in phase " + context.Connection.State);
            }
        }

        [MessageHandler(typeof(FullyJoinedAckMessage))]
        private void HandleFullyJoinedAckMessage(MessageContext context, FullyJoinedAckMessage message)
        {
            if (Role != CoopRole.Client || context.Connection == null)
            {
                return;
            }

            if (context.Connection.State != ConnectionState.Transferring)
            {
                CoopPlugin.Log.LogWarning("Ignoring late/duplicate FullyJoinedAck from connection "
                    + context.Connection.Id + " in phase " + context.Connection.State);
                return;
            }
            if (!Modules.SaveTransfer.SaveTransferApi.TryConsumeClientTransferAcknowledgement(
                context.Connection))
            {
                CoopPlugin.Log.LogWarning("Ignoring premature FullyJoinedAck before the client "
                    + "finished loading the transferred world");
                return;
            }

            if (context.Connection.TryTransition(ConnectionState.FullyJoined))
            {
                try
                {
                    _runtime?.FullyJoined(context.Connection);
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("Feature FullyJoined callbacks failed: " + error);
                }
            }
        }

        [MessageHandler(typeof(DisconnectMessage))]
        private void HandleDisconnectMessage(MessageContext context, DisconnectMessage message)
        {
            if (context.Connection == null)
            {
                CoopPlugin.Log.LogWarning("Ignoring disconnect control without a connection");
                return;
            }

            // Phase is advisory peer data. Normalize only that field; retain the bounded remote
            // code/reason so malformed peers still explain the close.
            var phase = Enum.IsDefined(typeof(ConnectionState), message.Phase)
                ? (ConnectionState)message.Phase : ConnectionState.Disconnecting;
            var info = new DisconnectInfo(message.Reason, true, message.Code,
                message.Retryable, phase);

            // The built-in KCP transport records the reason and completes the disconnect before
            // dispatch, so this handler is the application-level fallback for a DTO delivered as
            // a normal message. The client surfaces the structured remote reason through the
            // normal shutdown path.
            if (Role == CoopRole.Client)
            {
                RememberDisconnect(info);
                Shutdown("remote: " + info.Reason, info);
            }
        }
    }
}
