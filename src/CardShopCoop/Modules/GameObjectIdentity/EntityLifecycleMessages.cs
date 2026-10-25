using System;
using System.Collections.Generic;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Complete creation/deletion descriptor. One lifecycle message carries everything needed to
    /// create or remove the entity; state messages never create or destroy. The type itself is
    /// polymorphic: each replicated object kind carries only the fields it actually has.
    /// </summary>
    public abstract class EntityDescriptor
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public long WorldEpoch;
        public int SaveSlot = -1;

        [JsonIgnore]
        public abstract GameObjectId Id
        {
            get;
        }

        [JsonIgnore]
        public GameObjectKind Kind => Id.Kind;
    }

    /// <summary>Customer or worker. Identity is the existing (kind, pool index, incarnation).</summary>
    public sealed class NpcEntityDescriptor : EntityDescriptor
    {
        public byte NpcKind;
        public int NpcPoolIndex;
        public int NpcIncarnation;
        public bool NpcFemale;
        public string NpcName;
        public string NpcOutfitJson;
        public float NpcSpeed;
        public float NpcExtraSpeedMultiplier = 1f;
        public bool NpcSmelly;
        public int NpcSmellyMeter;
        public bool NpcTournament;
        public List<EntityNpcBagEntry> NpcBag = new();
        public float NpcYaw;
        public ushort NpcFlags;
        public int NpcActionSequence;
        public byte NpcActionKind;
        public bool NpcHoldBig;
        public EItemType NpcHoldItemType;
        public Guid NpcHoldBoxNetworkId;
        public bool NpcHoldBoxOpened;

        [JsonIgnore]
        public override GameObjectId Id => GameObjectId.Npc(
            NpcKind == 0 ? GameObjectKind.NpcCustomer : GameObjectKind.NpcWorker,
            NpcPoolIndex, NpcIncarnation);
    }

    /// <summary>Furniture or other object in the placement lists. Furniture owns one session
    /// Guid for its whole life; the packaging box it is boxed into is a view of that same id.</summary>
    public sealed class PlacementEntityDescriptor : EntityDescriptor
    {
        public Guid FurnitureId;
        public byte PlacementKind;
        public ushort ObjectId;
        public EObjectType ObjectType;
        public bool IsBoxed;
        public Vector3 BoxedPosition;
        public Quaternion BoxedRotation;

        [JsonIgnore]
        public int Key => (PlacementKind << 24) | ObjectId;

        [JsonIgnore]
        public override GameObjectId Id => GameObjectId.Furniture(FurnitureId);
    }

    /// <summary>A placed decoration. Identity is the existing host/local decoration id.</summary>
    public sealed class DecorationEntityDescriptor : EntityDescriptor
    {
        public long DecorationId;
        public EDecoObject DecorationType;
        public bool Vertical;
        public bool WarehouseWallSnap;
        public int WallIndex = -1;

        [JsonIgnore]
        public override GameObjectId Id => GameObjectId.Decoration(DecorationId);
    }

    /// <summary>A physical packaging box. Identity is the existing box Guid.</summary>
    public sealed class PackagingBoxEntityDescriptor : EntityDescriptor
    {
        public Guid BoxNetworkId;
        public BoxNetworkKind BoxKind;
        public bool BoxBig;
        public bool BoxOpened;
        public EItemType BoxItemType;
        public int BoxItemCount;
        public List<BoxCardState> BoxCards = new();

        [JsonIgnore]
        public override GameObjectId Id => GameObjectId.Box(BoxNetworkId);
    }

    public sealed class EntityNpcBagEntry
    {
        public EItemType ItemType;
        public float UnitPrice;
        public bool IsCard;
        public CardData Card;
    }

    public enum EntityRemovalMode : byte
    {
        Destroy = 1,
        Despawn = 2,
        Boxed = 3,
        Stored = 4,
        Rejected = 5,
    }

    [NetworkMessage]
    public sealed class EntityAddedMessage : INetMessage
    {
        public EntityDescriptor Descriptor;
        public bool Adopted;
        public int OwnerConnectionId;
    }

    [NetworkMessage]
    public sealed class EntityRemovedMessage : INetMessage
    {
        public GameObjectId Id;
        public EntityRemovalMode Mode;
        public EntityDescriptor Replacement;
        public Guid BoxNetworkId;
        public int SnapshotSlot = -1;
    }

    [NetworkMessage]
    public sealed class EntityBaselineMessage : INetMessage
    {
        public long BaselineId;
        public bool Complete = true;
        /// <summary>Host warehouse backend capability. The guest interprets the transferred save's
        /// stored boxes with the host's backend, never its own local probe.</summary>
        public bool HostUsesRecordWarehouse;
        public List<EntityDescriptor> Entries = new();
    }
}
