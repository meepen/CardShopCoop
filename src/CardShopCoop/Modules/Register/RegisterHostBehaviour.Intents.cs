using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Register
{
    /// <summary>The host command lane. The handler runs the message's <c>OnApply</c> inside the
    /// host-apply scope, then publishes the resulting snapshot for that action. The game hooks are
    /// suppressed during the apply, so exactly one authoritative state goes out.</summary>
    public sealed partial class RegisterHostBehaviour
    {
        [MessageHandler]
        private ActionOutcome HandleClaim(CoopMessageContext context, RegisterClaimIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            var counter = message.Counter;
            if (context == null || counter == null)
            {
                return ActionOutcome.Applied();
            }

            PublishNow(counter, context.Sender, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleRelease(CoopMessageContext context, RegisterReleaseIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            var counter = message.Counter;
            if (counter == null)
            {
                return ActionOutcome.Applied();
            }

            // A release has no game action; clear the owner and publish that state.
            PublishNow(counter, null, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleScanItem(CoopMessageContext context,
            RegisterScanItemIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            PublishCheckout(message.Counter, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleScanCard(CoopMessageContext context,
            RegisterScanCardIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            PublishCheckout(message.Counter, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleTakePayment(CoopMessageContext context,
            RegisterTakePaymentIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            PublishCheckout(message.Counter, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleCardPayment(CoopMessageContext context,
            RegisterCardPaymentIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            var counter = message.Counter;
            if (counter == null)
            {
                return ActionOutcome.Applied();
            }

            PublishNow(counter, ResolveOwner(counter), message.ActionId);
            // The game's own credit inside OnPressSpaceBar is gated on m_IsMannedByPlayer, which is
            // false here (the host never physically manned the counter), so credit the shared
            // tutorial, report count, and achievement on the host's authoritative state.
            CreditGuestCheckout();
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleChange(CoopMessageContext context, RegisterChangeIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            PublishCheckout(message.Counter, message.ActionId);
            return ActionOutcome.Applied();
        }

        [MessageHandler]
        private ActionOutcome HandleComplete(CoopMessageContext context,
            RegisterCompleteIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("register command is missing");
            }

            message.OnApply();
            var counter = message.Counter;
            if (counter == null)
            {
                return ActionOutcome.Applied();
            }

            PublishNow(counter, ResolveOwner(counter), message.ActionId);
            CreditGuestCheckout();
            return ActionOutcome.Applied();
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
