using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Register
{
    [NetworkMessage]
    public sealed class RegisterIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public byte Counter;
        public RegisterIntentKind Kind;
        public int Slot = -1;
        public bool IsCard;
        public bool IsCoin;
        public bool TakeBack;
        public double Value;
    }

    public enum RegisterIntentKind : byte
    {
        Claim = 1,
        Release = 2,
        ScanItem = 3,
        ScanCard = 4,
        TakePayment = 5,
        CardPayment = 6,
        Change = 7,
        Complete = 8,
    }

    public enum RegisterDeltaKind : byte
    {
        Ownership = 1,
        CustomerLifecycle = 2,
        Scan = 3,
        PhasePayment = 4,
        Change = 5,
        CounterLifecycle = 6,
    }

    /// <summary>Complete register state sent only as a join baseline.</summary>
    [NetworkMessage]
    public sealed class RegisterBaselineMessage : INetMessage
    {
        public List<RegisterBaselineCounter> Counters = new();
    }

    /// <summary>One keyed register operation. CustomerLifecycle carries the one-customer
    /// bootstrap needed when a new checkout carrier is constructed.</summary>
    [NetworkMessage]
    public sealed class RegisterDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public RegisterDeltaKind Kind;
        public byte Counter;
        public uint CounterGeneration;
        public bool Exists;
        public int Owner;

        public bool HasCustomer;
        public int CustomerIndex = -1;
        public int CustomerGeneration;
        public bool CustomerFemale;
        public string CharacterName;
        public List<RegisterLine> Lines = new();

        public byte State;
        public bool UsingCard;
        public double Paid;
        public double Total;
        public double CustomerTotal;
        public double Change;
        public bool ChangeReady;
        public bool ChangeStarted;
        public bool TooMuchChange;

        public int Slot = -1;
        public bool IsCard;
        public bool IsCoin;
        public bool TakeBack;
        public double Value;
        public int ChangeCount;
    }

    public sealed class RegisterBaselineCounter
    {
        public byte Counter;
        public uint CounterGeneration;
        public bool Exists;
        public int Owner;
        public bool HasCustomer;
        public int CustomerIndex = -1;
        public int CustomerGeneration;
        public bool CustomerFemale;
        public string CharacterName;
        public byte State;
        public bool UsingCard;
        public double Paid;
        public double Total;
        public double CustomerTotal;
        public double Change;
        public bool ChangeReady;
        public bool ChangeStarted;
        public bool TooMuchChange;
        public List<RegisterLine> Lines = new();
        public List<RegisterChange> ChangeItems = new();
    }

    public sealed class RegisterLine
    {
        public bool IsCard;
        public EItemType ItemType;
        public CardData Card;
        public float Price;
        public bool Scanned;
    }

    public sealed class RegisterChange
    {
        public int Slot;
        public bool IsCoin;
        public double Value;
        public int Count;
    }
}
