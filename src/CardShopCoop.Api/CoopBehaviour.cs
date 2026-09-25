using UnityEngine;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Base class for an external co-op behaviour. Mark the concrete class with
    /// <c>[ServerBehaviour]</c>, <c>[ClientBehaviour]</c>, or <c>[PersistentBehaviour]</c> and
    /// CardShopCoop creates it in the live session and assigns <see cref="Context"/>.
    ///
    /// A mod may instead use plain <see cref="MonoBehaviour"/>; CardShopCoop accepts any
    /// attributed component. The base class is only a convenience that carries the context.
    /// </summary>
    public abstract class CoopBehaviour : MonoBehaviour
    {
        /// <summary>The live session, or null outside a session. Assigned by CardShopCoop.</summary>
        public ICoopContext Context
        {
            get;
            private set;
        }

        internal void SetContext(ICoopContext context)
        {
            Context = context;
        }
    }
}
