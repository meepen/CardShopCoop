using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Bills
{
    /// <summary>Host-owned phone bill values.</summary>
    [NetworkMessage]
    public sealed class BillsStateMessage : INetMessage
    {
        public bool Full = true;
        public BillValue Rent = new();
        public BillValue Electric = new();
        public BillValue Employee = new();
    }

    /// <summary>Host-authoritative bill values. This is state, not a command: the host owns
    /// every mutation and broadcasts the result. Ambient accrual and host-local payments use
    /// this message; a guest's payment is answered with <see cref="BillPaymentResponse"/>.</summary>
    [NetworkMessage]
    public sealed class BillsDeltaMessage : INetMessage
    {
        public byte BillType;
        public bool All;
        public BillValue Value = new();
        public BillValue Rent;
        public BillValue Electric;
        public BillValue Employee;
    }

    /// <summary>Client command: pay one bill, or every bill when <see cref="BillType"/> is zero.
    /// The game already zeroed the bills locally; the captured values let <see cref="OnUndo"/>
    /// reverse exactly that prediction, and <see cref="OnMerge"/> settles the host's absolute
    /// bill state.</summary>
    [NetworkMessage]
    public sealed class BillPaymentMessage : ActionMessage
    {
        /// <summary>Zero pays all bills; otherwise this is the game's EBillType value.</summary>
        public byte BillType;

        /// <summary>The bills as they were when the payment was predicted. Local undo state; not
        /// serialized.</summary>
        [NonSerialized]
        public BillValue Rent;

        [NonSerialized]
        public BillValue Electric;

        [NonSerialized]
        public BillValue Employee;

        public override void OnUndo(ActionResponseMessage response)
            => BillsClientBehaviour.Active?.UndoPayment(this);

        public override void OnMerge(ActionResponseMessage response)
            => BillsClientBehaviour.Active?.MergePayment(this, response);
    }

    /// <summary>The host's absolute bill state after one payment. The framework sets
    /// <see cref="ActionResponseMessage.Rejected"/>; the values are authoritative whether the
    /// payment was accepted or refused.</summary>
    [NetworkMessage]
    public sealed class BillPaymentResponse : ActionResponseMessage
    {
        public BillsDeltaMessage State = new();
    }

    public sealed class BillValue
    {
        public int DayPassed;
        public float AmountToPay;
    }

    /// <summary>A bill UI notification produced while the host replays a guest payment.</summary>
    [NetworkMessage]
    public sealed class BillPopupMessage : INetMessage
    {
        public int Text;
    }
}
