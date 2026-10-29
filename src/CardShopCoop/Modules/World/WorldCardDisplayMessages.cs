using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>Client -> host: one single-card display slot was filled or emptied by the local
    /// player. The host owns the authoritative slot and echoes a <see cref="CardDisplayMessage"/>
    /// carrying the same prediction id.</summary>
    [NetworkMessage]
    public sealed class CardDisplayRequestMessage : WorldIntentMessage
    {
        /// <summary>The live display shelf that owns the compartment.</summary>
        public CardShelf Shelf;

        /// <summary>Index of the compartment in the shelf's <c>GetCardCompartmentList()</c>.</summary>
        public int Compartment;

        /// <summary>True when the slot now holds <see cref="Card"/>; false when the card left it.</summary>
        public bool Occupied;

        public BoxCardState Card;
        public int EncodedGrade;
    }

    /// <summary>Host -> peers: the authoritative state of one single-card display slot.</summary>
    [NetworkMessage]
    public sealed class CardDisplayMessage : WorldStateMessage
    {
        public CardShelf Shelf;
        public int Compartment;
        public bool Occupied;
        public BoxCardState Card;
        public int EncodedGrade;
    }
}
