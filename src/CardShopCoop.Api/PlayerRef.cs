using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// A player in the session, for messages that name someone other than their sender. The
    /// identity on the wire is <see cref="Id"/>, which is host-canonical: 0 is the host and
    /// 1..N are clients, in the ids the host assigned. <see cref="Name"/> travels as the sender's
    /// snapshot; <see cref="IsHost"/> is derived from the id and <see cref="IsSelf"/> is local.
    /// A <see cref="PlayerRef"/> in a DTO is a reference supplied by the sender, not an
    /// authentication token; use <see cref="CoopMessageContext.Sender"/> for the authenticated
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

        /// <summary>Host-canonical player id: 0 is the host, 1..N are clients.</summary>
        public int Id
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
            ?? (IsHost ? "Host" : "Player " + Id);
    }
}
