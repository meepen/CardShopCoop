using System;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Single canonical identity for every object owned by the identity module. The payload keeps
    /// each game domain's existing stable identity: NPC (kind, pool index, incarnation), placement
    /// (packed placement key), packaging box (Guid), decoration (existing local/host id), and
    /// furniture (a session Guid shared by the placed object and its packaging box).
    /// </summary>
    public readonly struct GameObjectId : IEquatable<GameObjectId>
    {
        public readonly GameObjectKind Kind;
        public readonly byte SubKind;
        public readonly long Value;
        public readonly Guid GuidValue;

        private GameObjectId(GameObjectKind kind, byte subKind, long value, Guid guidValue)
        {
            Kind = kind;
            SubKind = subKind;
            Value = value;
            GuidValue = guidValue;
        }

        public bool IsValid
        {
            get
            {
                return Kind switch
                {
                    GameObjectKind.NpcCustomer or GameObjectKind.NpcWorker => NpcIdentity > 0,
                    GameObjectKind.Placement or GameObjectKind.Decoration => Value != 0,
                    GameObjectKind.PackagingBox or GameObjectKind.Furniture =>
                        GuidValue != Guid.Empty,
                    GameObjectKind.Global => Value > 0,
                    _ => false,
                };

            }
        }

        public int NpcIndex => (int)((Value >> 32) & 0xFFFF);

        public int NpcIdentity => (int)(Value & 0xFFFFFFFF);

        public static GameObjectId Npc(GameObjectKind kind, int index, int identity)
        {
            if (kind != GameObjectKind.NpcCustomer && kind != GameObjectKind.NpcWorker)
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            return new GameObjectId(kind, 0,
                ((long)(ushort)index << 32) | (uint)identity, Guid.Empty);
        }

        public static GameObjectId Placement(int placementKind, int key)
            => new GameObjectId(GameObjectKind.Placement, (byte)placementKind, key, Guid.Empty);

        public static GameObjectId Decoration(long id)
            => new GameObjectId(GameObjectKind.Decoration, 0, id, Guid.Empty);

        public static GameObjectId Box(Guid id)
            => new GameObjectId(GameObjectKind.PackagingBox, 0, 0, id);

        /// <summary>The session identity of one placed furniture object. Its packaging box is a
        /// view of the same id, so boxing/unboxing never allocates a second identity.</summary>
        public static GameObjectId Furniture(Guid id)
            => new GameObjectId(GameObjectKind.Furniture, 0, 0, id);

        /// <summary>The one slot for session-wide extended information. The concrete information
        /// type identifies the entry, so a single slot is enough.</summary>
        public static GameObjectId Global()
            => new(GameObjectKind.Global, 0, 1, Guid.Empty);

        internal static GameObjectId Create(GameObjectKind kind, byte subKind, long value,
            Guid guidValue)
            => new GameObjectId(kind, subKind, value, guidValue);

        public bool Equals(GameObjectId other)
            => Kind == other.Kind && SubKind == other.SubKind && Value == other.Value
                && GuidValue == other.GuidValue;

        public override bool Equals(object obj)
            => obj is GameObjectId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Kind;
                hash = (hash * 397) ^ SubKind;
                hash = (hash * 397) ^ Value.GetHashCode();
                hash = (hash * 397) ^ GuidValue.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
            => Kind == GameObjectKind.PackagingBox || Kind == GameObjectKind.Furniture
                ? Kind + "[" + GuidValue + "]"
                : Kind + "[" + SubKind + ":" + Value + "]";

        public static bool operator ==(GameObjectId left, GameObjectId right) => left.Equals(right);

        public static bool operator !=(GameObjectId left, GameObjectId right) => !left.Equals(right);
    }
}
