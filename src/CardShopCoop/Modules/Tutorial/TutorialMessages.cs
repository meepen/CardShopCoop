using System.Collections.Generic;
using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>Host-owned tutorial index and condition values.</summary>
    [NetworkMessage]
    public sealed class TutorialStateMessage : INetMessage
    {
        public bool Full = true;
        public int TutorialIndex;
        public List<TutorialValue> Values = new();
    }

    public sealed class TutorialValue
    {
        public int Condition;
        public float Value;
    }

    /// <summary>Host-authoritative action delta. Progress is never accepted as an absolute value.</summary>
    [NetworkMessage]
    public sealed class TutorialActionDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int ExpectedTutorialIndex;
        public int ExpectedCondition;
        public int Action;
        public float Increment;
    }

    [NetworkMessage]
    public sealed class TutorialDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int TutorialIndex;
        public int Condition;
        public float Value;
        public List<TutorialValue> Values;
    }
}
