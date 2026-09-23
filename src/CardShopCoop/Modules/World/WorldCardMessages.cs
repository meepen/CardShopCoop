using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    [NetworkMessage]
    public class CardDeltaMessage : WorldMessage
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;
    }

    [NetworkMessage]
    public sealed class CardDeltaRequestMessage : CardDeltaMessage
    {
    }

    public struct CardDeltaEntry
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;
    }

    [NetworkMessage]
    public class CardDeltaBatchMessage : WorldMessage
    {
        public List<CardDeltaEntry> Deltas = new();
    }

    [NetworkMessage]
    public sealed class CardDeltaBatchRequestMessage : CardDeltaBatchMessage
    {
    }

    [NetworkMessage]
    public class GradedRemoveMessage : WorldMessage
    {
        public CardData Card;
    }

    [NetworkMessage]
    public sealed class GradedRemoveRequestMessage : GradedRemoveMessage
    {
    }
}
