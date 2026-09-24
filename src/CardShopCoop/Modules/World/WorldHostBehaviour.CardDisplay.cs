using System;
using System.Collections.Generic;
using CardShopCoop.Net;
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

        [MessageHandler(typeof(CardDisplayRequestMessage))]
        private void HandleCardDisplayRequest(MessageContext context, CardDisplayRequestMessage message)
        {
            if (!_context.InGame() || !IsFullyJoinedSender(context) || message == null)
            {
                RejectWorldIntent(context, message);
                return;
            }

            var echo = default(CardDisplayMessage);
            var accepted = ExecuteWorldCommand(context, message, () =>
            {
                if (!WorldCardDisplay.TryResolve(message.ShelfKey, message.Compartment,
                    out var compartment))
                {
                    return false;
                }

                _cardDisplayApplying = true;
                try
                {
                    if (!message.Occupied)
                    {
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
                    PredictionId = message.PredictionId,
                    ShelfKey = message.ShelfKey,
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
        }

        /// <summary>Host-initiated display changes (the host player placing a card, a worker
        /// restocking, or a customer buying a displayed card) are announced so every guest mirrors
        /// them. Remote applies made through <see cref="HandleCardDisplayRequest"/> are excluded by
        /// the in-flight guard because the handler publishes the prediction-stamped echo itself.</summary>
        private void PublishHostCardDisplay(InteractableCardCompartment compartment, bool occupied)
        {
            if (_shutdown || _cardDisplayApplying || compartment == null || !_context.InGame()
                || !WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                return;
            }

            var card = occupied ? WorldCardDisplay.Read(compartment) : null;
            var grade = occupied ? WorldCardDisplay.EncodedGradeOf(card) : -1;
            BroadcastWorld(new CardDisplayMessage
            {
                ShelfKey = shelfKey,
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

            AppendCardDisplayShelves(append, ShelfManager.GetCardShelfList());
            AppendCardDisplayShelves(append, ShelfManager.GetCardItemCombiShelfList());
            AppendCardDisplayShelves(append, ShelfManager.GetTournamentPrizeShelfList());
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

                var kind = PlacementInterop.FindKind(shelf);
                if (kind < 0 || !PlacementApi.TryMakeObjectKey(kind, shelf, out var shelfKey))
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
                        ShelfKey = shelfKey,
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
