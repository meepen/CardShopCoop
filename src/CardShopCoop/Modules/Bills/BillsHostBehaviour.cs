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
        private bool _shutdown;
        private bool _initialized;
        private ICoopPeer _forwardedRequester;
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
            var peer = PeerHandle.ForConnection(connection);
            if (peer == null)
            {
                return;
            }

            SendState(peer);
        }

        [MessageHandler]
        private ActionOutcome HandlePayment(CoopMessageContext context, BillPaymentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
            {
                return ActionOutcome.Reject("bills payment sender is not an admitted peer");
            }

            var peer = context.Peer;
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
                        _forwardedRequester = null;
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
                    _forwardedRequester = null;
                }

                if (!accepted)
                {
                    return RejectPayment(context, message, "the host refused the payment");
                }

                var state = BuildDelta(message.BillType);
                if (peer != null)
                {
                    Context.BroadcastExcept(peer, state);
                }

                return ActionOutcome.Applied(new BillPaymentResponse { State = state });
            }
            catch (Exception exception)
            {
                _forwardedRequester = null;
                CoopPlugin.Log.LogError("Bills payment failed for peer "
                    + (peer == null ? "?" : peer.Name) + ": " + exception);
                return RejectPayment(context, message, "bills payment failed");
            }
        }

        private void SendState(ICoopPeer peer)
        {
            if (_shutdown || !RuntimeContext.InGame() || peer == null)
            {
                return;
            }

            peer.Send(BuildState());
        }

        private void BroadcastDelta(BillsDeltaMessage message)
        {
            if (_shutdown || !RuntimeContext.InGame() || message == null)
            {
                return;
            }

            Context.Broadcast(message);
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

        private static BillsDeltaMessage BuildDelta(byte billType)
        {
            var message = new BillsDeltaMessage
            {
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

        /// <summary>Answers a rejected payment with the host's absolute bills. The response
        /// settles the requester's prediction (undo then merge), so the refusal never needs a
        /// second generic rollback. When the host cannot read its bills (leaving the game), the
        /// rejection carries no state and the guest's optimistic payment is simply reversed.</summary>
        private static ActionOutcome RejectPayment(CoopMessageContext context,
            BillPaymentMessage message, string reason)
        {
            var active = _active;
            if (active == null || context?.Peer == null || message == null
                || !active.TryBuildRejectionState(message, out var refresh))
            {
                return ActionOutcome.Reject(reason);
            }

            return ActionOutcome.Reject(reason, new BillPaymentResponse { State = refresh });
        }

        private bool TryBuildRejectionState(BillPaymentMessage message,
            out BillsDeltaMessage refresh)
        {
            refresh = null;
            if (_shutdown || !RuntimeContext.InGame())
            {
                return false;
            }

            refresh = BuildDelta(0);
            CoopPlugin.Log.LogInfo("[bills] rejection state to "
                + (message.Peer == null ? "?" : message.Peer.Name)
                + " rent=" + refresh.Rent.AmountToPay + " electric=" + refresh.Electric.AmountToPay
                + " employee=" + refresh.Employee.AmountToPay
                + " pred=" + message.ActionId + ".");
            return true;
        }

        private bool IsPeerMessage(CoopMessageContext context)
            => !_shutdown && context?.Peer != null && context.IsAuthenticated
                && context.Peer.State == ConnectionState.FullyJoined;

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

            _forwardedRequester = null;
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

                _active.BroadcastDelta(BuildDelta((byte)billType));
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

                _active.BroadcastDelta(BuildDelta((byte)billType));
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
                if (host == null || host._forwardedRequester == null)
                {
                    return;
                }

                host._forwardedRequester.Send(new BillPopupMessage { Text = (int)__0 });
            }
        }
    }
}
