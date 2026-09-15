using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Non-fabricating lookup for scene-lifetime managers.
    ///
    /// NEVER use <c>CSingleton&lt;T&gt;.Instance</c> for these. The game's getter auto-creates an
    /// empty DontDestroyOnLoad <c>(singleton)T</c> object the instant it is touched while no real
    /// instance exists - boot, the title screen, a guest's world reload, a host's mid-session save
    /// load - and that empty copy then shadows the real manager for the rest of the run. Every
    /// serialized reference on the copy is null (e.g. <c>CGameManager.m_TextSO</c>, sprite and UI
    /// refs), which silently disables whole systems: the HUD tooltip list then falls back to its
    /// prefab placeholder ("Action Name" + the default key image) for the whole session.
    ///
    /// <c>FindObjectOfType</c> never creates anything; the cache invalidates through Unity's
    /// fake-null when the real object is destroyed and re-resolves on the next call.
    /// </summary>
    internal static class SceneRef<T> where T : Component
    {
        private static T _cached;

        internal static T Get()
        {
            if (_cached == null)
                _cached = UnityEngine.Object.FindObjectOfType<T>();
            return _cached;
        }
    }
}
