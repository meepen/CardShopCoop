namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one rule for box possession ownership: a BoxUpdate is accepted iff the box is
    /// unowned or the sender is already the owner. Claim vs change vs release is implied
    /// by the current owner. Content-specific validation remains in each family adapter;
    /// lifecycle ownership does not.
    /// </summary>
    public static class BoxAuthority
    {
        public static bool AcceptBoxUpdate(PlayerRef currentOwner, PlayerRef sender)
        {
            return !currentOwner.IsOwned || currentOwner == sender;
        }

        /// <summary>The owner after accepting a BoxUpdate: the sender for Held/Placing,
        /// none for Free/Removed.</summary>
        public static PlayerRef NextBoxOwner(PlayerRef sender, BoxPossession possession)
        {
            return possession == BoxPossession.Held || possession == BoxPossession.Placing
                ? sender
                : PlayerRef.None;
        }
    }
}
