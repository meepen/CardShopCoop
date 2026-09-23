using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Staff
{
    public enum StaffIntentKind : byte
    {
        Hire = 1,
        Task = 2,
        Options = 3,
        Pack = 4,
        Bonus = 5,
        Fire = 6,
        BeginInteraction = 7,
        EndInteraction = 8,
    }

    public enum StaffDeltaKind : byte
    {
        Hired = 1,
        Fired = 2,
        Task = 3,
        Options = 4,
        Pack = 5,
        Bonus = 6,
        Experience = 7,
        Interaction = 8,
    }

    [NetworkMessage]
    public sealed class StaffModuleIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public StaffIntentKind Kind;
        public int Index;
        public Vector3 Position;
        public byte PrimaryTask;
        public byte SecondaryTask;
        public byte WorkerTask;
        public bool FillNoLabel;
        public bool RoundUpPrice;
        public bool AvoidSetCardPrice;
        public bool RoundUpCardPrice;
        public bool AvoidSetCardPriceRestock;
        public float PriceMult;
        public float CardPriceMult;
        public List<StaffPackChange> PackChanges = new();
    }

    /// <summary>Complete worker state sent only as a join baseline.</summary>
    [NetworkMessage]
    public sealed class StaffModuleBaselineMessage : INetMessage
    {
        public List<StaffModuleEntry> Entries = new();
    }

    /// <summary>One keyed worker operation. Hired carries the one-worker bootstrap needed when
    /// a newly constructed worker is first shown to a client.</summary>
    [NetworkMessage]
    public sealed class StaffModuleDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }

        public StaffDeltaKind Kind;
        public int Index;
        public uint Generation;
        public bool Hired;
        public StaffModuleEntry Bootstrap;

        public byte PrimaryTask;
        public byte SecondaryTask;
        public byte WorkerTask;
        public byte CurrentState;
        public bool GoingHome;

        public bool FillNoLabel;
        public bool RoundUpPrice;
        public bool RoundUpCardPrice;
        public bool AvoidSetCardPrice;
        public bool AvoidSetCardPriceRestock;
        public float PriceMult;
        public float CardPriceMult;

        public int PackIndex = -1;
        public bool PackEnabled;
        public List<StaffPackChange> PackChanges = new();
        public byte BonusCount;
        public bool BonusBoosted;
        public byte ExperienceTask;
        public int ExperienceAmount;
        public bool Granted;
        public bool Occupied;
    }

    public sealed class StaffPackChange
    {
        public int Index;
        public bool Enabled;
    }

    public struct StaffModuleEntry
    {
        public uint Generation;
        public bool Hired;
        public bool HasData;
        public byte PrimaryTask;
        public byte SecondaryTask;
        public byte WorkerTask;
        public byte CurrentState;
        public bool GoingHome;
        public byte BonusCount;
        public bool BonusBoosted;
        public bool FillNoLabel;
        public bool RoundUpPrice;
        public bool RoundUpCardPrice;
        public bool AvoidSetCardPrice;
        public bool AvoidSetCardPriceRestock;
        public float PriceMult;
        public float CardPriceMult;
        public List<bool> PackTypes;
        public List<int> ExpList;
    }
}
