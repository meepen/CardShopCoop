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

            PredictionApi.ApplyAuthoritative(message.PredictionId, () => ApplyDelta(message));
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

        private static bool PredictPayment(byte billType, bool forcePay)
        {
            var client = _active;
            if (client == null || client._shutdown || !client._joined || !client._context.InGame())
                return true;
            if (forcePay)
                return true;

            var rent = Copy(CPlayerData.GetBill(EBillType.Rent));
            var electric = Copy(CPlayerData.GetBill(EBillType.Electric));
            var employee = Copy(CPlayerData.GetBill(EBillType.Employee));
            PredictionApi.Predict(
                "bills",
                predictionId => client._context.Send(1, new BillPaymentMessage
                {
                    PredictionId = predictionId,
                    BillType = billType,
                }),
                () =>
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
            {
                return PredictPayment((byte)EBillType.Rent, forcePay);
            }
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayElectricBill")]
        private static class PayElectricPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool forcePay)
            {
                return PredictPayment((byte)EBillType.Electric, forcePay);
            }
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPaySalaryBill")]
        private static class PaySalaryPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(bool forcePay)
            {
                return PredictPayment((byte)EBillType.Employee, forcePay);
            }
        }

        [HarmonyPatch(typeof(RentBillScreen), "OnPressPayAllBill")]
        private static class PayAllPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                return PredictPayment(0, false);
            }
        }

        [HarmonyPatch(typeof(RentBillScreen), "EvaluateNewDayBill")]
        private static class AccrualPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => false;
        }
    }
}
