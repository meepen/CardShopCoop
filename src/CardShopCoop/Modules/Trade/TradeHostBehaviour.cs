using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.World;
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
            _harmony.CreateClassProcessor(typeof(CustomerActionPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerPathEndPatch)).Patch();
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
            CoopPlugin.Log.LogInfo("[trade] host module enabled.");
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
                    // Re-assert the offer to the remaining peers now that the departed actor no
                    // longer owns it, instead of leaving them on a stale owner until the next trade
                    // delta. This is the same kept-offer shape every other host-local offer update
                    // uses, so the contents are unchanged and the host stays the single writer.
                    SendOfferDelta(offer, removed: false, Guid.Empty, TradeOutcome.None, 0);
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
                CoopPlugin.Log.LogWarning("[trade] host ignored intent from invalid sender conn="
                    + (messageContext?.Connection?.Id ?? 0) + " op="
                    + (message == null ? "<null>" : message.Operation.ToString()) + ".");
                return;
            }

            if (!_offers.TryGetValue(message.Counter, out var offer)
                || !Matches(offer, message))
            {
                Reject(messageContext.Connection.Id, message,
                    offer == null
                        ? "no live offer for counter " + message.Counter
                        : "offer identity mismatch (offer index=" + offer.CustomerIndex + " gen="
                            + offer.CustomerGeneration + " nonce=" + offer.Nonce + ", intent index="
                            + message.CustomerIndex + " gen=" + message.CustomerGeneration
                            + " nonce=" + message.OfferNonce + ")");
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

            if (offer.Owner != connectionId)
            {
                Reject(connectionId, message,
                    "offer is owned by connection " + offer.Owner + ", not " + connectionId);
                return;
            }

            if (!ValidateLiveAction(connectionId, offer, out var reason))
            {
                Reject(connectionId, message, reason);
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
                Reject(connectionId, message, "unknown operation " + message.Operation);
            }
        }

        private void Open(int connectionId, TradeIntentMessage message, Offer offer)
        {
            if (offer.Owner != 0)
            {
                Reject(connectionId, message, "offer already owned by connection " + offer.Owner);
                return;
            }

            if (!ValidateLiveAction(connectionId, offer, out var reason))
            {
                Reject(connectionId, message, reason);
                return;
            }

            offer.Owner = connectionId;
            CoopPlugin.Log.LogInfo("[trade] host accepted open counter=" + offer.Counter + " index="
                + offer.CustomerIndex + " gen=" + offer.CustomerGeneration + " nonce=" + offer.Nonce
                + " owner=" + connectionId + ".");
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
                Reject(connectionId, message, "invalid bid price=" + message.Price
                    + " (trading=" + (data == null ? "null" : data.m_IsTrading.ToString())
                    + ", ask=" + (data == null ? "n/a" : data.m_SellCardAskPrice.ToString("F2"))
                    + ")");
                return;
            }

            // The actor's own vanilla accept is the single application: it already minted/took
            // cards, spent coins and rolled the accept, and those effects are carried on the one
            // accept intent (the client's separate forwards are suppressed). The host applies the
            // same card + coin + offer movement here, exactly once, so the whole outcome is a
            // single predicted action: the actor retires its prediction on the echo, every other
            // peer applies it, and a reject rolls all of it back together.
            switch (message.Result)
            {
                case TradeOutcome.Accepted:
                    if (!ApplyAcceptedEffect(connectionId, message, offer, bid))
                    {
                        Reject(connectionId, message, "accept effect could not be applied");
                        return;
                    }

                    RemoveOffer(offer);
                    SendOfferDelta(offer, removed: true, message.PredictionId, message.Result,
                        connectionId);
                    FinishCustomerReservation(offer);
                    return;
                case TradeOutcome.WalkedAway:
                    // The declines ran out, so vanilla minted/took nothing; only the offer ends.
                    RemoveOffer(offer);
                    SendOfferDelta(offer, removed: true, message.PredictionId, message.Result,
                        connectionId);
                    FinishCustomerReservation(offer);
                    return;
                case TradeOutcome.Haggle:
                case TradeOutcome.Refused:
                    {
                        if (message.ResultState == null)
                        {
                            Reject(connectionId, message, "kept offer has no resulting state");
                            return;
                        }

                        var updated = TradeInterop.DataFromState(message.ResultState);
                        TradeInterop.SetStoredData(offer.Customer, updated);
                        offer.Data = updated;
                        SendOfferDelta(offer, removed: false, message.PredictionId, message.Result,
                            connectionId);
                        return;
                    }
                default:
                    Reject(connectionId, message, "unexpected accept result " + message.Result);
                    return;
            }
        }

        /// <summary>Host side of the one atomic accept: validates the carried effect against the
        /// offer, admits the coin spend, and applies the card movement through the World ledger,
        /// which also fans the card batch out to every peer tied to the trade prediction. The actor
        /// already performed the movement locally, so its prediction retires on that echo while the
        /// other peers apply it. Returns false (for a full reject) when the effect cannot be
        /// admitted, having changed nothing.</summary>
        private bool ApplyAcceptedEffect(int connectionId, TradeIntentMessage message, Offer offer,
            float bid)
        {
            var data = offer.Data;
            var trading = data.m_IsTrading;
            if (trading && message.CoinSpent != 0f)
            {
                CoopPlugin.Log.LogWarning("[trade] accept for card-for-card offer " + offer.Counter
                    + " carried a coin spend; refusing.");
                return false;
            }

            if (!trading && Mathf.Abs(message.CoinSpent - bid) > 0.001f)
            {
                CoopPlugin.Log.LogWarning("[trade] accept for purchase offer " + offer.Counter
                    + " spent " + message.CoinSpent + " but the validated bid is " + bid
                    + "; refusing.");
                return false;
            }

            // The card movement is host-derived, never trusted from the client: the customer's
            // offered card and (for a card-for-card trade) the card the player must give are both
            // read from the authoritative offer data. A client that carries anything else is
            // desynced or hostile, so the whole accept is refused.
            var received = data.m_CardData_L;
            var given = trading ? data.m_CardData_R : null;
            if (!SameCard(received, message.CardReceived) || !SameCard(given, message.CardRemoved))
            {
                CoopPlugin.Log.LogWarning("[trade] accept for offer " + offer.Counter
                    + " carried cards that do not match the offer (received="
                    + DescribeCard(message.CardReceived) + " expected=" + DescribeCard(received)
                    + ", given=" + DescribeCard(message.CardRemoved) + " expected="
                    + DescribeCard(given) + "); refusing.");
                return false;
            }

            EconomyAuthority.HostSpendReservation spend = null;
            if (!trading && message.CoinSpent > 0f
                && !EconomyAuthority.TryReserveHostSpend(message.CoinSpent, out spend))
            {
                CoopPlugin.Log.LogInfo("[trade] host cannot fund guest purchase of "
                    + message.CoinSpent + " on offer " + offer.Counter + "; refusing the accept.");
                return false;
            }

            if (received != null || given != null)
            {
                var cards = WorldHostBehaviour.ActiveCards;
                var batch = new CardDeltaBatchMessage { PredictionId = message.PredictionId };
                // Give first. The strict batch only commits the received card once the card the
                // player hands over has actually left this host; a delta that was merely relayed
                // (this host lacks the content) or refused aborts the whole movement, so the host
                // can never mint a card the authoritative offer did not include.
                if (given != null)
                {
                    batch.Deltas.Add(new CardDeltaEntry
                    {
                        IsAdd = false,
                        Amount = 1,
                        Card = given,
                    });
                }

                if (received != null)
                {
                    batch.Deltas.Add(new CardDeltaEntry
                    {
                        IsAdd = true,
                        Amount = 1,
                        Card = received,
                    });
                }

                if (cards == null || !cards.HandleCardDeltaBatch(connectionId, batch, true))
                {
                    CoopPlugin.Log.LogWarning("[trade] host could not apply the card movement for "
                        + "offer " + offer.Counter + "; refusing the accept.");
                    return false;
                }
            }

            if (spend != null)
            {
                EconomyAuthority.QueueHostSpend(spend);
            }

            return true;
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
                Reject(connectionId, message, "invalid think price=" + message.Price
                    + " (trading=" + (offer.Data == null ? "null" : offer.Data.m_IsTrading.ToString())
                    + ")");
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

        private bool ValidateLiveAction(int connectionId, Offer offer, out string reason)
        {
            if (HostIsBusy())
            {
                var manager = TradeInterop.Manager;
                reason = "host is busy (playerTrading=" + (manager != null && manager.m_IsPlayerTrading)
                    + " screenOpen=" + TradeInterop.IsScreenOpen(TradeInterop.Screen) + ")";
                return false;
            }

            if (!TradeInterop.IsWaitingForTrade(offer.Customer))
            {
                reason = "customer is not waiting to trade (state="
                    + (offer.Customer == null ? "null" : offer.Customer.m_CurrentState.ToString()) + ")";
                return false;
            }

            if (!TradeInterop.SameCustomer(offer.Customer, offer.CustomerIndex,
                offer.CustomerGeneration))
            {
                reason = "customer identity changed (offer index=" + offer.CustomerIndex
                    + " gen=" + offer.CustomerGeneration + ", live index="
                    + TradeInterop.CustomerIdentity(offer.Customer) + " gen="
                    + TradeInterop.CustomerGeneration(offer.Customer) + ")";
                return false;
            }

            var liveCounter = TradeInterop.CounterIndex(offer.Customer);
            if (liveCounter != offer.Counter)
            {
                reason = "counter changed (offer=" + offer.Counter + ", live=" + liveCounter + ")";
                return false;
            }

            if (!_context.PeerPresence.TryGet(connectionId, out var presence))
            {
                reason = "no presence recorded for connection " + connectionId;
                return false;
            }

            if (presence.Age > TimeSpan.FromSeconds(2))
            {
                reason = "presence is stale (" + presence.Age.TotalSeconds.ToString("F2") + "s old)";
                return false;
            }

            if (!TradeInterop.IsReachable(presence.Position, offer.Customer.transform.position, Reach))
            {
                reason = "player is out of reach (distance=" + Vector3
                    .Distance(presence.Position, offer.Customer.transform.position).ToString("F2")
                    + " > " + Reach + ")";
                return false;
            }

            reason = null;
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
                CoopPlugin.Log.LogDebug("Trade host: waiting customer has no stable trade identity");
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
                CoopPlugin.Log.LogDebug("[trade] host offer index=" + customerIndex
                    + " deferred: busy=" + HostIsBusy() + " screen=" + (TradeInterop.Screen != null) + ".");
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
                CoopPlugin.Log.LogDebug("[trade] host offer index=" + customerIndex
                    + " has no tradeable card: L=" + (data.m_CardData_L != null)
                    + " R=" + (data.m_CardData_R != null) + " trading=" + data.m_IsTrading + ".");
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
            CoopPlugin.Log.LogInfo("[trade] host created offer counter=" + counter + " index="
                + customerIndex + " gen=" + generation + " nonce=" + _nextNonce + " ask="
                + data.m_SellCardAskPrice.ToString("F2") + " priceSet="
                + data.m_PriceSet.ToString("F2") + " trading=" + data.m_IsTrading + ".");
            SendOfferDelta(_offers[counter], removed: false, Guid.Empty, TradeOutcome.None, 0);
        }

        private void RefreshStoredData(Offer offer)
        {
            var stored = TradeInterop.StoredData(offer.Customer);
            if (stored != null && !SameData(offer.Data, stored))
            {
                CoopPlugin.Log.LogInfo("[trade] host refreshed offer counter=" + offer.Counter
                    + " nonce=" + offer.Nonce + " ask=" + stored.m_SellCardAskPrice.ToString("F2")
                    + " priceSet=" + stored.m_PriceSet.ToString("F2") + " trading="
                    + stored.m_IsTrading + " (was ask="
                    + (offer.Data == null ? "n/a" : offer.Data.m_SellCardAskPrice.ToString("F2"))
                    + " priceSet="
                    + (offer.Data == null ? "n/a" : offer.Data.m_PriceSet.ToString("F2"))
                    + " trading=" + (offer.Data == null ? "n/a" : offer.Data.m_IsTrading.ToString())
                    + ").");
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

        private void Reject(int connectionId, TradeIntentMessage message, string reason)
        {
            CoopPlugin.Log.LogInfo("[trade] host rejected " + message.Operation + " counter="
                + message.Counter + " id=" + message.PredictionId + " nonce=" + message.OfferNonce
                + ": " + reason + ".");
            PredictionApi.Rollback(_context, connectionId, message.PredictionId);
        }

        private void SendOfferDelta(Offer offer, bool removed, Guid predictionId,
            TradeOutcome outcome, int predictionPeer)
        {
            if (_shutdown || !_context.InGame() || offer == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[trade] host sending offer counter=" + offer.Counter + " index="
                + offer.CustomerIndex + " gen=" + offer.CustomerGeneration + " removed=" + removed
                + " nonce=" + offer.Nonce + " ask="
                + (offer.Data == null ? "n/a" : offer.Data.m_SellCardAskPrice.ToString("F2"))
                + " priceSet="
                + (offer.Data == null ? "n/a" : offer.Data.m_PriceSet.ToString("F2"))
                + " pred=" + predictionId + " peers=" + _joinedConnections.Count + ".");

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

        /// <summary>Validates the shape of one offer decision. Vanilla accepts any price the player
        /// entered into the money input: an offer at or above the ask is guaranteed (the ask shown
        /// in the UI is rounded to the currency's decimals, so typing the displayed ask can sit a
        /// cent above the raw ask and vanilla still accepts it), and an offer below the ask can
        /// still be accepted by the actor's roll. The host must never reject an offer vanilla
        /// accepted, so only non-finite or negative prices are refused here; the wallet admission
        /// in <see cref="ApplyAcceptedEffect"/> still bounds the actual spend, and card-for-card
        /// trades carry no price at all.</summary>
        private static bool TryValidateBid(CustomerTradeData data, float value, out float bid)
        {
            bid = 0f;
            if (data == null || float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            {
                return false;
            }

            if (data.m_IsTrading)
            {
                return value == 0f;
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

        private static string DescribeCard(CardData card)
            => card == null ? "<null>"
                : card.expansionType + "/" + card.monsterType + "/" + card.borderType
                    + (card.isFoil ? "/foil" : "") + (card.isDestiny ? "/destiny" : "")
                    + (card.cardGrade > 0 ? "/grade" + card.cardGrade : "");

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
            // Single-writer gate: a guest already owns this customer's trade session (offer.Owner
            // != 0), so the host must not open the same customer's screen and start a second
            // session. Only the host's own view of an unowned customer is allowed through.
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

        // Customer.SetState is a one-line private setter, so Mono can inline it at the call sites
        // that assign the trade states (both of them live in the two methods below), bypassing this
        // postfix. It is kept for builds/methods that are not inlined; the two event hooks below are
        // the reliable, event-driven triggers.
        [HarmonyPatch(typeof(Customer), "SetState")]
        private static class CustomerStatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ReconcileCustomer(__instance);
        }

        // Sets ECustomerState.WantToTradeCard (the customer spots a free trade counter and starts
        // walking to it).
        [HarmonyPatch(typeof(Customer), "DetermineShopAction")]
        private static class CustomerActionPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ReconcileCustomer(__instance);
        }

        // Sets ECustomerState.WaitingToTradeCard once the customer reaches the trade stand. This is
        // the transition that actually makes the trade available.
        [HarmonyPatch(typeof(Customer), "OnReachedPathEnd")]
        private static class CustomerPathEndPatch
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
