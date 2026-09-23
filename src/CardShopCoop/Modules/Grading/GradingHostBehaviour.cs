using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Grading
{
    /// <summary>
    /// Host grading authority. Full job state is a join baseline; normal job changes are keyed
    /// deltas. An intent is validated once and charged before the game operation.
    /// </summary>
    [ServerBehaviour]
    public sealed class GradingHostBehaviour : CoopBehaviour
    {
        private const int VanillaSlots = 8;
        private static GradingHostBehaviour _active;
        private readonly Dictionary<GradeCardSubmitSet, int> _setIds = new();
        private readonly HashSet<int> _joined = new();
        private readonly HashSet<int> _baselinePending = new();
        private int _nextSetId = 1;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        private sealed class GoJobSnapshot
        {
            internal int ServiceLevel;
            internal List<CardData> Cards;
            internal int CompanyId;
            internal bool UseCheats;
            internal int JobId;
            internal bool RegisterAttempted;
            internal bool PreRollAttempted;
        }

        internal static GradingHostBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                _active = this;
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.grading.host");
                GradingPatches.ApplyHost(_harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                throw;
            }
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
            => SendPendingBaselines();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection peer)
        {
            if (peer == null)
                return;
            _joined.Add(peer.Id);
            if (!FullUpdate(peer.Id))
                _baselinePending.Add(peer.Id);
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection peer, DisconnectInfo _)
        {
            if (peer == null)
                return;
            _joined.Remove(peer.Id);
            _baselinePending.Remove(peer.Id);
        }

        [MessageHandler(typeof(GradingOpMessage))]
        private void HandleSubmission(MessageContext context, GradingOpMessage message)
        {
            if (context?.Connection != null)
                HostApply(message, context.Connection.Id);
        }

        internal static void InventoryReset()
        {
            _active?.ResetInventory();
        }

        private void ResetInventory()
        {
            foreach (var pair in new Dictionary<GradeCardSubmitSet, int>(_setIds))
                BroadcastJobDelta(Guid.Empty, pair.Value, null, true);
            _setIds.Clear();
            _nextSetId = 1;
            GradingInterop.Reset();
        }

        internal bool FullUpdate(int connectionId)
        {
            if (_shutdown || !_context.InGame() || GradingWorldBridge.Current?.SceneReady != true)
                return false;

            var list = CPlayerData.m_GradeCardInProgressList;
            if (list == null)
                return false;
            _context.Send(connectionId, BuildState(list));
            _baselinePending.Remove(connectionId);
            return true;
        }

        private void SendPendingBaselines()
        {
            foreach (var connectionId in new List<int>(_baselinePending))
            {
                if (!_joined.Contains(connectionId))
                {
                    _baselinePending.Remove(connectionId);
                    continue;
                }
                FullUpdate(connectionId);
            }
        }

        /// <summary>Called by the vanilla day-start hook after job progress changed.</summary>
        internal void PushCurrentSets()
        {
            if (_shutdown || !_context.InGame())
                return;
            var list = CPlayerData.m_GradeCardInProgressList;
            var live = new HashSet<int>();
            if (list != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var set = list[i];
                    if (set == null)
                        continue;
                    var id = GetSetId(set);
                    live.Add(id);
                    BroadcastJobDelta(Guid.Empty, id, set, false);
                }
            }

            var removed = new List<GradeCardSubmitSet>();
            foreach (var pair in _setIds)
            {
                if (!live.Contains(pair.Value))
                {
                    BroadcastJobDelta(Guid.Empty, pair.Value, null, true);
                    removed.Add(pair.Key);
                }
            }
            for (var i = 0; i < removed.Count; i++)
                _setIds.Remove(removed[i]);
        }

        private void HostApply(GradingOpMessage message, int senderConnection)
        {
            var bridge = GradingWorldBridge.Current;
            var cards = message?.Cards;
            if (message == null || cards == null || cards.Count == 0)
            {
                Reject(senderConnection, message?.PredictionId ?? Guid.Empty,
                    cards, "grading-submit-cards", bridge);
                return;
            }

            if (bridge == null || !bridge.SceneReady)
            {
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-not-ready", bridge);
                return;
            }

            var cap = GradingInterop.MaxSubmitSlots;
            if (cards.Count > cap)
            {
                Reject(senderConnection, message.PredictionId, cards, "grading-submit-cap", bridge);
                return;
            }

            for (var i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null || !GradingInterop.ValidSubmissionCard(cards[i])
                    || !bridge.CardSetInstalled(cards[i]))
                {
                    Reject(senderConnection, message.PredictionId, cards,
                        "grading-submit-card", bridge);
                    return;
                }
            }

            if (CPlayerData.m_GradeCardInProgressList == null
                || CPlayerData.m_GradeCardInProgressList.Count >= 4)
            {
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-slots", bridge);
                return;
            }

            var serviceLevel = message.ServiceLevel;
            if (!bridge.TryGetVanillaServiceCost(serviceLevel, cards.Count, out var total)
                || float.IsNaN(total) || float.IsInfinity(total) || total < 0f)
            {
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-fee", bridge);
                return;
            }

            if (GradingInterop.Present && (!GradingInterop.GoCompatible
                || !GradingInterop.IsAllowedCompany(message.CompanyId)))
            {
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-service", bridge);
                return;
            }

            if (!EconomyAuthority.TryReserveHostSpend(total, out var spend))
            {
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-wallet", bridge);
                return;
            }

            if (!bridge.TryReserveCards(senderConnection, cards, out var reservation,
                out var ownershipReason) || reservation == null)
            {
                CoopPlugin.Log.LogWarning("grading submission refused: " + ownershipReason);
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-ownership", bridge);
                return;
            }

            GradeCardSubmitSet set = null;
            var report = CPlayerData.m_GameReportDataCollect.supplyCost;
            var permanentReport = CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            var transactionRecorded = false;
            GoJobSnapshot go = null;
            try
            {
                // The wallet is admitted before any local job/report/GO commit.
                if (!EconomyAuthority.QueueHostSpend(spend))
                    throw new InvalidOperationException("grading wallet debit could not be admitted");

                PriceChangeManager.AddTransaction(-total, ETransactionType.GradingFee, serviceLevel);
                transactionRecorded = true;
                CPlayerData.m_GameReportDataCollect.supplyCost -= total;
                CPlayerData.m_GameReportDataCollectPermanent.supplyCost -= total;

                set = new GradeCardSubmitSet
                {
                    m_ServiceLevel = serviceLevel,
                    m_DayPassed = 0,
                    m_MinutePassed = 0f,
                    m_CardDataList = new List<CardData>(Math.Max(cards.Count, VanillaSlots)),
                };
                for (var i = 0; i < cards.Count; i++)
                    set.m_CardDataList.Add(Clone(cards[i]));
                CPlayerData.m_GradeCardInProgressList.Add(set);

                if (GradingInterop.Present)
                {
                    if (!TryEnrollGoJob(set, message.CompanyId, serviceLevel, out go))
                        throw new InvalidOperationException("Grading Overhaul rejected the service/company");
                }
                else
                {
                    while (set.m_CardDataList.Count < VanillaSlots)
                        set.m_CardDataList.Add(new CardData());
                }

                reservation.Commit();
                var id = GetSetId(set);
                BroadcastJobDelta(message.PredictionId, id, set, false);
                SceneRef<GradeCardWebsiteUIScreen>.Get()?.UpdateSubmissionProgressPanelUI();
                CoopPlugin.Log.LogInfo("grading submission accepted for connection "
                    + senderConnection + " as set " + id);
            }
            catch (Exception error)
            {
                try
                {
                    reservation.Rollback();
                    if (set != null)
                    {
                        CPlayerData.m_GradeCardInProgressList?.Remove(set);
                        _setIds.Remove(set);
                    }
                    RollbackGoJob(set, go);
                    CPlayerData.m_GameReportDataCollect.supplyCost = report;
                    CPlayerData.m_GameReportDataCollectPermanent.supplyCost = permanentReport;
                    if (transactionRecorded)
                        PriceChangeManager.AddTransaction(total, ETransactionType.GradingFee, serviceLevel);
                    // This is only for an actual local game exception after the admitted debit;
                    // transport outcomes never reverse wallet state.
                    CEventManager.QueueEvent(new CEventPlayer_AddCoin(total, true));
                }
                catch (Exception compensationError)
                {
                    CoopPlugin.Log.LogError("grading local compensation failed: " + compensationError);
                }
                CoopPlugin.Log.LogError("grading submission failed after local validation: " + error);
                Reject(senderConnection, message.PredictionId, cards,
                    "grading-submit-failure", bridge);
            }
        }

        private bool TryEnrollGoJob(GradeCardSubmitSet set, int companyId, int serviceLevel,
            out GoJobSnapshot snapshot)
        {
            snapshot = null;
            if (!GradingInterop.GoCompatible || !GradingInterop.IsAllowedCompany(companyId))
                return false;

            var useCheats = GradingInterop.HostUseCheatsWebsite;
            var jobId = GradingInterop.NextJobId();
            var encoded = GradingInterop.EncodeServiceLevel(companyId, serviceLevel, jobId);
            if (jobId <= 0 || encoded <= 0
                || !GradingInterop.TryDecodeServiceLevel(encoded, out var decodedCompany,
                    out var decodedTier)
                || decodedCompany != companyId || decodedTier != serviceLevel)
                return false;

            snapshot = new GoJobSnapshot
            {
                ServiceLevel = set.m_ServiceLevel,
                Cards = CloneCards(set.m_CardDataList),
                CompanyId = companyId,
                UseCheats = useCheats,
                JobId = jobId,
            };
            snapshot.RegisterAttempted = true;
            if (!GradingInterop.RegisterJobCompany(set, companyId, useCheats))
                return false;

            set.m_ServiceLevel = encoded;
            snapshot.PreRollAttempted = true;
            if (!GradingInterop.PreRollJob(set, companyId, useCheats, jobId))
                return false;
            return true;
        }

        private static void RollbackGoJob(GradeCardSubmitSet set, GoJobSnapshot snapshot)
        {
            if (set == null || snapshot == null)
                return;
            if (snapshot.PreRollAttempted
                && !GradingInterop.RollbackPreRoll(set, snapshot.CompanyId, snapshot.UseCheats,
                    snapshot.JobId))
            {
                CoopPlugin.Log.LogError("Grading Overhaul pre-roll compensation was refused for job "
                    + snapshot.JobId);
            }
            if (snapshot.RegisterAttempted
                && !GradingInterop.RollbackJobCompany(set, snapshot.CompanyId, snapshot.UseCheats,
                    snapshot.JobId))
            {
                CoopPlugin.Log.LogError("Grading Overhaul job enrollment compensation was refused for job "
                    + snapshot.JobId);
            }
            set.m_ServiceLevel = snapshot.ServiceLevel;
            for (var i = 0; i < Math.Min(set.m_CardDataList?.Count ?? 0, snapshot.Cards?.Count ?? 0); i++)
            {
                set.m_CardDataList[i].cardGrade = snapshot.Cards[i].cardGrade;
                set.m_CardDataList[i].gradedCardIndex = snapshot.Cards[i].gradedCardIndex;
            }
        }

        private void Reject(int connectionId, Guid predictionId, List<CardData> cards, string reason,
            IGradingWorldBridge bridge)
        {
            cards ??= new List<CardData>();
            if (bridge != null)
            {
                for (var i = 0; i < cards.Count; i++)
                {
                    if (cards[i] != null && !bridge.CardSetInstalled(cards[i]))
                        bridge.WarnRefused(cards[i], reason);
                }
            }
            CoopPlugin.Log.LogWarning("grading submission rejected for connection "
                + connectionId + ": " + reason);
            if (predictionId != Guid.Empty)
                PredictionApi.Rollback(_context, connectionId, predictionId);
        }

        private int GetSetId(GradeCardSubmitSet set)
        {
            if (!_setIds.TryGetValue(set, out var id))
            {
                id = _nextSetId++;
                if (_nextSetId <= 0)
                    _nextSetId = 1;
                _setIds[set] = id;
            }
            return id;
        }

        private GradingStateMessage BuildState(List<GradeCardSubmitSet> sets)
        {
            var message = new GradingStateMessage();
            if (sets == null)
                return message;

            for (var i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set == null)
                    continue;
                var dto = new GradingSetDto
                {
                    Id = GetSetId(set),
                    ServiceLevel = set.m_ServiceLevel,
                    DayPassed = (byte)Mathf.Clamp(set.m_DayPassed, 0, byte.MaxValue),
                    MinutePassed = set.m_MinutePassed,
                };
                var cards = set.m_CardDataList;
                var count = Math.Min(cards?.Count ?? 0, GradingInterop.MaxSubmitSlots);
                for (var j = 0; j < count; j++)
                    dto.Cards.Add(Clone(cards[j]));
                message.Sets.Add(dto);
            }
            return message;
        }

        private void BroadcastJobDelta(Guid predictionId, int id, GradeCardSubmitSet set,
            bool removed)
        {
            if (_shutdown || !_context.InGame())
                return;

            var message = new GradingJobDeltaMessage
            {
                PredictionId = predictionId,
                Id = id,
                Removed = removed,
            };
            if (set != null)
            {
                message.ServiceLevel = set.m_ServiceLevel;
                message.DayPassed = (byte)Mathf.Clamp(set.m_DayPassed, 0, byte.MaxValue);
                message.MinutePassed = set.m_MinutePassed;
                var cards = set.m_CardDataList;
                var count = Math.Min(cards?.Count ?? 0, GradingInterop.MaxSubmitSlots);
                for (var i = 0; i < count; i++)
                    message.Cards.Add(Clone(cards[i]));
            }
            _context.Broadcast(message);
        }

        private static List<CardData> CloneCards(List<CardData> cards)
        {
            var result = new List<CardData>(cards?.Count ?? 0);
            if (cards == null)
                return result;
            for (var i = 0; i < cards.Count; i++)
                result.Add(Clone(cards[i]));
            return result;
        }

        private static CardData Clone(CardData card)
        {
            return card == null ? null : new CardData
            {
                expansionType = card.expansionType,
                monsterType = card.monsterType,
                borderType = card.borderType,
                isFoil = card.isFoil,
                isDestiny = card.isDestiny,
                isChampionCard = card.isChampionCard,
                isNew = card.isNew,
                cardGrade = GradingInterop.Encoded(card),
                gradedCardIndex = card.gradedCardIndex,
            };
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _setIds.Clear();
            _joined.Clear();
            _baselinePending.Clear();
            GradingInterop.Reset();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
