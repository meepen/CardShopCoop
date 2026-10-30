using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>One display slot's live identity and authoritative contents. The shelf is a live
    /// scene object and the compartment is its index in the shelf's list.</summary>
    public sealed class CardDisplayState
    {
        public CardShelf Shelf;
        public int Compartment;
        public bool Occupied;
        public BoxCardState Card;
        public int EncodedGrade;
    }

    /// <summary>Client -> host: one single-card display slot was filled or emptied by the local
    /// player. The game already performed the change; a rejection reverses exactly that change and
    /// the response carries the host's authoritative slot state.</summary>
    [NetworkMessage]
    public sealed class CardDisplayRequestMessage : WorldIntentMessage
    {
        public CardDisplayState State = new();

        /// <summary>The live local slot this prediction changed. Never serialized; the host
        /// addresses the slot through <see cref="State"/>.</summary>
        [NonSerialized]
        internal InteractableCardCompartment LocalCompartment;

        /// <summary>The exact card object vanilla moved into the hand, when this was a removal.
        /// Never serialized.</summary>
        [NonSerialized]
        internal InteractableCard3d LocalCard3d;

        /// <summary>The card data snapshot captured at prediction time, for the data fallback when
        /// the exact object is gone. Never serialized.</summary>
        [NonSerialized]
        internal CardData LocalCard;

        public override void OnUndo(ActionResponseMessage response)
            => WorldClientBehaviour.UndoCardDisplayPrediction(this);

        public override void OnMerge(ActionResponseMessage response)
            => WorldClientBehaviour.MergeCardDisplayPrediction(this, response);
    }

    /// <summary>Host -> peers: the authoritative state of one single-card display slot. The actor
    /// of a request is excluded and receives the same state in its action response instead.</summary>
    [NetworkMessage]
    public sealed class CardDisplayMessage : WorldStateMessage
    {
        public CardDisplayState State = new();
    }

    /// <summary>The host's authoritative display-slot state after one action, accepted or
    /// rejected. The framework sets <see cref="ActionResponseMessage.Rejected"/>.</summary>
    [NetworkMessage]
    public sealed class CardDisplayResponse : ActionResponseMessage
    {
        public CardDisplayState State;
    }
}
