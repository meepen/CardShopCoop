using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
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
        public InteractableObject Container;
        public ContainerRecord Record;
        public InteractablePackagingBox Box;
        public bool TakeIntoHand;
        public bool ReleaseHold;
        public bool CompletePackCollection;
    }

    public struct ContainerRecord
    {
        public InteractableObject Container;
        public bool CanWorkerTake;
        public List<CompactCardDataAmount> Cards;
        public List<EItemType> StoredTypes;
        public bool Processing;
        public float Timer;
        public int OpenedCount;
        public int Count;
        public byte Flags;
        public List<float> Fills;
        public int CurrentState;
        public bool CollectClaimed;
    }

    [NetworkMessage]
    public sealed class ContainerPackClaimMessage : WorldMessage
    {
        public InteractableAutoPackOpener Container;
        public int ClaimToken;
        public List<CompactCardDataAmount> Cards = new();
    }
}
