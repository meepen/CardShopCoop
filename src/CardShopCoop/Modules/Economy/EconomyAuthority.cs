using System;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Economy
{
    /// <summary>
    /// The host wallet has one operation: admit a vanilla reduction before the game mutation
    /// starts.  There is intentionally no reservation or result ledger here.  The game event is
    /// the authoritative debit and callers only compensate their own local multi-step work.
    /// </summary>
    public static class EconomyAuthority
    {
        private static object _owner;
        private static CoopRuntimeContext _context;
        private static bool _suppressVanillaReduction;

        internal static bool Activate(object owner, CoopRuntimeContext context)
        {
            if (owner == null || context == null)
                return false;

            if (_owner != null && !ReferenceEquals(_owner, owner))
                throw new InvalidOperationException("The economy authority is already active.");

            _owner = owner;
            _context = context;
            return true;
        }

        internal static void Deactivate(object owner)
        {
            if (owner == null || !ReferenceEquals(_owner, owner))
                return;

            _suppressVanillaReduction = false;
            _owner = null;
            _context = null;
        }

        /// <summary>Checks the current authoritative wallet without reserving it.</summary>
        public static bool TryReserveHostSpend(double amount, out HostSpendReservation reservation)
        {
            reservation = null;
            if (_owner == null || !IsValidAmount(amount) || CPlayerData.m_CoinAmountDouble < amount)
                return false;

            reservation = new HostSpendReservation(amount);
            return true;
        }

        /// <summary>Queues the vanilla debit immediately, before the caller commits its work.</summary>
        public static bool QueueHostSpend(HostSpendReservation reservation)
            => QueueHostSpend(reservation, null);

        internal static bool QueueHostSpend(HostSpendReservation reservation,
            Action<bool> completion)
        {
            if (!Owns(reservation) || reservation.Consumed)
                return false;

            if (reservation.Amount > 0d)
            {
                if (SceneRef<CEventManager>.Get() == null)
                    throw new InvalidOperationException("The host wallet event manager is not ready.");

                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)reservation.Amount));
            }

            reservation.Consumed = true;
            completion?.Invoke(true);
            return true;
        }

        // Kept as a compatibility no-op for callers which abandon a local operation after the
        // debit was admitted.  A network result can never undo an authoritative wallet event.
        public static void ReleaseHostSpend(HostSpendReservation reservation)
        {
        }

        public static void CancelHostSpend(HostSpendReservation reservation)
        {
        }

        /// <summary>
        /// Vanilla checkout methods queue their own reduction.  The host debit above already
        /// represents that reduction, so suppress exactly the next producer event while the
        /// vanilla method runs.  This is a one-call guard, not an event identity ledger.
        /// </summary>
        internal static bool RunWithVanillaSpend(HostSpendReservation reservation, Func<bool> action)
        {
            if (!Owns(reservation) || action == null || !QueueHostSpend(reservation))
                return false;

            _suppressVanillaReduction = true;
            try
            {
                return action();
            }
            finally
            {
                _suppressVanillaReduction = false;
            }
        }

        internal static bool DeferVanillaEvent(CEvent evt)
        {
            if (!_suppressVanillaReduction || !(evt is CEventPlayer_ReduceCoin))
                return false;

            _suppressVanillaReduction = false;
            return true;
        }

        internal static double ReservedHostSpend => 0d;

        private static bool Owns(HostSpendReservation reservation)
            => reservation != null && reservation.Owner == typeof(EconomyAuthority)
                && !reservation.Consumed;

        private static bool IsValidAmount(double amount)
            => !double.IsNaN(amount) && !double.IsInfinity(amount) && amount >= 0d
                && amount <= float.MaxValue;

        public sealed class HostSpendReservation : IDisposable
        {
            private readonly Type _owner = typeof(EconomyAuthority);

            internal HostSpendReservation(double amount)
            {
                Amount = amount;
            }

            internal double Amount
            {
                get;
            }

            internal Type Owner => _owner;

            internal bool Consumed
            {
                get;
                set;
            }

            public void Dispose()
            {
                // Admission is the commit.  Disposal is intentionally inert.
            }
        }
    }
}
