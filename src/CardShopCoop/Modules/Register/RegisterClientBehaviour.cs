using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Predictive register input and application of keyed host operations.</summary>
    [ClientBehaviour]
    public sealed class RegisterClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "register";
        private static RegisterClientBehaviour _active;

        private sealed class LocalStation
        {
            public Customer Carrier;
            public int CustomerIndex = -1;
            public int CustomerGeneration;
            public uint CounterGeneration;
        }

        private sealed class CheckoutUndo
        {
            public InteractableCashierCounter Counter;
            public Customer Customer;
            public int ItemScannedCount;
            public float CustomerTotal;
            public double Total;
            public double Paid;
            public double Change;
            public bool UsingCard;
            public bool ChangeReady;
            public bool ChangeStarted;
            public bool TooMuchChange;
            public ECashierCounterState State;
            public bool CashActive;
            public bool HandingOverCash;
            public readonly List<int> ChangeCounts = new();
        }

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private int _applyingRemote;
        private int _applyingPrediction;
        private bool _applyingBaseline;
        private RegisterBaselineMessage _pendingBaseline;
        private readonly Dictionary<int, int> _owners = new();
        private readonly Dictionary<int, uint> _counterGenerations = new();
        private readonly Dictionary<int, LocalStation> _stations = new();
        private readonly Dictionary<string, RegisterDeltaMessage> _deferredDeltas = new();
        private readonly HashSet<int> _claims = new();

        internal static bool IsCarrier(Customer customer)
        {
            if (customer == null || _active == null)
            {
                return false;
            }

            foreach (var station in _active._stations.Values)
            {
                if (ReferenceEquals(station.Carrier, customer))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ForceExitManned()
        {
            var active = _active;
            if (active == null)
            {
                return;
            }

            foreach (var index in new List<int>(active._claims))
            {
                var counter = RegisterInterop.Counter(index);
                if (counter != null && counter.IsMannedByPlayer())
                {
                    active.PredictRelease(counter, index);
                }
            }
        }

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            _harmony = new Harmony("com.zwhit.cardshopcoop.register.client");
            Patch(typeof(ManningPatch));
            Patch(typeof(ExitPatch));
            Patch(typeof(ScanItemPatch));
            Patch(typeof(ScanCardPatch));
            Patch(typeof(PaymentPatch));
            Patch(typeof(CardPaymentPatch));
            Patch(typeof(AddChangePatch));
            Patch(typeof(RemoveChangePatch));
            Patch(typeof(FinishPatch));
            Patch(typeof(ScanCompletionPatch));
            SceneManager.sceneLoaded += OnSceneLoaded;
            NpcClientBehaviour.CustomerManagerReady += OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged += OnReadinessSignal;
            OnReadinessSignal();
        }

        private void Patch(Type patchType)
            => _harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        private bool IsSceneReady()
            => RegisterInterop.Counters != null
                && SceneRef<CustomerManager>.Get()?.GetCustomerList() != null;

        private void OnReadinessSignal()
        {
            if (_shutdown || !_context.InGame() || !IsSceneReady())
            {
                return;
            }

            TryApplyPending();
            foreach (var pair in new List<KeyValuePair<string, RegisterDeltaMessage>>(_deferredDeltas))
            {
                if (ApplyDelta(pair.Value))
                {
                    _deferredDeltas.Remove(pair.Key);
                }
            }
        }

        private void TryApplyPending()
        {
            if (_applyingBaseline)
            {
                // ApplyBaseline can synchronously raise the Npc customer
                // PoolChanged/ExistingCustomerChanged events (Attach/DetachExistingCustomer) and
                // this module subscribes to them. Re-entering here re-applied the still-pending
                // baseline and could recurse without bound.
                CoopPlugin.Log.LogDebug("[register] re-entrant baseline apply suppressed");
                return;
            }

            if (_pendingBaseline == null || !_context.InGame() || !IsSceneReady())
            {
                return;
            }

            var baseline = _pendingBaseline;
            _pendingBaseline = null;
            _applyingBaseline = true;
            _applyingRemote++;
            try
            {
                _owners.Clear();
                _counterGenerations.Clear();
                for (var i = 0; i < baseline.Counters.Count; i++)
                {
                    ApplyBaseline(baseline.Counters[i]);
                }
            }
            finally
            {
                _applyingRemote--;
                _applyingBaseline = false;
            }
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (_shutdown)
            {
                return;
            }

            foreach (var index in new List<int>(_stations.Keys))
            {
                ReleaseStation(index);
            }

            _stations.Clear();
            _owners.Clear();
            _counterGenerations.Clear();
            _claims.Clear();
            ClearDeferredDeltas();
            _pendingBaseline = null;
            OnReadinessSignal();
        }

        [MessageHandler(typeof(RegisterBaselineMessage))]
        private void HandleBaseline(MessageContext _, RegisterBaselineMessage message)
        {
            _pendingBaseline = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(RegisterDeltaMessage))]
        private void HandleDelta(MessageContext _, RegisterDeltaMessage message)
        {
            if (!ApplyDelta(message))
            {
                var key = DeltaKey(message);
                if (_deferredDeltas.TryGetValue(key, out var previous))
                    PredictionApi.ConfirmSuperseded(previous.PredictionId);
                _deferredDeltas[key] = message;
            }
        }

        private bool ApplyDelta(RegisterDeltaMessage message)
        {
            if (_shutdown || !_context.InGame())
            {
                return false;
            }

            if (message.Kind == RegisterDeltaKind.CounterLifecycle && !message.Exists)
            {
                PredictionApi.ApplyConfirmed(message.PredictionId,
                    () => ApplyCounterLifecycle(message));
                return true;
            }

            if (!IsSceneReady())
            {
                return false;
            }

            if (message.Kind != RegisterDeltaKind.Ownership
                && !HasCounterGeneration(message.Counter, message.CounterGeneration))
            {
                return false;
            }

            if (message.Kind == RegisterDeltaKind.CustomerLifecycle && message.HasCustomer
                && !HasCustomerCapacity(message.CustomerIndex))
            {
                return false;
            }

            if (RequiresCustomer(message.Kind) && !HasCustomer(message))
            {
                return false;
            }

            // A delta only follows a host-applied intent (rejections arrive as a prediction
            // rollback), so it confirms the guest's optimistic claim/release/scan. Undoing that
            // optimism first would replay it - e.g. releasing the register would re-man the
            // counter and yank the player back before the ownership delta clears it.
            var confirmedOwnPrediction = PredictionApi.IsPending(message.PredictionId);
            PredictionApi.ApplyConfirmed(message.PredictionId, () =>
            {
                _applyingRemote++;
                try
                {
                    ApplyDeltaCore(message, confirmedOwnPrediction);
                }
                finally
                {
                    _applyingRemote--;
                }
            });
            return true;
        }

        private static bool RequiresCustomer(RegisterDeltaKind kind)
            => kind == RegisterDeltaKind.Scan || kind == RegisterDeltaKind.Change;

        private bool HasCounterGeneration(int index, uint generation)
            => _counterGenerations.TryGetValue(index, out var current)
                && current == generation && RegisterInterop.Counter(index) != null;

        private bool HasCustomer(RegisterDeltaMessage message)
            => _stations.TryGetValue(message.Counter, out var station)
                && station.Carrier != null && station.CustomerIndex == message.CustomerIndex
                && station.CustomerGeneration == message.CustomerGeneration;

        private bool HasCustomerCapacity(int index)
        {
            var customers = SceneRef<CustomerManager>.Get()?.GetCustomerList();
            return customers != null && index >= 0 && index < customers.Count;
        }

        private void ApplyBaseline(RegisterBaselineCounter baseline)
        {
            _owners[baseline.Counter] = baseline.Owner;
            _counterGenerations[baseline.Counter] = baseline.CounterGeneration;
            var index = (int)baseline.Counter;
            if (!baseline.Exists || RegisterInterop.Counter(index) == null)
            {
                ReleaseStation(index);
                return;
            }

            var counter = RegisterInterop.Counter(index);
            if (!baseline.HasCustomer)
            {
                ReleaseStation(index);
                ApplyOwner(index, counter, baseline.Owner);
                return;
            }

            var station = GetOrBuildStation(index, baseline.CounterGeneration, baseline.CustomerIndex,
                baseline.CustomerGeneration, baseline.CustomerFemale, baseline.CharacterName,
                baseline.Lines, counter);
            if (station == null)
            {
                return;
            }

            ApplyOwner(index, counter, baseline.Owner);
            ApplyBaselineScanned(station.Carrier, baseline.Lines);
            ApplyPhase(counter, station.Carrier, baseline.State, baseline.UsingCard, baseline.Paid,
                baseline.Total, baseline.CustomerTotal, baseline.Change, baseline.ChangeReady,
                baseline.ChangeStarted, baseline.TooMuchChange);
            ApplyChangeStack(counter, baseline.State, baseline.UsingCard, baseline.ChangeItems);
        }

        private void ApplyDeltaCore(RegisterDeltaMessage message, bool confirmedOwnPrediction)
        {
            var index = (int)message.Counter;
            switch (message.Kind)
            {
                case RegisterDeltaKind.CounterLifecycle:
                    ApplyCounterLifecycle(message);
                    break;
                case RegisterDeltaKind.Ownership:
                    _owners[index] = message.Owner;
                    ApplyOwner(index, RegisterInterop.Counter(index), message.Owner);
                    break;
                case RegisterDeltaKind.CustomerLifecycle:
                    ApplyCustomerLifecycle(message);
                    break;
                case RegisterDeltaKind.Scan:
                    ApplyScan(message);
                    break;
                case RegisterDeltaKind.PhasePayment:
                    ApplyPhaseDelta(message);
                    break;
                case RegisterDeltaKind.Change:
                    ApplyChange(message, confirmedOwnPrediction);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message.Kind), message.Kind,
                        "Unknown register delta.");
            }
        }

        private void ApplyCounterLifecycle(RegisterDeltaMessage message)
        {
            _counterGenerations[message.Counter] = message.CounterGeneration;
            if (!message.Exists)
            {
                _owners[message.Counter] = 0;
                _claims.Remove(message.Counter);
                ReleaseStation(message.Counter);
            }
        }

        private void ApplyCustomerLifecycle(RegisterDeltaMessage message)
        {
            var index = (int)message.Counter;
            var counter = RegisterInterop.Counter(index);
            if (!message.HasCustomer)
            {
                var existing = _stations.TryGetValue(message.Counter, out var existingStation)
                    ? existingStation : null;
                ApplyPhase(counter, existing?.Carrier, message.State, message.UsingCard, message.Paid,
                    message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                    message.ChangeStarted, message.TooMuchChange);
                ReleaseStation(index);
                return;
            }

            var station = GetOrBuildStation(index, message.CounterGeneration, message.CustomerIndex,
                message.CustomerGeneration, message.CustomerFemale, message.CharacterName,
                message.Lines, counter);
            if (station == null)
            {
                return;
            }

            ApplyPhase(counter, station.Carrier, message.State, message.UsingCard, message.Paid,
                message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                message.ChangeStarted, message.TooMuchChange);
        }

        private void ApplyScan(RegisterDeltaMessage message)
        {
            if (!_stations.TryGetValue(message.Counter, out var station) || station.Carrier == null)
            {
                return;
            }

            var customer = station.Carrier;
            // The authoritative apply is idempotent: InteractableScanItem.OnMouseButtonUp nulls
            // its scan customer and unconditionally dereferences it on the next call, so a slot
            // that is already scanned (a duplicate/replayed host delta, or an item the client
            // optimistically scanned outside a pending prediction) must be skipped instead of
            // re-applied. A second call used to dereference that null and drop the whole session.
            if (message.IsCard)
            {
                var cards = customer.GetCardInBagList();
                if (message.Slot >= 0 && message.Slot < cards.Count)
                {
                    var card = cards[message.Slot];
                    if (card != null && card.IsNotScanned())
                    {
                        card.OnMouseButtonUp();
                    }
                }
            }
            else
            {
                var items = customer.GetItemInBagList();
                if (message.Slot >= 0 && message.Slot < items.Count)
                {
                    var scanItem = items[message.Slot]?.m_InteractableScanItem;
                    if (scanItem != null && scanItem.IsNotScanned())
                    {
                        scanItem.OnMouseButtonUp();
                    }
                }
            }

            var counter = RegisterInterop.Counter(message.Counter);
            ApplyPhase(counter, customer, message.State, message.UsingCard, message.Paid, message.Total,
                message.CustomerTotal, message.Change, message.ChangeReady, message.ChangeStarted,
                message.TooMuchChange);
        }

        private void ApplyPhaseDelta(RegisterDeltaMessage message)
        {
            var counter = RegisterInterop.Counter(message.Counter);
            var station = _stations.TryGetValue(message.Counter, out var value) ? value : null;
            ApplyPhase(counter, station?.Carrier, message.State, message.UsingCard, message.Paid,
                message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                message.ChangeStarted, message.TooMuchChange);
        }

        private void ApplyChange(RegisterDeltaMessage message, bool confirmedOwnPrediction)
        {
            if (confirmedOwnPrediction)
            {
                // This delta confirms a change the guest already applied optimistically. The
                // guest's counter may have newer clicks stacked on top, so re-applying the host's
                // absolute ChangeCount here would walk the stack back down (SetGivenAmount calls
                // OnRightMouseButtonUp) and the next delta would give the cash back out - the
                // register cash visibly replaying. The optimistic state is already correct; only
                // a change the guest did not predict may need applying.
                return;
            }

            var change = RegisterInterop.FindChange(RegisterInterop.Counter(message.Counter), message.Slot,
                message.IsCoin, message.Value);
            RegisterInterop.SetGivenAmount(change, message.ChangeCount);
            var counter = RegisterInterop.Counter(message.Counter);
            var station = _stations.TryGetValue(message.Counter, out var value) ? value : null;
            ApplyPhase(counter, station?.Carrier, message.State, message.UsingCard, message.Paid,
                message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                message.ChangeStarted, message.TooMuchChange);
        }

        private LocalStation GetOrBuildStation(int index, uint counterGeneration, int customerIndex,
            int customerGeneration, bool female, string characterName, List<RegisterLine> lines,
            InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return null;
            }

            if (_stations.TryGetValue(index, out var existing)
                && existing.CounterGeneration == counterGeneration
                && existing.CustomerIndex == customerIndex
                && existing.CustomerGeneration == customerGeneration
                && existing.Carrier != null)
            {
                EnsureNpcAttachment(customerIndex, customerGeneration, existing.Carrier);
                return existing;
            }

            ReleaseStation(index);
            var carrier = FindCarrier(customerIndex, female);
            if (carrier == null)
            {
                return null;
            }

            RegisterInterop.PrepareCarrier(carrier, counter);
            // A pooled customer can be reused while it still carries a previous customer's bag.
            // The Lines here are the full authoritative bag for this customer, so clear the
            // carrier first; otherwise the guest accumulates phantom items and its bag indices
            // no longer match the host's, so every scan the guest sends is rejected.
            var staleItems = carrier.GetItemInBagList()?.Count ?? 0;
            var staleCards = carrier.GetCardInBagList()?.Count ?? 0;
            if (staleItems > 0 || staleCards > 0)
            {
                CoopPlugin.Log.LogDebug("[register] cleared stale carrier bag counter=" + index
                    + " items=" + staleItems + " cards=" + staleCards + ".");
            }

            RegisterInterop.ReleaseContents(carrier);
            if (carrier.m_CharacterCustom != null)
            {
                carrier.m_CharacterCustom.CharacterName = characterName;
                carrier.m_CharacterCustom.Initialize();
            }

            if (counter.m_QueueStartPos != null)
            {
                carrier.transform.position = counter.m_QueueStartPos.position;
                var facing = counter.transform.position - carrier.transform.position;
                facing.y = 0f;
                carrier.transform.rotation = facing.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(facing, Vector3.up)
                    : counter.m_QueueStartPos.rotation;
            }

            EnsureNpcAttachment(customerIndex, customerGeneration, carrier);
            carrier.gameObject.SetActive(true);
            counter.UpdateCurrentCustomer(carrier);
            counter.SetPlsaticBagVisibility(true);
            counter.UpdateCashierCounterState(ECashierCounterState.ScanningItem);

            var itemSlot = 0;
            var cardSlot = 0;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.IsCard)
                {
                    RegisterInterop.SpawnCard(counter, carrier, line.Card, line.Price, cardSlot++);
                }
                else
                {
                    RegisterInterop.SpawnItem(counter, carrier, line.ItemType, line.Price, itemSlot++);
                }
            }

            var station = new LocalStation
            {
                Carrier = carrier,
                CustomerIndex = customerIndex,
                CustomerGeneration = customerGeneration,
                CounterGeneration = counterGeneration,
            };
            _stations[index] = station;
            return station;
        }

        private Customer FindCarrier(int sourceIndex, bool female)
        {
            var customers = SceneRef<CustomerManager>.Get()?.GetCustomerList();
            if (customers == null)
            {
                return null;
            }

            if (sourceIndex >= 0 && sourceIndex < customers.Count
                && IsAvailableCarrier(customers[sourceIndex], female))
            {
                return customers[sourceIndex];
            }

            Customer fallback = null;
            for (var i = 0; i < customers.Count; i++)
            {
                var candidate = customers[i];
                if (!IsAvailableCarrier(candidate, female))
                {
                    continue;
                }

                fallback ??= candidate;
                if (!candidate.gameObject.activeSelf)
                {
                    return candidate;
                }
            }

            return fallback;
        }

        private bool IsAvailableCarrier(Customer customer, bool female)
        {
            if (customer == null || customer.m_IsFemale != female || NpcClientBehaviour.IsExistingCustomer(customer))
            {
                return false;
            }

            foreach (var station in _stations.Values)
            {
                if (ReferenceEquals(station.Carrier, customer))
                {
                    return false;
                }
            }

            return true;
        }

        private static void EnsureNpcAttachment(int index, int generation, Customer carrier)
        {
            NpcClientBehaviour.SuppressedCustomer.Add(index);
            if (!NpcClientBehaviour.IsExistingCustomer(carrier))
            {
                NpcClientBehaviour.AttachExistingCustomer(index, generation, carrier);
            }
        }

        private static void ApplyBaselineScanned(Customer customer, List<RegisterLine> lines)
        {
            var items = customer.GetItemInBagList();
            var cards = customer.GetCardInBagList();
            var itemSlot = 0;
            var cardSlot = 0;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.IsCard)
                {
                    if (line.Scanned && cardSlot < cards.Count && cards[cardSlot].IsNotScanned())
                    {
                        cards[cardSlot].OnMouseButtonUp();
                    }

                    cardSlot++;
                }
                else
                {
                    if (line.Scanned && itemSlot < items.Count
                        && items[itemSlot].m_InteractableScanItem.IsNotScanned())
                    {
                        items[itemSlot].m_InteractableScanItem.OnMouseButtonUp();
                    }

                    itemSlot++;
                }
            }
        }

        private static void ApplyPhase(InteractableCashierCounter counter, Customer customer, byte state,
            bool usingCard, double paid, double total, double customerTotal, double change,
            bool changeReady, bool changeStarted, bool tooMuchChange)
        {
            if (counter == null)
            {
                return;
            }

            var wantedState = (ECashierCounterState)state;
            if (customer != null && wantedState == ECashierCounterState.GivingChange
                && counter.m_CashierCounterState != ECashierCounterState.GivingChange)
            {
                customer.OnCashTaken(usingCard);
            }

            RegisterInterop.Write(counter, "m_IsUsingCard", usingCard);
            RegisterInterop.Write(counter, "m_CustomerPaidAmount", paid);
            RegisterInterop.Write(counter, "m_TotalScannedItemCost", total);
            RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", change);
            RegisterInterop.Write(counter, "m_IsChangeReady", changeReady);
            RegisterInterop.Write(counter, "m_IsStartGivingChange", changeStarted);
            RegisterInterop.Write(counter, "m_TooMuchChangeGiven", tooMuchChange);
            if (customer != null)
            {
                RegisterInterop.Write(customer, "m_TotalScannedItemCost", (float)customerTotal);
                if (customer.m_CustomerCash != null)
                {
                    customer.m_CustomerCash.SetIsCard(usingCard);
                    customer.m_CustomerCash.gameObject.SetActive(
                        state == (byte)ECashierCounterState.TakingCash);
                }

                customer.m_Anim.SetBool("HandingOverCash",
                    state == (byte)ECashierCounterState.TakingCash);
            }

            counter.UpdateCashierCounterState(wantedState);
            RegisterInterop.CashScreen(counter)?.UpdateMoneyChangeAmount(changeReady, paid, total, change);
        }

        private static void ApplyChangeStack(InteractableCashierCounter counter, byte state, bool usingCard,
            List<RegisterChange> changes)
        {
            if (counter == null || state != (byte)ECashierCounterState.GivingChange || usingCard)
            {
                return;
            }

            var money = counter.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                money[i]?.ResetAmountGiven();
            }

            for (var i = 0; changes != null && i < changes.Count; i++)
            {
                var wanted = changes[i];
                var change = RegisterInterop.FindChange(counter, wanted.Slot, wanted.IsCoin, wanted.Value);
                if (change == null)
                {
                    continue;
                }

                for (var count = 0; count < wanted.Count; count++)
                {
                    change.OnMouseButtonUp();
                }
            }
        }

        private void ApplyOwner(int index, InteractableCashierCounter counter, int owner)
        {
            var localOwner = owner > 0 && PresenceApi.IsLocalConnection(owner);
            if (localOwner)
            {
                _claims.Add(index);
                if (counter != null && !counter.IsMannedByPlayer())
                {
                    counter.OnMouseButtonUp();
                }
            }
            else
            {
                _claims.Remove(index);
                if (counter != null && counter.IsMannedByPlayer())
                {
                    counter.OnPressEsc();
                }
            }
        }

        private LocalStation Station(InteractableCashierCounter counter, out int index)
        {
            index = RegisterInterop.Index(counter);
            return index >= 0 && _stations.TryGetValue(index, out var station) ? station : null;
        }

        private CheckoutUndo CaptureUndo(InteractableCashierCounter counter, Customer customer)
        {
            var undo = new CheckoutUndo
            {
                Counter = counter,
                Customer = customer,
                ItemScannedCount = Convert.ToInt32(RegisterInterop.Read(customer, "m_ItemScannedCount") ?? 0),
                CustomerTotal = Convert.ToSingle(RegisterInterop.Read(customer, "m_TotalScannedItemCost") ?? 0f),
                Total = RegisterInterop.Total(counter),
                Paid = RegisterInterop.Paid(counter),
                Change = RegisterInterop.Change(counter),
                UsingCard = RegisterInterop.IsUsingCard(counter),
                ChangeReady = RegisterInterop.IsChangeReady(counter),
                ChangeStarted = RegisterInterop.ChangeStarted(counter),
                TooMuchChange = RegisterInterop.TooMuchChange(counter),
                State = counter.m_CashierCounterState,
                CashActive = customer?.m_CustomerCash != null && customer.m_CustomerCash.gameObject.activeSelf,
                HandingOverCash = customer?.m_Anim != null && customer.m_Anim.GetBool("HandingOverCash"),
            };
            var money = counter?.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                undo.ChangeCounts.Add(RegisterInterop.GivenAmount(money[i]));
            }

            return undo;
        }

        private void UndoCheckout(CheckoutUndo undo)
        {
            var counter = undo.Counter;
            var customer = undo.Customer;
            if (counter == null || customer == null)
            {
                return;
            }

            _applyingRemote++;
            try
            {
                if (!ReferenceEquals(counter.m_CurrentCustomer, customer))
                {
                    RestoreContents(counter, customer);
                    counter.UpdateCurrentCustomer(customer);
                    counter.SetPlsaticBagVisibility(true);
                }

                RegisterInterop.Write(customer, "m_IsActive", true);
                RegisterInterop.Write(customer, "m_HasCheckedOut", false);
                RegisterInterop.Write(customer, "m_ItemScannedCount", undo.ItemScannedCount);
                RegisterInterop.Write(customer, "m_TotalScannedItemCost", undo.CustomerTotal);
                RegisterInterop.Write(counter, "m_CustomerPaidAmount", undo.Paid);
                RegisterInterop.Write(counter, "m_TotalScannedItemCost", undo.Total);
                RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", undo.Change);
                RegisterInterop.Write(counter, "m_IsUsingCard", undo.UsingCard);
                RegisterInterop.Write(counter, "m_IsChangeReady", undo.ChangeReady);
                RegisterInterop.Write(counter, "m_IsStartGivingChange", undo.ChangeStarted);
                RegisterInterop.Write(counter, "m_TooMuchChangeGiven", undo.TooMuchChange);
                customer.m_CustomerCash?.SetIsCard(undo.UsingCard);
                if (customer.m_CustomerCash != null)
                {
                    customer.m_CustomerCash.gameObject.SetActive(undo.CashActive);
                }

                customer.m_Anim.SetBool("HandingOverCash", undo.HandingOverCash);
                counter.UpdateCashierCounterState(undo.State);
                // Rolling back the moment the customer handed over a card restores the counter
                // to a pre-giving-change phase, but vanilla only restores the credit card
                // machine inside OnPressSpaceBar (which ran before the rollback). If the machine
                // is left out at the player, the authoritative phase's re-entry into giving
                // change captures that moved spot as the machine's "original" and the phone is
                // stuck at the number pad forever. Put it back before the re-apply runs.
                if (undo.UsingCard && undo.State != ECashierCounterState.GivingChange)
                {
                    RegisterInterop.RestoreCreditCardMachine(counter);
                }

                RestoreChangeCounts(counter, undo.ChangeCounts);
                RebuildCashScreen(counter, customer);
                RegisterInterop.CashScreen(counter)?.UpdateMoneyChangeAmount(undo.ChangeReady,
                    undo.Paid, undo.Total, undo.Change);
            }
            finally
            {
                _applyingRemote--;
            }
        }

        private static void RestoreContents(InteractableCashierCounter counter, Customer customer)
        {
            var itemSlot = 0;
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                item.transform.SetParent(counter.transform);
                item.transform.position = counter.m_CustomerPlaceItemPos.position
                    + counter.m_CustomerPlaceItemPos.forward * (-0.025f * (itemSlot++ % 8));
                item.gameObject.SetActive(true);
                item.m_Collider.enabled = true;
                var rigidbody = RegisterInterop.EnsureItemRigidbody(item);
                if (rigidbody != null)
                {
                    rigidbody.isKinematic = false;
                }

                item.m_InteractableScanItem.enabled = true;
                item.m_InteractableScanItem.RegisterScanItem(customer, counter.m_ScannedItemLerpPos);
            }

            var cardSlot = 0;
            var cards = customer.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null)
                {
                    continue;
                }

                card.transform.SetParent(counter.transform);
                card.transform.position = counter.m_CustomerPlaceItemPos.position
                    + counter.m_CustomerPlaceItemPos.forward * (-0.035f * (cardSlot++ % 8));
                card.gameObject.SetActive(true);
                card.m_Collider.enabled = true;
                var rigidbody = RegisterInterop.EnsureCardRigidbody(card);
                if (rigidbody != null)
                {
                    rigidbody.isKinematic = false;
                }

                card.RegisterScanCard(customer, counter.m_ScannedItemLerpPos);
            }
        }

        private static void RestoreChangeCounts(InteractableCashierCounter counter, List<int> counts)
        {
            var money = counter.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                money[i]?.ResetAmountGiven();
                for (var count = 0; counts != null && i < counts.Count && count < counts[i]; count++)
                {
                    money[i].OnMouseButtonUp();
                }
            }
        }

        private static void RebuildCashScreen(InteractableCashierCounter counter, Customer customer)
        {
            var screen = RegisterInterop.CashScreen(counter);
            if (screen == null)
            {
                return;
            }

            screen.ResetCounter();
            var total = 0d;
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && item.m_InteractableScanItem != null
                    && !item.m_InteractableScanItem.IsNotScanned())
                {
                    total += item.GetCurrentPrice();
                    screen.OnItemScanned(item.GetCurrentPrice(), item.GetItemType(), total);
                }
            }

            var cards = customer.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                if (card != null && !card.IsNotScanned())
                {
                    total += card.GetCurrentPrice();
                    screen.OnCardScanned(card.GetCurrentPrice(),
                        card.m_Card3dUI.m_CardUI.GetCardData(), total);
                }
            }
        }

        private void PredictClaim(InteractableCashierCounter counter, int index)
        {
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.Claim,
                }),
                () =>
                {
                    _claims.Add(index);
                    _owners[index] = 0;
                    WithPrediction(() => counter.OnMouseButtonUp());
                },
                () =>
                {
                    _claims.Remove(index);
                    _owners[index] = 0;
                    if (counter.IsMannedByPlayer())
                    {
                        WithPrediction(() => counter.OnPressEsc());
                    }
                });
        }

        private void PredictRelease(InteractableCashierCounter counter, int index)
        {
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.Release,
                }),
                () =>
                {
                    _claims.Remove(index);
                    _owners[index] = 0;
                    WithPrediction(() => counter.OnPressEsc());
                },
                () =>
                {
                    _claims.Add(index);
                    _owners[index] = 0;
                    if (!counter.IsMannedByPlayer())
                    {
                        WithPrediction(() => counter.OnMouseButtonUp());
                    }
                });
        }

        private void PredictScan(InteractableCashierCounter counter, int index, int slot, bool card)
        {
            var customer = counter.m_CurrentCustomer;
            CoopPlugin.Log.LogInfo("[register] predicting " + (card ? "card" : "item")
                + " scan counter=" + index + " slot=" + slot + ".");
            var undo = CaptureUndo(counter, customer);
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = card ? RegisterIntentKind.ScanCard : RegisterIntentKind.ScanItem,
                    Slot = slot,
                }),
                () => WithPrediction(() =>
                {
                    if (card)
                    {
                        customer.GetCardInBagList()[slot].OnMouseButtonUp();
                    }
                    else
                    {
                        customer.GetItemInBagList()[slot].m_InteractableScanItem.OnMouseButtonUp();
                    }
                }),
                () => WithPrediction(() =>
                {
                    // Unscan only the predicted slot, then restore the checkout scalars the
                    // snapshot recorded. UndoCheckout deliberately leaves the carrier's already
                    // accepted scans alone, so without this the reconcile re-scanned an item that
                    // still reported scanned and the session dropped on a null scan customer.
                    if (card)
                    {
                        var cards = customer.GetCardInBagList();
                        if (slot >= 0 && slot < cards.Count)
                        {
                            RegisterInterop.UnscanCard(counter, customer, cards[slot]);
                        }
                    }
                    else
                    {
                        var items = customer.GetItemInBagList();
                        if (slot >= 0 && slot < items.Count)
                        {
                            RegisterInterop.UnscanItem(counter, customer, items[slot]);
                        }
                    }

                    UndoCheckout(undo);
                }));
        }

        private void PredictPayment(InteractableCashierCounter counter, int index,
            InteractableCustomerCash cash)
        {
            var undo = CaptureUndo(counter, counter.m_CurrentCustomer);
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.TakePayment,
                    IsCard = cash.m_IsCard,
                }),
                () => WithPrediction(cash.OnMouseButtonUp),
                () => UndoCheckout(undo));
        }

        private void PredictCardPayment(InteractableCashierCounter counter, int index, double value)
        {
            var undo = CaptureUndo(counter, counter.m_CurrentCustomer);
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.CardPayment,
                    Value = value,
                }),
                () => WithPrediction(() => counter.EvaluateCreditCard(value)),
                () => UndoCheckout(undo));
        }

        private void PredictChange(InteractableCounterMoneyChange change, bool takeBack, int index)
        {
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.Change,
                    Slot = change.m_Index,
                    IsCoin = change.m_IsCoin,
                    TakeBack = takeBack,
                    Value = change.m_ValueDouble,
                }),
                () => WithPrediction(() =>
                {
                    if (takeBack)
                    {
                        change.OnRightMouseButtonUp();
                    }
                    else
                    {
                        change.OnMouseButtonUp();
                    }
                }),
                () => WithPrediction(() =>
                {
                    if (takeBack)
                    {
                        change.OnMouseButtonUp();
                    }
                    else
                    {
                        change.OnRightMouseButtonUp();
                    }
                }));
        }

        private void PredictComplete(InteractableCashierCounter counter, int index)
        {
            var undo = CaptureUndo(counter, counter.m_CurrentCustomer);
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.Complete,
                }),
                () => WithPrediction(counter.OnPressSpaceBar),
                () => UndoCheckout(undo));
        }

        private void WithPrediction(Action action)
        {
            _applyingPrediction++;
            try
            {
                action();
            }
            finally
            {
                _applyingPrediction--;
            }
        }

        private void Send(RegisterIntentMessage message)
        {
            if (_shutdown || !_joined || !_context.InGame())
            {
                return;
            }

            _context.Send(1, message);
        }

        private void ReleaseStation(int index)
        {
            var counter = RegisterInterop.Counter(index);
            if (!_stations.TryGetValue(index, out var station))
            {
                return;
            }

            if (station.Carrier != null)
            {
                station.Carrier.m_CustomerCash?.gameObject.SetActive(false);
                station.Carrier.gameObject.SetActive(false);
                RegisterInterop.ReleaseContents(station.Carrier);
                NpcClientBehaviour.DetachExistingCustomer(station.CustomerIndex, station.Carrier);
            }

            if (counter != null)
            {
                RegisterInterop.ResetCheckoutVisuals(counter);
            }

            _stations.Remove(index);
        }

        private static string DeltaKey(RegisterDeltaMessage message)
        {
            if (message.Kind == RegisterDeltaKind.Change)
            {
                return message.Counter + ":" + message.CounterGeneration + ":change:"
                    + message.Slot + ":" + message.IsCoin + ":"
                    + message.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (message.Kind == RegisterDeltaKind.Scan)
            {
                return message.Counter + ":" + message.CounterGeneration + ":scan:"
                    + message.CustomerGeneration + ":" + message.Slot + ":" + message.IsCard;
            }

            return message.Counter + ":" + (byte)message.Kind + ":" + message.CounterGeneration
                + ":" + message.CustomerGeneration;
        }

        private void ClearDeferredDeltas()
        {
            foreach (var delta in _deferredDeltas.Values)
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            _deferredDeltas.Clear();
        }

        private void ShutdownContents()
        {
            foreach (var index in new List<int>(_claims))
            {
                var counter = RegisterInterop.Counter(index);
                if (counter != null && counter.IsMannedByPlayer())
                {
                    WithPrediction(counter.OnPressEsc);
                }
            }

            foreach (var index in new List<int>(_stations.Keys))
            {
                ReleaseStation(index);
            }

            _stations.Clear();
            _claims.Clear();
            _owners.Clear();
            _counterGenerations.Clear();
            ClearDeferredDeltas();
            _pendingBaseline = null;
            _joined = false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ShutdownContents();
            NpcClientBehaviour.CustomerManagerReady -= OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged -= OnReadinessSignal;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnMouseButtonUp")]
        private static class ManningPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var index = RegisterInterop.Index(__instance);
                if (index < 0 || !client._joined || !client._context.InGame()
                    || client._owners.TryGetValue(index, out var owner) && owner > 0
                        && !PresenceApi.IsLocalConnection(owner))
                {
                    return true;
                }

                client.PredictClaim(__instance, index);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressEsc")]
        private static class ExitPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var index = RegisterInterop.Index(__instance);
                if (index < 0 || !client._claims.Contains(index))
                {
                    return true;
                }

                client.PredictRelease(__instance, index);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableScanItem), "OnMouseButtonUp")]
        private static class ScanItemPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableScanItem __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var counter = RegisterInterop.FindCounterForScanItem(__instance);
                var station = client.Station(counter, out var index);
                if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
                {
                    return true;
                }

                if (!client._claims.Contains(index))
                {
                    CoopPlugin.Log.LogWarning("[register] item scan blocked: counter=" + index
                        + " is owned by another peer.");
                    return false;
                }

                var slot = client.ItemSlot(__instance, index);
                if (slot < 0)
                {
                    CoopPlugin.Log.LogWarning("[register] item scan blocked: the item is not in "
                        + "counter " + index + "'s bag.");
                    return false;
                }

                client.PredictScan(counter, index, slot, false);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableCard3d), "OnMouseButtonUp")]
        private static class ScanCardPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCard3d __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var counter = RegisterInterop.FindCounterForCard(__instance);
                var station = client.Station(counter, out var index);
                if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
                {
                    return true;
                }

                if (!client._claims.Contains(index))
                {
                    CoopPlugin.Log.LogWarning("[register] card scan blocked: counter=" + index
                        + " is owned by another peer.");
                    return false;
                }

                var slot = client.CardSlot(__instance, index);
                if (slot < 0)
                {
                    CoopPlugin.Log.LogWarning("[register] card scan blocked: the card is not in "
                        + "counter " + index + "'s bag.");
                    return false;
                }

                client.PredictScan(counter, index, slot, true);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableCustomerCash), "OnMouseButtonUp")]
        private static class PaymentPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCustomerCash __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var counter = RegisterInterop.FindCounterForCash(__instance);
                var station = client.Station(counter, out var index);
                if (station == null || !client._claims.Contains(index))
                {
                    return false;
                }

                client.PredictPayment(counter, index, __instance);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "EvaluateCreditCard")]
        private static class CardPaymentPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance, double value)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var station = client.Station(__instance, out var index);
                if (station == null || !client._claims.Contains(index))
                {
                    return true;
                }

                client.PredictCardPayment(__instance, index, value);
                return false;
            }
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnMouseButtonUp")]
        private static class AddChangePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCounterMoneyChange __instance)
                => SendChange(__instance, false);
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp")]
        private static class RemoveChangePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCounterMoneyChange __instance)
                => SendChange(__instance, true);
        }

        private static bool SendChange(InteractableCounterMoneyChange change, bool takeBack)
        {
            var client = _active;
            if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
            {
                return true;
            }

            var counter = change?.m_CashierCounter;
            var station = client.Station(counter, out var index);
            if (station == null)
            {
                return true;
            }

            if (!client._claims.Contains(index))
            {
                return false;
            }

            // Match the game's own guards so a predicted click is always one the optimistic apply
            // will really perform. Otherwise a confirming delta could be skipped locally while the
            // host applied it, leaving the guest short.
            if (counter == null || !counter.IsGivingChange())
            {
                return true;
            }

            var given = RegisterInterop.GivenAmount(change);
            if (takeBack ? given <= 0 : given >= 100)
            {
                return true;
            }

            client.PredictChange(change, takeBack, index);
            return false;
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressSpaceBar")]
        private static class FinishPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance)
            {
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var station = client.Station(__instance, out var index);
                if (station == null || !client._claims.Contains(index))
                {
                    return true;
                }

                client.PredictComplete(__instance, index);
                return false;
            }
        }

        [HarmonyPatch(typeof(Customer), "EvaluateFinishScanItem")]
        private static class ScanCompletionPatch
        {
            // EvaluateFinishScanItem rolls the cash-vs-card choice and the amount with the
            // local RNG, then flips the counter into TakingCash. On a client that prediction
            // would disagree with the host's independent roll, so the guest could see cash,
            // click it, and then watch it turn into a card while the host rejected the
            // mismatched payment. The host publishes the authoritative type/amount via the
            // phase delta and ApplyPhase performs the same transition, so the client never
            // needs to (and must not) run it.
            [HarmonyPrefix]
            private static bool Prefix()
                => _active == null;
        }

        private int ItemSlot(InteractableScanItem item, int counterIndex)
        {
            var items = RegisterInterop.Counter(counterIndex)?.m_CurrentCustomer?.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                if (items[i]?.m_InteractableScanItem == item)
                {
                    return i;
                }
            }

            return -1;
        }

        private int CardSlot(InteractableCard3d card, int counterIndex)
        {
            var cards = RegisterInterop.Counter(counterIndex)?.m_CurrentCustomer?.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                if (cards[i] == card)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
