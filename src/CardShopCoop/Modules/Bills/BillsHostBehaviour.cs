using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Bills
{
    /// <summary>Host authority for bill accrual, payment, and recovery.</summary>
    [ServerBehaviour]
    public sealed class BillsHostBehaviour : CoopBehaviour
    {
        private static BillsHostBehaviour _active;
        private readonly HashSet<int> _fullyJoined = new();
        private bool _shutdown;
        private bool _initialized;
        private int _forwardedRequester;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            try
            {
                BillsInterop.Reset();
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                Harmony.CreateClassProcessor(typeof(BillChangedPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BillSetPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(BillPopupPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Bills host initialization failed: " + exception);
                SceneManager.sceneLoaded -= OnSceneLoaded;

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                BillsInterop.Reset();
                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _fullyJoined.Remove(connection.Id);
            }
        }

        [MessageHandler]
        private ActionOutcome HandlePayment(MessageContext context, BillPaymentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
            {
                return ActionOutcome.Reject("bills payment sender is not an admitted peer");
            }

            var peer = context.Connection.Id;
            try
            {
                if (message.BillType != 0
                    && !Enum.IsDefined(typeof(EBillType), (int)message.BillType))
                {
                    return RejectPayment(context, message, "unsupported bill type");
                }

                var screen = BillsInterop.FindScreen();
                if (screen == null)
                {
                    return RejectPayment(context, message, "bill screen is not available");
                }

                if (!BillsInterop.TryReservePayment(message.BillType, out var spend))
                {
                    // The guest's intent was predicted locally, so the refusal feedback
                    // belongs to the requester, not (only) to the host screen.
                    _forwardedRequester = peer;
                    try
                    {
                        NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                    }
                    finally
                    {
                        _forwardedRequester = 0;
                    }

                    return RejectPayment(context, message, "host wallet could not cover the payment");
                }

                _forwardedRequester = peer;
                _applyingIntent = true;
                bool accepted;
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
                    _forwardedRequester = 0;
                }

                if (!accepted)
                {
                    return RejectPayment(context, message, "the host refused the payment");
                }

                BroadcastDelta(BuildDelta(message.BillType, message.ActionId));
                return ActionOutcome.Applied();
            }
            catch (Exception exception)
            {
                _forwardedRequester = 0;
                CoopPlugin.Log.LogError("Bills payment failed for peer " + peer + ": " + exception);
                return RejectPayment(context, message, "bills payment failed");
            }
        }

        private void SendState(int peer)
        {
            if (_shutdown || !RuntimeContext.InGame())
            {
                return;
            }

            RuntimeContext.Send(peer, BuildState());
        }

        private void BroadcastDelta(BillsDeltaMessage message)
        {
            if (_shutdown || !RuntimeContext.InGame() || message == null)
            {
                return;
            }

            RuntimeContext.Broadcast(message);
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

        private static BillsDeltaMessage BuildDelta(byte billType, Guid actionId)
        {
            var message = new BillsDeltaMessage
            {
                ActionId = actionId,
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

        /// <summary>Answers a rejected payment with the authoritative bills under the rejected
        /// prediction id. Those values settle the requester's prediction, so the handler reports
        /// the command as already applied instead of asking the framework for a second generic
        /// rollback. When no authority can be sent (the host is leaving the game), the caller gets
        /// a plain rejection so the framework still reverses the guest's optimistic payment.</summary>
        private static ActionOutcome RejectPayment(MessageContext context,
            BillPaymentMessage message, string reason)
        {
            var active = _active;
            if (active == null || context?.Connection == null || message == null)
            {
                return ActionOutcome.Reject(reason);
            }

            return active.SendRejectionState(context.Connection.Id, message)
                ? ActionOutcome.Applied()
                : ActionOutcome.Reject(reason);
        }

        private bool SendRejectionState(int peer, BillPaymentMessage message)
        {
            if (_shutdown || !RuntimeContext.InGame())
            {
                return false;
            }

            var refresh = BuildDelta(0, message.ActionId);
            CoopPlugin.Log.LogInfo("[bills] rejection refresh to conn " + peer
                + " rent=" + refresh.Rent.AmountToPay + " electric=" + refresh.Electric.AmountToPay
                + " employee=" + refresh.Employee.AmountToPay
                + " pred=" + message.ActionId + ".");
            RuntimeContext.Send(peer, refresh);
            return true;
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
            {
                return;
            }

            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _fullyJoined.Clear();
            BillsInterop.Reset();
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CPlayerData), "UpdateBill")]
        private static class BillChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EBillType billType)
            {
                if (_active == null || _active._applyingIntent)
                {
                    return;
                }

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
                {
                    return;
                }

                _active.BroadcastDelta(BuildDelta((byte)billType, Guid.Empty));
            }
        }

        [HarmonyPatch(typeof(NotEnoughResourceTextPopup), "ShowText")]
        private static class BillPopupPatch
        {
            // Feedback routing (not a state mutation): while the host replays a guest's payment,
            // the failure popup still shows on the host (vanilla runs) and is additionally relayed
            // to the requesting guest.
            [HarmonyPostfix]
            private static void Postfix(ENotEnoughResourceText __0)
            {
                var host = _active;
                if (host == null || host._forwardedRequester <= 0)
                {
                    return;
                }

                host.RuntimeContext.Send(host._forwardedRequester,
                    new BillPopupMessage { Text = (int)__0 });
            }
        }
    }
}
