using CardShopCoop.Net.Connection;

namespace CardShopCoop.Net.Protocol
{
    internal static class ProtocolAuthorization
    {
        public static ProtocolAuthorizationResult Authorize(MessageContext context,
            bool corePreAuthentication)
        {
            if (context == null)
            {
                return ProtocolAuthorizationResult.Deny("message context is null");
            }

            if (context.Connection == null)
            {
                return ProtocolAuthorizationResult.Deny("a peer connection is required");
            }

            if (context.Connection.IsDisconnectingOrDisconnected)
            {
                return ProtocolAuthorizationResult.Deny("connection is closing");
            }

            if (corePreAuthentication)
            {
                var phase = context.Connection.State;
                if (phase != ConnectionState.Handshaking
                    && phase != ConnectionState.Transferring
                    && phase != ConnectionState.FullyJoined)
                {
                    return ProtocolAuthorizationResult.Deny(
                        "control message is not valid in connection phase " + phase);
                }
            }
            else if (!context.IsAuthenticated)
            {
                return ProtocolAuthorizationResult.Deny("authenticated peer is required");
            }
            return ProtocolAuthorizationResult.Allow();
        }
    }
}
