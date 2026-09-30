using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
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
            // Signed wallet delta the completed checkout queued in vanilla OnPressSpaceBar
            // (positive = sale income, negative = change paid out). Captured at observation so a
            // rejection can reverse exactly what the local vanilla event did.
            public double WalletDelta;
            // True while that delta is currently applied to the local wallet. Undo reverses and
            // clears it; a follower replay that re-completes sets it again.
            public bool WalletApplied;
            public readonly List<int> ChangeCounts = new();
        }

        private sealed class ActionCapture
        {
            public InteractableCashierCounter Counter;
            public Customer Customer;
            public int Index;
            public CheckoutUndo Undo;
            public int Slot;
            public bool IsCard;
            public InteractableCustomerCash Cash;
            public double Value;
            public InteractableCounterMoneyChange Change;
            public bool TakeBack;
            public int GivenBefore;
        }

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private int _applyingRemote;
        private int _applyingPrediction;
        private bool _applyingBaseline;
        private bool _evaluatingCreditCard;
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
                    // Let the game perform the exit; the ExitPatch observes it and registers the
                    // single release prediction through the normal path.
                    counter.OnPressEsc();
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
            Patch(typeof(OutlinePatch));
            Patch(typeof(ExitPatch));
            Patch(typeof(ScanItemPatch));
            Patch(typeof(ScanCardPatch));
            Patch(typeof(PaymentPatch));
            Patch(typeof(CardPaymentPatch));
            Patch(typeof(AddChangePatch));
            Patch(typeof(RemoveChangePatch));
            Patch(typeof(FinishPatch));
            Patch(typeof(FinishScanPatch));
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
                    CoopPlugin.Log.LogInfo("[register] applied deferred delta kind="
                        + pair.Value.Kind + " counter=" + pair.Value.Counter + " pred="
                        + pair.Value.PredictionId);
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
                CoopPlugin.Log.LogInfo("[register] deferring delta kind=" + message.Kind
                    + " counter=" + message.Counter + " counterGen=" + message.CounterGeneration
                    + " customer=" + message.CustomerIndex + " customerGen="
                    + message.CustomerGeneration + " pred=" + message.PredictionId);
                var key = DeltaKey(message);
                if (_deferredDeltas.TryGetValue(key, out var previous))
                    PredictionApi.Ack(previous.PredictionId);
                _deferredDeltas[key] = message;
            }
        }

        private bool ApplyDelta(RegisterDeltaMessage message)
        {
            if (_shutdown || !_context.InGame())
            {
                return false;
            }

            if (message.Kind == RegisterDeltaKind.CounterLifecycle)
            {
                // Host-driven lifecycle: a tombstone (Exists=false) releases the counter, and an
                // add (Exists=true) adopts the host generation so later deltas for a counter
                // placed mid-session are accepted instead of rejected as unknown. No client
                // optimistic run confirms this, so the apply runs for every peer.
                PredictionApi.AckOrApply(message.PredictionId,
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
            // rollback), so it confirms the guest's optimistic claim/release/scan. Capture that
            // BEFORE Confirm retires the prediction, so the Scan/Change apply can tell whether it
            // must preserve the optimistic totals (see ApplyScan/ApplyChange); AckOrApply computed
            // the flag and then retired-and-returned, leaving it dead. The authoritative apply must
            // still run even for the actor's own prediction because it carries host-computed fields
            // (generations, phase totals). Undoing that optimism first would replay it - e.g.
            // releasing the register would re-man the counter and yank the player back before the
            // ownership delta clears it.
            var confirmedOwnPrediction = PredictionApi.IsPending(message.PredictionId);
            PredictionApi.Confirm(message.PredictionId, () =>
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
            => kind == RegisterDeltaKind.Scan || kind == RegisterDeltaKind.Change
                || kind == RegisterDeltaKind.PaidAmount;

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
            if (IsRemoteOwner(baseline.Owner))
            {
                // A late join can land while the player already aims at this counter, so clear a
                // highlight that started before the authoritative owner was known.
                RegisterHighlight.Clear(RegisterInterop.Counter(index));
            }

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
                baseline.CustomerGeneration, baseline.CharacterName,
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
                    ApplyOwnership(index, message.Owner);
                    break;
                case RegisterDeltaKind.CustomerLifecycle:
                    ApplyCustomerLifecycle(message);
                    break;
                case RegisterDeltaKind.Scan:
                    ApplyScan(message, confirmedOwnPrediction);
                    break;
                case RegisterDeltaKind.PhasePayment:
                    ApplyPhaseDelta(message);
                    break;
                case RegisterDeltaKind.PaidAmount:
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
                message.CustomerGeneration, message.CharacterName,
                message.Lines, counter);
            if (station == null)
            {
                return;
            }

            ApplyPhase(counter, station.Carrier, message.State, message.UsingCard, message.Paid,
                message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                message.ChangeStarted, message.TooMuchChange);
        }

        private void ApplyScan(RegisterDeltaMessage message, bool confirmedOwnPrediction)
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

            if (confirmedOwnPrediction)
            {
                // This delta confirms a scan this client already applied optimistically, and its
                // absolute Total is the host's state only up to THIS scan. The client may have
                // newer scans the host has not acknowledged yet, so writing the host's total here
                // walked the register's displayed amount backwards - a player could read a stale
                // total and enter the wrong card amount. The optimistic scan already advanced both
                // the counter's and the customer's totals; keep them and let the host's later
                // phase deltas reconcile.
                return;
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
                message.IsCoin);
            RegisterInterop.SetGivenAmount(change, message.ChangeCount);
            var counter = RegisterInterop.Counter(message.Counter);
            var station = _stations.TryGetValue(message.Counter, out var value) ? value : null;
            ApplyPhase(counter, station?.Carrier, message.State, message.UsingCard, message.Paid,
                message.Total, message.CustomerTotal, message.Change, message.ChangeReady,
                message.ChangeStarted, message.TooMuchChange);
        }

        private LocalStation GetOrBuildStation(int index, uint counterGeneration, int customerIndex,
            int customerGeneration, string characterName, List<RegisterLine> lines,
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
            var carrier = FindCarrier(customerIndex);
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

        /// <summary>Resolves the carrier from the host's authoritative customer list index. The Npc
        /// module keeps the guest's customer pool index-aligned with the host and grows it with the
        /// authoritative gender, so the slot object at that index is the correct customer; scanning
        /// the pool for a gender match could bind the wrong (or a second) customer to the identity.
        /// </summary>
        private static Customer FindCarrier(int customerIndex)
        {
            var customers = SceneRef<CustomerManager>.Get()?.GetCustomerList();
            if (customers == null || customerIndex < 0 || customerIndex >= customers.Count)
            {
                return null;
            }

            return customers[customerIndex];
        }

        private static void EnsureNpcAttachment(int index, int generation, Customer carrier)
        {
            NpcClientBehaviour.SuppressedCustomer.Add(index);
            // Attach with the host's authoritative generation, refreshing an existing mirror when
            // the generation advanced (a reused pooled customer) instead of leaving the stale one.
            if (!NpcClientBehaviour.IsExistingCustomer(carrier)
                || !NpcClientBehaviour.TryGetCustomerGeneration(index, out var current)
                || current != generation)
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

            var diagnosticBefore = RegisterInterop.Total(counter);
            var diagnosticBeforeCustomer = customer == null ? 0d
                : Convert.ToDouble(RegisterInterop.Read(customer, "m_TotalScannedItemCost") ?? 0f);
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
            if (Math.Abs(total - diagnosticBefore) > 0.005
                || Math.Abs(customerTotal - diagnosticBeforeCustomer) > 0.005)
            {
                CoopPlugin.Log.LogInfo("[register] phase counter=" + RegisterInterop.Index(counter)
                    + " state=" + wantedState + " card=" + usingCard
                    + " total=" + diagnosticBefore.ToString("F3") + "->" + total.ToString("F3")
                    + " customer=" + diagnosticBeforeCustomer.ToString("F3")
                    + "->" + customerTotal.ToString("F3")
                    + " paid=" + paid.ToString("F3") + " change=" + change.ToString("F3")
                    + " ready=" + changeReady + " started=" + changeStarted);
            }
        }

        private static void ApplyChangeStack(InteractableCashierCounter counter, byte state, bool usingCard,
            List<RegisterChange> changes)
        {
            if (counter == null || state != (byte)ECashierCounterState.GivingChange || usingCard)
            {
                return;
            }

            var money = counter.m_InteractableCounterMoneyChangeList;
            var counts = new int[money?.Count ?? 0];
            for (var i = 0; changes != null && i < changes.Count; i++)
            {
                // RegisterChange.Slot is the denomination's m_Index, which is not necessarily its
                // list position, so resolve it the same way FindChange does.
                for (var m = 0; m < counts.Length; m++)
                {
                    if (money[m] != null && money[m].m_Index == changes[i].Slot)
                    {
                        counts[m] = changes[i].Count;
                        break;
                    }
                }
            }

            SetChangeStack(counter, counts);
        }

        /// <summary>Rebuilds the counter's given-change stack from the authoritative per-
        /// denomination counts. <c>ResetAmountGiven</c> clears each denomination's count and its
        /// stack visuals but does NOT roll <c>m_CurrentMoneyChangeValue</c> or the coin/bill added
        /// counters back, so replaying the clicks on top of the old aggregate counted the whole
        /// change twice. The guest's displayed "given" (and its readiness) then drifted from the
        /// host until an authoritative phase overwrote it. Zero the derived counters first so the
        /// replay reconstructs the exact stack.</summary>
        private static void SetChangeStack(InteractableCashierCounter counter, IList<int> counts)
        {
            var money = counter.m_InteractableCounterMoneyChangeList;
            if (money == null)
            {
                return;
            }

            RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", 0d);
            RegisterInterop.Write(counter, "m_ChangeMoneyAddedCount", 0);
            RegisterInterop.Write(counter, "m_ChangeCoinAddedCount", 0);
            for (var i = 0; i < money.Count; i++)
            {
                money[i]?.ResetAmountGiven();
            }

            for (var i = 0; i < money.Count; i++)
            {
                var count = counts != null && i < counts.Count ? counts[i] : 0;
                for (var n = 0; n < count; n++)
                {
                    money[i]?.OnMouseButtonUp();
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

        /// <summary>True when another peer - the host (owner -1) or another client - is the
        /// authoritative owner of this register. The local claim is a no-op state and is never
        /// remote.</summary>
        private bool IsRemoteOwned(int index)
            => _owners.TryGetValue(index, out var owner) && IsRemoteOwner(owner);

        private static bool IsRemoteOwner(int owner)
            => owner != 0 && !PresenceApi.IsLocalConnection(owner);

        /// <summary>Keeps the local interaction highlight honest across an authoritative ownership
        /// change: a register that just became someone else's stops advertising itself. No
        /// restore is needed when one frees again - a detached counter is raycast again on the
        /// next frame and highlights normally if it is still the aimed target.</summary>
        private void RefreshOwnershipHighlight(int index, int owner)
        {
            if (IsRemoteOwner(owner))
            {
                RegisterHighlight.Clear(RegisterInterop.Counter(index));
            }
        }

        private void ApplyOwnership(int index, int owner)
        {
            _owners[index] = owner;
            ApplyOwner(index, RegisterInterop.Counter(index), owner);
            RefreshOwnershipHighlight(index, owner);
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
                // UndoCheckout restores checkout state but not the wallet. The guest's local
                // checkout ran vanilla and queued its AddCoin/ReduceCoin (the Hud observer is
                // suppressed by EconomyActionScope, so it was never forwarded), so a rejected
                // completion must reverse that same delta here or the mirror stays wrong until
                // the next authoritative Hud delta. The flag tracks whether this prediction's
                // delta is currently applied, so a follower replay that does not re-complete (its
                // change was undone) does not cause a later rejection to refund twice.
                if (undo.WalletApplied)
                {
                    ReverseWallet(undo.WalletDelta);
                    undo.WalletApplied = false;
                }

                RebuildCashScreen(counter, customer);
                RegisterInterop.CashScreen(counter)?.UpdateMoneyChangeAmount(undo.ChangeReady,
                    undo.Paid, undo.Total, undo.Change);
            }
            finally
            {
                _applyingRemote--;
            }
        }

        /// <summary>The signed wallet delta vanilla's completed checkout queues: the shop's net
        /// income from the sale (positive) or the change it pays out (negative), rounded exactly as
        /// <c>InteractableCashierCounter.OnPressSpaceBar</c> does before queueing its coin event.
        /// A card payment has no change, so <paramref name="change"/> is zero and
        /// <paramref name="paid"/> is the charged amount.</summary>
        private static double CheckoutWalletDelta(double paid, double change)
        {
            var value = paid - change;
            return GameInstance.GetCurrencyConversionRate() > 1f
                ? (double)(float)Math.Round(value, 3, MidpointRounding.AwayFromZero)
                : (double)(float)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Reverses a completed checkout's wallet delta through the game's own coin events
        /// (a refund of income, or a charge-back of change the counter paid out). Only ever runs
        /// while a prediction is being reconciled, so the Hud economy observer does not forward it
        /// as a second contribution.</summary>
        private static void ReverseWallet(double delta)
        {
            if (delta > 0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)delta));
            }
            else if (delta < -0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)(0d - delta), true));
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
            SetChangeStack(counter, counts);
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

        private void ObserveClaim(InteractableCashierCounter counter, int index)
        {
            // The game already manned the counter (the hook is a postfix). Record the local claim so
            // scans can proceed, then register one post-hoc prediction: the game owns the man, and
            // only a rejection unmounts through OnPressEsc.
            if (_claims.Contains(index))
            {
                return;
            }

            _claims.Add(index);
            _owners[index] = 0;
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)index,
                    Kind = RegisterIntentKind.Claim,
                }),
                () => WithPrediction(counter.OnMouseButtonUp),
                () =>
                {
                    _claims.Remove(index);
                    _owners[index] = 0;
                    if (counter.IsMannedByPlayer())
                    {
                        WithPrediction(counter.OnPressEsc);
                    }
                });
        }

        private void ObserveRelease(InteractableCashierCounter counter, int index)
        {
            // The game already un-manned the counter (postfix).
            _claims.Remove(index);
            _owners[index] = 0;
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
                    if (counter.IsMannedByPlayer())
                    {
                        WithPrediction(counter.OnPressEsc);
                    }
                },
                () =>
                {
                    _claims.Add(index);
                    _owners[index] = 0;
                    if (!counter.IsMannedByPlayer())
                    {
                        WithPrediction(counter.OnMouseButtonUp);
                    }
                });
        }

        private void ObserveScan(ActionCapture capture)
        {
            var customer = capture?.Customer;
            if (customer == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed " + (capture.IsCard ? "card" : "item")
                + " scan counter=" + capture.Index + " slot=" + capture.Slot + ".");
            var slot = capture.Slot;
            var isCard = capture.IsCard;
            PredictionApi.Predict(PredictionScope + ":" + capture.Index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)capture.Index,
                    Kind = isCard ? RegisterIntentKind.ScanCard : RegisterIntentKind.ScanItem,
                    Slot = slot,
                }),
                () => WithPrediction(() =>
                {
                    if (isCard)
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
                    if (isCard)
                    {
                        var cards = customer.GetCardInBagList();
                        if (slot >= 0 && slot < cards.Count)
                        {
                            RegisterInterop.UnscanCard(capture.Counter, customer, cards[slot]);
                        }
                    }
                    else
                    {
                        var items = customer.GetItemInBagList();
                        if (slot >= 0 && slot < items.Count)
                        {
                            RegisterInterop.UnscanItem(capture.Counter, customer, items[slot]);
                        }
                    }

                    UndoCheckout(capture.Undo);
                }));
        }

        private void ObservePayment(ActionCapture capture)
        {
            if (capture?.Cash == null || capture.Counter == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed take payment card=" + capture.Cash.m_IsCard
                + " counterTotal=" + RegisterInterop.Total(capture.Counter).ToString("F3")
                + " state=" + capture.Counter.m_CashierCounterState);
            PredictionApi.Predict(PredictionScope + ":" + capture.Index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)capture.Index,
                    Kind = RegisterIntentKind.TakePayment,
                    IsCard = capture.Cash.m_IsCard,
                }),
                () => WithPrediction(capture.Cash.OnMouseButtonUp),
                () => UndoCheckout(capture.Undo));
        }

        private void ObserveCardPayment(ActionCapture capture)
        {
            if (capture?.Counter == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed card payment value="
                + capture.Value.ToString("F3") + " counterTotal="
                + RegisterInterop.Total(capture.Counter).ToString("F3")
                + " customerTotal=" + Convert.ToDouble(RegisterInterop.Read(
                    capture.Counter.m_CurrentCustomer, "m_TotalScannedItemCost") ?? 0f).ToString("F3")
                + " state=" + capture.Counter.m_CashierCounterState);
            // EvaluateCreditCard runs OnPressSpaceBar, which zeroes the counter total only when the
            // card amount matched. A mismatch leaves the total untouched and queues no coin event,
            // so there is no wallet delta to reverse.
            if (RegisterInterop.Total(capture.Counter) <= 0.0001d)
            {
                capture.Undo.WalletDelta = CheckoutWalletDelta(capture.Value, 0d);
                capture.Undo.WalletApplied = true;
            }

            PredictionApi.Predict(PredictionScope + ":" + capture.Index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)capture.Index,
                    Kind = RegisterIntentKind.CardPayment,
                    Value = capture.Value,
                }),
                () => WithPrediction(() =>
                {
                    capture.Counter.EvaluateCreditCard(capture.Value);
                    capture.Undo.WalletApplied =
                        RegisterInterop.Total(capture.Counter) <= 0.0001d;
                }),
                () => UndoCheckout(capture.Undo));
        }

        private void ObserveChange(ActionCapture capture)
        {
            var change = capture?.Change;
            if (change == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed change slot=" + change.m_Index
                + " coin=" + change.m_IsCoin + " takeBack=" + capture.TakeBack
                + " value=" + change.m_ValueDouble.ToString("F3")
                + " given=" + RegisterInterop.GivenAmount(change)
                + " counterTotal=" + RegisterInterop.Total(change.m_CashierCounter).ToString("F3"));
            var takeBack = capture.TakeBack;
            PredictionApi.Predict(PredictionScope + ":" + capture.Index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)capture.Index,
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

        private void ObserveComplete(ActionCapture capture)
        {
            if (capture?.Counter == null)
            {
                return;
            }

            // Vanilla OnPressSpaceBar queues the shop's net income (or a change payout) for the
            // completed checkout. Capture that exact delta so a rejection reverses the wallet.
            capture.Undo.WalletDelta =
                CheckoutWalletDelta(capture.Undo.Paid, capture.Undo.Change);
            capture.Undo.WalletApplied = true;
            PredictionApi.Predict(PredictionScope + ":" + capture.Index,
                id => Send(new RegisterIntentMessage
                {
                    PredictionId = id,
                    Counter = (byte)capture.Index,
                    Kind = RegisterIntentKind.Complete,
                }),
                () => WithPrediction(() =>
                {
                    capture.Counter.OnPressSpaceBar();
                    capture.Undo.WalletApplied =
                        RegisterInterop.Total(capture.Counter) <= 0.0001d;
                }),
                () => UndoCheckout(capture.Undo));
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
                // Value is no longer part of the denomination's identity (m_Index + coin is), so it
                // must not split the deferred key.
                return message.Counter + ":" + message.CounterGeneration + ":change:"
                    + message.Slot + ":" + message.IsCoin;
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
                PredictionApi.Ack(delta.PredictionId);
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
            // Capture-only for a free counter: let the game man it, then register one post-hoc
            // claim. A counter the host or another client already owns is not this player's to
            // take: the man is suppressed (no man-then-rejection flicker) and no claim is sent,
            // which only skips a claim the authoritative host would reject anyway.
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance, out bool __state)
            {
                __state = false;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return true;
                }

                var index = RegisterInterop.Index(__instance);
                if (index < 0 || !client._joined || !client._context.InGame())
                {
                    return true;
                }

                if (client.IsRemoteOwned(index))
                {
                    CoopPlugin.Log.LogInfo("[register] not manning counter " + index
                        + "; another player owns it.");
                    return false;
                }

                __state = !__instance.IsMannedByPlayer();
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, bool __state)
            {
                var client = _active;
                if (!__state || client == null || __instance == null
                    || !__instance.IsMannedByPlayer())
                {
                    return;
                }

                var index = RegisterInterop.Index(__instance);
                if (index >= 0 && client._joined && client._context.InGame())
                {
                    client.ObserveClaim(__instance, index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "OnRaycasted")]
        private static class OutlinePatch
        {
            // The green hover outline is raised from this machine's raycast alone. A register the
            // host or another client already mans is not interactable here, so it must not
            // advertise itself (or its tooltips) as if it were. Ownership is authoritative and
            // known before the raycast in the normal flow; a mid-look takeover is cleared when its
            // ownership delta arrives.
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
            {
                var client = _active;
                if (client == null || !client._joined || !client._context.InGame()
                    || !(__instance is InteractableCashierCounter counter))
                {
                    return true;
                }

                if (client.IsRemoteOwned(RegisterInterop.Index(counter)))
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
            // Capture-only: let the game un-man the counter, then forward the post-hoc release.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, out bool __state)
            {
                __state = false;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return;
                }

                var index = RegisterInterop.Index(__instance);
                if (index < 0 || !client._claims.Contains(index))
                {
                    return;
                }

                __state = true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, bool __state)
            {
                var client = _active;
                if (!__state || client == null || __instance == null
                    || __instance.IsMannedByPlayer())
                {
                    return;
                }

                var index = RegisterInterop.Index(__instance);
                if (index >= 0)
                {
                    client.ObserveRelease(__instance, index);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableScanItem), "OnMouseButtonUp")]
        private static class ScanItemPatch
        {
            // Capture-only prefix: snapshot the pre-scan checkout state, then let the game scan.
            // A scan at a counter another peer owns is no longer gated on the client - vanilla
            // performs it and the postfix forwards one observation; the host rejects it if the
            // sender does not own the station and the generic rollback reverts the scan.
            [HarmonyPrefix]
            private static void Prefix(InteractableScanItem __instance, out ActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return;
                }

                var counter = RegisterInterop.FindCounterForScanItem(__instance);
                var station = client.Station(counter, out var index);
                if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
                {
                    return;
                }

                var slot = client.ItemSlot(__instance, index);
                if (slot < 0)
                {
                    return;
                }

                __state = new ActionCapture
                {
                    Counter = counter,
                    Customer = counter?.m_CurrentCustomer,
                    Index = index,
                    Slot = slot,
                    IsCard = false,
                    Undo = client.CaptureUndo(counter, counter?.m_CurrentCustomer),
                };
            }

            [HarmonyPostfix]
            private static void Postfix(ActionCapture __state)
            {
                if (__state != null)
                {
                    _active?.ObserveScan(__state);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCard3d), "OnMouseButtonUp")]
        private static class ScanCardPatch
        {
            // Card counterpart of ScanItemPatch.
            [HarmonyPrefix]
            private static void Prefix(InteractableCard3d __instance, out ActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return;
                }

                var counter = RegisterInterop.FindCounterForCard(__instance);
                var station = client.Station(counter, out var index);
                if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
                {
                    return;
                }

                var slot = client.CardSlot(__instance, index);
                if (slot < 0)
                {
                    return;
                }

                __state = new ActionCapture
                {
                    Counter = counter,
                    Customer = counter?.m_CurrentCustomer,
                    Index = index,
                    Slot = slot,
                    IsCard = true,
                    Undo = client.CaptureUndo(counter, counter?.m_CurrentCustomer),
                };
            }

            [HarmonyPostfix]
            private static void Postfix(ActionCapture __state)
            {
                if (__state != null)
                {
                    _active?.ObserveScan(__state);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCustomerCash), "OnMouseButtonUp")]
        private static class PaymentPatch
        {
            // Capture-only: let the game take the cash (the customer transitions to giving change),
            // then forward the post-hoc payment. A payment at a station another peer owns is no
            // longer gated on the client - the host rejects it and the rollback reverts it.
            [HarmonyPrefix]
            private static void Prefix(InteractableCustomerCash __instance, out ActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return;
                }

                var counter = RegisterInterop.FindCounterForCash(__instance);
                var station = client.Station(counter, out var index);
                if (station == null)
                {
                    return;
                }

                __state = new ActionCapture
                {
                    Counter = counter,
                    Customer = counter.m_CurrentCustomer,
                    Index = index,
                    Cash = __instance,
                    Undo = client.CaptureUndo(counter, counter.m_CurrentCustomer),
                };
            }

            [HarmonyPostfix]
            private static void Postfix(ActionCapture __state)
            {
                if (__state != null)
                {
                    _active?.ObservePayment(__state);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "EvaluateCreditCard")]
        private static class CardPaymentPatch
        {
            // Capture-only: let the game evaluate the card payment, then forward it. The vanilla
            // method ends by calling OnPressSpaceBar, so flag the nested call to make FinishPatch
            // record only the one higher-level card payment.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, double value,
                out ActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
                {
                    return;
                }

                var station = client.Station(__instance, out var index);
                if (station == null)
                {
                    return;
                }

                client._evaluatingCreditCard = true;
                __state = new ActionCapture
                {
                    Counter = __instance,
                    Customer = __instance.m_CurrentCustomer,
                    Index = index,
                    Value = value,
                    Undo = client.CaptureUndo(__instance, __instance.m_CurrentCustomer),
                };
                // EvaluateCreditCard ends by calling OnPressSpaceBar, which queues the counter's
                // AddCoin/AddShopExp. The host owns that charge for the guest's CardPayment intent,
                // so suppress the Hud observer for the vanilla event this call produces.
                EconomyActionScope.Enter();
            }

            [HarmonyPostfix]
            private static void Postfix(ActionCapture __state)
            {
                var client = _active;
                if (client != null)
                {
                    client._evaluatingCreditCard = false;
                }

                if (__state != null)
                {
                    client?.ObserveCardPayment(__state);
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(ActionCapture __state)
            {
                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnMouseButtonUp")]
        private static class AddChangePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCounterMoneyChange __instance,
                out ActionCapture __state)
                => CaptureChange(__instance, false, out __state);

            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance,
                ActionCapture __state)
            {
                if (__state != null && RegisterInterop.GivenAmount(__instance) != __state.GivenBefore)
                {
                    _active?.ObserveChange(__state);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp")]
        private static class RemoveChangePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCounterMoneyChange __instance,
                out ActionCapture __state)
                => CaptureChange(__instance, true, out __state);

            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance,
                ActionCapture __state)
            {
                if (__state != null && RegisterInterop.GivenAmount(__instance) != __state.GivenBefore)
                {
                    _active?.ObserveChange(__state);
                }
            }
        }

        private static void CaptureChange(InteractableCounterMoneyChange change, bool takeBack,
            out ActionCapture capture)
        {
            capture = null;
            var client = _active;
            if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0)
            {
                return;
            }

            var counter = change?.m_CashierCounter;
            var station = client.Station(counter, out var index);
            if (station == null)
            {
                return;
            }

            // Vanilla already ignores a click past the change limits or outside giving change, so
            // the postfix's GivenAmount comparison records only a click the game really performed.
            capture = new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = index,
                Change = change,
                TakeBack = takeBack,
                GivenBefore = RegisterInterop.GivenAmount(change),
            };
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressSpaceBar")]
        private static class FinishPatch
        {
            // Capture-only: let the game complete the checkout, then forward it. The nested call
            // inside EvaluateCreditCard is skipped so a card payment is exactly one prediction.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, out ActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || client._applyingRemote != 0 || client._applyingPrediction != 0
                    || client._evaluatingCreditCard)
                {
                    return;
                }

                var station = client.Station(__instance, out var index);
                if (station == null)
                {
                    return;
                }

                // Vanilla's OnPressSpaceBar only completes when the change is ready; otherwise it
                // shows the "wrong amount" / "too much change" popup and leaves the drawer open.
                // Let the game show its own popup and keep the register exactly as it was.
                if (!RegisterInterop.IsChangeReady(__instance))
                {
                    return;
                }

                __state = new ActionCapture
                {
                    Counter = __instance,
                    Customer = __instance.m_CurrentCustomer,
                    Index = index,
                    Undo = client.CaptureUndo(__instance, __instance.m_CurrentCustomer),
                };
                // OnPressSpaceBar queues the counter's AddCoin/AddShopExp. The host owns that
                // charge for the guest's Complete intent, so suppress the Hud observer for the
                // vanilla event this call produces.
                EconomyActionScope.Enter();
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, ActionCapture __state)
            {
                if (__state != null && !RegisterInterop.IsChangeReady(__instance))
                {
                    _active?.ObserveComplete(__state);
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(ActionCapture __state)
            {
                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "EvaluateFinishScanItem")]
        private static class FinishScanPatch
        {
            // The customer's paid amount and cash-vs-card kind are host-authoritative: only the
            // host runs the roll. A guest must not roll its own value (UnityEngine.Random differs
            // from the host) nor reveal the cash/card choice from a local roll; it waits for the
            // host's prediction-free PaidAmount delta, which drives the same settle through the
            // game's own setters. Suppressing the vanilla finish for a co-op register carrier is
            // the deliberate exception to "never stop vanilla": the host owns this value the same
            // way it owns another player's.
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                var client = _active;
                if (client == null || !client._context.InGame())
                {
                    return true;
                }

                return !IsCarrier(__instance);
            }
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
