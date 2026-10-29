using System;
using System.Collections.Generic;
using CardShopCoop.Api;
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
    public sealed class StaffModuleIntentMessage : ActionMessage
    {

        public StaffIntentKind Kind;
        /// <summary>The live worker the intent targets. Null for <see cref="StaffIntentKind.Hire"/>,
        /// which selects a save-data slot that has no live worker yet.</summary>
        public Worker Worker;
        /// <summary>The save-data slot a <see cref="StaffIntentKind.Hire"/> selects.</summary>
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

        // The client registers this prediction through the explicit Apply/Undo closures in
        // StaffClientBehaviour, and the host handler performs the worker operation itself, so the
        // action hooks are never the applied path.
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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
    public sealed class StaffModuleDeltaMessage : INetMessage
    {

        /// <summary>The prediction this delta answers, or empty for host-local worker state.</summary>
        public Guid ActionId;
        public StaffDeltaKind Kind;
        /// <summary>The live worker this operation/state describes.</summary>
        public Worker Worker;
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
        /// <summary>The live worker this entry describes. A null entry is logged and dropped.</summary>
        public Worker Worker;
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
