using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Host validation and keyed register operation publication.</summary>
    [ServerBehaviour]
    public sealed class RegisterHostBehaviour : CoopBehaviour
    {
        private static RegisterHostBehaviour _active;

        private sealed class Station
        {
            public InteractableCashierCounter Counter;
            public uint CounterGeneration;
            public int Owner;
            public Customer Customer;
            public uint CustomerGeneration;
        }

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private Guid _intentPredictionId;
        private readonly Dictionary<int, Station> _stations = new();

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            _harmony = new Harmony("com.zwhit.cardshopcoop.register.host");
            Patch(typeof(ManningPatch));
            Patch(typeof(ExitPatch));
            Patch(typeof(WorkerPatch));
            Patch(typeof(CustomerPatch));
            Patch(typeof(StatePatch));
            Patch(typeof(PaymentPatch));
            Patch(typeof(ChangePhasePatch));
            Patch(typeof(ScanItemPatch));
            Patch(typeof(ScanCardPatch));
            Patch(typeof(AddChangePatch));
            Patch(typeof(RemoveChangePatch));
            Patch(typeof(FinishPatch));
            Patch(typeof(CounterAddedPatch));
            Patch(typeof(CounterRemovedPatch));
            Patch(typeof(CounterDestroyedPatch));
            Patch(typeof(ShelfManagerStartPatch));
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Patch(Type patchType)
            => _harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (connection != null && IsJoinPhase(connection.State) && _context.InGame()
                && RegisterInterop.Counters != null)
            {
                _context.Send(connection.Id, BuildBaseline());
            }
        }

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (!_shutdown)
            {
                _stations.Clear();
            }
        }

        [MessageHandler(typeof(RegisterIntentMessage))]
        private void HandleIntent(MessageContext context, RegisterIntentMessage message)
        {
            var connection = context?.Connection;
            if (_shutdown || connection == null || message == null || !IsJoinPhase(connection.State)
                || !_context.InGame() || message.Counter >= 250
                || !Enum.IsDefined(typeof(RegisterIntentKind), message.Kind))
            {
                return;
            }

            _intentPredictionId = message.PredictionId;
            try
            {
                var index = message.Counter;
                var counter = RegisterInterop.Counter(index);
                var station = counter == null ? null : Observe(index, counter);
                if (counter == null)
                {
                    Reject(connection.Id, message.PredictionId);
                    return;
                }

                if (message.Kind == RegisterIntentKind.Claim)
                {
                    if (station.Owner != 0 && station.Owner != connection.Id)
                    {
                        Reject(connection.Id, message.PredictionId);
                        return;
                    }

                    if (counter.IsMannedByPlayer() && station.Owner != connection.Id)
                    {
                        Reject(connection.Id, message.PredictionId);
                        return;
                    }

                    counter.StopCurrentWorker();
                    station.Owner = connection.Id;
                    PublishOwnership(index);
                    return;
                }

                if (message.Kind == RegisterIntentKind.Release)
                {
                    if (station.Owner != connection.Id)
                    {
                        Reject(connection.Id, message.PredictionId);
                        return;
                    }

                    station.Owner = 0;
                    PublishOwnership(index);
                    return;
                }

                if (station.Owner != connection.Id || !Apply(counter, message))
                {
                    Reject(connection.Id, message.PredictionId);
                    return;
                }

                if (message.Kind == RegisterIntentKind.TakePayment)
                {
                    PublishPhase(index, true);
                }
                else if (message.Kind == RegisterIntentKind.CardPayment
                    || message.Kind == RegisterIntentKind.Complete)
                {
                    PublishCustomerLifecycle(index, true);
                }
            }
            finally
            {
                _intentPredictionId = Guid.Empty;
            }
        }

        private bool Apply(InteractableCashierCounter counter, RegisterIntentMessage message)
        {
            var customer = counter.m_CurrentCustomer;
            if (customer == null || !customer.m_IsActive)
            {
                return false;
            }

            switch (message.Kind)
            {
                case RegisterIntentKind.ScanItem:
                    if (counter.m_CashierCounterState != ECashierCounterState.ScanningItem)
                    {
                        return false;
                    }

                    var items = customer.GetItemInBagList();
                    if (message.Slot < 0 || message.Slot >= items.Count || items[message.Slot] == null
                        || items[message.Slot].m_InteractableScanItem == null
                        || !items[message.Slot].m_InteractableScanItem.IsNotScanned())
                    {
                        return false;
                    }

                    items[message.Slot].m_InteractableScanItem.OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.ScanCard:
                    if (counter.m_CashierCounterState != ECashierCounterState.ScanningItem)
                    {
                        return false;
                    }

                    var cards = customer.GetCardInBagList();
                    if (message.Slot < 0 || message.Slot >= cards.Count || cards[message.Slot] == null
                        || !cards[message.Slot].IsNotScanned())
                    {
                        return false;
                    }

                    cards[message.Slot].OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.TakePayment:
                    if (counter.m_CashierCounterState != ECashierCounterState.TakingCash
                        || customer.m_CustomerCash == null
                        || customer.m_CustomerCash.m_IsCard != message.IsCard)
                    {
                        return false;
                    }

                    customer.m_CustomerCash.OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.CardPayment:
                    if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                        || !RegisterInterop.IsUsingCard(counter) || !RegisterInterop.IsFinite(message.Value)
                        || Math.Abs(message.Value - RegisterInterop.Total(counter)) > 0.011d)
                    {
                        return false;
                    }

                    counter.EvaluateCreditCard(message.Value);
                    return true;

                case RegisterIntentKind.Change:
                    if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                        || RegisterInterop.IsUsingCard(counter) || !RegisterInterop.IsFinite(message.Value))
                    {
                        return false;
                    }

                    var change = RegisterInterop.FindChange(counter, message.Slot, message.IsCoin,
                        message.Value);
                    if (change == null)
                    {
                        return false;
                    }

                    if (message.TakeBack)
                    {
                        change.OnRightMouseButtonUp();
                    }
                    else
                    {
                        change.OnMouseButtonUp();
                    }

                    return true;

                case RegisterIntentKind.Complete:
                    if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                        || !RegisterInterop.IsChangeReady(counter))
                    {
                        return false;
                    }

                    counter.OnPressSpaceBar();
                    return true;

                default:
                    return false;
            }

        }

        private Station Observe(int index, InteractableCashierCounter counter)
        {
            if (!_stations.TryGetValue(index, out var station))
            {
                station = new Station();
                _stations.Add(index, station);
            }

            if (!ReferenceEquals(station.Counter, counter))
            {
                station.Counter = counter;
                station.CounterGeneration++;
                if (station.CounterGeneration == 0)
                {
                    station.CounterGeneration = 1;
                }

                station.Owner = 0;
                station.Customer = null;
            }

            var customer = counter.m_CurrentCustomer;
            if (!ReferenceEquals(station.Customer, customer))
            {
                station.Customer = customer;
                if (customer != null)
                {
                    var generation = NpcHostBehaviour.GetCustomerGeneration(customer);
                    station.CustomerGeneration = generation > 0
                        ? (uint)generation : NextCustomerGeneration(station.CustomerGeneration);
                }
            }

            if (station.Owner == -1 && !counter.IsMannedByPlayer())
            {
                station.Owner = 0;
            }
            else if (station.Owner == 0 && counter.IsMannedByPlayer())
            {
                station.Owner = -1;
            }

            return station;
        }

        private static uint NextCustomerGeneration(uint generation)
        {
            generation++;
            return generation == 0 ? 1u : generation;
        }

        private RegisterBaselineMessage BuildBaseline()
        {
            var baseline = new RegisterBaselineMessage();
            var counters = RegisterInterop.Counters;
            for (var i = 0; counters != null && i < counters.Count && i < 250; i++)
            {
                baseline.Counters.Add(BuildBaselineCounter(i, counters[i]));
            }

            return baseline;
        }

        private RegisterBaselineCounter BuildBaselineCounter(int index,
            InteractableCashierCounter counter)
        {
            var station = counter == null ? GetStation(index) : Observe(index, counter);
            var result = new RegisterBaselineCounter
            {
                Counter = (byte)index,
                CounterGeneration = station.CounterGeneration,
                Exists = counter != null,
                Owner = station.Owner,
                CustomerIndex = -1,
            };
            if (counter == null)
            {
                return result;
            }

            FillCommon(result, counter, station.Customer);
            return result;
        }

        private void FillCommon(RegisterBaselineCounter result, InteractableCashierCounter counter,
            Customer customer)
        {
            result.HasCustomer = customer != null;
            result.State = (byte)counter.m_CashierCounterState;
            result.UsingCard = RegisterInterop.IsUsingCard(counter);
            result.Paid = RegisterInterop.Paid(counter);
            result.Total = RegisterInterop.Total(counter);
            result.Change = RegisterInterop.Change(counter);
            result.ChangeReady = RegisterInterop.IsChangeReady(counter);
            result.ChangeStarted = RegisterInterop.ChangeStarted(counter);
            result.TooMuchChange = RegisterInterop.TooMuchChange(counter);
            if (customer == null)
            {
                return;
            }

            var station = Observe(RegisterInterop.Index(counter), counter);
            result.CustomerIndex = RegisterInterop.CustomerIndex(customer);
            result.CustomerGeneration = (int)station.CustomerGeneration;
            result.CustomerFemale = customer.m_IsFemale;
            result.CharacterName = customer.m_CharacterCustom?.CharacterName;
            result.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(customer,
                "m_TotalScannedItemCost") ?? 0f);
            AddLines(result.Lines, customer);
            AddChanges(result.ChangeItems, counter);
        }

        private static void AddLines(List<RegisterLine> lines, Customer customer)
        {
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                lines.Add(new RegisterLine
                {
                    ItemType = item == null ? EItemType.None : item.GetItemType(),
                    Price = item == null ? 0f : item.GetCurrentPrice(),
                    Scanned = item != null && item.m_InteractableScanItem != null
                        && !item.m_InteractableScanItem.IsNotScanned(),
                });
            }

            var cards = customer.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                lines.Add(new RegisterLine
                {
                    IsCard = true,
                    Card = card?.m_Card3dUI?.m_CardUI?.GetCardData(),
                    Price = card == null ? 0f : card.GetCurrentPrice(),
                    Scanned = card != null && !card.IsNotScanned(),
                });
            }
        }

        private static void AddChanges(List<RegisterChange> changes,
            InteractableCashierCounter counter)
        {
            var money = counter.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                var change = money[i];
                var count = change == null ? 0 : RegisterInterop.GivenAmount(change);
                if (change != null && count > 0)
                {
                    changes.Add(new RegisterChange
                    {
                        Slot = change.m_Index,
                        IsCoin = change.m_IsCoin,
                        Value = change.m_ValueDouble,
                        Count = count,
                    });
                }
            }
        }

        private RegisterDeltaMessage NewDelta(int index, RegisterDeltaKind kind)
        {
            var counter = RegisterInterop.Counter(index);
            var station = counter == null ? GetStation(index) : Observe(index, counter);
            return new RegisterDeltaMessage
            {
                Kind = kind,
                Counter = (byte)index,
                CounterGeneration = station.CounterGeneration,
                PredictionId = _intentPredictionId,
            };
        }

        private void PublishOwnership(int index)
        {
            var counter = RegisterInterop.Counter(index);
            var delta = NewDelta(index, RegisterDeltaKind.Ownership);
            delta.Owner = counter == null ? 0 : Observe(index, counter).Owner;
            Broadcast(delta);
        }

        private void PublishCustomerLifecycle(int index, bool force = false)
        {
            if (index < 0 || index >= 250)
            {
                return;
            }

            if (!force && _intentPredictionId != Guid.Empty)
            {
                return;
            }

            var counter = RegisterInterop.Counter(index);
            var delta = NewDelta(index, RegisterDeltaKind.CustomerLifecycle);
            var station = counter == null ? GetStation(index) : Observe(index, counter);
            var customer = counter?.m_CurrentCustomer;
            delta.HasCustomer = customer != null;
            if (customer != null)
            {
                delta.CustomerIndex = RegisterInterop.CustomerIndex(customer);
                delta.CustomerGeneration = (int)station.CustomerGeneration;
                delta.CustomerFemale = customer.m_IsFemale;
                delta.CharacterName = customer.m_CharacterCustom?.CharacterName;
                delta.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(customer,
                    "m_TotalScannedItemCost") ?? 0f);
                AddLines(delta.Lines, customer);
            }

            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private void PublishScan(int index, bool isCard, int slot)
        {
            var counter = RegisterInterop.Counter(index);
            if (counter == null || counter.m_CurrentCustomer == null || slot < 0)
            {
                return;
            }

            var delta = NewDelta(index, RegisterDeltaKind.Scan);
            delta.HasCustomer = true;
            delta.CustomerIndex = RegisterInterop.CustomerIndex(counter.m_CurrentCustomer);
            delta.CustomerGeneration = (int)Observe(index, counter).CustomerGeneration;
            delta.IsCard = isCard;
            delta.Slot = slot;
            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private void PublishPhase(int index, bool force = false)
        {
            if (index < 0 || index >= 250)
            {
                return;
            }

            if (!force && _intentPredictionId != Guid.Empty)
            {
                return;
            }

            var counter = RegisterInterop.Counter(index);
            if (counter == null)
            {
                return;
            }

            var delta = NewDelta(index, RegisterDeltaKind.PhasePayment);
            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private static void FillPhase(RegisterDeltaMessage delta, InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            delta.State = (byte)counter.m_CashierCounterState;
            delta.UsingCard = RegisterInterop.IsUsingCard(counter);
            delta.Paid = RegisterInterop.Paid(counter);
            delta.Total = RegisterInterop.Total(counter);
            delta.Change = RegisterInterop.Change(counter);
            delta.ChangeReady = RegisterInterop.IsChangeReady(counter);
            delta.ChangeStarted = RegisterInterop.ChangeStarted(counter);
            delta.TooMuchChange = RegisterInterop.TooMuchChange(counter);
            if (counter.m_CurrentCustomer != null)
            {
                delta.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(counter.m_CurrentCustomer,
                    "m_TotalScannedItemCost") ?? 0f);
            }
        }

        private void PublishChange(InteractableCounterMoneyChange change, bool takeBack)
        {
            var counter = change?.m_CashierCounter;
            var index = RegisterInterop.Index(counter);
            if (index < 0 || change == null)
            {
                return;
            }

            var delta = NewDelta(index, RegisterDeltaKind.Change);
            delta.Slot = change.m_Index;
            delta.IsCoin = change.m_IsCoin;
            delta.Value = change.m_ValueDouble;
            // ChangeCount and the phase fields are absolute results. The client must not
            // replay the click direction after deferred compaction.
            delta.ChangeCount = RegisterInterop.GivenAmount(change);
            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private void PublishCounterLifecycle(InteractableCashierCounter counter)
        {
            var index = RegisterInterop.Index(counter);
            if (index < 0 || index >= 250 || counter == null)
            {
                return;
            }

            Observe(index, counter);
            var delta = NewDelta(index, RegisterDeltaKind.CounterLifecycle);
            delta.Exists = true;
            Broadcast(delta);
        }

        private void Tombstone(int index)
        {
            if (index < 0 || index >= 250)
            {
                return;
            }

            var station = GetStation(index);
            if (station.Counter == null)
            {
                return;
            }

            station.Counter = null;
            station.Owner = 0;
            station.Customer = null;
            var delta = new RegisterDeltaMessage
            {
                Kind = RegisterDeltaKind.CounterLifecycle,
                Counter = (byte)index,
                CounterGeneration = station.CounterGeneration,
                Exists = false,
                PredictionId = _intentPredictionId,
            };
            Broadcast(delta);
        }

        private Station GetStation(int index)
        {
            if (!_stations.TryGetValue(index, out var station))
            {
                station = new Station { CounterGeneration = 1 };
                _stations.Add(index, station);
            }

            return station;
        }

        private void Broadcast(RegisterDeltaMessage delta)
        {
            if (!_shutdown && _context.InGame())
            {
                _context.Broadcast(delta);
            }
        }

        private void Reject(int connectionId, Guid predictionId)
        {
            if (predictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, connectionId, predictionId);
            }
        }

        private static void CreditGuestCheckout()
        {
            CPlayerData.m_GameReportDataCollect.manualCheckoutCount++;
            CPlayerData.m_GameReportDataCollectPermanent.manualCheckoutCount++;
            AchievementManager.OnCustomerFinishCheckout(
                CPlayerData.m_GameReportDataCollectPermanent.manualCheckoutCount);
            TutorialManager.AddTaskValue(ETutorialTaskCondition.CheckoutCustomer, 1f);
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null)
            {
                return;
            }

            foreach (var pair in _stations)
            {
                if (pair.Value.Owner == connection.Id)
                {
                    pair.Value.Owner = 0;
                    PublishOwnership(pair.Key);
                }
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
            _stations.Clear();
        }

        private void OnDestroy() => Shutdown();

        private static int Index(InteractableCashierCounter counter)
            => RegisterInterop.Index(counter);

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnMouseButtonUp")]
        private static class ManningPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
                => _active == null || !IsGuestOwned(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                if (_active == null || __instance == null || !_active._context.InGame()
                    || !__instance.IsMannedByPlayer())
                {
                    return;
                }

                var index = Index(__instance);
                if (index >= 0 && index < 250)
                {
                    _active.Observe(index, __instance).Owner = -1;
                    _active.PublishOwnership(index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressEsc")]
        private static class ExitPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                var index = Index(__instance);
                if (_active != null && index >= 0 && _active.GetStation(index).Owner == -1
                    && !__instance.IsMannedByPlayer())
                {
                    _active.GetStation(index).Owner = 0;
                    _active.PublishOwnership(index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "NPCStartManCounter")]
        private static class WorkerPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
                => _active == null || !IsGuestOwned(__instance);
        }

        private static bool IsGuestOwned(InteractableCashierCounter counter)
        {
            var index = Index(counter);
            return _active != null && index >= 0 && _active.GetStation(index).Owner > 0;
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCurrentCustomer")]
        private static class CustomerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishCustomerLifecycle(Index(__instance));
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCashierCounterState")]
        private static class StatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Index(__instance));
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCustomerPaidAmount")]
        private static class PaymentPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Index(__instance));
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "StartGivingChange")]
        private static class ChangePhasePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Index(__instance));
        }

        [HarmonyPatch(typeof(InteractableScanItem), "OnMouseButtonUp")]
        private static class ScanItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableScanItem __instance)
            {
                var counter = RegisterInterop.FindCounterForScanItem(__instance);
                var index = Index(counter);
                if (_active != null && index >= 0 && counter?.m_CurrentCustomer != null)
                {
                    var items = counter.m_CurrentCustomer.GetItemInBagList();
                    for (var i = 0; items != null && i < items.Count; i++)
                    {
                        if (items[i]?.m_InteractableScanItem == __instance)
                        {
                            _active.PublishScan(index, false, i);
                            return;
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCard3d), "OnMouseButtonUp")]
        private static class ScanCardPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCard3d __instance)
            {
                var counter = RegisterInterop.FindCounterForCard(__instance);
                var index = Index(counter);
                if (_active != null && index >= 0 && counter?.m_CurrentCustomer != null)
                {
                    var cards = counter.m_CurrentCustomer.GetCardInBagList();
                    for (var i = 0; cards != null && i < cards.Count; i++)
                    {
                        if (cards[i] == __instance)
                        {
                            _active.PublishScan(index, true, i);
                            return;
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnMouseButtonUp")]
        private static class AddChangePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance)
                => _active?.PublishChange(__instance, false);
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp")]
        private static class RemoveChangePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance)
                => _active?.PublishChange(__instance, true);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressSpaceBar")]
        private static class FinishPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Index(__instance));
        }

        [HarmonyPatch(typeof(ShelfManager), "InitCashierCounter")]
        private static class CounterAddedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishCounterLifecycle(__instance);
        }

        [HarmonyPatch(typeof(ShelfManager), "Start")]
        private static class ShelfManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                var counters = RegisterInterop.Counters;
                for (var i = 0; counters != null && i < counters.Count; i++)
                {
                    _active?.PublishCounterLifecycle(counters[i]);
                }
            }
        }

        [HarmonyPatch(typeof(ShelfManager), "RemoveCashierCounter")]
        private static class CounterRemovedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __0)
                => _active?.Tombstone(RegisterInterop.Index(__0));
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnDestroyed")]
        private static class CounterDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance)
                => _active?.Tombstone(RegisterInterop.Index(__instance));
        }
    }
}
