using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Register
{
    /// <summary>The one register command shape. An <see cref="ActionMessage"/> is recorded on the
    /// client and applied on the host; the concrete type is the action, so a handler only receives
    /// fields that apply to it.</summary>
    public abstract class RegisterIntent : ActionMessage
    {
        /// <summary>The live cashier counter the intent targets.</summary>
        public InteractableCashierCounter Counter;
    }

    [NetworkMessage]
    public sealed class RegisterClaimIntent : RegisterIntent
    {
        public override void OnApply() => RegisterActions.Claim(Counter);

        public override void OnUndo() => RegisterClientBehaviour.Active?.UndoClaim(this);
    }

    [NetworkMessage]
    public sealed class RegisterReleaseIntent : RegisterIntent
    {
        public override void OnApply() => RegisterActions.Release(Counter);

        public override void OnUndo() => RegisterClientBehaviour.Active?.UndoRelease(this);
    }

    [NetworkMessage]
    public sealed class RegisterScanItemIntent : RegisterIntent
    {
        public int Slot;

        public override void OnApply() => RegisterActions.ScanItem(Counter, Slot);
    }

    [NetworkMessage]
    public sealed class RegisterScanCardIntent : RegisterIntent
    {
        public int Slot;

        public override void OnApply() => RegisterActions.ScanCard(Counter, Slot);
    }

    [NetworkMessage]
    public sealed class RegisterTakePaymentIntent : RegisterIntent
    {
        public bool IsCard;

        public override void OnApply() => RegisterActions.TakePayment(Counter, IsCard);
    }

    [NetworkMessage]
    public sealed class RegisterCardPaymentIntent : RegisterIntent
    {
        public double Value;

        /// <summary>True when the local card payment zeroed the checkout total and queued the
        /// card income. A mismatched card amount leaves the total untouched and queues nothing.
        /// Local state on the pending action: it tracks whether undo still owes the wallet a
        /// reversal, so a no-op replay cannot cause a double reversal. Not serialized.</summary>
        [NonSerialized]
        public bool WalletApplied;

        public override void OnApply() => WalletApplied = RegisterActions.CardPayment(Counter, Value);

        public override void OnUndo() => RegisterClientBehaviour.Active?.UndoCardPayment(this);
    }

    [NetworkMessage]
    public sealed class RegisterChangeIntent : RegisterIntent
    {
        public int Slot;
        public bool IsCoin;
        public bool TakeBack;

        public override void OnApply() =>
            RegisterActions.Change(Counter, Slot, IsCoin, TakeBack);
    }

    [NetworkMessage]
    public sealed class RegisterCompleteIntent : RegisterIntent
    {
        /// <summary>The paid and change amounts at the moment the client completed. They are the
        /// exact income/change the game queued, so a rejection can reverse the wallet without
        /// carrying the checkout snapshot.</summary>
        public double Paid;
        public double Change;

        /// <summary>True when the local completion queued the checkout's coin event. Local state on
        /// the pending action: it tracks whether undo still owes the wallet a reversal, so a no-op
        /// replay or a follower undo cannot reverse the same income twice. Not serialized.</summary>
        [NonSerialized]
        public bool WalletApplied;

        public override void OnApply() => WalletApplied = RegisterActions.Complete(Counter);

        public override void OnUndo() => RegisterClientBehaviour.Active?.UndoComplete(this);
    }
}
