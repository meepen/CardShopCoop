using CardShopCoop.Api;
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
        private void HandleCardDisplay(CoopMessageContext context, CardDisplayMessage message)
        {
            if (_shutdown || message?.State == null)
            {
                return;
            }

            ApplyCardDisplayState(message.State);
        }

        /// <summary>Applies one authoritative slot state. The host excludes the actor from this
        /// broadcast, so the actor's own prediction is settled by its action response instead.</summary>
        private void ApplyCardDisplayState(CardDisplayState state)
        {
            if (state?.Shelf == null
                || !WorldCardDisplay.TryGetCompartment(state.Shelf, state.Compartment,
                    out var compartment))
            {
                if (state?.Shelf != null)
                {
                    CoopPlugin.Log.LogWarning("[card-display] dropped state: shelf="
                        + state.Shelf.name + " has no compartment " + state.Compartment + ".");
                }
                return;
            }

            _cardDisplayApplying = true;
            try
            {
                if (!state.Occupied)
                {
                    WorldCardDisplay.Clear(compartment);
                    return;
                }

                var card = WorldCardDisplay.FromState(state.Card);
                if (card == null
                    || WorldCardDisplay.Matches(compartment, card, state.EncodedGrade))
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
            if (_shutdown || _cardDisplayApplying || compartment == null || !RuntimeContext.InGame())
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
            // The local game already placed the card, so the prediction has nothing to apply; only
            // a rejection reverses it, and the response carries the host's authoritative slot.
            Context.Send(new CardDisplayRequestMessage
            {
                State = new CardDisplayState
                {
                    Shelf = shelf,
                    Compartment = index,
                    Occupied = true,
                    Card = state,
                    EncodedGrade = grade,
                },
                LocalCompartment = compartment,
                LocalCard = WorldCardInteraction.SnapshotCard(card),
            });
        }

        private void PublishCardDisplayRemove(InteractableCardCompartment compartment,
            InteractableCard3d card3d)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || card3d == null
                || !card3d || !RuntimeContext.InGame())
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
            Context.Send(new CardDisplayRequestMessage
            {
                State = new CardDisplayState
                {
                    Shelf = shelf,
                    Compartment = index,
                    Occupied = false,
                    Card = state,
                    EncodedGrade = grade,
                },
                LocalCompartment = compartment,
                LocalCard3d = card3d,
                LocalCard = WorldCardInteraction.SnapshotCard(card),
            });
        }

        internal static void UndoCardDisplayPrediction(CardDisplayRequestMessage message)
            => _instance?.UndoCardDisplayPrediction(message, merged: false);

        internal static void MergeCardDisplayPrediction(CardDisplayRequestMessage message,
            ActionResponseMessage response)
            => _instance?.UndoCardDisplayPrediction(message, merged: true,
                response as CardDisplayResponse);

        /// <summary>Reverses a rejected display prediction, then (on merge) applies the host's
        /// authoritative slot. A rejected placement returns the card to the shared collection; a
        /// rejected removal puts the exact held object back on the shelf.</summary>
        private void UndoCardDisplayPrediction(CardDisplayRequestMessage message, bool merged,
            CardDisplayResponse response = null)
        {
            if (message == null)
            {
                return;
            }

            if (!merged)
            {
                var compartment = message.LocalCompartment;
                if (compartment == null)
                {
                    return;
                }

                _cardDisplayApplying = true;
                try
                {
                    if (message.State?.Occupied == true)
                    {
                        WorldCardDisplay.Clear(compartment);
                        var card = message.LocalCard
                            ?? WorldCardDisplay.FromState(message.State.Card);
                        if (card != null)
                        {
                            using (_cardInteraction?.BeginRemoteCardApply())
                            {
                                CPlayerData.AddCard(card, 1);
                            }
                        }
                    }
                    else
                    {
                        UndoCardDisplayRemoval(compartment, message);
                    }
                }
                finally
                {
                    _cardDisplayApplying = false;
                }
            }

            if (response?.State != null)
            {
                ApplyCardDisplayState(response.State);
            }
        }

        private static void UndoCardDisplayRemoval(InteractableCardCompartment compartment,
            CardDisplayRequestMessage message)
        {
            var card3d = message.LocalCard3d;
            if (card3d != null && card3d)
            {
                if (WorldCardDisplay.ReturnHeldCardToShelf(compartment, card3d))
                {
                    return;
                }

                if (WorldCardDisplay.IsHeld(card3d))
                {
                    // Still in the hand but not returnable through the game's own teardown (the
                    // paired hand lists drifted). Leave it rather than corrupt the saved hand; the
                    // response state re-syncs the slot.
                    CoopPlugin.Log.LogWarning(
                        "[card-display] removal undo skipped: the held card could not be "
                        + "returned through the game's hold teardown.");
                    return;
                }
            }

            // The object is gone or no longer held: restore the data copy so the card is not lost.
            var card = message.LocalCard ?? WorldCardDisplay.FromState(message.State?.Card);
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
