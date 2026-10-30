using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// Host half of the shared counter. The host is authoritative: it owns the value, applies
    /// client increments itself, broadcasts the result, and sends one baseline to every late
    /// joiner. Press K in-game to increment on the host.
    /// </summary>
    [ServerBehaviour]
    public sealed class SampleCounterHost : CoopBehaviour
    {
        private int _value;

        private void OnEnable()
        {
            CoopLog.Info("[sample] host counter online");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.K))
            {
                _value++;
                CoopLog.Info("[sample] host incremented its own counter to " + _value);
                Broadcast();
            }
        }

        [OnFullyJoined]
        private void SendJoinBaseline(ICoopPeer peer)
        {
            peer?.Send(new SampleCounterState { Value = _value });
        }

        [MessageHandler]
        private ActionOutcome HandleIncrement(CoopMessageContext context,
            SampleIncrementIntent message)
        {
            // The host applies the command itself, then answers the sender with the resulting
            // value. Returning Applied(response) settles the sender's action exactly once.
            _value++;
            CoopLog.Info("[sample] host applied an increment from "
                + (context.Peer?.Name ?? "an unknown peer") + "; counter is now " + _value);
            Broadcast();
            return ActionOutcome.Applied(new SampleCounterResult { Value = _value });
        }

        private void Broadcast()
        {
            Context.Broadcast(new SampleCounterState { Value = _value });
        }
    }
}
