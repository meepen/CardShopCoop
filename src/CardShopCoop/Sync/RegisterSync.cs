using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The register, faithful to single-player.
    ///
    /// The HOST is authoritative on the items to be served: it owns the customer's real bag and
    /// broadcasts a RegisterCart digest (item types + prices, card data + prices) for every counter
    /// that has a customer. Identity is the COUNTER INDEX - the same index the whole mod and the
    /// shared save use - because the counter list is identical on every machine.
    ///
    /// The current MANNING PLAYER is authoritative on the register's live state: they run the
    /// register exactly like single-player (click the counter to man it, click the customer's items
    /// to scan them, click the cash/card to take payment, click the change buttons, press the finish
    /// key) against a locally reconstructed bag of REAL spawned items/cards wired to a carrier
    /// Customer through the game's own RegisterScanItem/RegisterScanCard. Every action is emitted as
    /// a RegisterOp and replayed on the host through vanilla public methods, so the host's real
    /// customer and economy progress and finalize with zero re-implementation.
    ///
    /// Exclusivity (symmetric, not defensive): one player mans a station at a time.
    ///  - A guest entering a counter claims it: the host stops any worker there, records the owner,
    ///    and the HOST player (and other guests) are blocked from that counter.
    ///  - The host player entering a counter broadcasts it as host-manned; guests are blocked.
    ///  - A worker can never man a guest-claimed counter (NPCStartManCounter is gated).
    ///
    /// No register hints or tooltips exist in here - the register is the exact vanilla screen.
    /// </summary>
    public class RegisterSync
    {
        // ---- wire op codes ----
        public const byte OpEnter = 1;
        public const byte OpExit = 2;
        public const byte OpScanItem = 3;
        public const byte OpScanCard = 4;
        public const byte OpTakingPayment = 5;
        public const byte OpTookPayment = 6;
        public const byte OpGiveChange = 7;
        public const byte OpFinishCash = 8;
        public const byte OpFinishCard = 9;

        // ---- reflection: InteractableCashierCounter privates ----
        private static readonly FieldInfo FiIsUsingCard = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsUsingCard");
        private static readonly FieldInfo FiPaidAmount = AccessTools.Field(typeof(InteractableCashierCounter), "m_CustomerPaidAmount");
        private static readonly FieldInfo FiTotalScanned = AccessTools.Field(typeof(InteractableCashierCounter), "m_TotalScannedItemCost");
        private static readonly FieldInfo FiCashScreen = AccessTools.Field(typeof(InteractableCashierCounter), "m_UICashCounterScreen");
        private static readonly FieldInfo FiCreditScreen = AccessTools.Field(typeof(InteractableCashierCounter), "m_UICreditCardScreen");
        private static readonly FieldInfo FiChangeReady = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsChangeReady");
        private static readonly FieldInfo FiStartGivingChange = AccessTools.Field(typeof(InteractableCashierCounter), "m_IsStartGivingChange");
        private static readonly FieldInfo FiCurrentMoneyChange = AccessTools.Field(typeof(InteractableCashierCounter), "m_CurrentMoneyChangeValue");
        private static readonly FieldInfo FiTooMuchChange = AccessTools.Field(typeof(InteractableCashierCounter), "m_TooMuchChangeGiven");
        // ---- reflection: Customer privates ----
        private static readonly FieldInfo FiScannedCount = AccessTools.Field(typeof(Customer), "m_ItemScannedCount");
        private static readonly FieldInfo FiCustTotal = AccessTools.Field(typeof(Customer), "m_TotalScannedItemCost");
        private static readonly FieldInfo FiQueueCounter = AccessTools.Field(typeof(Customer), "m_CurrentQueueCashierCounter");
        private static readonly MethodInfo MiEvaluateFinish = AccessTools.Method(typeof(Customer), "EvaluateFinishScanItem");
        // ---- reflection: InteractableCustomerCash privates ----
        private static readonly FieldInfo FiCashCustomer = AccessTools.Field(typeof(InteractableCustomerCash), "m_CurrentCustomer");

        /// <summary>Set by CoopCore: client -> host op (MsgType.RegisterOp).</summary>
        public System.Action<System.Action<BinaryWriter>> SendOp;
        /// <summary>Set by CoopCore: host -> clients observer state (MsgType.RegisterState).</summary>
        public System.Action<System.Action<BinaryWriter>> BroadcastState;
        /// <summary>Set by CoopCore: host -> clients cart digest (MsgType.RegisterCart).</summary>
        public System.Action<System.Action<BinaryWriter>> BroadcastCart;

        // harmony prefixes are static; CoopCore owns the single instance
        private static RegisterSync _live;
        public static bool AllowClientCustomerLifecycle;
        public static bool SuppressClientRegisterEvents;
        public static bool ApplyingAuthoritativePayment;

        public RegisterSync() { _live = this; }

        private ShelfManager _sm;
        private ShelfManager Sm()
        {
            if (_sm == null) _sm = Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        // ---------------- host ----------------
        private float _stateTimer;
        private readonly Dictionary<int, int> _guestManned = new Dictionary<int, int>(); // counter idx -> conn id
        private readonly Dictionary<int, int> _cartCustomer = new Dictionary<int, int>(); // counter idx -> customer instance
        private readonly Dictionary<int, string> _cartSignature = new Dictionary<int, string>();

        // ---------------- client ----------------
        private readonly Dictionary<int, Customer> _carrier = new Dictionary<int, Customer>();
        private readonly Dictionary<Item, int> _itemBag = new Dictionary<Item, int>();      // spawned item -> bag index
        private readonly Dictionary<Item, int> _itemCounter = new Dictionary<Item, int>();  // spawned item -> counter idx
        private readonly Dictionary<InteractableCard3d, int> _cardBag = new Dictionary<InteractableCard3d, int>();
        private readonly Dictionary<InteractableCard3d, int> _cardCounter = new Dictionary<InteractableCard3d, int>();
        private int _localManned = -1;                            // counter the local player is manning (or -1)
        private readonly Dictionary<int, byte> _mannedBy = new Dictionary<int, byte>();     // counter idx -> 0/1/2
        private readonly Dictionary<int, int> _cartGen = new Dictionary<int, int>();        // counter idx -> applied customer token
        private readonly Dictionary<int, string> _cartScanSignature = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _sourceIndex = new Dictionary<int, int>();    // counter idx -> served customer list index

        public void Reset()
        {
            _stateTimer = 0f;
            _guestManned.Clear();
            _cartCustomer.Clear();
            _cartSignature.Clear();
            _localManned = -1;
            _mannedBy.Clear();
            _cartGen.Clear();
            _cartScanSignature.Clear();
            _sourceIndex.Clear();
            AllowClientCustomerLifecycle = false;
            SuppressClientRegisterEvents = false;
            ApplyingAuthoritativePayment = false;
            TeardownCarriers();
            _sm = null;
        }

        public void ForceResend()
        {
            _cartCustomer.Clear(); // force fresh RegisterCart digests on the next host tick
            _cartSignature.Clear();
        }

        /// <summary>Host: a client disconnected - release whatever it was manning.</summary>
        public void HostReleaseConn(int connId)
        {
            var drop = new List<int>();
            foreach (var kv in _guestManned)
                if (kv.Value == connId) drop.Add(kv.Key);
            foreach (int idx in drop) _guestManned.Remove(idx);
        }

        /// <summary>Host: is this counter claimed by a guest? (worker + host-serve gate)</summary>
        public static bool IsGuestManned(InteractableCashierCounter counter)
        {
            var t = _live;
            if (t == null || counter == null) return false;
            var sm = t.Sm();
            if (sm == null) return false;
            int idx = sm.m_CashierCounterList.IndexOf(counter);
            return idx >= 0 && t._guestManned.ContainsKey(idx);
        }

        /// <summary>Client: is this customer currently the register carrier (live at a counter)?</summary>
        public static bool IsCarrier(Customer c)
        {
            var t = _live;
            if (t == null || c == null) return false;
            foreach (var kv in t._carrier)
                if (ReferenceEquals(kv.Value, c)) return true;
            return false;
        }

        // ---------------- patches ----------------
        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractableCashierCounter), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningBlockPrefix)),
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningEnterPostfix)));
            Try(h, typeof(InteractableCashierCounter), "OnPressEsc",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ManningExitPostfix)));
            Try(h, typeof(InteractableCashierCounter), "NPCStartManCounter",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(WorkerGatePrefix)));
            Try(h, typeof(InteractableCashierCounter), "OnPressSpaceBar",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(FinishPrefix)),
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(FinishPostfix)));
            Try(h, typeof(InteractableCashierCounter), "UpdateCashierCounterState",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(StateChangePostfix)));
            Try(h, typeof(InteractableScanItem), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ScanItemPostfix)));
            Try(h, typeof(InteractableCard3d), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(ScanCardPostfix)));
            Try(h, typeof(InteractableCustomerCash), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(TakePaymentPostfix)));
            Try(h, typeof(Customer), "EvaluateFinishScanItem",
                prefix: new HarmonyMethod(typeof(RegisterSync), nameof(EvaluateFinishPrefix)));
            Try(h, typeof(InteractableCounterMoneyChange), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(GiveChangeAddPostfix)));
            Try(h, typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp",
                postfix: new HarmonyMethod(typeof(RegisterSync), nameof(GiveChangeRemovePostfix)));
        }

        private static void Try(Harmony h, System.Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"RegisterSync: patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync: patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Entering a station someone else already mans is a no-op (vanilla silence, no hint).</summary>
        public static bool ManningBlockPrefix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || __instance == null) return true;
            if (CoopCore.Role == CoopRole.Host)
                return !IsGuestManned(__instance);
            if (CoopCore.Role != CoopRole.Client) return true;

            var sm = t.Sm();
            if (sm == null) return true;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0) return true;
            byte who = t._mannedBy.TryGetValue(idx, out byte w) ? w : (byte)0;
            if (who == 1) return false;                       // the host player mans it
            if (who == 2 && t._localManned != idx) return false; // another guest mans it
            return true;
        }

        /// <summary>Client: entering the counter - claim it with the host. Guarded on
        /// IsMannedByPlayer() because a Harmony postfix runs even when the block prefix returned
        /// false (the vanilla body was skipped, so the player never actually entered).</summary>
        public static void ManningEnterPostfix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return;
            if (!__instance.IsMannedByPlayer()) return; // the block prefix stopped the vanilla entry
            var sm = t.Sm();
            if (sm == null) return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0) return;
            t._localManned = idx;
            t.SendOp?.Invoke(bw => { bw.Write((byte)idx); bw.Write(OpEnter); });
            CoopPlugin.Log.LogDebug($"RegisterSync client: manned counter {idx}");
        }

        /// <summary>Client: leaving the counter (Esc / movement-away) - release the claim.</summary>
        public static void ManningExitPostfix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return;
            var sm = t.Sm();
            if (sm == null) return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0) return;
            t._localManned = -1;
            t.SendOp?.Invoke(bw => { bw.Write((byte)idx); bw.Write(OpExit); });
            CoopPlugin.Log.LogDebug($"RegisterSync client: left counter {idx}");
        }

        /// <summary>Host: a worker must never serve a guest-claimed station.</summary>
        public static bool WorkerGatePrefix(InteractableCashierCounter __instance)
        {
            if (CoopCore.Role != CoopRole.Host) return true;
            return !IsGuestManned(__instance);
        }

        /// <summary>Client: finishing a sale - the vanilla body cannot run (economy/AI hit the
        /// deactivated carrier and the economy belongs to the host). Forward the finish; reset locally.</summary>
        public static bool FinishPrefix(InteractableCashierCounter __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return true;
            var sm = t.Sm();
            if (sm == null) return true;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0 || !t._carrier.ContainsKey(idx)) return true;

            bool isCard = FiIsUsingCard?.GetValue(__instance) is bool c && c;
            if (!(FiChangeReady?.GetValue(__instance) is bool ready) || !ready) return true;
            double total = FiTotalScanned?.GetValue(__instance) is double d ? d : 0.0;
            CoopPlugin.Log.LogDebug($"RegisterSync client: finish counter {idx} card={isCard}");
            t.SendOp?.Invoke(bw =>
            {
                bw.Write((byte)idx);
                bw.Write(isCard ? OpFinishCard : OpFinishCash);
                if (isCard) bw.Write(total);
            });
            SuppressClientRegisterEvents = true;
            return true;
        }

        public static void FinishPostfix()
        {
            SuppressClientRegisterEvents = false;
        }

        public static bool EvaluateFinishPrefix(Customer __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null) return true;
            var t = _live;
            return t == null || !t._carrier.ContainsValue(__instance);
        }

        /// <summary>Client: the counter just entered TakingCash - forward the payment roll the game made.</summary>
        public static void StateChangePostfix(InteractableCashierCounter __instance, ECashierCounterState state)
        {
            if (ApplyingAuthoritativePayment) return;
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return;
            if (state != ECashierCounterState.TakingCash) return;
            var sm = t.Sm();
            if (sm == null) return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance);
            if (idx < 0 || !t._carrier.ContainsKey(idx)) return;

            bool isCard = FiIsUsingCard?.GetValue(__instance) is bool c && c;
            double paid = FiPaidAmount?.GetValue(__instance) is double p ? p : 0.0;
            CoopPlugin.Log.LogDebug($"RegisterSync client: taking payment counter {idx} card={isCard} paid={paid}");
            t.SendOp?.Invoke(bw =>
            {
                bw.Write((byte)idx);
                bw.Write(OpTakingPayment);
                bw.Write(isCard);
                bw.Write(paid);
            });
        }

        /// <summary>Client: a scan item was clicked - forward which bag slot it was.</summary>
        public static void ScanItemPostfix(InteractableScanItem __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null || __instance.m_Item == null) return;
            if (!t._itemBag.TryGetValue(__instance.m_Item, out int k)) return;
            if (!t._itemCounter.TryGetValue(__instance.m_Item, out int idx)) return;
            t.SendOp?.Invoke(bw => { bw.Write((byte)idx); bw.Write(OpScanItem); bw.Write((byte)k); });
            CoopPlugin.Log.LogDebug($"RegisterSync client: scan item {k} @ {idx}");
        }

        /// <summary>Client: a scan card was clicked - forward which bag slot it was.</summary>
        public static void ScanCardPostfix(InteractableCard3d __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return;
            if (!t._cardBag.TryGetValue(__instance, out int k)) return;
            if (!t._cardCounter.TryGetValue(__instance, out int idx)) return;
            t.SendOp?.Invoke(bw => { bw.Write((byte)idx); bw.Write(OpScanCard); bw.Write((byte)k); });
            CoopPlugin.Log.LogDebug($"RegisterSync client: scan card {k} @ {idx}");
        }

        /// <summary>Client: the cash/card was clicked - forward the payment shot.</summary>
        public static void TakePaymentPostfix(InteractableCustomerCash __instance)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null) return;
            var cust = FiCashCustomer?.GetValue(__instance) as Customer;
            if (cust == null || !t._carrier.ContainsValue(cust)) return;
            var counter = FiQueueCounter?.GetValue(cust) as InteractableCashierCounter;
            var sm = t.Sm();
            if (counter == null || sm == null) return;
            int idx = sm.m_CashierCounterList.IndexOf(counter);
            if (idx < 0) return;
            bool isCard = __instance.m_IsCard;
            t.SendOp?.Invoke(bw => { bw.Write((byte)idx); bw.Write(OpTookPayment); bw.Write(isCard); });
            CoopPlugin.Log.LogDebug($"RegisterSync client: took payment @ {idx} card={isCard}");
        }

        private static void GiveChangeAddPostfix(InteractableCounterMoneyChange __instance) => EmitChange(__instance, false);
        private static void GiveChangeRemovePostfix(InteractableCounterMoneyChange __instance) => EmitChange(__instance, true);

        private static void EmitChange(InteractableCounterMoneyChange __instance, bool takingBack)
        {
            var t = _live;
            if (t == null || CoopCore.Role != CoopRole.Client || __instance == null || __instance.m_CashierCounter == null) return;
            var sm = t.Sm();
            if (sm == null) return;
            int idx = sm.m_CashierCounterList.IndexOf(__instance.m_CashierCounter);
            if (idx < 0 || !t._carrier.ContainsKey(idx)) return;
            double value = __instance.m_ValueDouble;
            t.SendOp?.Invoke(bw =>
            {
                bw.Write((byte)idx);
                bw.Write(OpGiveChange);
                bw.Write(__instance.m_Index);
                bw.Write(value);
                bw.Write(takingBack);
            });
        }

        // ---------------- host tick ----------------
        public void HostTick(float dt, bool inGame)
        {
            if (!inGame) return;
            _stateTimer += dt;
            var sm = Sm();
            if (sm == null || sm.m_CashierCounterList == null) return;

            bool cartChanged = false;
            for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null) continue;
                int custId = counter.m_CurrentCustomer != null && counter.m_CurrentCustomer.m_IsActive
                    ? counter.m_CurrentCustomer.GetInstanceID() : 0;
                string signature = custId + "|" + (int)counter.m_CashierCounterState + "|"
                    + (FiIsUsingCard?.GetValue(counter) is bool card && card ? "1" : "0") + "|"
                    + (FiPaidAmount?.GetValue(counter) ?? 0.0).ToString() + "|"
                    + ScanSignature(counter.m_CurrentCustomer);
                if (_cartCustomer.TryGetValue(i, out int prev) && prev == custId
                    && _cartSignature.TryGetValue(i, out var oldSignature) && oldSignature == signature) continue;
                _cartCustomer[i] = custId;
                _cartSignature[i] = signature;
                cartChanged = true;
            }
            if (cartChanged) BroadcastCart?.Invoke(WriteCarts);

            if (_stateTimer >= 0.5f)
            {
                _stateTimer -= 0.5f;
                BroadcastState?.Invoke(WriteStates);
            }
        }

        private void WriteCarts(BinaryWriter bw)
        {
            var sm = Sm();
            using (var inner = new MemoryStream())
            using (var w = new BinaryWriter(inner))
            {
                int count = 0;
                for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
                {
                    var counter = sm.m_CashierCounterList[i];
                    if (counter == null) continue;
                    var cust = counter.m_CurrentCustomer;
                    bool active = cust != null && cust.m_IsActive;

                    w.Write((byte)i);
                    w.Write(active ? cust.GetInstanceID() : 0); // opaque change token (0 = customer left)
                    if (!active) { count++; continue; }
                    w.Write((ushort)CustomerListIndex(cust)); // for the client's carrier pick + NpcSync suppression
                    w.Write(NpcSync.GetCustomerGeneration(cust));
                    w.Write(cust.m_CharacterCustom != null ? cust.m_CharacterCustom.CharacterName : "");
                    w.Write((byte)counter.m_CashierCounterState);
                    w.Write(FiIsUsingCard?.GetValue(counter) is bool card && card);
                    w.Write(FiPaidAmount?.GetValue(counter) is double paid ? paid : 0.0);
                    var items = cust.GetItemInBagList();
                    w.Write((byte)items.Count);
                    for (int k = 0; k < items.Count; k++)
                    {
                        Net.Msg.WriteItemType(w, items[k].GetItemType());
                        w.Write(items[k].GetCurrentPrice());
                    }
                    for (int k = 0; k < items.Count; k++)
                        w.Write(items[k].m_InteractableScanItem != null && !items[k].m_InteractableScanItem.IsNotScanned());
                    var cards = cust.GetCardInBagList();
                    w.Write((byte)cards.Count);
                    for (int k = 0; k < cards.Count; k++)
                    {
                        Net.Msg.WriteCard(w, cards[k].m_Card3dUI.m_CardUI.GetCardData());
                        w.Write(cards[k].GetCurrentPrice());
                    }
                    for (int k = 0; k < cards.Count; k++)
                        w.Write(!cards[k].IsNotScanned());
                    count++;
                }
                if (count == 0) return;
                bw.Write((byte)count);
                bw.Write(inner.ToArray());
            }
        }

        private static int CustomerListIndex(Customer cust)
        {
            var cm = Object.FindObjectOfType<CustomerManager>();
            if (cm == null) return 0;
            var list = cm.GetCustomerList();
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], cust)) return i;
            return 0;
        }

        private static string ScanSignature(Customer cust)
        {
            if (cust == null) return "";
            var result = new System.Text.StringBuilder();
            var items = cust.GetItemInBagList();
            for (int i = 0; i < items.Count; i++)
                result.Append(items[i] != null && items[i].m_InteractableScanItem != null
                    && !items[i].m_InteractableScanItem.IsNotScanned() ? '1' : '0');
            result.Append('/');
            var cards = cust.GetCardInBagList();
            for (int i = 0; i < cards.Count; i++)
                result.Append(cards[i] != null && !cards[i].IsNotScanned() ? '1' : '0');
            return result.ToString();
        }

        private void WriteStates(BinaryWriter bw)
        {
            var sm = Sm();
            using (var inner = new MemoryStream())
            using (var w = new BinaryWriter(inner))
            {
                int count = 0;
                for (int i = 0; i < sm.m_CashierCounterList.Count && i < 250; i++)
                {
                    var counter = sm.m_CashierCounterList[i];
                    if (counter == null) continue;
                    byte manned = counter.IsMannedByPlayer() ? (byte)1 : (_guestManned.ContainsKey(i) ? (byte)2 : (byte)0);
                    w.Write((byte)i);
                    w.Write(manned);
                    count++;
                }
                if (count == 0) return;
                bw.Write((byte)count);
                bw.Write(inner.ToArray());
            }
        }

        // ---------------- host op application ----------------
        public void HostApplyOp(BinaryReader br, int connId)
        {
            int idx = br.ReadByte();
            byte op = br.ReadByte();
            var sm = Sm();
            if (sm == null || idx >= sm.m_CashierCounterList.Count) return;
            var counter = sm.m_CashierCounterList[idx];
            if (counter == null) return;

            if (op == OpEnter)
            {
                if (counter.IsMannedByPlayer()) return;                       // host player already there
                if (_guestManned.TryGetValue(idx, out int owner) && owner != connId) return; // another guest owns it
                _guestManned[idx] = connId;
                try { counter.StopCurrentWorker(); } catch { }
                _cartSignature.Remove(idx);
                BroadcastCart?.Invoke(WriteCarts);
                CoopPlugin.Log.LogDebug($"RegisterSync host: guest {connId} manned counter {idx}");
                return;
            }
            if (op == OpExit)
            {
                if (_guestManned.TryGetValue(idx, out int owner) && owner == connId)
                    _guestManned.Remove(idx);
                CoopPlugin.Log.LogDebug($"RegisterSync host: guest {connId} left counter {idx}");
                return;
            }
            // serving actions are only accepted from the owner
            if (!_guestManned.TryGetValue(idx, out int who) || who != connId) return;
            var cust = counter.m_CurrentCustomer;
            if (cust == null || !cust.m_IsActive) return;

            switch (op)
            {
                case OpScanItem:
                {
                    int k = br.ReadByte();
                    var items = cust.GetItemInBagList();
                    if (k < 0 || k >= items.Count) return;
                    var item = items[k];
                    if (item == null || item.m_InteractableScanItem == null || !item.m_InteractableScanItem.IsNotScanned()) return;
                    item.m_InteractableScanItem.OnMouseButtonUp();
                    break;
                }
                case OpScanCard:
                {
                    int k = br.ReadByte();
                    var cards = cust.GetCardInBagList();
                    if (k < 0 || k >= cards.Count) return;
                    var card = cards[k];
                    if (card == null || !card.IsNotScanned()) return;
                    card.OnMouseButtonUp();
                    break;
                }
                case OpTakingPayment:
                {
                    // The final host-side scan already ran Customer.EvaluateFinishScanItem,
                    // which selected payment, presented the real cash/card, and entered the
                    // vanilla TakingCash state. This notification is only a client-side
                    // convergence marker; never replace the customer flow with a manual state
                    // write here.
                    br.ReadBoolean();
                    br.ReadDouble();
                    break;
                }
                case OpTookPayment:
                {
                    br.ReadBoolean(); // host's customer/payment object is authoritative
                    cust.m_CustomerCash.OnMouseButtonUp();
                    break;
                }
                case OpGiveChange:
                {
                    int buttonIndex = br.ReadInt32();
                    double value = br.ReadDouble();
                    bool takingBack = br.ReadBoolean();
                    var money = counter.m_InteractableCounterMoneyChangeList;
                    if (buttonIndex < 0 || buttonIndex >= money.Count) return;
                    var button = money[buttonIndex];
                    if (takingBack) button.OnRightMouseButtonUp();
                    else button.OnMouseButtonUp();
                    break;
                }
                case OpFinishCash:
                {
                    counter.OnPressSpaceBar();
                    CoopPlugin.Log.LogDebug($"RegisterSync host: completed a cash sale at counter {idx}");
                    break;
                }
                case OpFinishCard:
                {
                    double total = br.ReadDouble();
                    counter.EvaluateCreditCard(total);
                    CoopPlugin.Log.LogDebug($"RegisterSync host: completed a card sale at counter {idx}");
                    break;
                }
            }
        }

        // ---------------- client ----------------
        private struct Cart
        {
            public byte Index;
            public ushort CustomerIndex;
            public int CustomerGeneration;
            public string CharacterName;
            public byte State;
            public bool IsCard;
            public double PaidAmount;
            public List<EItemType> ItemTypes;
            public List<float> ItemPrices;
            public List<CardData> Cards;
            public List<float> CardPrices;
            public List<bool> ItemScanned;
            public List<bool> CardScanned;
            public string ScanSignature;
        }

        /// <summary>Client: the host's authoritative cart for a counter - rebuild a real scannable bag.</summary>
        public void ClientApplyCart(BinaryReader br)
        {
            int count = br.ReadByte();
            for (int i = 0; i < count; i++)
            {
                int idx = br.ReadByte();
                int cid = br.ReadInt32();
                if (cid == 0)
                {
                    // customer left this counter - settle it (only if we were tracking it)
                    if (_cartGen.Remove(idx))
                        ClientSettle(idx);
                    continue;
                }
                var c = new Cart
                {
                    Index = (byte)idx,
                    CustomerIndex = br.ReadUInt16(),
                    CustomerGeneration = br.ReadInt32(),
                    CharacterName = br.ReadString(),
                    State = br.ReadByte(),
                    IsCard = br.ReadBoolean(),
                    PaidAmount = br.ReadDouble(),
                    ItemTypes = new List<EItemType>(),
                    ItemPrices = new List<float>(),
                    Cards = new List<CardData>(),
                    CardPrices = new List<float>(),
                    ItemScanned = new List<bool>(),
                    CardScanned = new List<bool>(),
                };
                int ni = br.ReadByte();
                for (int k = 0; k < ni; k++)
                {
                    c.ItemTypes.Add(Net.Msg.ReadItemType(br));
                    c.ItemPrices.Add(br.ReadSingle());
                }
                for (int k = 0; k < ni; k++) c.ItemScanned.Add(br.ReadBoolean());
                int nc = br.ReadByte();
                for (int k = 0; k < nc; k++)
                {
                    c.Cards.Add(Net.Msg.ReadCard(br));
                    c.CardPrices.Add(br.ReadSingle());
                }
                for (int k = 0; k < nc; k++) c.CardScanned.Add(br.ReadBoolean());
                c.ScanSignature = BuildScanSignature(c.ItemScanned, c.CardScanned);
                ApplyCart(c, cid);
            }
        }

        private void ApplyCart(Cart c, int cid)
        {
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count) return;
            var counter = sm.m_CashierCounterList[c.Index];
            if (counter == null) return;
            // only re-build when this counter's customer actually changed; an unrelated
            // counter's cart broadcast must never reset a sale in progress elsewhere
            if (_cartGen.TryGetValue(c.Index, out int prev) && prev == cid)
            {
                if (!_cartScanSignature.TryGetValue(c.Index, out var oldScan) || oldScan != c.ScanSignature)
                {
                    ApplyScannedItems(c);
                    _cartScanSignature[c.Index] = c.ScanSignature;
                }
                ApplyAuthoritativePhase(c);
                ApplyAuthoritativePayment(c);
                return;
            }
            _cartGen[c.Index] = cid;
            _cartScanSignature[c.Index] = c.ScanSignature;

            var carrier = GetCarrier(c.Index, c.CustomerIndex);
            if (carrier == null) return;
            AllowClientCustomerLifecycle = true;
            try { carrier.ActivateCustomer(canSpawnSmelly: false, randomizeCharacterMesh: false); }
            finally { AllowClientCustomerLifecycle = false; }
            if (carrier.m_CharacterCustom != null && !string.IsNullOrEmpty(c.CharacterName))
            {
                carrier.m_CharacterCustom.CharacterName = c.CharacterName;
                carrier.m_CharacterCustom.Initialize();
            }
            try { FiQueueCounter?.SetValue(carrier, counter); } catch { }

            // fresh customer: reset the scan/bookkeeping state the carrier carries across pool reuse.
            // ActivateCustomer is blocked on the client, so the cash was never Init()'d to the
            // carrier - wire it now or the presented payment would click into a null customer.
            try { FiScannedCount?.SetValue(carrier, 0); } catch { }
            try { FiCustTotal?.SetValue(carrier, 0.0); } catch { }
            try { carrier.m_CustomerCash.Init(carrier); } catch { }

            // The served customer is LIVE at the register: activate the carrier so its real
            // interactable cash (a child, in its hands) is the presented, clickable payment, and
            // suppress the NpcSync clone so there is only one body here.
            try
            {
                if (counter.m_QueueStartPos != null)
                {
                    carrier.transform.position = counter.m_QueueStartPos.position;
                    var facing = counter.transform.position - carrier.transform.position;
                    facing.y = 0f;
                    carrier.transform.rotation = facing.sqrMagnitude > 0.0001f
                        ? Quaternion.LookRotation(facing, Vector3.up)
                        : counter.m_QueueStartPos.rotation;
                }
                carrier.gameObject.SetActive(true);
            }
            catch { }
            NpcSync.SuppressedCustomer.Add(c.CustomerIndex);
            _sourceIndex[c.Index] = c.CustomerIndex;
            NpcSync.AttachExistingCustomer(c.CustomerIndex, c.CustomerGeneration, carrier);
            counter.UpdateCurrentCustomer(carrier);
            counter.UpdateCashierCounterState(ECashierCounterState.ScanningItem);
            counter.SetPlsaticBagVisibility(true);

            var ctf = counter.transform;
            var placePos = counter.m_CustomerPlaceItemPos;
            for (int i = 0; i < c.ItemTypes.Count; i++)
            {
                var item = SpawnBagItem(counter, ctf, placePos, c.ItemTypes[i], c.ItemPrices[i], carrier, i);
                if (item != null)
                {
                    _itemBag[item] = i;
                    _itemCounter[item] = c.Index;
                }
            }
            for (int j = 0; j < c.Cards.Count; j++)
            {
                var card = SpawnBagCard(counter, ctf, placePos, c.Cards[j], c.CardPrices[j], carrier, j);
                if (card != null)
                {
                    _cardBag[card] = j;
                    _cardCounter[card] = c.Index;
                }
            }
            ApplyScannedItems(c);
            ApplyAuthoritativePhase(c);
            ApplyAuthoritativePayment(c);
        }

        private static string BuildScanSignature(List<bool> items, List<bool> cards)
        {
            var result = new System.Text.StringBuilder();
            if (items != null) for (int i = 0; i < items.Count; i++) result.Append(items[i] ? '1' : '0');
            result.Append('/');
            if (cards != null) for (int i = 0; i < cards.Count; i++) result.Append(cards[i] ? '1' : '0');
            return result.ToString();
        }

        private void ApplyScannedItems(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null) return;
            var items = customer.GetItemInBagList();
            for (int i = 0; i < c.ItemScanned.Count && i < items.Count; i++)
                if (c.ItemScanned[i] && items[i] != null && items[i].m_InteractableScanItem != null
                    && items[i].m_InteractableScanItem.IsNotScanned())
                    items[i].m_InteractableScanItem.OnMouseButtonUp();
            var cards = customer.GetCardInBagList();
            for (int i = 0; i < c.CardScanned.Count && i < cards.Count; i++)
                if (c.CardScanned[i] && cards[i] != null && cards[i].IsNotScanned())
                    cards[i].OnMouseButtonUp();
        }

        private void ApplyAuthoritativePhase(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null) return;
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count) return;
            var counter = sm.m_CashierCounterList[c.Index];
            if (counter == null) return;
            var state = (ECashierCounterState)c.State;
            if (state != ECashierCounterState.ScanningItem) return;
            FiIsUsingCard?.SetValue(counter, false);
            FiStartGivingChange?.SetValue(counter, false);
            FiChangeReady?.SetValue(counter, false);
            FiCurrentMoneyChange?.SetValue(counter, 0.0);
            FiTooMuchChange?.SetValue(counter, false);
            customer.m_CustomerCash.gameObject.SetActive(false);
            if (customer.m_Anim != null) customer.m_Anim.SetBool("HandingOverCash", false);
            counter.UpdateCashierCounterState(state);
        }

        private void ApplyAuthoritativePayment(Cart c)
        {
            if (!_carrier.TryGetValue(c.Index, out var customer) || customer == null) return;
            var sm = Sm();
            if (sm == null || c.Index >= sm.m_CashierCounterList.Count) return;
            var counter = sm.m_CashierCounterList[c.Index];
            ApplyingAuthoritativePayment = true;
            try
            {
                counter.SetCustomerPaidAmount(c.IsCard, c.PaidAmount);
                if (c.State == (byte)ECashierCounterState.TakingCash)
                {
                    customer.m_CustomerCash.SetIsCard(c.IsCard);
                    customer.m_CustomerCash.gameObject.SetActive(true);
                    customer.m_Anim.SetBool("HandingOverCash", true);
                    counter.UpdateCashierCounterState(ECashierCounterState.TakingCash);
                }
                else if (c.State == (byte)ECashierCounterState.GivingChange
                    && counter.m_CashierCounterState != ECashierCounterState.GivingChange)
                {
                    customer.OnCashTaken(c.IsCard);
                }
            }
            finally { ApplyingAuthoritativePayment = false; }
        }

        private Item SpawnBagItem(InteractableCashierCounter counter, Transform ctf, Transform placePos,
            EItemType type, float price, Customer carrier, int i)
        {
            try
            {
                if (type == EItemType.None) return null;
                var meshData = InventoryBase.GetItemMeshData(type);
                if (meshData == null) return null;
                var item = ItemSpawnManager.GetItem(ctf);
                item.SetMesh(meshData.mesh, meshData.material, type,
                    meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                item.SetCurrentPrice(price);
                item.transform.parent = ctf;
                item.transform.position = placePos.position;
                int num = i % 8, num2 = Mathf.Clamp(i / 8, 0, 1), num3 = Mathf.Clamp(i / 16, 0, 2);
                item.transform.position += placePos.forward * (-0.025f * num);
                item.transform.position += placePos.right * (0.1f * num2);
                item.transform.position += Vector3.up * (0.2f * num3);
                item.transform.rotation = placePos.rotation;
                item.transform.Rotate(new Vector3(Random.Range(-30, -5), Random.Range(-5, 5), Random.Range(-5, 5)));
                item.m_Mesh.enabled = true;
                item.gameObject.SetActive(true);
                item.m_Collider.enabled = true;
                // kinematic: the deactivated carrier's vanilla bounds-check never runs here, so
                // a real, gravity-driven item would slide off the counter. Scanning is unaffected.
                if (item.m_Rigidbody != null) item.m_Rigidbody.isKinematic = true;
                item.m_InteractableScanItem.enabled = true;
                item.m_InteractableScanItem.RegisterScanItem(carrier, counter.m_ScannedItemLerpPos);
                carrier.m_ItemInBagList.Add(item);
                return item;
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync client: bag item spawn: {e.Message}");
                return null;
            }
        }

        private InteractableCard3d SpawnBagCard(InteractableCashierCounter counter, Transform ctf, Transform placePos,
            CardData cardData, float price, Customer carrier, int j)
        {
            try
            {
                if (cardData == null || cardData.monsterType == EMonsterType.None) return null;
                var cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                var card = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();
                if (card == null) return null;
                cardUI.m_CardUI.SetCardUI(cardData);
                card.SetCardUIFollow(cardUI);
                card.SetEnableCollision(false);
                card.SetCurrentPrice(price);
                card.transform.parent = ctf;
                card.transform.position = placePos.position;
                card.transform.localScale = Vector3.one;
                int num = j % 8, num2 = Mathf.Clamp(j / 8, 0, 1), num3 = Mathf.Clamp(j / 16, 0, 2);
                card.transform.position += placePos.forward * (-0.035f * num);
                card.transform.position += placePos.right * (0.1f * num2);
                card.transform.position += Vector3.up * (0.3f * num3);
                card.transform.rotation = placePos.rotation;
                card.transform.Rotate(new Vector3(Random.Range(-30, -5) + 180, Random.Range(-5, 5), Random.Range(-5, 5)));
                card.m_Card3dUI.gameObject.SetActive(true);
                card.gameObject.SetActive(true);
                card.m_Collider.enabled = true;
                if (card.m_Rigidbody != null) card.m_Rigidbody.isKinematic = true;
                card.RegisterScanCard(carrier, counter.m_ScannedItemLerpPos);
                carrier.m_CardInBagList.Add(card);
                return card;
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning($"RegisterSync client: bag card spawn: {e.Message}");
                return null;
            }
        }

        private Customer GetCarrier(int idx, ushort customerIndex)
        {
            if (_carrier.TryGetValue(idx, out var c) && c != null) return c;
            Customer carrier = null;
            var cm = Object.FindObjectOfType<CustomerManager>();
            if (cm != null)
            {
                var list = cm.GetCustomerList();
                if (customerIndex < list.Count && list[customerIndex] != null && !_carrier.ContainsValue(list[customerIndex]))
                    carrier = list[customerIndex];
                if (carrier == null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && !_carrier.ContainsValue(list[i])) { carrier = list[i]; break; }
            }
            _carrier[idx] = carrier;
            return carrier;
        }

        /// <summary>Client: observer state broadcast - values the manning gate only. (The
        /// register visuals come from the real RegisterCart reconstruction, so no fake mirror.)</summary>
        public void ClientApplyState(BinaryReader br)
        {
            int count = br.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte idx = br.ReadByte();
                byte manned = br.ReadByte();
                if (manned != 0) _mannedBy[idx] = manned;
                else _mannedBy.Remove(idx);
            }
        }

        /// <summary>Client: settle a counter after its customer left (no economy/AI - host owns those).</summary>
        private void ClientSettle(int idx)
        {
            var sm = Sm();
            if (sm == null || idx >= sm.m_CashierCounterList.Count) return;
            var counter = sm.m_CashierCounterList[idx];
            if (counter == null) return;
            try { counter.UpdateCashierCounterState(ECashierCounterState.Idle); } catch { }
            try { counter.UpdateCurrentCustomer(null); } catch { }
            try { counter.SetPlsaticBagVisibility(false); } catch { }
            Teardown(idx);
        }

        /// <summary>Client: reset one counter after a finished sale (no economy/AI - the host owns those).</summary>
        private void ClientResetCounter(InteractableCashierCounter counter, int idx, bool isCard)
        {
            try
            {
                if (isCard)
                {
                    var credit = FiCreditScreen?.GetValue(counter) as UI_CreditCardScreen;
                    if (credit != null) credit.ResetCounter();
                }
                var screen = FiCashScreen?.GetValue(counter) as UI_CashCounterScreen;
                if (screen != null) screen.ResetCounter();
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning("RegisterSync client reset: " + e.Message); }
            try
            {
                var mc = counter.m_InteractableCounterMoneyChangeList;
                for (int i = 0; i < mc.Count; i++) mc[i].ResetAmountGiven();
            }
            catch { }
            if (isCard) counter.OnPressEsc();
            ClientSettle(idx);
        }

        private void Teardown(int idx)
        {
            if (_carrier.TryGetValue(idx, out var carrier) && carrier != null)
            {
                try { carrier.m_CustomerCash.gameObject.SetActive(false); } catch { }
                if (carrier.m_ItemInBagList != null)
                    for (int i = carrier.m_ItemInBagList.Count - 1; i >= 0; i--)
                        if (carrier.m_ItemInBagList[i] != null) carrier.m_ItemInBagList[i].gameObject.SetActive(false);
                carrier.m_ItemInBagList.Clear();
                if (carrier.m_CardInBagList != null)
                    for (int i = carrier.m_CardInBagList.Count - 1; i >= 0; i--)
                        ReleaseCard(carrier.m_CardInBagList[i]);
                carrier.m_CardInBagList.Clear();
            }
            _carrier.Remove(idx);
            _cartGen.Remove(idx);
            _cartScanSignature.Remove(idx);
            if (_sourceIndex.TryGetValue(idx, out int src))
            {
                _sourceIndex.Remove(idx);
                NpcSync.DetachExistingCustomer(src, carrier);
            }
            var itemDrop = new List<Item>();
            foreach (var kv in _itemCounter) if (kv.Value == idx) itemDrop.Add(kv.Key);
            foreach (var it in itemDrop) { _itemBag.Remove(it); _itemCounter.Remove(it); }
            var cardDrop = new List<InteractableCard3d>();
            foreach (var kv in _cardCounter) if (kv.Value == idx) cardDrop.Add(kv.Key);
            foreach (var card in cardDrop) { _cardBag.Remove(card); _cardCounter.Remove(card); }
        }

        private void TeardownCarriers()
        {
            foreach (int idx in _carrier.Keys)
            {
                var carrier = _carrier[idx];
                try
                {
                    if (carrier != null)
                    {
                        carrier.m_CustomerCash.gameObject.SetActive(false);
                        carrier.gameObject.SetActive(false);
                        for (int i = carrier.m_ItemInBagList.Count - 1; i >= 0; i--)
                        if (carrier.m_ItemInBagList[i] != null) carrier.m_ItemInBagList[i].gameObject.SetActive(false);
                        carrier.m_ItemInBagList.Clear();
                        if (carrier.m_CardInBagList != null)
                            for (int i = carrier.m_CardInBagList.Count - 1; i >= 0; i--)
                                ReleaseCard(carrier.m_CardInBagList[i]);
                        carrier.m_CardInBagList.Clear();
                    }
                }
                catch { }
            }
            _carrier.Clear();
            _cartGen.Clear();
            _sourceIndex.Clear();
            _itemBag.Clear();
            _itemCounter.Clear();
            _cardBag.Clear();
            _cardCounter.Clear();
        }

        private static void ReleaseCard(InteractableCard3d card)
        {
            try
            {
                if (card != null)
                {
                    if (card.m_Card3dUI != null) card.m_Card3dUI.gameObject.SetActive(false);
                    card.gameObject.SetActive(false);
                }
            }
            catch { }
        }
    }
}
