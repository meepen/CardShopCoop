using System;
using CardShopCoop.Modules.Prediction;
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

    [NetworkMessage]
    public sealed class BillsDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte BillType;
        public bool All;
        public BillValue Value = new();
        public BillValue Rent;
        public BillValue Electric;
        public BillValue Employee;
    }

    [NetworkMessage]
    public sealed class BillPaymentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        /// <summary>Zero pays all bills; otherwise this is the game's EBillType value.</summary>
        public byte BillType;
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
