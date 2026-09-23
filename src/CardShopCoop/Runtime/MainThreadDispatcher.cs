using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Bounded main-thread work queue for callbacks produced by network and worker threads.
    /// The epoch is part of every work item, so clearing a queue is not the only protection
    /// against a producer from an old session racing a shutdown or re-host.
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        public const int DefaultFrameBudget = 64;
        public const int DefaultPendingLimit = 4096;
        public const byte DefaultMaxRetries = 3;

        private sealed class Work
        {
            public readonly int Epoch;
            public readonly string Stage;
            public readonly Action Action;
            public readonly bool Retryable;
            public int Attempts;
            public int NotBeforeFrame;

            public Work(int epoch, string stage, Action action, bool retryable)
            {
                Epoch = epoch;
                Stage = stage;
                Action = action;
                Retryable = retryable;
            }
        }

        private readonly ConcurrentQueue<Work> _queue = new();
        private readonly object _lifecycleGate = new();
        private readonly int _pendingLimit;
        private readonly int _frameBudget;
        private readonly byte _maxRetries;
        private int _epoch;
        private int _pending;
        private bool _running;

        public MainThreadDispatcher(int frameBudget = DefaultFrameBudget,
            int pendingLimit = DefaultPendingLimit, byte maxRetries = DefaultMaxRetries)
        {
            if (frameBudget < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(frameBudget));
            }

            if (pendingLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingLimit));
            }

            _frameBudget = frameBudget;
            _pendingLimit = pendingLimit;
            _maxRetries = maxRetries;
        }

        /// <summary>Current session epoch. Capture it before handing work to another thread.</summary>
        public int Epoch => Volatile.Read(ref _epoch);

        public bool IsRunning
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _running;
                }
            }
        }

        public int PendingCount => Math.Max(0, Volatile.Read(ref _pending));

        /// <summary>
        /// Starts a fresh session epoch and removes any work left by the previous owner.
        /// A producer holding the previous epoch can still enqueue only stale work; Drain
        /// drops it without invoking the callback.
        /// </summary>
        public int Start()
        {
            lock (_lifecycleGate)
            {
                ClearUnsafe();
                _epoch = unchecked(_epoch + 1);
                _running = true;
                return _epoch;
            }
        }

        /// <summary>
        /// Stops dispatch, invalidates every outstanding epoch, and clears queued work.
        /// Calling Stop more than once is safe and still invalidates a new producer race.
        /// </summary>
        public void Stop()
        {
            lock (_lifecycleGate)
            {
                _running = false;
                _epoch = unchecked(_epoch + 1);
                ClearUnsafe();
            }
        }

        /// <summary>Removes queued work without changing the current epoch or running state.</summary>
        public void Clear()
        {
            lock (_lifecycleGate)
            {
                ClearUnsafe();
            }
        }

        /// <summary>
        /// Invalidates all captured producer tokens while preserving the current running state.
        /// Use this at a rehost boundary when the dispatcher owner remains alive.
        /// </summary>
        public int InvalidateEpoch()
        {
            lock (_lifecycleGate)
            {
                _epoch = unchecked(_epoch + 1);
                ClearUnsafe();
                return _epoch;
            }
        }

        /// <summary>
        /// Queues work for the current epoch. Returns false when stopped, when the epoch has
        /// changed, or when the bounded pending queue is full.
        /// </summary>
        public bool TryEnqueue(string stage, Action action, bool retryable = false)
        {
            return TryEnqueue(Epoch, stage, action, retryable);
        }

        /// <summary>Queues work for a previously captured epoch.</summary>
        public bool TryEnqueue(int epoch, string stage, Action action, bool retryable = false)
        {
            if (action == null)
            {
                return false;
            }

            lock (_lifecycleGate)
            {
                if (!_running || epoch != _epoch || _pending >= _pendingLimit)
                {
                    return false;
                }

                _queue.Enqueue(new Work(epoch, stage ?? "unspecified", action, retryable));
                _pending++;
                return true;
            }
        }

        /// <summary>Strict variant of TryEnqueue for callers that require admission.</summary>
        public void Enqueue(string stage, Action action, bool retryable = false)
        {
            if (!TryEnqueue(stage, action, retryable))
            {
                throw new InvalidOperationException("MainThreadDispatcher is stopped or full.");
            }
        }

        /// <summary>
        /// Executes at most the configured number of queued items in this frame. Delayed retries
        /// are examined once and requeued, so a future retry can never turn into an unbounded
        /// frame spin. The return value is the number of callbacks invoked.
        /// </summary>
        public int Drain()
        {
            return Drain(_frameBudget);
        }

        /// <summary>Executes at most <paramref name="budget"/> items, capped by the queue limit.</summary>
        public int Drain(int budget)
        {
            if (budget < 1)
            {
                return 0;
            }

            budget = Math.Min(budget, _frameBudget);
            var invoked = 0;
            for (var inspected = 0; inspected < budget && _queue.TryDequeue(out var work); inspected++)
            {
                Interlocked.Decrement(ref _pending);
                lock (_lifecycleGate)
                {
                    if (!_running || work.Epoch != _epoch)
                    {
                        continue;
                    }

                    if (Time.frameCount < work.NotBeforeFrame)
                    {
                        RequeueUnsafe(work);
                        continue;
                    }

                    invoked++;
                    RunUnsafe(work);
                }
            }

            return invoked;
        }

        private void RunUnsafe(Work work)
        {
            try
            {
                work.Action();
            }
            catch (Exception error)
            {
                work.Attempts++;
                CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' failed "
                    + $"(epoch {work.Epoch}, attempt {work.Attempts}): {error}");
                if (!work.Retryable || work.Attempts > _maxRetries)
                {
                    CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' abandoned after "
                        + $"{work.Attempts} attempt(s)");
                    return;
                }

                work.NotBeforeFrame = Time.frameCount + 1 + work.Attempts;
                if (_running && work.Epoch == _epoch && _pending < _pendingLimit)
                {
                    RequeueUnsafe(work);
                }
                else
                {
                    CoopPlugin.Log.LogWarning($"main-thread action '{work.Stage}' retry discarded "
                        + "because its session epoch is no longer active");
                }
            }
        }

        private void RequeueUnsafe(Work work)
        {
            _queue.Enqueue(work);
            _pending++;
        }

        private void ClearUnsafe()
        {
            while (_queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pending);
            }

            if (Volatile.Read(ref _pending) < 0)
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        }
    }
}
