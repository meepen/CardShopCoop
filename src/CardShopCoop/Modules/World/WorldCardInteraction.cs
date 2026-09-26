using System;
using System.Collections.Generic;
using GradedRemoveMessage = CardShopCoop.Modules.World.GradedRemoveMessage;
using CardShopCoop.Net;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.SaveTransfer;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    internal sealed partial class WorldCardInteraction
    {
        private readonly CoopRuntimeContext _context;
        private readonly bool _host;

        internal Action<INetMessage> BroadcastOverride;
        internal Action<int, INetMessage> RelayOverride;
        internal Action<int, INetMessage> SendOverride;

        internal readonly WorldContainerInteraction Containers;
        internal readonly WorldMarketInteraction Market;
        private readonly WorldGradingBridge _gradingBridge;

        internal static bool ApplyingRemoteCards;

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

        private int ConnectionCount => _context.ConnectionIds?.Invoke()?.Count ?? 0;

        private List<int> ConnectionIds()
        {
            var ids = _context.ConnectionIds?.Invoke();
            return ids == null ? new List<int>() : new List<int>(ids);
        }

        internal static InventoryBase Inv()
            => SceneRef<InventoryBase>.Get();

        /// <summary>Checks live display compartments rather than the collected-card cache.
        /// Vanilla removes the last displayed card from that cache while it remains owned by
        /// its shelf, so price editing must use the actual compartment as the authority.</summary>
        internal static bool TryGetDisplayedCard(CardData requested, int encodedGrade,
            out CardData displayed)
        {
            displayed = null;
            if (requested == null)
                return false;
            var shelves = SceneRef<ShelfManager>.Get();
            if (shelves == null)
                return false;

            if (TryFindDisplayedCard(shelves.m_CardShelfList, requested, encodedGrade,
                out displayed)
                || TryFindDisplayedCard(shelves.m_CardItemCombiShelfList, requested,
                    encodedGrade, out displayed))
                return true;
            return false;
        }

        private static bool TryFindDisplayedCard<T>(IList<T> shelves, CardData requested,
            int encodedGrade, out CardData displayed) where T : CardShelf
        {
            displayed = null;
            if (shelves == null)
                return false;
            for (var i = 0; i < shelves.Count; i++)
            {
                var shelf = shelves[i];
                var compartments = shelf?.GetCardCompartmentList();
                if (compartments == null)
                    continue;
                for (var j = 0; j < compartments.Count; j++)
                {
                    var stored = compartments[j]?.m_StoredCardList;
                    if (stored == null || stored.Count == 0 || stored[0] == null
                        || stored[0].m_Card3dUI?.m_CardUI == null)
                        continue;
                    var candidate = stored[0].m_Card3dUI.m_CardUI.GetCardData();
                    if (candidate == null || candidate.expansionType != requested.expansionType
                        || candidate.monsterType != requested.monsterType
                        || candidate.borderType != requested.borderType
                        || candidate.isFoil != requested.isFoil
                        || candidate.isDestiny != requested.isDestiny
                        || candidate.isChampionCard != requested.isChampionCard)
                        continue;
                    if (GradingApi.Encoded(candidate) != encodedGrade)
                        continue;
                    displayed = SnapshotCard(candidate);
                    displayed.cardGrade = encodedGrade;
                    return true;
                }
            }
            return false;
        }

        private void Send(int connectionId, INetMessage message)
        {
            if (SendOverride != null)
            {
                SendOverride(connectionId, message);
                return;
            }
            _context.Send(connectionId, message);
        }

        private void Broadcast(INetMessage message)
        {
            if (BroadcastOverride != null)
                BroadcastOverride(message);
            else
                _context.Broadcast(message);
        }

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
                Send(1, new CardDeltaRequestMessage
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
            }
        }

        /// <summary>Predicts one inventory mutation. The hook suppresses vanilla's original
        /// method and applies the same small ledger change from inside PredictionApi so the undo
        /// closure is available when the accepted host delta arrives.</summary>
        internal bool PredictClientCardDelta(CardData card, int amount, bool isAdd)
        {
            if (_host || ApplyingRemoteCards || card == null || amount <= 0
                || SaveTransferApi.PreloadHold)
                return false;

            var snapshot = SnapshotCard(card);
            if (GradingApi.Present)
                snapshot.cardGrade = GradingApi.Encoded(card);
            var intent = new CardDeltaRequestMessage
            {
                IsAdd = isAdd,
                Amount = amount,
                Card = snapshot,
            };
            WorldPrediction.Predict(WorldPrediction.CardsScope, intent,
                () =>
                {
                    ApplyPredictedCardDelta(snapshot, amount, isAdd);
                    NotifyCardsChanged();
                },
                () =>
                {
                    ApplyPredictedCardDelta(snapshot, amount, !isAdd);
                    NotifyCardsChanged();
                },
                () =>
                {
                    // Only THIS removal's own rejection returns the card; a cascade undo of a
                    // later prediction must not clear a selection that was legitimately reserved.
                    if (!isAdd)
                        GradingApi.OnCardRemovalRefused(snapshot, amount);
                });
            return true;
        }

        internal bool PredictClientGradedRemoval(CardData card)
        {
            if (_host || ApplyingRemoteCards || card == null
                || SaveTransferApi.PreloadHold)
                return false;

            var snapshot = SnapshotCard(card);
            if (GradingApi.Present)
                snapshot.cardGrade = GradingApi.Encoded(card);
            var intent = new GradedRemoveRequestMessage { Card = snapshot };
            WorldPrediction.Predict(WorldPrediction.CardsScope, intent,
                () =>
                {
                    ApplyPredictedGradedRemoval(snapshot);
                    NotifyCardsChanged();
                },
                () =>
                {
                    ApplyPredictedCardDelta(snapshot, 1, true);
                    NotifyCardsChanged();
                },
                () => GradingApi.OnCardRemovalRefused(snapshot, 1));
            return true;
        }

        private static void ApplyPredictedCardDelta(CardData card, int amount, bool isAdd)
        {
            var previousRemote = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            try
            {
                if (isAdd)
                {
                    if (card.cardGrade > 10)
                        GradingApi.Remember(card);
                    CPlayerData.AddCard(card, amount);
                }
                else if (card.cardGrade > 0)
                {
                    for (var i = 0; i < amount; i++)
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
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

        private static void ApplyPredictedGradedRemoval(CardData card)
        {
            var previousRemote = ApplyingRemoteCards;
            ApplyingRemoteCards = true;
            try
            {
                CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
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

            var message = new CardDeltaBatchRequestMessage { Deltas = entries };
            WorldPrediction.Predict(WorldPrediction.CardsScope, message,
                () => ApplyPredictedCardBatch(entries, false),
                () => ApplyPredictedCardBatch(entries, true));
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

        internal void SendCardDeltaTo(int connectionId, CardData card, int amount, bool isAdd)
        {
            if (!_host || ConnectionCount == 0 || card == null || amount <= 0)
            {
                return;
            }

            Send(connectionId, new CardDeltaMessage
            {
                IsAdd = isAdd,
                Amount = amount,
                Card = SnapshotCard(card),
            });
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
                Send(1, new GradedRemoveRequestMessage { Card = SnapshotCard(card) });
            }
        }

        internal bool HandleCardDelta(int connectionId, CardDeltaMessage message)
        {
            if (_host && message == null)
                return false;

            if (!ApplyOrHoldCardDelta(connectionId, message.IsAdd, message.Amount, message.Card,
                out var relayAnyway, message.PredictionId) && !relayAnyway)
            {
                return false;
            }

            if (_host && message.IsAdd)
            {
                ConsumeGradingOwnership(connectionId, message.Card, message.Amount);
            }
            else if (_host && !relayAnyway)
            {
                if (!message.IsAdd)
                    RecordGradingOwnership(connectionId, message.Card, message.Amount);
            }

            if (_host)
            {
                Broadcast(new CardDeltaMessage
                {
                    PredictionId = message.PredictionId,
                    IsAdd = message.IsAdd,
                    Amount = message.Amount,
                    Card = SnapshotCard(message.Card),
                });
            }
            return true;
        }

        internal bool HandleCardDeltaBatch(int connectionId, CardDeltaBatchMessage message)
        {
            if (_host && message == null)
                return false;

            if (message.Deltas == null)
                throw new InvalidOperationException("Card delta batch has no delta list.");

            var total = message.Deltas.Count;
            if (_host && total > CardDeltaBatchMax)
                throw new InvalidOperationException("Incoming card delta batch is too large.");

            if (!_host)
            {
                for (var i = 0; i < total; i++)
                {
                    ApplyOrHoldCardDelta(connectionId, message.Deltas[i].IsAdd,
                        message.Deltas[i].Amount, message.Deltas[i].Card, out _,
                        message.PredictionId);
                }
                return true;
            }

            var needFiltered = _host && ConnectionCount > 1;
            _batchRelayBuf.Clear();
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
                    ok = ApplyOrHoldCardDelta(connectionId, delta.IsAdd, delta.Amount, delta.Card,
                        out relayAnyway, message.PredictionId);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"card delta batch: delta {i + 1}/{total} failed to apply.", e);
                }

                if (!ok && !relayAnyway)
                {
                    continue;
                }

                if (ok)
                {
                    applied++;
                }
                else
                {
                    relayedOnly++;
                }

                if (_host && delta.IsAdd && (ok || relayAnyway))
                {
                    ConsumeGradingOwnership(connectionId, delta.Card, delta.Amount);
                }
                else if (_host && !delta.IsAdd && ok)
                {
                    RecordGradingOwnership(connectionId, delta.Card, delta.Amount);
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

            if (applied == total && relayedOnly == 0 && total > 0)
            {
                var authoritative = ToAuthoritative(message);
                authoritative.PredictionId = _host ? message.PredictionId : Guid.Empty;
                Broadcast(authoritative);
            }
            else if (_batchRelayBuf.Count > 0)
            {
                RelayCardDeltaBatchToOthers(connectionId, _batchRelayBuf);
            }
            return applied > 0 || relayedOnly > 0;
        }

        internal bool HandleGradedRemove(int connectionId, GradedRemoveMessage message)
        {
            if (_host && message == null)
                return false;

            var card = message.Card;

            if (!InGameLevel())
            {
                _pendingCardDeltas.Add(new PendingCard
                {
                    ConnectionId = connectionId,
                    PredictionId = message.PredictionId,
                    IsAdd = false,
                    IsGradedRemoval = true,
                    Amount = 1,
                    Card = SnapshotCard(card),
                });
                return true;
            }

            var relayAnyway = false;
            var applied = _host
                ? ApplyCardDelta(false, 1, card, out relayAnyway)
                : ApplyTrustedCardDelta(false, 1, card);
            if (!applied && !relayAnyway)
            {
                return false;
            }

            if (_host && !relayAnyway)
            {
                RecordGradingOwnership(connectionId, card, 1);
            }

            if (ConnectionCount == 0)
            {
                return true;
            }

            if (_host)
            {
                Broadcast(new GradedRemoveMessage
                {
                    PredictionId = message.PredictionId,
                    Card = SnapshotCard(message.Card),
                });
            }
            return true;
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
            _deltaLogBuf.Clear();
            _binderRefreshPending = false;
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

        internal void ForgetGradingOwnership(int connectionId)
        {
            if (connectionId > 0)
            {
                RestoreGradingOwnership(connectionId);
            }
        }

        private bool RestoreAllGradingOwnership()
        {
            if (_gradingOwnership.Count == 0)
                return true;
            if (!InGameLevel() || Inv() == null)
                return false;

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
                        return false;
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

        private bool RestoreGradingOwnership(int connectionId)
        {
            if (!_gradingOwnership.ContainsKey(connectionId))
                return true;
            if (!InGameLevel() || Inv() == null
                || !_gradingOwnershipCards.TryGetValue(connectionId, out var cards))
                return false;

            var restored = new List<PendingCard>();
            foreach (var claim in _gradingOwnership[connectionId])
            {
                if (!cards.TryGetValue(claim.Key, out var card) || card == null
                    || claim.Value <= 0 || claim.Value > CardDeltaAmountMax)
                    return false;
                restored.Add(new PendingCard
                {
                    IsAdd = true,
                    Amount = claim.Value,
                    Card = SnapshotCard(card),
                });
            }

            if (!ApplyRestoredCards(restored))
                return false;

            _gradingOwnership.Remove(connectionId);
            _gradingOwnershipCards.Remove(connectionId);
            CoopPlugin.Log.LogInfo("restored " + restored.Count
                + " grading card claim(s) before clearing connection " + connectionId);
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
                            throw new InvalidOperationException("card count overflowed before claim restoration");

                        GradingApi.Remember(item.Card);
                        CPlayerData.AddCard(SnapshotCard(item.Card), item.Amount);

                        var after = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (after != before + item.Amount)
                            throw new InvalidOperationException("claim restoration did not update inventory by the expected amount");
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
                ForwardCardDelta(restored[i].Card, restored[i].Amount, true);
            if (restored.Count > 0)
                NotifyCardsChanged();
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
                            return false;

                        if (item.Card.cardGrade > 0)
                        {
                            for (var j = 0; j < item.Amount; j++)
                                CPlayerData.RemoveGradedCard(item.Card, ignoreGradedCardIndex: true);
                        }
                        else
                        {
                            CPlayerData.ReduceCard(item.Card, item.Amount);
                        }

                        var after = item.Card.cardGrade > 0
                            ? CountGradedCards(item.Card)
                            : CPlayerData.GetCardAmount(item.Card);
                        if (after != before - item.Amount)
                            return false;
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
        internal bool TryReserveGradingCards(int connectionId, IList<CardData> cards,
            out IGradingCardReservation reservation, out string reason)
        {
            reservation = null;
            reason = null;
            if (!_host || connectionId <= 0 || cards == null || cards.Count == 0)
            {
                reason = "the host card owner is not ready";
                return false;
            }
            var transferred = _gradingOwnership.TryGetValue(connectionId, out var owned)
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
                CoopPlugin.Log.LogError("grading card ownership check failed for connection "
                    + connectionId + ": " + error);
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
                            continue;

                        if (item.Card.cardGrade > 0)
                        {
                            var before = CountGradedCards(item.Card);
                            if (before <= 0)
                                throw new InvalidOperationException("graded card disappeared before reservation");
                            CPlayerData.RemoveGradedCard(item.Card, ignoreGradedCardIndex: true);
                            var after = CountGradedCards(item.Card);
                            if (after != before - 1)
                                throw new InvalidOperationException("host graded-card removal did not change inventory");
                        }
                        else
                        {
                            var before = CPlayerData.GetCardAmount(item.Card);
                            if (before <= 0)
                                throw new InvalidOperationException("card disappeared before reservation");
                            CPlayerData.ReduceCard(item.Card, 1);
                            var after = CPlayerData.GetCardAmount(item.Card);
                            if (after != before - 1)
                                throw new InvalidOperationException("host card removal did not change inventory");
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
                        ForwardGradedRemoval(card);
                    else
                        ForwardCardDelta(card, 1, false);
                }
                NotifyCardsChanged();
                reservation = new GradingCardReservation(this, connectionId, planned,
                    _gradingOwnership.TryGetValue(connectionId, out var claimCounts)
                        ? new Dictionary<string, int>(claimCounts, StringComparer.Ordinal)
                        : null,
                    _gradingOwnershipCards.TryGetValue(connectionId, out var claimCards)
                        ? CloneClaimCards(claimCards) : null);
                return true;
            }
            catch (Exception error)
            {
                RollbackGradingCards(applied);
                reason = "host card reservation failed: " + error.Message;
                CoopPlugin.Log.LogError("grading card reservation failed for connection "
                    + connectionId + ": " + error);
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
                NotifyCardsChanged();
        }

        private int GetCachedUngradedCount(CardData card, Dictionary<string, int> cache)
        {
            var key = CardOwnershipKey(card);
            if (cache.TryGetValue(key, out var count))
                return count;
            count = CPlayerData.GetCardAmount(card);
            cache[key] = count;
            return count;
        }

        private int GetCachedGradedCount(CardData card, Dictionary<string, int> cache)
        {
            var key = CardOwnershipKey(card);
            if (cache.TryGetValue(key, out var count))
                return count;
            count = 0;
            var rows = CPlayerData.m_GradedCardInventoryList;
            if (rows != null)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null)
                        continue;
                    var candidate = CPlayerData.GetGradedCardData(row);
                    if (CardOwnershipKey(candidate) == key)
                        count++;
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
                return count;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && CardOwnershipKey(CPlayerData.GetGradedCardData(row)) == key)
                    count++;
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
            private readonly int _connectionId;
            private readonly List<ReservedGradingCard> _cards;
            private readonly Dictionary<string, int> _claimCountsBefore;
            private readonly Dictionary<string, CardData> _claimCardsBefore;
            private bool _committed;
            private bool _rolledBack;
            private bool _disposed;

            internal GradingCardReservation(WorldCardInteraction owner, int connectionId,
                List<ReservedGradingCard> cards, Dictionary<string, int> claimCountsBefore,
                Dictionary<string, CardData> claimCardsBefore)
            {
                _owner = owner;
                _connectionId = connectionId;
                _cards = cards;
                _claimCountsBefore = claimCountsBefore;
                _claimCardsBefore = claimCardsBefore;
            }

            public void Commit()
            {
                if (_disposed || _committed || _rolledBack)
                    return;

                Dictionary<string, int> moved = null;
                for (var i = 0; i < _cards.Count; i++)
                {
                    if (!_cards[i].FromTransferredOwnership)
                        continue;

                    if (moved == null)
                    {
                        if (!_owner._gradingOwnership.TryGetValue(_connectionId, out var current))
                            throw new InvalidOperationException("grading ownership transfer disappeared before commit");
                        moved = new Dictionary<string, int>(current, StringComparer.Ordinal);
                    }

                    var key = CardOwnershipKey(_cards[i].Card);
                    if (!moved.TryGetValue(key, out var count) || count <= 0)
                        throw new InvalidOperationException("grading ownership transfer disappeared before commit");
                    if (count == 1)
                        moved.Remove(key);
                    else
                        moved[key] = count - 1;
                }

                if (moved != null)
                {
                    var current = _owner._gradingOwnership[_connectionId];
                    current.Clear();
                    foreach (var pair in moved)
                        current[pair.Key] = pair.Value;
                }
                _committed = true;
            }

            public void Rollback()
            {
                if (_rolledBack)
                    return;
                _rolledBack = true;
                Exception ownershipError = null;
                if (_committed)
                {
                    try
                    {
                        if (_claimCountsBefore == null || _claimCountsBefore.Count == 0)
                            _owner._gradingOwnership.Remove(_connectionId);
                        else
                        {
                            _owner._gradingOwnership[_connectionId] =
                                new Dictionary<string, int>(_claimCountsBefore, StringComparer.Ordinal);
                        }

                        if (_claimCardsBefore == null || _claimCardsBefore.Count == 0)
                            _owner._gradingOwnershipCards.Remove(_connectionId);
                        else
                            _owner._gradingOwnershipCards[_connectionId] = CloneClaimCards(_claimCardsBefore);
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
                        ownershipError = error;
                }

                if (ownershipError != null)
                    throw ownershipError;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (!_committed)
                    Rollback();
            }
        }

        private static Dictionary<string, CardData> CloneClaimCards(
            Dictionary<string, CardData> cards)
        {
            var result = new Dictionary<string, CardData>(StringComparer.Ordinal);
            if (cards == null)
                return result;
            foreach (var pair in cards)
                result[pair.Key] = pair.Value == null ? null : SnapshotCard(pair.Value);
            return result;
        }

        private Dictionary<string, int> GetOrCreateOwnership(int connectionId)
        {
            if (!_gradingOwnership.TryGetValue(connectionId, out var result))
            {
                result = new Dictionary<string, int>(StringComparer.Ordinal);
                _gradingOwnership[connectionId] = result;
            }
            return result;
        }

        private void RecordGradingOwnership(int connectionId, CardData card, int amount)
        {
            if (!_host || connectionId <= 0 || card == null || amount <= 0)
                return;
            var key = CardOwnershipKey(card);
            if (key == null)
                return;
            var owned = GetOrCreateOwnership(connectionId);
            owned.TryGetValue(key, out var count);
            owned[key] = Math.Min(1024, count > 1024 - amount ? 1024 : count + amount);
            if (!_gradingOwnershipCards.TryGetValue(connectionId, out var cards))
            {
                cards = new Dictionary<string, CardData>(StringComparer.Ordinal);
                _gradingOwnershipCards[connectionId] = cards;
            }
            cards[key] = SnapshotCard(card);
        }

        private bool ConsumeGradingOwnership(int connectionId, CardData card, int amount)
        {
            if (!_host || connectionId <= 0 || card == null || amount <= 0
                || !_gradingOwnership.TryGetValue(connectionId, out var owned))
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
                owned[key] = count;
            else
            {
                owned.Remove(key);
                if (_gradingOwnershipCards.TryGetValue(connectionId, out var cards))
                    cards.Remove(key);
            }
            if (owned.Count == 0)
            {
                _gradingOwnership.Remove(connectionId);
                _gradingOwnershipCards.Remove(connectionId);
            }
            return true;
        }

        internal void NotifyCardsChanged()
        {
            _binderRefreshPending = true;
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
            public int ConnectionId;
            public Guid PredictionId;
            public bool IsAdd;
            public bool IsGradedRemoval;
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

        // ONE binder relayout per frame, not per delta: with the book open RefreshOpenBinder
        // invokes the game's OnSortingMethodUpdated (O(N^2) re-sort + 72-slot UI rebuild +
        // album total recompute), which per delta is the reported 20-30s freeze.
        private static bool _binderRefreshPending;

        // The per-delta apply line is the field-log diagnosis for "cards didn't show up in the
        // binder", so it survives verbatim for ordinary changes (<=5 applied in a frame) and
        // folds into one summary line for a flood. Emitted by FlushFrameCardWork.
        private static readonly List<PendingCard> _deltaLogBuf = new();
        private static int _deltaAppliedThisFrame;

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
                    // Memoized per card key, like the price path: one host log showed 995
                    // identical lines. Printed through CardIdent because an ordinary modded id
                    // is a small ORDINAL: below ~122 the bare monsterType renders an unrelated
                    // VANILLA name, at 123+ it renders a bare number (EMonsterType has no
                    // members up there) - neither identifies the card without the expansion.
                    CoopPlugin.Log.LogWarning($"card delta: {CardIdent(card)} is from a card set you don't have installed - skipped");

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
            _deltaAppliedThisFrame++;
            if (_deltaLogBuf.Count < 5)
            {
                _deltaLogBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
            }
            // Deferred to the end of the frame: RefreshOpenBinder is O(N^2) re-sort + full UI
            // rebuild whenever the book is open, and running it per delta is the 20-30s freeze.
            _binderRefreshPending = true;
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
                        GradingApi.Remember(card);
                    CPlayerData.AddCard(card, amount);
                }
                else if (card.cardGrade > 0)
                {
                    for (var i = 0; i < amount; i++)
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
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
            if (_deltaLogBuf.Count < 5)
                _deltaLogBuf.Add(new PendingCard
                {
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
            _binderRefreshPending = true;
            return true;
        }

        /// <summary>Shared by the CardDelta and CardDeltaBatch handlers: hold the delta if a
        /// scene load is in flight (applying mid-load crashes into uninitialized card data;
        /// nothing is lost, FlushPendingCardWork replays it), otherwise apply it. Returns true
        /// only when it was actually applied - i.e. when it may be relayed onward - and reports
        /// through <paramref name="relayAnyway"/> the "this PC lacks the content, but the delta
        /// is fine" refusal that must still be forwarded (see ApplyCardDelta).</summary>
        internal bool ApplyOrHoldCardDelta(int connectionId, bool isAdd, int amount,
            CardData card, out bool relayAnyway, Guid predictionId = default)
        {
            relayAnyway = false;
            if (_host && !TryValidateCardDelta(card, amount, out var invalidReason))
            {
                CoopPlugin.Log.LogWarning("card delta from connection " + connectionId
                    + " refused: " + invalidReason);
                return false;
            }
            if (!InGameLevel())
            {
                // HELD, not relayAnyway. Note this is NOT "it will relay later": the replay in
                // FlushPendingCardWork applies without relaying, so a delta held across a scene
                // load never reaches the other guests. That is pre-existing 1.0.36 behavior and
                // is deliberately left alone here - the relay-anyway work is about content this
                // PC lacks, not about the load window.
                _pendingCardDeltas.Add(new PendingCard
                {
                    ConnectionId = connectionId,
                    PredictionId = predictionId,
                    IsAdd = isAdd,
                    Amount = amount,
                    Card = SnapshotCard(card),
                });
                return false;
            }
            return _host
                ? ApplyCardDelta(isAdd, amount, card, out relayAnyway)
                : ApplyTrustedCardDelta(isAdd, amount, card);
        }

        /// <summary>A private copy of exactly the nine fields the wire carries. Anything that
        /// DEFERS a send must snapshot: AddCardPostfix temporarily writes the
        /// ENCODED grade into the game's live cardData and restore it in a finally, so reading
        /// the same object a frame later would ship the bare 1-10 grade instead.</summary>
        internal static CardData SnapshotCard(CardData c)
        {
            if (c == null)
                return null;
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
                    || canonical.monsterType != card.monsterType
                    || canonical.borderType != card.borderType
                    || canonical.isFoil != card.isFoil
                    || canonical.isDestiny != card.isDestiny
                    || canonical.isChampionCard != card.isChampionCard)
                {
                    // The card is self-inconsistent with THIS host's layout for the expansion
                    // (e.g. the sender resolved an ordinal through a different shown list). Log
                    // both sides with raw ordinals so the divergent field is visible next time.
                    reason = "card fields do not match their save index (index=" + saveIndex
                        + " sent=" + CardFieldDump(card) + " canonical=" + CardFieldDump(canonical)
                        + ")";
                    return false;
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
                return false;
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
        /// would either refuse cards this install now has or accept ones it doesn't.</summary>
        internal static void ClearCardSetCache()
        {
            _shownMonsters.Clear();
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
            // (FindObjectOfType, fabricates nothing) both avoids that and makes the no-latch
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
                return "(null card)";
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
        private static readonly System.Reflection.MethodInfo MiBinderResort =
            HarmonyLib.AccessTools.Method(typeof(CollectionBinderFlipAnimCtrl), "OnSortingMethodUpdated");
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

        /// <summary>Make an ALREADY-OPEN collection binder re-lay-out after a card change.
        /// SetCanUpdateSort alone only ARMS a gate the vanilla per-frame Update never
        /// consumes, so a traded/pulled card stayed invisible until the player flipped a
        /// page or reopened the binder. When the book is open we also invoke the game's own
        /// OnSortingMethodUpdated (backToFirstPage:false, keeps the current page) which
        /// rebuilds the sorted list + relays out all page groups, so the card appears now.</summary>
        private static void RefreshOpenBinder()
        {
            try
            {
                var ipc = SceneRef<InteractionPlayerController>.Get();
                var ctrl = ipc != null ? ipc.m_CollectionBinderFlipAnimCtrl : null;
                if (ctrl == null)
                {
                    return;
                }

                ctrl.SetCanUpdateSort(canSort: true);
                var isOpen = FiBinderIsBookOpen != null && (bool)FiBinderIsBookOpen.GetValue(ctrl);
                if (isOpen && MiBinderResort != null)
                {
                    MiBinderResort.Invoke(ctrl, new object[] { false }); // backToFirstPage:false
                }

                // OnSortingMethodUpdated re-lays out the cards but NEVER touches the total-value
                // text - that write only happens in the binder OPEN path. So a traded/pulled card
                // showed up on the page but the "total value" header stayed stale (the reported
                // "total value differs"). While the book is open, mirror the exact SetTotalValue
                // call the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) would make for
                // the CURRENTLY open album. Behind the isOpen guard so a delta with no binder up
                // costs nothing.
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

            {
                var relayed = _host ? new List<PendingCard>(_pendingCardDeltas.Count) : null;
                foreach (var p in _pendingCardDeltas)
                {
                    var relayAnyway = false;
                    var applied = _host
                        ? ApplyCardDelta(p.IsAdd, p.Amount, p.Card, out relayAnyway)
                        : ApplyTrustedCardDelta(p.IsAdd, p.Amount, p.Card);
                    if (_host && (applied || relayAnyway) && p.IsAdd)
                    {
                        ConsumeGradingOwnership(p.ConnectionId, p.Card, p.Amount);
                    }
                    else if (_host && applied && !relayAnyway)
                    {
                        RecordGradingOwnership(p.ConnectionId, p.Card, p.Amount);
                    }

                    if (_host && (applied || relayAnyway))
                    {
                        relayed.Add(p);
                    }
                }

                if (_pendingCardDeltas.Count > 0)
                {
                    CoopPlugin.Log.LogInfo($"applied {_pendingCardDeltas.Count} card change(s) held during loading");
                }

                _pendingCardDeltas.Clear();

                // Deltas received while the host was loading are still authoritative events. They
                // were held before relay, so replay them in their original order now rather than
                // silently leaving the other guests with a stale card ledger.
                if (relayed != null)
                {
                    for (var i = 0; i < relayed.Count; i++)
                    {
                        var p = relayed[i];
                        if (p.IsGradedRemoval)
                        {
                            var message = new GradedRemoveMessage
                            {
                                PredictionId = p.PredictionId,
                                Card = SnapshotCard(p.Card),
                            };
                            SendAcceptedPendingCard(p, message);
                        }
                        else
                        {
                            var message = new CardDeltaMessage
                            {
                                PredictionId = p.PredictionId,
                                IsAdd = p.IsAdd,
                                Amount = p.Amount,
                                Card = SnapshotCard(p.Card),
                            };
                            SendAcceptedPendingCard(p, message);
                        }
                    }
                }
            }
        }

        private void SendAcceptedPendingCard(PendingCard pending, WorldMessage message)
        {
            if (pending.PredictionId != Guid.Empty && pending.ConnectionId > 0)
            {
                Send(pending.ConnectionId, message);
            }

            RelayToOthers(pending.ConnectionId, message);
        }

        /// <summary>Host-side raw fan-out: forward a message a guest sent us on to the OTHER
        /// guests, byte-for-byte. The collection is one shared inventory, so a card a 3rd+ player
        /// gains/loses must reach every peer, not just the host. We re-wrap the ORIGINAL payload
        /// bytes (not a re-serialized CardData) so an encoded graded grade - the >10 company/cert
        /// packing - survives verbatim; re-serializing would risk lossy round-trips. Same shape as
        /// the CardShelfRequest relay, generalized. No-op unless we're the host with >1 peer.</summary>
        /// <summary>Host: relay only the deltas of a CardDeltaBatch that THIS side accepted.
        /// Used when part of the batch was refused here (corrupt grade / uninstalled card set /
        /// would-go-negative): the whole-batch case relays the ORIGINAL bytes, but a delta we
        /// refused must never be propagated onward - exactly the guarantee the single-delta
        /// case has always had.</summary>
        /// <summary>Host-side DTO fan-out: forward a decoded DTO a guest sent us to the OTHER
        /// guests. The DTO carries the exact CardData (including the encoded graded grade), so
        /// re-serializing it reproduces the original wire bytes.</summary>
        internal void RelayToOthers(int senderConn, INetMessage message)
        {
            if (!_host || ConnectionCount <= 1)
            {
                return;
            }

            if (RelayOverride != null)
            {
                RelayOverride(senderConn, message);
                return;
            }

            foreach (var cid in ConnectionIds())
            {
                if (cid != senderConn)
                {
                    Send(cid, message);
                }
            }
        }

        private static CardDeltaBatchMessage ToAuthoritative(CardDeltaBatchMessage message)
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

        private void RelayCardDeltaBatchToOthers(int senderConn, List<PendingCard> deltas)
        {
            if (!_host || ConnectionCount <= 1 || deltas.Count == 0)
            {
                return;
            }

            var relay = new CardDeltaBatchMessage();
            for (var i = 0; i < deltas.Count; i++)
            {
                relay.Deltas.Add(new CardDeltaEntry
                {
                    IsAdd = deltas[i].IsAdd,
                    Amount = deltas[i].Amount,
                    Card = deltas[i].Card
                });
            }

            if (RelayOverride != null)
            {
                RelayOverride(senderConn, relay);
                return;
            }

            foreach (var cid in ConnectionIds())
            {
                if (cid != senderConn)
                    Send(cid, relay);
            }
        }

        /// <summary>End of frame: flush card-delta diagnostics and perform one binder relayout
        /// for everything applied this frame.</summary>
        private void FlushFrameCardWork()
        {
            if (_deltaAppliedThisFrame > 0)
            {
                if (_deltaAppliedThisFrame <= 5)
                {
                    for (var i = 0; i < _deltaLogBuf.Count; i++)
                    {
                        var d = _deltaLogBuf[i];
                        CoopPlugin.Log.LogInfo($"card delta applied: {(d.IsAdd ? "+" : "-")}{d.Amount} {CardIdent(d.Card)}{(d.Card.cardGrade > 0 ? $" (grade {d.Card.cardGrade})" : d.Card.isFoil ? " (foil)" : "")}");
                    }
                }
                else
                {
                    CoopPlugin.Log.LogInfo($"applied {_deltaAppliedThisFrame} card deltas");
                }

                _deltaLogBuf.Clear();
                _deltaAppliedThisFrame = 0;
            }
            if (_binderRefreshPending)
            {
                _binderRefreshPending = false;
                RefreshOpenBinder();
            }
        }

    }
}
