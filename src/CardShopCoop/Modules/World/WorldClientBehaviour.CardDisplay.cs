using System.Runtime.CompilerServices;
using CardShopCoop.Net;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldClientBehaviour
    {
        private bool _cardDisplayApplying;

        private void InstallCardDisplayPatches()
        {
            Harmony.CreateClassProcessor(typeof(CardDisplayPlacePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CardDisplayRemovePatch)).Patch();
        }

        private void ShutdownCardDisplay()
        {
            _cardDisplayApplying = false;
        }

        internal void ResetCardDisplayState()
        {
            _cardDisplayApplying = false;
        }

        [MessageHandler]
        private void HandleCardDisplay(MessageContext context, CardDisplayMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            if (message.Shelf == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[card-display] dropped state: the display shelf did not resolve to a live object.");
                return;
            }

            if (!CanApplyCardDisplay(message))
            {
                CoopPlugin.Log.LogWarning("[card-display] dropped state: shelf="
                    + message.Shelf.name + " has no compartment " + message.Compartment + ".");
                return;
            }

            // Confirm: retire our own prediction AND always apply the host's authoritative slot
            // state. Matches() makes the apply idempotent for the actor, while a host state that
            // raced ahead of our request (or a remote delta) must still be applied - AckOrApply
            // would retire our id and leave the slot showing our optimistic card.
            WorldAction.Confirm(message, () => ApplyCardDisplay(message));
        }

        /// <summary>One prediction key per live slot so a rejection on one slot cannot undo/replay
        /// an in-flight change on another.</summary>
        private static string CardDisplayPredictionKey(CardShelf shelf, int compartment)
            => WorldAction.CardDisplayScope + ":" + RuntimeHelpers.GetHashCode(shelf)
                + ":" + compartment;

        private static bool CanApplyCardDisplay(CardDisplayMessage message)
            => WorldCardDisplay.TryGetCompartment(message.Shelf, message.Compartment, out _);

        private void ApplyCardDisplay(CardDisplayMessage message)
        {
            if (message?.Shelf == null
                || !WorldCardDisplay.TryGetCompartment(message.Shelf, message.Compartment,
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

            if (!WorldCardDisplay.TryGetShelf(compartment, out var shelf, out var index))
            {
                CoopPlugin.Log.LogWarning(
                    "[card-display] placement dropped: the compartment has no owning display shelf.");
                return;
            }

            var card = WorldCardDisplay.Read(compartment);
            var state = WorldCardDisplay.ToState(card);
            if (card == null || state == null)
            {
                return;
            }

            var grade = WorldCardDisplay.EncodedGradeOf(card);
            CoopPlugin.Log.LogInfo("[card-display] forwarding placement shelf=" + shelf.name
                + " compartment=" + index + " card=" + card.monsterType + ".");
            // The local game already placed the card, so the prediction has no local apply to run;
            // only a rejection needs to undo it. Predict records the post-hoc prediction
            // without re-running the game mutation the hook already observed.
            WorldAction.Submit(CardDisplayPredictionKey(shelf, index),
                new CardDisplayRequestMessage
                {
                    Shelf = shelf,
                    Compartment = index,
                    Occupied = true,
                    Card = state,
                    EncodedGrade = grade,
                }, () => { }, () => UndoCardDisplayPlace(compartment, card));
        }

        private void PublishCardDisplayRemove(InteractableCardCompartment compartment,
            InteractableCard3d card3d)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || card3d == null
                || !card3d || !_context.InGame())
            {
                return;
            }

            var card = card3d.m_Card3dUI?.m_CardUI?.GetCardData();
            if (card == null)
            {
                return;
            }

            if (!WorldCardDisplay.TryGetShelf(compartment, out var shelf, out var index))
            {
                CoopPlugin.Log.LogWarning(
                    "[card-display] removal dropped: the compartment has no owning display shelf.");
                return;
            }

            var state = WorldCardDisplay.ToState(card);
            if (state == null)
            {
                return;
            }

            var grade = WorldCardDisplay.EncodedGradeOf(card);
            CoopPlugin.Log.LogInfo("[card-display] forwarding removal shelf=" + shelf.name
                + " compartment=" + index + " card=" + card.monsterType + ".");
            WorldAction.Submit(CardDisplayPredictionKey(shelf, index),
                new CardDisplayRequestMessage
                {
                    Shelf = shelf,
                    Compartment = index,
                    Occupied = false,
                    Card = state,
                    EncodedGrade = grade,
                },
                () => RedoCardDisplayRemove(compartment, card3d),
                () => UndoCardDisplayRemove(compartment, card3d, card));
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

        /// <summary>Replays an accepted (or once-undone) removal by moving the very card object
        /// vanilla moved into the hand back out of the slot.</summary>
        private static void RedoCardDisplayRemove(InteractableCardCompartment compartment,
            InteractableCard3d card3d)
        {
            if (compartment == null || card3d == null || !card3d)
            {
                return;
            }

            WorldCardDisplay.TakeCardIntoHand(compartment, card3d);
        }

        /// <summary>Undoes a rejected removal by putting the SAME card object back on the shelf.
        /// Spawning a fresh card here duplicated the copy vanilla already moved into the hand.</summary>
        private static void UndoCardDisplayRemove(InteractableCardCompartment compartment,
            InteractableCard3d card3d, CardData card)
        {
            if (compartment == null)
            {
                return;
            }

            if (card3d != null && card3d)
            {
                if (WorldCardDisplay.ReturnHeldCardToShelf(compartment, card3d))
                {
                    return;
                }

                if (WorldCardDisplay.IsHeld(card3d))
                {
                    // Still in the hand but not returnable through the game's own teardown (the
                    // paired hand lists drifted). Leave it rather than corrupt the saved hand;
                    // the next authoritative slot state re-syncs the shelf.
                    CoopPlugin.Log.LogWarning(
                        "[card-display] removal undo skipped: the held card could not be "
                        + "returned through the game's hold teardown.");
                    return;
                }
            }

            // The object is gone or no longer held: restore the data copy so the card is not lost.
            if (card != null)
            {
                WorldCardDisplay.Clear(compartment);
                WorldCardDisplay.Place(compartment, card);
            }
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
            // Capture the exact card object, not just its data: the undo puts this same object
            // back on the shelf instead of spawning a second copy beside the held one.
            [HarmonyPrefix]
            private static void Prefix(InteractableCardCompartment __instance,
                out InteractableCard3d __state)
            {
                var stored = __instance?.m_StoredCardList;
                __state = stored != null && stored.Count > 0 ? stored[0] : null;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance,
                InteractableCard3d __state)
            {
                // Only forward when vanilla actually took the card off the shelf.
                if (__state != null && __state && WorldCardDisplay.Read(__instance) == null)
                {
                    _instance?.PublishCardDisplayRemove(__instance, __state);
                }
            }
        }
    }
}
