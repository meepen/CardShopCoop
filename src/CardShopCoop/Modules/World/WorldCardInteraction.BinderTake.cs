using System;
using System.Collections.Generic;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// The hand half of a predicted binder pick. Taking a card out of the collection binder
    /// reduces the shared collection AND moves a physical card into the player's hand
    /// (CollectionBinderFlipAnimCtrl.OnRightMouseButtonUp -> ReduceCard/RemoveGradedCard, then
    /// AddHoldCard). The reduction travels as the prediction; if the host refuses it because the
    /// last copy went to another player, the prediction's undo restores the collection but the
    /// spawned hand card must be despawned too, or the player keeps a copy the shared album no
    /// longer owns - the duplication seen when two players take the same card at once.
    ///
    /// The card object cannot be captured where the prediction is registered: the game reduces
    /// the collection BEFORE it moves the card into the hand. So the two binder methods that can
    /// end in a hand card open a scope; the observer-side prediction binds itself while the scope
    /// runs, and the scope's finalizer pairs it with whatever the hold list actually gained - or
    /// the display slot vanilla auto-placed it on. A reduce that only fed a binder screen
    /// (grading selection, bulk donation, deck edit) adds nothing to the hand and leaves its
    /// binding empty, so its refusal keeps the collection-only behavior.
    ///
    /// The HOST's own take never travels as a prediction - the host is the authority and vanilla
    /// already performed it - so when the host loses the same race (a guest's accepted removal
    /// consumed the copy while the open binder still showed the slot) nothing refused it: the
    /// album went negative and the spawned hand card survived, the same duplication on the other
    /// side. <see cref="TryRefuseHostBinderTake"/>/<see cref="RefuseHostBinderTake"/> close that
    /// gap: the host observer undoes the reduction (or, for a graded row that was already gone,
    /// nothing) and marks the binding Refused, and the finalizer despawns the hand card the same
    /// way a refused client prediction does.
    /// </summary>
    internal sealed partial class WorldCardInteraction
    {
        /// <summary>One in-flight binder pick waiting for the card object the game moves into the
        /// hand. Null Card3d means this pick never produced one (a binder-screen reduction).</summary>
        private sealed class BinderTakeBinding
        {
            internal InteractableCard3d Card3d;

            /// <summary>Host only: the game took a card the shared album no longer owned (the take
            /// raced a remote removal). The reduction was undone here, and the hand card the game
            /// still spawned must be despawned when the take closes. Client refusals never set
            /// this - they despawn from the prediction's rejected callback instead.</summary>
            internal bool Refused;

            /// <summary>The card a refused host take moved, kept for the despawn log.</summary>
            internal CardData Card;

            /// <summary>Set when the card was auto-placed on a display compartment instead of
            /// staying in the hand: the slot to empty if the prediction is refused.</summary>
            internal InteractableCardCompartment Compartment;
        }

        /// <summary>One invocation of a binder method that can put a card into the hand. The held
        /// cards at scope open are kept as references: AddHoldCard only ever appends, but the same
        /// invocation can also move an OLDER card out of the hand (HideCardAlbum's
        /// EnterHoldCardMode auto-places slot 0 onto a primed display), so the new card must be
        /// found by diffing, not by index. A null list means the controller was unavailable when
        /// the scope opened; nothing can be paired, which is safer than misbinding.</summary>
        private sealed class BinderTakeScope
        {
            internal BinderTakeScope Previous;
            internal List<InteractableCard3d> HeldBefore;
            internal readonly List<BinderTakeBinding> Bindings = new();
            internal readonly List<BinderPlacement> Placements = new();
        }

        /// <summary>A card the game auto-placed on a display during a binder take (vanilla
        /// EnterHoldCardMode places the current hold card when a display slot was primed).</summary>
        private sealed class BinderPlacement
        {
            internal InteractableCard3d Card;
            internal InteractableCardCompartment Compartment;
        }

        /// <summary>One in-flight client graded removal. <see cref="Superseded"/> means the shared
        /// album row is already accounted for - the local removal changed nothing (a stale slot
        /// whose certificate left first) or another player's accepted removal consumed the same
        /// row - so neither the rollback nor a follower replay may touch the row again.</summary>
        private sealed class GradedRemovalGuard
        {
            internal Guid ActionId;
            internal bool Superseded;
        }

        private static BinderTakeScope _binderTakeScope;

        // Keyed by CardPriceKey (certificate included when Grading Overhaul encodes it). Only
        // client predictions register here; the host never predicts.
        private static readonly Dictionary<string, List<GradedRemovalGuard>> _gradedRemovalGuards =
            new(StringComparer.Ordinal);

        /// <summary>Opens one binder interaction that can end with a card in the hand. Called from
        /// a prefix so the held cards are known before vanilla runs; nesting is saved and restored
        /// by the scope.</summary>
        internal static void BeginBinderTake()
        {
            _binderTakeScope = new BinderTakeScope
            {
                Previous = _binderTakeScope,
                HeldBefore = WorldCardDisplay.HeldCards() is { } held
                    ? new List<InteractableCard3d>(held) : null,
            };
        }

        /// <summary>Closes the scope and pairs each waiting binding with one held card that was not
        /// in the hand when the scope opened, in order. Runs from a finalizer, so an exception in
        /// vanilla cannot leave the scope latched (an interrupted take simply leaves its bindings
        /// empty).</summary>
        internal static void EndBinderTake()
        {
            var scope = _binderTakeScope;
            if (scope == null)
            {
                return;
            }

            _binderTakeScope = scope.Previous;
            if (scope.Bindings.Count == 0 || scope.HeldBefore == null)
            {
                return;
            }

            var held = WorldCardDisplay.HeldCards();
            if (held == null)
            {
                return;
            }

            // The new card(s) are the held cards that were not there before. Scanning forward
            // keeps the append order; skipping pre-existing references keeps the pairing correct
            // when the same invocation also moved an older card out of the hand.
            var candidate = 0;
            for (var i = 0; i < scope.Bindings.Count; i++)
            {
                while (candidate < held.Count && scope.HeldBefore.Contains(held[candidate]))
                {
                    candidate++;
                }

                if (candidate >= held.Count)
                {
                    break;
                }

                scope.Bindings[i].Card3d = held[candidate];
                candidate++;
            }

            // A card auto-placed on a primed display never enters the held list. Pair the
            // placements recorded for this scope with the bindings the held diff left empty.
            var placedIndex = 0;
            for (var i = 0;
                i < scope.Bindings.Count && placedIndex < scope.Placements.Count; i++)
            {
                var binding = scope.Bindings[i];
                if (binding.Card3d != null)
                {
                    continue;
                }

                var placement = scope.Placements[placedIndex++];
                binding.Card3d = placement.Card;
                binding.Compartment = placement.Compartment;
            }

            // A refused HOST take spawned a hand card the album does not own (see
            // TryRefuseHostBinderTake): remove it through the game's own hold teardown now, the
            // same way a refused client prediction despawns from its rejected callback. Client
            // bindings never carry Refused, so this loop only ever runs on the host.
            for (var i = 0; i < scope.Bindings.Count; i++)
            {
                var binding = scope.Bindings[i];
                if (binding.Refused)
                {
                    DespawnRejectedBinderTake(binding, binding.Card);
                }
            }
        }

        /// <summary>Client: remembers an in-flight binder pick so its refusal can despawn the exact
        /// card the game moved into the hand. Null outside a binder method, so unrelated removals
        /// (donation and deck screens, trades, bulk actions) never touch the hand.</summary>
        private static BinderTakeBinding CreateBinderTakeBinding()
        {
            var scope = _binderTakeScope;
            if (scope == null)
            {
                return null;
            }

            var binding = new BinderTakeBinding();
            scope.Bindings.Add(binding);
            return binding;
        }

        /// <summary>Host: the game performed a binder take on a card the shared album no longer
        /// owns. Vanilla's take reads the slot straight off the open binder and reduces without
        /// checking ownership (CollectionBinderFlipAnimCtrl.OnRightMouseButtonUp), so when a
        /// remote removal had already consumed the copy - the binder still showed the slot until
        /// the end-of-frame mirror hid it - the host reduced the album below zero and STILL
        /// spawned a hand card. The host never predicts its own take, so nothing else would
        /// refuse it: both players keep a copy, the reported duplication. Undo the reduction
        /// through the game path and remember the take so <see cref="EndBinderTake"/> despawns
        /// the spawned card. Returns false outside a binder take or when the reduction was
        /// legitimate, so the caller forwards normally.</summary>
        internal static bool TryRefuseHostBinderTake(CardData card, int amount)
        {
            if (_binderTakeScope == null || card == null || amount <= 0 || card.cardGrade > 0)
            {
                return false;
            }

            // A take is legitimate while the album still held the copy: the count lands on 0
            // (the last copy) or above. Only a reduction that drove it negative means the copy
            // was already gone - ReduceCard subtracts without a floor.
            if (CPlayerData.GetCardAmount(card) >= 0)
            {
                return false;
            }

            // Restore the album exactly as the client's refused prediction does, behind the
            // remote-apply guard so the correction is not forwarded as a fresh gain.
            ApplyPredictedCardDelta(card, amount, isAdd: true);
            CancelPrimedDisplayPlacement();
            MarkHostBinderTakeRefused(card);
            CoopPlugin.Log.LogWarning("binder take refused on the host: " + CardIdent(card)
                + " was already taken by another player; the album was restored and the hand "
                + "card will be removed.");
            return true;
        }

        /// <summary>Host: a graded binder take matched no album row (the certificate was already
        /// consumed by a remote removal). There is nothing to undo - the album never lost a row -
        /// but vanilla still spawned a hand card, so remember the take for despawn.</summary>
        internal static bool RefuseHostBinderTake(CardData card)
        {
            if (_binderTakeScope == null || card == null)
            {
                return false;
            }

            CancelPrimedDisplayPlacement();
            MarkHostBinderTakeRefused(card);
            CoopPlugin.Log.LogWarning("graded binder take refused on the host: " + CardIdent(card)
                + " had no album row left; the spawned hand card will be removed.");
            return true;
        }

        /// <summary>Vanilla HideCardAlbum calls ReduceCard/RemoveGradedCard, then AddHoldCard and
        /// EnterHoldCardMode. With a primed display compartment, EnterHoldCardMode places the
        /// spawned card and removes it from the held list before EndBinderTake can despawn it.
        /// Clearing the flag (which also nulls the compartment) keeps the card held.</summary>
        private static void CancelPrimedDisplayPlacement()
            => SceneRef<InteractionPlayerController>.Get()?.SetIsPuttingCardOnDisplay(false);

        private static void MarkHostBinderTakeRefused(CardData card)
        {
            var binding = CreateBinderTakeBinding();
            if (binding == null)
            {
                return;
            }

            binding.Refused = true;
            binding.Card = SnapshotCard(card);
        }

        /// <summary>Records a card the game auto-placed on a display while a binder take was open.
        /// Only cards that were not already in the hand at scope start can be this take:
        /// EnterHoldCardMode places slot 0, which may be an older held card.</summary>
        internal static void RecordBinderPlacement(InteractableCardCompartment compartment,
            InteractableCard3d card)
        {
            var scope = _binderTakeScope;
            if (scope == null || scope.HeldBefore == null || compartment == null || card == null
                || scope.Bindings.Count == 0 || scope.HeldBefore.Contains(card))
            {
                return;
            }

            for (var i = 0; i < scope.Placements.Count; i++)
            {
                if (ReferenceEquals(scope.Placements[i].Card, card))
                {
                    return;
                }
            }

            scope.Placements.Add(new BinderPlacement
            {
                Card = card,
                Compartment = compartment,
            });
        }

        /// <summary>Client: the host refused a binder pick - remove the card the game spawned
        /// into the hand through the game's own teardown, or empty the display slot when vanilla
        /// auto-placed it there. A card the player moved somewhere else is left alone: its
        /// destination's own sync owns it now.</summary>
        private static void DespawnRejectedBinderTake(BinderTakeBinding binding, CardData card)
        {
            if (binding == null)
            {
                return;
            }

            var card3d = binding.Card3d;
            binding.Card3d = null;
            if (card3d == null || !card3d)
            {
                return;
            }

            if (!WorldCardDisplay.IsHeld(card3d))
            {
                DespawnPlacedBinderTake(binding, card3d, card);
                return;
            }

            if (!WorldCardDisplay.DespawnHeldCard(card3d))
            {
                CoopPlugin.Log.LogWarning("binder pick refused, but its card could not be removed "
                    + "from the hand through the game's hold teardown; the player may keep a copy "
                    + "the album no longer owns.");
                return;
            }

            CoopPlugin.Log.LogInfo("binder pick refused: removed " + CardIdent(card)
                + " from the hand.");
        }

        /// <summary>Client: a refused take was auto-placed on a display slot (vanilla
        /// EnterHoldCardMode's primed-display branch). Empty that slot through the compartment's
        /// own teardown, then destroy the card object and return its UI.</summary>
        private static void DespawnPlacedBinderTake(BinderTakeBinding binding,
            InteractableCard3d card3d, CardData card)
        {
            var compartment = binding.Compartment;
            binding.Compartment = null;
            if (compartment == null || compartment.m_StoredCardList == null
                || compartment.m_StoredCardList.Count == 0
                || !ReferenceEquals(compartment.m_StoredCardList[0], card3d))
            {
                // The player moved it after the take; that destination owns it now.
                return;
            }

            compartment.RemoveCardFromShelf(null, null);
            card3d.OnDestroyed();
            CoopPlugin.Log.LogInfo("binder pick refused: removed " + CardIdent(card)
                + " from a display slot.");
        }

        /// <summary>Client: tracks one in-flight graded removal so a competing accepted removal of
        /// the same certificate can mark its rollback. The prediction id only exists after the
        /// send succeeded, so the guard is registered here.</summary>
        private static void RegisterGradedRemovalGuard(Guid actionId, GradedRemovalGuard guard,
            CardData card)
        {
            var key = CardPriceKey(card);
            if (key == null)
            {
                return;
            }

            guard.ActionId = actionId;
            if (!_gradedRemovalGuards.TryGetValue(key, out var guards))
            {
                guards = new List<GradedRemovalGuard>();
                _gradedRemovalGuards.Add(key, guards);
            }

            guards.Add(guard);
        }

        /// <summary>Client: another player's accepted removal of this card was applied. When the
        /// apply removed nothing locally, the row it consumed was one this client had already
        /// taken itself: mark exactly ONE still-open guard, so that pick's rollback does not put
        /// the row back. Marking one (not all) keeps the row count right when several same-key
        /// picks are in flight; a vanilla album can carry several same-grade rows of one card.
        /// When the apply did remove a local row, it consumed a different copy and no open take
        /// needs marking.</summary>
        internal static void MarkRemoteGradedRemoval(CardData card)
        {
            var key = CardPriceKey(card);
            if (key == null || !_gradedRemovalGuards.TryGetValue(key, out var guards))
            {
                return;
            }

            for (var i = 0; i < guards.Count; i++)
            {
                if (guards[i].Superseded)
                {
                    continue;
                }

                guards[i].Superseded = true;
                return;
            }
        }

        /// <summary>Drops the guards of a prediction that left the tracker (accepted echo,
        /// refusal, or teardown), keeping the registry bounded by in-flight removals.</summary>
        internal static void OnActionSettled(Guid actionId)
        {
            if (actionId == Guid.Empty || _gradedRemovalGuards.Count == 0)
            {
                return;
            }

            List<string> emptiedKeys = null;
            foreach (var entry in _gradedRemovalGuards)
            {
                var guards = entry.Value;
                for (var i = guards.Count - 1; i >= 0; i--)
                {
                    if (guards[i].ActionId == actionId)
                    {
                        guards.RemoveAt(i);
                    }
                }

                if (guards.Count == 0)
                {
                    if (emptiedKeys == null)
                    {
                        emptiedKeys = new List<string>();
                    }

                    emptiedKeys.Add(entry.Key);
                }
            }

            if (emptiedKeys == null)
            {
                return;
            }

            for (var i = 0; i < emptiedKeys.Count; i++)
            {
                _gradedRemovalGuards.Remove(emptiedKeys[i]);
            }
        }

        /// <summary>World teardown: no prediction can survive the reset, so neither can its
        /// binding or guard.</summary>
        private static void ClearBinderTakeState()
        {
            _binderTakeScope = null;
            _gradedRemovalGuards.Clear();
        }
    }
}
