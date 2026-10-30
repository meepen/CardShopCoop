using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private bool _cardDisplayApplying;

        private void InstallCardDisplayPatches()
        {
            Harmony.CreateClassProcessor(typeof(CardDisplayPlacePatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CardDisplayRemovePatch)).Patch();
        }

        [MessageHandler]
        private ActionOutcome HandleCardDisplayRequest(CoopMessageContext context,
            CardDisplayRequestMessage message)
        {
            var requested = message?.State;
            if (!Context.InGame || context?.IsAuthenticated != true || requested?.Shelf == null)
            {
                return ActionOutcome.Reject("card display request is not admitted");
            }

            if (!WorldCardDisplay.TryGetCompartment(requested.Shelf, requested.Compartment,
                out var compartment))
            {
                CoopPlugin.Log.LogWarning("[card-display] dropped request: shelf="
                    + requested.Shelf.name + " has no compartment " + requested.Compartment + ".");
                return ActionOutcome.Reject("the display slot is not live");
            }

            string reason = null;
            _cardDisplayApplying = true;
            try
            {
                if (!requested.Occupied)
                {
                    // The removal must name the card the slot still holds. The host's own actors
                    // can consume the displayed card while the guest's removal is in flight (a
                    // customer buys it, a worker restocks); clearing blindly would destroy the
                    // OTHER card's state and leave the guest holding a copy the host no longer has.
                    var card = WorldCardDisplay.FromState(requested.Card);
                    if (card == null
                        || !WorldCardDisplay.Matches(compartment, card, requested.EncodedGrade))
                    {
                        reason = "the slot does not hold the named card";
                    }
                    else
                    {
                        WorldCardDisplay.Clear(compartment);
                    }
                }
                else
                {
                    var card = WorldCardDisplay.FromState(requested.Card);
                    if (card == null)
                    {
                        reason = "the placed card did not resolve";
                    }
                    else if (!WorldCardDisplay.Matches(compartment, card, requested.EncodedGrade))
                    {
                        if (compartment.m_StoredCardList != null
                            && compartment.m_StoredCardList.Count > 0)
                        {
                            WorldCardDisplay.Clear(compartment);
                        }

                        if (!WorldCardDisplay.Place(compartment, card))
                        {
                            reason = "the card could not be placed";
                        }
                    }
                }
            }
            finally
            {
                _cardDisplayApplying = false;
            }

            var authoritative = BuildCardDisplayState(compartment);
            if (authoritative == null)
            {
                return ActionOutcome.Reject(reason ?? "the display slot could not be read");
            }

            // The actor receives this same state in its response; every other peer mirrors it as
            // authoritative slot state. Publishing on a rejection too keeps a partial host apply
            // (a clear that could not be replaced) consistent everywhere.
            Context.BroadcastExcept(context.Peer, new CardDisplayMessage { State = authoritative });
            return reason == null
                ? ActionOutcome.Applied(new CardDisplayResponse { State = authoritative })
                : ActionOutcome.Reject(reason, new CardDisplayResponse { State = authoritative });
        }

        private static CardDisplayState BuildCardDisplayState(InteractableCardCompartment compartment)
        {
            if (!WorldCardDisplay.TryGetShelf(compartment, out var shelf, out var index))
            {
                return null;
            }

            var card = WorldCardDisplay.Read(compartment);
            return new CardDisplayState
            {
                Shelf = shelf,
                Compartment = index,
                Occupied = card != null,
                Card = card == null ? null : WorldCardDisplay.ToState(card),
                EncodedGrade = card == null ? -1 : WorldCardDisplay.EncodedGradeOf(card),
            };
        }

        /// <summary>Host-initiated display changes (the host player placing a card, a worker
        /// restocking, or a customer buying a displayed card) are announced so every guest mirrors
        /// them. Remote applies made through <see cref="HandleCardDisplayRequest"/> are excluded by
        /// the in-flight guard because the handler publishes the state itself.</summary>
        private void PublishHostCardDisplay(InteractableCardCompartment compartment, bool occupied)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || !RuntimeContext.InGame())
            {
                return;
            }

            var state = BuildCardDisplayState(compartment);
            if (state == null)
            {
                return;
            }

            state.Occupied = occupied;
            if (!occupied)
            {
                state.Card = null;
                state.EncodedGrade = -1;
            }

            Context.Broadcast(new CardDisplayMessage { State = state });
        }

        private void AppendCardDisplayBaseline(Action<INetMessage> append)
        {
            if (append == null)
            {
                return;
            }

            // The game's ShelfManager getters tunnel through CSingleton<ShelfManager>.Instance, which
            // fabricates an empty DontDestroyOnLoad manager when the scene has not produced one yet
            // (a scene-load window). That empty copy then shadows the real manager and every card
            // shelf lookup reads an empty list for the rest of the run. Resolve the scene object
            // only; a missing manager means this baseline has nothing to append.
            var manager = SceneRef<ShelfManager>.Get();
            if (manager == null)
            {
                return;
            }

            AppendCardDisplayShelves(append, manager.m_CardShelfList);
            AppendCardDisplayShelves(append, manager.m_CardItemCombiShelfList);
            AppendCardDisplayShelves(append, manager.m_TournamentPrizeShelfList);
        }

        private static void AppendCardDisplayShelves<T>(Action<INetMessage> append,
            IList<T> shelves) where T : CardShelf
        {
            for (var i = 0; shelves != null && i < shelves.Count; i++)
            {
                var shelf = shelves[i];
                if (shelf == null)
                {
                    continue;
                }

                var compartments = shelf.GetCardCompartmentList();
                for (var j = 0; compartments != null && j < compartments.Count; j++)
                {
                    var card = WorldCardDisplay.Read(compartments[j]);
                    if (card == null)
                    {
                        continue;
                    }

                    append(new CardDisplayMessage
                    {
                        State = new CardDisplayState
                        {
                            Shelf = shelf,
                            Compartment = j,
                            Occupied = true,
                            Card = WorldCardDisplay.ToState(card),
                            EncodedGrade = WorldCardDisplay.EncodedGradeOf(card),
                        },
                    });
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCardCompartment), "SetCardOnShelf")]
        private static class CardDisplayPlacePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance)
                => _instance?.PublishHostCardDisplay(__instance, true);
        }

        [HarmonyPatch(typeof(InteractableCardCompartment), "RemoveCardFromShelf")]
        private static class CardDisplayRemovePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance)
                => _instance?.PublishHostCardDisplay(__instance, false);
        }
    }
}
