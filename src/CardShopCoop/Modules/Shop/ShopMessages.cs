using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Shop
{
    [NetworkMessage]
    public sealed class ShopRenameRequestMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public string Name;
    }

    [NetworkMessage]
    public sealed class ShopRenameDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public string Name;

        /// <summary>True only for the canonical name sent to a freshly joined guest. A baseline
        /// is not a rename: it must not replay the host's tutorial/entitlement naming side
        /// effects, which would advance the guest's tutorial behind the host's back.</summary>
        public bool Initial;

        /// <summary>Host-authoritative: the shop has no name yet. Only meaningful on the Initial
        /// baseline. The host's <c>m_TutorialIndex == 0</c> is the same condition vanilla's
        /// SetIsTutorial uses to blank the sign.</summary>
        public bool Unnamed;
    }

    /// <summary>Host-authoritative owner of the single shop-renaming UI. Owner ids are in the
    /// host's canonical connection space (<c>0</c> is the host); <c>-1</c> means nobody is
    /// editing. Clients mirror this state and refuse to open the rename UI while another player
    /// owns it.</summary>
    [NetworkMessage]
    public sealed class ShopRenameLockMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int OwnerConn = -1;
        public string OwnerName;

        /// <summary>Host-authoritative: the shop has no name yet. A release repaint must leave
        /// the tutorial sign blank instead of putting the "My Card Shop" placeholder back.</summary>
        public bool Unnamed;
    }

    /// <summary>Client intent for the first-come-first-serve rename lock. Acquire carries the
    /// prediction id that a denial rolls back; release is a plain fact with an empty id.</summary>
    [NetworkMessage]
    public sealed class ShopRenameLockRequestMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Op;
    }

    internal static class ShopRenameLockOp
    {
        internal const byte Acquire = 0;
        internal const byte Release = 1;
    }

    /// <summary>The editor's live, unconfirmed text. The host applies it to every sign but never
    /// writes it to the canonical shop name.</summary>
    [NetworkMessage]
    public sealed class ShopRenameDraftMessage : INetMessage
    {
        public int OwnerConn;
        public string Text;
    }

    /// <summary>A client editor's live text, before the host validates the owner and rebroadcasts
    /// it. Drafts are display-only, so they carry no prediction.</summary>
    [NetworkMessage]
    public sealed class ShopRenameDraftIntentMessage : INetMessage
    {
        public string Text;
    }
}
