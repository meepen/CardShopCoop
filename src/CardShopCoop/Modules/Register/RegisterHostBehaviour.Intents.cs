using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Register
{
    /// <summary>The host command lane. The router applies the message's <c>OnApply</c> inside the
    /// host-apply scope; the handler only publishes the resulting snapshot for that action. The
    /// game hooks are suppressed during the apply, so exactly one authoritative state goes out.</summary>
    public sealed partial class RegisterHostBehaviour
    {
        [MessageHandler]
        private void HandleClaim(CoopMessageContext context, RegisterClaimIntent message)
        {
            var counter = message?.Counter;
            if (context == null || counter == null)
            {
                return;
            }

            PublishNow(counter, context.Sender, message.ActionId);
        }

        [MessageHandler]
        private void HandleRelease(CoopMessageContext context, RegisterReleaseIntent message)
        {
            var counter = message?.Counter;
            if (counter == null)
            {
                return;
            }

            // A release has no game action; clear the owner and publish that state.
            PublishNow(counter, null, message.ActionId);
        }

        [MessageHandler]
        private void HandleScanItem(CoopMessageContext context, RegisterScanItemIntent message)
        {
            PublishCheckout(message?.Counter, message?.ActionId ?? default);
        }

        [MessageHandler]
        private void HandleScanCard(CoopMessageContext context, RegisterScanCardIntent message)
        {
            PublishCheckout(message?.Counter, message?.ActionId ?? default);
        }

        [MessageHandler]
        private void HandleTakePayment(CoopMessageContext context, RegisterTakePaymentIntent message)
        {
            PublishCheckout(message?.Counter, message?.ActionId ?? default);
        }

        [MessageHandler]
        private void HandleCardPayment(CoopMessageContext context, RegisterCardPaymentIntent message)
        {
            var counter = message?.Counter;
            if (counter == null)
            {
                return;
            }

            PublishNow(counter, ResolveOwner(counter), message.ActionId);
            // The game's own credit inside OnPressSpaceBar is gated on m_IsMannedByPlayer, which is
            // false here (the host never physically manned the counter), so credit the shared
            // tutorial, report count, and achievement on the host's authoritative state.
            CreditGuestCheckout();
        }

        [MessageHandler]
        private void HandleChange(CoopMessageContext context, RegisterChangeIntent message)
        {
            PublishCheckout(message?.Counter, message?.ActionId ?? default);
        }

        [MessageHandler]
        private void HandleComplete(CoopMessageContext context, RegisterCompleteIntent message)
        {
            var counter = message?.Counter;
            if (counter == null)
            {
                return;
            }

            PublishNow(counter, ResolveOwner(counter), message.ActionId);
            CreditGuestCheckout();
        }

        private void PublishCheckout(InteractableCashierCounter counter, System.Guid actionId)
        {
            if (counter == null)
            {
                return;
            }

            PublishNow(counter, ResolveOwner(counter), actionId);
        }

        private static void CreditGuestCheckout()
        {
            CPlayerData.m_GameReportDataCollect.manualCheckoutCount++;
            CPlayerData.m_GameReportDataCollectPermanent.manualCheckoutCount++;
            AchievementManager.OnCustomerFinishCheckout(
                CPlayerData.m_GameReportDataCollectPermanent.manualCheckoutCount);
            TutorialManager.AddTaskValue(ETutorialTaskCondition.CheckoutCustomer, 1f);
        }
    }
}
