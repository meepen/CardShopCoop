using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Generic per-frame profiler for the mod's own main-thread code. When <see cref="PerfProbe"/>
    /// is enabled it Harmony-wraps, with a stopwatch:
    /// <list type="bullet">
    /// <item>every <c>Update</c>/<c>LateUpdate</c> declared by a MonoBehaviour in this assembly, and</item>
    /// <item>every Harmony patch body (<c>Prefix</c>/<c>Postfix</c>/<c>Transpiler</c>) in this assembly,
    /// which runs inside a patched GAME method rather than in one of our own callbacks.</item>
    /// </list>
    /// and reports a per-window breakdown. This exists so a mod-side frame cost can be located
    /// without hand-instrumenting each behaviour or patch with a PerfProbe stage.
    /// </summary>
    internal static class UpdateProfiler
    {
        private const string HarmonyId = "com.zwhit.cardshopcoop.perf.update-profiler";
        private static readonly string[] CallbackPhases = { "Update", "LateUpdate" };

        private sealed class Stat
        {
            internal long Calls;
            internal long Ticks;
            internal long MaxTicks;
        }

        // Main thread only: Unity callbacks and Harmony patch bodies for game methods all run on
        // the player-loop thread, never on a worker thread.
        private static readonly Dictionary<string, Stat> Stats = new();

        // Guards Stats: callbacks and patch bodies overwhelmingly run on the player-loop thread,
        // but a patch on a game method that some system calls off-thread must not corrupt it.
        private static readonly object StatsLock = new();

        private static Harmony _harmony;
        private static bool _installed;

        /// <summary>Idempotent; no-op unless PerfProbe is enabled. Safe to call every window.</summary>
        internal static void Install()
        {
            if (_installed || !PerfProbe.Enabled)
            {
                return;
            }

            _installed = true;
            _harmony = new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(UpdateProfiler), nameof(Prefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(UpdateProfiler), nameof(Postfix)));
            var callbacks = 0;
            var patches = 0;
            foreach (var type in SafeGetTypes())
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    foreach (var phase in CallbackPhases)
                    {
                        var method = type.GetMethod(phase,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (method == null || method.IsAbstract || !TryPatch(method, prefix, postfix))
                        {
                            continue;
                        }
                        callbacks++;
                    }
                }

                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!IsPatchBody(method) || !TryPatch(method, prefix, postfix))
                    {
                        continue;
                    }
                    patches++;
                }
            }

            CoopPlugin.Log.LogInfo("[perf-update] profiling " + callbacks + " Update/LateUpdate + "
                + patches + " Harmony patch method(s)");
        }

        private static bool TryPatch(MethodBase method, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                _harmony.Patch(method, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("[perf-update] could not profile "
                    + Label(method) + ": " + error.Message);
                return false;
            }
        }

        private static bool IsPatchBody(MethodInfo method)
            => method.IsDefined(typeof(HarmonyPrefix), false)
                || method.IsDefined(typeof(HarmonyPostfix), false)
                || method.IsDefined(typeof(HarmonyTranspiler), false);

        internal static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception error)
            {
                Swallow.Log(error);
            }
            _harmony = null;
            lock (StatsLock)
            {
                Stats.Clear();
            }
        }

        internal static void Reset()
        {
            lock (StatsLock)
            {
                Stats.Clear();
            }
        }

        private static void Prefix(out long __state)
            => __state = PerfProbe.Enabled ? Stopwatch.GetTimestamp() : 0L;

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            if (__state == 0L)
            {
                return;
            }

            var label = Label(__originalMethod);
            var elapsed = Stopwatch.GetTimestamp() - __state;
            lock (StatsLock)
            {
                if (!Stats.TryGetValue(label, out var stat))
                {
                    stat = new Stat();
                    Stats[label] = stat;
                }

                stat.Calls++;
                stat.Ticks += elapsed;
                if (elapsed > stat.MaxTicks)
                {
                    stat.MaxTicks = elapsed;
                }
            }
        }

        /// <summary>Emits one line naming every profiled mod callback/patch by cost, then clears
        /// the window. Called from <see cref="PerfProbe"/> on its average-window boundary.</summary>
        internal static void Flush(double seconds)
        {
            string line;
            lock (StatsLock)
            {
                if (Stats.Count == 0)
                {
                    return;
                }

                var totalTicks = 0L;
                var parts = new List<string>(Stats.Count);
                foreach (var pair in Stats.OrderByDescending(pair => pair.Value.Ticks))
                {
                    var stat = pair.Value;
                    totalTicks += stat.Ticks;
                    parts.Add(pair.Key
                        + "=" + TicksToMs(stat.Ticks / Math.Max(1, stat.Calls)).ToString("F3") + "ms avg/"
                        + TicksToMs(stat.MaxTicks).ToString("F2") + "ms max"
                        + " x" + stat.Calls);
                }

                Stats.Clear();
                line = "[perf-update] total=" + TicksToMs(totalTicks).ToString("F2")
                    + "ms over " + seconds.ToString("F1") + "s: [" + string.Join(", ", parts) + "]";
            }

            CoopPlugin.Log.LogInfo(line);
        }

        private static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        /// <summary>Nested type + method path, e.g. <c>CoopCore.Update</c> or
        /// <c>DecorationClientBehaviour.EquipPatch.Prefix</c>.</summary>
        private static string Label(MethodBase method)
        {
            var names = new List<string>();
            for (var type = method.DeclaringType; type != null; type = type.DeclaringType)
            {
                names.Add(type.Name);
            }
            names.Reverse();
            names.Add(method.Name);
            return string.Join(".", names);
        }

        private static IEnumerable<Type> SafeGetTypes()
        {
            try
            {
                return typeof(UpdateProfiler).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                var types = new List<Type>();
                foreach (var type in error.Types)
                {
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }
                return types;
            }
        }
    }
}
