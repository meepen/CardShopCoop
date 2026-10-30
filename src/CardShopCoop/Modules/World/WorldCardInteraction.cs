using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.SaveTransfer;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.World
{
    internal sealed partial class WorldCardInteraction
    {
        private readonly CoopRuntimeContext _context;
        private readonly bool _host;

        internal Action<INetMessage> BroadcastOverride;
        internal Action<ICoopPeer, INetMessage> RelayOverride;
        internal Action<ICoopPeer, INetMessage> SendOverride;

        internal readonly WorldContainerInteraction Containers;
        internal readonly WorldMarketInteraction Market;
        private readonly WorldGradingBridge _gradingBridge;

        internal static bool ApplyingRemoteCards;

        /// <summary>Client: true while a module owns a card-mutating game action and carries the
        /// effect in its own intent, so the card patches must not register a second world
        /// prediction and forward it. The game still performs the mutation (vanilla is never
        /// stopped); only the duplicate forward is skipped, the same shape as the wallet's
        /// suppression guard.</summary>
        internal static bool SuppressClientCardForwarding;

        /// <summary>Opens the card-forwarding guard for one module-owned action. Nesting is
        /// counted by saving the previous value, and the caller must dispose it (a Harmony
        /// finalizer for a game call) so an exception cannot leave the guard latched.</summary>
        internal static IDisposable SuppressCardForwarding()
        {
            var previous = SuppressClientCardForwarding;
            SuppressClientCardForwarding = true;
            return new CardForwardingScope(previous);
        }

        internal WorldCardInteraction(CoopRuntimeContext context, bool host,
            BoxNetworkInteraction boxes = null, PlayerBoxInteraction playerBox = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _host = host;

            Containers = new WorldContainerInteraction(boxes, playerBox);
            Market = new WorldMarketInteraction(context, host);
            _gradingBridge = new WorldGradingBridge(this);
            GradingWorldBridge.Attach(_gradingBridge);
            Market.BroadcastState = Broadcast;
        }

        internal bool IsHost => _host;

        internal bool IsClient => !_host;

        internal bool InGameLevel() => _context.InGame();

        /// <summary>The live card interaction for this machine (the host's world owner or the
        /// client's). Message hooks use it because a settled action can run outside a module
        /// instance's normal call path.</summary>
        internal static WorldCardInteraction Current
            => WorldHostBehaviour.ActiveCards ?? WorldClientBehaviour.ActiveCards;

        private int PeerCount => _context.Peers?.Count ?? 0;

        internal static InventoryBase Inv()
            => SceneRef<InventoryBase>.Get();

        private void Send(ICoopPeer peer, INetMessage message)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            if (SendOverride != null)
            {
                SendOverride(peer, message);
                return;
            }

            peer.Send(message);
        }

        private void SendToHost(INetMessage message)
        {
            Send(_context.Host, message);
        }

        private void Broadcast(INetMessage message)
        {
            if (BroadcastOverride != null)
            {
                BroadcastOverride(message);
            }
            else
            {
                _context.Broadcast(message);
            }
        }

        private void BroadcastExcept(ICoopPeer peer, INetMessage message)
        {
            if (peer == null || peer.IsSelf)
            {
                Broadcast(message);
                return;
            }

            if (RelayOverride != null)
            {
                RelayOverride(peer, message);
            }
            else
            {
                _context.BroadcastExcept(peer, message);
            }
        }

        private ICoopPeer FindPeer(int playerId)
        {
            if (playerId <= 0)
            {
                return null;
            }

            var peers = _context.Peers;
            for (var i = 0; i < peers.Count; i++)
            {
                var player = peers[i]?.Player;
                if (player != null && player.Id == playerId)
                {
                    return peers[i];
                }
            }

            return null;
        }

        private static int PlayerId(CoopMessageContext context)
            => context?.Peer?.Player?.Id ?? 0;

        internal void FlushWorldReady()
        {
            if (_gradingOwnership.Count > 0 && RestoreAllGradingOwnership())
            {
                _gradingOwnership.Clear();
                _gradingOwnershipCards.Clear();
            }
            FlushPendingCardWork();
            Containers.FlushClientState();
            Market.FlushClientState();
            FlushFrameCardWork();
        }

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null)
            {
                return;
            }

            Containers.AppendBaselineMessages(append);
            Market.AppendBaselineMessages(append);
        }

        internal void Tick()
        {
            if (!InGameLevel())
            {
                return;
            }

            FlushPendingCardWork();
            Market.Tick();
            FlushFrameCardWork();
        }

        internal void ForwardCardDelta(CardData card, int amount, bool isAdd)
        {
            if (card == null || amount <= 0)
            {
                return;
            }

            if (_host)
            {
                Broadcast(new CardDeltaMessage
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
            }
            else
            {
                SendToHost(new CardDeltaRequestMessage
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
            }
        }

        /// <summary>Records one inventory mutation the game ALREADY performed (the hook observes
        /// vanilla from a postfix). The action reverses the same change through the game's own
        /// methods when the host refuses it; the game owns the local mutation.</summary>
        internal bool PredictClientCardDelta(CardData card, int amount, bool isAdd)
        {
            if (_host || ApplyingRemoteCards || card == null || amount <= 0
                || SaveTransferApi.PreloadHold)
            {
                return false;
            }

            var snapshot = SnapshotCard(card);
            if (GradingApi.Present)
            {
                snapshot.cardGrade = GradingApi.Encoded(card);
            }

            SendToHost(new CardDeltaRequestMessage
            {
                IsAdd = isAdd,
                Amount = amount,
                Card = snapshot,
                Prediction = new CardActionPrediction
                {
                    // Only a take that actually moves a card into the hand can leave a spawned
                    // object to clean up after a refusal.
                    Binding = isAdd ? null : CreateBinderTakeBinding(),
                },
            });
            return true;
        }

        private sealed class CardForwardingScope : IDisposable
        {
            private readonly bool _previous;
            private bool _disposed;

            internal CardForwardingScope(bool previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                SuppressClientCardForwarding = _previous;
            }
        }

        internal bool PredictClientGradedRemoval(CardData card, bool removedLocally = true)
        {
            if (_host || ApplyingRemoteCards || card == null
                || SaveTransferApi.PreloadHold)
            {
                return false;
            }

            var snapshot = SnapshotCard(card);
            if (GradingApi.Present)
            {
                snapshot.cardGrade = GradingApi.Encoded(card);
            }

            var intent = new GradedRemoveRequestMessage
            {
                Card = snapshot,
                // A local removal that changed nothing (a stale album slot whose certificate left
                // through another player) must never be put back by the rollback: the row is gone in
                // the shared album, and restoring it would mint a ghost certificate.
                Prediction = new CardActionPrediction
                {
                    Binding = CreateBinderTakeBinding(),
                    Guard = new GradedRemovalGuard { Superseded = !removedLocally },
                },
            };
            SendToHost(intent);
            RegisterGradedRemovalGuard(intent.ActionId, intent.Prediction.Guard, snapshot);
            return true;
        }

        internal static void ApplyPredictedCardDelta(CardData card, int amount, bool isAdd)
        {
            var previousRemote = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            try
            {
                if (isAdd)
                {
                    if (card.cardGrade > 10)
                    {
                        GradingApi.Remember(card);
                    }

                    if (card.cardGrade > 0)
                    {
                        // CPlayerData.AddCard stores exactly ONE graded row per call, so a
                        // multi-copy prediction must call it once per copy to match vanilla.
                        for (var i = 0; i < amount; i++)
                        {
                            CPlayerData.AddCard(SnapshotCard(card), 1);
                        }
                    }
                    else
                    {
                        CPlayerData.AddCard(card, amount);
                    }
                }
                else if (card.cardGrade > 0)
                {
                    for (var i = 0; i < amount; i++)
                    {
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                    }
                }
                else
                {
                    CPlayerData.ReduceCard(card, amount);
                }
            }
            finally
            {
                ApplyingRemoteCards = previousRemote;
            }
        }

        private List<CardDeltaEntry> _clientCardBatch;

        /// <summary>True while a bulk card action (bundle cards, donation quick-fill) is
        /// collecting its deltas into one transaction.</summary>
        internal bool HasClientCardBatch => _clientCardBatch != null;

        /// <summary>Client: start collecting card deltas into one atomic batch. While open, the
        /// forwarding patch appends to the batch instead of sending a message per mutation, so a
        /// bulk action (up to a hundred reductions) becomes one intent and one reconcile instead
        /// of a per-delta prediction fan-out.</summary>
        internal void BeginClientCardBatch()
        {
            if (_host)
            {
                return;
            }

            if (_clientCardBatch != null)
            {
                CoopPlugin.Log.LogWarning("world cards: a client card batch was already open; "
                    + "restarting it.");
            }

            _clientCardBatch = new List<CardDeltaEntry>();
        }

        internal void AddClientCardBatch(CardData card, int amount, bool isAdd)
        {
            if (_clientCardBatch == null || card == null || amount <= 0)
            {
                return;
            }

            var snapshot = SnapshotCard(card);
            if (GradingApi.Present)
            {
                snapshot.cardGrade = GradingApi.Encoded(card);
            }

            _clientCardBatch.Add(new CardDeltaEntry
            {
                IsAdd = isAdd,
                Amount = amount,
                Card = snapshot,
            });
        }

        internal void CommitClientCardBatch()
        {
            var entries = _clientCardBatch;
            _clientCardBatch = null;
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            // The game already applied every change in the scope; record it as post-hoc
            // prediction(s) so a rejection can reverse it, without re-applying anything the game
            // already did. A pack-opener reveal can exceed the per-batch wire cap, so split into
            // capped chunks (each its own prediction); a bundle stays a single chunk.
            for (var offset = 0; offset < entries.Count; offset += CardDeltaBatchMax)
            {
                var count = Math.Min(CardDeltaBatchMax, entries.Count - offset);
                var chunk = entries.GetRange(offset, count);
                SendToHost(new CardDeltaBatchRequestMessage
                {
                    Deltas = chunk,
                    Predicted = true,
                });
            }
        }

        private static void ApplyPredictedCardBatch(List<CardDeltaEntry> entries, bool reverse)
        {
            if (reverse)
            {
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    ApplyPredictedCardDelta(entries[i].Card, entries[i].Amount, !entries[i].IsAdd);
                }

                return;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                ApplyPredictedCardDelta(entries[i].Card, entries[i].Amount, entries[i].IsAdd);
            }
        }

        internal void ForwardGradedRemoval(CardData card)
        {
            if (card == null || card.cardGrade <= 0)
            {
                return;
            }

            if (_host)
            {
                Broadcast(new GradedRemoveMessage { Card = SnapshotCard(card) });
            }
            else
            {
                SendToHost(new GradedRemoveRequestMessage { Card = SnapshotCard(card) });
            }
        }

        // ---------------- authoritative card state (host -> clients) ----------------

        /// <summary>Applies one authoritative card delta. The host excludes the actor from its
        /// broadcast, so this machine's own prediction is settled by its action response only.
        /// The player identity is kept for call-site symmetry; authoritative state does not need it.
        /// </summary>
        internal void HandleCardDelta(int playerId, CardDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            ApplyOrHoldCardDelta(message.IsAdd, message.Amount, message.Card, out _);
        }

        /// <summary>Applies one authoritative atomic card movement. See
        /// <see cref="HandleCardDelta(int, CardDeltaMessage)"/>.</summary>
        internal void HandleCardDeltaBatch(int playerId, CardDeltaBatchMessage message)
        {
            if (message?.Deltas == null)
            {
                throw new InvalidOperationException("Card delta batch has no delta list.");
            }

            for (var i = 0; i < message.Deltas.Count; i++)
            {
                ApplyOrHoldCardDelta(message.Deltas[i].IsAdd, message.Deltas[i].Amount,
                    message.Deltas[i].Card, out _);
            }
        }

        /// <summary>Applies one authoritative graded-album removal.</summary>
        internal void HandleGradedRemove(int playerId, GradedRemoveMessage message)
        {
            if (message?.Card == null)
            {
                return;
            }

            if (!InGameLevel())
            {
                _pendingCardDeltas.Add(new PendingCard
                {
                    IsAdd = false,
                    Amount = 1,
                    Card = SnapshotCard(message.Card),
                });
                return;
            }

            ApplyTrustedCardDelta(false, 1, message.Card);
        }

        // ---------------- host actions ----------------

        /// <summary>One client's card change. The host applies it and publishes the resulting
        /// delta to every peer except the actor; the actor settles through the action response.</summary>
        internal ActionOutcome HandleCardDelta(CoopMessageContext context, CardDeltaRequestMessage message)
        {
            if (!_host || !InGameLevel() || message?.Card == null)
            {
                return ActionOutcome.Reject("the card action is not ready");
            }

            var playerId = PlayerId(context);
            var applied = ApplyCardDelta(message.IsAdd, message.Amount, message.Card,
                out var relayAnyway);
            if (!applied && !relayAnyway)
            {
                return ActionOutcome.Reject("the host refused the card change",
                    BuildCardResult(message.Card));
            }

            if (!message.IsAdd && !applied && GradedRemoveUncovered(message.Card))
            {
                // A graded removal that traveled on the CardDelta path (a defence for senders that
                // classify it as a plain reduce): the host has the card set and no matching
                // certificate left, so the pick lost the race. The authoritative removal still goes
                // to every other peer, and the actor's response carries the host's count so its
                // rollback cannot mint a ghost certificate.
                BroadcastExcept(context.Peer, new CardDeltaMessage
                {
                    IsAdd = false,
                    Amount = message.Amount,
                    Card = SnapshotCard(message.Card),
                });
                return ActionOutcome.Reject("the host no longer has that card",
                    BuildCardResult(message.Card));
            }

            if (message.IsAdd)
            {
                ConsumeGradingOwnership(playerId, message.Card, message.Amount);
            }
            else if (!relayAnyway)
            {
                RecordGradingOwnership(playerId, message.Card, message.Amount);
            }

            BroadcastExcept(context.Peer, new CardDeltaMessage
            {
                IsAdd = message.IsAdd,
                Amount = message.Amount,
                Card = SnapshotCard(message.Card),
            });
            return ActionOutcome.Applied(BuildCardResult(message.Card));
        }

        /// <summary>One client's bulk card movement. The batch is atomic on the host, and the
        /// response carries the resulting host count for every card it touched.</summary>
        internal ActionOutcome HandleCardDeltaBatch(CoopMessageContext context,
            CardDeltaBatchRequestMessage message)
        {
            if (!_host || !InGameLevel() || message?.Deltas == null)
            {
                return ActionOutcome.Reject("the card batch is not ready");
            }

            if (message.Deltas.Count == 0)
            {
                return ActionOutcome.Reject("the card batch carries no deltas");
            }

            var applied = ApplyHostCardBatch(context.Peer, message, requireAllApplied: false);
            var result = BuildCardBatchResult(message.Deltas);
            return applied
                ? ActionOutcome.Applied(result)
                : ActionOutcome.Reject("the host refused the card batch", result);
        }

        /// <summary>Applies a host-derived atomic card movement (the trade accept path). The actor
        /// is excluded from the state broadcast; its own response owns the authoritative hand-off.
        /// With <paramref name="requireAllApplied"/> the batch aborts and retracts if any delta
        /// cannot truly be applied here.</summary>
        internal bool HandleCardDeltaBatch(int senderPlayerId, CardDeltaBatchMessage message,
            bool requireAllApplied)
        {
            if (!_host || message?.Deltas == null)
            {
                return false;
            }

            return ApplyHostCardBatch(FindPeer(senderPlayerId), message, requireAllApplied);
        }

        /// <summary>The shared host batch core. Deltas this host actually applied are retracted if
        /// a strict batch fails; accepted deltas are published to every peer except the actor.</summary>
        private bool ApplyHostCardBatch(ICoopPeer actor, ICardDeltaBatchSource message,
            bool requireAllApplied)
        {
            if (!_host)
            {
                return false;
            }

            if (message.Deltas == null)
            {
                throw new InvalidOperationException("Card delta batch has no delta list.");
            }

            var total = message.Deltas.Count;
            if (total > CardDeltaBatchMax)
            {
                throw new InvalidOperationException("Incoming card delta batch is too large.");
            }

            var playerId = actor?.Player?.Id ?? 0;
            var needFiltered = PeerCount > 1;
            _batchRelayBuf.Clear();
            // Deltas this host actually applied (retracted if the batch fails) and deltas it could
            // only relay for content it lacks. Ownership is recorded from these AFTER the batch is
            // known to have succeeded, so a failed batch never leaves a grading claim behind.
            var moved = new List<PendingCard>(total);
            var relayed = new List<PendingCard>(total);
            var applied = 0;
            var relayedOnly = 0;
            for (var i = 0; i < total; i++)
            {
                var delta = message.Deltas[i];
                var relayCopy = needFiltered ? SnapshotCard(delta.Card) : null;
                bool ok;
                bool relayAnyway;
                try
                {
                    ok = ApplyCardDelta(delta.IsAdd, delta.Amount, delta.Card, out relayAnyway);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"card delta batch: delta {i + 1}/{total} failed to apply.", e);
                }

                if (requireAllApplied && (!ok || relayAnyway))
                {
                    RetractCardDeltas(moved);
                    return false;
                }

                if (!ok && !relayAnyway)
                {
                    // REFUSED (invalid, overflow, or a remove this host cannot cover). Undo what
                    // this batch already applied and fail the whole batch, so the sender's
                    // rollback and this host both return to the pre-batch state.
                    CoopPlugin.Log.LogWarning("card delta batch from "
                        + (actor?.Name ?? "player " + playerId) + " refused at delta "
                        + (i + 1) + "/" + total + "; rolling the batch back.");
                    RetractCardDeltas(moved);
                    return false;
                }

                var pending = new PendingCard
                {
                    IsAdd = delta.IsAdd,
                    Amount = delta.Amount,
                    Card = SnapshotCard(delta.Card),
                };
                if (ok)
                {
                    moved.Add(pending);
                    applied++;
                }
                else
                {
                    relayed.Add(pending);
                    relayedOnly++;
                }

                if (needFiltered)
                {
                    _batchRelayBuf.Add(new PendingCard
                    {
                        IsAdd = delta.IsAdd,
                        Amount = delta.Amount,
                        Card = relayCopy,
                    });
                }
            }

            // The movement succeeded: record what actually moved. A relayed add still consumes the
            // sender's ownership claim (the old per-delta behavior), a relayed remove records none.
            for (var i = 0; i < moved.Count; i++)
            {
                if (moved[i].IsAdd)
                {
                    ConsumeGradingOwnership(playerId, moved[i].Card, moved[i].Amount);
                }
                else
                {
                    RecordGradingOwnership(playerId, moved[i].Card, moved[i].Amount);
                }
            }

            for (var i = 0; i < relayed.Count; i++)
            {
                if (relayed[i].IsAdd)
                {
                    ConsumeGradingOwnership(playerId, relayed[i].Card, relayed[i].Amount);
                }
            }

            if (applied == total && relayedOnly == 0 && total > 0)
            {
                BroadcastExcept(actor, ToAuthoritative(message));
            }
            else if (_batchRelayBuf.Count > 0)
            {
                BroadcastExcept(actor, ToBatchMessage(_batchRelayBuf));
            }
            return applied > 0 || relayedOnly > 0;
        }

        /// <summary>Undoes the deltas a strict batch applied before it aborted, newest first, so a
        /// refused atomic movement leaves this host exactly as it found it. The inverse of an add
        /// is the same remove the general path would apply, and vice versa.</summary>
        private static void RetractCardDeltas(List<PendingCard> applied)
        {
            for (var i = applied.Count - 1; i >= 0; i--)
            {
                var pending = applied[i];
                if (!ApplyCardDelta(!pending.IsAdd, pending.Amount, pending.Card, out var relayAnyway)
                    && !relayAnyway)
                {
                    CoopPlugin.Log.LogError("trade card movement could not be retracted for "
                        + CardIdent(pending.Card) + " (" + (pending.IsAdd ? "add" : "remove")
                        + " x" + pending.Amount + "); the host inventory may be inconsistent.");
                }
            }
        }

        /// <summary>One client's graded-album removal: the host applies it to the shared album,
        /// publishes the removal to every other peer, and answers the actor with the resulting
        /// count so a lost race cannot leave a ghost certificate behind.</summary>
        internal ActionOutcome HandleGradedRemove(CoopMessageContext context,
            GradedRemoveRequestMessage message)
        {
            if (!_host || !InGameLevel() || message?.Card == null)
            {
                return ActionOutcome.Reject("the graded removal is not ready");
            }

            var playerId = PlayerId(context);
            var applied = ApplyCardDelta(false, 1, message.Card, out var relayAnyway);
            if (!applied && !relayAnyway)
            {
                return ActionOutcome.Reject("the host refused the graded removal",
                    BuildCardResult(message.Card));
            }

            if (!applied && GradedRemoveUncovered(message.Card))
            {
                // The card set is installed and the album has no matching certificate: the pick
                // lost the race for that row, or the sender's replica drifted. The authoritative
                // removal still reaches every other peer, and the actor's response carries the
                // host's count so its rollback cannot put the row back.
                BroadcastExcept(context.Peer, new GradedRemoveMessage
                {
                    Card = SnapshotCard(message.Card),
                });
                return ActionOutcome.Reject("the host no longer has that card",
                    BuildCardResult(message.Card));
            }

            if (!relayAnyway)
            {
                RecordGradingOwnership(playerId, message.Card, 1);
            }

            BroadcastExcept(context.Peer, new GradedRemoveMessage
            {
                Card = SnapshotCard(message.Card),
            });
            return ActionOutcome.Applied(BuildCardResult(message.Card));
        }

        private CardActionResult BuildCardResult(CardData card)
            => BuildCardResult(new List<CardData> { card });

        private CardActionResult BuildCardResult(List<CardData> cards)
        {
            var result = new CardActionResult();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null || !CardSetInstalledHere(card))
                {
                    continue;
                }

                var key = CardOwnershipKey(card);
                if (key != null && !seen.Add(key))
                {
                    continue;
                }

                result.Cards.Add(new CardCountState
                {
                    Card = SnapshotCard(card),
                    Amount = OwnedCardCount(card),
                });
            }

            return result;
        }

        private CardActionResult BuildCardBatchResult(List<CardDeltaEntry> deltas)
        {
            var cards = new List<CardData>(deltas?.Count ?? 0);
            for (var i = 0; deltas != null && i < deltas.Count; i++)
            {
                cards.Add(deltas[i].Card);
            }

            return BuildCardResult(cards);
        }

        private static int OwnedCardCount(CardData card)
            => card.cardGrade > 0
                ? CountGradedCards(card)
                : Math.Max(0, CPlayerData.GetCardAmount(card));

        /// <summary>Applies the host's authoritative counts from an action response. This is the
        /// one correction point for an accepted prediction that raced a remote change and for a
        /// rejection whose local inverse no longer matches the host.</summary>
        internal static void ApplyCardActionResult(ActionResponseMessage response)
        {
            if (response is not CardActionResult result || result.Cards == null)
            {
                return;
            }

            var current = Current;
            if (current == null)
            {
                return;
            }

            current.ApplyAuthoritativeCardCounts(result.Cards);
        }

        private void ApplyAuthoritativeCardCounts(List<CardCountState> states)
        {
            var changed = false;
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state?.Card == null || !CardSetInstalledHere(state.Card))
                {
                    continue;
                }

                var target = Math.Max(0, state.Amount);
                var have = OwnedCardCount(state.Card);
                if (target == have)
                {
                    continue;
                }

                if (target > have)
                {
                    ApplyPredictedCardDelta(state.Card, target - have, true);
                }
                else
                {
                    ApplyPredictedCardDelta(state.Card, have - target, false);
                }

                changed = true;
            }

            if (changed)
            {
                NotifyCardsChanged();
            }
        }

        /// <summary>Local state a card action owns until its response settles it. Never serialized;
        /// the host's copy of the request has none of it.</summary>
        internal sealed class CardActionPrediction
        {
            internal BinderTakeBinding Binding;
            internal GradedRemovalGuard Guard;
        }

        internal static void UndoCardDelta(CardDeltaRequestMessage message)
        {
            if (message?.Prediction == null)
            {
                return;
            }

            ApplyPredictedCardDelta(message.Card, message.Amount, !message.IsAdd);
            Current?.NotifyCardsChanged();
        }

        internal static void MergeCardDelta(CardDeltaRequestMessage message,
            ActionResponseMessage response)
        {
            if (message == null)
            {
                return;
            }

            try
            {
                ApplyCardActionResult(response);
                if (response?.Rejected == true && message.Prediction != null && !message.IsAdd)
                {
                    DespawnRejectedBinderTake(message.Prediction.Binding, message.Card);
                    GradingApi.OnCardRemovalRefused(message.Card, message.Amount);
                }
            }
            finally
            {
                OnActionSettled(message.ActionId);
            }
        }

        internal static void UndoCardDeltaBatch(CardDeltaBatchRequestMessage message)
        {
            if (message?.Predicted != true || message.Deltas == null)
            {
                return;
            }

            ApplyPredictedCardBatch(message.Deltas, reverse: true);
            Current?.NotifyCardsChanged();
        }

        internal static void MergeCardDeltaBatch(CardDeltaBatchRequestMessage message,
            ActionResponseMessage response)
        {
            if (message == null)
            {
                return;
            }

            try
            {
                ApplyCardActionResult(response);
            }
            finally
            {
                OnActionSettled(message.ActionId);
            }
        }

        internal static void UndoGradedRemove(GradedRemoveRequestMessage message)
        {
            if (message?.Prediction == null)
            {
                return;
            }

            // A competing accepted removal of this same certificate means the shared row is
            // already gone; putting it back would mint a ghost certificate.
            if (message.Prediction.Guard?.Superseded == true)
            {
                return;
            }

            ApplyPredictedCardDelta(message.Card, 1, isAdd: true);
            Current?.NotifyCardsChanged();
        }

        internal static void MergeGradedRemove(GradedRemoveRequestMessage message,
            ActionResponseMessage response)
        {
            if (message == null)
            {
                return;
            }

            try
            {
                ApplyCardActionResult(response);
                if (response?.Rejected == true && message.Prediction != null)
                {
                    DespawnRejectedBinderTake(message.Prediction.Binding, message.Card);
                    GradingApi.OnCardRemovalRefused(message.Card, 1);
                }
            }
            finally
            {
                OnActionSettled(message.ActionId);
            }
        }

        internal void Reset()
        {
            Containers.Reset();
            Market.Reset();
            _pendingCardDeltas.Clear();
            var claimsRestored = RestoreAllGradingOwnership();
            if (claimsRestored)
            {
                _gradingOwnership.Clear();
                _gradingOwnershipCards.Clear();
            }
            else if (_gradingOwnership.Count > 0)
            {
                CoopPlugin.Log.LogWarning("World reset retained grading card claims because the live "
                    + "inventory is not ready; they will be restored before the claims are cleared.");
            }
            _shownMonsters.Clear();
            _deltaAppliedThisFrame = 0;
            _deltaNetThisFrame = 0;
            _deltaLogBuf.Clear();
            ResetBinderMirror();
            ClearBinderTakeState();
            ApplyingRemoteCards = false;
            _clientCardBatch = null;
            WorldContainerInteraction.ApplyingRemote = false;
            ClearCardSetCache();
            GradingApi.Reset();
        }

        internal void Shutdown()
        {
            Reset();
            GradingWorldBridge.Detach(_gradingBridge);
        }

        internal void ForgetGradingOwnership(int playerId)
        {
            if (playerId > 0)
            {
                RestoreGradingOwnership(playerId);
            }
        }

        private bool RestoreAllGradingOwnership()
        {
            if (_gradingOwnership.Count == 0)
            {
                return true;
            }

            if (!InGameLevel() || Inv() == null)
            {
                return false;
            }

            var restored = new List<PendingCard>();
            foreach (var owner in _gradingOwnership)
            {
                if (!_gradingOwnershipCards.TryGetValue(owner.Key, out var cards))
                {
                    return false;
                }

                foreach (var claim in owner.Value)
                {
                    if (!cards.TryGetValue(claim.Key, out var card) || card == null
                        || claim.Value <= 0 || claim.Value > CardDeltaAmountMax)
                    {
                        return false;
                    }

                    restored.Add(new PendingCard
                    {
                        IsAdd = true,
                        Amount = claim.Value,
                        Card = SnapshotCard(card),
                    });
                }
            }

            return ApplyRestoredCards(restored);
        }

        private bool RestoreGradingOwnership(int playerId)
        {
            if (!_gradingOwnership.ContainsKey(playerId))
            {
                return true;
            }

            if (!InGameLevel() || Inv() == null
                || !_gradingOwnershipCards.TryGetValue(playerId, out var cards))
            {
                return false;
            }

            var restored = new List<PendingCard>();
            foreach (var claim in _gradingOwnership[playerId])
            {
                if (!cards.TryGetValue(claim.Key, out var card) || card == null
                    || claim.Value <= 0 || claim.Value > CardDeltaAmountMax)
                {
                    return false;
                }

                restored.Add(new PendingCard
                {
                    IsAdd = true,
                    Amount = claim.Value,
                    Card = SnapshotCard(card),
                });
            }

            if (!ApplyRestoredCards(restored))
            {
                return false;
            }

            _gradingOwnership.Remove(playerId);
            _gradingOwnershipCards.Remove(playerId);
            CoopPlugin.Log.LogInfo("restored " + restored.Count
                + " grading card claim(s) before clearing player " + playerId);
            return true;
        }

        private bool ApplyRestoredCards(List<PendingCard> restored)
        {
            var applied = new List<PendingCard>();
            try
            {
                using (BeginRemoteCardApply())
                {
                    for (var i = 0; i < restored.Count; i++)
                    {
                        var item = restored[i];
                        var before = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (before < 0 || before > int.MaxValue - item.Amount)
                        {
                            throw new InvalidOperationException("card count overflowed before claim restoration");
                        }

                        GradingApi.Remember(item.Card);
                        CPlayerData.AddCard(SnapshotCard(item.Card), item.Amount);

                        var after = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (after != before + item.Amount)
                        {
                            throw new InvalidOperationException("claim restoration did not update inventory by the expected amount");
                        }

                        applied.Add(item);
                    }
                }
            }
            catch (Exception error)
            {
                if (!RollbackRestoredCards(applied))
                {
                    CoopPlugin.Log.LogError("grading claim restoration compensation failed: " + error);
                }
                else
                {
                    CoopPlugin.Log.LogWarning("grading claim restoration failed and was compensated: "
                        + error.Message);
                }
                return false;
            }

            for (var i = 0; i < restored.Count; i++)
            {
                ForwardCardDelta(restored[i].Card, restored[i].Amount, true);
            }

            if (restored.Count > 0)
            {
                NotifyCardsChanged();
            }

            return true;
        }

        private bool RollbackRestoredCards(List<PendingCard> applied)
        {
            try
            {
                using (BeginRemoteCardApply())
                {
                    for (var i = applied.Count - 1; i >= 0; i--)
                    {
                        var item = applied[i];
                        var before = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (before < item.Amount)
                        {
                            return false;
                        }

                        if (item.Card.cardGrade > 0)
                        {
                            for (var j = 0; j < item.Amount; j++)
                            {
                                CPlayerData.RemoveGradedCard(item.Card, ignoreGradedCardIndex: true);
                            }
                        }
                        else
                        {
                            CPlayerData.ReduceCard(item.Card, item.Amount);
                        }

                        var after = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (after != before - item.Amount)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("claim restoration rollback failed: " + error);
                return false;
            }

            return true;
        }

        internal IDisposable BeginRemoteCardApply()
        {
            var previous = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            return new RemoteCardApplyScope(previous);
        }

        /// <summary>Atomically reserves cards for the grading owner. A client-side selection
        /// reaches this host as an already-validated remove delta, represented by
        /// _gradingOwnership; cards still in the host's collected/graded inventory are checked
        /// directly. The caller gets one transaction which either commits those exact cards or
        /// restores every host mutation on dispose.</summary>
        internal bool TryReserveGradingCards(int playerId, IList<CardData> cards,
            out IGradingCardReservation reservation, out string reason)
        {
            reservation = null;
            reason = null;
            if (!_host || playerId <= 0 || cards == null || cards.Count == 0)
            {
                reason = "the host card owner is not ready";
                return false;
            }
            var transferred = _gradingOwnership.TryGetValue(playerId, out var owned)
                ? new Dictionary<string, int>(owned, StringComparer.Ordinal) : null;
            var planned = new List<ReservedGradingCard>(cards.Count);
            var actualUngraded = new Dictionary<string, int>(StringComparer.Ordinal);
            var actualGraded = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                for (var i = 0; i < cards.Count; i++)
                {
                    var card = cards[i];
                    if (card == null || !CardSetInstalledHere(card))
                    {
                        reason = "the host cannot represent one or more submitted cards";
                        return false;
                    }

                    var key = CardOwnershipKey(card);
                    if (key == null)
                    {
                        reason = "one submitted card has no stable identity";
                        return false;
                    }

                    var isGraded = card.cardGrade > 0;
                    if (transferred != null && transferred.TryGetValue(key, out var moved) && moved > 0)
                    {
                        transferred[key] = moved - 1;
                        planned.Add(new ReservedGradingCard
                        {
                            Card = SnapshotCard(card),
                            FromTransferredOwnership = true,
                        });
                        continue;
                    }

                    var remaining = isGraded
                        ? GetCachedGradedCount(card, actualGraded)
                        : GetCachedUngradedCount(card, actualUngraded);
                    if (remaining > 0)
                    {
                        SetCachedCount(isGraded ? actualGraded : actualUngraded, key, remaining - 1);
                        planned.Add(new ReservedGradingCard { Card = SnapshotCard(card) });
                        continue;
                    }

                    if (transferred == null || !transferred.TryGetValue(key, out moved) || moved <= 0)
                    {
                        reason = "the host does not own every submitted card";
                        return false;
                    }

                    transferred[key] = moved - 1;
                    planned.Add(new ReservedGradingCard
                    {
                        Card = SnapshotCard(card),
                        FromTransferredOwnership = true,
                    });
                }
            }
            catch (Exception error)
            {
                reason = "host card ownership could not be checked: " + error.Message;
                CoopPlugin.Log.LogError("grading card ownership check failed for player "
                    + playerId + ": " + error);
                return false;
            }

            var applied = new List<ReservedGradingCard>();
            try
            {
                using (BeginRemoteCardApply())
                {
                    for (var i = 0; i < planned.Count; i++)
                    {
                        var item = planned[i];
                        if (item.FromTransferredOwnership)
                        {
                            continue;
                        }

                        if (item.Card.cardGrade > 0)
                        {
                            var before = CountGradedCards(item.Card);
                            if (before <= 0)
                            {
                                throw new InvalidOperationException("graded card disappeared before reservation");
                            }

                            CPlayerData.RemoveGradedCard(item.Card, ignoreGradedCardIndex: true);
                            var after = CountGradedCards(item.Card);
                            if (after != before - 1)
                            {
                                throw new InvalidOperationException("host graded-card removal did not change inventory");
                            }
                        }
                        else
                        {
                            var before = CPlayerData.GetCardAmount(item.Card);
                            if (before <= 0)
                            {
                                throw new InvalidOperationException("card disappeared before reservation");
                            }

                            CPlayerData.ReduceCard(item.Card, 1);
                            var after = CPlayerData.GetCardAmount(item.Card);
                            if (after != before - 1)
                            {
                                throw new InvalidOperationException("host card removal did not change inventory");
                            }
                        }
                        applied.Add(item);
                    }
                }

                // Only fan out after every actual host mutation succeeded. The selected client's
                // original remove was already relayed; newly consumed host inventory is new
                // authority and must reach the other peers.
                for (var i = 0; i < applied.Count; i++)
                {
                    var card = applied[i].Card;
                    if (card.cardGrade > 0)
                    {
                        ForwardGradedRemoval(card);
                    }
                    else
                    {
                        ForwardCardDelta(card, 1, false);
                    }
                }
                NotifyCardsChanged();
                reservation = new GradingCardReservation(this, playerId, planned,
                    _gradingOwnership.TryGetValue(playerId, out var claimCounts)
                        ? new Dictionary<string, int>(claimCounts, StringComparer.Ordinal)
                        : null,
                    _gradingOwnershipCards.TryGetValue(playerId, out var claimCards)
                        ? CloneClaimCards(claimCards) : null);
                return true;
            }
            catch (Exception error)
            {
                RollbackGradingCards(applied);
                reason = "host card reservation failed: " + error.Message;
                CoopPlugin.Log.LogError("grading card reservation failed for player "
                    + playerId + ": " + error);
                return false;
            }
        }

        private void RollbackGradingCards(List<ReservedGradingCard> applied)
        {
            using (BeginRemoteCardApply())
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                {
                    var card = applied[i].Card;
                    if (card.cardGrade > 0)
                    {
                        GradingApi.Remember(card);
                        CPlayerData.AddCard(card, 1);
                    }
                    else
                    {
                        CPlayerData.AddCard(card, 1);
                    }
                }
            }

            for (var i = 0; i < applied.Count; i++)
            {
                var card = applied[i].Card;
                ForwardCardDelta(card, 1, true);
            }
            if (applied.Count > 0)
            {
                NotifyCardsChanged();
            }
        }

        private int GetCachedUngradedCount(CardData card, Dictionary<string, int> cache)
        {
            var key = CardOwnershipKey(card);
            if (cache.TryGetValue(key, out var count))
            {
                return count;
            }

            count = CPlayerData.GetCardAmount(card);
            cache[key] = count;
            return count;
        }

        private int GetCachedGradedCount(CardData card, Dictionary<string, int> cache)
        {
            var key = CardOwnershipKey(card);
            if (cache.TryGetValue(key, out var count))
            {
                return count;
            }

            count = 0;
            var rows = CPlayerData.m_GradedCardInventoryList;
            if (rows != null)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null)
                    {
                        continue;
                    }

                    var candidate = CPlayerData.GetGradedCardData(row);
                    if (CardOwnershipKey(candidate) == key)
                    {
                        count++;
                    }
                }
            }
            cache[key] = count;
            return count;
        }

        private static int CountGradedCards(CardData card)
        {
            var key = CardOwnershipKey(card);
            var count = 0;
            var rows = CPlayerData.m_GradedCardInventoryList;
            if (key == null || rows == null)
            {
                return count;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && CardOwnershipKey(CPlayerData.GetGradedCardData(row)) == key)
                {
                    count++;
                }
            }
            return count;
        }

        private static void SetCachedCount(Dictionary<string, int> cache, string key, int count)
        {
            cache[key] = count;
        }

        private sealed class ReservedGradingCard
        {
            internal CardData Card;
            internal bool FromTransferredOwnership;
        }

        private sealed class GradingCardReservation : IGradingCardReservation
        {
            private readonly WorldCardInteraction _owner;
            private readonly int _playerId;
            private readonly List<ReservedGradingCard> _cards;
            private readonly Dictionary<string, int> _claimCountsBefore;
            private readonly Dictionary<string, CardData> _claimCardsBefore;
            private bool _committed;
            private bool _rolledBack;
            private bool _disposed;

            internal GradingCardReservation(WorldCardInteraction owner, int playerId,
                List<ReservedGradingCard> cards, Dictionary<string, int> claimCountsBefore,
                Dictionary<string, CardData> claimCardsBefore)
            {
                _owner = owner;
                _playerId = playerId;
                _cards = cards;
                _claimCountsBefore = claimCountsBefore;
                _claimCardsBefore = claimCardsBefore;
            }

            public void Commit()
            {
                if (_disposed || _committed || _rolledBack)
                {
                    return;
                }

                Dictionary<string, int> moved = null;
                for (var i = 0; i < _cards.Count; i++)
                {
                    if (!_cards[i].FromTransferredOwnership)
                    {
                        continue;
                    }

                    if (moved == null)
                    {
                        if (!_owner._gradingOwnership.TryGetValue(_playerId, out var current))
                        {
                            throw new InvalidOperationException("grading ownership transfer disappeared before commit");
                        }

                        moved = new Dictionary<string, int>(current, StringComparer.Ordinal);
                    }

                    var key = CardOwnershipKey(_cards[i].Card);
                    if (!moved.TryGetValue(key, out var count) || count <= 0)
                    {
                        throw new InvalidOperationException("grading ownership transfer disappeared before commit");
                    }

                    if (count == 1)
                    {
                        moved.Remove(key);
                    }
                    else
                    {
                        moved[key] = count - 1;
                    }
                }

                if (moved != null)
                {
                    var current = _owner._gradingOwnership[_playerId];
                    current.Clear();
                    foreach (var pair in moved)
                    {
                        current[pair.Key] = pair.Value;
                    }
                }
                _committed = true;
            }

            public void Rollback()
            {
                if (_rolledBack)
                {
                    return;
                }

                _rolledBack = true;
                Exception ownershipError = null;
                if (_committed)
                {
                    try
                    {
                        if (_claimCountsBefore == null || _claimCountsBefore.Count == 0)
                        {
                            _owner._gradingOwnership.Remove(_playerId);
                        }
                        else
                        {
                            _owner._gradingOwnership[_playerId] =
                                new Dictionary<string, int>(_claimCountsBefore, StringComparer.Ordinal);
                        }

                        if (_claimCardsBefore == null || _claimCardsBefore.Count == 0)
                        {
                            _owner._gradingOwnershipCards.Remove(_playerId);
                        }
                        else
                        {
                            _owner._gradingOwnershipCards[_playerId] = CloneClaimCards(_claimCardsBefore);
                        }
                    }
                    catch (Exception error)
                    {
                        ownershipError = error;
                        CoopPlugin.Log.LogError("grading ownership rollback failed: " + error);
                    }
                }

                try
                {
                    _owner.RollbackGradingCards(_cards.FindAll(card => !card.FromTransferredOwnership));
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("grading card rollback failed: " + error);
                    if (ownershipError == null)
                    {
                        ownershipError = error;
                    }
                }

                if (ownershipError != null)
                {
                    throw ownershipError;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_committed)
                {
                    Rollback();
                }
            }
        }

        private static Dictionary<string, CardData> CloneClaimCards(
            Dictionary<string, CardData> cards)
        {
            var result = new Dictionary<string, CardData>(StringComparer.Ordinal);
            if (cards == null)
            {
                return result;
            }

            foreach (var pair in cards)
            {
                result[pair.Key] = pair.Value == null ? null : SnapshotCard(pair.Value);
            }

            return result;
        }

        private Dictionary<string, int> GetOrCreateOwnership(int playerId)
        {
            if (!_gradingOwnership.TryGetValue(playerId, out var result))
            {
                result = new Dictionary<string, int>(StringComparer.Ordinal);
                _gradingOwnership[playerId] = result;
            }
            return result;
        }

        private void RecordGradingOwnership(int playerId, CardData card, int amount)
        {
            if (!_host || playerId <= 0 || card == null || amount <= 0)
            {
                return;
            }

            var key = CardOwnershipKey(card);
            if (key == null)
            {
                return;
            }

            var owned = GetOrCreateOwnership(playerId);
            owned.TryGetValue(key, out var count);
            owned[key] = Math.Min(1024, count > 1024 - amount ? 1024 : count + amount);
            if (!_gradingOwnershipCards.TryGetValue(playerId, out var cards))
            {
                cards = new Dictionary<string, CardData>(StringComparer.Ordinal);
                _gradingOwnershipCards[playerId] = cards;
            }
            cards[key] = SnapshotCard(card);
        }

        private bool ConsumeGradingOwnership(int playerId, CardData card, int amount)
        {
            if (!_host || playerId <= 0 || card == null || amount <= 0
                || !_gradingOwnership.TryGetValue(playerId, out var owned))
            {
                return false;
            }

            var key = CardOwnershipKey(card);
            if (key == null || !owned.TryGetValue(key, out var count) || count < amount)
            {
                return false;
            }

            count -= amount;
            if (count > 0)
            {
                owned[key] = count;
            }
            else
            {
                owned.Remove(key);
                if (_gradingOwnershipCards.TryGetValue(playerId, out var cards))
                {
                    cards.Remove(key);
                }
            }
            if (owned.Count == 0)
            {
                _gradingOwnership.Remove(playerId);
                _gradingOwnershipCards.Remove(playerId);
            }
            return true;
        }

        internal void NotifyCardsChanged()
        {
            // Local repair (rejected prediction replay, trade undo, grading claim restore): the
            // slots the game already changed must be put back in step even when the net change
            // is a gain. See _binderFullResync.
            _binderFullResync = true;
            _binderRefreshPending = true;
        }

        /// <summary>Registers a graded card's certificate with Grading Overhaul BEFORE the game
        /// adds it to the inventory, and prints the decoded COMPANY/grade/certificate so a
        /// "fake"/"$0.01" report is traceable on BOTH host and client. Called from every
        /// reconstruction path the mod owns AND from the CPlayerData.AddCard prefix that runs
        /// ahead of Grading Overhaul's priority-800 anti-cheat (see WorldCardInteractionPatches),
        /// so no local add can leave a host-graded certificate unburned/unbound. No-op and no log
        /// when Grading Overhaul is absent or the card is ungraded - the vanilla path is
        /// untouched.</summary>
        internal static void RegisterGradedCardCert(CardData card, string context)
        {
            if (card == null || card.cardGrade <= 0 || !GradingApi.Present)
            {
                return; // ungraded, or no Grading Overhaul - nothing to register, nothing to log
            }

            var encoded = GradingApi.Encoded(card);
            if (encoded <= 10)
            {
                return;
            }

            var cert = GradingApi.DescribeCert(card) ?? ("encoded=" + encoded);
            var registered = GradingApi.IsCertRegistered(card);
            if (!registered)
            {
                GradingApi.Remember(card);
                registered = GradingApi.IsCertRegistered(card);
            }

            var key = context + "|" + cert;
            if (_loggedCertKeys.Count < LoggedCertKeysMax && _loggedCertKeys.Add(key))
            {
                var line = "grading cert " + (registered ? "ok" : "NOT registered") + " ("
                    + context + "): " + cert + " " + CardIdent(card);
                // A card that could not be registered is exactly the one Grading Overhaul will
                // flag FAKE / price at 0.01x, so it stands out from the normal lines.
                if (registered)
                {
                    CoopPlugin.Log.LogInfo(line);
                }
                else
                {
                    CoopPlugin.Log.LogWarning(line);
                }
            }
        }

        private sealed class RemoteCardApplyScope : IDisposable
        {
            private readonly bool _previous;
            private bool _disposed;

            internal RemoteCardApplyScope(bool previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ApplyingRemoteCards = _previous;
            }
        }

        // card mirrors that arrived during a scene load, flushed once in-game
        private struct PendingCard
        {
            public bool IsAdd;
            public int Amount;
            public CardData Card;
        }
        private readonly List<PendingCard> _pendingCardDeltas = new();

        // A client removes a selected grading card from its album before sending the grading
        // intent. The host has already accepted that removal by the time the intent arrives, so
        // retain the ownership transfer per sender until grading consumes it. Entries are
        // created only for card deltas this host applied after its normal ownership checks.
        private readonly Dictionary<int, Dictionary<string, int>> _gradingOwnership = new();
        private readonly Dictionary<int, Dictionary<string, CardData>> _gradingOwnershipCards = new();
        internal const int CardDeltaBatchMax = 200; // deltas per CardDeltaBatch frame
        internal const int CardDeltaAmountMax = 1024;
        private readonly List<PendingCard> _batchRelayBuf = new();

        // The per-delta apply line is the field-log diagnosis for "cards didn't show up in the
        // binder", so it survives verbatim for ordinary changes (<=5 applied in a frame) and
        // folds into one summary line for a flood. Emitted by FlushFrameCardWork.
        private static readonly List<PendingCard> _deltaLogBuf = new();
        private static int _deltaAppliedThisFrame;
        private static int _deltaNetThisFrame;

        /// <summary>Returns true when the delta was actually applied - the host's relay to
        /// OTHER guests keys off this, so a delta this side REFUSED (corrupt grade, would-go-
        /// negative registry mismatch) is never propagated onward and can't spread divergence.
        /// <paramref name="relayAnyway"/> separates "THIS PC lacks the content" from "this delta
        /// is garbage", exactly as the price path does: identical registries do NOT imply
        /// identical installed data (EPL seeds enum ids from enum_values.json even for bundles
        /// that aren't installed), so the host can fully RESOLVE a card it has no data row for.
        /// Refusing it locally is right; swallowing it is not - a third player who DOES have the
        /// pack must still receive it, which is what the 3+ player regression was. True for the
        /// CardSetInstalledHere refusal and the graded-remove album mismatch; false for a
        /// corrupt grade, a would-go-negative reduce, and any host-side validation failure.</summary>
        private static bool ApplyCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            if (!TryValidateCardDelta(card, amount, out var invalidReason))
            {
                CoopPlugin.Log.LogWarning("card delta refused: " + invalidReason);
                return false;
            }
            // A cardGrade > 10 is NOT corruption when Grading Overhaul is installed: it's an
            // ENCODED grade (company + 1-10 grade + cert serial). The old hard 1-10 drop-guard
            // discarded every real graded card. Only a >10 grade WITHOUT Grading Overhaul is
            // impossible/genuine corruption (vanilla only writes 1-10), so still refuse that.
            if (card.cardGrade != 0 && (card.cardGrade < 1 || card.cardGrade > 10) && !GradingApi.Present)
            {
                CoopPlugin.Log.LogWarning($"card delta: dropping corrupt graded card {CardIdent(card)} (grade {card.cardGrade}) - not applied (Grading Overhaul absent)");
                return false;
            }
            var previousRemote = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            try
            {
                // UNKNOWN-CARD guard - the mirror of the negative-reduce guard below, and the
                // price of tolerating extra registry entries in the handshake (FIX C): a card
                // can now arrive for a content pack THIS PC doesn't have. Every branch below
                // resolves the card's slot through CPlayerData.GetCardSaveIndex, whose loop over
                // InventoryBase.GetShownMonsterList simply leaves the index at 0 when the monster
                // isn't in the list - and GetShownMonsterList itself falls back to the TETRAMON
                // list for an expansion outside the vanilla switch. So on the vanilla path an
                // unknown card doesn't error: it silently credits save index 0, i.e. the
                // receiver's FIRST Tetramon card, quietly inflating a real card's count (and,
                // for a graded one, filing a bogus entry in the graded album). Under EPL the same
                // lookup is an IndexOf that returns -1, and CardCountList[-1] THROWS.
                // Guards ALL THREE branches (it used to sit inside the add arm only, leaving the
                // graded-remove and ungraded-reduce paths to reach GetCardSaveIndex unguarded).
                // Returns false so the host's relay doesn't spread it.
                if (!CardSetInstalledHere(card))
                {
                    // RELAY ANYWAY: the delta is well-formed, this PC just has no data row for
                    // that card. Other peers may well have the pack, and before 1.0.37 this
                    // case reached the ungraded-reduce arm, returned true (a vanilla no-op) and
                    // so kept relaying - dropping the relay is what broke 3+ player sessions.
                    relayAnyway = true;
                    // Memoized per card slot, like the price path: one host log showed 995
                    // identical lines. Printed through CardIdent because an ordinary modded id
                    // is a small ORDINAL: below ~122 the bare monsterType renders an unrelated
                    // VANILLA name, at 123+ it renders a bare number (EMonsterType has no
                    // members up there) - neither identifies the card without the expansion.
                    if (MarkCardWarned("missing:" + CardPriceKey(card)))
                    {
                        CoopPlugin.Log.LogWarning($"card delta: {CardIdent(card)} is from a card set you don't have installed - skipped");
                    }

                    return false;
                }
                if (isAdd)
                {
                    if (!CanAddCardAmount(card, amount))
                    {
                        CoopPlugin.Log.LogWarning("card delta would overflow the local inventory for "
                            + CardIdent(card));
                        return false;
                    }
                    // Register the host's cert with Grading Overhaul BEFORE AddCard, so its
                    // anti-cheat AddCard prefix sees the cert burned+bound and does NOT
                    // re-encode this card as FAKE (the ~20s changing-grade churn). BindCert
                    // is keyed by cardSaveIndex/expansion/isDestiny, so it survives AddCard's
                    // compaction into a fresh CompactCardDataAmount.
                    if (card.cardGrade > 10)
                    {
                        GradingApi.Remember(card);
                    }

                    CPlayerData.AddCard(card, amount);
                }
                else if (card.cardGrade > 0)
                {
                    // graded cards live in m_GradedCardInventoryList; ReduceCard would miss
                    // them and wrongly decrement the ungraded array. Route through
                    // RemoveGradedCard (the graded-remove mirror normally arrives as
                    // GradedRemoveMessage; this defends the CardDelta path too).
                    // Count what actually came out: removing NOTHING (album never had it -
                    // the graded analog of the registry mismatch below) must report
                    // not-applied so the host relay doesn't propagate a remove we refused.
                    var removed = 0;
                    for (var i = 0; i < amount && CPlayerData.HasGradedCardInAlbum(card); i++)
                    {
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                        removed++;
                    }
                    if (removed == 0)
                    {
                        CoopPlugin.Log.LogWarning($"graded remove: {CardIdent(card)} (grade {card.cardGrade}) not in this album - skipped (album mismatch?)");
                        // RELAY ANYWAY, by the same argument that gave CardSetInstalledHere its
                        // relay above: THIS album saying nothing about a card says nothing about
                        // a THIRD peer's album. Before this the GradedRemove handler broke without
                        // relaying and the third player kept a ghost copy forever. No effect on a
                        // 2-player session.
                        relayAnyway = true;
                        return false;
                    }
                }
                else
                {
                    // Negative-reduce guard: a remove that outruns what this side actually owns
                    // silently underflows the collected-count array (ReduceCard just subtracts),
                    // which is the slow "total value drifts" leak. GetCardAmount resolves the
                    // owned count through the SAME GetCardSaveIndex + per-expansion collected list
                    // that ReduceCard decrements, so it's the exact amount the apply would hit.
                    // (Graded cards - cardGrade > 10 - never reach here; they route through
                    // RemoveGradedCard above.)
                    // No null-collected-list arm here any more: CardSetInstalledHere above owns
                    // that case (it refuses when GetCardCollectedList is null) and relays it on.
                    var owned = CPlayerData.GetCardAmount(card);
                    if (owned < amount)
                    {
                        CoopPlugin.Log.LogWarning($"card delta would drive {CardIdent(card)} negative (have {owned}, remove {amount}) - skipped (card registry mismatch?)");
                        return false;
                    }
                    CPlayerData.ReduceCard(card, amount);
                }
            }
            finally { ApplyingRemoteCards = previousRemote; }
            // "cards didn't show up in the binder" reports were undiagnosable from the
            // receiving side - applies were completely silent. The line still goes out for an
            // ordinary change; a bulk collect (hundreds of deltas in one frame) folds into one
            // summary instead of its own log flood. Both are emitted by FlushFrameCardWork.
            // The open-binder mirror also learns about this apply from the observer patches
            // (RecordCardChanged) - the mutation above ran behind ApplyingRemoteCards.
            _deltaAppliedThisFrame++;
            _deltaNetThisFrame += isAdd ? amount : -amount;
            if (_deltaLogBuf.Count < 5)
            {
                _deltaLogBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
            }
            return true;
        }

        private static bool ApplyTrustedCardDelta(bool isAdd, int amount, CardData card)
        {
            var previousRemote = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            try
            {
                if (isAdd)
                {
                    if (card.cardGrade > 10)
                    {
                        GradingApi.Remember(card);
                    }

                    if (card.cardGrade > 0)
                    {
                        for (var i = 0; i < amount; i++)
                        {
                            CPlayerData.AddCard(SnapshotCard(card), 1);
                        }
                    }
                    else
                    {
                        CPlayerData.AddCard(card, amount);
                    }
                }
                else if (card.cardGrade > 0)
                {
                    var gradedBefore = CPlayerData.m_GradedCardInventoryList?.Count ?? 0;
                    for (var i = 0; i < amount; i++)
                    {
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                    }

                    var gradedAfter = CPlayerData.m_GradedCardInventoryList?.Count ?? 0;
                    if (gradedAfter == gradedBefore)
                    {
                        // This authoritative removal consumed a certificate this client had
                        // already removed itself - one of its own in-flight picks. That pick's
                        // rollback must not put the row back.
                        MarkRemoteGradedRemoval(card);
                    }
                }
                else
                {
                    CPlayerData.ReduceCard(card, amount);
                }
            }
            finally
            {
                ApplyingRemoteCards = previousRemote;
            }

            _deltaAppliedThisFrame++;
            _deltaNetThisFrame += isAdd ? amount : -amount;
            if (_deltaLogBuf.Count < 5)
            {
                _deltaLogBuf.Add(new PendingCard
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
            }

            return true;
        }

        /// <summary>Applies one authoritative card delta on this client. A delta that arrives
        /// during a scene load is held instead of applied mid-load (applying then crashes into
        /// uninitialized card data); nothing is lost, <see cref="FlushPendingCardWork"/> applies it
        /// once the world is ready.</summary>
        private bool ApplyOrHoldCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            if (!InGameLevel())
            {
                _pendingCardDeltas.Add(new PendingCard
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
                return false;
            }

            return ApplyTrustedCardDelta(isAdd, amount, card);
        }

        /// <summary>True when a graded removal request could not be applied although this host HAS
        /// the card set installed: the album simply has no matching certificate, i.e. another
        /// player's accepted removal consumed the row while the request was in flight (or the
        /// sender's replica is stale). The request is refused so the sender's prediction rolls
        /// back and its held card is despawned; the authoritative removal still reaches every
        /// other peer, and the actor's response carries the host's count so its rollback cannot
        /// resurrect a ghost certificate. A host that cannot represent the card keeps the plain
        /// relay-anyway accept path.</summary>
        private static bool GradedRemoveUncovered(CardData card)
            => card != null && card.cardGrade > 0 && CardSetInstalledHere(card);

        /// <summary>A private copy of exactly the nine fields the wire carries. Anything that
        /// DEFERS a send must snapshot: AddCardPostfix temporarily writes the
        /// ENCODED grade into the game's live cardData and restore it in a finally, so reading
        /// the same object a frame later would ship the bare 1-10 grade instead.</summary>
        internal static CardData SnapshotCard(CardData c)
        {
            if (c == null)
            {
                return null;
            }

            return new CardData
            {
                expansionType = c.expansionType,
                monsterType = c.monsterType,
                borderType = c.borderType,
                isFoil = c.isFoil,
                isDestiny = c.isDestiny,
                isChampionCard = c.isChampionCard,
                isNew = c.isNew,
                cardGrade = c.cardGrade,
                gradedCardIndex = c.gradedCardIndex,
            };
        }

        private static bool TryValidateCardDelta(CardData card, int amount, out string reason)
        {
            reason = null;
            if (card == null)
            {
                reason = "card is null";
                return false;
            }
            if (amount <= 0 || amount > CardDeltaAmountMax)
            {
                reason = "amount is outside the bounded positive range";
                return false;
            }
            if (card.expansionType == ECardExpansionType.None
                || card.monsterType == EMonsterType.None
                || !Enum.IsDefined(typeof(ECardBorderType), card.borderType))
            {
                reason = "card enum fields are invalid";
                return false;
            }
            if (card.isChampionCard || card.gradedCardIndex < 0 || card.gradedCardIndex > 1000000
                || card.cardGrade < 0)
            {
                reason = "card identity fields are invalid";
                return false;
            }

            try
            {
                var encoded = GradingApi.Encoded(card);
                if (encoded < 0 || (encoded > 10 && !GradingApi.Present)
                    || (encoded > 10 && (GradingApi.Actual(encoded) < 1
                        || GradingApi.Actual(encoded) > 10)))
                {
                    reason = "card grade is invalid for the installed grading backend";
                    return false;
                }
                if (encoded == 0 && card.gradedCardIndex != 0)
                {
                    reason = "an ungraded card carried a graded-card index";
                    return false;
                }

                if (!CardSetInstalledHere(card))
                {
                    // The caller may relay a well-formed card to a peer with the content pack;
                    // save-index consistency cannot be proved on a machine without that pack.
                    return true;
                }

                var saveIndex = CPlayerData.GetCardSaveIndex(card);
                var collected = CPlayerData.GetCardCollectedList(card.expansionType, card.isDestiny);
                if (saveIndex < 0 || collected == null || saveIndex >= collected.Count)
                {
                    reason = "card save index is outside the live collection";
                    return false;
                }
                var canonical = CPlayerData.GetCardData(saveIndex, card.expansionType,
                    card.isDestiny);
                if (canonical == null || canonical.expansionType != card.expansionType
                    || canonical.monsterType != card.monsterType)
                {
                    // The card does not resolve back to the slot it claimed (the sender resolved
                    // an ordinal through a different shown list, or a cross-build card). Applying
                    // it would credit some other card's slot, so it stays refused and both sides
                    // are logged with raw ordinals so the divergent field is visible next time.
                    reason = "card fields do not match their save index (index=" + saveIndex
                        + " sent=" + CardFieldDump(card) + " canonical=" + CardFieldDump(canonical)
                        + ")";
                    return false;
                }
                if (canonical.borderType != card.borderType
                    || canonical.isFoil != card.isFoil
                    || canonical.isDestiny != card.isDestiny
                    || canonical.isChampionCard != card.isChampionCard)
                {
                    // Field logs (2.0.0, paired host+client): a guest's modded pull was rolled
                    // back and lost because the same NAMED content pack was defined differently
                    // at this save slot on the two PCs - a pack update on one side, or two
                    // bundles declaring one expansion name. For a VANILLA expansion the border
                    // and foil ARE part of the save index, so a mismatch can only be a bogus or
                    // cross-build card and stays refused. For a modded (EPL-style) expansion the
                    // slot is the monster ordinal (plus the foil bit only when the pack rolls
                    // random foils); the border and the other definition fields never move it,
                    // so this host can represent the card exactly as its own album defines that
                    // slot - 1.0.x applied it the same way. Refusing deleted the pull from BOTH
                    // sides (the sender undoes its prediction), which is the reported "modded
                    // cards vanish when a guest opens packs". Accept it, and say so once per
                    // slot so content drift is still visible in the log.
                    if (IsVanillaCardExpansion(card.expansionType))
                    {
                        reason = "card fields do not match their save index (index=" + saveIndex
                            + " sent=" + CardFieldDump(card) + " canonical="
                            + CardFieldDump(canonical) + ")";
                        return false;
                    }

                    WarnCardDefinitionDivergence(card, canonical, saveIndex);
                }
            }
            catch (Exception error)
            {
                reason = "card save identity could not be validated: " + error.Message;
                return false;
            }
            return true;
        }

        private static bool CanAddCardAmount(CardData card, int amount)
        {
            if (card == null || amount <= 0)
            {
                return false;
            }

            if (card.cardGrade <= 0)
            {
                var owned = CPlayerData.GetCardAmount(card);
                return owned >= 0 && owned <= int.MaxValue - amount;
            }

            var rows = CPlayerData.m_GradedCardInventoryList;
            return rows != null && rows.Count <= int.MaxValue - amount;
        }

        /// <summary>Canonical identity of a card's MARKED PRICE - everything the price store
        /// keys on and nothing else (gradedCardIndex is a per-copy serial, isNew is cosmetic).
        /// Used both as the ownership key and as the human-readable id in the price logs.</summary>
        private static string CardPriceKey(CardData card)
        {
            if (card == null)
            {
                return null;
            }

            var encoded = GradingApi.Encoded(card);
            return (int)card.expansionType + ":" + (int)card.monsterType + ":" + (int)card.borderType
                + ":" + (card.isFoil ? 1 : 0) + (card.isDestiny ? 1 : 0) + (card.isChampionCard ? 1 : 0)
                + ":" + encoded;
        }

        private static string CardOwnershipKey(CardData card)
        {
            // gradedCardIndex is a local compact-inventory position on the legacy backend,
            // so it cannot be used as a cross-peer provenance key.
            return CardPriceKey(card);
        }

        /// <summary>Per-expansion set of the monster ids that genuinely have a data row on THIS
        /// machine, taken from InventoryBase.GetShownMonsterList - the one list EPL prefixes, so
        /// it reports the expansion's real card keys on the modded path and the vanilla ones on
        /// the vanilla path. Built lazily and kept for the session (the shown lists are
        /// ScriptableObject content: they do not change while the game runs).</summary>
        private static readonly Dictionary<ECardExpansionType, HashSet<EMonsterType>> _shownMonsters =
            new();

        /// <summary>Drop the shown-monster cache. Called from world reset beside the other
        /// session state:
        /// the next session may load a different save/content set, and a stale membership set
        /// would either refuse cards this install now has or accept ones it doesn't. The
        /// once-per-slot warning memo is dropped with it, since a rejoin may legitimately have
        /// new content to warn about.</summary>
        internal static void ClearCardSetCache()
        {
            _shownMonsters.Clear();
            _cardWarnedKeys.Clear();
            _loggedCertKeys.Clear();
        }

        /// <summary>Card slots a warning has already been printed for this session, so a repeated
        /// pull of one card cannot flood the log (pre-2.0 memoized these; the 2.0 rewrite lost the
        /// memo but kept the comment promising it). Bounded: once the cap is reached new keys are
        /// not remembered - a warning must never grow memory without limit.</summary>
        private static readonly HashSet<string> _cardWarnedKeys = new(StringComparer.Ordinal);
        private const int CardWarnedKeysMax = 512;

        /// <summary>Certificates already logged this session, so a bulk reveal prints each cert
        /// once instead of once per card and cannot flood the log. Bounded like the warn memo.</summary>
        private static readonly HashSet<string> _loggedCertKeys = new(StringComparer.Ordinal);
        private const int LoggedCertKeysMax = 2048;

        /// <summary>True for the expansions the GAME itself ships: exactly the members below
        /// ECardExpansionType.MAX. Vanilla save indices are dense - they pack the shown-list
        /// position, the border and (on expansions with a foil slot) the foil bit into one slot -
        /// so border/foil ARE part of the slot identity. Every modded expansion is minted by EPL's
        /// prepatcher at and above 200000 (EnumPatcher's startingValue), far outside the game's
        /// enum space, and its save index is the monster ordinal alone (plus the foil bit when the
        /// pack rolls random foils) - which is exactly why a definition mismatch is benign there
        /// and fatal here. Range, not a member list: a game update that adds an expansion before
        /// MAX is included automatically, and CardIdent already draws the same line.</summary>
        internal static bool IsVanillaCardExpansion(ECardExpansionType expansion)
            => (int)expansion >= (int)ECardExpansionType.Tetramon
                && (int)expansion < (int)ECardExpansionType.MAX;

        /// <summary>Returns true the first time this key is warned in a session, so the caller can
        /// log exactly once per card slot. Never throws and never grows past the cap.</summary>
        private static bool MarkCardWarned(string key)
        {
            if (key == null || _cardWarnedKeys.Contains(key)
                || _cardWarnedKeys.Count >= CardWarnedKeysMax)
            {
                return false;
            }

            _cardWarnedKeys.Add(key);
            return true;
        }

        /// <summary>Host: the sender's copy of a modded content pack defines this save slot
        /// differently (a pack update on one side, or two bundles declaring one expansion name).
        /// The slot is still valid here, so the delta is applied - this only makes the content
        /// drift visible, once per slot, with both raw definitions side by side.</summary>
        private static void WarnCardDefinitionDivergence(CardData card, CardData canonical,
            int saveIndex)
        {
            if (!MarkCardWarned("diverged:" + (int)card.expansionType + ":" + (int)card.monsterType
                + ":" + (card.isFoil ? 1 : 0)))
            {
                return;
            }

            CoopPlugin.Log.LogWarning("card delta: " + CardIdent(card)
                + " is defined differently on this host (index=" + saveIndex
                + " sent=" + CardFieldDump(card) + " canonical=" + CardFieldDump(canonical)
                + "); the card is stored as this host defines that slot - make sure both players "
                + "installed the same version of the pack");
        }

        /// <summary>Membership test: does a data row for this monster exist under this expansion
        /// on this machine? Fills the cache on first ask, but NEVER caches a null-or-empty list -
        /// that means "InventoryBase isn't ready yet" (pre-load, or mid scene swap), and latching
        /// it would turn a timing miss into a permanent refusal for the rest of the session.</summary>
        private static bool MonsterHasDataRowHere(ECardExpansionType expansion, EMonsterType monster)
        {
            // FABRICATED-SINGLETON GATE. InventoryBase.GetShownMonsterList reads
            // CSingleton<InventoryBase>.Instance, and that getter does NOT return null when the
            // real inventory is absent (client reload window - InGameLevel() stays true there):
            // it FABRICATES one (new GameObject + AddComponent + DontDestroyOnLoad) and caches
            // it forever, so the fake permanently shadows the real inventory for the rest of the
            // run. Same house rule as everywhere else in this file - see the comment above Inv()
            // (~"NEVER CSingleton<>.Instance for scene-lifetime managers"). Asking Inv() first
            // (FindFirstObjectByType, fabricates nothing) both avoids that and makes the no-latch
            // not-ready refusal below actually reachable: without it this window threw an NRE
            // out of the fake's empty fields and landed in CardSetInstalledHere's catch.
            if (Inv() == null)
            {
                return false; // not ready - do not latch, do not fabricate
            }

            HashSet<EMonsterType> set;
            if (!_shownMonsters.TryGetValue(expansion, out set))
            {
                var shown = InventoryBase.GetShownMonsterList(expansion);
                if (shown == null || shown.Count == 0)
                {
                    return false; // not ready - do not latch
                }

                set = new HashSet<EMonsterType>(shown);
                _shownMonsters[expansion] = set;
            }
            return set.Contains(monster);
        }

        /// <summary>True when THIS install can actually place the card - i.e. a data row for
        /// (expansion, monster) really exists here. Anything else would mis-index through
        /// GetCardSaveIndex/GetShownMonsterList into save slot 0 (silent album corruption on the
        /// vanilla path) or into CardCountList[-1] (a throw on the EPL path), so it is refused.
        /// Errs toward REFUSING on any throw.
        ///
        /// TWO ORACLES THAT LOOK RIGHT AND ARE NOT - do not reinstate either:
        ///
        ///  1. Enum.IsDefined(typeof(EMonsterType), ...). EPL never MINTS EMonsterType members.
        ///     A modded expansion numbers its cards as plain ORDINALS - (EMonsterType)(index+1),
        ///     1..N - so the ids collide with whatever vanilla names happen to sit at those
        ///     numbers. That split the pack's own cards in two, which is what made the field
        ///     reports so confusing: ordinals 1..122 PASSED IsDefined by pure numeric collision
        ///     and synced SILENTLY (they never reached the refusal log at all, and they landed
        ///     in the save slot of the colliding vanilla card); ordinals 123 and up failed the
        ///     check on EVERY machine - including both players' - and were universally refused,
        ///     logging as BARE NUMBERS because EMonsterType simply has no members up there.
        ///     So "some of the modded cards work" was the collision half, and the missing cards
        ///     were the 123+ half. Note the ECardExpansionType half of the check IS legitimate -
        ///     expansions genuinely ARE EPL-minted enum members - which is exactly why the two
        ///     halves look symmetric and are not.
        ///
        ///  2. InventoryBase.GetMonsterData(...) != null. EPL does not patch that method; it
        ///     rewrites the game's own CALL SITES with a transpiler. A third-party caller like
        ///     this mod runs the ORIGINAL body, which for a modded id returns null or - worse -
        ///     the wrong vanilla monster's data by collision.
        ///
        /// The oracle that holds on both paths is per-expansion MEMBERSHIP in
        /// GetShownMonsterList, which EPL prefixes with the expansion's real card keys:
        /// membership means "a data row exists here", which is precisely the question.</summary>
        internal static bool CardSetInstalledHere(CardData card)
        {
            try
            {
                if (card == null)
                {
                    return false;
                }
                // Refuse the None sentinels explicitly. Since 1.0.37 an incoming modded id whose
                // NAME does not exist on this PC is translated to the game's own None member
                // (CatalogIdMap.FromWire) instead of arriving as a foreign number, and None is a
                // DEFINED member of both enums (ECardExpansionType.None = -1, EMonsterType.None
                // = 0). Neither value names a real card, so refusing them costs nothing.
                if (card.expansionType == ECardExpansionType.None)
                {
                    return false;
                }

                if (card.monsterType == EMonsterType.None)
                {
                    return false;
                }
                // Expansions ARE EPL-minted enum members, so IsDefined is the correct oracle
                // HERE (and only here - see the doc comment above).
                if (!Enum.IsDefined(typeof(ECardExpansionType), card.expansionType))
                {
                    return false;
                }
                // ...and the expansion must still be one this save actually has a collected list
                // for. Without EPL's interceptor woven in, GetCardCollectedList returns null for
                // an out-of-vocabulary expansion, and every downstream lookup would fall through
                // GetShownMonsterList's default arm onto the TETRAMON list - the slot-0 mis-index.
                if (CPlayerData.GetCardCollectedList(card.expansionType, card.isDestiny) == null)
                {
                    return false;
                }

                return MonsterHasDataRowHere(card.expansionType, card.monsterType);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("card set check: " + e.Message);

                return false;
            }
        }

        /// <summary>Diagnostic suffix carrying the decoded certificate of a graded card, so the
        /// per-delta log lines a field report quotes identify the exact slab (company, grade,
        /// serial). Empty for an ungraded card or without Grading Overhaul.</summary>
        private static string CertSuffix(CardData card)
        {
            if (card == null || card.cardGrade <= 0)
            {
                return string.Empty;
            }

            var cert = GradingApi.DescribeCert(card);
            return cert == null ? string.Empty : " [cert " + cert + "]";
        }

        /// <summary>Human-readable card id for the log lines that can now carry MODDED cards.
        /// A modded expansion numbers its cards as plain ORDINALS (1..N), so for anything at or
        /// past the vanilla expansion range the bare monsterType renders a completely unrelated
        /// vanilla member NAME by numeric collision - which is worse than useless in a field log.
        /// Vanilla expansions keep the readable name; modded ones print Expansion#N.
        ///
        /// None (= -1, NOT a missing member) needs its own arm: it is numerically BELOW MAX, so
        /// the vanilla branch used to claim it and render whatever monster name collides with that
        /// ordinal - a confidently wrong card name on exactly the rows where the expansion is the
        /// thing that failed to resolve (a card off the wire from a pack this PC lacks, or one
        /// whose expansion id did not map). Naming the unknown beats naming the wrong card.</summary>
        private static string CardIdent(CardData c)
        {
            if (c == null)
            {
                return "(null card)";
            }

            if (c.expansionType == ECardExpansionType.None)
            {
                return "unknown-pack card #" + (int)c.monsterType;
            }

            if ((int)c.expansionType < (int)ECardExpansionType.MAX)
            {
                return c.monsterType.ToString();
            }

            return c.expansionType + "#" + (int)c.monsterType;
        }

        /// <summary>Raw-ordinal field dump for the save-index consistency refusal. CardIdent
        /// alone cannot show WHICH field diverged, which is the whole question when the same
        /// monster resolves to a different save index on the two peers.</summary>
        private static string CardFieldDump(CardData c)
        {
            if (c == null)
            {
                return "(null card)";
            }

            return CardIdent(c) + "[exp=" + (int)c.expansionType + " monster=" + (int)c.monsterType
                + " border=" + (int)c.borderType + (c.isFoil ? " foil" : "")
                + (c.isDestiny ? " destiny" : "") + (c.isChampionCard ? " champion" : "")
                + " grade=" + c.cardGrade + " gidx=" + c.gradedCardIndex + "]";
        }

        /// <summary>Warn when a card cannot be processed locally.</summary>
        internal static void WarnRefusedCard(CardData c, string context)
        {
            if (c == null)
            {
                return;
            }

            CoopPlugin.Log.LogWarning($"{context}: {c.expansionType}#{(int)c.monsterType} is from a card set this PC doesn't have - the card could NOT be processed here");
        }

        // Resolve the live binder through the non-fabricating scene reference.
        private static readonly System.Reflection.FieldInfo FiBinderIsBookOpen =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsBookOpen");
        // Extra binder internals we read to recompute the OPEN album's total-value text after a
        // card delta - the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) branches on
        // these to pick which SetTotalValue variant to call. All private, so AccessTools-cached.
        private static readonly System.Reflection.FieldInfo FiBinderUI =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_CollectionBinderUI");
        private static readonly System.Reflection.FieldInfo FiBinderIsGradedAlbum =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsGradedCardAlbum");
        private static readonly System.Reflection.FieldInfo FiBinderExpansionType =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_ExpansionType");

        /// <summary>End-of-frame catch-up for a card change while the collection binder is up.
        /// SetCanUpdateSort alone only ARMS the game's own open-time re-sort (the vanilla
        /// per-frame Update never consumes it while the book is open), so the open binder
        /// would stay stale. We deliberately do NOT invoke OnSortingMethodUpdated on a live
        /// book: that moved every card to a new slot and left the emptied slots' raycast
        /// proxies behind, which is the reported "card moved there but cannot be taken".
        /// Instead ApplyOpenBinderCardChanges rewrites only the affected slots the way vanilla
        /// itself does when a card is taken out, arming the re-sort so holes heal on reopen.
        /// The total-value text is refreshed here because the open path is the only other
        /// writer of it.</summary>
        private static void RefreshOpenBinder()
        {
            try
            {
                var ipc = SceneRef<InteractionPlayerController>.Get();
                var ctrl = ipc?.m_CollectionBinderFlipAnimCtrl;
                if (ctrl == null)
                {
                    return;
                }

                ctrl.SetCanUpdateSort(canSort: true);
                var isOpen = FiBinderIsBookOpen != null && (bool)FiBinderIsBookOpen.GetValue(ctrl);
                if (isOpen)
                {
                    ApplyOpenBinderCardChanges(ctrl);
                }

                // The binder's own open path is the only other writer of the total-value text -
                // it never updates while the book is open. So a traded/pulled card showed up on
                // the page but the "total value" header stayed stale (the reported "total value
                // differs"). Mirror the exact SetTotalValue call the open path
                // (CollectionBinderFlipAnimCtrl.Update ~807-830) would make for the CURRENTLY
                // open album. Behind the isOpen guard so a delta with no binder up costs nothing.
                if (isOpen && FiBinderUI != null)
                {
                    var ui = FiBinderUI.GetValue(ctrl) as CollectionBinderUI;
                    if (ui != null)
                    {
                        var isGraded = FiBinderIsGradedAlbum != null && (bool)FiBinderIsGradedAlbum.GetValue(ctrl);
                        var expansion = FiBinderExpansionType != null
                            ? (ECardExpansionType)FiBinderExpansionType.GetValue(ctrl)
                            : ECardExpansionType.None;
                        if (isGraded)
                        {
                            // graded album: sum GetCardMarketPrice over the graded inventory - and
                            // DELIBERATELY NOT the way the open path's loop (~810-819) does it.
                            //
                            // DO NOT "MAKE THIS MATCH VANILLA" AGAIN. Vanilla's loop WRITES
                            // m_GradedCardInventoryList[i].amount = 10 on every row over 10.
                            // CompactCardDataAmount is a CLASS (decompiled/CompactCardDataAmount.cs:4)
                            // and that list is the LIVE save list CGameData hands to the serializer
                            // BY REFERENCE (decompiled/CGameData.cs:1119 -> SetLoadData's
                            // `data = loadData` at :1019), so the write is a write to the save. With
                            // Grading Overhaul installed `amount` is not an amount at all: it is the
                            // ENCODED grade, packing grading company + the real 1-10 + the certificate
                            // serial (CPlayerData.AddCard stores cardGrade straight into it,
                            // decompiled/CPlayerData.cs:1506). Clamping it replaces every certificate
                            // in the album with a bare 10, permanently and world-wide, and it is
                            // UNRECOVERABLE - GO's own repair sweeps all skip amount <= 10
                            // (decompiled-grading :8266, :8652, :8804, :8901) and its
                            // EncodedGradeRegistry is keyed by CardData REFERENCE (:5881) while
                            // GetGradedCardData mints a fresh CardData every call (:1689).
                            //
                            // Ours was strictly worse than vanilla's, which is why this had to
                            // diverge rather than be left alone: vanilla runs that loop only on the
                            // binder OPEN transition (m_OpenBinder && !m_IsBookOpen,
                            // decompiled/CollectionBinderFlipAnimCtrl.cs:766), whereas this method is
                            // armed on the success tail of every applied card delta (~1333) and by
                            // the graded-adopt path, then drained per frame - so it re-ran on an
                            // ALREADY-OPEN book. GradeDataLifeSaver, the community fix, transpiles
                            // vanilla's clamp away but patches CollectionBinderFlipAnimCtrl.Update,
                            // so it can never reach a copy compiled into CardShopCoop.dll.
                            //
                            // So: READ the row, never write it. GetGradedCardData returns a brand-new
                            // CardData every call (:1689-1701), so nothing done to that throwaway
                            // copy can reach the save.
                            //
                            // AND WITH GO PRESENT, HAND ITS GRADE TO GetCardMarketPrice ENCODED AND
                            // UNTOUCHED. Do NOT "decode it first so vanilla sees a real 1-10" -
                            // vanilla already does. GO's
                            // PricingPatch_MarketPrice_GetMarketPrice.Prefix takes `ref int cardGrade`
                            // and does `if (cardGrade > 10) cardGrade = Helper.GetActualGrade(cardGrade)`
                            // (decompiled-grading :13697-13708), so MarketPrice.GetMarketPrice's body
                            // runs on the decoded value either way. Pre-decoding buys nothing there
                            // and COSTS the whole company multiplier: GO's postfix on
                            // CPlayerData.GetCardMarketPrice - PricingFix_GetCardMarketPrice_UseRegistry
                            // (:15052-15069) - reads the ENCODED value back off the card and applies
                            // nothing unless Helper.TryGetCompanyFromGrade accepts it, and that
                            // returns FALSE for every value in 1-10 (:16026-16035). Note the registry
                            // read it does that through is keyed by CardData REFERENCE (:5881, :5929),
                            // so for a fresh copy like this one it can only ever return the field we
                            // just set - the value we pass IS the value that decides the multiplier.
                            // Dropping it is not a rounding error: Cardinals 10 is 3x, PSA 10 is
                            // 16.43x, Beckett 10 is 21x and a Beckett Black Label multiplies that by
                            // 5 again for 105x (tables :1867-1881, applied at :1925-1975). A decoded
                            // copy therefore understates a top-grade album by up to 105x - and
                            // OVERSTATES a low-grade one, since grades 1-6 multiply by less than 1.
                            // An earlier version of this comment claimed that cost "a few percent";
                            // that was false, and it is why the decode is gone.
                            //
                            // The clamp survives for the NO-GO case ONLY. With GO absent nothing
                            // decodes an encoded grade on the way in, and vanilla indexes
                            // `(index * 10 + (cardGrade - 1)) % list.Count` (:1415-1420 ->
                            // MarketPrice.cs:18) - the `%` means an encoded int WRAPS rather than
                            // throws, so it cannot crash, it just totals a meaningless slot. Present
                            // is "GO's assembly loaded and its API resolved" in the grading module,
                            // which is also the only condition under which anything on this PC could
                            // have written an encoded grade in the first place. It does NOT track
                            // GO's own ConfigSettings.EnableMod, which gates both patches above; a
                            // player who installs GO and then disables it in config gets the same
                            // meaningless-slot number here, for the same harmless reason.
                            var goPresent = GradingApi.Present;
                            var total = 0f;
                            for (var i = 0; i < CPlayerData.m_GradedCardInventoryList.Count; i++)
                            {
                                var row = CPlayerData.m_GradedCardInventoryList[i];
                                if (row == null)
                                {
                                    continue;
                                }
                                // PER-ROW, never around the loop: one bad row must not abandon the
                                // rest of the total. Same hazard as the compact-row walk documented
                                // on the grading module's compact-card decoder, and it is NOT the divide by
                                // zero an earlier draft of this comment claimed.
                                // GetCardAmountPerMonsterType initialises num = 6 BEFORE its switch,
                                // every case assigns 6 (or 1 for Ghost), and there is no default arm
                                // (decompiled/CPlayerData.cs:692-721, the init at :694), so it
                                // returns 6 or 12 for an expansion it has never heard of and cannot
                                // return 0.
                                //
                                // What can actually throw is an out-of-range INDEX, and BOTH calls
                                // inside this try can do it for a row whose card set this install
                                // does not have. GetGradedCardData resolves the monster through
                                // GetMonsterTypeFromCardSaveIndex, which indexes
                                // InventoryBase.GetShownMonsterList(exp)[cardSaveIndex / perType]
                                // (:790-793) - a list that falls back to TETRAMON's for an unknown
                                // expansion (decompiled/InventoryBase.cs:290-308). GetCardMarketPrice
                                // then indexes m_GenCardMarketPriceList[GetCardSaveIndex(card)]
                                // (:1415-1420), and that list is sized from THIS install's own
                                // GetCardCollectionDataCount() + 100 (:491), so a high enough index
                                // runs off the end. Skipping such a row costs its value in one
                                // header total, which is the trade this loop wants.
                                //
                                // Verified against vanilla and Grading Overhaul (GO's only reference
                                // to GetCardAmountPerMonsterType is a read, decompiled-grading
                                // :7132). EPL is not visible from this repo and could patch it, so
                                // the catch - not the invariant - is what makes this safe.
                                try
                                {
                                    var copy = CPlayerData.GetGradedCardData(row);
                                    if (!goPresent && copy.cardGrade > 10)
                                    {
                                        copy.cardGrade = 10;
                                    }

                                    total += CPlayerData.GetCardMarketPrice(copy);
                                }
                                catch { continue; }
                            }
                            ui.SetTotalValue(total);
                        }
                        else if (expansion == ECardExpansionType.Ghost)
                        {
                            // Ghost/dimension album sums both the normal and dimension halves (~824).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false)
                                + CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: true));
                        }
                        else
                        {
                            // normal expansion album (~829).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false));
                        }
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"binder relayout after card change failed: {e.Message}"); }
        }

        private void FlushPendingCardWork()
        {
            if (!InGameLevel() || _pendingCardDeltas.Count == 0)
            {
                return;
            }

            var count = _pendingCardDeltas.Count;
            for (var i = 0; i < count; i++)
            {
                var pending = _pendingCardDeltas[i];
                ApplyTrustedCardDelta(pending.IsAdd, pending.Amount, pending.Card);
            }

            CoopPlugin.Log.LogInfo("applied " + count + " card change(s) held during loading");
            _pendingCardDeltas.Clear();
        }

        private static CardDeltaBatchMessage ToAuthoritative(ICardDeltaBatchSource message)
        {
            var result = new CardDeltaBatchMessage();
            if (message?.Deltas == null)
            {
                return result;
            }

            for (var i = 0; i < message.Deltas.Count; i++)
            {
                var delta = message.Deltas[i];
                result.Deltas.Add(new CardDeltaEntry
                {
                    IsAdd = delta.IsAdd,
                    Amount = delta.Amount,
                    Card = SnapshotCard(delta.Card),
                });
            }

            return result;
        }

        private static CardDeltaBatchMessage ToBatchMessage(List<PendingCard> deltas)
        {
            var result = new CardDeltaBatchMessage();
            for (var i = 0; deltas != null && i < deltas.Count; i++)
            {
                result.Deltas.Add(new CardDeltaEntry
                {
                    IsAdd = deltas[i].IsAdd,
                    Amount = deltas[i].Amount,
                    Card = deltas[i].Card,
                });
            }

            return result;
        }

        /// <summary>End of frame: flush card-delta diagnostics and mirror everything applied this
        /// frame into the binder, if one is open.</summary>
        private void FlushFrameCardWork()
        {
            if (_deltaAppliedThisFrame > 0)
            {
                if (_deltaAppliedThisFrame <= 5)
                {
                    for (var i = 0; i < _deltaLogBuf.Count; i++)
                    {
                        var d = _deltaLogBuf[i];
                        CoopPlugin.Log.LogInfo($"card delta applied: {(d.IsAdd ? "+" : "-")}{d.Amount} {CardIdent(d.Card)}{(d.Card.cardGrade > 0 ? $" (grade {d.Card.cardGrade})" : d.Card.isFoil ? " (foil)" : "")}{CertSuffix(d.Card)}");
                    }
                }
                else
                {
                    CoopPlugin.Log.LogInfo($"applied {_deltaAppliedThisFrame} card deltas (net "
                        + (_deltaNetThisFrame >= 0 ? "+" : "") + _deltaNetThisFrame + " cards)");
                }

                _deltaLogBuf.Clear();
                _deltaAppliedThisFrame = 0;
                _deltaNetThisFrame = 0;
            }
            if (_binderRefreshPending)
            {
                _binderRefreshPending = false;
                RefreshOpenBinder();
                ClearBinderMirrorFrame();
            }
        }

    }
}
