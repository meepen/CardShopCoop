using System;
using System.Collections.Generic;
using CardShopCoop.Api;
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
    }

    /// <summary>
    /// Client intent. Prices, inventory and placement validity are never trusted from this
    /// message. <see cref="Entity"/> is the live decoration being moved or removed. A new
    /// placement has no host object yet and therefore leaves it null: identity assigns the
    /// decoration's id on the host when the object is created, and the client's local piece is
    /// reconciled by the resulting lifecycle message.
    /// </summary>
    [NetworkMessage]
    public sealed class DecorationIntentMessage : ActionMessage
    {
        public byte Action;
        public int Category;
        public int Index;
        public EDecoObject DecorationType;
        public bool LotB;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Vertical;
        public bool WarehouseWallSnap;
        public int WallIndex;
        public InteractableObject Entity;

        /// <summary>The live prediction/reversal for an intent is recorded by the client's
        /// action-ledger closures: they capture the piece's pre-action snapshot and which game
        /// path produced it. Nothing is replayed from this DTO itself.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    /// <summary>Authoritative decoration state delta. Object creation, movement and removal are
    /// identity lifecycle events; this message carries only inventory/equip state and is never a
    /// command. <see cref="ActionId"/> retires the intent prediction this delta answers.</summary>
    [NetworkMessage]
    public sealed class DecorationDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public byte Action;
        public int Category;
        public int Index;
        public EDecoObject DecorationType;
        public bool LotB;
        public int InventoryCount;
    }

    /// <summary>Local pose carrier between the module and the identity adapter. It is never sent
    /// on the wire: settlement flows through the identity lifecycle descriptors.</summary>
    public sealed class DecorationPose
    {
        public long Id;
        public EDecoObject DecorationType;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Vertical;
        public bool WarehouseWallSnap;
        public int Wall;
    }
}
