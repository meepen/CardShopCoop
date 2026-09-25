using System;
using System.Reflection;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Grading;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>Common game-facing price operations and wire validation.</summary>
    internal static class PricingInterop
    {
        private static readonly FieldInfo PriceField = typeof(SetItemPriceScreen).GetField(
            "m_PriceSet", BindingFlags.Instance | BindingFlags.NonPublic);

        internal const float PriceEpsilon = 0.0075f;

        /// <summary>A named EItemType member with a live price table. The id is deliberately NOT
        /// bounded by the vanilla list length: EPL's CollectionWeaver serves its minted members
        /// through that list, and <see cref="CatalogApi.IsWireableItemType"/> already rejects the
        /// unnamed raw slots in the price list's tail.</summary>
        internal static bool IsItemTypeValid(EItemType type)
            => CatalogApi.IsWireableItemType(type, "pricing item")
                && CPlayerData.m_SetItemPriceList != null;

        internal static bool TryReadConfirmPrice(SetItemPriceScreen screen, out float price)
        {
            price = 0f;
            if (screen == null || PriceField == null)
                return false;

            price = (float)PriceField.GetValue(screen);
            return true;
        }

        internal static float ReadItem(EItemType type)
        {
            if (!IsItemTypeValid(type))
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown item type.");
            return CPlayerData.GetItemPrice(type, false);
        }

        internal static bool SetItem(EItemType type, float price, out float actual)
        {
            actual = 0f;
            if (!IsItemTypeValid(type) || !ValidPrice(price))
                return false;

            CPlayerData.SetItemPrice(type, price);
            actual = ReadItem(type);
            return Math.Abs(actual - price) <= PriceEpsilon;
        }

        /// <summary>Writes a card price and reports the value to publish. The game's price store
        /// only holds the seven vanilla expansions: <see cref="CPlayerData.SetCardPrice"/> is a
        /// hard-coded if-chain that silently writes nothing for any other expansion, yet still
        /// fires <c>CEventPlayer_CardPriceChanged</c> (the event that repaints every visible price
        /// tag). A store that cannot hold the value is not an invalid intent, so this accepts the
        /// requested price instead of reporting the read-back: rejecting here rolled the setter
        /// back and left them looking at the old price while every other peer showed the new one.</summary>
        internal static bool SetCard(CardData card, float price, out float actual)
        {
            actual = 0f;
            if (card == null || !ValidPrice(price))
                return false;

            CPlayerData.SetCardPrice(card, price);
            actual = ReadCard(card);
            if (Math.Abs(actual - price) <= PriceEpsilon)
                return true;

            CoopPlugin.Log.LogWarning("[pricing] card price store did not hold " + price
                + " for expansion=" + (int)card.expansionType + " monster=" + (int)card.monsterType
                + " grade=" + card.cardGrade + " (read back " + actual
                + "); publishing the requested price so peers stay in step.");
            actual = price;
            return true;
        }

        internal static float ReadCard(CardData card)
        {
            if (card == null)
                throw new ArgumentNullException(nameof(card));
            return CPlayerData.GetCardPrice(card);
        }

        internal static CardData CopyCard(CardData card, int encodedGrade)
        {
            if (card == null)
                return null;
            var copy = new CardData();
            copy.CopyData(card);
            copy.cardGrade = encodedGrade;
            return copy;
        }

        internal static int ItemCount => CPlayerData.m_SetItemPriceList?.Count ?? 0;

        internal static bool ValidCard(CardData card)
        {
            if (card == null || card.expansionType == ECardExpansionType.None
                || card.monsterType == EMonsterType.None || card.cardGrade < 0)
                return false;

            try
            {
                return CPlayerData.GetCardSaveIndex(card) >= 0
                    && GradingApi.Encoded(card) >= 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool ValidPrice(float price)
            => !float.IsNaN(price) && !float.IsInfinity(price) && price >= 0f
                && price <= 1000000000f;

        internal static int SafeSaveIndex(CardData card)
        {
            if (card == null)
            {
                return -1;
            }

            try
            {
                return CPlayerData.GetCardSaveIndex(card);
            }
            catch
            {
                return -1;
            }
        }

        internal static string CardKey(CardData card, int encodedGrade = int.MinValue)
        {
            if (card == null)
                return null;

            var grade = encodedGrade == int.MinValue ? GradingApi.Encoded(card) : encodedGrade;
            return (int)card.expansionType + ":" + (int)card.monsterType + ":"
                + (int)card.borderType + ":" + (card.isFoil ? 1 : 0) + ":"
                + (card.isDestiny ? 1 : 0) + ":" + (card.isChampionCard ? 1 : 0)
                + ":" + grade;
        }
    }
}
