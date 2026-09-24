using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Report
{
    /// <summary>Host source of the shared end-of-day report and review stream.</summary>
    [ServerBehaviour]
    public sealed class ReportHostBehaviour : CoopBehaviour
    {
        private const int ReviewTail = 15;
        private const int CounterSlice = 0;
        private const int MoneySlice = 1;
        private const int ReviewSlice = 2;

        private static ReportHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private readonly HashSet<int> _nextDayParticipants = new();
        private readonly HashSet<int> _nextDayReadyPeers = new();
        private bool _nextDayHostReady;
        private bool _nextDayGateActive;
        private bool _advancingNextDay;
        private bool _nextDayAdvancing;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.report.host");
                Patch(typeof(ReportOpenPatch));
                Patch(typeof(ReviewAddPatch));
                Patch(typeof(ReportMutationPatch));
                Patch(typeof(NextDayCoroutinePatch));
                Patch(typeof(NextDayGatePatch));
                Patch(typeof(ShowGoNextDayPatch));
                Patch(typeof(TournamentEntryMutationPatch));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Report host initialization failed: " + error);
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

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _nextDayParticipants.Add(connection.Id);
            if (_nextDayGateActive)
            {
                // A late joiner becomes a required reader mid-gate; make sure everyone sees them
                // in the pending list instead of the day silently stalling.
                BroadcastNextDayWait();
            }

            if (!_context.InGame())
            {
                return;
            }

            _context.Send(connection.Id, BuildFullState());
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (_shutdown || connection == null)
            {
                return;
            }

            var wasParticipant = _nextDayParticipants.Remove(connection.Id);
            _nextDayReadyPeers.Remove(connection.Id);
            if (!wasParticipant || !_nextDayGateActive)
            {
                return;
            }

            if (AllNextDayReady())
            {
                AdvanceNextDay();
            }
            else
            {
                BroadcastNextDayWait();
            }
        }

        /// <summary>A guest is ready for the day to roll. The host records it and advances only
        /// once every connected player has readied.</summary>
        [MessageHandler(typeof(ReportNextDayReadyMessage))]
        private void HandleNextDayReady(MessageContext context, ReportNextDayReadyMessage message)
        {
            if (_shutdown || _nextDayAdvancing || !_context.InGame() || context?.Connection == null)
            {
                return;
            }

            var id = context.Connection.Id;
            if (!_nextDayParticipants.Contains(id) || !_nextDayReadyPeers.Add(id))
            {
                return;
            }

            _nextDayGateActive = true;
            if (AllNextDayReady())
            {
                AdvanceNextDay();
            }
            else
            {
                BroadcastNextDayWait();
            }
        }

        /// <summary>Host-side gate for its own Next Day press: record the host's readiness and only
        /// let the vanilla advance run once every player has readied.</summary>
        private bool HostPressNextDay()
        {
            if (_shutdown || _context == null || !_context.InGame())
            {
                return true;
            }

            if (_nextDayAdvancing)
            {
                return false;
            }

            _nextDayHostReady = true;
            _nextDayGateActive = true;
            if (AllNextDayReady())
            {
                AdvanceNextDay();
                return false;
            }

            BroadcastNextDayWait();
            return false;
        }

        private bool AllNextDayReady()
        {
            if (!_nextDayHostReady)
            {
                return false;
            }

            foreach (var id in _nextDayParticipants)
            {
                if (!_nextDayReadyPeers.Contains(id))
                {
                    return false;
                }
            }

            return true;
        }

        private void AdvanceNextDay()
        {
            ResetNextDayGate();
            var screen = SceneRef<EndOfDayReportScreen>.Get();
            if (screen == null)
            {
                BroadcastNextDayWait();
                return;
            }

            // Hold the gate shut until the roll-over coroutine finishes so a re-press during the
            // loading screen cannot re-arm it and leave a stale waiting notice on the new day.
            _nextDayAdvancing = true;
            _advancingNextDay = true;
            try
            {
                // The host's vanilla roll-over closes the recap (which PublishClose already
                // broadcasts), advances the day and charges the event fee. The guest mirrors that
                // by playing the same loading overlay when the close arrives; nothing extra to send.
                screen.OnPressGoNextDay();
            }
            finally
            {
                _advancingNextDay = false;
            }

            BroadcastNextDayWait();
        }

        internal void OnNextDayCoroutineCompleted()
        {
            _nextDayAdvancing = false;
        }

        private void ResetNextDayGate()
        {
            _nextDayHostReady = false;
            _nextDayGateActive = false;
            _nextDayReadyPeers.Clear();
        }

        private void BroadcastNextDayWait()
        {
            if (_shutdown || _context == null || !_context.InGame())
            {
                return;
            }

            var pending = BuildPendingNextDayNames();
            var showHostWait = _nextDayHostReady && pending.Count > 0;
            HudApi.SetNextDayWait(showHostWait,
                showHostWait ? "Waiting for: " + string.Join(", ", pending) : "");
            _context.Broadcast(new ReportNextDayWaitMessage
            {
                Active = _nextDayGateActive,
                Pending = pending,
            });
        }

        private List<string> BuildPendingNextDayNames()
        {
            var pending = new List<string>();
            if (!_nextDayHostReady)
            {
                pending.Add(PresenceApi.LocalPlayerName);
            }

            foreach (var id in _nextDayParticipants)
            {
                if (_nextDayReadyPeers.Contains(id))
                {
                    continue;
                }

                var name = _context.PeerName?.Invoke(id);
                pending.Add(string.IsNullOrWhiteSpace(name) ? "Player " + id : name);
            }

            return pending;
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        private static ReportStateMessage BuildFullState()
        {
            var message = BuildState(CPlayerData.m_GameReportDataCollect, -1, false, false, true);
            message.Full = true;
            message.Index = -1;
            return message;
        }

        private void Publish(ReportStateMessage message)
        {
            if (_shutdown || message == null || !_context.InGame())
            {
                return;
            }

            _context.Broadcast(message);
        }

        private static ReportStateMessage BuildState(GameReportDataCollect report, int index,
            bool openScreen, bool closeScreen = false, bool full = false)
        {
            var message = new ReportStateMessage
            {
                Full = full,
                Index = full ? -1 : index,
                OpenScreen = openScreen,
                CloseScreen = closeScreen,
            };

            var counters = full || index == CounterSlice;
            var money = full || index == MoneySlice;
            var reviewSection = full || index == ReviewSlice;
            if (counters)
            {
                message.CustomerVisited = report.customerVisited;
                message.CheckoutCount = report.checkoutCount;
                message.CustomerDisatisfied = report.customerDisatisfied;
                message.CustomerBoughtItem = report.customerBoughtItem;
                message.CustomerBoughtCard = report.customerBoughtCard;
                message.CustomerPlayed = report.customerPlayed;
                message.StoreExpGained = report.storeExpGained;
                message.StoreLevelGained = report.storeLevelGained;
                message.ItemAmountSold = report.itemAmountSold;
                message.CardAmountSold = report.cardAmountSold;
                message.CardPackOpened = report.cardPackOpened;
                message.SmellyCustomerCleaned = report.smellyCustomerCleaned;
                message.ManualCheckoutCount = report.manualCheckoutCount;
                message.GemMintCardObtained = report.gemMintCardObtained;
            }

            if (money)
            {
                message.TotalPlayTableTime = report.totalPlayTableTime;
                message.TotalItemEarning = report.totalItemEarning;
                message.TotalCardEarning = report.totalCardEarning;
                message.TotalPlayTableEarning = report.totalPlayTableEarning;
                message.SupplyCost = report.supplyCost;
                message.UpgradeCost = report.upgradeCost;
                message.EmployeeCost = report.employeeCost;
                message.RentCost = report.rentCost;
                message.BillCost = report.billCost;
            }

            if (reviewSection)
            {
                message.ReviewCount = CPlayerData.m_CustomerReviewCount;
                message.ReviewScoreAverage = CPlayerData.m_CustomerReviewScoreAverage;
            }

            var reviewList = CPlayerData.m_CustomerReviewDataList;
            if (reviewSection)
            {
                var maxReviews = full ? 50 : ReviewTail;
                var count = Mathf.Min(reviewList == null ? 0 : reviewList.Count, maxReviews);
                for (var i = 0; i < count; i++)
                {
                    var review = reviewList[reviewList.Count - count + i];
                    message.Reviews.Add(new ReportReviewEntry
                    {
                        CustomerReviewType = (int)review.customerReviewType,
                        StarLevel = (byte)Mathf.Clamp(review.starLevel, 0, 255),
                        TextSOGoodBadLevel = (byte)Mathf.Clamp(review.textSOGoodBadLevel, 0, 255),
                        TextSOIndex = review.textSOIndex,
                        Day = review.day,
                        Hour = (byte)Mathf.Clamp(review.hour, 0, 255),
                        Minute = (byte)Mathf.Clamp(review.minute, 0, 255),
                        ItemType = review.itemType,
                        CustomerName = review.customerName ?? "",
                    });
                }
            }

            return message;
        }

        private static bool IsHostActive()
        {
            return _active != null && !_active._shutdown
                && _active._context.InGame();
        }

        private static void ReportOpened()
        {
            if (!IsHostActive())
            {
                return;
            }

            try
            {
                if (!EndOfDayReportScreen.IsActive())
                {
                    return;
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Report open detection failed: " + error.Message);
                return;
            }

            ModuleGuard.Run("report.host:open", () =>
            {
                _active.PublishDelta(BuildState(CPlayerData.m_GameReportDataCollect,
                    CounterSlice, true), CounterSlice);
                _active.PublishDelta(BuildState(CPlayerData.m_GameReportDataCollect,
                    MoneySlice, false), MoneySlice);
                _active.PublishDelta(BuildLatestReview(), ReviewSlice);
            });
        }

        private static void ReviewAdded(int before)
        {
            if (IsHostActive() && CPlayerData.m_CustomerReviewCount != before)
            {
                _active.PublishDelta(BuildLatestReview(), ReviewSlice);
            }
        }

        private void PublishCounterAndMoney()
        {
            PublishDelta(BuildState(CPlayerData.m_GameReportDataCollect, CounterSlice, false),
                CounterSlice);
            PublishDelta(BuildState(CPlayerData.m_GameReportDataCollect, MoneySlice, false),
                MoneySlice);
        }

        internal static void NotifyCommittedPurchaseMutation()
        {
            if (IsHostActive())
                _active.PublishCounterAndMoney();
        }

        private static ReportStateMessage BuildLatestReview()
        {
            var message = BuildState(CPlayerData.m_GameReportDataCollect, ReviewSlice, false);
            if (message.Reviews.Count > 1)
                message.Reviews = new List<ReportReviewEntry>
                {
                    message.Reviews[message.Reviews.Count - 1],
                };
            return message;
        }

        private void PublishClose()
        {
            PublishDelta(new ReportStateMessage { CloseScreen = true }, CounterSlice);
        }

        private void PublishDelta(ReportStateMessage source, int kind)
        {
            if (_shutdown || source == null || !_context.InGame())
                return;
            _context.Broadcast(ToDelta(source, kind));
        }

        private static ReportDeltaMessage ToDelta(ReportStateMessage source, int kind)
            => new ReportDeltaMessage
            {
                Kind = (byte)kind,
                OpenScreen = source.OpenScreen,
                CloseScreen = source.CloseScreen,
                CustomerVisited = source.CustomerVisited,
                CheckoutCount = source.CheckoutCount,
                CustomerDisatisfied = source.CustomerDisatisfied,
                CustomerBoughtItem = source.CustomerBoughtItem,
                CustomerBoughtCard = source.CustomerBoughtCard,
                CustomerPlayed = source.CustomerPlayed,
                StoreExpGained = source.StoreExpGained,
                StoreLevelGained = source.StoreLevelGained,
                ItemAmountSold = source.ItemAmountSold,
                CardAmountSold = source.CardAmountSold,
                TotalPlayTableTime = source.TotalPlayTableTime,
                TotalItemEarning = source.TotalItemEarning,
                TotalCardEarning = source.TotalCardEarning,
                TotalPlayTableEarning = source.TotalPlayTableEarning,
                SupplyCost = source.SupplyCost,
                UpgradeCost = source.UpgradeCost,
                EmployeeCost = source.EmployeeCost,
                RentCost = source.RentCost,
                BillCost = source.BillCost,
                CardPackOpened = source.CardPackOpened,
                SmellyCustomerCleaned = source.SmellyCustomerCleaned,
                ManualCheckoutCount = source.ManualCheckoutCount,
                GemMintCardObtained = source.GemMintCardObtained,
                ReviewCount = source.ReviewCount,
                ReviewScoreAverage = source.ReviewScoreAverage,
                Reviews = source.Reviews,
            };

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _nextDayParticipants.Clear();
            _nextDayReadyPeers.Clear();
            _nextDayHostReady = false;
            _nextDayGateActive = false;
            _advancingNextDay = false;
            _nextDayAdvancing = false;
            HudApi.SetNextDayWait(false, "");
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(EndOfDayReportScreen), "OpenScreen")]
        private static class ReportOpenPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => ReportOpened();
        }

        [HarmonyPatch(typeof(CustomerReviewManager), "AddCustomerReview")]
        private static class ReviewAddPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out int __state)
            {
                __state = CPlayerData.m_CustomerReviewCount;
            }

            [HarmonyPostfix]
            private static void Postfix(int __state) => ReviewAdded(__state);
        }

        [HarmonyPatch]
        private static class ReportMutationPatch
        {
            private static readonly string[][] Methods =
            {
                new[] { "Customer", "OnCustomerReachInsideShop" },
                new[] { "Customer", "ExitShop" },
                new[] { "Customer", "DeodorantSprayCheck" },
                new[] { "Customer", "PlayTableGameEnded" },
                new[] { "CPlayerData", "CreateDefaultData" },
                new[] { "CPlayerData", "CPlayer_OnAddShopExp" },
                new[] { "CustomerTradeCardScreen", "OnPressAccept" },
                new[] { "EndOfDayReportScreen", "CloseScreen" },
                new[] { "AchievementManager", "OnCardPackOpened" },
                new[] { "AchievementManager", "OnCustomerFinishPlay" },
                new[] { "AchievementManager", "OnDuelWin" },
                new[] { "RentBillScreen", "OnPressPayRentBill" },
                new[] { "RentBillScreen", "OnPressPayElectricBill" },
                new[] { "RentBillScreen", "OnPressPaySalaryBill" },
                new[] { "RentBillScreen", "OnPressPayAllBill" },
                new[] { "ExpansionShopUIScreen", "EvaluateCartCheckout" },
                new[] { "ExpansionShopUIScreen", "OnPressUnlockShopB" },
                new[] { "FurnitureShopUIScreen", "EvaluateCartCheckout" },
                new[] { "GradedCardSubmitSelectScreen", "OnPressSubmitButton" },
                new[] { "HireWorkerPanelUI", "OnPressHireButton" },
                new[] { "InteractableAutoPackOpener", "OnMouseButtonUp" },
                new[] { "InteractableCashierCounter", "OnPressSpaceBar" },
                new[] { "PlayTableGame", "EvaluateEndGameGift" },
                new[] { "RestockItemPanelUI", "OnPressPurchaseButton" },
                new[] { "RestockItemScreen", "EvaluateCartCheckout" },
                new[] { "ScannerRestockScreen", "OnPressUnlockButton" },
                new[] { "ScannerRestockScreen", "EvaluateCartCheckout" },
                new[] { "ShopBuyDecoUIScreen", "OnPressBuyShopDeco" },
                new[] { "ShopBuyDecoUIScreen", "OnPressBuyShopDecoItem" },
                new[] { "WorkerInteractUIScreen", "OnPressGiveBonus" },
            };

            private static IEnumerable<MethodBase> TargetMethods()
            {
                for (var i = 0; i < Methods.Length; i++)
                {
                    var type = AccessTools.TypeByName(Methods[i][0]);
                    var method = type == null ? null : AccessTools.Method(type, Methods[i][1]);
                    if (method != null)
                    {
                        yield return method;
                    }
                }
            }

            [HarmonyPostfix]
            private static void Postfix(MethodBase __originalMethod)
            {
                if (_active == null)
                    return;
                if (string.Equals(__originalMethod?.Name, "CloseScreen", StringComparison.Ordinal))
                    _active.PublishClose();
                else
                    _active.PublishCounterAndMoney();
            }
        }

        /// <summary>
        /// Gates the host's own Next Day press on every player being ready. While players are
        /// still pending the vanilla advance is suppressed and the wait state is broadcast;
        /// <see cref="AdvanceNextDay"/> re-enters the vanilla method with the gate bypassed.
        /// </summary>
        [HarmonyPatch(typeof(EndOfDayReportScreen), "OnPressGoNextDay")]
        private static class NextDayGatePatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                var host = _active;
                if (host == null || host._shutdown || host._advancingNextDay)
                {
                    return true;
                }

                return host.HostPressNextDay();
            }
        }

        /// <summary>
        /// With the recap already open, the Enter/GoNextDay key belongs to the recap's own Next Day
        /// action. The vanilla <c>ShowGoNextDayScreen</c> would call <c>OpenScreen</c>, which closes
        /// an already-open recap, so suppress it while the recap is up. With the recap closed this
        /// still runs vanilla, which is the first Enter that opens the menu.
        /// </summary>
        [HarmonyPatch(typeof(InteractionPlayerController), "ShowGoNextDayScreen")]
        private static class ShowGoNextDayPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
                => _active == null || _active._shutdown || !EndOfDayReportScreen.IsActive();
        }

        /// <summary>
        /// DelayGoNextDay is an iterator. A normal postfix runs when the iterator is created,
        /// before the wait and before the host event fee is applied. Observe the completed step
        /// and relay the money section only after the coroutine actually changed it.
        /// </summary>
        [HarmonyPatch(typeof(EndOfDayReportScreen), "DelayGoNextDay")]
        private static class NextDayCoroutinePatch
        {
            [HarmonyPostfix]
            private static void Postfix(ref IEnumerator __result)
            {
                if (__result != null)
                {
                    __result = Relay(__result);
                }
            }

            private static IEnumerator Relay(IEnumerator inner)
            {
                while (true)
                {
                    var beforeSupplyCost = CPlayerData.m_GameReportDataCollect.supplyCost;
                    if (!inner.MoveNext())
                    {
                        _active?.OnNextDayCoroutineCompleted();
                        yield break;
                    }

                    if (CPlayerData.m_GameReportDataCollect.supplyCost != beforeSupplyCost)
                    {
                        _active?.PublishDelta(BuildState(CPlayerData.m_GameReportDataCollect,
                            MoneySlice, false), MoneySlice);
                    }

                    yield return inner.Current;
                }
            }
        }

        /// <summary>
        /// Both supported game builds account for a paid tournament customer in the body of
        /// Customer.Update. The callback is injected immediately after the current report's
        /// totalPlayTableEarning store, which is after customerPlayed and the fee mutation. This
        /// is an exact mutation boundary, not a per-frame comparison of report totals.
        /// </summary>
        [HarmonyPatch(typeof(Customer), "Update")]
        private static class TournamentEntryMutationPatch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions)
            {
                var field = AccessTools.Field(typeof(GameReportDataCollect), "totalPlayTableEarning");
                var callback = AccessTools.Method(typeof(ReportHostBehaviour),
                    nameof(TournamentEntryReportMutation));
                var injected = false;
                foreach (var instruction in instructions)
                {
                    yield return instruction;
                    if (!injected && instruction.opcode == OpCodes.Stfld
                        && IsTargetField(instruction.operand, field))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, callback);
                        injected = true;
                    }
                }

                if (!injected)
                {
                    CoopPlugin.Log.LogError("Report host could not find Customer.Update tournament "
                        + "fee mutation boundary on this game build.");
                }
            }
        }

        private static bool IsTargetField(object operand, FieldInfo target)
        {
            var field = operand as FieldInfo;
            return field != null && target != null && field.DeclaringType == target.DeclaringType
                && string.Equals(field.Name, target.Name, StringComparison.Ordinal);
        }

        private static void TournamentEntryReportMutation(Customer customer)
        {
            if (!IsHostActive() || customer == null || customer.GetCustomerTournamentData() == null
                || !customer.GetCustomerTournamentData().m_HasRegisteredTournamentStart
                || CPlayerData.m_TournamentData == null
                || CPlayerData.m_TournamentData.m_TournamentFee <= 0f)
            {
                return;
            }

            _active.PublishCounterAndMoney();
        }
    }
}
