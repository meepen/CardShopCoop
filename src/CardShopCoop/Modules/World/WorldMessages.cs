using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>Common correlation carried by world messages. Every entity the message addresses
    /// is a live scene reference; the identity module owns object/identity translation on the
    /// wire. ActionId is non-empty only on a client intent and the accepted authoritative
    /// delta that reconciles it.</summary>
    public abstract class WorldMessage : ActionMessage
    {
    }

    /// <summary>Internal control marker used by the ordered world-baseline lifecycle.</summary>
    public abstract class WorldControlMessage : WorldMessage
    {
    }

    /// <summary>The concrete game object represented by a host-assigned box network ID.</summary>
    public enum BoxNetworkKind : byte
    {
        Item = 1,
        Card = 2,
    }

    /// <summary>One card carried by a card packaging box. This deliberately uses named enum
    /// values for game enums that can differ between installs, except card-border which is a
    /// game-owned closed enum and is carried by its member name.</summary>
    public sealed class BoxCardState
    {
        public ECardExpansionType ExpansionType;
        public EMonsterType MonsterType;
        public string BorderType;
        public bool IsFoil;
        public bool IsDestiny;
        public bool IsChampionCard;
        public bool IsNew;
        public int CardGrade;
        public int GradedCardIndex;
    }

    /// <summary>Identity-module descriptor used to create one physical packaging box. This is the
    /// creation payload of the identity module, not a World wire message, so it keeps the stable
    /// box id that names the entity being created.</summary>
    public sealed class BoxNetworkState
    {
        public Guid BoxNetworkId;
        public BoxNetworkKind Kind;
        public Vector3 Position;
        public Quaternion Rotation;

        // Item box payload.
        public EItemType ItemType;
        public int ItemCount;
        public bool IsBig;
        public bool IsBoxOpened;

        // Card box payload.
        public List<BoxCardState> Cards = new();

    }

    /// <summary>Client -> host: request retirement of a locally consumed box.</summary>
    [NetworkMessage]
    public sealed class BoxDestroyRequestMessage : WorldMessage
    {
        /// <summary>The identity minted when the box was created. The local destroy hook has
        /// already unbound the box by the time this intent is serialized, so the id — never the
        /// live object — names the box being retired.</summary>
        public Guid BoxNetworkId;
    }

    /// <summary>Client -> host: an item box's open flag and/or contents changed after a local
    /// player action. The host mirrors it into its live box and republishes the state.</summary>
    [NetworkMessage]
    public sealed class BoxStateRequestMessage : WorldMessage
    {
        public InteractablePackagingBox Box;
        public bool IsBoxOpened;
        public bool ContentsChanged;
        public EItemType ItemType;
        public int ItemCount;
    }

    /// <summary>Host -> clients: authoritative box state. <see cref="PoseChanged"/> carries a
    /// pose refresh for an already-created box; a held box or a newer local move ignores it.</summary>
    [NetworkMessage]
    public sealed class BoxStateMessage : WorldMessage
    {
        public InteractablePackagingBox Box;
        public bool IsBoxOpened;
        public bool ContentsChanged;
        public EItemType ItemType;
        public int ItemCount;
        public bool PoseChanged;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>Marks the end of the complete per-connection shared-world baseline.</summary>
    [NetworkMessage]
    public sealed class WorldBaselineStartMessage : WorldControlMessage
    {
        public long BaselineId;
    }

    [NetworkMessage]
    public sealed class WorldBaselineCompleteMessage : WorldControlMessage
    {
        public long BaselineId;
    }

    internal static class WorldMessageMetadata
    {
        internal static string WorkbenchEntityId(int index)
            => "workbench:" + index;

        /// <summary>An intent is valid only when it carries its correlation id and every entity
        /// it addresses resolved to a live scene object. A message whose entity did not resolve
        /// is dropped, never resolved by a fallback id.</summary>
        internal static bool IsValidIntent(WorldMessage message)
            => message != null && message.ActionId != Guid.Empty && HasLiveEntity(message);

        private static bool HasLiveEntity(WorldMessage message) => message switch
        {
            BoxDestroyRequestMessage destroy => destroy.BoxNetworkId != Guid.Empty,
            BoxStateRequestMessage state => state.Box != null,
            FurnitureBoxUpRequestMessage furniture => furniture.Entity != null,
            FurnitureDestroyIntent furnitureDestroy => furnitureDestroy.Entity != null,
            PlayerBoxInteractionMessage playerBox => playerBox.Box != null,
            ShelfInteractionMessage shelf => shelf.Shelf != null,
            WarehouseStoreMessage store => store.Shelf != null && store.Box != null,
            WarehouseTakeMessage take => take.Source != null && take.Source.Shelf != null
                && take.CreatedBox != null,
            CardDisplayRequestMessage cardDisplay => cardDisplay.Shelf != null,
            ContainerIntent container => container.Container != null,
            _ => true,
        };
    }

    /// <summary>Client intent to retire a furniture object the player sold or trashed while it was
    /// boxed. The package is a view of the furniture; destroying the furniture retires both.</summary>
    [NetworkMessage]
    public sealed class FurnitureDestroyIntent : WorldMessage
    {
        public InteractableObject Entity;
    }

    /// <summary>Client intent to box an existing furniture object; execution remains host-side.
    /// The entity is the live placed object the client boxed up. Its packaging box is a view of
    /// that furniture and shares its session Guid, so the intent never carries a box id.</summary>
    [NetworkMessage]
    public sealed class FurnitureBoxUpRequestMessage : WorldMessage
    {
        public EObjectType ObjectType;
        public Vector3 Position;
        public InteractableObject Entity;
    }

    /// <summary>Location of one warehouse entry. The shelf is a live scene object; the
    /// compartment index names its storage compartment and the slot is a hint that mirrors the
    /// compartment's current order. The identity module owns the Guid that travels on the wire; it
    /// is not part of the public DTO surface and is used internally by the wire converter and the
    /// location registry.</summary>
    public sealed class WarehouseShelfBoxRef
    {
        public WarehouseShelf Shelf;
        public int CompartmentIndex;
        public int Slot = -1;

        /// <summary>Wire identity, owned by the identity module's warehouse location registry.
        /// It is deliberately internal: feature DTOs expose live objects or shelf locations, never
        /// a public Guid surface.</summary>
        internal Guid Identity;

        /// <summary>The shelf's structured identity, retained when the live shelf is not loaded yet
        /// so a deferred message can resolve it later.</summary>
        internal GameObjectId ShelfIdentity;
    }

    /// <summary>One warehouse compartment header. ItemType and IsBig are a property of the
    /// compartment's cached box set, so they travel once instead of once per entry.</summary>
    public sealed class WarehouseCompartmentState
    {
        public WarehouseShelf Shelf;
        public int CompartmentIndex;
        public EItemType ItemType;
        public bool IsBig;
        public List<WarehouseBoxEntry> Boxes = new();

        /// <summary>The shelf's structured identity, retained for deferred application.</summary>
        internal GameObjectId ShelfIdentity;
    }

    /// <summary>One stored warehouse entry. <see cref="Box"/> is only a live representation hint:
    /// a record-backed entry has no live object and stays valid with a null box.</summary>
    public sealed class WarehouseBoxEntry
    {
        public WarehouseShelfBoxRef Ref;
        public int Amount;
        public InteractablePackagingBox Box;
    }

    [NetworkMessage]
    public sealed class WarehouseStateMessage : WorldMessage
    {
        public List<WarehouseCompartmentState> Compartments = new();
    }

    [NetworkMessage]
    public sealed class WarehouseStoreMessage : WorldMessage
    {
        public WarehouseShelf Shelf;
        public int CompartmentIndex;
        public InteractablePackagingBox Box;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;
    }

    /// <summary>Client -> host: take the top entry of <see cref="Source"/>. The locally spawned
    /// live box is a creation event and arrives as a descriptor with its own identity; it may keep
    /// the stored entry's host id or carry a fresh one.</summary>
    [NetworkMessage]
    public sealed class WarehouseTakeMessage : WorldMessage
    {
        public WarehouseShelfBoxRef Source;
        public EntityDescriptor CreatedBox;
    }

    /// <summary>One accepted warehouse inventory change. <see cref="Ref"/> names the entry;
    /// <see cref="Box"/> is an optional live representation and is null for record-backed and
    /// worker/auto-restock entries. Deltas are authoritative state, never intents.</summary>
    [NetworkMessage]
    public sealed class WarehouseDeltaMessage : WorldMessage
    {
        public bool IsStore;
        public WarehouseShelfBoxRef Ref;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;
        public InteractablePackagingBox Box;
    }
}
