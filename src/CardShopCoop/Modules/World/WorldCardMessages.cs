using System;
using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>Shared read surface for the card delta fields both the authoritative state and
    /// the client request carry; the wire shapes stay identical.</summary>
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
        Guid ActionId
        {
            get;
        }
    }

    [NetworkMessage]
    public class CardDeltaMessage : WorldStateMessage, ICardDeltaSource
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;

        bool ICardDeltaSource.IsAdd => IsAdd;
        int ICardDeltaSource.Amount => Amount;
        CardData ICardDeltaSource.Card => Card;
        Guid ICardDeltaSource.ActionId => ActionId;
    }

    [NetworkMessage]
    public sealed class CardDeltaRequestMessage : WorldIntentMessage, ICardDeltaSource
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;

        bool ICardDeltaSource.IsAdd => IsAdd;
        int ICardDeltaSource.Amount => Amount;
        CardData ICardDeltaSource.Card => Card;
        Guid ICardDeltaSource.ActionId => ActionId;
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
        Guid ActionId
        {
            get;
        }
    }

    [NetworkMessage]
    public class CardDeltaBatchMessage : WorldStateMessage, ICardDeltaBatchSource
    {
        public List<CardDeltaEntry> Deltas = new();

        List<CardDeltaEntry> ICardDeltaBatchSource.Deltas => Deltas;
        Guid ICardDeltaBatchSource.ActionId => ActionId;
    }

    [NetworkMessage]
    public sealed class CardDeltaBatchRequestMessage : WorldIntentMessage, ICardDeltaBatchSource
    {
        public List<CardDeltaEntry> Deltas = new();

        List<CardDeltaEntry> ICardDeltaBatchSource.Deltas => Deltas;
        Guid ICardDeltaBatchSource.ActionId => ActionId;
    }

    /// <summary>Shared read surface for a graded card removal on either direction.</summary>
    internal interface IGradedRemoveSource
    {
        CardData Card
        {
            get;
        }
        Guid ActionId
        {
            get;
        }
    }

    [NetworkMessage]
    public class GradedRemoveMessage : WorldStateMessage, IGradedRemoveSource
    {
        public CardData Card;

        CardData IGradedRemoveSource.Card => Card;
        Guid IGradedRemoveSource.ActionId => ActionId;
    }

    [NetworkMessage]
    public sealed class GradedRemoveRequestMessage : WorldIntentMessage, IGradedRemoveSource
    {
        public CardData Card;

        CardData IGradedRemoveSource.Card => Card;
        Guid IGradedRemoveSource.ActionId => ActionId;
    }
}
