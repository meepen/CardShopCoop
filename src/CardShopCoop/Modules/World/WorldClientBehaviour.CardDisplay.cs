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

            // AckOrApply: the prediction's optimistic apply is a deliberate no-op (vanilla already
            // placed the card) and the host echoes this peer's own request fields back verbatim, so
            // the actor's local slot already matches ApplyCardDisplay (it early-returns on Matches).
            // A remote/host delta carries no pending id and still applies.
            WorldPrediction.AckOrApply(message, () => ApplyCardDisplay(message));
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
                // Same as HandleCardDisplay: the echoed host state is this peer's own request, so
                // the actor's slot already matches; a remote delta has no pending id.
                WorldPrediction.AckOrApply(message, () => ApplyCardDisplay(message));
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
            if (_shutdown || _cardDisplayApplying || compartment == null || !_context.InGame())
            {
                return;
            }

            if (!WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                // No key means the host cannot resolve the slot, so the placement never leaves
                // this peer. The usual cause is that the display shelf has no placement identity
                // yet (its baseline bind missed, e.g. a runtime-registered modded object type).
                CoopPlugin.Log.LogWarning(
                    "[card-display] placement dropped: display shelf has no placement identity.");
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
            // only a rejection needs to undo it. Predict records the post-hoc prediction
            // without re-running the game mutation the hook already observed.
            WorldPrediction.Predict(WorldPrediction.CardDisplayScope,
                new CardDisplayRequestMessage
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
                || !_context.InGame())
            {
                return;
            }

            if (!WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                CoopPlugin.Log.LogWarning(
                    "[card-display] removal dropped: display shelf has no placement identity.");
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
            WorldPrediction.Predict(WorldPrediction.CardDisplayScope,
                new CardDisplayRequestMessage
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
            // Capture-only prefix: record the slot's state before vanilla runs. The postfix then
            // forwards only when vanilla actually placed a card into a previously empty slot.
            [HarmonyPrefix]
            private static void Prefix(InteractableCardCompartment __instance, out CardData __state)
                => __state = WorldCardDisplay.Read(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance, CardData __state)
            {
                // An occupied slot makes vanilla return early without placing, so publishing on
                // __state alone would invent a prediction for a mutation the game never performed.
                if (__state == null && WorldCardDisplay.Read(__instance) != null)
                {
                    _instance?.PublishCardDisplayPlace(__instance);
                }
            }
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
