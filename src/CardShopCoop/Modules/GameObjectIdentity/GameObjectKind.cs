namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>The kinds of world object whose identity is owned by this module.</summary>
    public enum GameObjectKind : byte
    {
        None = 0,
        NpcCustomer = 1,
        NpcWorker = 2,
        Placement = 3,
        Decoration = 4,
        PackagingBox = 5,
        /// <summary>A session-wide extended-information slot. It has no live object; the
        /// information type identifies the value.</summary>
        Global = 6,
        /// <summary>A placed furniture object and the packaging box it is currently boxed into.
        /// Both live representations share this one session identity; the box never mints its
        /// own Guid.</summary>
        Furniture = 7,
    }
}
