using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Client intent for the single shop-rename edit lock. Acquire is a command, so a
    /// denial reverses the requester's pending open and applies the host's lock state.</summary>
    [NetworkMessage]
    public sealed class ShopRenameLockIntent : ActionMessage
    {
        /// <summary>Runs the deferred vanilla open after the host grants the lock. Local-only.</summary>
        [NonSerialized]
        internal Action Accepted;

        /// <summary>Cancels the deferred open/abandons the edit after a refusal. Local-only.</summary>
        [NonSerialized]
        internal Action Reversed;

        [NonSerialized]
        private bool _reversed;

        public override void OnUndo(ActionResponseMessage response)
        {
            _reversed = true;
            var reversed = Reversed;
            Reversed = null;
            reversed?.Invoke();
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            var accepted = Accepted;
            Accepted = null;
            Reversed = null;
            if (!_reversed)
            {
                accepted?.Invoke();
            }

            _reversed = false;
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

        /// <summary>The canonical name before the proposal, restored when the host refuses it.
        /// Local-only.</summary>
        [NonSerialized]
        public string PreviousName;

        public override void OnUndo(ActionResponseMessage response)
        {
            if (PreviousName != null && ShopState.IsPainted(Name))
            {
                ShopState.Apply(PreviousName);
            }
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            // ShopRenameState is an IActionValue: the extended-information handler applies the
            // host's post-rename/lock state before this hook.
            PreviousName = null;
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
