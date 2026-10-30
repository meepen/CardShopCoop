using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Pricing
{
    [NetworkMessage]
    public sealed class PricingItemIntentMessage : ActionMessage
    {

        public EItemType ItemType;
        public float Price;

        /// <summary>The item price before the local change. Local undo state; not serialized.</summary>
        [NonSerialized]
        public float Previous;

        public override void OnUndo(ActionResponseMessage response)
            => PricingClientBehaviour.Active?.UndoItem(this);

        public override void OnMerge(ActionResponseMessage response)
            => PricingClientBehaviour.Active?.MergeItem(this, response);
    }

    [NetworkMessage]
    public sealed class PricingCardIntentMessage : ActionMessage
    {

        public CardData Card;
        public float Price;
        public int EncodedGrade;

        /// <summary>The live display shelf holding the priced card.</summary>
        public CardShelf Shelf;

        /// <summary>Index of the compartment in the shelf's GetCardCompartmentList().</summary>
        public int Compartment;

        /// <summary>The card price before the local change. Local undo state; not serialized.</summary>
        [NonSerialized]
        public float Previous;

        public override void OnUndo(ActionResponseMessage response)
            => PricingClientBehaviour.Active?.UndoCard(this);

        public override void OnMerge(ActionResponseMessage response)
            => PricingClientBehaviour.Active?.MergeCard(this, response);
    }

    /// <summary>The host's absolute item price after one intent. <see cref="Price"/> is
    /// <see cref="float.NaN"/> when the host has no authoritative value to send.</summary>
    [NetworkMessage]
    public sealed class PricingItemResponse : ActionResponseMessage
    {
        public EItemType ItemType;
        public float Price = float.NaN;
    }

    /// <summary>The host's absolute card price after one intent. <see cref="Price"/> is
    /// <see cref="float.NaN"/> when the host has no authoritative value to send.</summary>
    [NetworkMessage]
    public sealed class PricingCardResponse : ActionResponseMessage
    {
        public CardData Card;
        public int EncodedGrade;
        public float Price = float.NaN;
    }

    /// <summary>One host-owned pricing snapshot. The newest received snapshot replaces the old one.</summary>
    [NetworkMessage]
    public sealed class PricingStateMessage : INetMessage
    {
        public List<EItemType> ItemTypes = new();
        public List<float> ItemPrices = new();
        public List<CardData> Cards = new();
        public List<float> CardPrices = new();
        public List<int> CardGrades = new();
    }

    /// <summary>One item price change inside a <see cref="PricingDeltaBatchMessage"/>.</summary>
    public struct PricingItemDeltaEntry
    {
        public EItemType ItemType;
        public float Price;
    }

    /// <summary>One card price change inside a <see cref="PricingDeltaBatchMessage"/>. The card is
    /// snapshotted when the entry is queued, never when the batch is flushed.</summary>
    public struct PricingCardDeltaEntry
    {
        public CardData Card;
        public float Price;
        public int EncodedGrade;
        public bool Removed;
    }

    /// <summary>Every host-originated pricing change produced in one frame, in apply order. One
    /// message per frame replaces the old one-message-per-change deltas so a mass price update
    /// (bulk reprice, a day-start automation pass) cannot overflow a peer's bounded outbound
    /// queue. A guest's own intent is answered by its <see cref="ActionResponseMessage"/>.</summary>
    [NetworkMessage]
    public sealed class PricingDeltaBatchMessage : INetMessage
    {
        public List<PricingItemDeltaEntry> Items = new();
        public List<PricingCardDeltaEntry> Cards = new();
    }
}
