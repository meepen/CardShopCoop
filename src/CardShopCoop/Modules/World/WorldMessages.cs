using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>Semantic operation carried by a World command.</summary>
    public enum WorldOperationKind : ushort
    {
        Unknown = 0,
        BoxDestroy = 2,
        FurnitureBoxUp = 3,
        PlayerBoxPickup = 4,
        PlayerBoxPlacement = 5,
        PlayerBoxThrow = 6,
        ShelfAdd = 7,
        ShelfRemove = 8,
        WarehouseStore = 9,
        WarehouseTake = 10,
        Container = 11,
        CardDelta = 12,
        CardDeltaBatch = 13,
        GradedRemove = 14,
        BoxState = 15,
        CardDisplay = 16,
        Workbench = 17,
    }

    /// <summary>Common identity carried by world messages. Stable entity IDs make validation
    /// independent of Unity instance IDs; PredictionId is non-empty only on a client intent and
    /// the accepted authoritative delta that reconciles it.</summary>
    public abstract class WorldMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public WorldOperationKind OperationKind;
        public string StableEntityId;
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
        Furniture = 3,
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

    /// <summary>Host-authoritative descriptor used to create one physical packaging box.</summary>
    public sealed class BoxNetworkState
    {
        public long BoxNetworkId;
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

        // Furniture box payload.
        public EObjectType FurnitureObjectType;
    }

    /// <summary>Host -> clients: create or bind a box to its stable network ID.</summary>
    [NetworkMessage]
    public sealed class BoxCreatedMessage : WorldMessage
    {
        public BoxNetworkState Box;

        /// <summary>Index of this box in the world snapshot's serialization order for its kind,
        /// or -1 for a box created after the snapshot. The guest reproduces the same index from
        /// the transferred save, so a scene box is adopted by slot instead of by content/pose.
        /// </summary>
        public int SnapshotSlot = -1;
    }

    /// <summary>Client -> host: request retirement of a locally consumed box.</summary>
    [NetworkMessage]
    public sealed class BoxDestroyRequestMessage : WorldMessage
    {
        public long BoxNetworkId;
    }

    /// <summary>Host -> clients: the box ID no longer represents a physical or stored box.</summary>
    [NetworkMessage]
    public sealed class BoxDestroyedMessage : WorldMessage
    {
        public long BoxNetworkId;
    }

    /// <summary>Marks the end of a per-connection box creation baseline.</summary>
    [NetworkMessage]
    public sealed class BoxBaselineCompleteMessage : WorldControlMessage
    {
    }

    /// <summary>Client -> host: an item box's open flag and/or contents changed after a local
    /// player action. The host mirrors it into its live box and republishes the state.</summary>
    [NetworkMessage]
    public sealed class BoxStateRequestMessage : WorldMessage
    {
        public long BoxNetworkId;
        public bool IsBoxOpened;
        public bool ContentsChanged;
        public EItemType ItemType;
        public int ItemCount;
    }

    /// <summary>Host -> clients: authoritative item-box open flag and contents. Carries no pose,
    /// so a held box keeps its local/remote hand anchor.</summary>
    [NetworkMessage]
    public sealed class BoxStateMessage : WorldMessage
    {
        public long BoxNetworkId;
        public bool IsBoxOpened;
        public bool ContentsChanged;
        public EItemType ItemType;
        public int ItemCount;
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
        // Placement's furniture identity API predates generic world prediction and still needs
        // a positive namespace value. It is not carried on the wire or used for intent validity.
        internal const long FurnitureIdentityScope = 1L;

        internal static string StableEntityId(WorldMessage message)
        {
            if (message == null)
                return string.Empty;
            if (!string.IsNullOrEmpty(message.StableEntityId))
                return message.StableEntityId;
            if (message is BoxCreatedMessage created && created.Box != null)
                return "box:" + created.Box.BoxNetworkId;
            if (message is BoxDestroyRequestMessage destroy)
                return "box:" + destroy.BoxNetworkId;
            if (message is BoxDestroyedMessage destroyed)
                return "box:" + destroyed.BoxNetworkId;
            if (message is BoxStateRequestMessage boxState)
                return "box:" + boxState.BoxNetworkId;
            if (message is BoxStateMessage boxStateResult)
                return "box:" + boxStateResult.BoxNetworkId;
            if (message is PlayerBoxInteractionMessage player)
                return "box:" + player.BoxNetworkId;
            if (message is CardDisplayRequestMessage cardDisplay)
                return CardDisplayEntityId(cardDisplay.ShelfKey, cardDisplay.Compartment);
            if (message is CardDisplayMessage cardDisplayState)
                return CardDisplayEntityId(cardDisplayState.ShelfKey, cardDisplayState.Compartment);
            if (message is ContainerOpMessage container)
                return ContainerEntityId(container.Kind, container.Index);
            if (message is WarehouseStoreMessage store)
                return WarehouseEntityId(store.ShelfIndex, store.CompartmentIndex);
            if (message is WarehouseTakeMessage take)
                return WarehouseEntityId(take.ShelfIndex, take.CompartmentIndex);
            if (message is ShelfInteractionMessage shelf)
                return "shelf:" + shelf.ShelfKey + ":" + shelf.Compartment;
            if (message is FurnitureBoxUpRequestMessage furniture)
                return "furniture:" + furniture.ObjectType + ":"
                    + furniture.Position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + furniture.Position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + furniture.Position.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (message is ContainerStateMessage state)
                return "container-state";
            if (message is WorkbenchStateMessage workbench)
                return WorkbenchEntityId(workbench.Index);
            if (message is WorkbenchBundleMessage bundle)
                return WorkbenchEntityId(bundle.Index);
            if (message is WorkbenchGrantMessage grant)
                return grant.StableEntityId ?? "workbench-grant";
            if (message is WarehouseStateMessage)
                return "warehouse-state";
            if (message is MarketStateMessage)
                return "market";
            return message.GetType().FullName ?? message.GetType().Name;
        }

        internal static string CardDisplayEntityId(int shelfKey, int compartment)
            => "card-display:" + shelfKey + ":" + compartment;

        internal static string ContainerEntityId(int kind, int index)
            => "container:" + kind + ":" + index;

        internal static string WorkbenchEntityId(int index)
            => "workbench:" + index;

        internal static string WarehouseEntityId(int shelfIndex, int compartmentIndex)
            => "warehouse:" + shelfIndex + ":" + compartmentIndex;

        internal static void PrepareIntent(WorldMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            message.StableEntityId ??= StableEntityId(message);
            message.OperationKind = OperationFor(message);
        }

        internal static bool IsValidIntent(WorldMessage message)
            => message != null && message.OperationKind == OperationFor(message)
                && message.OperationKind != WorldOperationKind.Unknown
                && !string.IsNullOrEmpty(message.StableEntityId);

        private static WorldOperationKind OperationFor(WorldMessage message)
        {
            if (message is BoxDestroyRequestMessage)
                return WorldOperationKind.BoxDestroy;
            if (message is BoxStateRequestMessage)
                return WorldOperationKind.BoxState;
            if (message is FurnitureBoxUpRequestMessage)
                return WorldOperationKind.FurnitureBoxUp;
            if (message is PlayerBoxPickupRequestMessage)
                return WorldOperationKind.PlayerBoxPickup;
            if (message is PlayerBoxPlacementRequestMessage)
                return WorldOperationKind.PlayerBoxPlacement;
            if (message is PlayerBoxThrowRequestMessage)
                return WorldOperationKind.PlayerBoxThrow;
            if (message is ShelfItemAddRequestMessage)
                return WorldOperationKind.ShelfAdd;
            if (message is ShelfItemRemoveRequestMessage)
                return WorldOperationKind.ShelfRemove;
            if (message is WarehouseStoreMessage)
                return WorldOperationKind.WarehouseStore;
            if (message is WarehouseTakeMessage)
                return WorldOperationKind.WarehouseTake;
            if (message is CardDisplayRequestMessage)
                return WorldOperationKind.CardDisplay;
            if (message is ContainerOpMessage)
                return WorldOperationKind.Container;
            if (message is CardDeltaBatchRequestMessage)
                return WorldOperationKind.CardDeltaBatch;
            if (message is CardDeltaRequestMessage)
                return WorldOperationKind.CardDelta;
            if (message is GradedRemoveRequestMessage)
                return WorldOperationKind.GradedRemove;
            if (message is WorkbenchStateRequestMessage || message is WorkbenchBundleMessage)
                return WorldOperationKind.Workbench;
            return WorldOperationKind.Unknown;
        }
    }

    /// <summary>Client intent to box an existing furniture object; execution remains host-side.</summary>
    [NetworkMessage]
    public sealed class FurnitureBoxUpRequestMessage : WorldMessage
    {
        public EObjectType ObjectType;
        public Vector3 Position;
    }

    [NetworkMessage]
    public sealed class WarehouseStateMessage : WorldMessage
    {
        public List<WarehouseCompartmentState> Compartments = new();
    }

    public sealed class WarehouseCompartmentState
    {
        public string StableEntityId;
        public int ShelfIndex;
        public int CompartmentIndex;
        public List<WarehouseBoxState> Boxes = new();
    }

    public sealed class WarehouseBoxState
    {
        public long BoxNetworkId;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;
    }

    [NetworkMessage]
    public sealed class WarehouseStoreMessage : WorldMessage
    {
        public long BoxNetworkId;
        public int ShelfIndex;
        public int CompartmentIndex;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;
    }

    [NetworkMessage]
    public sealed class WarehouseTakeMessage : WorldMessage
    {
        public long BoxNetworkId;
        public int ShelfIndex;
        public int CompartmentIndex;
    }

    /// <summary>One accepted warehouse inventory change. The complete compartment list is sent
    /// only in WarehouseStateMessage during the join baseline.</summary>
    [NetworkMessage]
    public sealed class WarehouseDeltaMessage : WorldMessage
    {
        public bool IsStore;
        public int ShelfIndex;
        public int CompartmentIndex;
        public long BoxNetworkId;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;
    }
}
