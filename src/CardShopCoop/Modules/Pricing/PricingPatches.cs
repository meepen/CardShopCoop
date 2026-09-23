using HarmonyLib;

namespace CardShopCoop.Modules.Pricing
{
    internal static class PricingHostPatches
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(PlayerConfirm)).Patch();
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

        [HarmonyPatch(typeof(SetItemPriceScreen), "OnPressConfirm")]
        private static class PlayerConfirm
        {
            [HarmonyPrefix]
            private static bool Prefix(SetItemPriceScreen __instance)
                => PricingHostBehaviour.Submit(__instance);
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
            harmony.CreateClassProcessor(typeof(PlayerConfirm)).Patch();
            harmony.CreateClassProcessor(typeof(DirectItemSetter)).Patch();
            harmony.CreateClassProcessor(typeof(DirectCardSetter)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryDefaultReset)).Patch();
            harmony.CreateClassProcessor(typeof(CardInventoryLoad)).Patch();
        }

        [HarmonyPatch(typeof(SetItemPriceScreen), "OnPressConfirm")]
        private static class PlayerConfirm
        {
            [HarmonyPrefix]
            private static bool Prefix(SetItemPriceScreen __instance)
                => PricingClientBehaviour.Submit(__instance);
        }

        [HarmonyPatch(typeof(CPlayerData), "SetItemPrice")]
        private static class DirectItemSetter
        {
            [HarmonyPrefix]
            private static bool Prefix(EItemType itemType, float price, out bool __state)
                => PricingClientBehaviour.InterceptItemSetter(itemType, price, out __state);

            [HarmonyPostfix]
            private static void Postfix(EItemType itemType, float price, bool __state)
            {
                if (!__state)
                    PricingClientBehaviour.CaptureItemSetter(itemType, price);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "SetCardPrice")]
        private static class DirectCardSetter
        {
            [HarmonyPrefix]
            private static bool Prefix(CardData cardData, float priceSet, out bool __state)
                => PricingClientBehaviour.InterceptCardSetter(cardData, priceSet, out __state);

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, float priceSet, bool __state)
            {
                if (!__state)
                    PricingClientBehaviour.CaptureCardSetter(cardData, priceSet);
            }
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
