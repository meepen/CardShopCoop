using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Modules.Economy;

namespace CardShopCoop.Modules.Bills
{
    /// <summary>All bill UI and private-member access stays behind this module boundary.</summary>
    internal static class BillsInterop
    {
        private static RentBillScreen _screen;

        internal static void Reset()
        {
            // Unity's overloaded null makes this explicit reset safe for both a scene unload and
            // a destroyed UI object that is still held by the managed cache.
            _screen = null;
        }
        /// <summary>Bill-local handle for the shared Economy reservation. The vanilla bill
        /// handler remains the sole owner of the actual wallet charge.</summary>
        internal readonly struct SpendToken
        {
            internal SpendToken(double amount, EconomyAuthority.HostSpendReservation reservation)
            {
                Reservation = reservation;
            }
            internal bool IsValid
            {
                get
                {
                    return Reservation != null;
                }
            }
            internal EconomyAuthority.HostSpendReservation Reservation
            {
                get;
            }
        }

        private static readonly MethodInfo MiEvaluateUi = AccessTools.Method(typeof(RentBillScreen),
            "EvaluateUI");
        private static readonly MethodInfo MiEvaluateNotification = AccessTools.Method(
            typeof(RentBillScreen), "EvaluateBillNotification");

        internal static RentBillScreen FindScreen()
        {
            if (_screen != null)
            {
                return _screen;
            }

            var all = Resources.FindObjectsOfTypeAll<RentBillScreen>();
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.scene.IsValid())
                {
                    return _screen = all[i];
                }
            }

            return null;
        }

        internal static void Refresh(RentBillScreen screen)
        {
            if (screen == null)
            {
                return;
            }

            MiEvaluateUi?.Invoke(screen, null);
            MiEvaluateNotification?.Invoke(screen, null);
        }

        internal static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool TryReservePayment(byte billType, out SpendToken token)
        {
            var amount = 0d;
            if (billType == 0)
            {
                var rent = CPlayerData.GetBill(EBillType.Rent);
                var electric = CPlayerData.GetBill(EBillType.Electric);
                var employee = CPlayerData.GetBill(EBillType.Employee);
                amount = (rent == null ? 0f : rent.amountToPay)
                    + (electric == null ? 0f : electric.amountToPay)
                    + (employee == null ? 0f : employee.amountToPay);
            }
            else if (Enum.IsDefined(typeof(EBillType), (int)billType))
            {
                var bill = CPlayerData.GetBill((EBillType)billType);
                amount = bill == null ? 0f : bill.amountToPay;
            }
            else
            {
                token = new SpendToken(0d, null);
                return false;
            }

            // Reserve against the same host ledger used by every other shared-wallet path. The
            // vanilla screen still owns the actual queued reduction and its UI feedback.
            if (!IsFinite((float)amount) || amount <= 0d
                || !EconomyAuthority.TryReserveHostSpend(amount, out var reservation))
            {
                token = new SpendToken(amount, null);
                return false;
            }

            token = new SpendToken(amount, reservation);
            return true;
        }

        internal static bool IsSceneReady()
            => FindScreen() != null;

        internal static bool CommitPayment(SpendToken token, Action payment)
        {
            if (!token.IsValid || payment == null)
                return false;

            return EconomyAuthority.RunWithVanillaSpend(token.Reservation, () =>
            {
                payment();
                return true;
            });
        }
    }
}
