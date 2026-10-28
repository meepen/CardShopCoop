using System;
using UnityEngine;

namespace CardShopCoop.Modules.Hud
{
    /// <summary>
    /// Read-only presentation surface for the co-op UI. Network and game-time modules update
    /// this state; the native co-op screen only reads it and remains responsible for drawing.
    /// </summary>
    internal static class HudPresentationState
    {
        private const float DefaultToastSeconds = 8f;
        private static string _toastText = "";
        private static float _toastRemaining;
        private static string _hostTimeText = "";
        private static bool _nextDayWaitActive;
        private static string _nextDayWaitText = "";

        /// <summary>Deadline (Time.time) of the end-of-day auto-continue countdown, or -1 when no
        /// timer is running. Stored rather than the remaining seconds so the pill ticks live
        /// between updates without anyone resending state.</summary>
        private static float _nextDayCountdownEnd = -1f;
        private static bool _endOfDayPendingActive;
        private static string _endOfDayPendingText = "";

        public static string ToastText => _toastText;
        public static float ToastRemaining => _toastRemaining;
        public static string HostTimeText => _hostTimeText;
        public static bool NextDayWaitActive => _nextDayWaitActive;
        public static string NextDayWaitText => ComposeNextDayWaitText();
        public static bool EndOfDayPendingActive => _endOfDayPendingActive;
        public static string EndOfDayPendingText => _endOfDayPendingText;

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

        /// <summary>Persistent "waiting for other players" line, shown while the end-of-day
        /// ready gate is waiting on players who have not pressed Next Day. When a countdown is
        /// armed the reader sees how long until the day continues without them; the countdown is
        /// composed here so host and guests tick from one shared formula.</summary>
        internal static void SetNextDayWait(bool active, string text, int countdownSeconds = 0)
        {
            _nextDayWaitActive = active && !string.IsNullOrEmpty(text);
            _nextDayWaitText = _nextDayWaitActive ? text : "";
            _nextDayCountdownEnd = _nextDayWaitActive && countdownSeconds > 0
                ? Time.time + countdownSeconds
                : -1f;
        }

        /// <summary>The wait line, prefixed with the live auto-continue countdown while one is
        /// armed. Once the deadline passes, the plain waiting text remains until the actual
        /// day-advance state arrives — the countdown itself is no longer true.</summary>
        private static string ComposeNextDayWaitText()
        {
            if (_nextDayCountdownEnd < 0f)
            {
                return _nextDayWaitText;
            }

            var remaining = Mathf.CeilToInt(_nextDayCountdownEnd - Time.time);
            return remaining <= 0
                ? _nextDayWaitText
                : "Next day in " + remaining + "s - " + _nextDayWaitText;
        }

        /// <summary>Persistent "the host ended the day but this player is still in a screen"
        /// notice. Shown while the guest recap cannot open yet and cleared the moment it can.</summary>
        internal static void SetEndOfDayPending(bool active, string text)
        {
            _endOfDayPendingActive = active && !string.IsNullOrEmpty(text);
            _endOfDayPendingText = _endOfDayPendingActive ? text : "";
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
            _nextDayWaitActive = false;
            _nextDayWaitText = "";
            _nextDayCountdownEnd = -1f;
            _endOfDayPendingActive = false;
            _endOfDayPendingText = "";
        }
    }
}
