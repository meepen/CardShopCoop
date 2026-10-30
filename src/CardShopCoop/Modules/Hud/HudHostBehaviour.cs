using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using CardShopCoop.Net.Connection;
using System;
using System.Collections.Generic;
using UnityEngine;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Hud
{
    [ServerBehaviour]
    public sealed class HudHostBehaviour : CoopBehaviour
    {
        private const float MaxContribution = 100000000f;
        private const int MaxProgressContribution = 100000000;

        /// <summary>The game's own queued-event dispatch. The handler applies the contribution
        /// synchronously through this so it can answer with the host's post-action snapshot; a
        /// queued event would not have run yet when the handler returns.</summary>
        private static readonly System.Reflection.MethodInfo NotifyEvent =
            CardShopCoop.Util.ReflectionSurface.RequiredMethod(typeof(CEventManager), "OnNotify");

        private static HudHostBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private readonly HashSet<CEvent> _queuedContributions = new();

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            try
            {
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
            PeerHandle.ForConnection(connection)?.Send(Snapshot());
        }

        [MessageHandler]
        private ActionOutcome HandleContribution(CoopMessageContext context,
            HudContributionIntent message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return ActionOutcome.Reject("HUD contribution sender is not authenticated");
            }

            if (!TryValidateContribution(message))
            {
                return RejectContribution("HUD contribution is invalid");
            }

            switch (message.Kind)
            {
                case HudContributionKind.AddCoin:
                    ApplyEvent(new CEventPlayer_AddCoin(message.Value));
                    return AppliedContribution(message.Kind);
                case HudContributionKind.ReduceCoin:
                    // Admit against the shared wallet ledger, then apply the game's own debit
                    // event now so the answer below carries the post-spend balance. Queueing the
                    // debit would answer with the pre-spend wallet and apply it a frame later.
                    if (!EconomyAuthority.TryReserveHostSpend(message.Value, out var spend))
                    {
                        return RejectContribution("host wallet could not cover the contribution");
                    }

                    spend.Dispose();
                    ApplyEvent(new CEventPlayer_ReduceCoin(message.Value));
                    return AppliedContribution(message.Kind);
                case HudContributionKind.AddShopExperience:
                    ApplyEvent(new CEventPlayer_AddShopExp((int)message.Value));
                    return AppliedContribution(message.Kind);
                case HudContributionKind.AddFame:
                    ApplyEvent(new CEventPlayer_AddFame((int)message.Value));
                    return AppliedContribution(message.Kind);
                default:
                    return RejectContribution("unsupported HUD contribution kind");
            }
        }

        /// <summary>Applies the contribution through the game's own event listeners immediately,
        /// so the answer carries the resulting authoritative values.</summary>
        private static void ApplyEvent(CEvent evt)
        {
            var manager = CSingleton<CEventManager>.Instance;
            if (manager == null || NotifyEvent == null)
            {
                throw new InvalidOperationException(
                    "HUD contribution could not reach the game event manager.");
            }

            NotifyEvent.Invoke(manager, new object[] { evt });
        }

        /// <summary>Broadcasts and answers with the host's absolute post-contribution state.</summary>
        private ActionOutcome AppliedContribution(HudContributionKind kind)
        {
            var snapshot = Snapshot();
            Broadcast(kind, snapshot);
            return ActionOutcome.Applied(new HudContributionResponse { State = snapshot });
        }

        private static ActionOutcome RejectContribution(string reason)
            => ActionOutcome.Reject(reason, new HudContributionResponse { State = Snapshot() });

        private bool IsAuthenticatedSender(CoopMessageContext context)
        {
            return !_shutdown && RuntimeContext.InGame() && context?.Peer != null
                && context.IsAuthenticated;
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

        /// <summary>Publishes one host-originated economy event (the host's own play or another
        /// module's automation). Guest contributions are applied inline by the handler and never
        /// reach this path.</summary>
        private void PublishContribution(HudContributionKind kind, CEvent evt)
        {
            if (evt == null || !_queuedContributions.Remove(evt))
            {
                return;
            }

            if (_shutdown || !RuntimeContext.InGame())
            {
                return;
            }

            Broadcast(kind, Snapshot());
        }

        private void Broadcast(HudContributionKind kind, HudAuthoritativeState snapshot)
        {
            switch (kind)
            {
                case HudContributionKind.AddCoin:
                case HudContributionKind.ReduceCoin:
                    Context.Broadcast(new HudWalletDeltaMessage
                    {
                        Coins = snapshot.Coins,
                        CoinDisplay = snapshot.CoinDisplay,
                    });

                    break;
                case HudContributionKind.AddShopExperience:
                    Context.Broadcast(new HudProgressDeltaMessage
                    {
                        Experience = snapshot.Experience,
                        Level = snapshot.Level,
                    });

                    break;
                case HudContributionKind.AddFame:
                    Context.Broadcast(new HudFameDeltaMessage
                    {
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
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _queuedContributions.Clear();
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
                    || !active.RuntimeContext.InGame() || !TryGetContributionKind(evt, out _))
                {
                    return;
                }

                active._queuedContributions.Add(evt);
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
