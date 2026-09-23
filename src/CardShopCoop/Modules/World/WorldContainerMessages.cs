using System.Collections.Generic;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    [NetworkMessage]
    public sealed class ContainerOpMessage : WorldMessage
    {
        public byte Op;
        public byte Kind;
        public byte Index;
        public bool CanWorkerTake;
        public List<CompactCardDataAmount> Cards = new();
        public EItemType ItemType;
        public ushort StorageId;
        public Vector3 Position;
        public ushort BoxId;
        public long BoxNetworkId;
        public bool IsBig;
        public bool TurnedOn;
        public bool IsPlayer;
        public float Fill;
        public int ClaimToken;
    }

    [NetworkMessage]
    public sealed class ContainerStateMessage : WorldMessage
    {
        public List<ContainerRecord> Records = new();
    }

    /// <summary>One authoritative container record changed during normal play. A complete
    /// ContainerStateMessage is reserved for the join baseline.</summary>
    [NetworkMessage]
    public sealed class ContainerDeltaMessage : WorldMessage
    {
        public int Key;
        public ContainerRecord Record;
        public bool HasBox;
        public BoxNetworkState Box;
        public bool TakeIntoHand;
        public bool ReleaseHold;
        public bool CompletePackCollection;
        public byte PackIndex;
        public int PackOpenedCount;
        public List<CompactCardDataAmount> RevealedCards = new();
    }

    public struct ContainerRecord
    {
        public string StableEntityId;
        public byte Kind;
        public byte Index;
        public bool CanWorkerTake;
        public List<CompactCardDataAmount> Cards;
        public List<EItemType> StoredTypes;
        public bool Processing;
        public float Timer;
        public int OpenedCount;
        public ushort StorageId;
        public int Count;
        public byte Flags;
        public List<float> Fills;
        public int CurrentState;
        public bool CollectClaimed;
        // The host's monotonic clock at which the current processing cycle began,
        // the duration represented by that cycle, and the host clock at serialization.
        // Clients translate these values to their local monotonic clock once and
        // render between lifecycle events without sending progress snapshots.
        public double PackStartTimestamp;
        public float PackDuration;
        public double PackTimestamp;
    }

    [NetworkMessage]
    public sealed class ContainerPackClaimMessage : WorldMessage
    {
        public byte Index;
        public int ClaimToken;
        public List<CompactCardDataAmount> Cards = new();
    }
}
