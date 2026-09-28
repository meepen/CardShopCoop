using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.SaveTransfer;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Harmony entry points for shared card state. These patches live beside the
    /// World behaviour instead of the process-wide legacy patch set so their callbacks always
    /// resolve the active host/client world owner.
    ///
    /// The GAME owns every mutation: each hook below is an OBSERVER. Vanilla runs its own method
    /// to completion and the postfix records the resulting change as ONE prediction (client) or
    /// fans it out (host). No prefix suppresses the original, so the local ledger can never drift
    /// from what the game actually did.</summary>
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
            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int addAmount)
            {
                var current = Current;
                if (current == null || WorldCardInteraction.ApplyingRemoteCards
                    || WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold)
                {
                    return;
                }

                if (current.HasClientCardBatch)
                {
                    // A bulk reveal (a pack-opener collect) folds every card into one atomic
                    // batch, exactly like the workbench bundle, instead of one intent per card.
                    current.AddClientCardBatch(cardData, addAmount, true);
                    return;
                }

                if (current.IsClient)
                {
                    // The game already added the card; record how to redo/undo it and send one
                    // intent. No local apply - the game owns the mutation.
                    current.PredictClientCardDelta(cardData, addAmount, true);
                    return;
                }

                // Host: the local addition is authoritative, so fan it out. Grading Overhaul keeps
                // the live card's cardGrade as the bare 1-10 and the encoded grade in its registry,
                // so temporarily write the encoded grade for the snapshot only.
                var encoded = GradingApi.Present
                    ? GradingApi.Encoded(cardData) : cardData.cardGrade;
                if (encoded > 10 && cardData.cardGrade <= 10 && cardData.cardGrade != 0)
                {
                    var saved = cardData.cardGrade;
                    cardData.cardGrade = encoded;
                    try
                    {
                        current.ForwardCardDelta(cardData, addAmount, true);
                    }
                    finally
                    {
                        cardData.cardGrade = saved;
                    }
                }
                else
                {
                    current.ForwardCardDelta(cardData, addAmount, true);
                }
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCard")]
        private static class ReduceCardPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int reduceAmount)
            {
                var current = Current;
                if (current == null || WorldCardInteraction.ApplyingRemoteCards
                    || WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold)
                {
                    return;
                }

                if (current.IsClient)
                {
                    current.PredictClientCardDelta(cardData, reduceAmount, false);
                    return;
                }

                current.ForwardCardDelta(cardData, reduceAmount, false);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "ReduceCardUsingIndex")]
        private static class ReduceCardIndexPatch
        {
            [HarmonyPostfix]
            private static void Postfix(int index, ECardExpansionType expansionType,
                bool isDestiny, int reduceAmount)
            {
                var current = Current;
                if (current == null || WorldCardInteraction.ApplyingRemoteCards
                    || WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold)
                {
                    return;
                }

                var card = CPlayerData.GetCardData(index, expansionType, isDestiny);
                if (card == null)
                {
                    return;
                }

                // A bulk action (bundle, donation quick-fill) folds every reduction into ONE
                // pending transaction; CommitClientCardBatch records it once the scope closes. The
                // game already reduced, so the batch is a post-hoc record, not a deferred apply.
                if (current.HasClientCardBatch)
                {
                    current.AddClientCardBatch(card, reduceAmount, false);
                    return;
                }

                if (current.IsClient)
                {
                    current.PredictClientCardDelta(card, reduceAmount, false);
                    return;
                }

                current.ForwardCardDelta(card, reduceAmount, false);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "RemoveGradedCard")]
        private static class RemoveGradedCardPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData)
            {
                var current = Current;
                if (current == null || WorldCardInteraction.ApplyingRemoteCards
                    || WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold
                    || cardData == null || cardData.cardGrade <= 0)
                {
                    return;
                }

                if (current.IsClient)
                {
                    current.PredictClientGradedRemoval(cardData);
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
                        current.ForwardGradedRemoval(cardData);
                    }
                    finally
                    {
                        cardData.cardGrade = saved;
                    }
                }
                else
                {
                    current.ForwardGradedRemoval(cardData);
                }
            }
        }
    }
}
