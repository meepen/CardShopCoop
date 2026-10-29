using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Actions;
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
        private bool _shutdown;
        private bool _initialized;
        private Guid _pendingActionId;
        private readonly Dictionary<CEvent, PendingContribution> _contributionsByEvent = new();

        private readonly struct PendingContribution
        {
            internal readonly HudContributionKind Kind;
            internal readonly Guid ActionId;

            internal PendingContribution(HudContributionKind kind, Guid actionId)
            {
                Kind = kind;
                ActionId = actionId;
            }
        }

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            var handlersRegistered = false;
            try
            {
                RuntimeContext.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                Harmony.CreateClassProcessor(typeof(EventQueuePatch)).Patch();
                Harmony.CreateClassProcessor(typeof(AddCoinPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(ReduceCoinPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(AddShopExperiencePatch)).Patch();
                Harmony.CreateClassProcessor(typeof(AddFamePatch)).Patch();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"HUD host initialization failed: {error}");
                if (handlersRegistered)
                {
                    RuntimeContext.Messages.UnregisterAttributedHandlers(this);
                }

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                throw;
            }
            _initialized = true;
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

        [MessageHandler]
        private void HandleContribution(MessageContext context, HudContributionIntent message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            if (!TryValidateContribution(message))
            {
                Reject(context, message.ActionId);
                return;
            }

            switch (message.Kind)
            {
                case HudContributionKind.AddCoin:
                    QueueContribution(message.ActionId, new CEventPlayer_AddCoin(message.Value));
                    break;
                case HudContributionKind.ReduceCoin:
                    if (!EconomyAuthority.TryReserveHostSpend(message.Value, out var spend))
                    {
                        Reject(context, message.ActionId);
                        _pendingActionId = Guid.Empty;
                        return;
                    }

                    _pendingActionId = message.ActionId;
                    bool queued;
                    try
                    {
                        queued = EconomyAuthority.QueueHostSpend(spend);
                    }
                    finally
                    {
                        _pendingActionId = Guid.Empty;
                    }

                    if (!queued)
                    {
                        spend.Dispose();
                        Reject(context, message.ActionId);
                    }
                    break;
                case HudContributionKind.AddShopExperience:
                    QueueContribution(message.ActionId,
                        new CEventPlayer_AddShopExp((int)message.Value));
                    break;
                case HudContributionKind.AddFame:
                    QueueContribution(message.ActionId,
                        new CEventPlayer_AddFame((int)message.Value));
                    break;
                default:
                    return;
            }
        }

        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && RuntimeContext.InGame() && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid actionId)
        {
            if (actionId != Guid.Empty && context?.Connection != null)
            {
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, actionId);
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

        private void QueueContribution(Guid actionId, CEvent contribution)
        {
            _pendingActionId = actionId;
            try
            {
                CEventManager.QueueEvent(contribution);
            }
            finally
            {
                _pendingActionId = Guid.Empty;
            }
        }

        private void PublishContribution(HudContributionKind kind, CEvent evt)
        {
            if (evt == null || !_contributionsByEvent.TryGetValue(evt, out var contribution))
            {
                return;
            }

            _contributionsByEvent.Remove(evt);
            if (_shutdown || !RuntimeContext.InGame())
            {
                return;
            }

            if (contribution.Kind != kind)
            {
                throw new InvalidOperationException("HUD contribution event kind is inconsistent.");
            }


            var snapshot = Snapshot();

            switch (kind)
            {
                case HudContributionKind.AddCoin:
                case HudContributionKind.ReduceCoin:
                    RuntimeContext.Broadcast(new HudWalletDeltaMessage
                    {
                        ActionId = contribution.ActionId,
                        Coins = snapshot.Coins,
                        CoinDisplay = snapshot.CoinDisplay,
                    });

                    break;
                case HudContributionKind.AddShopExperience:
                    RuntimeContext.Broadcast(new HudProgressDeltaMessage
                    {
                        ActionId = contribution.ActionId,
                        Experience = snapshot.Experience,
                        Level = snapshot.Level,
                    });

                    break;
                case HudContributionKind.AddFame:
                    RuntimeContext.Broadcast(new HudFameDeltaMessage
                    {
                        ActionId = contribution.ActionId,
                        Fame = snapshot.Fame,
                    });

                    break;
            }
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
            RuntimeContext.Send(connectionId, snapshot);
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
            {
                return;
            }

            _shutdown = true;
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _pendingActionId = Guid.Empty;
            _contributionsByEvent.Clear();
            HudPresentationState.Clear();
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CEventManager), "QueueEvent")]
        private static class EventQueuePatch
        {
            [HarmonyPrefix]
            private static void Prefix(CEvent evt)
            {
                var active = _active;
                if (active == null || active._shutdown
                    || !active.RuntimeContext.InGame() || !TryGetContributionKind(evt, out var kind))
                {
                    return;
                }

                active._contributionsByEvent[evt] = new PendingContribution(kind,
                    active._pendingActionId);
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
