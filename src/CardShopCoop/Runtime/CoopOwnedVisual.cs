using UnityEngine;

namespace CardShopCoop.Runtime
{
    /// <summary>Marks a visual-only GameObject the mod instantiated itself.
    ///
    /// The Npc client suppresses vanilla pool activation by watching for <c>Customer</c>/
    /// <c>Worker</c> roots becoming active. A preview or avatar clone still carries its
    /// <c>Customer</c> component at that moment, so without this marker the guard mistakes it
    /// for a native pool spawn, deactivates it, and the body is then dressed while inactive -
    /// invisible (the preview) or partially dressed (avatars) on every client.</summary>
    internal sealed class CoopOwnedVisual : MonoBehaviour
    {
    }
}
