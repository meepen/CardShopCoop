using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
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
        private bool _buildingBaseline;
        private Guid _intentPredictionId;
        // True while vanilla runs Customer.WaypointEndUpdate's paying-position block. Vanilla
        // announces the customer (UpdateCurrentCustomer) before it registers the bag for scanning
        // (RegisterScanItem), so a customer lifecycle built from the announcement reads every
        // never-registered item as already scanned and every guest scanned the whole bag the
        // moment the customer set it down. CustomerArrivalPatch suppresses that publish and owns
        // the arrival publish instead, from after the registration loop has run.
        private bool _insideCustomerArrival;
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
            _harmony = new Harmony("dev.meepen.cardshopcoop.register.host");
            Patch(typeof(ManningPatch));
            Patch(typeof(ExitPatch));
            Patch(typeof(CustomerPatch));
            Patch(typeof(CustomerArrivalPatch));
            Patch(typeof(StatePatch));
            Patch(typeof(PaymentPatch));
            Patch(typeof(ChangePhasePatch));
            Patch(typeof(ScanItemPatch));
            Patch(typeof(ScanCardPatch));
            Patch(typeof(AddChangePatch));
            Patch(typeof(RemoveChangePatch));
            Patch(typeof(FinishPatch));
            Patch(typeof(OutlinePatch));
            // Subscribe before patching: a failed Patch(...) throws out of OnEnable, and the
            // sceneLoaded subscription must survive so host scene reloads still reset stations.
            SceneManager.sceneLoaded += OnSceneLoaded;
            Patch(typeof(CounterAddedPatch));
            Patch(typeof(CounterRemovedPatch));
            Patch(typeof(CounterDestroyedPatch));
            Patch(typeof(ResetCounterPatch));
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
                || !_context.InGame() || message.CounterKey == 0
                || !Enum.IsDefined(typeof(RegisterIntentKind), message.Kind))
            {
                return;
            }

            _intentPredictionId = message.PredictionId;
            try
            {
                var index = message.CounterKey;
                var counter = RegisterInterop.CounterForKey(index);
                var station = counter == null ? null : ObservePublishing(index, counter);
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
                    // The register became this client's; stop any local highlight that was
                    // advertising it to the host.
                    RegisterHighlight.Clear(counter);
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
                    // No highlight restore is needed here: a detached counter is raycast again
                    // on the next frame, where a free register highlights normally.
                    return;
                }

                if (station.Owner != connection.Id || !Apply(counter, message))
                {
                    if (station.Owner != connection.Id)
                    {
                        LogIntentReject("station owner " + station.Owner + " != sender "
                            + connection.Id, message);
                    }

                    RejectCheckout(connection.Id, index, message.PredictionId);
                    return;
                }

                if (message.Kind == RegisterIntentKind.TakePayment)
                {
                    PublishPhase(index, true);
                }
                else if (message.Kind == RegisterIntentKind.CardPayment
                    || message.Kind == RegisterIntentKind.Complete)
                {
                    // The checkout completed on this host by applying a guest's intent. The game's
                    // own credit inside OnPressSpaceBar is gated on m_IsMannedByPlayer, which is
                    // false here (the host never physically manned the counter), so the shared
                    // tutorial, the end-of-day manual-checkout count, and the checkout achievement
                    // would otherwise never advance for a guest-run register. Credit them on the
                    // host's authoritative state; the guest's local vanilla credit is presentation
                    // and is overwritten by the authoritative tutorial/report deltas.
                    CreditGuestCheckout();
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
                        LogIntentReject("counter state " + counter.m_CashierCounterState, message);
                        return false;
                    }

                    var items = customer.GetItemInBagList();
                    if (message.Slot < 0 || message.Slot >= items.Count || items[message.Slot] == null
                        || items[message.Slot].m_InteractableScanItem == null
                        || !items[message.Slot].m_InteractableScanItem.IsNotScanned())
                    {
                        LogIntentReject("slot=" + message.Slot + " bagItems="
                            + (items?.Count ?? -1) + " alreadyScanned="
                            + (items != null && message.Slot >= 0 && message.Slot < items.Count
                                && items[message.Slot]?.m_InteractableScanItem != null
                                && !items[message.Slot].m_InteractableScanItem.IsNotScanned()),
                            message);
                        return false;
                    }

                    items[message.Slot].m_InteractableScanItem.OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.ScanCard:
                    if (counter.m_CashierCounterState != ECashierCounterState.ScanningItem)
                    {
                        LogIntentReject("counter state " + counter.m_CashierCounterState, message);
                        return false;
                    }

                    var cards = customer.GetCardInBagList();
                    if (message.Slot < 0 || message.Slot >= cards.Count || cards[message.Slot] == null
                        || !cards[message.Slot].IsNotScanned())
                    {
                        LogIntentReject("slot=" + message.Slot + " bagCards="
                            + (cards?.Count ?? -1) + " alreadyScanned="
                            + (cards != null && message.Slot >= 0 && message.Slot < cards.Count
                                && cards[message.Slot] != null
                                && !cards[message.Slot].IsNotScanned()),
                            message);
                        return false;
                    }

                    cards[message.Slot].OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.TakePayment:
                    if (counter.m_CashierCounterState != ECashierCounterState.TakingCash
                        || customer.m_CustomerCash == null
                        || customer.m_CustomerCash.m_IsCard != message.IsCard)
                    {
                        LogIntentReject("take payment state=" + counter.m_CashierCounterState
                            + " intentCard=" + message.IsCard + " cash="
                            + (customer.m_CustomerCash == null
                                ? "null" : customer.m_CustomerCash.m_IsCard.ToString()), message);
                        return false;
                    }

                    customer.m_CustomerCash.OnMouseButtonUp();
                    return true;

                case RegisterIntentKind.CardPayment:
                    if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                        || !RegisterInterop.IsUsingCard(counter) || !RegisterInterop.IsFinite(message.Value)
                        || Math.Abs(message.Value - RegisterInterop.Total(counter)) > 0.011d)
                    {
                        LogIntentReject("card payment state=" + counter.m_CashierCounterState
                            + " usingCard=" + RegisterInterop.IsUsingCard(counter)
                            + " value=" + message.Value.ToString("F3")
                            + " hostTotal=" + RegisterInterop.Total(counter).ToString("F3"), message);
                        return false;
                    }

                    counter.EvaluateCreditCard(message.Value);
                    return true;

                case RegisterIntentKind.Change:
                    if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                        || RegisterInterop.IsUsingCard(counter) || !RegisterInterop.IsFinite(message.Value))
                    {
                        LogIntentReject("change state=" + counter.m_CashierCounterState
                            + " usingCard=" + RegisterInterop.IsUsingCard(counter)
                            + " value=" + message.Value.ToString("F3"), message);
                        return false;
                    }

                    var change = RegisterInterop.FindChange(counter, message.Slot, message.IsCoin);
                    if (change == null)
                    {
                        LogIntentReject("change slot=" + message.Slot + " coin=" + message.IsCoin
                            + " value=" + message.Value.ToString("F3") + " not found", message);
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
                        LogIntentReject("complete state=" + counter.m_CashierCounterState
                            + " changeReady=" + RegisterInterop.IsChangeReady(counter), message);
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
            else if (customer != null)
            {
                // A pooled customer object can be reused for the next customer at this counter, so
                // the reference is unchanged even though it is a new incarnation. The Npc module
                // bumps its generation on the inactive->active transition; re-sync it here so this
                // counter gets a new generation and the client rebuilds its carrier. Without this
                // the guest kept the previous customer's (now stale) bag and its scans were
                // rejected against the host's live, different bag.
                var generation = NpcHostBehaviour.GetCustomerGeneration(customer);
                if (generation > 0 && (uint)generation != station.CustomerGeneration)
                {
                    CoopPlugin.Log.LogDebug("[register] counter " + index + " customer generation "
                        + station.CustomerGeneration + " -> " + generation
                        + " (pooled customer reused).");
                    station.CustomerGeneration = (uint)generation;
                    // Re-syncing the generation is only half the promise: the client rebuilds its
                    // carrier when it learns the new generation, so the advance has to be published.
                    // Without this the guest kept the old generation, every customer-gated delta was
                    // deferred against it, and its carrier stayed on the previous customer's bag.
                    PublishObservedCustomer(index, station, customer);
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

        /// <summary>Observe for a live broadcast path: if the stable key somehow names a different
        /// counter object than the station remembered (the object was replaced under an identity),
        /// the new generation must reach the clients before the caller's delta. Clients gate every
        /// non-ownership delta on the generation they hold for the key (see
        /// <c>HasCounterGeneration</c>). Prediction-free on purpose: this corrects state, it does
        /// not confirm the intent whose publish happened to notice the change.</summary>
        private Station ObservePublishing(int index, InteractableCashierCounter counter)
        {
            var station = GetStation(index);
            var rebound = !ReferenceEquals(station.Counter, counter);
            var observed = Observe(index, counter);
            if (rebound)
            {
                Broadcast(new RegisterDeltaMessage
                {
                    Kind = RegisterDeltaKind.CounterLifecycle,
                    CounterKey = index,
                    CounterGeneration = observed.CounterGeneration,
                    Exists = true,
                    PredictionId = Guid.Empty,
                });
            }

            return observed;
        }

        private RegisterBaselineMessage BuildBaseline()
        {
            var baseline = new RegisterBaselineMessage();
            var counters = RegisterInterop.Counters;
            // Building the join baseline observes every counter, and an observation can notice a
            // pooled customer's generation advance. The baseline already carries each counter's
            // current generation, so the live resync broadcast must not fire from this path.
            _buildingBaseline = true;
            try
            {
                for (var i = 0; counters != null && i < counters.Count; i++)
                {
                    var counter = counters[i];
                    var key = counter == null ? 0 : RegisterInterop.Key(counter);
                    if (key == 0)
                    {
                        continue;
                    }

                    baseline.Counters.Add(BuildBaselineCounter(key, counter));
                }
            }
            finally
            {
                _buildingBaseline = false;
            }

            return baseline;
        }

        private RegisterBaselineCounter BuildBaselineCounter(int key,
            InteractableCashierCounter counter)
        {
            var station = Observe(key, counter);
            var result = new RegisterBaselineCounter
            {
                CounterKey = key,
                CounterGeneration = station.CounterGeneration,
                Exists = true,
                Owner = station.Owner,
                CustomerIndex = -1,
            };
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

            var station = Observe(RegisterInterop.Key(counter), counter);
            result.CustomerIndex = RegisterInterop.CustomerIndex(customer);
            result.CustomerGeneration = (int)station.CustomerGeneration;
            result.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(customer,
                "m_TotalScannedItemCost") ?? 0f);
            AddLines(result.Lines, customer);
            AddChanges(result.ChangeItems, counter);
        }

        private static void AddLines(List<RegisterLine> lines, Customer customer)
        {
            // A bag item is registered for scanning (InteractableScanItem.m_CurrentCustomer set)
            // only once the customer reaches the paying position, and IsNotScanned() is
            // m_CurrentCustomer != null - so an item that was simply never registered reads
            // exactly like "already scanned". Two windows can publish a lifecycle while the bag is
            // not yet registered: a re-anchor sent while the customer is still walking to the
            // counter, and the arrival announcement itself (UpdateCurrentCustomer runs before
            // RegisterScanItem in Customer.WaypointEndUpdate - see CustomerArrivalPatch, which
            // owns the arrival publish). Gate the flag on the customer actually being at the
            // paying position so both windows read every line as un-scanned.
            var atPayingPosition = RegisterInterop.Read(customer, "m_IsAtPayingPosition") is bool atPaying
                && atPaying;
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                lines.Add(new RegisterLine
                {
                    ItemType = item == null ? EItemType.None : item.GetItemType(),
                    Price = item == null ? 0f : item.GetCurrentPrice(),
                    Scanned = atPayingPosition && item != null && item.m_InteractableScanItem != null
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
                    Scanned = atPayingPosition && card != null && !card.IsNotScanned(),
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
            var counter = RegisterInterop.CounterForKey(index);
            var station = counter == null ? GetStation(index) : ObservePublishing(index, counter);
            return new RegisterDeltaMessage
            {
                Kind = kind,
                CounterKey = index,
                CounterGeneration = station.CounterGeneration,
                PredictionId = _intentPredictionId,
            };
        }

        private void PublishOwnership(int index)
        {
            var counter = RegisterInterop.CounterForKey(index);
            var delta = NewDelta(index, RegisterDeltaKind.Ownership);
            delta.Owner = counter == null ? 0 : Observe(index, counter).Owner;
            Broadcast(delta);
        }

        private void PublishCustomerLifecycle(int index, bool force = false)
        {
            if (index == 0)
            {
                return;
            }

            if (!force && _intentPredictionId != Guid.Empty)
            {
                return;
            }

            var counter = RegisterInterop.CounterForKey(index);
            var delta = NewDelta(index, RegisterDeltaKind.CustomerLifecycle);
            var station = counter == null ? GetStation(index) : Observe(index, counter);
            var customer = counter?.m_CurrentCustomer;
            delta.HasCustomer = customer != null;
            if (customer != null)
            {
                delta.CustomerIndex = RegisterInterop.CustomerIndex(customer);
                delta.CustomerGeneration = (int)station.CustomerGeneration;
                delta.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(customer,
                    "m_TotalScannedItemCost") ?? 0f);
                AddLines(delta.Lines, customer);
            }

            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private void PublishScan(int index, bool isCard, int slot)
        {
            var counter = RegisterInterop.CounterForKey(index);
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
            if (index == 0)
            {
                return;
            }

            if (!force && _intentPredictionId != Guid.Empty)
            {
                return;
            }

            var counter = RegisterInterop.CounterForKey(index);
            if (counter == null)
            {
                return;
            }

            var delta = NewDelta(index, RegisterDeltaKind.PhasePayment);
            FillPhase(delta, counter);
            Broadcast(delta);
        }

        /// <summary>The host is the single roller of the customer's paid amount and cash/card kind
        /// (vanilla <c>EvaluateFinishScanItem</c> runs on the host's customer). <see cref="PublishPhase"/>
        /// is suppressed while an intent is being applied and, when it does fire, carries the
        /// guest's scan prediction id, so the guest's acknowledgement retires it without applying
        /// the host's value. This delta is deliberately prediction-free so the guest always applies
        /// the host's authoritative paid amount and kind, then presents the cash/card choice.</summary>
        private void PublishPaidAmount(int index)
        {
            if (index == 0)
            {
                return;
            }

            var counter = RegisterInterop.CounterForKey(index);
            var customer = counter?.m_CurrentCustomer;
            if (counter == null || customer == null)
            {
                return;
            }

            var station = ObservePublishing(index, counter);
            var delta = new RegisterDeltaMessage
            {
                Kind = RegisterDeltaKind.PaidAmount,
                CounterKey = index,
                CounterGeneration = station.CounterGeneration,
                HasCustomer = true,
                CustomerIndex = RegisterInterop.CustomerIndex(customer),
                CustomerGeneration = (int)station.CustomerGeneration,
                PredictionId = Guid.Empty,
            };
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

        /// <summary>Builds the full authoritative customer + phase state for one counter as a
        /// <see cref="RegisterDeltaKind.CustomerLifecycle"/> delta. Used both to re-anchor a guest
        /// whose station has gone stale (a reused pooled customer, or a checkout the host refused)
        /// and by the join baseline's live equivalent. The guest rebuilds its carrier from
        /// <see cref="RegisterDeltaMessage.Lines"/> and applies the phase, so a divergent bag,
        /// cash/card offer or total is replaced by authority instead of lingering.</summary>
        private RegisterDeltaMessage BuildCustomerState(int index, Station station, Customer customer,
            Guid predictionId)
        {
            var counter = RegisterInterop.CounterForKey(index);
            var delta = new RegisterDeltaMessage
            {
                Kind = RegisterDeltaKind.CustomerLifecycle,
                CounterKey = index,
                CounterGeneration = station.CounterGeneration,
                PredictionId = predictionId,
                HasCustomer = customer != null,
            };
            if (customer != null)
            {
                delta.CustomerIndex = RegisterInterop.CustomerIndex(customer);
                delta.CustomerGeneration = (int)station.CustomerGeneration;
                delta.CustomerTotal = Convert.ToDouble(RegisterInterop.Read(customer,
                    "m_TotalScannedItemCost") ?? 0f);
                AddLines(delta.Lines, customer);
            }

            FillPhase(delta, counter);
            return delta;
        }

        /// <summary>Answers a rejected checkout with this counter's authoritative customer and
        /// phase under the rejected prediction id, then sends the generic rollback (the store-access
        /// pattern). The requester's handler <c>Confirm</c> retires the rejected action and applies
        /// this authority, so the trailing rollback is ignored as already-resolved instead of
        /// inverting a snapshot the host has moved past. Broadcast rather than sent to the requester
        /// alone: every guest mirrors every counter's customer, and the id is unknown to the others,
        /// so their <c>Confirm</c> simply applies the authority too. This is what corrects a checkout
        /// the host refused: without it the guest kept its own stale bag, cash/card offer and total
        /// until some later delta happened to land - the negative balance due and the "customer
        /// holds neither cash nor card" report.</summary>
        private void PublishRejectionState(int connectionId, int index, Guid predictionId)
        {
            if (_shutdown || !_context.InGame() || index == 0)
            {
                return;
            }

            var counter = RegisterInterop.CounterForKey(index);
            if (counter == null)
            {
                return;
            }

            var station = ObservePublishing(index, counter);
            CoopPlugin.Log.LogInfo("[register] re-anchoring counter " + index + " after rejecting "
                + "conn " + connectionId + " pred=" + predictionId + ".");
            Broadcast(BuildCustomerState(index, station, counter.m_CurrentCustomer, predictionId));
        }

        /// <summary>Re-anchors every guest after <see cref="Observe"/> advanced a station's
        /// generation for a reused pooled customer. Prediction-free: this corrects state, it does
        /// not confirm an intent.</summary>
        private void PublishObservedCustomer(int index, Station station, Customer customer)
        {
            if (_shutdown || _buildingBaseline || !_context.InGame() || index == 0)
            {
                return;
            }

            Broadcast(BuildCustomerState(index, station, customer, Guid.Empty));
        }

        private void PublishChange(InteractableCounterMoneyChange change, bool takeBack)
        {
            var counter = change?.m_CashierCounter;
            var index = RegisterInterop.Key(counter);
            if (index == 0 || change == null)
            {
                return;
            }

            var delta = NewDelta(index, RegisterDeltaKind.Change);
            delta.Slot = change.m_Index;
            delta.IsCoin = change.m_IsCoin;
            delta.Value = change.m_ValueDouble;
            // The client gates Scan and Change on the station's customer identity (see
            // RequiresCustomer); without the index/generation here every change delta was
            // deferred and never applied, so money added or removed at the counter never
            // reached the other player.
            var customer = counter.m_CurrentCustomer;
            delta.HasCustomer = customer != null;
            if (customer != null)
            {
                delta.CustomerIndex = RegisterInterop.CustomerIndex(customer);
                delta.CustomerGeneration = (int)Observe(index, counter).CustomerGeneration;
            }

            // ChangeCount and the phase fields are absolute results. The client must not
            // replay the click direction after deferred compaction.
            delta.ChangeCount = RegisterInterop.GivenAmount(change);
            FillPhase(delta, counter);
            Broadcast(delta);
        }

        private void PublishCounterLifecycle(InteractableCashierCounter counter)
        {
            var key = counter == null ? 0 : RegisterInterop.Key(counter);
            if (key == 0)
            {
                return;
            }

            var delta = NewDelta(key, RegisterDeltaKind.CounterLifecycle);
            delta.Exists = true;
            Broadcast(delta);
        }

        /// <summary>Publishes a removed register. The key comes from the station already bound to
        /// this counter rather than being re-derived: at removal the placement identity may already
        /// be forgotten, and re-deriving on the host would assign a fresh key and tombstone the
        /// wrong register.</summary>
        private void Tombstone(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            var key = 0;
            foreach (var pair in _stations)
            {
                if (ReferenceEquals(pair.Value.Counter, counter))
                {
                    key = pair.Key;
                    break;
                }
            }

            if (key == 0)
            {
                return;
            }

            var station = _stations[key];
            var generation = station.CounterGeneration;
            _stations.Remove(key);
            var delta = new RegisterDeltaMessage
            {
                Kind = RegisterDeltaKind.CounterLifecycle,
                CounterKey = key,
                CounterGeneration = generation,
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

        private static void LogIntentReject(string reason, RegisterIntentMessage message)
            => CoopPlugin.Log.LogWarning("[register] rejected " + message?.Kind + " counter="
                + (message?.CounterKey ?? 0) + " slot=" + (message?.Slot ?? -1) + ": " + reason);

        private void Reject(int connectionId, Guid predictionId)
        {
            if (predictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, connectionId, predictionId);
            }
        }

        /// <summary>A rejected checkout first carries the counter's authoritative state back to
        /// every guest, then sends the generic rollback (see <see cref="PublishRejectionState"/>).
        /// Claim/release rejections are ownership only and carry no checkout state to re-anchor.</summary>
        private void RejectCheckout(int connectionId, int index, Guid predictionId)
        {
            PublishRejectionState(connectionId, index, predictionId);
            Reject(connectionId, predictionId);
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

        private static int Key(InteractableCashierCounter counter)
            => RegisterInterop.Key(counter);

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnMouseButtonUp")]
        private static class ManningPatch
        {
            // A register a guest is already manning is not the host's to take, so the prefix stops
            // the host's click from entering cashier mode (and from publishing an ownership that
            // would yank the guest off). A free counter passes through and the postfix observes the
            // host's own successful man and publishes it.
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
            {
                var active = _active;
                if (active == null || __instance == null || !active._context.InGame())
                {
                    return true;
                }

                var index = Key(__instance);
                if (index == 0)
                {
                    return true;
                }

                if (active.ObservePublishing(index, __instance).Owner > 0)
                {
                    CoopPlugin.Log.LogInfo("[register] host left counter " + index
                        + " to the player already manning it.");
                    return false;
                }

                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                if (_active == null || __instance == null || !_active._context.InGame()
                    || !__instance.IsMannedByPlayer())
                {
                    return;
                }

                var index = Key(__instance);
                if (index != 0)
                {
                    _active.ObservePublishing(index, __instance).Owner = -1;
                    _active.PublishOwnership(index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "OnRaycasted")]
        private static class OutlinePatch
        {
            // The green hover outline is raised from this machine's raycast alone. A register a
            // guest already mans is not interactable here, so it must not advertise itself (or its
            // tooltips) as if it were. Ownership is authoritative and known before the raycast in
            // the normal flow; a mid-look takeover is cleared when its claim is handled.
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
            {
                var active = _active;
                if (active == null || !active._context.InGame()
                    || __instance is not InteractableCashierCounter counter)
                {
                    return true;
                }

                var index = Key(counter);
                if (index != 0 && active._stations.TryGetValue(index, out var station)
                    && station.Owner > 0)
                {
                    RegisterHighlight.Clear(counter);
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressEsc")]
        private static class ExitPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                var index = Key(__instance);
                if (_active != null && index != 0 && _active.GetStation(index).Owner == -1
                    && !__instance.IsMannedByPlayer())
                {
                    _active.GetStation(index).Owner = 0;
                    _active.PublishOwnership(index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCurrentCustomer")]
        private static class CustomerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                var active = _active;
                // The one non-null announcement in vanilla is the customer's arrival, and it is
                // always mid-block (before the bag is registered for scanning), so its lifecycle
                // is owned by CustomerArrivalPatch. Only the customer-left and counter-reset
                // announcements (null, no lines) publish from here.
                if (active != null && !active._insideCustomerArrival)
                {
                    active.PublishCustomerLifecycle(Key(__instance));
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "WaypointEndUpdate")]
        private static class CustomerArrivalPatch
        {
            // Vanilla announces the arrival and registers the bag in one paying-position block, in
            // that order: UpdateCurrentCustomer runs before RegisterScanItem/RegisterScanCard, so
            // a lifecycle built from the announcement read IsNotScanned() == false ("already
            // scanned") for every line that was merely not-yet-registered, and guests scanned the
            // customer's whole bag the moment it was placed on the counter. Publish once the block
            // has run instead - at that point the bag is registered and "un-scanned" means the
            // item is waiting on the counter, exactly what the guest must show. Gated on the
            // paying-position transition (false -> true) so the frames where the method
            // early-returns for a customer already at the counter publish nothing.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Customer __instance, out bool __state)
            {
                var active = _active;
                if (active == null)
                {
                    __state = true;
                    return;
                }

                __state = RegisterInterop.Read(__instance, "m_IsAtPayingPosition") is bool atPaying
                    && atPaying;
                active._insideCustomerArrival = true;
            }

            [HarmonyPostfix]
            private static void Postfix(Customer __instance, bool __state)
            {
                var active = _active;
                if (active == null)
                {
                    return;
                }

                active._insideCustomerArrival = false;
                if (__state
                    || !(RegisterInterop.Read(__instance, "m_IsAtPayingPosition") is bool atPaying
                        && atPaying))
                {
                    return;
                }

                active.PublishCustomerLifecycle(
                    RegisterInterop.Key(RegisterInterop.CounterFor(__instance)));
            }

            [HarmonyFinalizer]
            private static void Finalizer()
            {
                var active = _active;
                if (active != null)
                {
                    active._insideCustomerArrival = false;
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCashierCounterState")]
        private static class StatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                _active?.PublishPhase(Key(__instance));
                if (__instance != null
                    && __instance.m_CashierCounterState == ECashierCounterState.TakingCash)
                {
                    // Entering TakingCash is where vanilla reveals the cash/card choice. Publish the
                    // prediction-free paid delta here so the guest applies the host's rolled amount
                    // and kind even when this state change came from applying the guest's own scan.
                    _active?.PublishPaidAmount(Key(__instance));
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCustomerPaidAmount")]
        private static class PaymentPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Key(__instance));
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "StartGivingChange")]
        private static class ChangePhasePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => _active?.PublishPhase(Key(__instance));
        }

        [HarmonyPatch(typeof(InteractableScanItem), "OnMouseButtonUp")]
        private static class ScanItemPatch
        {
            // The scan nulls the item's owning-customer reference, so capture the counter it
            // belongs to before vanilla runs; the postfix publishes against that stable counter.
            [HarmonyPrefix]
            private static void Prefix(InteractableScanItem __instance,
                out InteractableCashierCounter __state)
                => __state = RegisterInterop.FindCounterForScanItem(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableScanItem __instance,
                InteractableCashierCounter __state)
            {
                var counter = __state;
                var index = Key(counter);
                if (_active != null && index != 0 && counter?.m_CurrentCustomer != null)
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
            // Card counterpart of ScanItemPatch: capture the counter before the card's owning
            // customer is cleared.
            [HarmonyPrefix]
            private static void Prefix(InteractableCard3d __instance,
                out InteractableCashierCounter __state)
                => __state = RegisterInterop.FindCounterForCard(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableCard3d __instance,
                InteractableCashierCounter __state)
            {
                var counter = __state;
                var index = Key(counter);
                if (_active != null && index != 0 && counter?.m_CurrentCustomer != null)
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
                => _active?.PublishPhase(Key(__instance));
        }

        [HarmonyPatch(typeof(ShelfManager), "InitCashierCounter")]
        private static class CounterAddedPatch
        {
            // InitCashierCounter is static in both game baselines, so Harmony binds __instance to
            // null. Bind the counter argument positionally, matching CounterRemovedPatch.
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __0)
                => _active?.PublishCounterLifecycle(__0);
        }

        [HarmonyPatch(typeof(ShelfManager), "RemoveCashierCounter")]
        private static class CounterRemovedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __0) => _active?.Tombstone(__0);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnDestroyed")]
        private static class CounterDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance)
                => _active?.Tombstone(__instance);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "ForceResetCounter")]
        private static class ResetCounterPatch
        {
            // Day start (CEventPlayer_OnDayStarted) force-clears the counter's manned state via
            // ForceResetCounter, without OnPressEsc, so any station ownership - the host's -1 or
            // a guest's id - is stale afterwards. Publish a clean 0 so nobody keeps, or is locked
            // out of, a register no one is manning. Without the publish the ownership gate would
            // leave guests unable to claim the host's old counter for the rest of the day. Save
            // load recreates counters, which clears the host's station table via the scene
            // lifecycle, so only the day-start path needs to be handled here.
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
            {
                var active = _active;
                if (active == null || __instance == null || !active._context.InGame())
                {
                    return;
                }

                var index = Key(__instance);
                if (index == 0 || active.GetStation(index).Owner == 0)
                {
                    return;
                }

                active.GetStation(index).Owner = 0;
                active.ObservePublishing(index, __instance);
                active.PublishOwnership(index);
                RegisterHighlight.Clear(__instance);
            }
        }
    }
}
