using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Cross-module box state that belongs to none of the families: whether sync code is
    /// currently applying a remote change and the local-destroy notification.
    /// </summary>
    public static class BoxShared
    {
        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox> LocalBoxDestroyed;

        /// <summary>Diagnostics toggle (CoopPlugin "BoxSyncDebug"). When on, the box engine
        /// logs each client possession report, each host accept/reject, and each adopt/spawn.</summary>
        public static bool Debug => CoopPlugin.BoxSyncDebug != null && CoopPlugin.BoxSyncDebug.Value;

        private static readonly Dictionary<int, double> _debugLast = new Dictionary<int, double>();
        internal static int DebugThrottleCount => _debugLast.Count;

        /// <summary>Two-phase diagnostic gate. Call this FIRST, then build the message and
        /// pass it to <see cref="DebugLog"/>. Interpolating the message at the call site built
        /// the string on every call even when the diagnostic was off - steady garbage on the
        /// per-frame/per-box paths. key == 0 logs unthrottled; otherwise at most one line per
        /// <paramref name="throttle"/> seconds for that key (avoids per-frame spam).</summary>
        public static bool ShouldDebugLog(int key = 0, float throttle = 0.5f)
        {
            if (!Debug)
                return false;
            try
            {
                if (key != 0 && throttle > 0f)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (_debugLast.TryGetValue(key, out var last) && now - last < throttle)
                        return false;
                    _debugLast[key] = now;
                }
                return true;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Raw diagnostic emission. Only call from behind <see cref="ShouldDebugLog"/>,
        /// so the message is built exactly when it is going to be written.</summary>
        public static void DebugLog(string tag, string message)
        {
            try
            {
                CoopPlugin.Log.LogInfo($"[{tag}] {message}");
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public static void ResetDebugThrottles()
        {
            _debugLast.Clear();
        }

        /// <summary>True while ANY holder has this box in hand - the local player or a worker.
        /// The player-only IsLocallyCarried guard left worker-held boxes read as Free, so a
        /// client's mirror was dragged along the floor following the restocker instead of
        /// staying in the worker's hands. Q-drag placement does not set this flag, so
        /// BoxPossession.Placing is unaffected.</summary>
        public static bool IsHeldByAnyone(InteractablePackagingBox box)
        {
            if (box == null)
                return false;
            try
            {
                return BoxFields.BeingHold?.GetValue(box) is bool held && held;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

    }
}
