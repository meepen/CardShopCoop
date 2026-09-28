using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host -> clients: authoritative state of one workbench - the bulk boxes stored on
    /// it and, while a bundle is running, the box tier it is producing. Sent for the join
    /// baseline and as a complete state-set after any change (never as a mutation). A non-empty
    /// <see cref="WorldMessage.PredictionId"/> is the accepted client intent this state confirms.</summary>
    [NetworkMessage]
    public class WorkbenchStateMessage : WorldMessage
    {
        public byte Index;
        public List<EItemType> StoredTypes = new();
        public EItemType SpawnItemType;
        public bool Bundling;
    }

    /// <summary>Client -> host: one workbench storage edit by the acting player. The host applies
    /// this delta to its own authoritative list and broadcasts the resulting state, so two players
    /// editing one bench at once can no longer overwrite (or duplicate) each other's item. The
    /// inherited <see cref="WorldMessage.PredictionId"/> confirms the edit to the sender.</summary>
    [NetworkMessage]
    public sealed class WorkbenchOpMessage : WorldMessage
    {
        public byte Index;
        public List<EItemType> AddedTypes = new();
        public List<EItemType> RemovedTypes = new();
        public EItemType SpawnItemType;
        public bool Bundling;
    }

    /// <summary>Client -> host: the acting player finished the bundling animation. The acting
    /// client already minted the bulk box locally in vanilla <c>OnTaskCompleted</c>; the host only
    /// clears its mirrored bundling state and republishes the bench.</summary>
    [NetworkMessage]
    public sealed class WorkbenchBundleMessage : WorldMessage
    {
        public byte Index;
    }
}
