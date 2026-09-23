using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Register;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Report
{
    /// <summary>
    /// Guest-side report mirror.  The recap is display-only: both vanilla advance paths close the
    /// local screen instead of advancing the guest's day or charging the shared event fee again.
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

        private const int CounterSlice = 0;
        private const int MoneySlice = 1;
        private const int ReviewSlice = 2;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.report.client");
                Patch(typeof(NextButtonPatch));
                Patch(typeof(NextDayPatch));
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
            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            _pendingOpen = false;
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
                _pendingOpen = !TryOpenReportScreen();
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
                _pendingOpen = false;
                return true;
            }

            if (_ipc == null)
                _ipc = SceneRef<InteractionPlayerController>.Get();

            var playerController = _ipc;
            if (playerController != null)
            {
                if ((bool)FiPhoneMode.GetValue(playerController))
                    return true;
                if ((bool)FiCashMode.GetValue(playerController))
                    playerController.OnExitCashCounterMode();
                RegisterClientBehaviour.ForceExitManned();
            }

            EndOfDayReportScreen.OpenScreen();
            _haveOpenReport = true;
            _pendingOpen = false;
            return true;
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
                _pendingOpen = !TryOpenReportScreen();
            }

        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPendingState();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
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
            active._pendingOpen = false;
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

            var lerping = (bool)FiIsLerping.GetValue(screen);

            if (lerping)
            {
                return true;
            }

            CloseClientReport();
            return false;
        }

        private static bool NextDay()
        {
            if (_active == null || _active._shutdown)
            {
                return true;
            }

            CloseClientReport();
            return false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
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
            _pendingOpen = false;
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

        [HarmonyPatch(typeof(EndOfDayReportScreen), "CloseScreen")]
        private static class ReportClosePatch
        {
            [HarmonyPostfix]
            private static void Postfix() => ReportClosed();
        }
    }
}
