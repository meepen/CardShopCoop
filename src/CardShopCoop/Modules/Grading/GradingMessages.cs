using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Grading
{
    [NetworkMessage]
    public sealed class GradingOpMessage : ActionMessage
    {
        public byte ServiceLevel;
        public List<CardData> Cards = new();
        public byte CompanyId;

        /// <summary>Stable client-minted identity for the job this submission creates. The host
        /// adopts it as the authoritative set id, so both peers bind the job by id rather than by
        /// list position.</summary>
        public Guid JobId;

        /// <summary>The local submission this action predicted. Local-only; never serialized.</summary>
        [NonSerialized]
        internal GradingClientBehaviour.SubmissionPrediction Prediction;

        public override void OnUndo(ActionResponseMessage response)
        {
            Prediction?.Undo();
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            var prediction = Prediction;
            Prediction = null;
            prediction?.Merge(response);
        }
    }

    /// <summary>Complete authoritative grading-job state.</summary>
    [NetworkMessage]
    public sealed class GradingStateMessage : INetMessage
    {
        public List<GradingSetDto> Sets = new();
    }

    [NetworkMessage]
    public sealed class GradingJobDeltaMessage : INetMessage
    {
        public Guid Id;
        public bool Removed;
        public int ServiceLevel;
        public byte DayPassed;
        public float MinutePassed;
        public List<CardData> Cards = new();
    }

    /// <summary>The acting client's answer: the post-submission job delta on accept, or the
    /// host's authoritative job list on reject.</summary>
    [NetworkMessage]
    public sealed class GradingIntentResponse : ActionResponseMessage
    {
        public GradingJobDeltaMessage Delta;
        public GradingStateMessage State;
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
