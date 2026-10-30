using CardShopCoop.Attributes;
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
