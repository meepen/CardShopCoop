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
    /// from what the game actually did.
    ///
    /// A mutation the MOD made itself runs behind WorldCardInteraction.ApplyingRemoteCards; the
    /// game's own UI never saw it, so the postfix also feeds it to the open-binder mirror
    /// (WorldCardInteraction.RecordCardChanged). Local mutations stay on the game's own path and
    /// need no mirroring.</summary>
    internal static class WorldCardInteractionPatches
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(AddCardCertPatch)).Patch();
            harmony.CreateClassProcessor(typeof(AddCardPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReduceCardPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReduceCardIndexPatch)).Patch();
            harmony.CreateClassProcessor(typeof(RemoveGradedCardPatch)).Patch();
            harmony.CreateClassProcessor(typeof(BinderTakeScopePatch)).Patch();
            harmony.CreateClassProcessor(typeof(BinderCloseTakeScopePatch)).Patch();
            harmony.CreateClassProcessor(typeof(BinderPlacementScopePatch)).Patch();
        }

        private static WorldCardInteraction Current => WorldHostBehaviour.ActiveCards
            ?? WorldClientBehaviour.ActiveCards;

        /// <summary>Runs AHEAD of Grading Overhaul's priority-800 anti-cheat prefix on
        /// CPlayerData.AddCard (this prefix is priority 900, and a higher priority runs first).
        /// Every inventory add passes through here - the graded delivery-box reveal, a
        /// save-recovered card box, a graded pack, a trade - so registering the certificate here
        /// means Grading Overhaul never sees a host-graded card whose certificate is unburned or
        /// unbound and never re-encodes it as FAKE (which also prices it at 0.01x). The per-path
        /// Remember calls remain as defence in depth. No-op without Grading Overhaul or for an
        /// ungraded card, so the vanilla path is untouched. Prints the decoded
        /// COMPANY/grade/certificate on both host and client.</summary>
        [HarmonyPatch(typeof(CPlayerData), "AddCard")]
        private static class AddCardCertPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(900)]
            private static void Prefix(CardData cardData)
            {
                var current = Current;
                if (current == null)
                {
                    return;
                }

                WorldCardInteraction.RegisterGradedCardCert(cardData,
                    current.IsHost ? "host add" : "client add");
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "AddCard")]
        private static class AddCardPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int addAmount)
            {
                var current = Current;
                if (current == null)
                {
                    return;
                }

                if (WorldCardInteraction.ApplyingRemoteCards)
                {
                    // The mod applied this itself (remote delta, prediction replay, grading
                    // move); the game's own UI never saw it, so mirror it into an open binder.
                    WorldCardInteraction.RecordCardChanged(cardData, addAmount, true);
                    return;
                }

                if (WorldCardInteraction.SuppressClientCardForwarding
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
                if (current == null)
                {
                    return;
                }

                if (WorldCardInteraction.ApplyingRemoteCards)
                {
                    WorldCardInteraction.RecordCardChanged(cardData, reduceAmount, false);
                    return;
                }

                if (WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold)
                {
                    return;
                }

                if (current.IsClient)
                {
                    current.PredictClientCardDelta(cardData, reduceAmount, false);
                    return;
                }

                // Host: a binder take that raced a remote removal reduces the album below zero.
                // Undo it and drop the spawned hand card instead of fanning out a removal the
                // album cannot cover (the duplication seen when two players take the same card).
                if (WorldCardInteraction.TryRefuseHostBinderTake(cardData, reduceAmount))
                {
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
                if (current == null)
                {
                    return;
                }

                if (WorldCardInteraction.ApplyingRemoteCards)
                {
                    var changed = CPlayerData.GetCardData(index, expansionType, isDestiny);
                    if (changed != null)
                    {
                        WorldCardInteraction.RecordCardChanged(changed, reduceAmount, false);
                    }
                    return;
                }

                if (WorldCardInteraction.SuppressClientCardForwarding
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
            /// <summary>The graded inventory size before vanilla runs. RemoveGradedCard removes
            /// at most one matching row, so a size that did not shrink means the call changed
            /// nothing - a stale album slot whose certificate left via another player. That
            /// matters twice: the removal must not be forwarded (there is no change to sync),
            /// and its prediction's rollback must not restore a row the client never removed.</summary>
            [HarmonyPrefix]
            private static void Prefix(out int __state)
                => __state = CPlayerData.m_GradedCardInventoryList?.Count ?? 0;

            [HarmonyPostfix]
            private static void Postfix(CardData cardData, int __state)
            {
                var current = Current;
                if (current == null)
                {
                    return;
                }

                if (WorldCardInteraction.ApplyingRemoteCards)
                {
                    if (cardData != null && cardData.cardGrade > 0)
                    {
                        WorldCardInteraction.RecordCardChanged(cardData, 1, false);
                    }
                    return;
                }

                if (WorldCardInteraction.SuppressClientCardForwarding
                    || SaveTransferApi.PreloadHold
                    || cardData == null || cardData.cardGrade <= 0)
                {
                    return;
                }

                var removed = (CPlayerData.m_GradedCardInventoryList?.Count ?? 0) < __state;
                if (current.IsClient)
                {
                    current.PredictClientGradedRemoval(cardData, removed);
                    return;
                }

                if (!removed)
                {
                    // The host's own slot was stale too: the game removed nothing, so there is
                    // no change to fan out (forwarding one would consume a same-key row on a
                    // peer's album instead). If this was a binder take, vanilla still spawned a
                    // hand card the album does not own - drop it.
                    WorldCardInteraction.RefuseHostBinderTake(cardData);
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

        /// <summary>The binder's open-book take (the generic branch of
        /// <c>OnRightMouseButtonUp</c>): vanilla reduces the album first and moves the spawned card
        /// into the hand last. The scope measures the hand before vanilla runs; the prediction
        /// registered from the ReduceCard/RemoveGradedCard observer inside the scope is paired with
        /// the card the hand actually gained, so a refusal can despawn exactly that object.
        /// OnRightMouseButtonUp also drives the binder's selection screens (grading submission,
        /// bulk donation, deck edit) - those reduce without holding a card, and their bindings stay
        /// empty.</summary>
        [HarmonyPatch(typeof(CollectionBinderFlipAnimCtrl), "OnRightMouseButtonUp")]
        private static class BinderTakeScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix() => WorldCardInteraction.BeginBinderTake();

            [HarmonyFinalizer]
            private static void Finalizer() => WorldCardInteraction.EndBinderTake();
        }

        /// <summary>Hiding the album can also move a close-up card into the hand (vanilla
        /// <c>HideCardAlbum</c>), with the same reduce-then-hold sequence.</summary>
        [HarmonyPatch(typeof(CollectionBinderFlipAnimCtrl), "HideCardAlbum")]
        private static class BinderCloseTakeScopePatch
        {
            [HarmonyPrefix]
            private static void Prefix() => WorldCardInteraction.BeginBinderTake();

            [HarmonyFinalizer]
            private static void Finalizer() => WorldCardInteraction.EndBinderTake();
        }

        /// <summary>Captures a card the game auto-placed on a display during a binder take
        /// (vanilla <c>EnterHoldCardMode</c> places the current hold card when a slot was primed).
        /// A refused client prediction then empties that slot instead of leaving a duplicate.</summary>
        [HarmonyPatch(typeof(InteractableCardCompartment), "SetCardOnShelf")]
        private static class BinderPlacementScopePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCardCompartment __instance,
                InteractableCard3d card)
                => WorldCardInteraction.RecordBinderPlacement(__instance, card);
        }
    }
}
