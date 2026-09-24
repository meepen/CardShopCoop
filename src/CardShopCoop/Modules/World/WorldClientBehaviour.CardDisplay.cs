using System.Collections.Generic;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private bool _cardDisplayApplying;
        // A card-display baseline can arrive before the placement identities that name its
        // shelves are bound, so unresolvable slots are held until the placement structure changes.
        private readonly Dictionary<long, CardDisplayMessage> _pendingCardDisplay = new();

        private void InstallCardDisplayPatches()
        {
            _harmony.CreateClassProcessor(typeof(CardDisplayPlacePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardDisplayRemovePatch)).Patch();
            PlacementApi.StructureChanged += OnCardDisplayStructureChanged;
        }

        private void ShutdownCardDisplay()
        {
            PlacementApi.StructureChanged -= OnCardDisplayStructureChanged;
            _pendingCardDisplay.Clear();
        }

        internal void ResetCardDisplayState()
        {
            _pendingCardDisplay.Clear();
            _cardDisplayApplying = false;
        }

        private void OnCardDisplayStructureChanged(int kind) => RetryPendingCardDisplay();

        [MessageHandler(typeof(CardDisplayMessage))]
        private void HandleCardDisplay(MessageContext context, CardDisplayMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            if (!CanApplyCardDisplay(message))
            {
                _pendingCardDisplay[CardDisplayKey(message)] = message;
                return;
            }

            WorldPrediction.ApplyConfirmed(message, () => ApplyCardDisplay(message));
        }

        private void RetryPendingCardDisplay()
        {
            if (_shutdown || _pendingCardDisplay.Count == 0)
            {
                return;
            }

            var pending = new List<CardDisplayMessage>(_pendingCardDisplay.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                var message = pending[i];
                if (!CanApplyCardDisplay(message))
                {
                    continue;
                }

                _pendingCardDisplay.Remove(CardDisplayKey(message));
                WorldPrediction.ApplyConfirmed(message, () => ApplyCardDisplay(message));
            }
        }

        private static long CardDisplayKey(CardDisplayMessage message)
            => ((long)message.ShelfKey << 32) ^ (uint)message.Compartment;

        private static bool CanApplyCardDisplay(CardDisplayMessage message)
            => WorldCardDisplay.TryResolve(message.ShelfKey, message.Compartment, out _);

        private void ApplyCardDisplay(CardDisplayMessage message)
        {
            if (!WorldCardDisplay.TryResolve(message.ShelfKey, message.Compartment,
                out var compartment))
            {
                return;
            }

            _cardDisplayApplying = true;
            try
            {
                if (!message.Occupied)
                {
                    WorldCardDisplay.Clear(compartment);
                    return;
                }

                var card = WorldCardDisplay.FromState(message.Card);
                if (card == null
                    || WorldCardDisplay.Matches(compartment, card, message.EncodedGrade))
                {
                    return;
                }

                if (compartment.m_StoredCardList != null
                    && compartment.m_StoredCardList.Count > 0)
                {
                    WorldCardDisplay.Clear(compartment);
                }

                WorldCardDisplay.Place(compartment, card);
            }
            finally
            {
                _cardDisplayApplying = false;
            }
        }

        private void PublishCardDisplayPlace(InteractableCardCompartment compartment)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null
                || !_context.InGame()
                || !WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                return;
            }

            var card = WorldCardDisplay.Read(compartment);
            var state = WorldCardDisplay.ToState(card);
            if (card == null || state == null)
            {
                return;
            }

            var grade = WorldCardDisplay.EncodedGradeOf(card);
            CoopPlugin.Log.LogInfo("[card-display] forwarding placement shelf=" + shelfKey
                + " compartment=" + index + " card=" + card.monsterType + ".");
            // The local game already placed the card, so the prediction has no local apply to run;
            // only a rejection needs to undo it.
            WorldPrediction.Predict(WorldPrediction.CardDisplayScope, new CardDisplayRequestMessage
            {
                ShelfKey = shelfKey,
                Compartment = index,
                Occupied = true,
                Card = state,
                EncodedGrade = grade,
            }, () => { }, () => UndoCardDisplayPlace(compartment, card));
        }

        private void PublishCardDisplayRemove(InteractableCardCompartment compartment, CardData card)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || card == null
                || !_context.InGame()
                || !WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                return;
            }

            var state = WorldCardDisplay.ToState(card);
            if (state == null)
            {
                return;
            }

            var grade = WorldCardDisplay.EncodedGradeOf(card);
            CoopPlugin.Log.LogInfo("[card-display] forwarding removal shelf=" + shelfKey
                + " compartment=" + index + " card=" + card.monsterType + ".");
            WorldPrediction.Predict(WorldPrediction.CardDisplayScope, new CardDisplayRequestMessage
            {
                ShelfKey = shelfKey,
                Compartment = index,
                Occupied = false,
                Card = state,
                EncodedGrade = grade,
            }, () => { }, () => UndoCardDisplayRemove(compartment, card));
        }

        /// <summary>A rejected placement: the card left the shared collection when it was picked up,
        /// so bank it back into the binder rather than destroying the only copy.</summary>
        private static void UndoCardDisplayPlace(InteractableCardCompartment compartment,
            CardData card)
        {
            WorldCardDisplay.Clear(compartment);
            if (card != null)
            {
                CPlayerData.AddCard(card, 1);
            }
        }

        private static void UndoCardDisplayRemove(InteractableCardCompartment compartment,
            CardData card)
        {
            if (compartment == null || card == null)
            {
                return;
            }

            if (compartment.m_StoredCardList != null
                && compartment.m_StoredCardList.Count > 0)
            {
                WorldCardDisplay.Clear(compartment);
            }

            WorldCardDisplay.Place(compartment, card);
        }

        [HarmonyPatch(typeof(InteractableCardCompartment), "OnMouseButtonUp")]
        private static class CardDisplayPlacePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance)
                => _instance?.PublishCardDisplayPlace(__instance);
        }

        [HarmonyPatch(typeof(InteractableCardCompartment), "OnRightMouseButtonUp")]
        private static class CardDisplayRemovePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCardCompartment __instance, out CardData __state)
                => __state = WorldCardDisplay.Read(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance, CardData __state)
            {
                // Only forward when vanilla actually took the card off the shelf.
                if (__state != null && WorldCardDisplay.Read(__instance) == null)
                {
                    _instance?.PublishCardDisplayRemove(__instance, __state);
                }
            }
        }
    }
}
