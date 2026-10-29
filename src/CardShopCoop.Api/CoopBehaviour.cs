using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Base class for an external co-op behaviour. Mark the concrete class with
    /// <c>[ServerBehaviour]</c>, <c>[ClientBehaviour]</c>, or <c>[PersistentBehaviour]</c> and
    /// CardShopCoop creates it in the live session, binds its attributed handlers, and assigns
    /// <see cref="Context"/>.
    ///
    /// A mod may instead use plain <see cref="MonoBehaviour"/>; CardShopCoopCommunity accepts any
    /// attributed component. The base class is only a convenience that carries the context and
    /// the shared Harmony instance.
    /// </summary>
    public abstract class CoopBehaviour : MonoBehaviour
    {
        /// <summary>The live session, or null outside a session. Assigned by CardShopCoop.</summary>
        public ICoopContext Context
        {
            get;
            private set;
        }

        /// <summary>
        /// The Harmony instance owned by CardShopCoop for this behaviour. The runtime creates it
        /// before <c>OnEnable</c> runs, so a behaviour may call
        /// <c>Harmony.Patch</c> or <c>CreateClassProcessor(...).Patch()</c> from its own setup.
        ///
        /// The runtime unpatches this instance when the behaviour is disposed. A mod must not
        /// call <c>UnpatchSelf</c> or <c>UnpatchAll</c>: patch removal belongs to the runtime, and
        /// removing patches from inside the behaviour leaves the runtime's bookkeeping stale.
        /// </summary>
        public Harmony Harmony
        {
            get;
            private set;
        }

        internal void SetHarmony(Harmony harmony)
        {
            Harmony = harmony;
        }

        internal void SetContext(ICoopContext context)
        {
            Context = context;
        }
    }
}
