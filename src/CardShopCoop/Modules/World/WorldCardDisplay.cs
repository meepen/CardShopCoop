using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Shared, build-neutral operations for the single-card display slots on card shelves
    /// and tables. Mirrors the game's own save-loader recipe for placing a card and the vanilla
    /// purchase teardown for clearing one, so a remote apply produces exactly what the acting
    /// player's game produced.</summary>
    internal static class WorldCardDisplay
    {
        /// <summary>Compartment index is carried as an int; cap it so a malformed host/guest cannot
        /// walk an unbounded shelf list.</summary>
        internal const int MaxCompartments = 256;

        /// <summary>Returns the live shelf that owns a display compartment plus the compartment's
        /// index in that shelf's list. Both peers address the slot by those live values only.</summary>
        internal static bool TryGetShelf(InteractableCardCompartment compartment, out CardShelf shelf,
            out int compartmentIndex)
        {
            shelf = null;
            compartmentIndex = -1;
            if (compartment == null)
            {
                return false;
            }

            shelf = compartment.GetCardShelf();
            var compartments = shelf?.GetCardCompartmentList();
            if (compartments == null)
            {
                return false;
            }

            compartmentIndex = compartments.IndexOf(compartment);
            return compartmentIndex >= 0 && compartmentIndex < MaxCompartments;
        }

        /// <summary>Resolves a compartment inside an already-live display shelf. A null shelf or an
        /// out-of-range index is an unresolved entity, never a reason to search for a fallback.</summary>
        internal static bool TryGetCompartment(CardShelf shelf, int compartmentIndex,
            out InteractableCardCompartment compartment)
        {
            compartment = null;
            if (shelf == null || compartmentIndex < 0 || compartmentIndex >= MaxCompartments)
            {
                return false;
            }

            var compartments = shelf.GetCardCompartmentList();
            if (compartments == null || compartmentIndex >= compartments.Count)
            {
                return false;
            }

            compartment = compartments[compartmentIndex];
            return compartment != null;
        }

        // Compatibility bridge for the Pricing module, whose own wire intent still carries a
        // placement key. World card-display messages do not use these; they carry the live shelf.
        internal static bool TryMakeKey(InteractableCardCompartment compartment, out int shelfKey,
            out int compartmentIndex)
        {
            shelfKey = 0;
            compartmentIndex = -1;
            if (compartment == null)
            {
                return false;
            }

            var shelf = compartment.GetCardShelf();
            if (shelf == null)
            {
                return false;
            }

            var kind = PlacementInterop.FindKind(shelf);
            if (kind < 0 || !PlacementApi.TryMakeObjectKey(kind, shelf, out shelfKey))
            {
                return false;
            }

            var compartments = shelf.GetCardCompartmentList();
            if (compartments == null)
            {
                return false;
            }

            compartmentIndex = compartments.IndexOf(compartment);
            return compartmentIndex >= 0 && compartmentIndex < MaxCompartments;
        }

        internal static bool TryResolve(int shelfKey, int compartmentIndex,
            out InteractableCardCompartment compartment)
        {
            compartment = null;
            return PlacementApi.ResolveObjectByKey(shelfKey) is CardShelf shelf
                && TryGetCompartment(shelf, compartmentIndex, out compartment);
        }

        /// <summary>The card currently displayed in a slot, or null when the slot is empty or its
        /// card UI has not been built yet.</summary>
        internal static CardData Read(InteractableCardCompartment compartment)
        {
            var stored = compartment?.m_StoredCardList;
            if (stored == null || stored.Count == 0 || stored[0] == null
                || stored[0].m_Card3dUI?.m_CardUI == null)
            {
                return null;
            }

            return stored[0].m_Card3dUI.m_CardUI.GetCardData();
        }

        /// <summary>Content+grade equality against the slot that is ALREADY addressed by its
        /// stable (shelfKey, compartment). This is only an idempotency/verification check for a
        /// slot the caller resolved by key - never a search that identifies the slot itself.
        /// The placed card's content is a legitimate fungible key; the slot identity is the key.</summary>
        internal static bool Matches(InteractableCardCompartment compartment, CardData card,
            int encodedGrade)
        {
            var current = Read(compartment);
            if (current == null || card == null)
            {
                return false;
            }

            return current.expansionType == card.expansionType
                && current.monsterType == card.monsterType
                && current.borderType == card.borderType
                && current.isFoil == card.isFoil
                && current.isDestiny == card.isDestiny
                && current.isChampionCard == card.isChampionCard
                && EncodedGradeOf(current) == encodedGrade;
        }

        internal static int EncodedGradeOf(CardData card)
            => card == null ? -1 : (GradingApi.Present ? GradingApi.Encoded(card) : card.cardGrade);

        internal static BoxCardState ToState(CardData card)
            => card == null ? null : BoxNetworkInteraction.ToState(card);

        internal static CardData FromState(BoxCardState state)
            => state == null ? null : BoxNetworkInteraction.FromState(state);

        /// <summary>Empties a display slot the way the vanilla purchase path does. Calling only
        /// <c>DisableAllCard</c> despawns the card but leaves the slot's price tag showing the sold
        /// card forever, so the price-tag bookkeeping is mirrored here too.</summary>
        internal static void Clear(InteractableCardCompartment compartment)
        {
            if (compartment == null)
            {
                return;
            }

            compartment.DisableAllCard();
            compartment.m_StoredCardList?.Clear();

            if (compartment.m_InteractablePriceTagList != null)
            {
                for (var i = 0; i < compartment.m_InteractablePriceTagList.Count; i++)
                {
                    compartment.m_InteractablePriceTagList[i]?.SetPriceChecked(false);
                }
            }

            compartment.SetPriceTagCardData(null);
            compartment.SetPriceTagVisibility(false);
            var shelf = compartment.GetCardShelf();
            if (shelf != null && shelf.m_ElectronicCardListener != null)
            {
                shelf.m_ElectronicCardListener.UpdateCardUI(null);
            }
        }

        /// <summary>Recreates the card 3d + UI through the game's own objects and displays it, the
        /// same recipe the save loader uses. The caller must have cleared the slot first.</summary>
        internal static bool Place(InteractableCardCompartment compartment, CardData card)
        {
            if (compartment == null || card == null || compartment.m_StoredCardList == null
                || compartment.m_StoredCardList.Count > 0)
            {
                return false;
            }

            var spawner = SceneRef<Card3dUISpawner>.Get();
            if (spawner == null)
            {
                CoopPlugin.Log.LogWarning("card display: Card3dUISpawner is unavailable.");
                return false;
            }

            Card3dUIGroup cardUI = null;
            InteractableCard3d card3d = null;
            try
            {
                cardUI = spawner.GetCardUI();
                card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d)
                    ?.GetComponent<InteractableCard3d>();
                if (cardUI == null || card3d == null)
                {
                    return false;
                }

                cardUI.m_IgnoreCulling = true;
                cardUI.m_CardUI.SetFoilCullListVisibility(true);
                cardUI.SetSimplifyCardDistanceCull(false);
                cardUI.m_CardUI.ResetFarDistanceCull();
                if (card.cardGrade > 10)
                {
                    // A reconstructed display card carries the ENCODED grade. Register the
                    // certificate like every other reconstruction path, or Grading Overhaul
                    // renders the bare grade and the slot keeps failing Matches -> clear/replace
                    // churn. No-op when Grading Overhaul is absent.
                    GradingApi.Remember(card);
                }

                cardUI.m_CardUI.SetCardUI(card);
                cardUI.transform.position = card3d.transform.position;
                cardUI.transform.rotation = card3d.transform.rotation;
                card3d.SetCardUIFollow(cardUI);
                card3d.SetEnableCollision(false);
                compartment.SetCardOnShelf(card3d);
                cardUI.m_IgnoreCulling = false;
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("card display: could not place " + card.monsterType
                    + ": " + error.Message);
                if (card3d != null)
                {
                    UnityEngine.Object.Destroy(card3d.gameObject);
                }

                if (cardUI != null)
                {
                    UnityEngine.Object.Destroy(cardUI.gameObject);
                }

                return false;
            }
        }

        private static readonly FieldInfo HoldingCardsField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingCard3dList");

        /// <summary>True while the game's own hand list still contains this exact card object.</summary>
        internal static bool IsHeld(InteractableCard3d card3d)
        {
            if (card3d == null || !card3d || HoldingCardsField == null)
            {
                return false;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            var held = controller == null
                ? null : HoldingCardsField.GetValue(controller) as List<InteractableCard3d>;
            return held != null && held.Contains(card3d);
        }

        /// <summary>The game's live held-card object list, or null when the controller or its
        /// field is unavailable.</summary>
        internal static List<InteractableCard3d> HeldCards()
        {
            if (HoldingCardsField == null)
            {
                return null;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            return controller == null
                ? null : HoldingCardsField.GetValue(controller) as List<InteractableCard3d>;
        }

        /// <summary>Removes one exact held card and destroys it through the game's own teardown
        /// (InteractionPlayerController.RemoveCurrentCard, then InteractableCard3d.OnDestroyed -
        /// the trash-card path's sequence), for state a prediction is taking back (a refused
        /// binder pick). The two hand lists are index-aligned AND persisted, so the card is moved
        /// to slot 0 in both before RemoveCurrentCard runs; if they have drifted apart, refuse
        /// rather than remove the wrong card's saved data.</summary>
        internal static bool DespawnHeldCard(InteractableCard3d card3d)
        {
            if (card3d == null || !card3d)
            {
                return false;
            }

            var held = HeldCards();
            var index = held?.IndexOf(card3d) ?? -1;
            if (held == null || index < 0)
            {
                return false;
            }

            var heldData = CPlayerData.m_HoldCardDataList;
            if (heldData == null || heldData.Count != held.Count)
            {
                CoopPlugin.Log.LogWarning("card despawn: held-card lists are out of sync (objects="
                    + held.Count + ", data=" + (heldData?.Count ?? -1)
                    + "); refusing to remove the card through RemoveCurrentCard.");
                return false;
            }

            if (index != 0)
            {
                held.RemoveAt(index);
                held.Insert(0, card3d);
                var paired = heldData[index];
                heldData.RemoveAt(index);
                heldData.Insert(0, paired);
            }

            InteractionPlayerController.RemoveCurrentCard();
            card3d.OnDestroyed();
            return true;
        }

        /// <summary>Moves the exact held card object back onto a display slot through the game's
        /// own hold teardown, instead of spawning a second card.</summary>
        internal static bool ReturnHeldCardToShelf(InteractableCardCompartment compartment,
            InteractableCard3d card3d)
        {
            if (compartment == null || card3d == null || !card3d || HoldingCardsField == null)
            {
                return false;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null)
            {
                return false;
            }

            var held = HoldingCardsField.GetValue(controller) as List<InteractableCard3d>;
            var index = held?.IndexOf(card3d) ?? -1;
            if (held == null || index < 0)
            {
                return false;
            }

            // RemoveCurrentCard tears down slot 0 of BOTH m_CurrentHoldingCard3dList and the
            // index-aligned, persisted CPlayerData.m_HoldCardDataList. Move this card to slot 0 in
            // both lists or the game removes the wrong card's saved data: the hand then shows one
            // card while the save rebuilds a different one after the next load. If the two lists
            // have drifted apart, refuse rather than corrupt the save - the caller falls back.
            var heldData = CPlayerData.m_HoldCardDataList;
            if (heldData == null || heldData.Count != held.Count)
            {
                CoopPlugin.Log.LogWarning("card display: held-card lists are out of sync (objects="
                    + held.Count + ", data=" + (heldData?.Count ?? -1)
                    + "); refusing to return the card through RemoveCurrentCard.");
                return false;
            }

            if (index != 0)
            {
                held.RemoveAt(index);
                held.Insert(0, card3d);
                var paired = heldData[index];
                heldData.RemoveAt(index);
                heldData.Insert(0, paired);
            }

            InteractionPlayerController.RemoveCurrentCard();
            if (compartment.m_StoredCardList != null && compartment.m_StoredCardList.Count > 0)
            {
                Clear(compartment);
            }

            compartment.SetCardOnShelf(card3d);
            return true;
        }

        /// <summary>Replays a removal by moving a stored card into the hand exactly like vanilla
        /// <c>OnRightMouseButtonUp</c> does.</summary>
        internal static bool TakeCardIntoHand(InteractableCardCompartment compartment,
            InteractableCard3d card3d)
        {
            if (compartment == null || card3d == null || !card3d
                || compartment.m_StoredCardList == null
                || !compartment.m_StoredCardList.Contains(card3d)
                || !InteractionPlayerController.HasEnoughSlotToHoldCard())
            {
                return false;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null)
            {
                return false;
            }

            InteractionPlayerController.AddHoldCard(card3d);
            controller.EnterHoldCardMode();
            compartment.RemoveCardFromShelf(null, null);
            return true;
        }
    }
}
