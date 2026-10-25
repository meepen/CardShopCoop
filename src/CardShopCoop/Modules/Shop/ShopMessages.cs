using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Client intent for the single shop-rename edit lock. Acquire carries the action id
    /// that a denial rolls back; release is a session fact with an empty id and must still reach
    /// the host across a loading transition.</summary>
    [NetworkMessage]
    public sealed class ShopRenameLockIntent : ActionMessage
    {
        public byte Op;
    }

    internal static class ShopRenameLockOp
    {
        internal const byte Acquire = 0;
        internal const byte Release = 1;
    }

    /// <summary>Client rename proposal. Vanilla already renamed the local sign; the host validates
    /// the lock and name and publishes the canonical <see cref="ShopRenameState"/>.</summary>
    [NetworkMessage]
    public sealed class ShopRenameRequestIntent : ActionMessage
    {
        public string Name;
    }

    /// <summary>The editor's live text, before the host validates the lock owner and republishes
    /// it as <see cref="ShopRenameDraftState"/>. Drafts are display-only, so they carry no action
    /// id and never settle an action.</summary>
    [NetworkMessage]
    public sealed class ShopRenameDraftIntent : INetMessage
    {
        public string Text;
    }
}
