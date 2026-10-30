using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>Shared read surface for the card delta fields both the authoritative state and
    /// the client action carry; the wire shapes stay identical.</summary>
    internal interface ICardDeltaSource
    {
        bool IsAdd
        {
            get;
        }
        int Amount
        {
            get;
        }
        CardData Card
        {
            get;
        }
    }

    /// <summary>Host -> peers: one authoritative card change. This is state for the players that
    /// did not perform the action; the actor receives its own copy through the action response.</summary>
    [NetworkMessage]
    public class CardDeltaMessage : WorldStateMessage, ICardDeltaSource
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;

        bool ICardDeltaSource.IsAdd => IsAdd;
        int ICardDeltaSource.Amount => Amount;
        CardData ICardDeltaSource.Card => Card;
    }

    /// <summary>Client -> host: one card change the game already performed locally. A rejection
    /// reverses exactly that change in <see cref="OnUndo"/>; <see cref="OnMerge"/> applies the
    /// host's authoritative counts and releases the graded-binder guard.</summary>
    [NetworkMessage]
    public sealed class CardDeltaRequestMessage : WorldIntentMessage, ICardDeltaSource
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;

        /// <summary>Local undo/cleanup state for this prediction. Never serialized and never
        /// present on the host's copy.</summary>
        [NonSerialized]
        internal WorldCardInteraction.CardActionPrediction Prediction;

        bool ICardDeltaSource.IsAdd => IsAdd;
        int ICardDeltaSource.Amount => Amount;
        CardData ICardDeltaSource.Card => Card;

        public override void OnUndo(ActionResponseMessage response)
            => WorldCardInteraction.UndoCardDelta(this);

        public override void OnMerge(ActionResponseMessage response)
            => WorldCardInteraction.MergeCardDelta(this, response);
    }

    public struct CardDeltaEntry
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;
    }

    /// <summary>Shared read surface for a card delta batch on either direction.</summary>
    internal interface ICardDeltaBatchSource
    {
        List<CardDeltaEntry> Deltas
        {
            get;
        }
    }

    /// <summary>Host -> peers: one authoritative atomic card movement.</summary>
    [NetworkMessage]
    public class CardDeltaBatchMessage : WorldStateMessage, ICardDeltaBatchSource
    {
        public List<CardDeltaEntry> Deltas = new();

        List<CardDeltaEntry> ICardDeltaBatchSource.Deltas => Deltas;
    }

    /// <summary>Client -> host: one atomic card movement the game already performed locally (a
    /// bundle, a trade hand-off, or one capped pack-opener reveal chunk).</summary>
    [NetworkMessage]
    public sealed class CardDeltaBatchRequestMessage : WorldIntentMessage, ICardDeltaBatchSource
    {
        public List<CardDeltaEntry> Deltas = new();

        /// <summary>True when the game already produced these deltas on this client, so a
        /// rejection must reverse them. Host-derived movements send the same wire shape without a
        /// local prediction.</summary>
        [NonSerialized]
        internal bool Predicted;

        List<CardDeltaEntry> ICardDeltaBatchSource.Deltas => Deltas;

        public override void OnUndo(ActionResponseMessage response)
            => WorldCardInteraction.UndoCardDeltaBatch(this);

        public override void OnMerge(ActionResponseMessage response)
            => WorldCardInteraction.MergeCardDeltaBatch(this, response);
    }

    /// <summary>Shared read surface for a graded card removal on either direction.</summary>
    internal interface IGradedRemoveSource
    {
        CardData Card
        {
            get;
        }
    }

    /// <summary>Host -> peers: one authoritative graded-album removal.</summary>
    [NetworkMessage]
    public class GradedRemoveMessage : WorldStateMessage, IGradedRemoveSource
    {
        public CardData Card;

        CardData IGradedRemoveSource.Card => Card;
    }

    /// <summary>Client -> host: one graded-album removal the game already performed locally.</summary>
    [NetworkMessage]
    public sealed class GradedRemoveRequestMessage : WorldIntentMessage, IGradedRemoveSource
    {
        public CardData Card;

        /// <summary>Local undo/cleanup state for this prediction. Never serialized.</summary>
        [NonSerialized]
        internal WorldCardInteraction.CardActionPrediction Prediction;

        CardData IGradedRemoveSource.Card => Card;

        public override void OnUndo(ActionResponseMessage response)
            => WorldCardInteraction.UndoGradedRemove(this);

        public override void OnMerge(ActionResponseMessage response)
            => WorldCardInteraction.MergeGradedRemove(this, response);
    }

    /// <summary>One card slot's authoritative owned count after an action. The actor applies
    /// these counts on merge, so a diverged count is corrected instead of trusted.</summary>
    public sealed class CardCountState
    {
        public CardData Card;
        public int Amount;
    }

    /// <summary>The host's authoritative card counts for one action, accepted or rejected.</summary>
    [NetworkMessage]
    public sealed class CardActionResult : ActionResponseMessage
    {
        public List<CardCountState> Cards = new();
    }
}
