using System;

namespace CardShopCoop.Modules.Register
{
    /// <summary>The game path for one register action. The same method runs when the host applies a
    /// client's command and when a newer authoritative state makes the client replay a surviving
    /// local action. Role differences live here, not in the DTOs.</summary>
    internal static class RegisterActions
    {
        private static int _hostApplying;

        /// <summary>True while a register command is driving the host's game methods. The host
        /// hooks use it to publish exactly one snapshot from the handler instead of the
        /// intermediate states the game methods emit.</summary>
        internal static bool HostApplying => _hostApplying > 0;

        private static void ApplyOnHost(Action action)
        {
            if (CoopCore.Role != CoopRole.Host)
            {
                action();
                return;
            }

            _hostApplying++;
            try
            {
                action();
            }
            finally
            {
                _hostApplying--;
            }
        }

        internal static void Claim(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            if (CoopCore.Role == CoopRole.Host)
            {
                ApplyOnHost(() =>
                {
                    counter.StopCurrentWorker();
                    RegisterHighlight.Clear(counter);
                });
                return;
            }

            if (!counter.IsMannedByPlayer())
            {
                counter.OnMouseButtonUp();
            }
        }

        internal static void Release(InteractableCashierCounter counter)
        {
            if (counter == null || CoopCore.Role == CoopRole.Host)
            {
                return;
            }

            if (counter.IsMannedByPlayer())
            {
                counter.OnPressEsc();
            }
        }

        internal static void ScanItem(InteractableCashierCounter counter, int slot)
        {
            var customer = counter?.m_CurrentCustomer;
            var items = customer?.GetItemInBagList();
            if (items == null || slot < 0 || slot >= items.Count)
            {
                return;
            }

            var scanItem = items[slot]?.m_InteractableScanItem;
            if (scanItem == null || !scanItem.IsNotScanned())
            {
                return;
            }

            ApplyOnHost(scanItem.OnMouseButtonUp);
        }

        internal static void ScanCard(InteractableCashierCounter counter, int slot)
        {
            var customer = counter?.m_CurrentCustomer;
            var cards = customer?.GetCardInBagList();
            if (cards == null || slot < 0 || slot >= cards.Count)
            {
                return;
            }

            var card = cards[slot];
            if (card == null || !card.IsNotScanned())
            {
                return;
            }

            ApplyOnHost(card.OnMouseButtonUp);
        }

        internal static void TakePayment(InteractableCashierCounter counter, bool isCard)
        {
            var cash = counter?.m_CurrentCustomer?.m_CustomerCash;
            if (cash == null || cash.m_IsCard != isCard
                || counter.m_CashierCounterState != ECashierCounterState.TakingCash)
            {
                return;
            }

            ApplyOnHost(cash.OnMouseButtonUp);
        }

        /// <summary>Applies a card payment. Returns true when the card amount matched, so vanilla
        /// zeroed the checkout total and queued its coin event; a mismatched amount leaves the total
        /// and the wallet untouched.</summary>
        internal static bool CardPayment(InteractableCashierCounter counter, double value)
        {
            if (counter == null
                || counter.m_CashierCounterState != ECashierCounterState.GivingChange
                || !RegisterInterop.IsUsingCard(counter))
            {
                return false;
            }

            ApplyOnHost(() => counter.EvaluateCreditCard(value));
            return RegisterInterop.Total(counter) <= 0.0001d;
        }

        internal static void Change(InteractableCashierCounter counter, int slot, bool isCoin,
            bool takeBack)
        {
            var change = RegisterInterop.FindChange(counter, slot, isCoin);
            if (change == null)
            {
                return;
            }

            ApplyOnHost(() =>
            {
                if (takeBack)
                {
                    change.OnRightMouseButtonUp();
                }
                else
                {
                    change.OnMouseButtonUp();
                }
            });
        }

        /// <summary>Completes a checkout. Returns true when vanilla actually finished it (the change
        /// was ready), so it zeroed the checkout total and queued its coin event; a press that
        /// leaves the change unfinished touched neither the total nor the wallet.</summary>
        internal static bool Complete(InteractableCashierCounter counter)
        {
            if (counter == null
                || counter.m_CashierCounterState != ECashierCounterState.GivingChange)
            {
                return false;
            }

            ApplyOnHost(counter.OnPressSpaceBar);
            return RegisterInterop.Total(counter) <= 0.0001d;
        }
    }
}
