using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.SaveTransfer;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Harmony entry points for shared card state. These patches live beside the
    /// World behaviour instead of the process-wide legacy patch set so their callbacks always
    /// resolve the active host/client world owner.</summary>
    internal static class WorldCardInteractionPatches
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(AddCardPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReduceCardPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReduceCardIndexPatch)).Patch();
            harmony.CreateClassProcessor(typeof(RemoveGradedCardPatch)).Patch();
        }

        private static WorldCardInteraction Current => WorldHostBehaviour.ActiveCards
            ?? WorldClientBehaviour.ActiveCards;

        [HarmonyPatch(typeof(CPlayerData), "AddCard")]
        private static class AddCardPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(CardData cardData, int addAmount, out bool __state)
            {
                __state = false;
                if (Current?.IsClient == true && !WorldCardInteraction.ApplyingRemoteCards
                    && !SaveTransferApi.PreloadHold)
                {
                    __state = Current.PredictClientCardDelta(cardData, addAmount, true);
                    return !__state;
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int addAmount, bool __state)
            {
                if (__state || WorldCardInteraction.ApplyingRemoteCards || Current == null)
                {
                    return;
                }

                var encoded = GradingApi.Present
                    ? GradingApi.Encoded(cardData) : cardData.cardGrade;
                if (encoded > 10 && cardData.cardGrade <= 10 && cardData.cardGrade != 0)
                {
                    var saved = cardData.cardGrade;
                    cardData.cardGrade = encoded;
                    try
                    {
                        Current.ForwardCardDelta(cardData, addAmount, true);
                    }
                    finally
                    {
                        cardData.cardGrade = saved;
                    }
                }
                else
                {
                    Current.ForwardCardDelta(cardData, addAmount, true);
                }
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCard")]
        private static class ReduceCardPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(CardData cardData, int reduceAmount, out bool __state)
            {
                __state = false;
                if (Current?.IsClient == true && !WorldCardInteraction.ApplyingRemoteCards
                    && !SaveTransferApi.PreloadHold)
                {
                    __state = Current.PredictClientCardDelta(cardData, reduceAmount, false);
                    return !__state;
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int reduceAmount, bool __state)
            {
                if (__state || WorldCardInteraction.ApplyingRemoteCards || Current == null)
                {
                    return;
                }

                Current.ForwardCardDelta(cardData, reduceAmount, false);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCardUsingIndex")]
        private static class ReduceCardIndexPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(int index, ECardExpansionType expansionType,
                bool isDestiny, int reduceAmount, out bool __state)
            {
                __state = false;
                if (Current?.IsClient == true && !WorldCardInteraction.ApplyingRemoteCards
                    && !SaveTransferApi.PreloadHold)
                {
                    var card = CPlayerData.GetCardData(index, expansionType, isDestiny);
                    __state = Current.PredictClientCardDelta(card, reduceAmount, false);
                    return !__state;
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(int index, ECardExpansionType expansionType,
                bool isDestiny, int reduceAmount, bool __state)
            {
                if (__state || WorldCardInteraction.ApplyingRemoteCards || Current == null)
                {
                    return;
                }

                var card = CPlayerData.GetCardData(index, expansionType, isDestiny);
                if (card != null)
                {
                    Current.ForwardCardDelta(card, reduceAmount, false);
                }
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "RemoveGradedCard")]
        private static class RemoveGradedCardPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(CardData cardData, out bool __state)
            {
                __state = false;
                if (Current?.IsClient == true && !WorldCardInteraction.ApplyingRemoteCards
                    && !SaveTransferApi.PreloadHold)
                {
                    __state = Current.PredictClientGradedRemoval(cardData);
                    return !__state;
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, bool __state)
            {
                if (__state || WorldCardInteraction.ApplyingRemoteCards || Current == null
                    || cardData == null || cardData.cardGrade <= 0)
                {
                    return;
                }

                var encoded = GradingApi.Present
                    ? GradingApi.Encoded(cardData) : cardData.cardGrade;
                if (encoded > 10 && cardData.cardGrade <= 10)
                {
                    var saved = cardData.cardGrade;
                    cardData.cardGrade = encoded;
                    try
                    {
                        Current.ForwardGradedRemoval(cardData);
                    }
                    finally
                    {
                        cardData.cardGrade = saved;
                    }
                }
                else
                {
                    Current.ForwardGradedRemoval(cardData);
                }
            }
        }
    }
}
