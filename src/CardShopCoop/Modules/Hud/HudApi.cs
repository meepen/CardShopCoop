namespace CardShopCoop.Modules.Hud
{
    /// <summary>
    /// Narrow read-only boundary for the co-op window's HUD overlays. UI code does not need to
    /// know which network handler or game-time module produced a value.
    /// </summary>
    public static class HudApi
    {
        public static string ToastText => HudPresentationState.ToastText;
        public static float ToastRemaining => HudPresentationState.ToastRemaining;
        public static bool HasToast => HudPresentationState.HasToast;
        public static string HostTimeText => HudPresentationState.HostTimeText;
        public static bool NextDayWaitActive => HudPresentationState.NextDayWaitActive;
        public static string NextDayWaitText => HudPresentationState.NextDayWaitText;

        internal static void SetToast(string text, float seconds = 8f)
        {
            HudPresentationState.SetToast(text, seconds);
        }

        internal static void SetStatusLine(string text, float seconds)
        {
            HudPresentationState.SetStatusLine(text, seconds);
        }

        internal static void SetHostTime(int day, int hour, int minute)
        {
            HudPresentationState.SetHostTime(day, hour, minute);
        }

        internal static void SetHostTime(string text)
        {
            HudPresentationState.SetHostTime(text);
        }

        internal static void SetNextDayWait(bool active, string text)
        {
            HudPresentationState.SetNextDayWait(active, text);
        }

        internal static void Clear()
        {
            HudPresentationState.Clear();
        }
    }
}
