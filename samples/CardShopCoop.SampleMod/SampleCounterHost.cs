using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using UnityEngine;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// Host half of the shared counter. The host is authoritative: it owns the value, applies
    /// client intents, broadcasts the result, and sends one baseline to every late joiner.
    /// Press K in-game to increment on the host.
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
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (connection != null)
            {
                Context.Send(connection.Id, new SampleCounterState { Value = _value });
            }
        }

        [MessageHandler]
        private void HandleIncrement(CoopMessageContext context, SampleIncrementIntent message)
        {
            _value++;
            CoopLog.Info("[sample] host applied an increment from connection " + context.ConnectionId
                + "; counter is now " + _value);
            Broadcast();
        }

        private void Broadcast()
        {
            Context.Broadcast(new SampleCounterState { Value = _value });
        }
    }
}
