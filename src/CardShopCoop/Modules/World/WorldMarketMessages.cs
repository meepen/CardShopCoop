using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    [NetworkMessage]
    public sealed class MarketStateMessage : WorldMessage
    {
        /// <summary>True only for the snapshot produced by the host's day roll. The client
        /// appends that day's price history locally after applying the values; history is
        /// derived from the current percents, so no history rows travel on the wire.</summary>
        public bool IsDayRoll;

        /// <summary>The host's current game day for <see cref="IsDayRoll"/>. The client uses it
        /// to append history at most once per day.</summary>
        public int Day;

        public List<MarketPercentEntry> ItemPricePercentChangeList = new();
        public List<MarketCardEntry> GenCardMarketPriceList = new();
        public List<MarketCardEntry> GenCardMarketPriceListDestiny = new();
        public List<MarketCardEntry> GenCardMarketPriceListGhost = new();
        public List<MarketCardEntry> GenCardMarketPriceListGhostBlack = new();
        public List<MarketCardEntry> GenCardMarketPriceListMegabot = new();
        public List<MarketCardEntry> GenCardMarketPriceListFantasyRPG = new();
        public List<MarketCardEntry> GenCardMarketPriceListCatJob = new();
        public List<MarketCardEntry> GenCardMarketPriceListAscension = new();
        public List<float> GenGradedCardPriceMultiplierList = new();
        public List<MarketModdedCardEntry> ModdedCards = new();
        public List<float> SetGameEventPriceList = new();
        public List<float> GeneratedGameEventPriceList = new();
        public List<float> GameEventPricePercentChangeList = new();
        public List<MarketSparseEntry> GeneratedMarketPriceList = new();
        public List<MarketSparseEntry> GeneratedCostPriceList = new();
        public List<MarketSparseEntry> AverageItemCostList = new();
    }

    public sealed class MarketPercentEntry
    {
        public EItemType ItemType;
        public short Percent;
    }

    public sealed class MarketCardEntry
    {
        public short Percent;
        public float GeneratedMarketPrice;
    }

    public sealed class MarketModdedCardEntry
    {
        public ECardExpansionType Expansion;
        public int Index;
        public bool IsDestiny;
        public bool HasPercent;
        public float Percent;
        public float Base;
        public bool HasBase;
    }

    public sealed class MarketSparseEntry
    {
        public EItemType ItemType;
        public float Value;
    }
}
