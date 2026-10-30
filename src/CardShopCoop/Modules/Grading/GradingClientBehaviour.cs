using System;
using CardShopCoop.Api;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using CardShopCoop.Util;
using HarmonyLib;

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
        private GradingStateMessage _pendingState;
        private readonly Dictionary<Guid, GradingJobDeltaMessage> _pendingDeltas = new();
        private bool _contentReady;
        private bool _shutdown;
        private bool _initialized;
        private GradeCardWebsiteUIScreen _website;
        private GradedCardSubmitSelectScreen _submitScreen;

        internal static GradingClientBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }


            try
            {
                _active = this;
                GradingPatches.ApplyClient(Harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                throw;
            }
            _initialized = true;
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
        {
            _contentReady = true;
            ApplyPending();
        }

        [MessageHandler]
        private void HandleState(MessageContext context, GradingStateMessage message)
        {
            if (_shutdown)
            {
                return;
            }


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
            if (_shutdown || RuntimeContext.InGame() != true || IsShowingOrHiding(screen))
            {
                return;
            }


            var submitSet = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (submitSet?.m_CardDataList == null)
            {
                return;
            }

            var cards = new List<CardData>();
            for (var i = 0; i < submitSet.m_CardDataList.Count; i++)
            {
                var card = submitSet.m_CardDataList[i];
                if (card != null && card.monsterType != EMonsterType.None)
                {
                    cards.Add(Clone(card));
                }

            }
            if (cards.Count == 0)
            {
                return;
            }


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
            if (!state.Armed || _shutdown || RuntimeContext.InGame() != true)
            {
                return;
            }


            var list = CPlayerData.m_GradeCardInProgressList;
            // Vanilla appends the captured selection object as the new job, so the local job is the
            // game's own object identity rather than list[last]. When vanilla bails (no card
            // selected, or the mirror cannot afford the fee) the object never enters the list and
            // the game has already shown its popup.
            var jobSet = state.Job;
            if (list == null || jobSet == null || !list.Contains(jobSet))
            {
                return;
            }


            var spent = state.SupplyCostBefore
                - CPlayerData.m_GameReportDataCollectPermanent.supplyCost;
            // Bind the local job to its stable id before sending. The host adopts this same id, so
            // the accepted delta finds the job by id rather than appending a duplicate.
            var message = new GradingOpMessage
            {
                JobId = state.JobId,
                ServiceLevel = (byte)state.ServiceLevel,
                CompanyId = (byte)GradingInterop.CurrentCompanyId,
                Cards = state.Cards,
                Prediction = new SubmissionPrediction(this, jobSet, state.Previous,
                    state.ServiceLevel, state.Cap, state.Screen, spent),
            };
            _jobIds[jobSet] = state.JobId;
            try
            {
                Context.Send(message);
            }
            catch
            {
                _jobIds.Remove(jobSet);
                throw;
            }
        }

        /// <summary>One predicted grading submission. The vanilla call already minted, reserved
        /// and charged the job; the prediction owns the local object until the host answers.</summary>
        internal sealed class SubmissionPrediction
        {
            private readonly GradingClientBehaviour _client;
            private readonly GradeCardSubmitSet _jobSet;
            private readonly GradeCardSubmitSet _previous;
            private readonly int _serviceLevel;
            private readonly int _cap;
            private readonly GradedCardSubmitSelectScreen _screen;
            private readonly float _spent;

            internal SubmissionPrediction(GradingClientBehaviour client, GradeCardSubmitSet jobSet,
                GradeCardSubmitSet previous, int serviceLevel, int cap,
                GradedCardSubmitSelectScreen screen, float spent)
            {
                _client = client;
                _jobSet = jobSet;
                _previous = previous;
                _serviceLevel = serviceLevel;
                _cap = cap;
                _screen = screen;
                _spent = spent;
            }

            public void Undo()
                => _client.UndoSubmission(_jobSet, _previous, _serviceLevel, _cap, _screen, _spent);

            public void Merge(ActionResponseMessage response)
                => _client.MergeSubmission(_jobSet, response);
        }

        /// <summary>Reverses a refused submission: removes the locally minted job, restores the
        /// player's selection, refunds the mirrored fee and reopens the submit screen.</summary>
        private void UndoSubmission(GradeCardSubmitSet jobSet, GradeCardSubmitSet previous,
            int serviceLevel, int cap, GradedCardSubmitSelectScreen screen, float spent)
        {
            RemoveLocalJob(jobSet);
            CPlayerData.m_CurrentGradeCardSubmitSet = previous;
            if (spent > 0.0001f)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin(spent, true));
            }

            RefreshWebsite();
            ReopenSubmissionScreen(screen);
        }

        /// <summary>Settles a resolved submission against the host's answer: the accepted job id
        /// binds the surviving local job, or the rejected job list replaces authority.</summary>
        private void MergeSubmission(GradeCardSubmitSet jobSet, ActionResponseMessage response)
        {
            if (response is not GradingIntentResponse result)
            {
                return;
            }

            if (result.Delta != null)
            {
                if (jobSet != null)
                {
                    _jobIds[jobSet] = result.Delta.Id;
                }

                // Vanilla already reset the selection and closed the screen; only drop the stale
                // screen reference the rejection path would otherwise reopen.
                _submitScreen = null;
                RefreshWebsite();
                return;
            }

            if (result.State != null)
            {
                _pendingState = result.State;
                ApplyPending();
            }
        }

        private void RemoveLocalJob(GradeCardSubmitSet set)
        {
            if (set == null)
            {
                return;
            }


            _jobIds.Remove(set);
            CPlayerData.m_GradeCardInProgressList?.Remove(set);
        }

        private void ApplyPending()
        {
            if (_shutdown || !_contentReady || !RuntimeContext.InGame()
                || GradingWorldBridge.Current?.SceneReady != true)
            {
                return;
            }


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
                    {
                        _pendingDeltas.Remove(deltas[i].Key);
                    }

                }
            }
        }

        [MessageHandler]
        private void HandleDelta(MessageContext context, GradingJobDeltaMessage message)
        {
            if (_shutdown)
            {
                return;
            }


            if (!_contentReady || !RuntimeContext.InGame()
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
                {
                    existing[pair.Value] = pair.Key;
                }

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
            {
                _jobIds[pair.Key] = pair.Value;
            }


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
            {
                cards.Add(Clone(dto.Cards[i]));
            }


            return set;
        }

        private bool ApplyDelta(GradingJobDeltaMessage message)
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
                {
                    list.Add(replacement);
                }

                _jobIds[replacement] = message.Id;
            }
            CPlayerData.m_GradeCardInProgressList = list;
            // This body only runs for a delta that is not ours; the actor settles its own
            // submission in the action merge. Clearing the local submission selection and showing
            // the "cards sent" status/SFX is the actor's own post-submit side effect; running it
            // here would wipe a peer's in-progress selection for someone else's job.
            RefreshWebsite();
            return true;
        }

        private void DeferDelta(GradingJobDeltaMessage message)
        {
            _pendingDeltas[message.Id] = message;
        }

        private int FindSet(List<GradeCardSubmitSet> list, Guid id)
        {
            // Client job identity is carried by a side table because the game object itself has
            // no stable id field.
            for (var i = 0; i < list.Count; i++)
            {
                if (_jobIds.TryGetValue(list[i], out var existing) && existing == id)
                {
                    return i;
                }

            }
            return -1;
        }

        /// <summary>World reports that a predicted collection removal was rejected: the card is
        /// back in the album. Drop it from any unsubmitted grading selection so the selection
        /// only ever contains cards the host actually reserved.</summary>
        internal static void OnCardRemovalRefused(CardData card, int amount)
        {
            var active = _active;
            if (active == null || card == null || amount <= 0)
            {
                return;
            }


            active.StripRefusedSelection(card, amount);
        }

        private void StripRefusedSelection(CardData card, int amount)
        {
            var set = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (set?.m_CardDataList == null)
            {
                return;
            }


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
            {
                return;
            }


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
            {
                panels[index].UpdateCardUI(card);
            }

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
        private void ReopenSubmissionScreen(GradedCardSubmitSelectScreen screen)
        {
            screen ??= _submitScreen;
            if (screen == null || screen.IsScreenOpened())
            {
                return;
            }


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
                {
                    clone.m_CardDataList.Add(Clone(source.m_CardDataList[i]));
                }

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
            {
                set.m_CardDataList.Add(Clone(dto.Cards[i]));
            }


            return set;
        }

        private static bool IsShowingOrHiding(GradedCardSubmitSelectScreen screen)
        {
            if (screen == null || FiShowingAlpha == null || FiHidingAlpha == null)
            {

                return true;
            }


            return (bool)FiShowingAlpha.GetValue(screen) || (bool)FiHidingAlpha.GetValue(screen);
        }

        private void RefreshWebsite()
        {
            _website ??= SceneRef<GradeCardWebsiteUIScreen>.Get();
            if (_website != null && _website.gameObject.activeInHierarchy)
            {
                _website.UpdateSubmissionProgressPanelUI();
            }

        }

        private void SetStatus(string text, float seconds)
            => RuntimeContext.SetStatusLine?.Invoke(text, seconds);

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
            {
                return;
            }


            _active._pendingState = null;
            _active._pendingDeltas.Clear();
            _active._contentReady = false;
            _active._jobIds.Clear();
            _active._submitScreen = null;
        }

        private void ClearPendingDeltas()
        {
            _pendingDeltas.Clear();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _pendingState = null;
            ClearPendingDeltas();
            _jobIds.Clear();
            _submitScreen = null;
            GradingInterop.Reset();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


        }

        private void OnDestroy() => Shutdown();
    }
}
