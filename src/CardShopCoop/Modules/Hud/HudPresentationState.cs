using System;

namespace CardShopCoop.Modules.Hud
{
    /// <summary>
    /// Read-only presentation surface for the co-op UI. Network and game-time modules update
    /// this state; the IMGUI owner only reads it and remains responsible for drawing.
    /// </summary>
    internal static class HudPresentationState
    {
        private const float DefaultToastSeconds = 8f;
        private static string _toastText = "";
        private static float _toastRemaining;
        private static string _hostTimeText = "";

        public static string ToastText => _toastText;
        public static float ToastRemaining => _toastRemaining;
        public static string HostTimeText => _hostTimeText;

        public static bool HasToast => !string.IsNullOrEmpty(_toastText) && _toastRemaining > 0f;

        /// <summary>Sets the client HUD toast and its display lifetime.</summary>
        internal static void SetToast(string text, float seconds = DefaultToastSeconds)
        {
            _toastText = text ?? "";
            _toastRemaining = string.IsNullOrEmpty(_toastText) ? 0f : Math.Max(0f, seconds);
            if (_toastRemaining <= 0f)
            {
                _toastText = "";
            }
        }

        /// <summary>Shared status-line bridge used by the parent cutover.</summary>
        internal static void SetStatusLine(string text, float seconds)
        {
            SetToast(text, seconds);
        }

        /// <summary>Publishes the formatted host clock without exposing GameTime internals.</summary>
        internal static void SetHostTime(int day, int hour, int minute)
        {
            _hostTimeText = $"Day {day + 1}  {hour:00}:{minute:00}";
        }

        internal static void SetHostTime(string text)
        {
            _hostTimeText = text ?? "";
        }

        internal static void Tick(float deltaTime)
        {
            if (_toastRemaining <= 0f)
            {
                return;
            }

            _toastRemaining = Math.Max(0f, _toastRemaining - Math.Max(0f, deltaTime));
            if (_toastRemaining <= 0f)
            {
                _toastText = "";
            }
        }

        internal static void Clear()
        {
            _toastText = "";
            _toastRemaining = 0f;
            _hostTimeText = "";
        }
    }
}
