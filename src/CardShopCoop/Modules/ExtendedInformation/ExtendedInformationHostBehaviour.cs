using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>Host message surface for extended information. The typed store and baseline
    /// generation live in <see cref="ExtendedInformationModule"/>.</summary>
    [ServerBehaviour]
    public sealed class ExtendedInformationHostBehaviour : CoopBehaviour
    {
        [MessageHandler]
        private void HandleIntent(MessageContext context, ExtendedInformationIntentMessage message)
            => ExtendedInformationModule.HostHandleIntent(message);

        [OnFullyJoined]
        private void SendBaseline(PeerConnection connection)
        {
            var peer = PeerHandle.ForConnection(connection);
            if (peer == null)
            {
                return;
            }

            peer.Send(ExtendedInformationModule.BuildBaseline());
        }
    }
}
