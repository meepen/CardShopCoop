using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>Client message surface for host-authoritative extended information.</summary>
    [ClientBehaviour]
    public sealed class ExtendedInformationClientBehaviour : CoopBehaviour
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
        private void HandleSet(MessageContext context, ExtendedInformationSetMessage message)
            => ExtendedInformationModule.ClientHandleSet(message);

        [MessageHandler]
        private void HandleRemoved(MessageContext context, ExtendedInformationRemovedMessage message)
            => ExtendedInformationModule.ClientHandleRemoved(message);

        [MessageHandler]
        private void HandleBaseline(MessageContext context,
            ExtendedInformationBaselineMessage message)
            => ExtendedInformationModule.ClientApplyBaseline(message);
    }
}
