using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Trade
{
    /// <summary>Host-owned customer trade state and resolution. The host is the only process that
    /// calls CustomerTradeCardScreen's economy-changing methods.</summary>
    [ServerBehaviour]
    public sealed class TradeHostBehaviour : CoopBehaviour
    {
        private const float Reach = 4f;

        private sealed class Offer
        {
            public byte Counter;
            public ushort CustomerIndex;
            public int CustomerGeneration;
            public Customer Customer;
            public CustomerTradeData Data;
            public uint Nonce;
            public int Owner;
        }

        private static TradeHostBehaviour _active;
        private readonly Dictionary<byte, Offer> _offers = new();
        private readonly Dictionary<Customer, Offer> _offersByCustomer = new();
        private readonly Dictionary<Customer, int> _customerIndices = new();
        private readonly Dictionary<int, PeerConnection> _joinedConnections = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private uint _nextNonce;
        private bool _shutdown;
        private bool _customerManagerReady;
        private bool _tradeScreenReady;
        private int _observedDay;
        private bool _hasObservedDay;

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            _harmony = new Harmony("com.zwhit.cardshopcoop.trade.host");
            _harmony.CreateClassProcessor(typeof(CustomerPressPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerStatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerStopPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ThinkPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerManagerStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerPopulationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TradeScreenStartPatch)).Patch();
            SceneManager.sceneLoaded += OnSceneLoaded;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            RegisterAllCustomers(TradeInterop.Customers);
            SignalCustomerManagerReady();
            SignalTradeScreenReady();
        }

        private void Update()
        {
            if (!_context.InGame())
            {
                if (_offers.Count != 0)
                {
                    CleanupOffers();
                }

                _hasObservedDay = false;
                return;
            }

            if (!_hasObservedDay)
            {
                _observedDay = CPlayerData.m_CurrentDay;
                _hasObservedDay = true;
            }
            else if (_observedDay != CPlayerData.m_CurrentDay)
            {
                _observedDay = CPlayerData.m_CurrentDay;
                CleanupOffers();
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CleanupOffers();
            _joinedConnections.Clear();
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }

        private void OnDestroy() => Shutdown();

        private void RegisterAllCustomers(IList<Customer> customers)
        {
            for (var i = 0; customers != null && i < customers.Count; i++)
            {
                var customer = customers[i];
                if (customer != null)
                {
                    _customerIndices[customer] = i;
                    ReconcileCustomer(customer);
                }
            }
        }

        [OnFullyJoined]
        private void SendJoinState(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _joinedConnections[connection.Id] = connection;
            SendBaselineWhenReady(connection.Id);
        }

        [OnClientDisconnected]
        private void ReleaseConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection == null)
            {
                return;
            }

            _joinedConnections.Remove(connection.Id);
            foreach (var offer in _offers.Values)
            {
                if (offer.Owner == connection.Id)
                {
                    offer.Owner = 0;
                }
            }
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        private void SendBaselineWhenReady(int connectionId)
        {
            if (!_context.InGame() || !_customerManagerReady || !_tradeScreenReady
                || !_joinedConnections.TryGetValue(connectionId, out var connection)
                || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _context.Send(connectionId, BuildBaseline());
        }

        private void SendBaselinesToJoined()
        {
            if (!_context.InGame() || !_customerManagerReady || !_tradeScreenReady)
            {
                return;
            }

            foreach (var pair in _joinedConnections)
            {
                if (pair.Value != null && IsJoinPhase(pair.Value.State))
                {
                    _context.Send(pair.Key, BuildBaseline());
                }
            }
        }

        [MessageHandler(typeof(TradeIntentMessage))]
        private void HandleIntent(MessageContext messageContext, TradeIntentMessage message)
        {
            if (!IsValidSender(messageContext, message))
            {
                return;
            }

            if (!_offers.TryGetValue(message.Counter, out var offer)
                || !Matches(offer, message))
            {
                PredictionApi.Rollback(_context, messageContext.Connection.Id, message.PredictionId);
                return;
            }

            var connectionId = messageContext.Connection.Id;
            if (message.Operation == TradeIntentOperation.Open)
            {
                Open(connectionId, message, offer);
                return;
            }

            if (message.Operation == TradeIntentOperation.Close)
            {
                if (offer.Owner == connectionId)
                {
                    offer.Owner = 0;
                }

                return;
            }

            if (offer.Owner != connectionId || !ValidateLiveAction(connectionId, offer))
            {
                Reject(connectionId, message);
                return;
            }

            if (message.Operation == TradeIntentOperation.Accept)
            {
                ResolveAccept(connectionId, message, offer);
            }
            else if (message.Operation == TradeIntentOperation.Decline)
            {
                ResolveDecline(connectionId, message, offer);
            }
            else if (message.Operation == TradeIntentOperation.Think)
            {
                ResolveThink(connectionId, message, offer);
            }
            else
            {
                Reject(connectionId, message);
            }
        }

        private void Open(int connectionId, TradeIntentMessage message, Offer offer)
        {
            var accepted = offer.Owner == 0 && !HostIsBusy()
                && ValidateLiveAction(connectionId, offer);
            if (accepted)
            {
                offer.Owner = connectionId;
            }

            if (!accepted)
            {
                Reject(connectionId, message);
                return;
            }

            _context.Send(connectionId, new TradeSessionMessage
            {
                PredictionId = message.PredictionId,
                Counter = offer.Counter,
                CustomerIndex = offer.CustomerIndex,
                CustomerGeneration = offer.CustomerGeneration,
                OfferNonce = offer.Nonce,
                Accepted = true,
            });
        }

        private void ResolveAccept(int connectionId, TradeIntentMessage message, Offer offer)
        {
            var data = offer.Data;
            if (!TryValidateBid(data, message.Price, out var bid))
            {
                Reject(connectionId, message);
                return;
            }

            var screen = TradeInterop.Screen;
            if (screen == null)
            {
                throw new InvalidOperationException("Trade accept has no CustomerTradeCardScreen.");
            }

            if (data.m_IsTrading && !TradeInterop.HasCard(data.m_CardData_R))
            {
                Reject(connectionId, message);
                return;
            }

            if (!data.m_IsTrading && CPlayerData.m_CoinAmountDouble < bid)
            {
                Reject(connectionId, message);
                return;
            }

            var previousMaxDecline = data.m_MaxDeclineCount;
            TradeInterop.OpenData(offer.Customer, data);
            if (!data.m_IsTrading)
            {
                TradeInterop.SetPrice(screen, bid);
            }

            bool accepted;
            CustomerTradeData updated;
            try
            {
                screen.OnPressAccept();
                accepted = TradeInterop.HasAccepted(screen);
                updated = TradeInterop.Capture(screen);
            }
            finally
            {
                TradeInterop.SetManagerTrading(false);
            }

            if (accepted)
            {
                RemoveOffer(offer);
                SendOfferDelta(offer, removed: true, message.PredictionId, TradeOutcome.Accepted,
                    connectionId);
                FinishCustomerReservation(offer);
                return;
            }

            TradeInterop.SetStoredData(offer.Customer, updated);
            if (!data.m_IsTrading && previousMaxDecline <= 0)
            {
                RemoveOffer(offer);
                SendOfferDelta(offer, removed: true, message.PredictionId,
                    TradeOutcome.WalkedAway, connectionId);
                FinishCustomerReservation(offer);
                return;
            }

            offer.Data = updated;
            var outcome = data.m_IsTrading || updated.m_SellCardAskPrice == data.m_SellCardAskPrice
                ? TradeOutcome.Refused : TradeOutcome.Haggle;
            SendOfferDelta(offer, removed: false, message.PredictionId, outcome, connectionId);
        }

        private void ResolveDecline(int connectionId, TradeIntentMessage message, Offer offer)
        {
            RemoveOffer(offer);
            SendOfferDelta(offer, removed: true, message.PredictionId, TradeOutcome.Declined,
                connectionId);
            FinishCustomerReservation(offer);
        }

        private void ResolveThink(int connectionId, TradeIntentMessage message, Offer offer)
        {
            if (!TryValidateBid(offer.Data, message.Price, out var bid))
            {
                Reject(connectionId, message);
                return;
            }

            var screen = TradeInterop.Screen;
            if (screen == null)
            {
                throw new InvalidOperationException("Trade think has no CustomerTradeCardScreen.");
            }

            TradeInterop.OpenData(offer.Customer, offer.Data);
            if (!offer.Data.m_IsTrading)
            {
                TradeInterop.SetPrice(screen, bid);
            }

            try
            {
                screen.OnPressLetMeThink();
            }
            finally
            {
                TradeInterop.SetManagerTrading(false);
            }

            var stored = TradeInterop.StoredData(offer.Customer);
            if (stored == null)
            {
                throw new InvalidOperationException("Trade think did not persist customer data.");
            }

            offer.Data = TradeInterop.CopyData(stored);
            SendOfferDelta(offer, removed: false, message.PredictionId, TradeOutcome.Thinking,
                connectionId);
            offer.Owner = 0;
        }

        private bool ValidateLiveAction(int connectionId, Offer offer)
        {
            if (HostIsBusy() || !TradeInterop.IsWaitingForTrade(offer.Customer)
                || !TradeInterop.SameCustomer(offer.Customer, offer.CustomerIndex,
                    offer.CustomerGeneration)
                || TradeInterop.CounterIndex(offer.Customer) != offer.Counter)
            {
                return false;
            }

            if (!_context.PeerPresence.TryGet(connectionId, out var presence)
                || presence.Age > TimeSpan.FromSeconds(2)
                || !TradeInterop.IsReachable(presence.Position, offer.Customer.transform.position, Reach))
            {
                return false;
            }

            return true;
        }

        private bool HostIsBusy()
        {
            var manager = TradeInterop.Manager;
            var screen = TradeInterop.Screen;
            return manager != null && (manager.m_IsPlayerTrading || TradeInterop.IsScreenOpen(screen));
        }

        private static bool IsValidSender(MessageContext context, TradeIntentMessage message)
        {
            return context?.Connection != null
                && context.Connection.State == ConnectionState.FullyJoined
                && message != null && message.Counter < 250
                && message.CustomerIndex != ushort.MaxValue
                && message.PredictionId != Guid.Empty;
        }

        private static bool Matches(Offer offer, TradeIntentMessage message)
        {
            return offer.CustomerIndex == message.CustomerIndex
                && offer.CustomerGeneration == message.CustomerGeneration
                && offer.Nonce == message.OfferNonce;
        }

        private void ReconcileCustomer(Customer customer)
        {
            if (customer == null)
            {
                return;
            }

            var customerIndex = _customerIndices.TryGetValue(customer, out var knownIndex)
                ? knownIndex : TradeInterop.CustomerListIndex(customer);
            if (customerIndex >= 0)
            {
                _customerIndices[customer] = customerIndex;
            }

            _offersByCustomer.TryGetValue(customer, out var customerOffer);
            if (!TradeInterop.IsWaitingForTrade(customer))
            {
                if (customerOffer != null)
                {
                    ExpireOffer(customerOffer);
                }

                return;
            }

            var counter = TradeInterop.CounterIndex(customer);
            var generation = TradeInterop.CustomerGeneration(customer);
            if (customerIndex < 0 || customerIndex > ushort.MaxValue || counter < 0
                || counter >= 250 || generation <= 0)
            {
                CoopPlugin.Log.LogWarning("Trade host: waiting customer has no stable trade identity");
                return;
            }

            if (_offers.TryGetValue((byte)counter, out var counterOffer)
                && !ReferenceEquals(counterOffer.Customer, customer))
            {
                ExpireOffer(counterOffer);
            }

            if (customerOffer != null && (customerOffer.Counter != counter
                || customerOffer.CustomerIndex != customerIndex
                || customerOffer.CustomerGeneration != generation))
            {
                ExpireOffer(customerOffer);
                customerOffer = null;
            }

            if (customerOffer == null)
            {
                TryCreateOffer(customer, (ushort)customerIndex, (byte)counter);
            }
            else
            {
                RefreshStoredData(customerOffer);
            }
        }

        private void TryCreateOffer(Customer customer, ushort customerIndex, byte counter)
        {
            if (HostIsBusy() || TradeInterop.Screen == null)
            {
                return;
            }

            CustomerTradeData data;
            try
            {
                TradeInterop.Screen.SetCustomer(customer, null);
                data = TradeInterop.Capture(TradeInterop.Screen);
            }
            finally
            {
                TradeInterop.SetManagerTrading(false);
            }

            if (data.m_CardData_L == null || (data.m_IsTrading && data.m_CardData_R == null))
            {
                return;
            }

            var generation = TradeInterop.CustomerGeneration(customer);
            if (generation <= 0)
            {
                return;
            }

            TradeInterop.SetStoredData(customer, data);
            _nextNonce++;
            if (_nextNonce == 0)
            {
                _nextNonce = 1;
            }

            _offers[counter] = new Offer
            {
                Counter = counter,
                CustomerIndex = customerIndex,
                CustomerGeneration = generation,
                Customer = customer,
                Data = data,
                Nonce = _nextNonce,
            };
            _offersByCustomer[customer] = _offers[counter];
            SendOfferDelta(_offers[counter], removed: false, Guid.Empty, TradeOutcome.None, 0);
        }

        private void RefreshStoredData(Offer offer)
        {
            var stored = TradeInterop.StoredData(offer.Customer);
            if (stored != null && !SameData(offer.Data, stored))
            {
                offer.Data = TradeInterop.CopyData(stored);
                SendOfferDelta(offer, removed: false, Guid.Empty, TradeOutcome.None, 0);
            }
        }

        private void CleanupOffers()
        {
            var offers = new List<Offer>(_offers.Values);
            _offers.Clear();
            _offersByCustomer.Clear();
            for (var i = 0; i < offers.Count; i++)
            {
                var offer = offers[i];
                SendOfferDelta(offer, removed: true, Guid.Empty, TradeOutcome.Expired, 0);
                offer.Owner = 0;
                FinishCustomerReservation(offer);
            }
        }

        private void ExpireOffer(Offer offer)
        {
            if (offer == null)
            {
                return;
            }

            RemoveOffer(offer);
            SendOfferDelta(offer, removed: true, Guid.Empty, TradeOutcome.Expired, 0);
            FinishCustomerReservation(offer);
        }

        private void RemoveOffer(Offer offer)
        {
            if (offer != null && _offers.TryGetValue(offer.Counter, out var current)
                && ReferenceEquals(current, offer))
            {
                _offers.Remove(offer.Counter);
                _offersByCustomer.Remove(offer.Customer);
            }
        }

        private void FinishCustomerReservation(Offer offer)
        {
            if (offer?.Customer == null)
            {
                return;
            }

            var screen = TradeInterop.Screen;
            if (screen != null && TradeInterop.IsScreenOpen(screen)
                && ReferenceEquals(TradeInterop.CurrentCustomer(screen), offer.Customer))
            {
                TradeInterop.CloseScreen(screen);
            }

            TradeInterop.FinishCustomer(offer.Customer);
        }

        private TradeOfferBaselineMessage BuildBaseline()
        {
            var state = new TradeOfferBaselineMessage();
            var counters = new List<byte>(_offers.Keys);
            counters.Sort();
            for (var i = 0; i < counters.Count; i++)
            {
                state.Offers.Add(ToState(_offers[counters[i]]));
            }

            return state;
        }

        private static TradeOfferState ToState(Offer offer)
        {
            return new TradeOfferState
            {
                Counter = offer.Counter,
                Trading = offer.Data.m_IsTrading,
                CardL = CopyCard(offer.Data.m_CardData_L),
                CardR = CopyCard(offer.Data.m_CardData_R),
                Price = offer.Data.m_SellCardAskPrice,
                MarketPrice = offer.Data.m_SellCardMarketPrice,
                PriceSet = offer.Data.m_PriceSet,
                LastPriceSet = offer.Data.m_LastPriceSet,
                MaxDeclineCount = offer.Data.m_MaxDeclineCount,
                DeclineCount = offer.Data.m_DeclineCount,
                Remaining = TradeInterop.WaitRemaining(offer.Customer),
                CustomerIndex = offer.CustomerIndex,
                CustomerGeneration = offer.CustomerGeneration,
                OfferNonce = offer.Nonce,
                CustomerFemale = offer.Customer.m_IsFemale,
                Position = offer.Customer.transform.position,
                Yaw = offer.Customer.transform.eulerAngles.y,
            };
        }

        private void Reject(int connectionId, TradeIntentMessage message)
        {
            PredictionApi.Rollback(_context, connectionId, message.PredictionId);
        }

        private void SendOfferDelta(Offer offer, bool removed, Guid predictionId,
            TradeOutcome outcome, int predictionPeer)
        {
            if (_shutdown || !_context.InGame() || offer == null)
            {
                return;
            }

            var state = removed ? null : ToState(offer);
            foreach (var pair in _joinedConnections)
            {
                if (pair.Value == null || !IsJoinPhase(pair.Value.State))
                {
                    continue;
                }

                _context.Send(pair.Key, new TradeOfferDeltaMessage
                {
                    PredictionId = pair.Key == predictionPeer ? predictionId : Guid.Empty,
                    Counter = offer.Counter,
                    Outcome = outcome,
                    Removed = removed,
                    Offer = state,
                });
            }
        }

        private static bool TryValidateBid(CustomerTradeData data, float value, out float bid)
        {
            bid = 0f;
            if (data == null || float.IsNaN(value) || float.IsInfinity(value))
            {
                return false;
            }

            if (data.m_IsTrading)
            {
                return value == 0f;
            }

            var ask = data.m_SellCardAskPrice;
            if (float.IsNaN(ask) || float.IsInfinity(ask) || ask <= 0f
                || value < 0f || value > ask)
            {
                return false;
            }

            bid = value;
            return true;
        }

        private static bool SameData(CustomerTradeData left, CustomerTradeData right)
        {
            return left != null && right != null
                && left.m_IsTrading == right.m_IsTrading
                && Mathf.Approximately(left.m_PriceSet, right.m_PriceSet)
                && Mathf.Approximately(left.m_LastPriceSet, right.m_LastPriceSet)
                && Mathf.Approximately(left.m_SellCardAskPrice, right.m_SellCardAskPrice)
                && Mathf.Approximately(left.m_SellCardMarketPrice, right.m_SellCardMarketPrice)
                && left.m_MaxDeclineCount == right.m_MaxDeclineCount
                && left.m_DeclineCount == right.m_DeclineCount
                && SameCard(left.m_CardData_L, right.m_CardData_L)
                && SameCard(left.m_CardData_R, right.m_CardData_R);
        }

        private static bool SameCard(CardData left, CardData right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return left.monsterType == right.monsterType
                && left.expansionType == right.expansionType
                && left.borderType == right.borderType
                && left.isFoil == right.isFoil
                && left.isDestiny == right.isDestiny
                && left.cardGrade == right.cardGrade
                && left.gradedCardIndex == right.gradedCardIndex;
        }

        private static CardData CopyCard(CardData card)
        {
            if (card == null)
            {
                return null;
            }

            var copy = new CardData();
            copy.CopyData(card);
            return copy;
        }

        private void SignalCustomerManagerReady()
        {
            _customerManagerReady = TradeInterop.Manager != null && TradeInterop.Customers != null;
            SendBaselinesToJoined();
        }

        private void SignalTradeScreenReady()
        {
            _tradeScreenReady = TradeInterop.Screen != null;
            if (_tradeScreenReady)
            {
                RegisterAllCustomers(TradeInterop.Customers);
            }
            SendBaselinesToJoined();
        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
        {
            SignalCustomerManagerReady();
            SignalTradeScreenReady();
            RegisterAllCustomers(TradeInterop.Customers);
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (_shutdown)
            {
                return;
            }

            _customerManagerReady = false;
            _tradeScreenReady = false;
            _offers.Clear();
            _offersByCustomer.Clear();
            _customerIndices.Clear();
            _hasObservedDay = false;
        }

        [HarmonyPatch(typeof(CustomerManager), "Start")]
        private static class CustomerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CustomerManager __instance)
            {
                _active?.RegisterAllCustomers(__instance?.GetCustomerList());
                _active?.SignalCustomerManagerReady();
            }
        }

        [HarmonyPatch(typeof(UIScreenBase), "Start")]
        private static class TradeScreenStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(UIScreenBase __instance)
            {
                if (__instance is CustomerTradeCardScreen)
                {
                    _active?.SignalTradeScreenReady();
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        [HarmonyPatch(typeof(Customer), "DeactivateCustomer")]
        private static class CustomerPopulationPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ReconcileCustomer(__instance);
        }

        [HarmonyPatch(typeof(Customer), "OnMousePress")]
        private static class CustomerPressPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                if (_active == null || __instance == null)
                {
                    return true;
                }

                foreach (var offer in _active._offers.Values)
                {
                    if (ReferenceEquals(offer.Customer, __instance) && offer.Owner != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Customer), "SetState")]
        private static class CustomerStatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ReconcileCustomer(__instance);
        }

        [HarmonyPatch(typeof(Customer), "OnPressStopInteract")]
        private static class CustomerStopPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ReconcileCustomer(__instance);
        }

        [HarmonyPatch(typeof(CustomerTradeCardScreen), "OnPressLetMeThink")]
        private static class ThinkPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                var screen = TradeInterop.Screen;
                _active?.ReconcileCustomer(TradeInterop.CurrentCustomer(screen));
            }
        }
    }
}
