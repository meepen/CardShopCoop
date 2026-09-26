using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using CardShopCoop.Net.Connection;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Owns the dedicated network thread for a transport chain (bearer -> KCP -> lag).
    ///
    /// Producers may call <see cref="Send"/>/<see cref="Broadcast"/> from any thread; the inner
    /// transport only queues an owned frame and this pump is signalled, so an available message
    /// never waits for a tick. The pump thread drains marshalled lifecycle work, advances the
    /// inner transport, then waits on the signal only until the next KCP tick deadline. Calls
    /// that must observe transport state (Start/Stop/Dispose/ActivateMessageIds) are marshalled
    /// onto the pump thread, keeping bearer and KCP state single-threaded.
    /// </summary>
    public sealed class NetworkPump : ICoopTransport, ICoopHandshakeTransport, ICoopStartable
    {
        // Upper bound on how long the pump sleeps when no producer signals it. KCP
        // retransmission and pull-only bearers (Steam) rely on this cadence; every producer
        // path signals to cut the sleep short, so idle latency is bounded, never added.
        private const int TickMilliseconds = 10;
        private const int ShutdownJoinMilliseconds = 5000;

        private readonly ICoopTransport _inner;
        private readonly ConcurrentQueue<Action> _control = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);

        private Thread _thread;
        private int _pumpThreadId;
        private int _wakePending;
        private int _stopRequested;
        private int _disposeRequested;
        private int _disposed;
        private int _innerStopped;
        private int _innerDisposed;
        private Exception _startError;

        private NetworkPump(ICoopTransport inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Wraps a freshly created transport chain in the network pump.</summary>
        public static ICoopTransport Wrap(ICoopTransport inner)
        {
            if (inner == null || inner is NetworkPump)
            {
                return inner;
            }

            return new NetworkPump(inner);
        }

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(NetworkPump));
            }
            if (_thread != null)
            {
                if (_startError != null)
                {
                    throw new InvalidOperationException(
                        "network pump failed to start the transport", _startError);
                }
                return;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "CardShopCoop.Network",
            };
            _thread.Start();

            _started.Wait();
            if (_startError != null)
            {
                throw new InvalidOperationException(
                    "network pump failed to start the transport", _startError);
            }
        }

        private void Run()
        {
            _pumpThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                if (_inner is ICoopStartable startable)
                {
                    startable.Start();
                }
                else
                {
                    throw new InvalidOperationException("The wrapped transport cannot be started.");
                }
            }
            catch (Exception error)
            {
                _startError = error;
                try
                {
                    DisposeInner();
                }
                catch (Exception disposeError)
                {
                    CoopPlugin.Log?.LogError(
                        "network pump startup cleanup failed: " + disposeError);
                }
                _started.Set();
                return;
            }

            _started.Set();

            while (Volatile.Read(ref _stopRequested) == 0)
            {
                DrainControl();
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    break;
                }

                try
                {
                    _inner.PumpNetworkThread();
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogError("network pump step failed: " + error);
                }

                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    break;
                }

                Interlocked.Exchange(ref _wakePending, 0);
                _wake.Wait(TickMilliseconds);
            }

            // The teardown runs on the pump thread so thread-affine cleanup executes in place.
            try
            {
                StopInner();
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("network pump stop failed: " + error);
            }

            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                try
                {
                    DisposeInner();
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogError("network pump dispose failed: " + error);
                }
            }
        }

        private void DrainControl()
        {
            while (_control.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogError("network pump control action failed: " + error);
                }
            }
        }

        private void Signal()
        {
            if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                _wake.Release();
            }
        }

        private bool IsPumpThread()
        {
            return _pumpThreadId != 0
                && _pumpThreadId == Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Runs <paramref name="action"/> on the pump thread. Inline when already on it,
        /// otherwise queued and awaited so ordering and failures reach the caller.</summary>
        private void InvokeOnPump(Action action, string name)
        {
            if (IsPumpThread() || _thread == null)
            {
                action();
                return;
            }

            using (var completed = new ManualResetEventSlim(false))
            {
                Exception failure = null;
                _control.Enqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        completed.Set();
                    }
                });
                Signal();

                if (!completed.Wait(ShutdownJoinMilliseconds))
                {
                    throw new TimeoutException("network pump did not complete " + name);
                }
                if (failure != null)
                {
                    throw new InvalidOperationException("network pump " + name + " failed", failure);
                }
            }
        }

        private void StopInner()
        {
            if (Interlocked.Exchange(ref _innerStopped, 1) == 0)
            {
                _inner.Stop();
            }
        }

        private void DisposeInner()
        {
            if (Interlocked.Exchange(ref _innerDisposed, 1) == 0)
            {
                _inner.Dispose();
            }
        }

        public bool TryDequeueIncoming(out InMsg message)
        {
            return _inner.TryDequeueIncoming(out message);
        }

        public ConcurrentQueue<ConnectionEvent> Disconnects => _inner.Disconnects;

        public event Action<PeerConnection> PeerConnected
        {
            add
            {
                _inner.PeerConnected += value;
            }
            remove
            {
                _inner.PeerConnected -= value;
            }
        }

        public int ConnectionCount => _inner.ConnectionCount;

        public double TimeoutSeconds => _inner.TimeoutSeconds;

        public IReadOnlyList<PeerConnection> Connections => _inner.Connections;

        public double SecondsSinceLastRecv(PeerConnection connection)
            => _inner.SecondsSinceLastRecv(connection);

        public void Send(PeerConnection connection, INetMessage message)
        {
            _inner.Send(connection, message);
            Signal();
        }

        public void Broadcast(INetMessage message)
        {
            _inner.Broadcast(message);
            Signal();
        }

        void ICoopHandshakeTransport.SendHandshake(PeerConnection connection, INetMessage message)
        {
            if (_inner is not ICoopHandshakeTransport handshake)
            {
                throw new InvalidOperationException("The wrapped transport has no handshake seam");
            }

            handshake.SendHandshake(connection, message);
            Signal();
        }

        public void ActivateMessageIds(PeerConnection connection)
        {
            InvokeOnPump(() => _inner.ActivateMessageIds(connection), "message-id activation");
        }

        public void Kick(PeerConnection connection, DisconnectInfo info = null)
        {
            _inner.Kick(connection, info);
            Signal();
        }

        public void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null)
        {
            _inner.GracefulDisconnect(connection, info);
            Signal();
        }

        public void PumpNetworkThread()
        {
            _inner.PumpNetworkThread();
        }

        public void Stop()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_thread == null)
            {
                StopInner();
                return;
            }

            InvokeOnPump(StopInner, "stop");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_thread == null)
            {
                DisposeInner();
                return;
            }

            if (IsPumpThread())
            {
                // Invoked from the pump itself: the loop cannot join itself, so tear down inline.
                Volatile.Write(ref _disposeRequested, 1);
                Volatile.Write(ref _stopRequested, 1);
                StopInner();
                DisposeInner();
                return;
            }

            Volatile.Write(ref _disposeRequested, 1);
            Volatile.Write(ref _stopRequested, 1);
            Signal();
            _thread.Join(ShutdownJoinMilliseconds);
        }
    }
}
