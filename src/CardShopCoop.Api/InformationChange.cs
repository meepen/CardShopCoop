namespace CardShopCoop.Api
{
    /// <summary>
    /// One change notification for a global or object information value. <see cref="Previous"/> is
    /// null for the first value and for a join baseline; <see cref="Current"/> is null when the
    /// entry was removed. When <see cref="Current"/> is an <see cref="IActionValue"/>, the
    /// framework applies it before the action's <see cref="ActionMessage.OnMerge"/> runs with a
    /// null response, so the value is the authoritative merge state.
    /// </summary>
    public readonly struct InformationChange<T> where T : class
    {
        internal InformationChange(T previous, T current)
        {
            Previous = previous;
            Current = current;
        }

        /// <summary>The value before this change, or null for the first value/baseline.</summary>
        public T Previous
        {
            get;
        }

        /// <summary>The value after this change, or null when the entry was removed.</summary>
        public T Current
        {
            get;
        }

        /// <summary>True when this notification reports a removal.</summary>
        public bool Removed => Current == null;

        /// <summary>True for the first value or a join baseline, where a full apply is expected.</summary>
        public bool IsFirstValue => Previous == null;
    }
}
