using CardShopCoop.Attributes;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Owns the cross-build furniture checkout gate for the process lifetime. The gate is
    /// CLIENT-only: it refuses a joiner's local checkout of a furniture type the host cannot
    /// represent by name. A host is never refused - its ids are the wire identity, and a peer
    /// coalesces what it ingests - so the same hook does not fire against a host's own purchase.
    /// See <see cref="CatalogCoalescePatches"/>.</summary>
    [PersistentBehaviour]
    public sealed class CatalogCoalesceBehaviour : CoopBehaviour
    {
        private bool _shutdown;
        private bool _initialized;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            CatalogCoalescePatches.Apply(Harmony);
            _initialized = true;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
        }

        private void OnDestroy() => Shutdown();
    }
}
