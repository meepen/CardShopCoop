using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CardShopCoop.Net.Connection;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using CardShopCoop.Api;

namespace CardShopCoop.Net.Networking
{
    /// <summary>
    /// Presents several backend transports to Core as one, so a host can be reachable on more than
    /// one at once (Steam friends and LAN friends in the same shop). It is a bare composite: it is
    /// the thing a single <see cref="NetworkPump"/> is wrapped around, and its pump step advances
    /// every child and funnels their disconnect queues into one. Children must NOT pump themselves.
    ///
    /// ID BANDS are what make routing possible. Each child allocates its peers' PeerConnection.Id
    /// from its own disjoint band (<see cref="BandSize"/> wide, starting at the backend's
    /// <c>ConnectionIdBase</c>), so <see cref="Send"/>/<see cref="Kick"/> and friends can pick the
    /// child that owns an id without any per-child bookkeeping.
    /// </summary>
    internal sealed class CoopTransportGroup : ICoopTransport, ICoopStartable,
        ICoopHandshakeTransport, ICoopRelayTransport, IWireFrameTransport
    {
        /// <summary>Width of one backend's id band. Wide enough that no realistic session fills
        /// it, small enough that plenty of backends fit in the int range.</summary>
        public const int BandSize = NetworkingBands.Size;

        private sealed class Child
        {
            internal ICoopTransport Transport;
            internal int Base;
            internal int Limit;
            internal Action<PeerConnection> OnPeerConnected;
            internal bool Started;
        }

        private readonly object _gate = new();
        private readonly List<Child> _children = new();
        private readonly ConcurrentQueue<ConnectionEvent> _disconnects = new();
        /// <summary>Children removed from the live set but not yet stopped/disposed. Their teardown
        /// runs on the PUMP thread (see <see cref="PumpNetworkThread"/>), never on the caller - the
        /// UI/main thread - because the child is being pumped there; a direct Stop/Dispose from the
        /// main thread races the pump.</summary>
        private readonly List<Child> _pendingTeardown = new();

        public event Action<PeerConnection> PeerConnected;

        /// <summary>Adds a child transport and the base of the id band it allocates from. The child
        /// must already have that band set on its session manager (see
        /// <see cref="Kcp.KcpSessionManager.SetConnectionIdBand"/>).</summary>
        public void Add(ICoopTransport transport, int baseId)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            var child = new Child
            {
                Transport = transport,
                Base = baseId,
                Limit = baseId + BandSize,
            };
            child.OnPeerConnected = connection => PeerConnected?.Invoke(connection);
            transport.PeerConnected += child.OnPeerConnected;

            lock (_gate)
            {
                _children.Add(child);
            }
        }

        /// <summary>Removes a child (its backend disconnected) and forgets its band. Its transport is
        /// queued for teardown on the pump thread; the caller must NOT stop it itself.</summary>
        public void Remove(ICoopTransport transport)
        {
            Child removed = null;
            lock (_gate)
            {
                for (var i = 0; i < _children.Count; i++)
                {
                    if (ReferenceEquals(_children[i].Transport, transport))
                    {
                        removed = _children[i];
                        _children.RemoveAt(i);
                        _pendingTeardown.Add(removed);
                        break;
                    }
                }
            }

            if (removed != null)
            {
                removed.Transport.PeerConnected -= removed.OnPeerConnected;
            }
        }

        /// <summary>Stops and disposes one child on the pump thread. Idempotent enough for teardown:
        /// Stop/Dispose on a KCP transport are guarded internally.</summary>
        private void TearDownChild(Child child, bool dispose)
        {
            try
            {
                child.Transport.Stop();
            }
            catch (Exception e) { CoopLog.Swallow(e); }

            // Stop() flushes the child's terminal events into its OWN queue. Funnel them into the
            // group's here, while the transport still answers: the child has left _children, so the
            // pump loop no longer drains it, and without this its peers' OnClientDisconnected hooks
            // would never run.
            while (child.Transport.TryDequeueDisconnect(out var disconnect))
            {
                _disconnects.Enqueue(disconnect);
            }

            if (dispose)
            {
                try
                {
                    child.Transport.Dispose();
                }
                catch (Exception e) { CoopLog.Swallow(e); }
            }
        }

        private Child[] DrainPendingTeardown()
        {
            lock (_gate)
            {
                if (_pendingTeardown.Count == 0)
                {
                    return Array.Empty<Child>();
                }

                var drained = _pendingTeardown.ToArray();
                _pendingTeardown.Clear();
                return drained;
            }
        }

        private Child[] Snapshot()
        {
            lock (_gate)
            {
                return _children.ToArray();
            }
        }

        private Child Route(PeerConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            var children = Snapshot();
            foreach (var child in children)
            {
                if (connection.Id >= child.Base && connection.Id < child.Limit)
                {
                    return child;
                }
            }

            // A CLIENT has a single connection whose id is 1 ("1 is always the host" from a
            // client's view) - outside every band, because only a HOST allocates from bands.
            // With exactly one child there is nothing to disambiguate.
            if (connection.Id == 1 && children.Length == 1)
            {
                return children[0];
            }

            return null;
        }

        // ------------------------------------------------------------------ ICoopStartable

        public void Start()
        {
            foreach (var child in Snapshot())
            {
                StartChild(child);
            }
        }

        /// <summary>Starts one child ON THE PUMP THREAD. Children are added by the UI thread when a
        /// second backend joins, so this is called from the pump step too - the session owns the
        /// pump, and a transport's lifecycle must not run on the Unity thread.</summary>
        private static void StartChild(Child child)
        {
            if (child.Started)
            {
                return;
            }

            child.Started = true;
            if (child.Transport is ICoopStartable startable)
            {
                startable.Start();
            }
        }

        // ------------------------------------------------------------------ ICoopTransport

        public bool TryDequeueIncoming(out InMsg message)
        {
            foreach (var child in Snapshot())
            {
                if (child.Transport.TryDequeueIncoming(out message))
                {
                    return true;
                }
            }

            message = default;
            return false;
        }

        public bool TryDequeueWireFrame(out WireFrame frame)
        {
            foreach (var child in Snapshot())
            {
                if (child.Transport is IWireFrameTransport wire
                    && wire.TryDequeueWireFrame(out frame))
                {
                    return true;
                }
            }

            frame = null;
            return false;
        }

        public bool TryDequeueDisconnect(out ConnectionEvent disconnect) => _disconnects.TryDequeue(out disconnect);

        public IReadOnlyList<PeerConnection> Connections
        {
            get
            {
                var all = new List<PeerConnection>();
                foreach (var child in Snapshot())
                {
                    all.AddRange(child.Transport.Connections);
                }

                return all;
            }
        }

        public int ConnectionCount
        {
            get
            {
                var count = 0;
                foreach (var child in Snapshot())
                {
                    count += child.Transport.ConnectionCount;
                }

                return count;
            }
        }

        public void ActivateMessageIds(PeerConnection connection)
            => Route(connection)?.Transport.ActivateMessageIds(connection);

        public void Send(PeerConnection connection, INetMessage message)
            => Route(connection)?.Transport.Send(connection, message);

        public void Broadcast(INetMessage message)
        {
            foreach (var child in Snapshot())
            {
                child.Transport.Broadcast(message);
            }
        }

        public double SecondsSinceLastRecv(PeerConnection connection)
            => Route(connection)?.Transport.SecondsSinceLastRecv(connection) ?? 0d;

        public void Kick(PeerConnection connection, DisconnectInfo info = null)
            => Route(connection)?.Transport.Kick(connection, info);

        public void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null)
            => Route(connection)?.Transport.GracefulDisconnect(connection, info);

        public void Stop()
        {
            foreach (var child in Snapshot())
            {
                TearDownChild(child, dispose: false);
            }

            // A child removed but not yet drained by a pump pass must still be stopped here. The list
            // survives this call so Dispose can dispose the same children.
            Child[] pending;
            lock (_gate)
            {
                pending = _pendingTeardown.ToArray();
            }
            for (var i = 0; i < pending.Length; i++)
            {
                TearDownChild(pending[i], dispose: false);
            }
        }

        /// <summary>Advances every child (they are bare and do not pump themselves) and funnels
        /// each child's disconnect queue into the group's. Called by the ONE
        /// <see cref="NetworkPump"/> the group is wrapped in.</summary>
        public void PumpNetworkThread()
        {
            foreach (var child in Snapshot())
            {
                // A backend added after the group started is started here, on the pump thread.
                try
                {
                    StartChild(child);
                }
                catch (Exception e)
                {
                    CoopPlugin.Log?.LogError("transport group could not start a child: " + e);
                    continue;
                }

                try
                {
                    child.Transport.PumpNetworkThread();
                }
                catch (Exception e)
                {
                    CoopPlugin.Log?.LogError("transport group pump step failed: " + e);
                }

                while (child.Transport.TryDequeueDisconnect(out var disconnect))
                {
                    _disconnects.Enqueue(disconnect);
                }
            }

            // Tear down any child whose backend has gone, on THIS thread (the pump), never on the
            // caller. A child's Stop/Dispose races its own pump step if run from the main thread.
            var teardown = DrainPendingTeardown();
            for (var i = 0; i < teardown.Length; i++)
            {
                TearDownChild(teardown[i], dispose: true);
            }
        }

        public double TimeoutSeconds
        {
            get
            {
                var max = 0d;
                foreach (var child in Snapshot())
                {
                    if (child.Transport.TimeoutSeconds > max)
                    {
                        max = child.Transport.TimeoutSeconds;
                    }
                }

                return max;
            }
        }

        public void Dispose()
        {
            Child[] children;
            Child[] pending;
            lock (_gate)
            {
                children = _children.ToArray();
                pending = _pendingTeardown.ToArray();
                _children.Clear();
                _pendingTeardown.Clear();
            }

            for (var i = 0; i < children.Length; i++)
            {
                children[i].Transport.PeerConnected -= children[i].OnPeerConnected;
                TearDownChild(children[i], dispose: true);
            }

            // Children whose backend left but whose teardown the pump never got to.
            for (var i = 0; i < pending.Length; i++)
            {
                pending[i].Transport.PeerConnected -= pending[i].OnPeerConnected;
                TearDownChild(pending[i], dispose: true);
            }
        }

        // ------------------------------------------------------------------ control-plane seams

        void ICoopHandshakeTransport.SendHandshake(PeerConnection connection, INetMessage message)
        {
            var child = Route(connection);
            if (child?.Transport is ICoopHandshakeTransport handshake)
            {
                handshake.SendHandshake(connection, message);
            }
        }

        void ICoopRelayTransport.Relay(INetMessage message, int exceptConnectionId)
        {
            foreach (var child in Snapshot())
            {
                if (child.Transport is ICoopRelayTransport relay)
                {
                    relay.Relay(message, exceptConnectionId);
                }
            }
        }
    }
}
