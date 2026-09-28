using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Grading
{
    [NetworkMessage]
    public sealed class GradingOpMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public byte ServiceLevel;
        public List<CardData> Cards = new();
        public byte CompanyId;

        /// <summary>Stable client-minted identity for the job this submission creates. The host
        /// adopts it as the authoritative set id, so both peers bind the job by id rather than by
        /// list position.</summary>
        public Guid JobId;
    }

    /// <summary>Complete authoritative grading-job state.</summary>
    [NetworkMessage]
    public sealed class GradingStateMessage : INetMessage
    {
        public List<GradingSetDto> Sets = new();
    }

    [NetworkMessage]
    public sealed class GradingJobDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public Guid Id;
        public bool Removed;
        public int ServiceLevel;
        public byte DayPassed;
        public float MinutePassed;
        public List<CardData> Cards = new();
    }

    public sealed class GradingSetDto
    {
        public Guid Id;
        public int ServiceLevel;
        public byte DayPassed;
        public float MinutePassed;
        public List<CardData> Cards = new();
    }
}
