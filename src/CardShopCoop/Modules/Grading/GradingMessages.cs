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

        // The client registers this prediction through the explicit Apply/Undo closures in
        // GradingClientBehaviour, and the host handler performs the submission itself, so the
        // action hooks are never the applied path.
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
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
        /// <summary>The prediction this delta answers, or empty for host-local job state.</summary>
        public Guid ActionId;
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
