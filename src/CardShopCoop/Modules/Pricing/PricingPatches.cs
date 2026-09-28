using HarmonyLib;

namespace CardShopCoop.Modules.Pricing
{
    internal static class PricingHostPatches
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(DirectItemSetter)).Patch();
            harmony.CreateClassProcessor(typeof(DirectCardSetter)).Patch();
            harmony.CreateClassProcessor(typeof(CardAdded)).Patch();
            harmony.CreateClassProcessor(typeof(CardReduced)).Patch();
            harmony.CreateClassProcessor(typeof(CardReducedUsingIndex)).Patch();
            harmony.CreateClassProcessor(typeof(GradedCardRemoved)).Patch();
            harmony.CreateClassProcessor(typeof(CardSet)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryDefaultReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryLoad)).Patch();
            harmony.CreateClassProcessor(typeof(GradedInventoryDayStarted)).Patch();
        }

        [HarmonyPatch(typeof(CPlayerData), "SetItemPrice")]
        private static class DirectItemSetter
        {
            [HarmonyPostfix]
            private static void Postfix(EItemType itemType, float price)
                => PricingHostBehaviour.CaptureItemSetter(itemType, price);
        }

        [HarmonyPatch(typeof(CPlayerData), "SetCardPrice")]
        private static class DirectCardSetter
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData, float priceSet)
                => PricingHostBehaviour.CaptureCardSetter(cardData, priceSet);
        }

        [HarmonyPatch(typeof(CPlayerData), "AddCard")]
        private static class CardAdded
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData)
            {
                PricingHostBehaviour.InventoryChanged(cardData);
                PricingHostBehaviour.GradedInventoryChanged();
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCard")]
        private static class CardReduced
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData)
                => PricingHostBehaviour.InventoryChanged(cardData);
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCardUsingIndex")]
        private static class CardReducedUsingIndex
        {
            [HarmonyPostfix]
            private static void Postfix(int index, ECardExpansionType expansionType, bool isDestiny)
            {
                PricingHostBehaviour.InventoryChanged(
                    CPlayerData.GetCardData(index, expansionType, isDestiny));
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "RemoveGradedCard")]
        private static class GradedCardRemoved
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData)
                => PricingHostBehaviour.InventoryChanged(cardData);
        }

        [HarmonyPatch(typeof(CPlayerData), "SetCard")]
        private static class CardSet
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData)
                => PricingHostBehaviour.InventoryChanged(cardData);
        }

        [HarmonyPatch(typeof(CPlayerData), "ResetData")]
        private static class CardInventoryReset
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingHostBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CPlayerData), "CreateDefaultData")]
        private static class CardInventoryDefaultReset
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingHostBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CGameData), "PropagateLoadData")]
        private static class CardInventoryLoad
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingHostBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(RestockManager), "OnDayStarted")]
        private static class GradedInventoryDayStarted
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingHostBehaviour.GradedInventoryChanged();
        }
    }

    internal static class PricingClientPatches
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(DirectItemSetter)).Patch();
            harmony.CreateClassProcessor(typeof(DirectCardSetter)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryDefaultReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryLoad)).Patch();
        }

        [HarmonyPatch(typeof(CPlayerData), "SetItemPrice")]
        private static class DirectItemSetter
        {
            // Capture-only prefix: snapshot the pre-change price, then let the game's own setter
            // run. The client no longer suppresses the confirm or the setter, so the game owns the
            // local price mutation; the postfix observes it and registers one post-hoc prediction
            // so a host rejection can replay the previous value.
            [HarmonyPrefix]
            private static void Prefix(EItemType itemType, out float __state)
                => __state = PricingClientBehaviour.CaptureItemBefore(itemType);

            [HarmonyPostfix]
            private static void Postfix(EItemType itemType, float price, float __state)
                => PricingClientBehaviour.ObserveItemSetter(itemType, price, __state);
        }

        [HarmonyPatch(typeof(CPlayerData), "SetCardPrice")]
        private static class DirectCardSetter
        {
            [HarmonyPrefix]
            private static void Prefix(CardData cardData, out float __state)
                => __state = PricingClientBehaviour.CaptureCardBefore(cardData);

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, float priceSet, float __state)
                => PricingClientBehaviour.ObserveCardSetter(cardData, priceSet, __state);
        }

        [HarmonyPatch(typeof(CPlayerData), "ResetData")]
        private static class CardInventoryReset
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingClientBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CPlayerData), "CreateDefaultData")]
        private static class CardInventoryDefaultReset
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingClientBehaviour.InventoryReset();
        }

        [HarmonyPatch(typeof(CGameData), "PropagateLoadData")]
        private static class CardInventoryLoad
        {
            [HarmonyPostfix]
            private static void Postfix()
                => PricingClientBehaviour.InventoryReset();
        }
    }
}
