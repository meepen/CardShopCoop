using System;
using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>A client command against one placed container. The concrete type is the command;
    /// the host validates it and applies it through the game's own path. The container is the live
    /// placed object, never a list index or key.</summary>
    public abstract class ContainerIntent : WorldIntentMessage
    {
        public InteractableObject Container;
    }

    [NetworkMessage]
    public sealed class ContainerContentSetIntent : ContainerIntent
    {
        public bool CanWorkerTake;
        public List<CompactCardDataAmount> Cards = new();
    }

    [NetworkMessage]
    public sealed class PackInsertIntent : ContainerIntent
    {
        public EItemType ItemType;
    }

    [NetworkMessage]
    public sealed class PackTurnOnIntent : ContainerIntent
    {
    }

    [NetworkMessage]
    public sealed class PackCollectIntent : ContainerIntent
    {
        public int ClaimToken;
        public List<CompactCardDataAmount> Cards = new();
    }

    [NetworkMessage]
    public sealed class PackClaimIntent : ContainerIntent
    {
    }

    [NetworkMessage]
    public sealed class CleanserToggleIntent : ContainerIntent
    {
        public bool TurnedOn;
    }

    [NetworkMessage]
    public sealed class CleanserRefillIntent : ContainerIntent
    {
        public float Fill;
    }

    [NetworkMessage]
    public sealed class WorkerTakeFlagIntent : ContainerIntent
    {
        public bool CanWorkerTake;
    }

    /// <summary>Take one empty box out of an empty-box storage. The client minted the box's stable
    /// id locally (its game spawned the box); the host binds its created counterpart to the same
    /// id, so every later message names the box by its live object on both peers.</summary>
    [NetworkMessage]
    public sealed class EmptyBoxTakeIntent : ContainerIntent
    {
        public Guid BoxNetworkId;
        public bool IsPlayer;
    }

    /// <summary>Put the held empty box into an empty-box storage. The box id was minted when the
    /// box was created; the local destroy hook has already unbound the live object, so the id is
    /// the only identity the host needs.</summary>
    [NetworkMessage]
    public sealed class EmptyBoxStoreIntent : ContainerIntent
    {
        public Guid BoxNetworkId;
        public bool IsPlayer;
    }
}
