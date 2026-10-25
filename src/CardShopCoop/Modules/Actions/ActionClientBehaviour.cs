using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Actions
{
    [ClientBehaviour]
    public sealed class ActionClientBehaviour : CoopBehaviour
    {
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown)
            {
                return;
            }


            ActionLedger.Start();
            RuntimeContext.Messages.RegisterAttributedHandlers(this);
        }

        [MessageHandler]
        private void Rollback(MessageContext _, ActionRollbackMessage message)
            => ActionLedger.Rollback(message.ActionId);

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            ActionLedger.Stop();
        }

        private void OnDestroy() => Shutdown();
    }
}
