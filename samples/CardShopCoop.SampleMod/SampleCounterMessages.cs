using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.SampleMod
{
    /// <summary>Client to host: please increment the shared counter. The client predicts the
    /// increment locally before sending, so the counter feels instant; the host answers with
    /// <see cref="SampleCounterResult"/>.</summary>
    [NetworkMessage]
    public sealed class SampleIncrementIntent : ActionMessage
    {
        /// <summary>Reverse the local prediction when the host rejects the increment. This must
        /// not apply authoritative state; <see cref="OnMerge"/> does that next, exactly once.</summary>
        public override void OnUndo(ActionResponseMessage response)
            => SampleCounterClient.Active?.UndoPredictedIncrement();

        /// <summary>Apply the host's post-action value. Runs exactly once for every resolution,
        /// after <see cref="OnUndo"/> when the host rejected the increment.</summary>
        public override void OnMerge(ActionResponseMessage response)
        {
            if (response is SampleCounterResult result)
            {
                SampleCounterClient.Active?.Merge(result.Value);
            }
        }
    }

    /// <summary>Host to the requesting client: the authoritative counter after the increment.</summary>
    [NetworkMessage]
    public sealed class SampleCounterResult : ActionResponseMessage
    {
        public int Value;
    }

    /// <summary>Host to everyone: the authoritative counter value.</summary>
    [NetworkMessage]
    public sealed class SampleCounterState : INetMessage
    {
        public int Value;
    }
}
