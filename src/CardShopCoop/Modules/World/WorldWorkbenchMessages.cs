using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host -> clients: authoritative state of one workbench - the bulk boxes stored on
    /// it and, while a bundle is running, the box tier it is producing. Sent for the join
    /// baseline and as a complete state-set after any change (never as a mutation).</summary>
    [NetworkMessage]
    public class WorkbenchStateMessage : WorldMessage
    {
        public byte Index;
        public List<EItemType> StoredTypes = new();
        public EItemType SpawnItemType;
        public bool Bundling;
    }

    /// <summary>Client -> host: the acting player changed one workbench (a hand/box transfer or
    /// started a bundle). Carries the resulting complete state - the same "requesting player is
    /// authoritative for the object they acted on" shape as the item-box state request.</summary>
    [NetworkMessage]
    public sealed class WorkbenchStateRequestMessage : WorkbenchStateMessage
    {
    }

    /// <summary>Client -> host: the acting player finished the bundling animation. The host mints
    /// the bulk box authoritatively and grants it back into their hand.</summary>
    [NetworkMessage]
    public sealed class WorkbenchBundleMessage : WorldMessage
    {
        public byte Index;
    }

    /// <summary>Host -> actor: spawn this bulk box in your hand. The host owns the box and can
    /// re-send the grant if the actor rejoins.</summary>
    [NetworkMessage]
    public sealed class WorkbenchGrantMessage : WorldMessage
    {
        public EItemType ItemType;
        public long GrantId;
    }
}
