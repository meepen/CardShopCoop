using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Protocol;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>Complete placement baseline sent when a peer joins or a scene is rebuilt.</summary>
    [NetworkMessage]
    public sealed class PlacementBaselineMessage : INetMessage
    {
        public PlacementPopulationMessage Population = new();
        public List<PlacementMoveEntry> Moves = new();
    }

    /// <summary>A minimal host-owned placement entity change.</summary>
    [NetworkMessage]
    public sealed class PlacementDeltaMessage : IPredictedMessage
    {
        public const byte Add = 1;
        public const byte Update = 2;
        public const byte Remove = 3;
        public const byte Pose = 4;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Operation;
        public PlacementMoveEntry Entity;
    }

    /// <summary>A client request to settle one object at a pose.</summary>
    [NetworkMessage]
    public sealed class PlacementMoveIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public PlacementMoveEntry Move;
    }

    /// <summary>Guest -> host: sent once this peer is fully connected, asking for one more
    /// placement baseline. The join baseline can race the guest's scene as objects finish their
    /// load-time spawn/recovery from the transferred save, so the fresh pass rebinds anything
    /// that missed. The host answers the requesting connection only.</summary>
    [NetworkMessage]
    public sealed class PlacementBaselineRequestMessage : INetMessage
    {
    }

    /// <summary>Population grouped by the stable placement kind ordinal.</summary>
    [JsonConverter(typeof(PlacementPopulationMessageConverter))]
    public sealed class PlacementPopulationMessage
    {
        public List<List<PlacementPopulationEntry>> Entries = new();
    }

    public sealed class PlacementPopulationEntry
    {
        public ushort Id;
        /// <summary>The slot this entity occupied in the host's save-time identity manifest for
        /// its kind, captured when the transferred save was written. The guest loads that exact
        /// order, so this is the transfer-time manifest key for an entity the guest already has.
        /// It is only consulted when the carried stable id names no local object yet. A -1 means
        /// the entity was created after the snapshot (or no manifest was available) and must be
        /// materialized from its descriptor.</summary>
        public int Slot;
        public int ObjType;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool IsBoxed;
        public Vector3 BoxedPos;
        public Quaternion BoxedRot;
    }

    [JsonConverter(typeof(PlacementMoveEntryConverter))]
    public sealed class PlacementMoveEntry
    {
        public int Key;
        public int Type;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool IsBoxed;
        public Vector3 BoxedPos;
        public Quaternion BoxedRot;
    }
}
