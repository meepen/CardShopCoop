using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Host policy for register intents. Each action has its own validator and can only
    /// carry the fields it actually uses, so the handler receives an already-valid action.</summary>
    public sealed partial class RegisterHostBehaviour
    {
        [IntentValidator]
        private IntentValidation ValidateClaim(CoopMessageContext context, RegisterClaimIntent message)
        {
            if (!TryAdmit(context, message, out var denial, out _, out var owner))
            {
                return denial;
            }

            if (owner != null)
            {
                return IntentValidation.Deny("register is owned by another player");
            }

            if (message.Counter.IsMannedByPlayer())
            {
                return IntentValidation.Deny("register is manned by another player");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateRelease(CoopMessageContext context,
            RegisterReleaseIntent message)
        {
            if (!TryAdmit(context, message, out var denial, out var sender, out var owner))
            {
                return denial;
            }

            return owner != null && owner.Equals(sender)
                ? IntentValidation.Allow()
                : IntentValidation.Deny("you do not own this register");
        }

        [IntentValidator]
        private IntentValidation ValidateScanItem(CoopMessageContext context,
            RegisterScanItemIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter,
                out var customer))
            {
                return denial;
            }

            if (counter.m_CashierCounterState != ECashierCounterState.ScanningItem)
            {
                return IntentValidation.Deny("the register is not scanning");
            }

            var items = customer.GetItemInBagList();
            if (message.Slot < 0 || items == null || message.Slot >= items.Count
                || items[message.Slot] == null
                || items[message.Slot].m_InteractableScanItem == null
                || !items[message.Slot].m_InteractableScanItem.IsNotScanned())
            {
                return IntentValidation.Deny("the item slot cannot be scanned");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateScanCard(CoopMessageContext context,
            RegisterScanCardIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter,
                out var customer))
            {
                return denial;
            }

            if (counter.m_CashierCounterState != ECashierCounterState.ScanningItem)
            {
                return IntentValidation.Deny("the register is not scanning");
            }

            var cards = customer.GetCardInBagList();
            if (message.Slot < 0 || cards == null || message.Slot >= cards.Count
                || cards[message.Slot] == null || !cards[message.Slot].IsNotScanned())
            {
                return IntentValidation.Deny("the card slot cannot be scanned");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateTakePayment(CoopMessageContext context,
            RegisterTakePaymentIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter,
                out var customer))
            {
                return denial;
            }

            if (counter.m_CashierCounterState != ECashierCounterState.TakingCash
                || customer.m_CustomerCash == null
                || customer.m_CustomerCash.m_IsCard != message.IsCard)
            {
                return IntentValidation.Deny("the payment method does not match the customer");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateCardPayment(CoopMessageContext context,
            RegisterCardPaymentIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter, out _))
            {
                return denial;
            }

            if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                || !RegisterInterop.IsUsingCard(counter)
                || !RegisterInterop.IsFinite(message.Value)
                || Math.Abs(message.Value - RegisterInterop.Total(counter)) > (double)0.01f)
            {
                return IntentValidation.Deny("the card amount does not match the total");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateChange(CoopMessageContext context,
            RegisterChangeIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter, out _))
            {
                return denial;
            }

            if (counter.m_CashierCounterState != ECashierCounterState.GivingChange
                || RegisterInterop.IsUsingCard(counter)
                || RegisterInterop.FindChange(counter, message.Slot, message.IsCoin) == null)
            {
                return IntentValidation.Deny("that denomination is not at this register");
            }

            return IntentValidation.Allow();
        }

        [IntentValidator]
        private IntentValidation ValidateComplete(CoopMessageContext context,
            RegisterCompleteIntent message)
        {
            if (!TryAdmitCheckout(context, message, out var denial, out var counter, out _))
            {
                return denial;
            }

            return counter.m_CashierCounterState == ECashierCounterState.GivingChange
                && RegisterInterop.IsChangeReady(counter)
                    ? IntentValidation.Allow()
                    : IntentValidation.Deny("the checkout is not ready to complete");
        }

        private bool TryAdmit(CoopMessageContext context, RegisterIntent message,
            out IntentValidation denial, out PlayerRef sender, out PlayerRef owner)
        {
            denial = default;
            sender = null;
            owner = null;
            if (_shutdown || context?.Connection == null || message == null)
            {
                denial = IntentValidation.Deny("session is not active");
                return false;
            }

            if (!context.IsAuthenticated || !context.InGame)
            {
                denial = IntentValidation.Deny("player is not in the game");
                return false;
            }

            if (message.Counter == null)
            {
                denial = IntentValidation.Deny("register is gone");
                return false;
            }

            sender = context.Sender;
            if (sender == null)
            {
                denial = IntentValidation.Deny("player is not in the roster yet");
                return false;
            }

            owner = StateOf(message.Counter)?.State?.Owner;
            return true;
        }

        private bool TryAdmitCheckout(CoopMessageContext context, RegisterIntent message,
            out IntentValidation denial, out InteractableCashierCounter counter,
            out Customer customer)
        {
            counter = null;
            customer = null;
            if (!TryAdmit(context, message, out denial, out var sender, out var owner))
            {
                return false;
            }

            if (owner == null || !owner.Equals(sender))
            {
                denial = IntentValidation.Deny("you do not own this register");
                return false;
            }

            counter = message.Counter;
            customer = counter.m_CurrentCustomer;
            if (customer == null || !customer.m_IsActive)
            {
                denial = IntentValidation.Deny("no customer at the register");
                return false;
            }

            return true;
        }

        [IntentRejected]
        private void OnClaimRejected(CoopMessageContext context, RegisterClaimIntent message,
            string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnReleaseRejected(CoopMessageContext context, RegisterReleaseIntent message,
            string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnScanItemRejected(CoopMessageContext context, RegisterScanItemIntent message,
            string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnScanCardRejected(CoopMessageContext context, RegisterScanCardIntent message,
            string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnTakePaymentRejected(CoopMessageContext context,
            RegisterTakePaymentIntent message, string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnCardPaymentRejected(CoopMessageContext context,
            RegisterCardPaymentIntent message, string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnChangeRejected(CoopMessageContext context, RegisterChangeIntent message,
            string reason) => Reject(context, message, reason);

        [IntentRejected]
        private void OnCompleteRejected(CoopMessageContext context, RegisterCompleteIntent message,
            string reason) => Reject(context, message, reason);

        private void Reject(CoopMessageContext context, RegisterIntent message, string reason)
        {
            if (_shutdown || context?.Connection == null || message?.Counter == null)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("[register] rejected " + message.GetType().Name + " counter="
                + message.Counter.name + ": " + reason);
            if (message is RegisterClaimIntent || message is RegisterReleaseIntent)
            {
                // Ownership decisions have no checkout state to re-anchor; the framework's
                // action rollback is enough.
                return;
            }

            PublishRejectionState(message.Counter, message.ActionId);
        }
    }
}
