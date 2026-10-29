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

        /// <summary>The client's ledger replay/undo closures own the prediction; the message is
        /// only the host command.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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

        /// <summary>The client's ledger replay/undo closures own the prediction; the message is
        /// only the host command.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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
        public Guid ActionId;
        public EItemType ItemType;
        public float Price;
    }

    /// <summary>One card price change inside a <see cref="PricingDeltaBatchMessage"/>. The card is
    /// snapshotted when the entry is queued, never when the batch is flushed.</summary>
    public struct PricingCardDeltaEntry
    {
        public Guid ActionId;
        public CardData Card;
        public float Price;
        public int EncodedGrade;
        public bool Removed;
    }

    /// <summary>Every pricing change produced in one frame, in apply order. One message per frame
    /// replaces the old one-message-per-change deltas so a mass price update (bulk reprice, a
    /// day-start automation pass) cannot overflow a peer's bounded outbound queue. The entries are
    /// append-only and each keeps its own prediction id: a later same-key entry must not swallow an
    /// earlier prediction-bearing one, or the peer that recorded it never reconciles.</summary>
    [NetworkMessage]
    public sealed class PricingDeltaBatchMessage : INetMessage
    {
        public List<PricingItemDeltaEntry> Items = new();
        public List<PricingCardDeltaEntry> Cards = new();
    }
}
