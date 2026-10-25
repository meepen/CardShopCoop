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
    }

    [NetworkMessage]
    public sealed class HandheldSprayEventMessage : ActionMessage
    {
        public Vector3 Position;
        public float Content;
        public System.Collections.Generic.List<DeodorantCustomerState> Customers = new();
    }

    /// <summary>Resulting smell state for one live customer.</summary>
    public sealed class DeodorantCustomerState
    {
        public Customer Customer;
        public bool IsSmelly;
        public int SmellyMeter;
    }
}
