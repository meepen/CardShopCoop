using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Bills
{
    /// <summary>Host authority for bill accrual, payment, and recovery.</summary>
    [ServerBehaviour]
    public sealed class BillsHostBehaviour : CoopBehaviour
    {
        private static BillsHostBehaviour _active;
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private int _forwardedRequester;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                BillsInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.bills.host");
                _harmony.CreateClassProcessor(typeof(BillChangedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BillSetPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(BillPopupPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Bills host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (handlersRegistered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                BillsInterop.Reset();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
                return;

            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
                _fullyJoined.Remove(connection.Id);
        }

        [MessageHandler(typeof(BillPaymentMessage))]
        private void HandlePayment(MessageContext context, BillPaymentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
                return;

            var peer = context.Connection.Id;
            var accepted = false;
            try
            {
                if (message.BillType != 0
                    && !Enum.IsDefined(typeof(EBillType), (int)message.BillType))
                {
                    Rollback(context, message);
                }
                else
                {
                    var screen = BillsInterop.FindScreen();
                    if (screen == null)
                    {
                        Rollback(context, message);
                    }
                    else if (!BillsInterop.TryReservePayment(message.BillType, out var spend))
                    {
                        NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                        Rollback(context, message);
                    }
                    else
                    {
                        _forwardedRequester = peer;
                        _applyingIntent = true;
                        try
                        {
                            accepted = BillsInterop.CommitPayment(spend, () =>
                            {
                                switch (message.BillType)
                                {
                                    case 0:
                                        screen.OnPressPayAllBill();
                                        break;
                                    case (byte)EBillType.Rent:
                                        screen.OnPressPayRentBill();
                                        break;
                                    case (byte)EBillType.Electric:
                                        screen.OnPressPayElectricBill();
                                        break;
                                    case (byte)EBillType.Employee:
                                        screen.OnPressPaySalaryBill();
                                        break;
                                }
                            });
                        }
                        finally
                        {
                            _applyingIntent = false;
                        }
                        _forwardedRequester = 0;
                        if (accepted)
                            BroadcastDelta(BuildDelta(message.BillType, message.PredictionId));
                        else
                            Rollback(context, message);
                    }
                }
            }
            catch (Exception exception)
            {
                _forwardedRequester = 0;
                CoopPlugin.Log.LogError("Bills payment failed for peer " + peer + ": " + exception);
                Rollback(context, message);
            }
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame())
                return;

            _context.Send(peer, BuildState());
        }

        private void BroadcastDelta(BillsDeltaMessage message)
        {
            if (_shutdown || _context == null || !_context.InGame() || message == null)
                return;

            _context.Broadcast(message);
        }

        private static BillsStateMessage BuildState()
        {
            var rent = CPlayerData.GetBill(EBillType.Rent);
            var electric = CPlayerData.GetBill(EBillType.Electric);
            var employee = CPlayerData.GetBill(EBillType.Employee);
            return new BillsStateMessage
            {
                Rent = new BillValue
                {
                    DayPassed = rent.billDayPassed,
                    AmountToPay = rent.amountToPay,
                },
                Electric = new BillValue
                {
                    DayPassed = electric.billDayPassed,
                    AmountToPay = electric.amountToPay,
                },
                Employee = new BillValue
                {
                    DayPassed = employee.billDayPassed,
                    AmountToPay = employee.amountToPay,
                },
            };
        }

        private static BillsDeltaMessage BuildDelta(byte billType, Guid predictionId)
        {
            var message = new BillsDeltaMessage
            {
                PredictionId = predictionId,
                BillType = billType,
                All = billType == 0,
            };
            if (message.All)
            {
                message.Rent = ToValue(CPlayerData.GetBill(EBillType.Rent));
                message.Electric = ToValue(CPlayerData.GetBill(EBillType.Electric));
                message.Employee = ToValue(CPlayerData.GetBill(EBillType.Employee));
            }
            else
            {
                message.Value = ToValue(CPlayerData.GetBill((EBillType)billType));
            }

            return message;
        }

        private static BillValue ToValue(BillData bill)
            => new BillValue
            {
                DayPassed = bill == null ? 0 : bill.billDayPassed,
                AmountToPay = bill == null ? 0f : bill.amountToPay,
            };

        private static void Rollback(MessageContext context, BillPaymentMessage message)
        {
            if (context?.Connection != null && message != null
                && message.PredictionId != Guid.Empty)
                PredictionApi.Rollback(_active._context, context.Connection.Id, message.PredictionId);
        }

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            BillsInterop.Reset();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            _fullyJoined.Clear();
            BillsInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CPlayerData), "UpdateBill")]
        private static class BillChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EBillType billType)
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta((byte)billType, Guid.Empty));
            }
        }

        [HarmonyPatch(typeof(CPlayerData), "SetBill")]
        private static class BillSetPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EBillType billType)
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta((byte)billType, Guid.Empty));
            }
        }

        [HarmonyPatch(typeof(NotEnoughResourceTextPopup), "ShowText")]
        private static class BillPopupPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ENotEnoughResourceText __0)
            {
                var host = _active;
                if (host == null || host._forwardedRequester <= 0 || host._context == null)
                    return true;

                host._context.Send(host._forwardedRequester,
                    new BillPopupMessage { Text = (int)__0 });
                return false;
            }
        }
    }
}
