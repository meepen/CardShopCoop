using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>
    /// The live price-tag views, indexed by the value they show. The game's own price-change
    /// events keep an enabled tag current; this registry covers the two cases an event cannot:
    /// a tag that was disabled while the price changed (it refreshes when it enables), and a
    /// market snapshot that rewrites the price lists directly. Refreshing a key touches only the
    /// tags showing that item/card, never the scene.
    /// </summary>
    internal static class PriceTagRegistry
    {
        private sealed class Entry
        {
            internal EItemType Item;
            internal string Card;
        }

        private static readonly Dictionary<UI_PriceTag, Entry> Indexed = new();
        private static readonly Dictionary<EItemType, HashSet<UI_PriceTag>> ByItem = new();
        private static readonly Dictionary<string, HashSet<UI_PriceTag>> ByCard =
            new(StringComparer.Ordinal);
        private static readonly FieldInfo FiItemTag = AccessTools.Field(typeof(UI_PriceTag),
            "m_InteractablePriceTag");
        private static readonly FieldInfo FiCardTag = AccessTools.Field(typeof(UI_PriceTag),
            "m_InteractableCardPriceTag");
        private static readonly FieldInfo FiItemCompartment = AccessTools.Field(
            typeof(InteractablePriceTag), "m_ShelfCompartment");
        private static readonly FieldInfo FiCardCompartment = AccessTools.Field(
            typeof(InteractableCardPriceTag), "m_CardCompartment");

        /// <summary>One-time registration for tags that were already active before the pricing
        /// patches were installed (a host that starts a session in an already-loaded shop). New
        /// tags register through OnEnable; this is not a recurring scan.</summary>
        internal static void Bootstrap()
        {
            var tags = Resources.FindObjectsOfTypeAll<UI_PriceTag>();
            for (var i = 0; i < tags.Length; i++)
            {
                var tag = tags[i];
                if (tag == null || tag.gameObject == null || !tag.gameObject.scene.IsValid()
                    || !tag.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Add(tag);
            }
        }

        internal static void Add(UI_PriceTag tag)
        {
            if (tag == null)
            {
                return;
            }

            var itemTag = FiItemTag?.GetValue(tag) as InteractablePriceTag;
            var cardTag = FiCardTag?.GetValue(tag) as InteractableCardPriceTag;
            if (itemTag == null && cardTag == null)
            {
                // Not a shelf tag (a UI template or preview): leave it to the game.
                return;
            }

            Remove(tag);
            var entry = new Entry();
            var card = tag.GetCardData();
            if (card != null)
            {
                var key = PricingInterop.CardKey(card);
                if (key != null)
                {
                    entry.Card = key;
                    GetOrAdd(ByCard, key).Add(tag);
                }
            }
            else
            {
                var type = tag.GetItemType();
                if (type != EItemType.None)
                {
                    entry.Item = type;
                    GetOrAdd(ByItem, type).Add(tag);
                }
            }

            Indexed[tag] = entry;
            Refresh(tag);
        }

        internal static void Remove(UI_PriceTag tag)
        {
            if (tag == null || !Indexed.TryGetValue(tag, out var entry))
            {
                return;
            }

            Indexed.Remove(tag);
            if (entry.Card != null)
            {
                Remove(ByCard, entry.Card, tag);
            }

            if (entry.Item != EItemType.None)
            {
                Remove(ByItem, entry.Item, tag);
            }
        }

        /// <summary>A tag's item/card was replaced. Re-index it and show the new value.</summary>
        internal static void Reindex(UI_PriceTag tag)
        {
            if (tag == null || !Indexed.ContainsKey(tag))
            {
                return;
            }

            Remove(tag);
            Add(tag);
        }

        internal static void RefreshItem(EItemType type)
        {
            if (!ByItem.TryGetValue(type, out var tags))
            {
                return;
            }

            foreach (var tag in tags)
            {
                Refresh(tag);
            }
        }

        internal static void RefreshCard(string key)
        {
            if (key == null || !ByCard.TryGetValue(key, out var tags))
            {
                return;
            }

            foreach (var tag in tags)
            {
                Refresh(tag);
            }
        }

        /// <summary>Refresh every live tag. Used only after a market snapshot rewrites the price
        /// tables directly; normal price changes are already targeted by item/card.</summary>
        internal static void RefreshAll()
        {
            foreach (var tag in Indexed.Keys)
            {
                Refresh(tag);
            }
        }

        private static void Refresh(UI_PriceTag tag)
        {
            if (tag == null)
            {
                return;
            }

            try
            {
                var card = tag.GetCardData();
                var cardTag = FiCardTag?.GetValue(tag) as InteractableCardPriceTag;
                if (card != null && cardTag != null)
                {
                    var compartment = FiCardCompartment?.GetValue(cardTag)
                        as InteractableCardCompartment;
                    if (compartment != null && compartment.m_ItemNotForSale)
                    {
                        return;
                    }

                    tag.SetPriceText(PricingDisplay.ReadCard(card));
                    return;
                }

                var itemTag = FiItemTag?.GetValue(tag) as InteractablePriceTag;
                if (itemTag != null)
                {
                    if (itemTag.m_IsWarehouseTag)
                    {
                        return;
                    }

                    var compartment = FiItemCompartment?.GetValue(itemTag) as ShelfCompartment;
                    if (compartment != null
                        && (compartment.m_ItemNotForSale || !compartment.m_CanPutItem))
                    {
                        return;
                    }
                }

                if (card != null)
                {
                    tag.SetPriceText(PricingDisplay.ReadCard(card));
                    return;
                }

                var type = tag.GetItemType();
                if (type != EItemType.None)
                {
                    tag.SetPriceText(PricingDisplay.ReadItem(type));
                }
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("pricing.tag.refresh",
                    "pricing: could not refresh a price tag: " + ex.Message);
            }
        }

        private static void Remove<TKey>(Dictionary<TKey, HashSet<UI_PriceTag>> map, TKey key,
            UI_PriceTag tag)
        {
            if (!map.TryGetValue(key, out var tags))
            {
                return;
            }

            tags.Remove(tag);
            if (tags.Count == 0)
            {
                map.Remove(key);
            }
        }

        private static HashSet<UI_PriceTag> GetOrAdd<TKey>(
            Dictionary<TKey, HashSet<UI_PriceTag>> map, TKey key)
        {
            if (!map.TryGetValue(key, out var tags))
            {
                tags = new HashSet<UI_PriceTag>();
                map.Add(key, tags);
            }

            return tags;
        }
    }
}
