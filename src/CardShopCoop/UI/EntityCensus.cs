using System.Text;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Debug-only census of the live scene entities, shown at the bottom of the SETTINGS tab so a
    /// runaway spawn (the "ghost boxes" lag report) shows up as a count that climbs on its own. It
    /// is a diagnostic, not gameplay state, so it lives outside every module and keeps working
    /// while modules are toggled off.
    ///
    /// <see cref="Tick"/> logs the census every <see cref="LogSeconds"/> from the window draw, which
    /// runs whether or not the window is visible. <see cref="Text"/> feeds the on-screen line. Both
    /// rebuild the scan at most once per second: OnGUI runs multiple times per frame and each count
    /// is a full scene scan. The scan uses <see cref="Resources.FindObjectsOfTypeAll{T}"/> (the same
    /// non-obsolete lookup the other interops use) rather than the Unity 6-deprecated
    /// FindObjectsOfType, and rejects prefab assets by requiring a valid scene. Inactive scene
    /// objects are counted on purpose: a leaked entity is often disabled, and seeing it is the
    /// whole point.
    /// </summary>
    internal static class EntityCensus
    {
        private const float RefreshSeconds = 1f;
        private const float LogSeconds = 30f;

        private static string _text;
        private static float _refreshedAt = float.NegativeInfinity;
        private static float _loggedAt = float.NegativeInfinity;

        /// <summary>Logs the census every <see cref="LogSeconds"/>. Called once per OnGUI frame, so
        /// it keeps logging even while the co-op window is closed.</summary>
        internal static void Tick()
        {
            var now = Time.unscaledTime;
            if (now - _loggedAt < LogSeconds)
            {
                return;
            }

            _loggedAt = now;
            CoopPlugin.Log.LogInfo("[entities] " + Refresh(now));
        }

        internal static string Text() => Refresh(Time.unscaledTime);

        private static string Refresh(float now)
        {
            if (_text == null || now - _refreshedAt >= RefreshSeconds)
            {
                _refreshedAt = now;
                _text = Snapshot();
            }

            return _text;
        }

        private static string Snapshot()
        {
            // Boxes and cards are themselves InteractableObjects, so subtract them from the base
            // count to report furniture as the placed objects that are neither. One line: the label
            // wraps (LabelDimWrap), so a long line cannot widen the 400 px panel.
            var boxes = CountScene<InteractablePackagingBox>();
            var cards = CountScene<InteractableCard3d>();
            var builder = new StringBuilder(96);
            Append(builder, "furniture", CountScene<InteractableObject>() - boxes - cards);
            Append(builder, "boxes", boxes);
            Append(builder, "cards", cards);
            Append(builder, "items", CountScene<Item>());
            Append(builder, "customers", CountScene<Customer>());
            Append(builder, "workers", CountScene<Worker>());
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string label, int count)
        {
            if (builder.Length > 0)
            {
                builder.Append("   ");
            }

            builder.Append(label).Append('=').Append(count);
        }

        private static int CountScene<T>() where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            var count = 0;
            for (var i = 0; i < all.Length; i++)
            {
                var component = all[i];
                if (component != null && component.gameObject.scene.IsValid())
                {
                    count++;
                }
            }

            return count;
        }
    }
}
