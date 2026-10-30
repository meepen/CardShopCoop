using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Deodorant
{
    /// <summary>An intent to apply one handheld spray tick. The host is authoritative.</summary>
    [NetworkMessage]
    public sealed class HandheldSprayIntentMessage : ActionMessage
    {
        public Vector3 Position;
        public float Content;

        /// <summary>The local spray content before this tick. Restored when the host rejects the
        /// spray so the prediction is reversed through the game's own surface.</summary>
        [NonSerialized]
        public InteractionPlayerController Controller;

        [NonSerialized]
        public float Before;

        public override void OnUndo(ActionResponseMessage response)
        {
            DeodorantState.TrySetContent(Controller, Before);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            // Shared customers are host-computed; the response carries the host's post-spray
            // state for the acting client. Other peers get the event broadcast.
            if (response is HandheldSprayResponseMessage result)
            {
                DeodorantState.ApplySprayTick(Controller, result.Content, result.Customers);
            }

            Controller = null;
            Before = 0f;
        }
    }

    /// <summary>The acting client's post-spray state: the spray content and every customer the
    /// host's spray changed. Other peers receive <see cref="HandheldSprayEventMessage"/>.</summary>
    [NetworkMessage]
    public sealed class HandheldSprayResponseMessage : ActionResponseMessage
    {
        public float Content;
        public List<DeodorantCustomerState> Customers = new();
    }

    /// <summary>One host-computed spray result for peers other than the acting client.</summary>
    [NetworkMessage]
    public sealed class HandheldSprayEventMessage : INetMessage
    {
        public Vector3 Position;
        public float Content;
        public List<DeodorantCustomerState> Customers = new();
    }

    /// <summary>Resulting smell state for one live customer.</summary>
    public sealed class DeodorantCustomerState
    {
        public Customer Customer;
        public bool IsSmelly;
        public int SmellyMeter;
    }
}
