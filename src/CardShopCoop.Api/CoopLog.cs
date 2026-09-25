using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Optional-dependency-safe logging for integrating mods. Every call no-ops when the API is
    /// loaded but CardShopCoop has not installed its binding. Because the API assembly ships with
    /// CardShopCoop, a direct call when CardShopCoop may be absent must still be guarded — see
    /// <see cref="CoopApi"/> for the guard pattern.
    /// </summary>
    public static class CoopLog
    {
        private const int SwallowCooldownMs = 2000;
        private static readonly ConcurrentDictionary<string, int> LastSwallowed = new();

        public static void Info(string message) => Safe(log => log.LogInfo(message));

        public static void Warn(string message) => Safe(log => log.LogWarning(message));

        public static void Error(string message) => Safe(log => log.LogError(message));

        public static void Warn(Exception exception)
            => Safe(log => log.LogWarning("[caught] " + exception));

        public static void Error(Exception exception)
            => Safe(log => log.LogError(exception));

        /// <summary>Logs a deliberately contained exception, rate-limited to one line per call site
        /// per two seconds and tagged with caller context. Use this in per-frame or per-object
        /// catch blocks where a persistent failure would otherwise flood the log.</summary>
        public static void Swallow(Exception exception,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (exception == null)
            {
                return;
            }

            string filePart;
            try
            {
                filePart = string.IsNullOrEmpty(file) ? "?" : Path.GetFileNameWithoutExtension(file);
            }
            catch
            {
                filePart = "?";
            }

            var site = filePart + "." + (member ?? "?") + ":" + line;
            var now = Environment.TickCount;
            if (LastSwallowed.TryGetValue(site, out var last)
                && unchecked(now - last) < SwallowCooldownMs)
            {
                return;
            }

            LastSwallowed[site] = now;
            Safe(log => log.LogWarning("[caught] " + site + ": " + exception));
        }

        private static void Safe(Action<BepInEx.Logging.ManualLogSource> write)
        {
            var log = CoopApi.Log;
            if (log == null)
            {
                return;
            }

            try
            {
                write(log);
            }
            catch
            {
                // Diagnostics must never throw into a caller's frame.
            }
        }
    }
}
