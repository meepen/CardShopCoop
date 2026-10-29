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
    /// every mutation and broadcasts the result. <see cref="ActionId"/> carries the rejected
    /// payment prediction the values answer, or empty for ambient accrual.</summary>
    [NetworkMessage]
    public sealed class BillsDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public byte BillType;
        public bool All;
        public BillValue Value = new();
        public BillValue Rent;
        public BillValue Electric;
        public BillValue Employee;
    }

    /// <summary>Client command: pay one bill, or every bill when <see cref="BillType"/> is zero.
    /// The prediction is recorded by the client's action-ledger closures, which capture the bills
    /// as they were at payment time; the framework hooks carry no per-instance state.</summary>
    [NetworkMessage]
    public sealed class BillPaymentMessage : ActionMessage
    {
        /// <summary>Zero pays all bills; otherwise this is the game's EBillType value.</summary>
        public byte BillType;

        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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
