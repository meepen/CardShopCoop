using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Optional-dependency-safe logging for integrating mods. Every call no-ops when the API is
    /// loaded but CardShopCoopCommunity has not installed its binding. Because the API assembly ships with
    /// CardShopCoopCommunity, a direct call when CardShopCoopCommunity may be absent must still be guarded - see
    /// <see cref="CoopApi"/> for the guard pattern.
    /// </summary>
    public static class CoopLog
    {
        private const int SwallowCooldownMs = 2000;

        /// <summary>Keys already reported once, for <see cref="First"/>.</summary>
        private static readonly ConcurrentDictionary<string, byte> ReportedOnce = new();

        /// <summary>When each key was last reported, for <see cref="FirstWithin"/>.</summary>
        private static readonly ConcurrentDictionary<string, int> LastReported = new();

        /// <summary>Writes an informational line through the shared CardShopCoop log.</summary>
        public static void Info(string message) => Safe(log => log.LogInfo(message));

        /// <summary>Writes a warning line through the shared CardShopCoop log.</summary>
        public static void Warn(string message) => Safe(log => log.LogWarning(message));

        /// <summary>Writes an error line through the shared CardShopCoop log.</summary>
        public static void Error(string message) => Safe(log => log.LogError(message));

        /// <summary>Writes a warning line for a contained exception, tagged with caller context.</summary>
        public static void Warn(Exception exception)
            => Safe(log => log.LogWarning("[caught] " + exception));

        /// <summary>Writes an error line for an exception.</summary>
        public static void Error(Exception exception)
            => Safe(log => log.LogError(exception));

        /// <summary>
        /// The gate behind every "say this once" site: true the first time this key is seen, false on
        /// every later call, so the caller skips its own log line. Use this instead of keeping a
        /// per-feature HashSet or bool - it is the once-only form of the same idea
        /// <see cref="FirstWithin"/> and <see cref="Swallow"/> use, thread-safe without a lock.
        /// </summary>
        public static bool First(string key)
            => string.IsNullOrEmpty(key) || ReportedOnce.TryAdd(key, 0);

        /// <summary>
        /// Same idea for a condition worth repeating occasionally (a per-object failure that keeps
        /// happening): true when this key has not been reported within the window, in milliseconds.
        /// </summary>
        public static bool FirstWithin(string key, int milliseconds)
        {
            if (string.IsNullOrEmpty(key))
            {
                return true;
            }

            var now = Environment.TickCount;
            if (LastReported.TryGetValue(key, out var last)
                && unchecked(now - last) < milliseconds)
            {
                return false;
            }

            LastReported[key] = now;
            return true;
        }

        /// <summary>First(key) plus one warning line; true when the line was written.</summary>
        public static bool WarnOnce(string key, string message)
        {
            if (!First(key))
            {
                return false;
            }

            Warn(message);
            return true;
        }

        /// <summary>First(key) plus one error line; true when the line was written.</summary>
        public static bool ErrorOnce(string key, string message)
        {
            if (!First(key))
            {
                return false;
            }

            Error(message);
            return true;
        }

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

            var site = Site(member, file, line);
            if (!FirstWithin(site, SwallowCooldownMs))
            {
                return;
            }

            Safe(log => log.LogWarning("[caught] " + site + ": " + exception));
        }

        /// <summary>"File.Method:line" of the caller, the key both Swallow and its callers report under.</summary>
        internal static string Site(string member, string file, int line)
        {
            string filePart;
            try
            {
                filePart = string.IsNullOrEmpty(file) ? "?" : Path.GetFileNameWithoutExtension(file);
            }
            catch
            {
                filePart = "?";
            }

            return filePart + "." + (member ?? "?") + ":" + line;
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
