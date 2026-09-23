using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    [NetworkMessage]
    public sealed class MarketStateMessage : WorldMessage
    {
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
