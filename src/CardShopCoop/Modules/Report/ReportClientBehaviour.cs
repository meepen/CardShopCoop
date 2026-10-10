using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Register;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Report
{
    /// <summary>
    /// Guest-side report mirror. The recap is display-only: both vanilla advance paths are
    /// suppressed on the guest and instead signal readiness to the host, which is the single
    /// writer of the next day and the shared event fee. The recap is opened as soon as the local
    /// player is out of any blocking screen, with a persistent HUD notice while it waits.
    /// </summary>
    [ClientBehaviour]
    public sealed class ReportClientBehaviour : CoopBehaviour
    {
        private static readonly FieldInfo FiIsLerping =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_IsLerpingNumber");
        private static readonly FieldInfo FiHoldingMouseDown =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_IsHoldingMouseDown");
        private static readonly FieldInfo FiMouseDownTime =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_MouseDownTime");
        private static readonly FieldInfo FiPhoneMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");
        private static readonly FieldInfo FiCashMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsCashCounterMode");
        private static readonly FieldInfo FiInUiMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsInUIMode");
        private static readonly FieldInfo FiViewAlbumMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsViewCardAlbumMode");
        private static readonly FieldInfo FiExitingAlbumMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsExitingViewCardAlbumMode");
        private static readonly FieldInfo FiScanRestockMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsScanRestockMode");
        private static readonly FieldInfo FiCameraPhotoMode =
            ReflectionSurface.RequiredField(typeof(InteractionPlayerController), "m_IsCameraTakePhotoMode");
        private static readonly FieldInfo FiLoadingGrp =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_LoadingScreenGrp");
        private static readonly FieldInfo FiLoadingCurrentDay =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_LoadingCurrentDayText");
        private static readonly FieldInfo FiLoadingNextDay =
            ReflectionSurface.RequiredField(typeof(EndOfDayReportScreen), "m_LoadingNextDayText");

        private const int CounterSlice = 0;
        private const int MoneySlice = 1;
        private const int ReviewSlice = 2;
        private const string EndOfDayPendingNotice =
            "End of Day pending, back out of what you are doing to proceed!";

        private static ReportClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private ReportStateMessage _pendingState;
        private EndOfDayReportScreen _screen;
        private InteractionPlayerController _ipc;
        private GameReportDataCollect _clientOpenReport;
        private int _reviewSeq = -1;
        private bool _haveOpenReport;
        private bool _pendingOpen;
        private bool _shutdown;
        private bool _nextDayLocalReady;
        private ReportNextDayWaitMessage _nextDayWait;
        private Coroutine _rolloverCoroutine;

        /// <summary>True while the host's recap is owed here but has not opened yet. Every write
        /// goes through this property so the pending notice cannot outlive the state that shows
        /// it; writing the same value re-asserts the notice because another subsystem's HUD clear
        /// must not desynchronize the two.</summary>
        private bool PendingOpen
        {
            get => _pendingOpen;
            set
            {
                _pendingOpen = value;
                RefreshPendingNotice();
            }
        }

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.report.client");
                Patch(typeof(NextButtonPatch));
                Patch(typeof(NextDayPatch));
                Patch(typeof(ShowGoNextDayPatch));
                Patch(typeof(ReportClosePatch));
                PatchScreenReadiness();
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Report client initialization failed: " + error);
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

        private void Patch(Type patchType)
        {
            _harmony.CreateClassProcessor(patchType).Patch();
        }

        private void PatchScreenReadiness()
        {
            // Awake was added to this screen in the beta build. Resolve it dynamically so the
            // legacy build remains loadable while still providing a precise UI-ready signal where
            // the screen exposes one.
            var awake = AccessTools.Method(typeof(EndOfDayReportScreen), "Awake");
            if (awake != null)
            {
                _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(ReportClientBehaviour),
                    nameof(ScreenReadyPostfix)));
            }
        }

        private static void ScreenReadyPostfix(EndOfDayReportScreen __instance)
        {
            if (_active == null || _active._shutdown)
            {
                return;
            }

            _active._screen = __instance;
            _active.TryApplyPendingState();
        }

        /// <summary>Retries a blocked recap open while the host's report is owed. This is not an
        /// idle poll: it only runs between the host opening the recap and this guest actually
        /// seeing it, and each attempt re-checks the states the player must back out of first.</summary>
        private void Update()
        {
            if (_shutdown || !_pendingOpen)
            {
                return;
            }

            if (_context == null || !_context.InGame())
            {
                RefreshPendingNotice();
                return;
            }

            PendingOpen = !TryOpenReportScreen();
        }

        private void RefreshPendingNotice()
        {
            var show = !_shutdown && _pendingOpen && _context != null && _context.InGame();
            HudApi.SetEndOfDayPending(show, show ? EndOfDayPendingNotice : "");
        }

        [MessageHandler(typeof(ReportStateMessage))]
        private void HandleState(MessageContext context, ReportStateMessage message)
        {
            _pendingState = message;
            TryApplyPendingState();
        }

        [MessageHandler(typeof(ReportDeltaMessage))]
        private void HandleDelta(MessageContext context, ReportDeltaMessage message)
        {
            if (_shutdown)
                return;
            PredictionApi.AckOrApply(message.PredictionId,
                () => ApplyState(ToState(message)));
        }

        private static ReportStateMessage ToState(ReportDeltaMessage message)
            => new ReportStateMessage
            {
                Full = false,
                Index = message.Kind,
                OpenScreen = message.OpenScreen,
                CloseScreen = message.CloseScreen,
                CustomerVisited = message.CustomerVisited,
                CheckoutCount = message.CheckoutCount,
                CustomerDisatisfied = message.CustomerDisatisfied,
                CustomerBoughtItem = message.CustomerBoughtItem,
                CustomerBoughtCard = message.CustomerBoughtCard,
                CustomerPlayed = message.CustomerPlayed,
                StoreExpGained = message.StoreExpGained,
                StoreLevelGained = message.StoreLevelGained,
                ItemAmountSold = message.ItemAmountSold,
                CardAmountSold = message.CardAmountSold,
                TotalPlayTableTime = message.TotalPlayTableTime,
                TotalItemEarning = message.TotalItemEarning,
                TotalCardEarning = message.TotalCardEarning,
                TotalPlayTableEarning = message.TotalPlayTableEarning,
                SupplyCost = message.SupplyCost,
                UpgradeCost = message.UpgradeCost,
                EmployeeCost = message.EmployeeCost,
                RentCost = message.RentCost,
                BillCost = message.BillCost,
                CardPackOpened = message.CardPackOpened,
                SmellyCustomerCleaned = message.SmellyCustomerCleaned,
                ManualCheckoutCount = message.ManualCheckoutCount,
                GemMintCardObtained = message.GemMintCardObtained,
                ReviewCount = message.ReviewCount,
                ReviewScoreAverage = message.ReviewScoreAverage,
                Reviews = message.Reviews,
            };

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id != 1)
            {
                return;
            }

            _pendingState = null;
            _clientOpenReport = default(GameReportDataCollect);
            _reviewSeq = -1;
            _haveOpenReport = false;
            PendingOpen = false;
            ClearNextDayWait();
        }

        private void ApplyState(ReportStateMessage message)
        {
            ApplyStateInner(message);
        }

        private void ApplyStateInner(ReportStateMessage message)
        {
            var full = message.Full;
            var index = message.Index;
            if (!full && (index < CounterSlice || index > ReviewSlice))
            {
                throw new ArgumentOutOfRangeException(nameof(message.Index), message.Index,
                    "Unknown authoritative report discriminator.");
            }

            var counters = full || index == CounterSlice;
            var money = full || index == MoneySlice;
            var review = full || index == ReviewSlice;
            var openScreen = counters && message.OpenScreen;
            var report = CPlayerData.m_GameReportDataCollect;

            if (message.CloseScreen)
            {
                CloseClientReport();
                // The host closes the recap as the first step of the vanilla day roll-over (it has
                // just accepted every player's Next Day). Play the same "Loading Day X" overlay the
                // host's OnPressGoNextDay shows, so the guest sees the roll-over too. The day
                // advance and the host event fee stay host-owned; the guest only mirrors the overlay.
                PlayNextDayRollover();
                return;
            }

            if (counters)
            {
                report.customerVisited = message.CustomerVisited;
                report.checkoutCount = message.CheckoutCount;
                report.customerDisatisfied = message.CustomerDisatisfied;
                report.customerBoughtItem = message.CustomerBoughtItem;
                report.customerBoughtCard = message.CustomerBoughtCard;
                report.customerPlayed = message.CustomerPlayed;
                report.storeExpGained = message.StoreExpGained;
                report.storeLevelGained = message.StoreLevelGained;
                report.itemAmountSold = message.ItemAmountSold;
                report.cardAmountSold = message.CardAmountSold;
                report.cardPackOpened = message.CardPackOpened;
                report.smellyCustomerCleaned = message.SmellyCustomerCleaned;
                report.manualCheckoutCount = message.ManualCheckoutCount;
                report.gemMintCardObtained = message.GemMintCardObtained;
            }

            if (money)
            {
                report.totalPlayTableTime = message.TotalPlayTableTime;
                report.totalItemEarning = message.TotalItemEarning;
                report.totalCardEarning = message.TotalCardEarning;
                report.totalPlayTableEarning = message.TotalPlayTableEarning;
                report.supplyCost = message.SupplyCost;
                report.upgradeCost = message.UpgradeCost;
                report.employeeCost = message.EmployeeCost;
                report.rentCost = message.RentCost;
                report.billCost = message.BillCost;
            }

            CPlayerData.m_GameReportDataCollect = report;
            if (_haveOpenReport || openScreen)
            {
                // Money and review slices are part of the same open report. Keep the latest
                // authoritative totals so closing the screen does not restore an old snapshot.
                _clientOpenReport = report;
            }

            if (review)
            {
                ApplyReviews(message, full);
            }

            if (openScreen)
            {
                _clientOpenReport = CPlayerData.m_GameReportDataCollect;
                PendingOpen = !TryOpenReportScreen();
            }
        }

        private void ApplyReviews(ReportStateMessage message, bool full)
        {
            var totalCount = message.ReviewCount;
            var entries = message.Reviews;
            var reviews = CPlayerData.m_CustomerReviewDataList;
            if (full)
            {
                reviews.Clear();
                _reviewSeq = totalCount - entries.Count;
            }
            else if (_reviewSeq < 0)
            {
                _reviewSeq = CPlayerData.m_CustomerReviewCount;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                if (totalCount - entries.Count + i + 1 <= _reviewSeq)
                    continue;

                var entry = entries[i];
                reviews.Add(new CustomerReviewData
                {
                    customerReviewType = (ECustomerReviewType)entry.CustomerReviewType,
                    starLevel = entry.StarLevel,
                    textSOGoodBadLevel = entry.TextSOGoodBadLevel,
                    textSOIndex = entry.TextSOIndex,
                    day = entry.Day,
                    hour = entry.Hour,
                    minute = entry.Minute,
                    itemType = entry.ItemType,
                    customerName = entry.CustomerName,
                });
            }

            _reviewSeq = Math.Max(_reviewSeq, totalCount);
            while (reviews.Count > 50)
                reviews.RemoveAt(0);

            CPlayerData.m_CustomerReviewCount = totalCount;
            CPlayerData.m_CustomerReviewScoreAverage = message.ReviewScoreAverage;
        }

        /// <summary>Attempts to bring up the guest recap. Returns false while the local player is
        /// still in a state the recap must not be layered on top of; the pending notice tells them
        /// to back out and <see cref="Update"/> retries. A manned register or cash-counter position
        /// is released here first, exactly like the vanilla End Day key does.</summary>
        private bool TryOpenReportScreen()
        {
            if (_screen == null)
            {
                _screen = SceneRef<EndOfDayReportScreen>.Get();
            }

            if (_screen == null)
            {
                return false;
            }

            if (EndOfDayReportScreen.IsActive())
            {
                _haveOpenReport = true;
                PendingOpen = false;
                return true;
            }

            if (_ipc == null)
            {
                _ipc = SceneRef<InteractionPlayerController>.Get();
            }

            var playerController = _ipc;
            if (playerController != null)
            {
                // The player cannot back out of a counter position themselves without losing the
                // checkout, so the game's own exit path runs here. Everything else waits: the
                // recap must not be forced over the phone, the card album, or an open UI screen.
                if ((bool)FiCashMode.GetValue(playerController))
                {
                    playerController.OnExitCashCounterMode();
                    // A manned counter also owns UI mode through its card-payment phase
                    // (StartGivingChange calls EnterUIMode). Once the counter is unmanned that
                    // screen has no reachable exit, because vanilla only clears the mode when the
                    // payment completes and RaycastCashCounterState stops running while it is set.
                    // Release it here or the recap would wait forever on a state the player cannot
                    // back out of.
                    if ((bool)FiInUiMode.GetValue(playerController))
                    {
                        playerController.ExitUIMode();
                    }
                }

                RegisterClientBehaviour.ForceExitManned();
                if (IsInteractionBlocking(playerController))
                {
                    return false;
                }
            }

            if (IsGlobalScreenBlocking())
            {
                return false;
            }

            EndOfDayReportScreen.OpenScreen();
            _haveOpenReport = true;
            PendingOpen = false;
            return true;
        }

        /// <summary>Modes the player must back out of before the recap can be shown: the phone,
        /// an open UI screen (price/restock/grading/table screens), the card album, or the phone's
        /// scanner and camera modes. The counter position is released separately above.</summary>
        private static bool IsInteractionBlocking(InteractionPlayerController playerController)
        {
            return (bool)FiPhoneMode.GetValue(playerController)
                || (bool)FiInUiMode.GetValue(playerController)
                || (bool)FiViewAlbumMode.GetValue(playerController)
                || (bool)FiExitingAlbumMode.GetValue(playerController)
                || (bool)FiScanRestockMode.GetValue(playerController)
                || (bool)FiCameraPhotoMode.GetValue(playerController);
        }

        /// <summary>Vanilla ignores its own End Day key while the pause, settings, or loading
        /// screens are up, so the mirrored recap waits for those too. SceneRef never fabricates a
        /// game CSingleton the way <c>Instance</c> would.</summary>
        private static bool IsGlobalScreenBlocking()
        {
            var pause = SceneRef<PauseScreen>.Get();
            if (pause != null && pause.m_ScreenGrp != null && pause.m_ScreenGrp.activeSelf)
            {
                return true;
            }

            var settings = SceneRef<SettingScreen>.Get();
            if (settings != null && settings.m_ScreenGrp != null && settings.m_ScreenGrp.activeSelf)
            {
                return true;
            }

            var loading = SceneRef<LoadingScreen>.Get();
            return loading != null && loading.m_ScreenGrp != null && loading.m_ScreenGrp.activeSelf;
        }

        private void TryApplyPendingState()
        {
            if (_shutdown || !_context.InGame())
            {
                return;
            }

            if (_pendingState != null)
            {
                var pending = _pendingState;
                _pendingState = null;
                ApplyState(pending);
            }

            if (_pendingOpen)
            {
                PendingOpen = !TryOpenReportScreen();
            }

        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPendingState();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            StopNextDayRollover();
            _screen = null;
            _ipc = null;
            TryApplyPendingState();
        }

        /// <summary>Closes a guest report without invoking the host-owned next-day action.</summary>
        public static void CloseClientReport()
        {
            var active = _active;
            if (active == null || active._shutdown)
            {
                return;
            }

            // The host's close always ends the pending-open state, even when this guest never got
            // the recap open at all; otherwise the notice would outlive the day it belongs to.
            active.PendingOpen = false;
            if (active._screen == null)
            {
                active._screen = SceneRef<EndOfDayReportScreen>.Get();
            }

            if (active._screen == null || !EndOfDayReportScreen.IsActive())
            {
                return;
            }

            SoundManager.SetEnableSound_CoinIncrease(false);
            if (active._haveOpenReport)
                CPlayerData.m_GameReportDataCollect = active._clientOpenReport;

            active._haveOpenReport = false;
            EndOfDayReportScreen.CloseScreen();
            FiHoldingMouseDown.SetValue(active._screen, false);
            FiMouseDownTime.SetValue(active._screen, 0f);
        }

        private static void ReportClosed()
        {
            if (_active == null || _active._shutdown)
            {
                return;
            }

            SoundManager.SetEnableSound_CoinIncrease(false);
        }

        private static bool NextButton(EndOfDayReportScreen screen)
        {
            if (_active == null || _active._shutdown)
            {
                return true;
            }

            // While the recap still counts its totals up, the button skips the animation; that is
            // also vanilla, so let it run. Once the numbers have settled the press becomes a
            // "ready" signal for the host, and the guest must NOT run vanilla
            // OnPressGoNextDay -> DelayGoNextDay: that would advance this guest's day and queue
            // its own CEventPlayer_ReduceCoin for the host event fee, double-charging the shared
            // wallet when the host's own roll-over runs. The host is the single day/fee writer; it
            // rolls over once every player has readied and the guest mirrors the close + overlay.
            if ((bool)FiIsLerping.GetValue(screen))
            {
                return true;
            }

            _active.ReadyForNextDay();
            return false;
        }

        private static bool NextDay()
        {
            if (_active == null || _active._shutdown)
            {
                return true;
            }

            _active.ReadyForNextDay();
            return false;
        }

        /// <summary>Tells the host this guest is ready for the next day. The recap stays open (with
        /// the wait notice) until every player has readied and the host rolls the day over.</summary>
        private void ReadyForNextDay()
        {
            if (_context == null || !_context.InGame())
            {
                return;
            }

            if (!_nextDayLocalReady)
            {
                _nextDayLocalReady = true;
                _context.Send(1, new ReportNextDayReadyMessage());
            }

            ApplyNextDayWaitUi();
        }

        [MessageHandler(typeof(ReportNextDayWaitMessage))]
        private void HandleNextDayWait(MessageContext context, ReportNextDayWaitMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            if (message == null || !message.Active)
            {
                ClearNextDayWait();
                return;
            }

            _nextDayWait = message;
            ApplyNextDayWaitUi();
        }

        private void ApplyNextDayWaitUi()
        {
            var wait = _nextDayWait;
            if (wait == null || !wait.Active || !_nextDayLocalReady
                || wait.Pending == null || wait.Pending.Count == 0)
            {
                HudApi.SetNextDayWait(false, "");
                return;
            }

            HudApi.SetNextDayWait(true, "Waiting for: " + string.Join(", ", wait.Pending));
        }

        private void ClearNextDayWait()
        {
            _nextDayLocalReady = false;
            _nextDayWait = null;
            HudApi.SetNextDayWait(false, "");
        }

        /// <summary>Mirrors vanilla <c>DelayGoNextDay</c>'s presentation on the guest: show the
        /// "Loading Day X â†’ X+1" overlay for the same window. The host's <c>CloseScreen</c> already
        /// closed the recap and (on the host only) advanced the day and charged the event fee.</summary>
        private void PlayNextDayRollover()
        {
            if (_shutdown || _rolloverCoroutine != null)
            {
                return;
            }

            var screen = _screen != null ? _screen : SceneRef<EndOfDayReportScreen>.Get();
            if (screen == null)
            {
                return;
            }

            _screen = screen;
            _rolloverCoroutine = StartCoroutine(NextDayRollover(screen));
        }

        private IEnumerator NextDayRollover(EndOfDayReportScreen screen)
        {
            var loading = FiLoadingGrp.GetValue(screen) as GameObject;
            if (FiLoadingCurrentDay.GetValue(screen) is TextMeshProUGUI current)
            {
                current.text = LocalizationManager.GetTranslation("Day XXX")
                    .Replace("XXX", (CPlayerData.m_CurrentDay + 1).ToString());
            }

            if (FiLoadingNextDay.GetValue(screen) is TextMeshProUGUI next)
            {
                next.text = LocalizationManager.GetTranslation("Day XXX")
                    .Replace("XXX", (CPlayerData.m_CurrentDay + 2).ToString());
            }

            if (loading != null)
            {
                loading.SetActive(true);
            }

            // Match DelayGoNextDay's own timings: the recap closed at the top, then the loading
            // overlay stays up while the host advances the day before it is hidden.
            yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(2.5f);

            if (loading != null)
            {
                loading.SetActive(false);
            }

            _rolloverCoroutine = null;
        }

        private void StopNextDayRollover()
        {
            if (_rolloverCoroutine != null)
            {
                StopCoroutine(_rolloverCoroutine);
                _rolloverCoroutine = null;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            StopNextDayRollover();
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _pendingState = null;
            _screen = null;
            _ipc = null;
            _clientOpenReport = default(GameReportDataCollect);
            _reviewSeq = -1;
            _haveOpenReport = false;
            PendingOpen = false;
            ClearNextDayWait();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(EndOfDayReportScreen), "OnPressGoNextButton")]
        private static class NextButtonPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(EndOfDayReportScreen __instance) => NextButton(__instance);
        }

        [HarmonyPatch(typeof(EndOfDayReportScreen), "OnPressGoNextDay")]
        private static class NextDayPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => NextDay();
        }

        /// <summary>With the recap already open, the Enter/GoNextDay key belongs to the recap's own
        /// Next Day action. The vanilla <c>ShowGoNextDayScreen</c> would call <c>OpenScreen</c>,
        /// which closes an already-open recap, so suppress it while the recap is up. With the recap
        /// closed this still runs vanilla, which is the first Enter that opens the menu.</summary>
        [HarmonyPatch(typeof(InteractionPlayerController), "ShowGoNextDayScreen")]
        private static class ShowGoNextDayPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
                => _active == null || _active._shutdown || !EndOfDayReportScreen.IsActive();
        }

        [HarmonyPatch(typeof(EndOfDayReportScreen), "CloseScreen")]
        private static class ReportClosePatch
        {
            [HarmonyPostfix]
            private static void Postfix() => ReportClosed();
        }
    }
}
