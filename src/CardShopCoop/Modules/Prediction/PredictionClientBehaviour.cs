using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Prediction
{
    [ClientBehaviour]
    public sealed class PredictionClientBehaviour : CoopBehaviour
    {
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown)
                return;
            PredictionApi.Start();
            RuntimeContext.Messages.RegisterAttributedHandlers(this);
        }

        [MessageHandler(typeof(PredictionRollbackMessage))]
        private void Rollback(MessageContext _, PredictionRollbackMessage message)
            => PredictionApi.Rollback(message.PredictionId);

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            PredictionApi.Stop();
        }

        private void OnDestroy() => Shutdown();
    }
}
