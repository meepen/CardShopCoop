namespace CardShopCoop.UI
{
    /// <summary>
    /// The on-screen alert for a network failure the player has to be told about - currently a
    /// reliable message whose handler threw and was therefore discarded - and for an accepted
    /// invite that cannot start a session (already in one, or a shop is loaded). The core's
    /// dispatch loop (main thread) writes it; <see cref="CoopUI"/> owns the drawing and the
    /// dismiss button.
    ///
    /// It holds only the most recent alert and how many times that exact alert has repeated,
    /// so a handler that throws on every message cannot stack a pile of popups - the count just
    /// climbs.
    /// </summary>
    internal static class CoopAlert
    {
        private const string DefaultHeader = "CO-OP ERROR";

        private static string _header = DefaultHeader;
        private static string _title = "";
        private static string _detail = "";
        private static int _repeatCount;

        public static bool Active => _title.Length > 0;

        /// <summary>The window chrome title: "CO-OP ERROR" for a failure, or a caller-supplied
        /// header for a notice that is not an error (an invite the player cannot act on).</summary>
        public static string Header => _header;

        public static string Title => _title;

        public static string Detail => _detail;

        public static int RepeatCount => _repeatCount;

        public static void Show(string title, string detail) => Show(title, detail, null);

        /// <summary>Raises the alert, or bumps its count when the exact same failure repeats.</summary>
        public static void Show(string title, string detail, string header)
        {
            title ??= "";
            detail ??= "";
            header = string.IsNullOrEmpty(header) ? DefaultHeader : header;
            if (_title.Length > 0 && _title == title && _detail == detail && _header == header)
            {
                _repeatCount++;
                return;
            }

            _header = header;
            _title = title;
            _detail = detail;
            _repeatCount = 1;
        }

        public static void Dismiss()
        {
            _header = DefaultHeader;
            _title = "";
            _detail = "";
            _repeatCount = 0;
        }
    }
}
