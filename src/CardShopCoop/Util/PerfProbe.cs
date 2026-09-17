using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Lightweight per-frame stage timing. When CoopPlugin.PerfDebug is on, Measure() times
    /// one sync stage and logs a single line when it exceeds ThresholdMs (rate-limited per
    /// stage) so a real session produces a short list of the actual lag spikes instead of a
    /// flood of numbers.
    /// </summary>
    internal static class PerfProbe
    {
        private const double ThresholdMs = 5.0;
        private const double RepeatSeconds = 2.0;

        private static readonly Dictionary<string, double> _lastLog = new Dictionary<string, double>();
        private static readonly ConcurrentDictionary<string, ThreadMetric> _threadMetrics
            = new ConcurrentDictionary<string, ThreadMetric>();

        private sealed class ThreadMetric
        {
            public long Count;
            public long TotalTicks;
            public long MaxTicks;
        }

        public static bool Enabled
        {
            get
            {
                try
                {
                    return CoopPlugin.PerfDebug != null && CoopPlugin.PerfDebug.Value;
                }
                catch (System.Exception e) { Swallow.Log(e); return false; }
            }
        }

        /// <summary>Run <paramref name="action"/> and, when probing, log it if it runs long.
        /// Exceptions propagate to the caller (CoopCore.Guarded handles them).</summary>
        public static void Measure(string stage, Action action)
        {
            if (!Enabled)
            {
                action();
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (ms >= ThresholdMs)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (!_lastLog.TryGetValue(stage, out var last) || now - last >= RepeatSeconds)
                    {
                        _lastLog[stage] = now;
                        CoopPlugin.Log.LogWarning($"[perf] {stage} took {ms:F1} ms");
                    }
                }
            }
        }

        public static void Reset()
        {
            _lastLog.Clear();
        }

        /// <summary>Allocation-free variant for call sites that can't pass a delegate:
        /// Start() returns a token (0 when probing is off) and End() logs the span.</summary>
        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(string stage, long start)
        {
            if (start == 0L)
                return;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms < ThresholdMs)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastLog.TryGetValue(stage, out var last) && now - last < RepeatSeconds)
                return;
            _lastLog[stage] = now;
            CoopPlugin.Log.LogWarning($"[perf] {stage} took {ms:F1} ms");
        }

        public static void End(string stagePrefix, Net.MsgType type, long start)
        {
            if (start == 0L)
                return;
            End(stagePrefix + type, start);
        }

        // TCP receive/decode and keepalive/encode can run off the Unity thread.  Keep those
        // measurements out of the main-thread-only _lastLog dictionary and publish them from
        // FlushThreadMetrics, which is called by CoopCore.Update.
        public static long StartThreadMetric()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void EndThreadMetric(string stage, long start)
        {
            if (start == 0L)
                return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            var metric = _threadMetrics.GetOrAdd(stage, _ => new ThreadMetric());
            System.Threading.Interlocked.Increment(ref metric.Count);
            System.Threading.Interlocked.Add(ref metric.TotalTicks, elapsed);
            long previous;
            do
            {
                previous = System.Threading.Interlocked.Read(ref metric.MaxTicks);
                if (previous >= elapsed)
                    break;
            } while (System.Threading.Interlocked.CompareExchange(ref metric.MaxTicks, elapsed, previous) != previous);
        }

        public static void EndThreadMetric(string stagePrefix, Net.MsgType type, long start)
        {
            if (start == 0L)
                return;
            EndThreadMetric(stagePrefix + type, start);
        }

        public static void FlushThreadMetrics()
        {
            if (!Enabled)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var pair in _threadMetrics)
            {
                var metric = pair.Value;
                long count = System.Threading.Interlocked.Exchange(ref metric.Count, 0L);
                long total = System.Threading.Interlocked.Exchange(ref metric.TotalTicks, 0L);
                long max = System.Threading.Interlocked.Exchange(ref metric.MaxTicks, 0L);
                if (count == 0)
                    continue;
                double maxMs = max * 1000.0 / Stopwatch.Frequency;
                if (maxMs < ThresholdMs || (_lastLog.TryGetValue(pair.Key, out var last)
                    && now - last < RepeatSeconds))
                    continue;
                _lastLog[pair.Key] = now;
                double averageMs = total * 1000.0 / Stopwatch.Frequency / count;
                CoopPlugin.Log.LogWarning($"[perf] {pair.Key} {count} call(s), avg {averageMs:F1} ms, max {maxMs:F1} ms");
            }
        }
    }
}
