using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>
    /// Host-authoritative shop-renaming state: the committed canonical name, whether the shop is
    /// still unnamed, and the single first-come editor's lock. It is session-wide because the lock
    /// and the name are shared by every player and must survive the rename UI being closed.
    ///
    /// <see cref="IsRename"/> marks a state that represents a committed rename (rather than a lock
    /// change or a baseline), so the client knows when to replay the host-owned tutorial naming
    /// side effects. <see cref="ActionId"/> and <see cref="Rejected"/> correlate the host's
    /// echo/rejection with the optimistic rename or lock action in
    /// <see cref="Actions.ActionLedger"/>.
    /// </summary>
    [GlobalInformation]
    [HostOnlyInformation]
    internal sealed class ShopRenameState : IActionValue
    {
        /// <summary>The committed canonical name, or null before one exists.</summary>
        public string Name
        {
            get; init;
        }

        /// <summary>True while the host's tutorial still treats the shop as unnamed. It keeps the
        /// placeholder off the tutorial sign without trusting a guest's possibly stale tutorial
        /// snapshot.</summary>
        public bool Unnamed
        {
            get; init;
        }

        /// <summary>The player holding the rename lock, or null when nobody is editing.</summary>
        public PlayerRef Owner
        {
            get; init;
        }

        /// <summary>True when this value is a committed rename, not a lock update or baseline.</summary>
        public bool IsRename
        {
            get; init;
        }

        public Guid ActionId
        {
            get; init;
        }

        public bool Rejected
        {
            get; init;
        }
    }

    /// <summary>
    /// The editor's live, unconfirmed text, kept separate from the canonical rename state so a
    /// keystroke never applies the name/lock or resolves an action. Only the lock owner may write
    /// it; the host rebroadcasts it and every other peer paints it on the shop sign.
    /// </summary>
    [GlobalInformation]
    [HostOnlyInformation]
    internal sealed class ShopRenameDraftState
    {
        public PlayerRef Owner
        {
            get; init;
        }

        public string Text
        {
            get; init;
        }
    }
}
