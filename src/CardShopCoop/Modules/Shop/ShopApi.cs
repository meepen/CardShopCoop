namespace CardShopCoop.Modules.Shop
{
    /// <summary>Narrow surface for other features (the co-op window) to read the shop's name
    /// without reaching into this module's internals - the same pattern as
    /// <see cref="Presence.PresenceApi"/>.</summary>
    public static class ShopApi
    {
        /// <summary>The canonical shop name, or false before one exists. On a client this is the
        /// host's name, applied by this module from the rename stream.</summary>
        public static bool TryGetShopName(out string name) => ShopState.TryGetCanonical(out name);
    }
}
