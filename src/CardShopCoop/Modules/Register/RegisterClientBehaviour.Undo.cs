using System;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Client half of register actions: the inverse of the small number of effects a full
    /// authoritative render cannot undo. The snapshot re-anchor restores the register, customer,
    /// cash stack and interaction state; the wallet is outside that state, so a rejected
    /// completion or card payment reverses exactly the coin event the game queued.</summary>
    public sealed partial class RegisterClientBehaviour
    {
        internal void UndoClaim(RegisterClaimIntent message)
        {
            var counter = message?.Counter;
            if (counter != null && counter.IsMannedByPlayer())
            {
                counter.OnPressEsc();
            }
        }

        internal void UndoRelease(RegisterReleaseIntent message)
        {
            var counter = message?.Counter;
            if (counter == null)
            {
                return;
            }

            // Re-man only when the store still says this player owns the register. A release
            // observed for a manning that never claimed would otherwise re-man a counter the host
            // never granted and trap the player in a denial loop.
            if (StateOf(counter)?.State?.Owner?.IsSelf == true && !counter.IsMannedByPlayer())
            {
                counter.OnMouseButtonUp();
            }
        }

        internal void UndoCardPayment(RegisterCardPaymentIntent message)
        {
            if (message == null || !message.WalletApplied)
            {
                return;
            }

            ReverseWallet(CheckoutWalletDelta(message.Value, 0d));
        }

        internal void UndoComplete(RegisterCompleteIntent message)
        {
            if (message == null)
            {
                return;
            }

            // The message carries the exact paid/change pair the local OnPressSpaceBar used, so
            // this reverses the coin event even when the action is undone as a follower of an
            // earlier authority. The register state itself is restored by the re-anchor render.
            ReverseWallet(CheckoutWalletDelta(message.Paid, message.Change));
        }

        /// <summary>The signed wallet delta vanilla's completed checkout queues: the shop's net
        /// income from the sale (positive) or the change it pays out (negative), rounded exactly as
        /// <c>InteractableCashierCounter.OnPressSpaceBar</c> does before queueing its coin event.
        /// A card payment has no change, so <paramref name="change"/> is zero and
        /// <paramref name="paid"/> is the charged amount.</summary>
        private static double CheckoutWalletDelta(double paid, double change)
        {
            var value = paid - change;
            return GameInstance.GetCurrencyConversionRate() > 1f
                ? (double)(float)Math.Round(value, 3, MidpointRounding.AwayFromZero)
                : (double)(float)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Reverses a completed checkout's wallet delta through the game's own coin events
        /// (a refund of income, or a charge-back of change the counter paid out). Only ever runs
        /// while an action is being reconciled, so the Hud economy observer does not forward it as
        /// a second contribution.</summary>
        private static void ReverseWallet(double delta)
        {
            if (delta > 0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)delta));
            }
            else if (delta < -0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)(0d - delta), true));
            }
        }

        /// <summary>Repopulates the cash screen from the carrier's current scanned bag. A local
        /// completion reset it; an authoritative render that is still giving change must put the
        /// scanned items back before the panel is shown.</summary>
        private static void RebuildCashScreen(InteractableCashierCounter counter, Customer customer)
        {
            var screen = RegisterInterop.CashScreen(counter);
            if (screen == null || customer == null)
            {
                return;
            }

            screen.ResetCounter();
            var total = 0d;
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && item.m_InteractableScanItem != null
                    && !item.m_InteractableScanItem.IsNotScanned())
                {
                    total += item.GetCurrentPrice();
                    screen.OnItemScanned(item.GetCurrentPrice(), item.GetItemType(), total);
                }
            }

            var cards = customer.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                if (card != null && !card.IsNotScanned())
                {
                    total += card.GetCurrentPrice();
                    screen.OnCardScanned(card.GetCurrentPrice(),
                        card.m_Card3dUI.m_CardUI.GetCardData(), total);
                }
            }
        }
    }
}
