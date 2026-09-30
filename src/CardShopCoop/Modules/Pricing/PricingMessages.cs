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

    [NetworkMessage]
    public sealed class PricingItemDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public EItemType ItemType;
        public float Price;
    }

    [NetworkMessage]
    public sealed class PricingCardDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public CardData Card;
        public float Price;
        public int EncodedGrade;
        public bool Removed;
    }
}
