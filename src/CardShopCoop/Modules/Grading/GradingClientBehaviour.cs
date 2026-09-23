using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
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
        private readonly Dictionary<GradeCardSubmitSet, int> _jobIds = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private GradingStateMessage _pendingState;
        private readonly Dictionary<int, GradingJobDeltaMessage> _pendingDeltas = new();
        private bool _contentReady;
        private bool _shutdown;
        private GradeCardWebsiteUIScreen _website;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.grading.client");
                GradingPatches.ApplyClient(_harmony);
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
        {
            _contentReady = true;
            ApplyPending();
        }

        [MessageHandler(typeof(GradingStateMessage))]
        private void HandleState(MessageContext context, GradingStateMessage message)
        {
            if (_shutdown)
                return;
            _pendingState = message;
            ApplyPending();
        }

        internal bool Submit(GradedCardSubmitSelectScreen screen)
        {
            if (_shutdown || _context?.InGame() != true || IsShowingOrHiding(screen))
                return false;

            var submitSet = CPlayerData.m_CurrentGradeCardSubmitSet;
            if (submitSet?.m_CardDataList == null)
                return false;

            var cards = new List<CardData>();
            for (var i = 0; i < submitSet.m_CardDataList.Count; i++)
            {
                var card = submitSet.m_CardDataList[i];
                if (card != null && card.monsterType != EMonsterType.None)
                    cards.Add(Clone(card));
            }
            if (cards.Count == 0)
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardSelected);
                return false;
            }

            var cap = GradingInterop.MaxSubmitSlots;
            var previous = CloneSet(submitSet);
            PredictionApi.Predict(
                "grading",
                predictionId => _context.Send(1, new GradingOpMessage
                {
                    PredictionId = predictionId,
                    ServiceLevel = (byte)submitSet.m_ServiceLevel,
                    CompanyId = (byte)GradingInterop.CurrentCompanyId,
                    Cards = cards,
                }),
                () => ApplyLocalSubmission(submitSet.m_ServiceLevel, cap, screen),
                () =>
                {
                    CPlayerData.m_CurrentGradeCardSubmitSet = previous;
                    RefreshWebsite();
                });
            return false;
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
                var deltas = new List<KeyValuePair<int, GradingJobDeltaMessage>>(_pendingDeltas);
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
            var list = new List<GradeCardSubmitSet>();
            for (var i = 0; i < message.Sets.Count; i++)
            {
                var set = ToGameSet(message.Sets[i]);
                list.Add(set);
            }
            _jobIds.Clear();
            for (var i = 0; i < list.Count; i++)
                _jobIds[list[i]] = message.Sets[i].Id;
            CPlayerData.m_GradeCardInProgressList = list;
            RefreshWebsite();
        }

        private bool ApplyDelta(GradingJobDeltaMessage message)
        {
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
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
                if (message.PredictionId != Guid.Empty)
                    ClearCurrentSubmission();
                RefreshWebsite();
            });
            return true;
        }

        private void DeferDelta(GradingJobDeltaMessage message)
        {
            if (_pendingDeltas.TryGetValue(message.Id, out var previous))
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            _pendingDeltas[message.Id] = message;
        }

        private int FindSet(List<GradeCardSubmitSet> list, int id)
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

        private void ClearCurrentSubmission()
        {
            var current = CPlayerData.m_CurrentGradeCardSubmitSet;
            ApplyLocalSubmission(current?.m_ServiceLevel ?? 0,
                GradingInterop.MaxSubmitSlots, null);
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
            => _context.SetStatusLine?.Invoke(text, seconds);

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
            _active.ClearPendingDeltas();
            _active._contentReady = false;
            _active._jobIds.Clear();
        }

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingDeltas.Values)
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            _pendingDeltas.Clear();
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
            _pendingState = null;
            ClearPendingDeltas();
            _jobIds.Clear();
            GradingInterop.Reset();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
