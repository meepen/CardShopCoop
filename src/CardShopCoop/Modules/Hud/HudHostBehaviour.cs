using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using CardShopCoop.Net.Connection;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Modules.Hud
{
    [ServerBehaviour]
    public sealed class HudHostBehaviour : CoopBehaviour
    {
        private const float MaxContribution = 100000000f;
        private const int MaxProgressContribution = 100000000;

        private static HudHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private HudAuthoritativeState _lastSnapshot;
        private bool _hasLastSnapshot;
        private Guid _pendingPredictionId;
        private readonly Dictionary<CEvent, PendingContribution> _contributionsByEvent = new();

        private readonly struct PendingContribution
        {
            internal readonly HudContributionKind Kind;
            internal readonly Guid PredictionId;

            internal PendingContribution(HudContributionKind kind, Guid predictionId)
            {
                Kind = kind;
                PredictionId = predictionId;
            }
        }

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;
            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.hud.host");
                _harmony.CreateClassProcessor(typeof(EventQueuePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(AddCoinPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ReduceCoinPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(AddShopExperiencePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(AddFamePatch)).Patch();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"HUD host initialization failed: {error}");
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                _context = null;
                throw;
            }
        }

        private void Update()
        {
            if (_shutdown)
            {
                return;
            }

            HudPresentationState.Tick(Time.deltaTime);
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null)
            {
                return;
            }

            SendBaseline(connection.Id);
        }

        [MessageHandler(typeof(HudContributionIntent))]
        private void HandleContribution(MessageContext context, HudContributionIntent message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            if (!TryValidateContribution(message))
            {
                Reject(context, message.PredictionId);
                return;
            }

            switch (message.Kind)
            {
                case HudContributionKind.AddCoin:
                    QueueContribution(message.PredictionId, new CEventPlayer_AddCoin(message.Value));
                    break;
                case HudContributionKind.ReduceCoin:
                    if (!EconomyAuthority.TryReserveHostSpend(message.Value, out var spend))
                    {
                        Reject(context, message.PredictionId);
                        _pendingPredictionId = Guid.Empty;
                        return;
                    }

                    _pendingPredictionId = message.PredictionId;
                    bool queued;
                    try
                    {
                        queued = EconomyAuthority.QueueHostSpend(spend);
                    }
                    finally
                    {
                        _pendingPredictionId = Guid.Empty;
                    }

                    if (!queued)
                    {
                        spend.Dispose();
                        Reject(context, message.PredictionId);
                    }
                    break;
                case HudContributionKind.AddShopExperience:
                    QueueContribution(message.PredictionId,
                        new CEventPlayer_AddShopExp((int)message.Value));
                    break;
                case HudContributionKind.AddFame:
                    QueueContribution(message.PredictionId,
                        new CEventPlayer_AddFame((int)message.Value));
                    break;
                default:
                    return;
            }
        }

        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && _context.InGame() && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
            }
        }

        private static bool TryValidateContribution(HudContributionIntent message)
        {
            if (message == null || !Enum.IsDefined(typeof(HudContributionKind), message.Kind)
                || float.IsNaN(message.Value) || float.IsInfinity(message.Value)
                || message.Value <= 0f || message.Value > MaxContribution)
            {
                return false;
            }

            if (message.Kind != HudContributionKind.AddShopExperience
                && message.Kind != HudContributionKind.AddFame)
            {
                return true;
            }

            return message.Value <= MaxProgressContribution
                && message.Value == (int)message.Value;
        }

        private void QueueContribution(Guid predictionId, CEvent contribution)
        {
            _pendingPredictionId = predictionId;
            try
            {
                CEventManager.QueueEvent(contribution);
            }
            finally
            {
                _pendingPredictionId = Guid.Empty;
            }
        }

        private void PublishContribution(HudContributionKind kind, CEvent evt)
        {
            if (evt == null || !_contributionsByEvent.TryGetValue(evt, out var contribution))
            {
                return;
            }

            _contributionsByEvent.Remove(evt);
            if (_shutdown || !_context.InGame())
                return;

            if (contribution.Kind != kind)
                throw new InvalidOperationException("HUD contribution event kind is inconsistent.");

            var snapshot = Snapshot();
            if (!_hasLastSnapshot)
            {
                _lastSnapshot = snapshot;
                _hasLastSnapshot = true;
            }

            switch (kind)
            {
                case HudContributionKind.AddCoin:
                case HudContributionKind.ReduceCoin:
                    _context.Broadcast(new HudWalletDeltaMessage
                    {
                        PredictionId = contribution.PredictionId,
                        Coins = snapshot.Coins,
                        CoinDisplay = snapshot.CoinDisplay,
                    });

                    break;
                case HudContributionKind.AddShopExperience:
                    _context.Broadcast(new HudProgressDeltaMessage
                    {
                        PredictionId = contribution.PredictionId,
                        Experience = snapshot.Experience,
                        Level = snapshot.Level,
                    });

                    break;
                case HudContributionKind.AddFame:
                    _context.Broadcast(new HudFameDeltaMessage
                    {
                        PredictionId = contribution.PredictionId,
                        Fame = snapshot.Fame,
                    });

                    break;
            }

            _lastSnapshot = snapshot;
        }

        private static bool TryGetContributionKind(CEvent evt, out HudContributionKind kind)
        {
            if (evt is CEventPlayer_AddCoin)
            {
                kind = HudContributionKind.AddCoin;
                return true;
            }

            if (evt is CEventPlayer_ReduceCoin)
            {
                kind = HudContributionKind.ReduceCoin;
                return true;
            }

            if (evt is CEventPlayer_AddShopExp)
            {
                kind = HudContributionKind.AddShopExperience;
                return true;
            }

            if (evt is CEventPlayer_AddFame)
            {
                kind = HudContributionKind.AddFame;
                return true;
            }

            kind = default;
            return false;
        }

        private void SendBaseline(int connectionId)
        {
            var snapshot = Snapshot();
            _lastSnapshot = snapshot;
            _hasLastSnapshot = true;
            _context.Send(connectionId, snapshot);
        }

        private static HudAuthoritativeState Snapshot()
        {
            return new HudAuthoritativeState
            {
                Coins = CPlayerData.m_CoinAmountDouble,
                CoinDisplay = CPlayerData.m_CoinAmount,
                Experience = CPlayerData.m_ShopExpPoint,
                Level = CPlayerData.m_ShopLevel,
                Fame = CPlayerData.m_FamePoint
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
            if (ReferenceEquals(_active, this))
                _active = null;
            _lastSnapshot = null;
            _hasLastSnapshot = false;
            _pendingPredictionId = Guid.Empty;
            _contributionsByEvent.Clear();
            HudPresentationState.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CEventManager), "QueueEvent")]
        private static class EventQueuePatch
        {
            [HarmonyPrefix]
            private static void Prefix(CEvent evt)
            {
                var active = _active;
                if (active == null || active._shutdown || active._context == null
                    || !active._context.InGame() || !TryGetContributionKind(evt, out var kind))
                {
                    return;
                }

                active._contributionsByEvent[evt] = new PendingContribution(kind,
                    active._pendingPredictionId);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddCoin")]
        private static class AddCoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddCoin __0)
                => _active?.PublishContribution(HudContributionKind.AddCoin, __0);
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnReduceCoin")]
        private static class ReduceCoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_ReduceCoin __0)
                => _active?.PublishContribution(HudContributionKind.ReduceCoin, __0);
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddShopExp")]
        private static class AddShopExperiencePatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddShopExp __0)
                => _active?.PublishContribution(HudContributionKind.AddShopExperience, __0);
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddFame")]
        private static class AddFamePatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddFame __0)
                => _active?.PublishContribution(HudContributionKind.AddFame, __0);
        }
    }
}
