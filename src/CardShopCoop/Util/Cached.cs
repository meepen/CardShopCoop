namespace CardShopCoop.Util
{
    /// <summary>
    /// A lazily-resolved, session-scoped reference holder. The non-virtual
    /// <see cref="Get"/> owns the cache check: it returns the stored value while it is usable and
    /// only calls the virtual <see cref="GetRawValue"/> when the cache is empty. A subclass
    /// supplies the one expensive lookup (a scene search, a manager resolve, ...) and never has
    /// to repeat the guard.
    ///
    /// "Usable" covers both a plain null reference and a destroyed Unity object: a generic
    /// <c>value != null</c> does not bind <c>UnityEngine.Object</c>'s overloaded equality, so a
    /// destroyed object must be detected explicitly. A plain array cached here has no Unity
    /// fake-null behaviour, so a caller that caches an array must <see cref="Clear"/> it on the
    /// scene/session boundary that invalidates it.
    /// </summary>
    public abstract class Cached<T>
        where T : class
    {
        private T _value;

        /// <summary>The cached value, resolved on first use and re-resolved whenever the stored
        /// value is null or a destroyed Unity object.</summary>
        public T Get()
        {
            if (_value != null && !IsDestroyed(_value))
            {
                return _value;
            }

            _value = GetRawValue();
            return _value;
        }

        /// <summary>Forget the cached value so the next <see cref="Get"/> re-resolves it. Call on
        /// a scene/session boundary.</summary>
        public void Clear()
        {
            _value = null;
        }

        /// <summary>The expensive lookup, run only when there is no usable cached value. The base
        /// returns null (a permanent "not resolvable, retry next access"); every real cache
        /// overrides it.</summary>
        protected virtual T GetRawValue()
        {
            return null;
        }

        private static bool IsDestroyed(T value)
        {
            // Unity's fake-null: `unityObject == null` is true once the object is destroyed,
            // even though the managed reference is still non-null.
            return value is UnityEngine.Object unityObject && unityObject == null;
        }
    }
}


