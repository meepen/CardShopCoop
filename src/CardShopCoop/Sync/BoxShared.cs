using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Cross-module box state that belongs to none of the families: whether sync code is
    /// currently applying a remote change, the local-destroy notification, and the shared
    /// per-connection removal rate limiter.
    /// </summary>
    public static class BoxShared
    {
        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox> LocalBoxDestroyed;

        private static readonly Dictionary<int, double> _remWindowStart = new Dictionary<int, double>();
        private static readonly Dictionary<int, int> _remWindowCount = new Dictionary<int, int>();

        /// <summary>Diagnostics toggle (CoopPlugin "BoxSyncDebug"). When on, the box engine
        /// logs each client possession report, each host accept/reject, and each adopt/spawn.</summary>
        public static bool Debug => CoopPlugin.BoxSyncDebug != null && CoopPlugin.BoxSyncDebug.Value;

        private static readonly Dictionary<int, double> _debugLast = new Dictionary<int, double>();

        /// <summary>Throttled diagnostic line. key == 0 logs unthrottled; otherwise at most
        /// one line per <paramref name="throttle"/> seconds for that key (avoids per-frame spam).</summary>
        public static void DebugLog(string tag, string message, int key = 0, float throttle = 0.5f)
        {
            if (!Debug)
                return;
            try
            {
                if (key != 0 && throttle > 0f)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (_debugLast.TryGetValue(key, out var last) && now - last < throttle)
                        return;
                    _debugLast[key] = now;
                }
                CoopPlugin.Log.LogInfo($"[{tag}] {message}");
            }
            catch { }
        }

        public static void ResetDebugThrottles()
        {
            _debugLast.Clear();
        }

        /// <summary>Shared by ALL box-family modules (item/card/furniture): true if this
        /// client's removal budget for the current 2s window is spent. A human trashing a
        /// warehouse won't exceed 16 boxes in 2 seconds - but a client whose game is
        /// re-running its world-load teardown echoes its ENTIRE box population as "trashed"
        /// (first field incident: 250 boxes in 10ms). Per-connection, so one player's flood
        /// never eats another's legitimate trash. refusedId (optional) names the box in the
        /// throttled over-budget warning.</summary>
        public static bool RemovalFlooded(int connId, string channel, int refusedId = -1)
        {
            double nowT = Time.realtimeSinceStartupAsDouble;
            if (!_remWindowStart.TryGetValue(connId, out double start) || nowT - start > 2.0)
            {
                _remWindowStart[connId] = nowT;
                _remWindowCount[connId] = 0;
            }
            int c = _remWindowCount[connId] = _remWindowCount[connId] + 1;
            // C-b: cap raised 4 -> 16 (4x). Friends-coop, not anti-grief-critical: the old
            // cap of 4/2s stranded host-side boxes during a legitimate warehouse cleanup
            // spree. A reload teardown echo still floods hundreds in milliseconds, so the
            // flood protection stays; only the ceiling moves.
            if (c <= 16)
                return false;
            if (c == 17 || c % 100 == 0)
                CoopPlugin.Log.LogWarning($"ignoring {channel} removal"
                    + (refusedId >= 0 ? $" of box id {refusedId}" : "")
                    + $" from client {connId} - removal budget spent for this 2s window (reload echo, not gameplay)");
            return true;
        }

        public static void ResetRateLimiter()
        {
            _remWindowStart.Clear();
            _remWindowCount.Clear();
        }
    }
}
