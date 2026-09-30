using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Grading
{
    /// <summary>Forwards one grading intent and applies the latest host-owned job state.</summary>
    [ClientBehaviour]
    public sealed class GradingClientBehaviour : CoopBehaviour
    {
        private static readonly FieldInfo FiShowingAlpha = ReflectionSurface.OptionalField(
            typeof(GradedCardSubmitSelectScreen), "m_IsShowingCanvasGrpAlpha");
        private static readonly FieldInfo FiHidingAlpha = ReflectionSurface.OptionalField(
            typeof(GradedCardSubmitSelectScreen), "m_IsHidingCanvasGrpAlpha");

        private static GradingClientBehaviour _active;
        private readonly Dictionary<GradeCardSubmitSet, Guid> _jobIds = new();
        private readonly Dictionary<Guid, GradeCardSubmitSet> _localJobSets = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private GradingStateMessage _pendingState;
        private readonly Dictionary<Guid, GradingJobDeltaMessage> _pendingDeltas = new();
        private bool _contentReady;
        private bool _shutdown;
        private GradeCardWebsiteUIScreen _website;
        private GradedCardSubmitSelectScreen _submitScreen;

        internal static GradingClientBehaviour Active => _active;

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
                // A rejected prediction leaves its locally minted job behind until the next
                // authoritative state; retiring the prediction must also drop the job binding so a
                // later delta cannot adopt a job the host never accepted.
                PredictionApi.PredictionRetired += RetireLocalJob;
                _harmony = new Harmony("com.zwhit.cardshopcoop.grading.client");
                GradingPatches.ApplyClient(_harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                PredictionApi.PredictionRetired -= RetireLocalJob;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                throw;
            }
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
        {
            _contentReady = true;
            ApplyPending();
        }

        /// <summary>Drops the local-job binding for a prediction that left the tracker (accepted
        /// and bound, or rejected and removed). Prevents a stale binding from adopting a later
        /// authoritative delta onto a job the host never accepted.</summary>
        private void RetireLocalJob(Guid predictionId)
            => _localJobSets.Remove(predictionId);

        [MessageHandler(typeof(GradingStateMessage))]
        private void HandleState(MessageContext context, GradingStateMessage message)
        {
            if (_shutdown)
                return;
            _pendingState = message;
            ApplyPending();
        }

        /// <summary>State captured before the vanilla submit runs, so the postfix can register one
        /// post-hoc prediction for the job the game already minted.</summary>
        internal struct SubmissionCapture
        {
            internal bool Armed;
            internal GradeCardSubmitSet Previous;
            internal GradeCardSubmitSet Job;
            internal Guid JobId;
            internal List<CardData> Cards;
            internal int ServiceLevel;
            internal int Cap;
            internal GradedCardSubmitSelectScreen Screen;
            internal float SupplyCostBefore;
        }

        /// <summary>
        /// Captures the selection and wallet before the vanilla submission runs. The game performs
        /// the submission itself (mint job, reserve cards, charge the mirror); the postfix observes
        /// the resulting job and registers one post-hoc prediction, which the host confirms with a
        /// job delta or rejects with the generic rollback.
        /// </summary>
        internal void CaptureSubmission(GradedCardSubmitSelectScreen screen,
            out SubmissionCapture state)
        {
            state = default;
            if (_shutdown || _context?.InGame() != true || IsShowingOrHiding(screen))
                return;

            var submitSet = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (submitSet?.m_CardDataList == null)
                return;

            var cards = new List<CardData>();
            for (var i = 0; i < submitSet.m_CardDataList.Count; i++)
            {
                var card = submitSet.m_CardDataList[i];
                if (card != null && card.monsterType != EMonsterType.None)
                    cards.Add(Clone(card));
            }
            if (cards.Count == 0)
                return;

            state.Armed = true;
            state.Previous = CloneSet(submitSet);
            // Vanilla appends this exact set object to m_GradeCardInProgressList before replacing
            // the selection, so the local job is identified by the game's own object identity and
            // this stable id, never by its position in the list.
            state.Job = submitSet;
            state.JobId = Guid.NewGuid();
            state.Cards = cards;
            state.ServiceLevel = submitSet.m_ServiceLevel;
            state.Cap = GradingInterop.MaxSubmitSlots;
            state.Screen = screen;
            // The wallet event is only queued by vanilla, but the report cost is decremented
            // synchronously, so it is the reliable "did it charge" signal and the exact fee.
            state.SupplyCostBefore = CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            _submitScreen = screen;
            EconomyActionScope.Enter();
        }

        internal void ObserveSubmission(SubmissionCapture state)
        {
            if (!state.Armed || _shutdown || _context?.InGame() != true)
                return;

            var list = CPlayerData.m_GradeCardInProgressList;
            // Vanilla appends the captured selection object as the new job, so the local job is the
            // game's own object identity rather than list[last]. When vanilla bails (no card
            // selected, or the mirror cannot afford the fee) the object never enters the list and
            // the game has already shown its popup.
            var jobSet = state.Job;
            if (list == null || jobSet == null || !list.Contains(jobSet))
                return;

            var spent = state.SupplyCostBefore
                - CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            PredictionApi.Predict(
                "grading",
                predictionId =>
                {
                    // Bind the local job to the prediction and its stable id before sending. The
                    // host adopts this same id, so the accepted delta finds the job by id rather
                    // than appending a duplicate.
                    _localJobSets[predictionId] = jobSet;
                    _jobIds[jobSet] = state.JobId;
                    try
                    {
                        _context.Send(1, new GradingOpMessage
                        {
                            PredictionId = predictionId,
                            JobId = state.JobId,
                            ServiceLevel = (byte)state.ServiceLevel,
                            CompanyId = (byte)GradingInterop.CurrentCompanyId,
                            Cards = state.Cards,
                        });
                    }
                    catch
                    {
                        _localJobSets.Remove(predictionId);
                        _jobIds.Remove(jobSet);
                        throw;
                    }
                },
                () =>
                {
                    var jobs = CPlayerData.m_GradeCardInProgressList;
                    // Undo removed the locally minted job; replay must put it back before the
                    // authoritative delta for this prediction arrives to bind it to its host id.
                    if (jobSet != null && jobs != null && !jobs.Contains(jobSet))
                        jobs.Add(jobSet);
                    ApplyLocalSubmission(state.ServiceLevel, state.Cap, state.Screen);
                    if (spent > 0.0001f)
                        CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(spent));
                },
                () =>
                {
                    RemoveLocalJob(jobSet);
                    CPlayerData.m_CurrentGradeCardSubmitSet = state.Previous;
                    if (spent > 0.0001f)
                        CEventManager.QueueEvent(new CEventPlayer_AddCoin(spent, true));
                    RefreshWebsite();
                },
                ReopenSubmissionScreen);
        }

        private void RemoveLocalJob(GradeCardSubmitSet set)
        {
            if (set == null)
                return;
            _jobIds.Remove(set);
            CPlayerData.m_GradeCardInProgressList?.Remove(set);
        }

        private void ApplyPending()
        {
            if (_shutdown || !_contentReady || !_context.InGame()
                || GradingWorldBridge.Current?.SceneReady != true)
                return;

            if (_pendingState != null)
            {
                var state = _pendingState;
                ApplyState(state);
                _pendingState = null;
            }
            if (_pendingDeltas.Count > 0)
            {
                var deltas = new List<KeyValuePair<Guid, GradingJobDeltaMessage>>(_pendingDeltas);
                for (var i = 0; i < deltas.Count; i++)
                {
                    if (ApplyDelta(deltas[i].Value))
                        _pendingDeltas.Remove(deltas[i].Key);
                }
            }
        }

        [MessageHandler(typeof(GradingJobDeltaMessage))]
        private void HandleDelta(MessageContext context, GradingJobDeltaMessage message)
        {
            if (_shutdown)
                return;
            if (!_contentReady || !_context.InGame()
                || GradingWorldBridge.Current?.SceneReady != true)
            {
                DeferDelta(message);
                return;
            }
            ApplyDelta(message);
        }

        private void ApplyState(GradingStateMessage message)
        {
            // Bind each authoritative set to its own DTO id, never by list position: reuse a local
            // set already bound to that id so a join baseline cannot orphan a job whose accept is
            // still in flight.
            var existing = new Dictionary<Guid, GradeCardSubmitSet>();
            foreach (var pair in _jobIds)
            {
                if (pair.Key != null)
                    existing[pair.Value] = pair.Key;
            }

            var list = new List<GradeCardSubmitSet>(message.Sets.Count);
            var ids = new Dictionary<GradeCardSubmitSet, Guid>();
            for (var i = 0; i < message.Sets.Count; i++)
            {
                var dto = message.Sets[i];
                var set = existing.TryGetValue(dto.Id, out var bound) && bound != null
                    ? ApplySetDto(bound, dto)
                    : ToGameSet(dto);
                list.Add(set);
                ids[set] = dto.Id;
            }

            // A local job still awaiting its accept is not in the baseline yet; keep it so the
            // in-flight delta binds the surviving object instead of appending a duplicate.
            foreach (var pair in _jobIds)
            {
                if (pair.Key != null && !ids.ContainsValue(pair.Value))
                {
                    list.Add(pair.Key);
                    ids[pair.Key] = pair.Value;
                }
            }

            _jobIds.Clear();
            foreach (var pair in ids)
                _jobIds[pair.Key] = pair.Value;
            CPlayerData.m_GradeCardInProgressList = list;
            RefreshWebsite();
        }

        private static GradeCardSubmitSet ApplySetDto(GradeCardSubmitSet set, GradingSetDto dto)
        {
            set.m_ServiceLevel = dto.ServiceLevel;
            set.m_DayPassed = dto.DayPassed;
            set.m_MinutePassed = dto.MinutePassed;
            var cards = set.m_CardDataList ??= new List<CardData>(dto.Cards.Count);
            cards.Clear();
            for (var i = 0; i < dto.Cards.Count; i++)
                cards.Add(Clone(dto.Cards[i]));
            return set;
        }

        private bool ApplyDelta(GradingJobDeltaMessage message)
        {
            // A submission we observed locally: the vanilla call already minted the job, so the
            // authoritative delta only needs to bind that local job to the host id before the
            // prediction retires. Applying it as a remote change would add a duplicate job.
            if (message.PredictionId != Guid.Empty
                && _localJobSets.TryGetValue(message.PredictionId, out var localSet))
            {
                _localJobSets.Remove(message.PredictionId);
                if (localSet != null)
                    _jobIds[localSet] = message.Id;
                PredictionApi.Ack(message.PredictionId);
                // Vanilla already reset the selection and closed the screen; only drop the stale
                // screen reference the rejection path would otherwise reopen.
                _submitScreen = null;
                RefreshWebsite();
                return true;
            }

            PredictionApi.AckOrApply(message.PredictionId, () =>
            {
                var list = CPlayerData.m_GradeCardInProgressList
                    ?? new List<GradeCardSubmitSet>();
                var index = FindSet(list, message.Id);
                if (message.Removed)
                {
                    if (index >= 0)
                    {
                        _jobIds.Remove(list[index]);
                        list.RemoveAt(index);
                    }
                }
                else
                {
                    var replacement = ToGameSet(message);
                    if (index >= 0)
                    {
                        _jobIds.Remove(list[index]);
                        list[index] = replacement;
                    }
                    else
                        list.Add(replacement);
                    _jobIds[replacement] = message.Id;
                }
                CPlayerData.m_GradeCardInProgressList = list;
                // This body only runs for a delta that is not ours (AckOrApply retired our own
                // prediction in the branch above). Clearing the local submission selection and
                // showing the "cards sent" status/SFX is the actor's own post-submit side effect;
                // running it here would wipe a peer's in-progress selection for someone else's job.
                RefreshWebsite();
            });
            return true;
        }

        private void DeferDelta(GradingJobDeltaMessage message)
        {
            if (_pendingDeltas.TryGetValue(message.Id, out var previous))
            {
                _localJobSets.Remove(previous.PredictionId);
                PredictionApi.Ack(previous.PredictionId);
            }
            _pendingDeltas[message.Id] = message;
        }

        private int FindSet(List<GradeCardSubmitSet> list, Guid id)
        {
            // Client job identity is carried by a side table because the game object itself has
            // no stable id field.
            for (var i = 0; i < list.Count; i++)
            {
                if (_jobIds.TryGetValue(list[i], out var existing) && existing == id)
                    return i;
            }
            return -1;
        }

        /// <summary>Optimistic local half of a submission: clear the selection and close the
        /// screen. <see cref="Submit"/> restores the selection and reopens the screen if the host
        /// rejects the intent, so a refusal never strands the cards out of view.</summary>
        private void ApplyLocalSubmission(int serviceLevel, int cap,
            GradedCardSubmitSelectScreen screen)
        {
            var fresh = new GradeCardSubmitSet
            {
                m_ServiceLevel = serviceLevel,
                m_CardDataList = new List<CardData>(cap),
            };
            for (var i = 0; i < cap; i++)
                fresh.m_CardDataList.Add(new CardData());
            CPlayerData.m_CurrentGradeCardSubmitSet = fresh;
            screen?.CloseScreen();
            RefreshWebsite();
            SetStatus("cards sent for grading - they mature on the host's days", 4f);
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
        }

        /// <summary>World reports that a predicted collection removal was rejected: the card is
        /// back in the album. Drop it from any unsubmitted grading selection so the selection
        /// only ever contains cards the host actually reserved.</summary>
        internal static void OnCardRemovalRefused(CardData card, int amount)
        {
            var active = _active;
            if (active == null || card == null || amount <= 0)
                return;
            active.StripRefusedSelection(card, amount);
        }

        private void StripRefusedSelection(CardData card, int amount)
        {
            var set = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (set?.m_CardDataList == null)
                return;

            var stripped = 0;
            for (var i = 0; i < set.m_CardDataList.Count && stripped < amount; i++)
            {
                var slot = set.m_CardDataList[i];
                if (slot == null || slot.monsterType == EMonsterType.None
                    || !SameSelectionCard(slot, card))
                {
                    continue;
                }

                set.m_CardDataList[i] = new CardData();
                stripped++;
                UpdateSubmitPanel(i, set.m_CardDataList[i]);
            }

            if (stripped == 0)
                return;

            CoopPlugin.Log.LogWarning("grading: the host refused to reserve " + stripped
                + " selected card(s) (" + GradingInterop.DescribeCard(card)
                + "); removed them from the submission selection");
            SetStatus("the host could not reserve those cards - they were returned to the album", 4f);
            RefreshWebsite();
        }

        private static void UpdateSubmitPanel(int index, CardData card)
        {
            var panels = SceneRef<GradedCardSubmitSelectScreen>.Get()?.m_GradeCardPanelUIList;
            if (panels != null && index >= 0 && index < panels.Count)
                panels[index].UpdateCardUI(card);
        }

        private static bool SameSelectionCard(CardData left, CardData right)
            => left != null && right != null
                && left.expansionType == right.expansionType
                && left.monsterType == right.monsterType
                && left.borderType == right.borderType
                && left.isFoil == right.isFoil
                && left.isDestiny == right.isDestiny
                && left.isChampionCard == right.isChampionCard
                && left.gradedCardIndex == right.gradedCardIndex
                && GradingInterop.Encoded(left) == GradingInterop.Encoded(right);

        /// <summary>Host-rejection callback for a submission. The optimistic apply closed the
        /// submit screen, so restoring the selection is invisible unless the screen comes back.
        /// Prefer the website's own open path (keeps child registration correct) and fall back to
        /// the screen itself when no free job slot blocks the website path.</summary>
        private void ReopenSubmissionScreen()
        {
            var screen = _submitScreen;
            if (screen == null || screen.IsScreenOpened())
                return;

            _website ??= SceneRef<GradeCardWebsiteUIScreen>.Get();
            if (_website != null && _website.gameObject.activeInHierarchy
                && CPlayerData.m_GradeCardInProgressList != null
                && CPlayerData.m_GradeCardInProgressList.Count < 4)
            {
                _website.OnPressNewSubmissionButton();
                return;
            }

            // No free job slot: still show the recovered selection rather than hiding it.
            screen.OpenScreen();
        }

        private static GradeCardSubmitSet CloneSet(GradeCardSubmitSet source)
        {
            var clone = new GradeCardSubmitSet
            {
                m_ServiceLevel = source.m_ServiceLevel,
                m_DayPassed = source.m_DayPassed,
                m_MinutePassed = source.m_MinutePassed,
                m_CardDataList = new List<CardData>(source.m_CardDataList?.Count ?? 0),
            };
            if (source.m_CardDataList != null)
            {
                for (var i = 0; i < source.m_CardDataList.Count; i++)
                    clone.m_CardDataList.Add(Clone(source.m_CardDataList[i]));
            }
            return clone;
        }

        private static GradeCardSubmitSet ToGameSet(GradingSetDto dto)
            => ApplySetDto(new GradeCardSubmitSet(), dto);

        private static GradeCardSubmitSet ToGameSet(GradingJobDeltaMessage dto)
        {
            var set = new GradeCardSubmitSet
            {
                m_ServiceLevel = dto.ServiceLevel,
                m_DayPassed = dto.DayPassed,
                m_MinutePassed = dto.MinutePassed,
                m_CardDataList = new List<CardData>(dto.Cards.Count),
            };
            for (var i = 0; i < dto.Cards.Count; i++)
                set.m_CardDataList.Add(Clone(dto.Cards[i]));
            return set;
        }

        private static bool IsShowingOrHiding(GradedCardSubmitSelectScreen screen)
        {
            if (screen == null || FiShowingAlpha == null || FiHidingAlpha == null)
                return true;
            return (bool)FiShowingAlpha.GetValue(screen) || (bool)FiHidingAlpha.GetValue(screen);
        }

        private void RefreshWebsite()
        {
            _website ??= SceneRef<GradeCardWebsiteUIScreen>.Get();
            if (_website != null && _website.gameObject.activeInHierarchy)
                _website.UpdateSubmissionProgressPanelUI();
        }

        private void SetStatus(string text, float seconds)
            => _context?.SetStatusLine?.Invoke(text, seconds);

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

        internal static void InventoryReset()
        {
            if (_active == null)
                return;
            _active._pendingState = null;
            _active.RetainPendingLocalJobs();
            _active.ResetPendingDeltas();
            _active._contentReady = false;
            _active._jobIds.Clear();
            _active._submitScreen = null;
        }

        /// <summary>A data reload replaces or clears authoritative grading state, but a submission
        /// still awaiting its host decision must keep its local-job binding. The accept delta is
        /// matched by prediction id, not by object, so dropping the entry makes the accept retire
        /// the prediction without binding and the next authoritative delta append the host job a
        /// second time. Entries leave only when the prediction retires (accepted and bound, or
        /// rejected), which <see cref="RetireLocalJob"/> and <see cref="ApplyDelta"/> already do.</summary>
        private void RetainPendingLocalJobs()
        {
            var retired = new List<Guid>();
            foreach (var pair in _localJobSets)
            {
                if (!PredictionApi.IsPending(pair.Key))
                    retired.Add(pair.Key);
            }
            for (var i = 0; i < retired.Count; i++)
                _localJobSets.Remove(retired[i]);
        }

        /// <summary>Drops deferred authoritative deltas on a reload, except the accept delta of a
        /// submission still awaiting its host decision: keeping it lets <see cref="ApplyPending"/>
        /// bind the surviving local job once the reloaded scene is ready instead of appending a
        /// duplicate.</summary>
        private void ResetPendingDeltas()
        {
            var kept = new List<KeyValuePair<Guid, GradingJobDeltaMessage>>();
            foreach (var delta in _pendingDeltas)
            {
                if (delta.Value.PredictionId != Guid.Empty
                    && _localJobSets.ContainsKey(delta.Value.PredictionId))
                {
                    kept.Add(delta);
                }
                else
                {
                    PredictionApi.Ack(delta.Value.PredictionId);
                }
            }
            _pendingDeltas.Clear();
            for (var i = 0; i < kept.Count; i++)
                _pendingDeltas[kept[i].Key] = kept[i].Value;
        }

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingDeltas.Values)
                PredictionApi.Ack(delta.PredictionId);
            _pendingDeltas.Clear();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            PredictionApi.PredictionRetired -= RetireLocalJob;
            _harmony?.UnpatchSelf();
            _harmony = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _pendingState = null;
            ClearPendingDeltas();
            _jobIds.Clear();
            _localJobSets.Clear();
            _submitScreen = null;
            GradingInterop.Reset();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
