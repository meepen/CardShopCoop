using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
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
