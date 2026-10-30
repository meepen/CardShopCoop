using CardShopCoop.Modules.Actions;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// The one question a Harmony observer asks before forwarding a game change:
    /// <code>if (ReentrancyGuard.Suppressed(_active)) return;</code>
    ///
    /// True when the call is authority being applied rather than a local player action: the
    /// behaviour is gone, one of its own handlers is dispatching (the framework marks every
    /// message/global-information/action apply), its <see cref="ObservationScope"/> is open for a
    /// write performed outside a handler, or the action ledger is undoing, applying authoritative
    /// state, or merging a local action. The module keeps no suppression state of its own.
    /// </summary>
    internal static class ReentrancyGuard
    {
        /// <summary>True while the action system is reversing, applying, or merging a prediction.
        /// Modules that are not behaviours use this instead of reaching into the ledger.</summary>
        internal static bool ActionApplying => ActionLedger.IsApplying;

        internal static bool Suppressed(CoopBehaviour owner)
            => owner == null
                || owner.IsApplyingAuthority
                || owner.Writes.Active
                || ActionLedger.IsApplying;

        /// <summary>For a local player-action observer that must still forward while another
        /// ledger action is applying. Only this behaviour's own authority dispatch or write scope
        /// means "this is our replay", not any unrelated ledger replay.</summary>
        internal static bool SuppressedForLocalObservation(CoopBehaviour owner)
            => owner == null || owner.IsApplyingAuthority || owner.Writes.Active;
    }
}
