using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Deodorant
{
    /// <summary>An intent to apply one handheld spray tick. The host is authoritative.</summary>
    [NetworkMessage]
    public sealed class HandheldSprayIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public Vector3 Position;
        public float Content;
    }

    [NetworkMessage]
    public sealed class HandheldSprayEventMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public Vector3 Position;
        public float Content;
        public System.Collections.Generic.List<DeodorantCustomerState> Customers = new();
    }

    /// <summary>Resulting smell state for one pooled customer incarnation.</summary>
    public sealed class DeodorantCustomerState
    {
        public int Index;
        public int Generation;
        public bool IsSmelly;
        public int SmellyMeter;
    }
}
