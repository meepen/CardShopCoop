using System;
using System.Collections.Generic;
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
            _harmony.CreateClassProcessor(typeof(CardDisplayPlacePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CardDisplayRemovePatch)).Patch();
        }

        [MessageHandler]
        private void HandleCardDisplayRequest(MessageContext context, CardDisplayRequestMessage message)
        {
            if (!RuntimeContext.InGame() || !IsJoinPhaseSender(context) || message == null)
            {
                RejectWorldIntent(context, message);
                return;
            }

            if (message.Shelf == null)
            {
                CoopPlugin.Log.LogWarning("[card-display] dropped request: the display shelf did not "
                    + "resolve to a live object.");
                RejectWorldIntent(context, message);
                return;
            }

            var echo = default(CardDisplayMessage);
            var accepted = ExecuteWorldCommand(context, message, () =>
            {
                if (!WorldCardDisplay.TryGetCompartment(message.Shelf, message.Compartment,
                    out var compartment))
                {
                    CoopPlugin.Log.LogWarning("[card-display] dropped request: shelf="
                        + message.Shelf.name + " has no compartment " + message.Compartment + ".");
                    return false;
                }

                _cardDisplayApplying = true;
                try
                {
                    if (!message.Occupied)
                    {
                        // The removal must name the card the slot still holds. The host's own
                        // actors can consume the displayed card while the guest's removal is in
                        // flight (a customer buys it, a worker restocks); clearing blindly would
                        // destroy the OTHER card's state and leave the guest holding a copy the
                        // host no longer has.
                        var requested = WorldCardDisplay.FromState(message.Card);
                        if (requested == null
                            || !WorldCardDisplay.Matches(compartment, requested,
                                message.EncodedGrade))
                        {
                            CoopPlugin.Log.LogWarning("[card-display] rejected removal shelf="
                                + message.Shelf.name + " compartment=" + message.Compartment
                                + ": the slot does not hold the named card.");
                            return false;
                        }

                        WorldCardDisplay.Clear(compartment);
                    }
                    else
                    {
                        var card = WorldCardDisplay.FromState(message.Card);
                        if (card == null)
                        {
                            return false;
                        }

                        if (WorldCardDisplay.Matches(compartment, card, message.EncodedGrade))
                        {
                            // Already the authoritative state (resend of a confirmed placement).
                        }
                        else
                        {
                            if (compartment.m_StoredCardList.Count > 0)
                            {
                                WorldCardDisplay.Clear(compartment);
                            }

                            if (!WorldCardDisplay.Place(compartment, card))
                            {
                                return false;
                            }
                        }
                    }
                }
                finally
                {
                    _cardDisplayApplying = false;
                }

                echo = new CardDisplayMessage
                {
                    ActionId = message.ActionId,
                    Shelf = message.Shelf,
                    Compartment = message.Compartment,
                    Occupied = message.Occupied,
                    Card = message.Occupied ? message.Card : null,
                    EncodedGrade = message.Occupied ? message.EncodedGrade : -1,
                };
                return true;
            });

            if (accepted && echo != null)
            {
                BroadcastWorld(echo);
            }
            else if (!accepted
                && WorldCardDisplay.TryGetCompartment(message.Shelf, message.Compartment,
                    out var rejectedCompartment))
            {
                // ExecuteWorldCommand has already sent the rollback. Sending the authority after
                // it means the client first returns its optimistic card through the action's undo
                // and then applies the host's real slot state; the other order retires the action
                // first and would lose the guest's card.
                RefreshRejectedCardDisplay(context, message, rejectedCompartment);
            }
        }

        /// <summary>After a refusal, tell the requester the slot's real state so the rollback's
        /// restored card is replaced by authority rather than leaving a ghost on the shelf.</summary>
        private void RefreshRejectedCardDisplay(MessageContext context,
            CardDisplayRequestMessage request, InteractableCardCompartment compartment)
        {
            if (context?.Connection == null || request == null || compartment == null
                || !WorldCardDisplay.TryGetShelf(compartment, out var shelf, out var index))
            {
                return;
            }

            var card = WorldCardDisplay.Read(compartment);
            SendWorldTo(context.ConnectionId, new CardDisplayMessage
            {
                ActionId = request.ActionId,
                Shelf = shelf,
                Compartment = index,
                Occupied = card != null,
                Card = card == null ? null : WorldCardDisplay.ToState(card),
                EncodedGrade = card == null ? -1 : WorldCardDisplay.EncodedGradeOf(card),
            });
        }

        /// <summary>Host-initiated display changes (the host player placing a card, a worker
        /// restocking, or a customer buying a displayed card) are announced so every guest mirrors
        /// them. Remote applies made through <see cref="HandleCardDisplayRequest"/> are excluded by
        /// the in-flight guard because the handler publishes the prediction-stamped echo itself.</summary>
        private void PublishHostCardDisplay(InteractableCardCompartment compartment, bool occupied)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || !RuntimeContext.InGame()
                || !WorldCardDisplay.TryGetShelf(compartment, out var shelf, out var index))
            {
                return;
            }

            var card = occupied ? WorldCardDisplay.Read(compartment) : null;
            var grade = occupied ? WorldCardDisplay.EncodedGradeOf(card) : -1;
            BroadcastWorld(new CardDisplayMessage
            {
                Shelf = shelf,
                Compartment = index,
                Occupied = occupied,
                Card = occupied ? WorldCardDisplay.ToState(card) : null,
                EncodedGrade = grade,
            });
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
                    var compartment = compartments[j];
                    var card = WorldCardDisplay.Read(compartment);
                    if (card == null)
                    {
                        continue;
                    }

                    append(new CardDisplayMessage
                    {
                        Shelf = shelf,
                        Compartment = j,
                        Occupied = true,
                        Card = WorldCardDisplay.ToState(card),
                        EncodedGrade = WorldCardDisplay.EncodedGradeOf(card),
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
