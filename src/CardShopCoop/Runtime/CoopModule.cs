using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Common state helper for world-owned co-op interactions. Modules keep their own reset and
    /// send logic; this base provides the small shared surface and one contextual error boundary.
    /// </summary>
    public abstract class CoopModule
    {
        public abstract string Name
        {
            get;
        }

        /// <summary>Clears state associated with the current world or save.</summary>
        public virtual void Reset()
        {
        }

        /// <summary>Invalidates change gates so the next host tick sends a baseline.</summary>
        public virtual void ForceResend()
        {
        }

        /// <summary>Host: send this module's COMPLETE state to one connection - the join
        /// catch-up / explicit re-baseline path. Never called periodically.</summary>
        public virtual void FullUpdate(PeerConnection connection)
        {
        }

        /// <summary>Runs one module step under the shared error boundary. Exceptions are logged
        /// with module context and rate-limited per tag.</summary>
        protected void Guarded(string label, Action action)
        {
            ModuleGuard.Run(Name + ":" + label, action);
        }
    }

    /// <summary>
    /// Central rate-limited logger for module error boundaries. One line per tag per
    /// <see cref="CooldownSeconds"/>, always with the full exception so a support log has the
    /// stack. Keys are the fixed module/step tags, so the map is bounded.
    /// </summary>
    internal static class ModuleGuard
    {
        private const double CooldownSeconds = 5.0;
        private static readonly Dictionary<string, double> NextAt = new();

        public static void Run(string tag, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log(tag, e);
            }
        }

        /// <summary>The single rate-limited error logger for module boundaries: one line per tag
        /// per <see cref="CooldownSeconds"/>, including the full exception.</summary>
        public static void Log(string tag, Exception e)
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (NextAt.TryGetValue(tag, out var next) && now < next)
            {
                return;
            }

            NextAt[tag] = now + CooldownSeconds;
            CoopPlugin.Log.LogError("[" + tag + "] " + e);
        }
    }

}




