using System;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Marks a game write this behaviour performs itself. A Harmony observer watching the same
    /// game method asks <see cref="ReentrancyGuard.Suppressed"/> and skips the change: it is
    /// authority being applied, not a local player action to forward or broadcast. Nesting is
    /// counted, so one authoritative write may drive another observed method.
    /// </summary>
    internal sealed class ObservationScope
    {
        private int _depth;

        internal bool Active => _depth > 0;

        internal Entry Enter() => new(this);

        internal void Exit()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }

        /// <summary>Disposable entry so an exception cannot leave the scope latched. A Harmony
        /// prefix/finalizer pair may use <see cref="Enter"/>/<see cref="Exit"/> instead when the
        /// scope must span the vanilla method rather than a module body.</summary>
        internal readonly struct Entry : IDisposable
        {
            private readonly ObservationScope _owner;

            internal Entry(ObservationScope owner)
            {
                _owner = owner;
                owner._depth++;
            }

            public void Dispose() => _owner.Exit();
        }
    }
}
