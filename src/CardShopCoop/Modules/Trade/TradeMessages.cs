using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Api;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Trade
{
    public enum TradeIntentOperation : byte
    {
        Open = 1,
        Accept = 2,
        Decline = 3,
        Think = 4,
        Close = 5,
    }

    public enum TradeOutcome : byte
    {
        None = 0,
        Accepted = 1,
        Declined = 2,
        Haggle = 3,
        Refused = 4,
        WalkedAway = 5,
        Expired = 6,
        Thinking = 7,
    }

    /// <summary>Client intent for one host-owned customer offer.</summary>
    [NetworkMessage]
    public sealed class TradeIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public TradeIntentOperation Operation;
        public byte Counter;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public uint OfferNonce;
        public float Price;

        /// <summary>The outcome the actor's own vanilla run already produced. On Accept the host
        /// records and broadcasts this instead of re-running the trade method, so cards, coins and
        /// the accept roll are applied exactly once (by the actor). For a kept offer the resulting
        /// state is carried in <see cref="ResultState"/>; a removed offer carries a null state.</summary>
        public TradeOutcome Result;
        public TradeOfferState ResultState;

        /// <summary>The card the customer handed the actor (the offer's left card), part of the one
        /// atomic accept outcome. The host applies it and fans it out; the actor already applied it
        /// locally through vanilla, so only a full reject reverts it.</summary>
        public CardData CardReceived;

        /// <summary>The card the actor handed the customer (the offer's right card) for a
        /// card-for-card trade; null for a coin purchase.</summary>
        public CardData CardRemoved;

        /// <summary>Coins the actor spent for a coin purchase; 0 for a card-for-card trade.</summary>
        public float CoinSpent;
    }

    /// <summary>Full authoritative offer baseline sent once when a peer joins.</summary>
    [NetworkMessage]
    public sealed class TradeOfferBaselineMessage : INetMessage
    {
        public List<TradeOfferState> Offers = new();
    }

    /// <summary>Accepted claim for a predicted guest trade session.</summary>
    [NetworkMessage]
    public sealed class TradeSessionMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Counter;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public uint OfferNonce;
        public bool Accepted;
    }

    /// <summary>One authoritative offer upsert or removal. A host-local delta has an empty
    /// PredictionId; an accepted guest action carries that action's prediction id.</summary>
    [NetworkMessage]
    public sealed class TradeOfferDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Counter;
        public TradeOutcome Outcome;
        public bool Removed;
        public TradeOfferState Offer;
    }

    public sealed class TradeOfferState
    {
        public byte Counter;
        public bool Trading;
        public CardData CardL;
        public CardData CardR;
        public float Price;
        public float MarketPrice;
        public float PriceSet;
        public float LastPriceSet;
        public int MaxDeclineCount;
        public int DeclineCount;
        public float Remaining;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public uint OfferNonce;
        public bool CustomerFemale;
        public Vector3 Position;
        public float Yaw;
    }
}
