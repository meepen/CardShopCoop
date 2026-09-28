using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Bills
{
    /// <summary>Guest bill intent capture and host-state application.</summary>
    [ClientBehaviour]
    public sealed class BillsClientBehaviour : CoopBehaviour
    {
        private static BillsClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private BillsStateMessage _pendingState;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var handlersRegistered = false;
            var lifecycleSubscribed = false;
            try
            {
                ResetSessionState();
                BillsInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycleSubscribed = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.bills.client");
                _harmony.CreateClassProcessor(typeof(PayRentPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PayElectricPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PaySalaryPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(PayAllPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(AccrualPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Bills client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycleSubscribed)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (handlersRegistered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                ResetSessionState();
                BillsInterop.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(BillsStateMessage))]
        private void HandleState(MessageContext context, BillsStateMessage message)
        {
            if (_shutdown)
                return;

            _pendingState = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(BillsDeltaMessage))]
        private void HandleDelta(MessageContext context, BillsDeltaMessage message)
        {
            if (_shutdown)
                return;

            // Host pays the exact bill the guest asked to pay and reads back the zeroed values
            // (a rejected payment rolls the prediction back), so this confirms the prediction.
            PredictionApi.AckOrApply(message.PredictionId, () => ApplyDelta(message));
        }

        [MessageHandler(typeof(BillPopupMessage))]
        private void HandlePopup(MessageContext context, BillPopupMessage message)
        {
            if (_shutdown || !_context.InGame() || !BillsInterop.IsSceneReady())
                return;

            NotEnoughResourceTextPopup.ShowText((ENotEnoughResourceText)message.Text);
        }

        private void TryApplyPending()
        {
            if (_shutdown || _pendingState == null || _context == null || !_context.InGame()
                || !BillsInterop.IsSceneReady())
                return;

            Apply(_pendingState);
            BillsInterop.Refresh(BillsInterop.FindScreen());
            _pendingState = null;
        }

        private static void Apply(BillsStateMessage message)
        {
            ApplyBill(EBillType.Rent, message.Rent);
            ApplyBill(EBillType.Electric, message.Electric);
            ApplyBill(EBillType.Employee, message.Employee);
        }

        private static void ApplyDelta(BillsDeltaMessage message)
        {
            if (message.All)
            {
                ApplyBill(EBillType.Rent, message.Rent);
                ApplyBill(EBillType.Electric, message.Electric);
                ApplyBill(EBillType.Employee, message.Employee);
            }
            else
            {
                ApplyBill((EBillType)message.BillType, message.Value);
            }

            BillsInterop.Refresh(BillsInterop.FindScreen());
        }

        private static void ApplyBill(EBillType type, BillValue value)
        {
            var bill = CPlayerData.GetBill(type);
            bill.billDayPassed = value.DayPassed;
            bill.amountToPay = value.AmountToPay;
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            BillsInterop.Reset();
            TryApplyPending();
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
                ResetSessionState();
        }

        private struct PaymentCapture
        {
            public bool Armed;
            public BillValue Rent;
            public BillValue Electric;
            public BillValue Employee;
        }

        /// <summary>
        /// Captures the pre-payment values and marks the vanilla bill path as an action whose
        /// wallet side effect the host owns. The game performs the payment itself; the postfix
        /// observes the result and registers one post-hoc prediction (the host confirms with a
        /// delta or rejects with the generic rollback). Forced (day-rollover) payments are
        /// host-driven and are never predicted by a guest, but they stay inside the economy scope
        /// so their local wallet event is not forwarded as a second charge.
        /// </summary>
        private static void CapturePayment(byte billType, out PaymentCapture state)
        {
            state = default;
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return;

            state.Armed = true;
            state.Rent = Copy(CPlayerData.GetBill(EBillType.Rent));
            state.Electric = Copy(CPlayerData.GetBill(EBillType.Electric));
            state.Employee = Copy(CPlayerData.GetBill(EBillType.Employee));
            EconomyActionScope.Enter();
        }

        private static void ObservePayment(byte billType, bool forcePay, PaymentCapture state)
        {
            if (!state.Armed || forcePay)
                return;
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
                return;

            // Affordability and "no amount due" both leave the bills unchanged; nothing happened,
            // so there is no action to observe. (The wallet event is only queued by vanilla, so the
            // bill change - not the wallet - is the synchronous success signal.)
            if (!BillsChanged(state))
                return;

            var paid = PaidAmount(state, billType);
            PredictionApi.Predict(
                "bills",
                predictionId => client._context.Send(1, new BillPaymentMessage
                {
                    PredictionId = predictionId,
                    BillType = billType,
                }),
                () =>
                {
                    ZeroPaidBills(billType);
                    if (paid > 0.0001f)
                        CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(paid));
                    BillsInterop.Refresh(BillsInterop.FindScreen());
                },
                () =>
                {
                    ApplyBill(EBillType.Rent, state.Rent);
                    ApplyBill(EBillType.Electric, state.Electric);
                    ApplyBill(EBillType.Employee, state.Employee);
                    if (paid > 0.0001f)
                        CEventManager.QueueEvent(new CEventPlayer_AddCoin(paid, true));
                    BillsInterop.Refresh(BillsInterop.FindScreen());
                });
        }

        /// <summary>The amount the vanilla payment debits: each relevant bill's positive balance.</summary>
        private static float PaidAmount(PaymentCapture state, byte billType)
        {
            if (billType == 0)
                return Positive(state.Rent) + Positive(state.Electric) + Positive(state.Employee);
            return Positive(billType == (byte)EBillType.Rent ? state.Rent
                : billType == (byte)EBillType.Electric ? state.Electric
                : state.Employee);
        }

        private static float Positive(BillValue value)
            => value != null && value.AmountToPay > 0f ? value.AmountToPay : 0f;

        private static bool BillsChanged(PaymentCapture state)
            => !Unchanged(CPlayerData.GetBill(EBillType.Rent), state.Rent)
                || !Unchanged(CPlayerData.GetBill(EBillType.Electric), state.Electric)
                || !Unchanged(CPlayerData.GetBill(EBillType.Employee), state.Employee);

        private static bool Unchanged(BillData bill, BillValue value)
            => bill != null && value != null && bill.billDayPassed == value.DayPassed
                && Math.Abs(bill.amountToPay - value.AmountToPay) < 0.0001f;

        private static void ZeroPaidBills(byte billType)
        {
            if (billType == 0)
            {
                ApplyBill(EBillType.Rent, new BillValue());
                ApplyBill(EBillType.Electric, new BillValue());
                ApplyBill(EBillType.Employee, new BillValue());
            }
            else
            {
                ApplyBill((EBillType)billType, new BillValue());
            }
        }

        private static BillValue Copy(BillData bill)
            => new BillValue
            {
                DayPassed = bill == null ? 0 : bill.billDayPassed,
                AmountToPay = bill == null ? 0f : bill.amountToPay,
            };

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            ResetSessionState();
            BillsInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private static void ReleaseCapture(PaymentCapture state)
        {
            if (state.Armed)
                EconomyActionScope.Exit();
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayRentBill")]
        private static class PayRentPatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool forcePay, out PaymentCapture __state)
                => CapturePayment((byte)EBillType.Rent, out __state);

            [HarmonyPostfix]
            private static void Postfix(bool forcePay, PaymentCapture __state)
                => ObservePayment((byte)EBillType.Rent, forcePay, __state);

            [HarmonyFinalizer]
            private static void Finalizer(PaymentCapture __state) => ReleaseCapture(__state);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayElectricBill")]
        private static class PayElectricPatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool forcePay, out PaymentCapture __state)
                => CapturePayment((byte)EBillType.Electric, out __state);

            [HarmonyPostfix]
            private static void Postfix(bool forcePay, PaymentCapture __state)
                => ObservePayment((byte)EBillType.Electric, forcePay, __state);

            [HarmonyFinalizer]
            private static void Finalizer(PaymentCapture __state) => ReleaseCapture(__state);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPaySalaryBill")]
        private static class PaySalaryPatch
        {
            [HarmonyPrefix]
            private static void Prefix(bool forcePay, out PaymentCapture __state)
                => CapturePayment((byte)EBillType.Employee, out __state);

            [HarmonyPostfix]
            private static void Postfix(bool forcePay, PaymentCapture __state)
                => ObservePayment((byte)EBillType.Employee, forcePay, __state);

            [HarmonyFinalizer]
            private static void Finalizer(PaymentCapture __state) => ReleaseCapture(__state);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayAllBill")]
        private static class PayAllPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out PaymentCapture __state)
                => CapturePayment(0, out __state);

            [HarmonyPostfix]
            private static void Postfix(PaymentCapture __state)
                => ObservePayment(0, false, __state);

            [HarmonyFinalizer]
            private static void Finalizer(PaymentCapture __state) => ReleaseCapture(__state);
        }

        [HarmonyPatch(typeof(RentBillScreen), "EvaluateNewDayBill")]
        private static class AccrualPatch
        {
            // Day-rollover accrual is host-owned (the host advances the day and broadcasts the
            // resulting bill values). The guest runs the vanilla path so its local state matches,
            // but the accrual's forced auto-payments must not be forwarded as the guest's own
            // economy contributions; the host's authoritative bill delta then overwrites.
            [HarmonyPrefix]
            private static void Prefix() => EconomyActionScope.Enter();

            [HarmonyFinalizer]
            private static void Finalizer() => EconomyActionScope.Exit();
        }
    }
}
