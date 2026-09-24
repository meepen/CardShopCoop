using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.GameTime
{
    /// <summary>
    /// Applies authoritative host corrections without patching client game code. Vanilla continues
    /// advancing the clock between event-driven host messages; a message received before the local
    /// clock is initialized is retained until the local lifecycle says it is ready.
    /// </summary>
    [ClientBehaviour]
    public sealed class GameTimeClientBehaviour : CoopBehaviour
    {
        private static GameTimeClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private DayTimeMessage _pendingMessage;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("com.zwhit.cardshopcoop.game-time.client");
            _harmony.CreateClassProcessor(typeof(LightManagerReadyPatch)).Patch();
            _context.Messages.RegisterAttributedHandlers(this);
        }

        [MessageHandler(typeof(DayTimeMessage))]
        private void HandleDayTime(MessageContext context, DayTimeMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            // The baseline and event-driven corrections can arrive while the guest is loading.
            // One latest authoritative message is sufficient; applying it is idempotent.
            _pendingMessage = message;
            TryApplyPending();
        }

        private static void Apply(LightManager manager, DayTimeMessage message)
        {
            var morningReset = message.Hour == 8 && message.Minute == 0 && !message.HasDayEnded
                && !message.ShopOnceOpen;
            CPlayerData.m_CurrentDay = message.Day;
            if (morningReset)
            {
                CPlayerData.m_IsShopOpen = false;
                CPlayerData.m_IsShopOnceOpen = false;
                GameTimeInterop.ResetSunlightIntensity.Invoke(manager, null);
            }
            else
            {
                CPlayerData.m_IsShopOnceOpen = message.ShopOnceOpen;
            }
            GameTimeInterop.TimeHour.SetValue(manager, message.Hour);
            GameTimeInterop.TimeMin.SetValue(manager, message.Minute);
            GameTimeInterop.TimeMinFloat.SetValue(manager, message.MinuteFloat);
            GameTimeInterop.TimeOfDayIndex.SetValue(manager, message.TimeOfDayIndex);
            GameTimeInterop.HasDayEnded.SetValue(manager, message.HasDayEnded);
            GameTimeInterop.EvaluateTimeClock.Invoke(manager, null);
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingMessage == null || _context == null || !_context.InGame())
            {
                return;
            }

            // The clock surface belongs to the scene's LightManager, and it is not usable until the
            // game has finished restoring it (m_FinishLoading, set by LightManager.Init). The join
            // baseline or a correction can land during the world load, so the latest message is
            // retained here and applied by the LightManager init hook below once the clock is live.
            // Treating this ordinary lifecycle gap as fatal used to disconnect the guest mid-join.
            var manager = GameTimeInterop.FindSceneManager();
            if (!GameTimeInterop.IsSceneReady(manager))
            {
                CoopPlugin.Log.LogDebug("game-time baseline held until the world clock is ready.");
                return;
            }

            var pending = _pendingMessage;
            Apply(manager, pending);
            _pendingMessage = null;
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => TryApplyPending();

        private void OnLightManagerReady()
            => TryApplyPending();

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id != 1)
            {
                return;
            }

            _pendingMessage = null;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _pendingMessage = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(LightManager), "Init")]
        private static class LightManagerReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.OnLightManagerReady();
        }
    }
}
