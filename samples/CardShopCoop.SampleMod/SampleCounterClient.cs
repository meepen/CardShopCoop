using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.SampleMod
{
    /// <summary>
    /// Joiner half of the shared counter. It predicts an increment locally, sends the action to the
    /// host, and mirrors whatever authoritative value comes back. Press K in-game to ask the host
    /// to increment.
    /// </summary>
    [ClientBehaviour]
    public sealed class SampleCounterClient : CoopBehaviour
    {
        private int _value;

        internal static SampleCounterClient Active
        {
            get;
            private set;
        }

        private void OnEnable()
        {
            Active = this;
            CoopLog.Info("[sample] client counter online");
        }

        private void OnDisable()
        {
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }
        }

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.K))
            {
                return;
            }

            if (Context == null || !Context.InGame || Context.Host == null)
            {
                CoopLog.Warn("[sample] the client is not in a co-op game yet");
                return;
            }

            // Predict first so the counter feels instant. The framework records the action and
            // hands it to the host; OnUndo reverses this if the host rejects it, and OnMerge
            // settles the live value either way.
            _value++;
            CoopLog.Info("[sample] client predicted an increment to " + _value);
            Context.Send(new SampleIncrementIntent());
        }

        internal void UndoPredictedIncrement()
        {
            _value--;
            CoopLog.Info("[sample] host rejected the increment; counter back to " + _value);
        }

        internal void Merge(int value)
        {
            _value = value;
            CoopLog.Info("[sample] client merged the authoritative counter value: " + _value);
        }

        [MessageHandler]
        private void HandleState(CoopMessageContext context, SampleCounterState message)
        {
            _value = message.Value;
            CoopLog.Info("[sample] client mirrored the counter value: " + _value);
        }
    }
}
