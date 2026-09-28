namespace CardShopCoop.Modules.Hud
{
    /// <summary>
    /// Marks the client game path of an action whose wallet/experience side effects are owned by a
    /// module intent: the host validates and charges that action exactly once, so the Hud economy
    /// observer must not forward the resulting <c>CEventPlayer_*</c> as a second contribution.
    /// The game still runs the event locally (the client plays vanilla); only the forwarding is
    /// skipped.
    ///
    /// Nesting is counted because one owning action can invoke another (a bill day-rollover accrual
    /// calls the forced bill payment it wraps). Enter on the owning hook's prefix, exit from its
    /// Harmony finalizer so an exception cannot leave the scope latched.
    /// </summary>
    internal static class EconomyActionScope
    {
        private static int _depth;

        internal static bool Active => _depth > 0;

        internal static void Enter() => _depth++;

        internal static void Exit()
        {
            if (_depth > 0)
                _depth--;
        }
    }
}
