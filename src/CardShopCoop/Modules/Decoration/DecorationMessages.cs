using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Decoration
{
    internal static class DecorationActions
    {
        internal const byte Equip = 1;
        internal const byte BuyShopDecoration = 2;
        internal const byte BuyItemDecoration = 3;
        internal const byte Place = 4;
        internal const byte Remove = 5;
    }

    /// <summary>Complete host-owned decoration state.</summary>
    [NetworkMessage]
    public sealed class DecorationStateMessage : INetMessage
    {
        public bool Full = true;
        public int Wall;
        public int WallB;
        public int Floor;
        public int FloorB;
        public int Ceiling;
        public int CeilingB;
        public List<bool> WallUnlocks = new();
        public List<bool> FloorUnlocks = new();
        public List<bool> CeilingUnlocks = new();
        public List<int> Inventory = new();
        public List<DecorationPose> Placed = new();
    }

    /// <summary>Client intent. Prices, inventory, validity and host object ids are never trusted
    /// from this message.</summary>
    [NetworkMessage]
    public sealed class DecorationIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Action;
        public int Category;
        public int Index;
        public int DecorationType;
        public bool LotB;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Vertical;
        public bool WarehouseWallSnap;
        public int WallIndex;
        public long ObjectId;
    }

    [NetworkMessage]
    public sealed class DecorationDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Action;
        public int Category;
        public int Index;
        public int DecorationType;
        public bool LotB;
        public int InventoryCount;
        public long ObjectId;
        public DecorationPose Pose;
    }

    public sealed class DecorationPose
    {
        public long Id;
        public int DecorationType;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Vertical;
        public bool WarehouseWallSnap;
        public int Wall;
    }
}
