using System;
using CardShopCoop.Attributes;
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

        /// <summary>
        /// Client intent for a bill payment. In a session the vanilla payment does not run: the
        /// same bill zeroing the game would do is applied locally as a prediction (so the bill
        /// panel and total update immediately), the intent is sent, and the host's accepted delta
        /// retires the prediction or its rollback restores the captured values. The wallet is
        /// host-owned - the guest never queues a coin event - so the mirror only moves when the
        /// host's authoritative wallet snapshot arrives, and a rejection has nothing to refund.
        /// </summary>
        private static bool BeginPayment(byte billType, bool forcePay)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || client._context == null
                || !client._context.InGame())
            {
                return true;
            }

            // Forced auto-payments belong to the host's day rollover (the guest's accrual is
            // suppressed), so a guest never predicts one.
            if (forcePay)
                return true;

            var rent = Copy(CPlayerData.GetBill(EBillType.Rent));
            var electric = Copy(CPlayerData.GetBill(EBillType.Electric));
            var employee = Copy(CPlayerData.GetBill(EBillType.Employee));
            var amount = PaidAmount(rent, electric, employee, billType);

            // Nothing due, or the mirror cannot afford it: leave the vanilla path to show its own
            // feedback and queue nothing.
            if (amount <= 0.0001f || CPlayerData.m_CoinAmountDouble < amount)
                return true;

            ZeroPaidBills(billType);
            BillsInterop.Refresh(BillsInterop.FindScreen());
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
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
                    BillsInterop.Refresh(BillsInterop.FindScreen());
                },
                () =>
                {
                    ApplyBill(EBillType.Rent, rent);
                    ApplyBill(EBillType.Electric, electric);
                    ApplyBill(EBillType.Employee, employee);
                    BillsInterop.Refresh(BillsInterop.FindScreen());
                });
            return false;
        }

        /// <summary>The amount the vanilla payment debits: each relevant bill's positive balance.</summary>
        private static float PaidAmount(BillValue rent, BillValue electric, BillValue employee,
            byte billType)
        {
            if (billType == 0)
                return Positive(rent) + Positive(electric) + Positive(employee);
            return Positive(billType == (byte)EBillType.Rent ? rent
                : billType == (byte)EBillType.Electric ? electric
                : employee);
        }

        private static float Positive(BillValue value)
            => value != null && value.AmountToPay > 0f ? value.AmountToPay : 0f;

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

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayRentBill")]
        private static class PayRentPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool forcePay)
                => BeginPayment((byte)EBillType.Rent, forcePay);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayElectricBill")]
        private static class PayElectricPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool forcePay)
                => BeginPayment((byte)EBillType.Electric, forcePay);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPaySalaryBill")]
        private static class PaySalaryPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool forcePay)
                => BeginPayment((byte)EBillType.Employee, forcePay);
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayAllBill")]
        private static class PayAllPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => BeginPayment(0, false);
        }

        [HarmonyPatch(typeof(RentBillScreen), "EvaluateNewDayBill")]
        private static class AccrualPatch
        {
            // Bill accrual is host-owned ambient state: the host advances the day, accrues on its
            // own shop state, and broadcasts the resulting values (UpdateBill/SetBill deltas plus
            // the forced auto-payments, whose wallet events the host owns). The guest's dawn
            // sequence runs via GameTime (ResetSunlightIntensity -> DelayUpdateEnv -> OnDayStarted)
            // on every host day advance and on a join at 08:00, but its accrual would compute from
            // guest-local inputs (light-on time, its worker mirror) and ADD +1 day/amounts on top of
            // the host deltas already applied - a guest bill table the host does not have. A later
            // Pay All then charges the guest's larger local total, the host charges its real total,
            // and the difference is credited back. The guest therefore never accrues; it only
            // applies the host's bill state.
            [HarmonyPrefix]
            private static bool Prefix() => false;
        }
    }
}
