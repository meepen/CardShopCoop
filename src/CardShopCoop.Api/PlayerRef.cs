using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// A player in the session, for messages that name someone other than their sender.
    /// <see cref="Name"/>, <see cref="IsHost"/> and <see cref="IsSelf"/> are the public identity;
    /// the underlying id is internal wire identity and must not be used by an integrating mod.
    /// A <see cref="PlayerRef"/> in a DTO is a reference supplied by the sender, not an
    /// authentication token; use <see cref="CoopMessageContext.Peer"/> for the authenticated
    /// sender of an inbound message.
    /// </summary>
    public sealed class PlayerRef : IEquatable<PlayerRef>
    {
        internal PlayerRef(int id, string name, bool isHost, bool isSelf)
        {
            Id = id;
            Name = name;
            IsHost = isHost;
            IsSelf = isSelf;
        }

        /// <summary>Host-canonical wire identity: 0 is the host, 1..N are clients. Internal to
        /// the framework; compare <see cref="PlayerRef"/> objects instead.</summary>
        internal int Id
        {
            get;
        }

        /// <summary>Display name snapshot supplied by the sender, or null when it had none.</summary>
        public string Name
        {
            get;
        }

        public bool IsHost
        {
            get;
        }

        public bool IsSelf
        {
            get;
        }

        public bool Equals(PlayerRef other) => other != null && Id == other.Id;

        public override bool Equals(object obj) => Equals(obj as PlayerRef);

        public override int GetHashCode() => Id;

        public override string ToString() => Name
            ?? (IsHost ? "Host" : "Player");
    }
}
