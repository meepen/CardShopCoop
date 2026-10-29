using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Client intent for the single shop-rename edit lock. Acquire is a command, so a
    /// denial rolls the requester's action back.</summary>
    [NetworkMessage]
    public sealed class ShopRenameLockIntent : ActionMessage
    {
        /// <summary>The client's ledger replay/undo closures own the pending UI open; the message
        /// is only the host command.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }

    /// <summary>A release is a session fact, not a command: it carries no action id and must still
    /// reach the host across a loading transition, so it is a plain one-way message.</summary>
    [NetworkMessage]
    public sealed class ShopRenameReleaseMessage : INetMessage
    {
    }

    /// <summary>Client rename proposal. Vanilla already renamed the local sign; the host validates
    /// the lock and name and publishes the canonical <see cref="ShopRenameState"/>.</summary>
    [NetworkMessage]
    public sealed class ShopRenameRequestIntent : ActionMessage
    {
        public string Name;

        /// <summary>The client's ledger replay/undo closures own the optimistic sign; the message
        /// is only the host command.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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
