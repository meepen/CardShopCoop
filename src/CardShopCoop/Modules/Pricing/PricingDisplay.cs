using System;
using System.Collections.Generic;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>
    /// The host-authoritative price a price tag should show. The game's own price store is the
    /// primary source, but it cannot represent every modded card (and some modded item ids), so
    /// this mirror keeps the last value the pricing protocol actually applied. It is only a view
    /// value: the module still writes the game setters first, and the pricing state/deltas remain
    /// the authority.
    /// </summary>
    internal static class PricingDisplay
    {
        private static readonly Dictionary<EItemType, float> Items = new();
        private static readonly Dictionary<string, float> Cards = new(StringComparer.Ordinal);

        internal static void SetItem(EItemType type, float price)
        {
            if (!PricingInterop.IsItemTypeValid(type) || !PricingInterop.ValidPrice(price))
            {
                return;
            }

            Items[type] = price;
            PriceTagRegistry.RefreshItem(type);
        }

        internal static void SetCard(CardData card, int encodedGrade, float price)
        {
            if (card == null || !PricingInterop.ValidCard(card)
                || !PricingInterop.ValidPrice(price))
            {
                return;
            }

            var key = PricingInterop.CardKey(card, encodedGrade);
            if (key == null)
            {
                return;
            }

            Cards[key] = price;
            PriceTagRegistry.RefreshCard(key);
        }

        internal static void RemoveCard(CardData card, int encodedGrade)
        {
            var key = card == null ? null : PricingInterop.CardKey(card, encodedGrade);
            if (key == null)
            {
                return;
            }

            Cards.Remove(key);
            PriceTagRegistry.RefreshCard(key);
        }

        internal static void Clear()
        {
            Items.Clear();
            Cards.Clear();
        }

        internal static float ReadItem(EItemType type)
        {
            if (!PricingInterop.IsItemTypeValid(type))
            {
                return 0f;
            }

            return Items.TryGetValue(type, out var price) ? price : PricingInterop.ReadItem(type);
        }

        internal static float ReadCard(CardData card)
        {
            if (card == null || !PricingInterop.ValidCard(card))
            {
                return 0f;
            }

            var key = PricingInterop.CardKey(card);
            if (key != null && Cards.TryGetValue(key, out var price))
            {
                return price;
            }

            return PricingInterop.ReadCard(card);
        }
    }
}
