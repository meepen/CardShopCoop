using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// Joiner half of the shared counter. It sends an intent to the host (connection id 1) and
    /// mirrors whatever authoritative value comes back. Press K in-game to ask the host to
    /// increment.
    /// </summary>
    [ClientBehaviour]
    public sealed class SampleCounterClient : CoopBehaviour
    {
        private ICoopContext _context;
        private int _value;

        private void OnEnable()
        {
            _context = Context;
            CoopLog.Info("[sample] client counter online");
        }

        private void OnDisable()
        {
            _context = null;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.K))
            {
                CoopLog.Info("[sample] client sends an increment intent");
                var host = _context.HostConnection;
                if (host != null)
                {
                    _context.Send(host, new SampleIncrementIntent());
                }
            }
        }

        [MessageHandler]
        private void HandleState(CoopMessageContext context, SampleCounterState message)
        {
            _value = message.Value;
            CoopLog.Info("[sample] client mirrored the counter value: " + _value);
        }
    }
}
