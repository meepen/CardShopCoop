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
        private CoopRuntimeContext _context;

        private void OnEnable()
        {
            _context = RuntimeContext;
            _context.Messages.RegisterAttributedHandlers(this);
        }

        private void OnDisable()
        {
            _context?.Messages.UnregisterAttributedHandlers(this);
            _context = null;
        }

        [MessageHandler]
        private void HandleIntent(MessageContext context, ExtendedInformationIntentMessage message)
            => ExtendedInformationModule.HostHandleIntent(message);

        [OnFullyJoined]
        private void SendBaseline(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            _context.Send(connection.Id, ExtendedInformationModule.BuildBaseline());
        }
    }
}
