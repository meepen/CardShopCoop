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
        }

        [MessageHandler]
        private void Rollback(MessageContext _, ActionRollbackMessage message)
        {
            if (message == null)
            {
                return;
            }
            if (message.Undo)
            {
                ActionLedger.Rollback(message.ActionId);
            }
            else
            {
                // The host performed the action; only the authoritative publication failed. The
                // optimistic local run is the correct state, so retire it without reversing it.
                ActionLedger.Ack(message.ActionId);
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            ActionLedger.Stop();
        }

        private void OnDestroy() => Shutdown();
    }
}
