using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Opt-in hitch diagnostics. Explicit co-op stages are measured with a monotonic clock and
    /// exposed as Unity profiler samples. Built-in ProfilerRecorder counters add whole-frame,
    /// allocation, rendering, and physics context when the retail player exposes those markers.
    /// </summary>
    internal static class PerfProbe
    {
        private const double StageThresholdMs = 5.0;
        private const double HitchThresholdMs = 33.0;
        private const double RepeatSeconds = 2.0;
        private const double AverageIntervalSeconds = 5.0;
        private const int TopStageCount = 8;

        private static readonly Dictionary<string, double> LastStageLog = new();
        private static readonly Dictionary<string, long> AverageStageTicks = new();
        private static readonly FrameSlot[] FrameSlots = { new FrameSlot(), new FrameSlot() };
        private static readonly ConcurrentDictionary<string, ThreadMetric> ThreadMetrics = new();
        private static readonly ConcurrentDictionary<string, HandlerMetric> HandlerMetrics = new();
        private static readonly ConcurrentDictionary<string, QueueMetric> QueueMetrics = new();
        private static readonly List<RecorderMetric> Recorders = new();

        private static bool _recordersStarted;
        private static double _lastHitchLog;
        private static int _suppressedHitches;
        private static int _averageFrames;
        private static double _averageWindowStart;

        private struct FrameStage
        {
            internal long TotalTicks;
            internal long MaxTicks;
            internal int Calls;
        }

        private sealed class FrameSlot
        {
            internal int Frame = -1;
            internal double ElapsedMs;
            internal readonly Dictionary<string, FrameStage> Stages = new();

            internal void Reset(int frame)
            {
                Frame = frame;
                ElapsedMs = Time.unscaledDeltaTime * 1000d;
                Stages.Clear();
            }

            internal void Clear()
            {
                Frame = -1;
                ElapsedMs = 0d;
                Stages.Clear();
            }
        }

        private sealed class ThreadMetric
        {
            public long Count;
            public long TotalTicks;
            public long MaxTicks;
        }

        private sealed class HandlerMetric
        {
            public long Count;
            public long TotalTicks;
            public long MaxTicks;
            public long TotalCost;
            public long Failures;
        }

        private sealed class QueueMetric
        {
            public long Samples;
            public int LastDepth;
            public int PeakDepth;
            public int Capacity;
        }

        private sealed class RecorderMetric : IDisposable
        {
            internal readonly string Name;
            internal readonly bool Nanoseconds;
            internal readonly bool Bytes;
            internal ProfilerRecorder Recorder;

            internal RecorderMetric(string name, ProfilerCategory category, string marker,
                bool nanoseconds = false, bool bytes = false)
            {
                Name = name;
                Nanoseconds = nanoseconds;
                Bytes = bytes;
                Recorder = ProfilerRecorder.StartNew(category, marker, 1);
            }

            internal bool Valid => Recorder.Valid;

            public void Dispose()
            {
                Recorder.Dispose();
            }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly string _stage;
            private readonly long _start;
            private readonly bool _sample;

            internal Scope(string stage)
            {
                _stage = stage;
                _start = Enabled ? Stopwatch.GetTimestamp() : 0L;
                _sample = _start != 0L;
                if (_sample)
                {
                    Profiler.BeginSample(stage);
                }
            }

            public void Dispose()
            {
                if (!_sample)
                {
                    return;
                }

                try
                {
                    Profiler.EndSample();
                }
                finally
                {
                    End(_stage, _start);
                }
            }
        }

        public static bool Enabled
        {
            get
            {
                try
                {
                    return CoopPlugin.PerfDebug != null && CoopPlugin.PerfDebug.Value;
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                    return false;
                }
            }
        }

        /// <summary>Call once at the beginning of the main Unity Update.</summary>
        public static void BeginFrame()
        {
            if (!Enabled)
            {
                if (_recordersStarted)
                {
                    StopRecorders();
                    ClearFrameSlots();
                    ThreadMetrics.Clear();
                    HandlerMetrics.Clear();
                    QueueMetrics.Clear();
                    LastStageLog.Clear();
                    AverageStageTicks.Clear();
                    _lastHitchLog = 0d;
                    _suppressedHitches = 0;
                    _averageFrames = 0;
                    _averageWindowStart = 0d;
                }
                return;
            }

            EnsureRecorders();
            AccumulateAverage();
            var frame = Time.frameCount;
            var previousFrame = frame - 1;
            var previous = FindFrameSlot(previousFrame);
            if (previous != null)
            {
                ReportPreviousFrame(previous.Frame, previous.ElapsedMs, previous.Stages);
                previous.Clear();
            }
        }

        public static Scope Sample(string stage)
        {
            if (string.IsNullOrEmpty(stage))
            {
                throw new ArgumentException("A performance stage name is required.", nameof(stage));
            }
            return new Scope(stage);
        }

        /// <summary>Run an action and expose/log its stage while diagnostics are enabled.</summary>
        public static void Measure(string stage, Action action)
        {
            if (!Enabled)
            {
                action();
                return;
            }

            using (Sample(stage))
            {
                action();
            }
        }

        public static void Reset()
        {
            LastStageLog.Clear();
            ClearFrameSlots();
            ThreadMetrics.Clear();
            HandlerMetrics.Clear();
            QueueMetrics.Clear();
            _lastHitchLog = 0d;
            _suppressedHitches = 0;
            AverageStageTicks.Clear();
            _averageFrames = 0;
            _averageWindowStart = 0d;
            StopRecorders();
            UpdateProfiler.Reset();
        }

        /// <summary>Allocation-free timing token for legacy call sites.</summary>
        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(string stage, long start)
        {
            if (start == 0L)
            {
                return;
            }

            var elapsed = Stopwatch.GetTimestamp() - start;
            RecordFrameStage(stage, elapsed);
            var ms = TicksToMilliseconds(elapsed);
            if (ms < StageThresholdMs)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (LastStageLog.TryGetValue(stage, out var last) && now - last < RepeatSeconds)
            {
                return;
            }

            LastStageLog[stage] = now;
            CoopPlugin.Log.LogWarning($"[perf] {stage} took {ms:F1} ms");
        }

        public static void End(string stagePrefix, Type type, long start)
        {
            if (start != 0L)
            {
                End(stagePrefix + (type?.Name ?? "unknown"), start);
            }
        }

        public static long StartThreadMetric()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void EndThreadMetric(string stage, long start)
        {
            if (start == 0L)
            {
                return;
            }

            var elapsed = Stopwatch.GetTimestamp() - start;
            var metric = ThreadMetrics.GetOrAdd(stage, _ => new ThreadMetric());
            System.Threading.Interlocked.Increment(ref metric.Count);
            System.Threading.Interlocked.Add(ref metric.TotalTicks, elapsed);
            long previous;
            do
            {
                previous = System.Threading.Interlocked.Read(ref metric.MaxTicks);
                if (previous >= elapsed)
                {
                    break;
                }
            } while (System.Threading.Interlocked.CompareExchange(
                ref metric.MaxTicks, elapsed, previous) != previous);
        }

        public static void EndThreadMetric(string stagePrefix, Type type, long start)
        {
            if (start != 0L)
            {
                EndThreadMetric(stagePrefix + (type?.Name ?? "unknown"), start);
            }
        }

        internal static long StartHandlerMetric()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        internal static void EndHandlerMetric(Type type, int cost, long start, bool failed)
        {
            if (start == 0L)
            {
                return;
            }

            var name = "net.handler." + (type?.FullName ?? "unknown");
            var metric = HandlerMetrics.GetOrAdd(name, _ => new HandlerMetric());
            var elapsed = Stopwatch.GetTimestamp() - start;
            System.Threading.Interlocked.Increment(ref metric.Count);
            System.Threading.Interlocked.Add(ref metric.TotalTicks, elapsed);
            System.Threading.Interlocked.Add(ref metric.TotalCost, Math.Max(1, cost));
            if (failed)
            {
                System.Threading.Interlocked.Increment(ref metric.Failures);
            }

            long previous;
            do
            {
                previous = System.Threading.Interlocked.Read(ref metric.MaxTicks);
                if (previous >= elapsed)
                {
                    break;
                }
            } while (System.Threading.Interlocked.CompareExchange(
                ref metric.MaxTicks, elapsed, previous) != previous);
        }

        /// <summary>Records a bounded queue's depth without adding a per-frame log line.
        /// Aggregated depth and peak values are emitted by <see cref="FlushThreadMetrics"/>.</summary>
        internal static void RecordQueueDepth(string name, int depth, int capacity)
        {
            if (!Enabled || string.IsNullOrEmpty(name))
            {
                return;
            }

            var metric = QueueMetrics.GetOrAdd(name, _ => new QueueMetric());
            depth = Math.Max(0, depth);
            capacity = Math.Max(0, capacity);
            System.Threading.Interlocked.Increment(ref metric.Samples);
            System.Threading.Interlocked.Exchange(ref metric.LastDepth, depth);
            System.Threading.Interlocked.Exchange(ref metric.Capacity, capacity);
            int previous;
            do
            {
                previous = System.Threading.Interlocked.CompareExchange(ref metric.PeakDepth, 0, 0);
                if (previous >= depth)
                {
                    break;
                }
            } while (System.Threading.Interlocked.CompareExchange(
                ref metric.PeakDepth, depth, previous) != previous);
        }

        public static void FlushThreadMetrics()
        {
            if (!Enabled)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            foreach (var pair in ThreadMetrics)
            {
                var metric = pair.Value;
                var count = System.Threading.Interlocked.Exchange(ref metric.Count, 0L);
                var total = System.Threading.Interlocked.Exchange(ref metric.TotalTicks, 0L);
                var max = System.Threading.Interlocked.Exchange(ref metric.MaxTicks, 0L);
                if (count == 0)
                {
                    continue;
                }

                var maxMs = TicksToMilliseconds(max);
                if (maxMs < StageThresholdMs || (LastStageLog.TryGetValue(pair.Key, out var last)
                    && now - last < RepeatSeconds))
                {
                    continue;
                }

                LastStageLog[pair.Key] = now;
                var averageMs = TicksToMilliseconds(total) / count;
                CoopPlugin.Log.LogWarning($"[perf] {pair.Key} {count} call(s), "
                    + $"avg {averageMs:F1} ms, max {maxMs:F1} ms");
            }

            FlushHandlerMetrics(now);
            FlushQueueMetrics(now);
        }

        private static void FlushHandlerMetrics(double now)
        {
            foreach (var pair in HandlerMetrics)
            {
                var metric = pair.Value;
                var count = System.Threading.Interlocked.Exchange(ref metric.Count, 0L);
                var total = System.Threading.Interlocked.Exchange(ref metric.TotalTicks, 0L);
                var max = System.Threading.Interlocked.Exchange(ref metric.MaxTicks, 0L);
                var totalCost = System.Threading.Interlocked.Exchange(ref metric.TotalCost, 0L);
                var failures = System.Threading.Interlocked.Exchange(ref metric.Failures, 0L);
                if (count == 0)
                {
                    continue;
                }

                var maxMs = TicksToMilliseconds(max);
                if (LastStageLog.TryGetValue(pair.Key, out var last)
                    && now - last < RepeatSeconds)
                {
                    continue;
                }

                LastStageLog[pair.Key] = now;
                var averageMs = TicksToMilliseconds(total) / count;
                var averageCost = (double)totalCost / count;
                var text = $"[perf] {pair.Key} {count} call(s), "
                    + $"avg {averageMs:F1} ms, max {maxMs:F1} ms, "
                    + $"avg-cost {averageCost:F1}, failures {failures}";
                if (failures != 0 || maxMs >= StageThresholdMs)
                {
                    CoopPlugin.Log.LogWarning(text);
                }
                else
                {
                    CoopPlugin.Log.LogDebug(text);
                }
            }
        }

        private static void FlushQueueMetrics(double now)
        {
            foreach (var pair in QueueMetrics)
            {
                var metric = pair.Value;
                var samples = System.Threading.Interlocked.Exchange(ref metric.Samples, 0L);
                var depth = System.Threading.Interlocked.Exchange(ref metric.LastDepth, 0);
                var peak = System.Threading.Interlocked.Exchange(ref metric.PeakDepth, 0);
                var capacity = System.Threading.Interlocked.CompareExchange(ref metric.Capacity, 0, 0);
                if (samples == 0)
                {
                    continue;
                }
                if (LastStageLog.TryGetValue("queue." + pair.Key, out var last)
                    && now - last < RepeatSeconds)
                {
                    continue;
                }

                LastStageLog["queue." + pair.Key] = now;
                CoopPlugin.Log.LogInfo($"[perf] queue {pair.Key}: depth {depth}, peak {peak}/"
                    + capacity + ", samples " + samples);
            }
        }

        private static void RecordFrameStage(string stage, long elapsed)
        {
            var frame = Time.frameCount;
            var stages = GetFrameSlot(frame).Stages;

            stages.TryGetValue(stage, out var value);
            value.TotalTicks += elapsed;
            value.Calls++;
            if (elapsed > value.MaxTicks)
            {
                value.MaxTicks = elapsed;
            }
            stages[stage] = value;

            AverageStageTicks.TryGetValue(stage, out var accumulated);
            AverageStageTicks[stage] = accumulated + elapsed;
        }

        /// <summary>Emits one averaged frame summary every <see cref="AverageIntervalSeconds"/>,
        /// so per-frame costs far below the 5 ms stage threshold stay visible. This is how a
        /// steady sub-millisecond drain that still breaks a VSync budget can be found.</summary>
        private static void AccumulateAverage()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (_averageWindowStart <= 0d)
            {
                _averageWindowStart = now;
            }

            _averageFrames++;
            var seconds = now - _averageWindowStart;
            if (seconds < AverageIntervalSeconds)
            {
                return;
            }

            var frames = Math.Max(1, _averageFrames);
            var stages = AverageStageTicks
                .OrderByDescending(pair => pair.Value)
                .Take(TopStageCount)
                .Select(pair => pair.Key + "="
                    + (TicksToMilliseconds(pair.Value) / frames).ToString("F2") + "ms")
                .ToArray();

            CoopPlugin.Log.LogInfo("[perf-avg] fps=" + (frames / seconds).ToString("F1")
                + " frame=" + (seconds * 1000d / frames).ToString("F2") + "ms"
                + " stages=[" + string.Join(",", stages) + "]");
            UpdateProfiler.Flush(seconds);

            AverageStageTicks.Clear();
            _averageFrames = 0;
            _averageWindowStart = now;
        }

        private static void EnsureRecorders()
        {
            if (_recordersStarted)
            {
                return;
            }

            _recordersStarted = true;
            TryAddRecorder("main", ProfilerCategory.Internal, "Main Thread", nanoseconds: true);
            TryAddRecorder("gc-alloc", ProfilerCategory.Memory, "GC Allocated In Frame", bytes: true);
            TryAddRecorder("gc-used", ProfilerCategory.Memory, "GC Used Memory", bytes: true);
            TryAddRecorder("draw-calls", ProfilerCategory.Render, "Draw Calls Count");
            TryAddRecorder("batches", ProfilerCategory.Render, "Batches Count");
            TryAddRecorder("physics", ProfilerCategory.Physics, "Physics.Processing", nanoseconds: true);
            UpdateProfiler.Install();

            var available = Recorders.Count == 0 ? "none"
                : string.Join(",", Recorders.Select(metric => metric.Name));
            CoopPlugin.Log.LogInfo("[perf] Unity hitch recorder enabled; available counters="
                + available + "; hitch-threshold=" + HitchThresholdMs.ToString("F0") + " ms; avg-window="
                + AverageIntervalSeconds.ToString("F0") + "s");
        }

        private static void TryAddRecorder(string name, ProfilerCategory category, string marker,
            bool nanoseconds = false, bool bytes = false)
        {
            try
            {
                var metric = new RecorderMetric(name, category, marker, nanoseconds, bytes);
                if (metric.Valid)
                {
                    Recorders.Add(metric);
                }
                else
                {
                    metric.Dispose();
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogDebug("[perf] Unity counter unavailable: " + marker
                    + " (" + error.Message + ")");
            }
        }

        private static void ReportPreviousFrame(int frame, double elapsedMs,
            IReadOnlyDictionary<string, FrameStage> frameStages)
        {
            var mainMs = ReadNanoseconds("main");
            var frameMs = mainMs > 0d ? mainMs : elapsedMs;
            if (frameMs < HitchThresholdMs)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (_lastHitchLog > 0d && now - _lastHitchLog < RepeatSeconds)
            {
                _suppressedHitches++;
                return;
            }

            var stages = frameStages
                .OrderByDescending(pair => pair.Value.TotalTicks)
                .Take(TopStageCount)
                .Select(pair => pair.Key + "="
                    + TicksToMilliseconds(pair.Value.TotalTicks).ToString("F1") + "ms"
                    + (pair.Value.Calls > 1 ? "x" + pair.Value.Calls : ""))
                .ToArray();

            var details = new List<string>
            {
                "frame=" + frame,
                "frame-time=" + frameMs.ToString("F1") + "ms",
            };
            if (mainMs > 0d && Math.Abs(mainMs - elapsedMs) > 0.1d)
            {
                details.Add("wall=" + elapsedMs.ToString("F1") + "ms");
            }
            AppendMetric(details, "gc-alloc");
            AppendMetric(details, "gc-used");
            AppendMetric(details, "draw-calls");
            AppendMetric(details, "batches");
            AppendMetric(details, "physics");
            if (stages.Length > 0)
            {
                details.Add("stages=[" + string.Join(",", stages) + "]");
            }
            if (_suppressedHitches > 0)
            {
                details.Add("suppressed=" + _suppressedHitches);
            }

            _lastHitchLog = now;
            _suppressedHitches = 0;
            CoopPlugin.Log.LogWarning("[perf-frame] " + string.Join(" ", details));
        }

        private static void AppendMetric(ICollection<string> details, string name)
        {
            var metric = Recorders.FirstOrDefault(candidate => candidate.Name == name);
            if (metric == null || !metric.Valid)
            {
                return;
            }

            var value = metric.Recorder.LastValue;
            if (metric.Nanoseconds)
            {
                details.Add(name + "=" + (value / 1000000d).ToString("F1") + "ms");
            }
            else if (metric.Bytes)
            {
                details.Add(name + "=" + FormatBytes(value));
            }
            else
            {
                details.Add(name + "=" + value);
            }
        }

        private static double ReadNanoseconds(string name)
        {
            var metric = Recorders.FirstOrDefault(candidate => candidate.Name == name);
            return metric == null || !metric.Valid ? 0d : metric.Recorder.LastValue / 1000000d;
        }

        private static string FormatBytes(long bytes)
        {
            if (Math.Abs(bytes) >= 1024L * 1024L)
            {
                return (bytes / (1024d * 1024d)).ToString("F1") + "MiB";
            }
            if (Math.Abs(bytes) >= 1024L)
            {
                return (bytes / 1024d).ToString("F1") + "KiB";
            }
            return bytes + "B";
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static FrameSlot FindFrameSlot(int frame)
        {
            for (var i = 0; i < FrameSlots.Length; i++)
            {
                if (FrameSlots[i].Frame == frame)
                {
                    return FrameSlots[i];
                }
            }
            return null;
        }

        private static FrameSlot GetFrameSlot(int frame)
        {
            var existing = FindFrameSlot(frame);
            if (existing != null)
            {
                return existing;
            }

            var slot = FrameSlots[0].Frame <= FrameSlots[1].Frame
                ? FrameSlots[0] : FrameSlots[1];
            slot.Reset(frame);
            return slot;
        }

        private static void ClearFrameSlots()
        {
            for (var i = 0; i < FrameSlots.Length; i++)
            {
                FrameSlots[i].Clear();
            }
        }

        private static void StopRecorders()
        {
            if (!_recordersStarted)
            {
                return;
            }

            for (var i = 0; i < Recorders.Count; i++)
            {
                Recorders[i].Dispose();
            }
            Recorders.Clear();
            _recordersStarted = false;
            UpdateProfiler.Uninstall();
        }
    }
}
