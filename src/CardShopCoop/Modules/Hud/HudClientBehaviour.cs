using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Hud
{
    [ClientBehaviour]
    public sealed class HudClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "hud";
        private CoopRuntimeContext _context;
        private HudAuthoritativeState _pending;
        private bool _shutdown;
        private Harmony _harmony;
        private static HudClientBehaviour _active;
        private int _highestNotifiedLevel = -1;
        private bool _hasAuthoritativeLevel;
        // Per-kind counters bumped whenever an authoritative absolute for that kind is applied.
        // A pending contribution whose rollback arrives after such a delta is already governed by
        // it; restoring the pre-change snapshot would revert the authoritative value.
        private int _walletVersion;
        private int _progressVersion;
        private int _fameVersion;
        private int _applyingSnapshot;
        private GameUIScreen _gameUi;
        private readonly System.Collections.Generic.Dictionary<CEvent, PendingContribution>
            _pendingContributions = new();

        private readonly struct PendingContribution
        {
            internal readonly HudContributionKind Kind;
            internal readonly float Value;
            internal readonly HudAuthoritativeState Previous;

            internal PendingContribution(HudContributionKind kind, float value,
                HudAuthoritativeState previous)
            {
                Kind = kind;
                Value = value;
                Previous = previous;
            }
        }

        private void OnEnable()
        {
            if (_shutdown || _context != null || _harmony != null)
                return;
            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.hud.client");
                _harmony.CreateClassProcessor(typeof(EconomyQueuePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ObservedAddCoinPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ObservedReduceCoinPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ObservedAddShopExpPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ObservedAddFamePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(GameUiReadyPatch)).Patch();
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"HUD client initialization failed: {error}");
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                if (_active == this)
                    _active = null;
                _context = null;
                throw;
            }
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            _gameUi = null;
        }

        private void Update()
        {
            if (_shutdown)
                return;

            HudPresentationState.Tick(Time.deltaTime);
        }

        private void ApplyPendingIfReady()
        {
            if (_shutdown || !_context.InGame() || !IsGameUiReady())
            {
                return;
            }

            if (_pending != null)
            {
                var state = _pending;
                _pending = null;
                Apply(state);
            }
        }

        private bool IsGameUiReady()
        {
            _gameUi = SceneRef<GameUIScreen>.Get();
            return _gameUi != null && _gameUi.gameObject.scene.IsValid()
                && _gameUi.isActiveAndEnabled;
        }

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _highestNotifiedLevel = -1;
            _hasAuthoritativeLevel = false;
            _gameUi = null;
        }

        /// <summary>
        /// Registers one economy contribution the game already applied as a post-hoc prediction.
        /// The game performed the local mutation through its own handler, so this only forwards the
        /// intent and keeps the apply/undo closures for a rejection replay; a host accept retires it
        /// (the absolute authoritative value then overwrites the mirror).
        /// </summary>
        private void ForwardObserved(HudContributionKind kind, float value,
            HudAuthoritativeState previous)
        {
            if (_shutdown || _context == null || !_context.InGame())
                return;

            var version = VersionFor(kind);
            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new HudContributionIntent
                {
                    PredictionId = predictionId,
                    Kind = kind,
                    Value = value,
                }),
                () => ApplyContribution(kind, value),
                () => RestoreUnlessSuperseded(kind, version, previous));
        }

        private int VersionFor(HudContributionKind kind)
        {
            switch (kind)
            {
                case HudContributionKind.AddShopExperience:
                    return _progressVersion;
                case HudContributionKind.AddFame:
                    return _fameVersion;
                default:
                    return _walletVersion;
            }
        }

        /// <summary>Rolls a rejected contribution back to the captured pre-change state, unless an
        /// authoritative absolute for the same kind has arrived since the contribution was queued.
        /// That delta already governs the value (and folding it in also dropped the optimistic
        /// change), so restoring the stale snapshot would revert an interleaved remote change.</summary>
        private void RestoreUnlessSuperseded(HudContributionKind kind, int version,
            HudAuthoritativeState previous)
        {
            if (VersionFor(kind) != version)
            {
                return;
            }

            Restore(previous);
        }

        /// <summary>Publishes one economy event captured at queue time. The capture exists because
        /// the "owned by a module intent" and reconciliation guards must be evaluated when the game
        /// queues the event, not when the handler later runs.</summary>
        private void PublishObserved(CEvent evt)
        {
            if (evt == null || !_pendingContributions.TryGetValue(evt, out var pending))
                return;
            _pendingContributions.Remove(evt);
            ForwardObserved(pending.Kind, pending.Value, pending.Previous);
        }

        [MessageHandler(typeof(HudAuthoritativeState))]
        private void State(MessageContext _, HudAuthoritativeState state)
        {
            if (state == null)
                return;
            _pending = Clone(state);
            ApplyPendingIfReady();
        }

        [MessageHandler(typeof(HudWalletDeltaMessage))]
        private void WalletDelta(MessageContext _, HudWalletDeltaMessage message)
        {
            if (message == null)
                return;

            // The delta carries the host's ABSOLUTE wallet. Retire our own pending contribution and
            // always fold the absolute in, so a host edit that interleaved with our optimistic
            // change (which the optimistic apply alone cannot reproduce) does not drift.
            //
            // The mod does not fabricate a money popup here. The client plays vanilla, so every
            // change the local player causes runs the game's own CEventPlayer_Add/ReduceCoin handler
            // and GameUIScreen pops it exactly once; a second popup from this authoritative echo is
            // what produced the double "-$X" for every EconomyActionScope-owned action. The cost is
            // that a remote player's spend now updates the wallet silently (it only reaches the
            // guest as an absolute SetCoin, which the game never pops).
            PredictionApi.Confirm(message.PredictionId,
                () => ApplyWallet(message.Coins, message.CoinDisplay));
        }

        [MessageHandler(typeof(HudProgressDeltaMessage))]
        private void ProgressDelta(MessageContext _, HudProgressDeltaMessage message)
        {
            if (message == null)
                return;

            // Absolute exp/level: retire our own pending contribution and always fold the host's
            // values in (see the wallet handler).
            PredictionApi.Confirm(message.PredictionId,
                () => ApplyProgress(message.Experience, message.Level));
        }

        [MessageHandler(typeof(HudFameDeltaMessage))]
        private void FameDelta(MessageContext _, HudFameDeltaMessage message)
        {
            if (message == null)
                return;

            // Absolute fame: retire our own pending contribution and always fold the host's
            // value in (see the wallet handler).
            PredictionApi.Confirm(message.PredictionId,
                () => ApplyFame(message.Fame));
        }

        private void Apply(HudAuthoritativeState state)
        {
            _walletVersion++;
            _progressVersion++;
            _fameVersion++;
            ApplyValues(state.Coins, state.CoinDisplay, state.Experience, state.Level, state.Fame);
            NotifyLevel(state.Level);
        }

        private void ApplyWallet(double coins, float coinDisplay)
        {
            _walletVersion++;
            UpdatePending(state =>
            {
                state.Coins = coins;
                state.CoinDisplay = coinDisplay;
            });
            ApplyValues(coins, coinDisplay, CPlayerData.m_ShopExpPoint,
                CPlayerData.m_ShopLevel, CPlayerData.m_FamePoint);
        }

        private void ApplyProgress(int experience, int level)
        {
            _progressVersion++;
            UpdatePending(state =>
            {
                state.Experience = experience;
                state.Level = level;
            });
            ApplyValues(CPlayerData.m_CoinAmountDouble, CPlayerData.m_CoinAmount,
                experience, level, CPlayerData.m_FamePoint);
            NotifyLevel(level);
        }

        private void ApplyFame(int fame)
        {
            _fameVersion++;
            UpdatePending(state => state.Fame = fame);
            ApplyValues(CPlayerData.m_CoinAmountDouble, CPlayerData.m_CoinAmount,
                CPlayerData.m_ShopExpPoint, CPlayerData.m_ShopLevel, fame);
        }

        private void ApplyContribution(HudContributionKind kind, float value)
        {
            var state = Capture();
            switch (kind)
            {
                case HudContributionKind.AddCoin:
                    state.CoinDisplay += value;
                    state.Coins += value;
                    RoundCoins(ref state);
                    break;
                case HudContributionKind.ReduceCoin:
                    state.CoinDisplay -= value;
                    state.Coins -= value;
                    RoundCoins(ref state);
                    break;
                case HudContributionKind.AddShopExperience:
                    AddExperience(state, value);
                    break;
                case HudContributionKind.AddFame:
                    state.Fame = Mathf.Clamp(state.Fame + Mathf.RoundToInt(value), 0, 99999999);
                    break;
            }

            ApplyValues(state.Coins, state.CoinDisplay, state.Experience, state.Level, state.Fame);
        }

        private void ApplyValues(double coins, float coinDisplay, int experience, int level, int fame)
        {
            _applyingSnapshot++;
            try
            {
                CPlayerData.m_CoinAmountDouble = coins;
                CPlayerData.m_CoinAmount = coinDisplay;
                CPlayerData.m_ShopExpPoint = experience;
                CPlayerData.m_ShopLevel = level;
                CPlayerData.m_FamePoint = fame;
                CEventManager.QueueEvent(new CEventPlayer_SetCoin(coinDisplay, coins));
                CEventManager.QueueEvent(new CEventPlayer_SetShopExp(experience));
                CEventManager.QueueEvent(new CEventPlayer_SetFame(fame));
            }
            finally
            {
                _applyingSnapshot--;
            }
        }

        private void Restore(HudAuthoritativeState state)
        {
            ApplyValues(state.Coins, state.CoinDisplay, state.Experience, state.Level, state.Fame);
        }

        /// <summary>Records a level the local vanilla game just presented (its own
        /// <c>CPlayer_OnAddShopExp</c> already queued the level-up). The high-water mark makes the
        /// host's authoritative echo of the same level a no-op, so a guest's own purchase does not
        /// present the level-up twice, while a genuinely remote level still does.</summary>
        private void NoteLocalShopLevel()
        {
            var level = CPlayerData.m_ShopLevel;
            if (!_hasAuthoritativeLevel || level > _highestNotifiedLevel)
            {
                _highestNotifiedLevel = level;
                _hasAuthoritativeLevel = true;
            }
        }

        private void NotifyLevel(int level)
        {
            if (!_hasAuthoritativeLevel)
            {
                _highestNotifiedLevel = level;
                _hasAuthoritativeLevel = true;
                return;
            }

            if (level < _highestNotifiedLevel)
            {
                _highestNotifiedLevel = level;
                return;
            }

            for (var next = _highestNotifiedLevel + 1; next <= level; next++)
                CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(next));
            if (level > _highestNotifiedLevel)
                _highestNotifiedLevel = level;
        }

        private void UpdatePending(Action<HudAuthoritativeState> update)
        {
            if (_pending != null)
                update(_pending);
        }

        private HudAuthoritativeState Capture()
        {
            return new HudAuthoritativeState
            {
                Coins = CPlayerData.m_CoinAmountDouble,
                CoinDisplay = CPlayerData.m_CoinAmount,
                Experience = CPlayerData.m_ShopExpPoint,
                Level = CPlayerData.m_ShopLevel,
                Fame = CPlayerData.m_FamePoint,
            };
        }

        private static HudAuthoritativeState Clone(HudAuthoritativeState state)
        {
            return new HudAuthoritativeState
            {
                Coins = state.Coins,
                CoinDisplay = state.CoinDisplay,
                Experience = state.Experience,
                Level = state.Level,
                Fame = state.Fame,
            };
        }

        private static void RoundCoins(ref HudAuthoritativeState state)
        {
            var rate = GameInstance.GetCurrencyConversionRate();
            state.Coins = Math.Round(state.Coins, rate > 1f ? 3 : 2,
                MidpointRounding.AwayFromZero);
            if (state.CoinDisplay < -100000000f)
            {
                state.CoinDisplay = 0f;
                state.Coins = 0d;
            }
        }

        private static void AddExperience(HudAuthoritativeState state, float value)
        {
            state.Experience += Mathf.RoundToInt(value);
            while (state.Experience >= CPlayerData.GetExpRequiredToLevelUp())
            {
                state.Experience -= CPlayerData.GetExpRequiredToLevelUp();
                state.Level++;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _pending = null;
            _applyingSnapshot = 0;
            _pendingContributions.Clear();
            _highestNotifiedLevel = -1;
            _hasAuthoritativeLevel = false;
            _gameUi = null;
            HudPresentationState.Clear();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (_active == this)
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        /// <summary>
        /// Economy observer. The client runs the game's own economy handlers (it plays vanilla),
        /// so this no longer suppresses anything. The queue hook captures the pre-change values
        /// while the event is still being queued, so the "owned by a module intent" scope and the
        /// prediction-reconciliation guards are evaluated at the moment the game emits the event.
        /// Each event that is genuinely the guest's own contribution is then forwarded once from
        /// the matching handler postfix; a host accept retires the prediction and the host's
        /// absolute wallet/progress/fame delta remains the only authoritative writer.
        /// </summary>
        [HarmonyPatch(typeof(CEventManager), "QueueEvent")]
        private static class EconomyQueuePatch
        {
            [HarmonyPrefix]
            private static void Prefix(CEvent evt)
            {
                var active = _active;
                if (active == null || active._shutdown || active._context == null
                    || !active._context.InGame() || evt == null
                    || active._applyingSnapshot != 0
                    || EconomyActionScope.Active
                    || PredictionApi.IsApplying || PredictionApi.IsReconciling)
                {
                    return;
                }

                if (!TryGetContribution(evt, out var kind, out var value))
                    return;

                active._pendingContributions[evt] = new PendingContribution(kind, value,
                    active.Capture());
            }

            private static bool TryGetContribution(CEvent evt, out HudContributionKind kind,
                out float value)
            {
                switch (evt)
                {
                    case CEventPlayer_AddCoin add:
                        kind = HudContributionKind.AddCoin;
                        value = add.m_CoinValue;
                        return true;
                    case CEventPlayer_ReduceCoin reduce:
                        kind = HudContributionKind.ReduceCoin;
                        value = reduce.m_CoinValue;
                        return true;
                    case CEventPlayer_AddShopExp exp:
                        kind = HudContributionKind.AddShopExperience;
                        value = exp.m_ExpValue;
                        return true;
                    case CEventPlayer_AddFame fame:
                        kind = HudContributionKind.AddFame;
                        value = fame.m_FameValue;
                        return true;
                    default:
                        kind = default;
                        value = 0f;
                        return false;
                }
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddCoin")]
        private static class ObservedAddCoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddCoin __0) => _active?.PublishObserved(__0);
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnReduceCoin")]
        private static class ObservedReduceCoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_ReduceCoin __0) => _active?.PublishObserved(__0);
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddShopExp")]
        private static class ObservedAddShopExpPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddShopExp __0)
            {
                var active = _active;
                if (active == null)
                {
                    return;
                }

                // The guest plays vanilla, so its own XP gain already ran CPlayer_OnAddShopExp,
                // which queued the game's own ShopLeveledUp presentation. Record the level the
                // client has now presented before forwarding, so the host's authoritative echo of
                // that same level (ApplyProgress -> NotifyLevel) does not queue it a second time.
                active.NoteLocalShopLevel();
                active.PublishObserved(__0);
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "CPlayer_OnAddFame")]
        private static class ObservedAddFamePatch
        {
            [HarmonyPostfix]
            private static void Postfix(CEventPlayer_AddFame __0) => _active?.PublishObserved(__0);
        }

        [HarmonyPatch(typeof(GameUIScreen), "OnEnable")]
        private static class GameUiReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.ApplyPendingIfReady();
        }
    }
}
