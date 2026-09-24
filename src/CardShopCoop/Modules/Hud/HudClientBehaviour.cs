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
        private int _applyingSnapshot;
        private GameUIScreen _gameUi;

        // Last authoritative values pushed by the host, kept separate from the optimistic
        // predictions so a guest can recover the size of each confirmed change and play the
        // game's own top-right money/experience popup.
        private float _authoritativeCoinDisplay;
        private int _authoritativeExperience;
        private int _authoritativeLevel;
        private bool _hasAuthoritativeTrack;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.hud.client");
                _harmony.CreateClassProcessor(typeof(LocalEconomyPatch)).Patch();
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
            _hasAuthoritativeTrack = false;
            _gameUi = null;
        }

        private bool Forward(HudContributionKind kind, float value)
        {
            if (_shutdown || !_context.InGame())
            {
                return false;
            }

            var previous = Capture();
            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new HudContributionIntent
                {
                    PredictionId = predictionId,
                    Kind = kind,
                    Value = value,
                }),
                () => ApplyContribution(kind, value),
                () => Restore(previous));
            return true;
        }

        [MessageHandler(typeof(HudAuthoritativeState))]
        private void State(MessageContext _, HudAuthoritativeState state)
        {
            if (state == null)
                return;
            Track(state);
            _pending = Clone(state);
            ApplyPendingIfReady();
        }

        [MessageHandler(typeof(HudWalletDeltaMessage))]
        private void WalletDelta(MessageContext _, HudWalletDeltaMessage message)
        {
            if (message == null)
                return;

            var confirmedOwnContribution = PredictionApi.IsPending(message.PredictionId);
            var hadTrack = _hasAuthoritativeTrack;
            var delta = message.CoinDisplay - _authoritativeCoinDisplay;
            _authoritativeCoinDisplay = message.CoinDisplay;
            _hasAuthoritativeTrack = true;

            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyWallet(message.Coins, message.CoinDisplay));

            // The optimistic apply already popped the local player's own change, so only remote
            // changes (and host-driven changes with no local prediction) need a popup here.
            if (!confirmedOwnContribution && hadTrack)
                ShowWalletPopup(delta);
        }

        [MessageHandler(typeof(HudProgressDeltaMessage))]
        private void ProgressDelta(MessageContext _, HudProgressDeltaMessage message)
        {
            if (message == null)
                return;

            var confirmedOwnContribution = PredictionApi.IsPending(message.PredictionId);
            var hadTrack = _hasAuthoritativeTrack;
            var gained = hadTrack
                ? GainedExperience(_authoritativeLevel, _authoritativeExperience,
                    message.Level, message.Experience)
                : 0;
            _authoritativeExperience = message.Experience;
            _authoritativeLevel = message.Level;
            _hasAuthoritativeTrack = true;

            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyProgress(message.Experience, message.Level));

            if (!confirmedOwnContribution && gained > 0)
                ShowExperiencePopup(gained);
        }

        [MessageHandler(typeof(HudFameDeltaMessage))]
        private void FameDelta(MessageContext _, HudFameDeltaMessage message)
        {
            if (message == null)
                return;

            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyFame(message.Fame));
        }

        private void Apply(HudAuthoritativeState state)
        {
            ApplyValues(state.Coins, state.CoinDisplay, state.Experience, state.Level, state.Fame);
            NotifyLevel(state.Level);
        }

        private void ApplyWallet(double coins, float coinDisplay)
        {
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

            // Immediate feedback for the local player's own predicted change. Reconciliation
            // re-applies newer predictions after an undo, so skip the popup while reconciling to
            // avoid replaying changes that already appeared.
            if (!PredictionApi.IsReconciling)
                ShowContributionPopup(kind, value);
        }

        private void ShowContributionPopup(HudContributionKind kind, float value)
        {
            switch (kind)
            {
                case HudContributionKind.AddCoin:
                    ShowWalletPopup(value);
                    break;
                case HudContributionKind.ReduceCoin:
                    ShowWalletPopup(-value);
                    break;
                case HudContributionKind.AddShopExperience:
                    ShowExperiencePopup(Mathf.RoundToInt(value));
                    break;
            }
        }

        private void ShowWalletPopup(float delta)
        {
            if (delta == 0f || !IsGameUiReady())
                return;
            HudPopupBridge.ShowWallet(_gameUi, delta);
        }

        private void ShowExperiencePopup(int gained)
        {
            if (gained <= 0 || !IsGameUiReady())
                return;
            HudPopupBridge.ShowExperience(_gameUi, gained);
        }

        private void Track(HudAuthoritativeState state)
        {
            _authoritativeCoinDisplay = state.CoinDisplay;
            _authoritativeExperience = state.Experience;
            _authoritativeLevel = state.Level;
            _hasAuthoritativeTrack = true;
        }

        /// <summary>Experience gained between two authoritative shop states, expressed as raw XP so
        /// the popup keeps showing the true gain even when the change crossed a level boundary.</summary>
        private static int GainedExperience(int oldLevel, int oldExperience, int newLevel,
            int newExperience)
        {
            if (newLevel < oldLevel)
                return 0;

            return (int)(CumulativeExperience(newLevel, newExperience)
                - CumulativeExperience(oldLevel, oldExperience));
        }

        private static long CumulativeExperience(int level, int experience)
        {
            var total = (long)experience;
            for (var i = 0; i < level; i++)
                total += CPlayerData.GetExpRequiredToLevelUpAtLevel(i);
            return total;
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
            _highestNotifiedLevel = -1;
            _hasAuthoritativeLevel = false;
            _authoritativeCoinDisplay = 0f;
            _authoritativeExperience = 0;
            _authoritativeLevel = 0;
            _hasAuthoritativeTrack = false;
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

        [HarmonyPatch(typeof(CEventManager), "QueueEvent")]
        private static class LocalEconomyPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(CEvent evt)
            {
                var active = _active;
                if (active == null || active._applyingSnapshot != 0
                    || evt == null || !active._context.InGame())
                {
                    return true;
                }

                // Economy events emitted while a prediction is being applied belong to an action
                // the host applies authoritatively too (a guest checkout's AddCoin/AddShopExp, a
                // worker/register action, etc.). Mirroring them here as well would credit the
                // host twice and pop the HUD popup twice, so drop them and let the host's
                // authoritative wallet/exp delta drive the guest.
                if (PredictionApi.IsApplying || PredictionApi.IsReconciling)
                {
                    return false;
                }

                if (evt is CEventPlayer_AddCoin add)
                    return !active.Forward(HudContributionKind.AddCoin, add.m_CoinValue);
                if (evt is CEventPlayer_ReduceCoin reduce)
                    return !active.Forward(HudContributionKind.ReduceCoin, reduce.m_CoinValue);
                if (evt is CEventPlayer_AddShopExp exp)
                    return !active.Forward(HudContributionKind.AddShopExperience, exp.m_ExpValue);
                if (evt is CEventPlayer_AddFame fame)
                    return !active.Forward(HudContributionKind.AddFame, fame.m_FameValue);

                return true;
            }
        }

        [HarmonyPatch(typeof(GameUIScreen), "OnEnable")]
        private static class GameUiReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.ApplyPendingIfReady();
        }
    }
}
