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

        // The client registers this prediction through the explicit Apply/Undo closures in
        // DeodorantClientBehaviour, and the host handler performs the spray itself, so the action
        // hooks are never the applied path.
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    [NetworkMessage]
    public sealed class HandheldSprayEventMessage : INetMessage
    {
        /// <summary>The prediction this event answers, or empty for a host-local spray.</summary>
        public Guid ActionId;
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
