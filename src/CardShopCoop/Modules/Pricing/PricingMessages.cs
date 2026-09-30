using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Pricing
{
    [NetworkMessage]
    public sealed class PricingItemIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public EItemType ItemType;
        public float Price;
    }

    [NetworkMessage]
    public sealed class PricingCardIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public CardData Card;
        public float Price;
        public int EncodedGrade;

        /// <summary>Placement identity of the display shelf holding the priced card:
        /// kind&lt;&lt;24 | object id, the same key the card-display protocol uses.</summary>
        public int ShelfKey;

        /// <summary>Index of the compartment in the shelf's GetCardCompartmentList().</summary>
        public int Compartment;
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
        public Guid PredictionId;
        public EItemType ItemType;
        public float Price;
    }

    /// <summary>One card price change inside a <see cref="PricingDeltaBatchMessage"/>. The card is
    /// snapshotted when the entry is queued, never when the batch is flushed.</summary>
    public struct PricingCardDeltaEntry
    {
        public Guid PredictionId;
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
