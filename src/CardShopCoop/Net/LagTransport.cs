using CardShopCoop.Net.Connection;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace CardShopCoop.Net
{
    /// <summary>
    /// TESTING DECORATOR around a real transport. Delays every message this process
    /// RECEIVES by CoopPlugin.ArtificialLagMs, plus a per-message random +/- of
    /// CoopPlugin.ArtificialJitterMs. It wraps both the LAN UDP/KCP and Steam
    /// KCP over Steam Networking Sockets transports, so a
    /// lag test behaves the same either way, and it reads the config live so the SETTINGS
    /// sliders take effect without restarting.
    ///
    /// Inbound-only by design: with the same value on both peers each hop adds one delay,
    /// so the perceived round trip is roughly twice the configured value. Frames are
    /// released in arrival order (FIFO), matching ordered KCP delivery: a frame whose
    /// release time is still ahead holds the ones behind it, exactly like a slow serial
    /// link. With both settings at 0 the decorator is a transparent pass-through with one
    /// queue hop per frame. Config values are clamped to 0-60s and the delay buffer has a
    /// hard frame cap, so a hand-edited config cannot throw or grow memory without bound.
    /// SecondsSinceLastRecv/TimeoutSeconds deliberately pass through to the inner transport:
    /// artificial lag must not trip the peer-silence timeout, so those timers follow real
    /// wire receipt rather than delayed delivery.
    /// </summary>
    public sealed class LagTransport : ICoopTransport, ICoopHandshakeTransport
    {
        private struct Pending
        {
            public long ReleaseAt; // Stopwatch timestamp
            public InMsg Message;
            public int Bytes;
        }

        private struct ReadyMessage
        {
            public InMsg Message;
            public int Bytes;
        }

        // Hand-edited configs can exceed the slider bounds; clamp to a sane ceiling and cap
        // the buffer so a pathological value can never grow memory without bound.
        private const int MaxDelayMs = 60000;
        private const int MaxBufferedFrames = 20000;
        private const long MaxBufferedBytes = 64L * 1024 * 1024;
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        private readonly ICoopTransport _inner;
        private readonly ConcurrentQueue<ReadyMessage> _incoming = new();
        private readonly object _incomingGate = new object();
        private readonly Queue<Pending> _delay = new();
        internal int DelayCount => _delay.Count;
        // The decorator deliberately knows only the transport contract, never Steam.
        internal int InnerReassemblyCount => 0;
        internal int InnerReliableOutboxCount => 0;
        internal int InnerTransientOutboxCount => 0;
        private readonly Random _random = new();
        private bool _overflowWarned;
        private long _delayBytes;
        private long _incomingBytes;

        private LagTransport(ICoopTransport inner)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }

            _inner = inner;
        }

        /// <summary>Wrap a freshly created transport. Every CoopCore.Net assignment goes
        /// through here so lag applies to LAN and Steam alike. The decorator is installed
        /// unconditionally, even when both settings are currently 0: the sliders are live,
        /// so the only way a mid-session change can take effect is for this wrapper to already
        /// be in the chain reading the config each PumpMainThread. With both values at 0 it
        /// behaves as a transparent pass-through.</summary>
        public static ICoopTransport Wrap(ICoopTransport inner)
        {
            if (inner == null || inner is LagTransport)
            {
                return inner;
            }

            return new LagTransport(inner);
        }

        public bool TryDequeueIncoming(out InMsg message)
        {
            lock (_incomingGate)
            {
                if (!_incoming.TryDequeue(out var ready))
                {
                    message = default(InMsg);
                    return false;
                }
                _incomingBytes -= ready.Bytes;
                if (_incomingBytes < 0)
                    _incomingBytes = 0;
                message = ready.Message;
                return true;
            }
        }

        public ConcurrentQueue<ConnectionEvent> Disconnects
        {
            get
            {
                return _inner.Disconnects;
            }
        }

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

        public int ConnectionCount
        {
            get
            {
                return _inner.ConnectionCount;
            }
        }

        public double TimeoutSeconds
        {
            get
            {
                return _inner.TimeoutSeconds;
            }
        }

        public void Send(PeerConnection connection, INetMessage message)
        {
            _inner.Send(connection, message);
        }

        public void Broadcast(INetMessage message)
        {
            _inner.Broadcast(message);
        }

        void ICoopHandshakeTransport.SendHandshake(PeerConnection connection, INetMessage message)
        {
            if (_inner is not ICoopHandshakeTransport handshake)
            {
                throw new InvalidOperationException("The wrapped transport has no handshake seam");
            }

            handshake.SendHandshake(connection, message);
        }

        public void ActivateMessageIds(PeerConnection connection)
        {
            _inner.ActivateMessageIds(connection);
        }

        public double SecondsSinceLastRecv(PeerConnection connection)
        {
            return _inner.SecondsSinceLastRecv(connection);
        }

        public IReadOnlyList<PeerConnection> Connections
        {
            get
            {
                return _inner.Connections;
            }
        }

        public void Kick(PeerConnection connection, DisconnectInfo info = null)
        {
            _inner.Kick(connection, info);
        }

        public void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null)
        {
            _inner.GracefulDisconnect(connection, info);
        }

        public void Stop()
        {
            _inner.Stop();
            lock (_incomingGate)
            {
                while (_incoming.TryDequeue(out _))
                {
                }
                _incomingBytes = 0;
            }
            _delay.Clear();
            _delayBytes = 0;
            _overflowWarned = false;
        }

        public void PumpMainThread()
        {
            // Both KCP transports do their sending and receiving here. The inner pump must
            // run first so this frame's
            // arrivals are considered for (possibly zero) delay before anything is released.
            _inner.PumpMainThread();

            var lag = ClampMs(CoopPlugin.ArtificialLagMs != null ? CoopPlugin.ArtificialLagMs.Value : 0);
            var jitter = ClampMs(CoopPlugin.ArtificialJitterMs != null ? CoopPlugin.ArtificialJitterMs.Value : 0);

            if (lag == 0 && jitter == 0)
            {
                // A live config change can leave frames in the delayed path. They are due
                // immediately now, and must be delivered before newly arrived frames.
                while (_delay.Count > 0)
                {
                    var pending = _delay.Dequeue();
                    _delayBytes = Math.Max(0, _delayBytes - pending.Bytes);
                    EnqueueReady(pending.Message, pending.Bytes);
                }

                _delayBytes = 0;
                _overflowWarned = false;

                while (_inner.TryDequeueIncoming(out var msg))
                {
                    EnqueueReceived(msg);
                }

                return;
            }

            var now = Stopwatch.GetTimestamp();

            while (_inner.TryDequeueIncoming(out var msg))
            {
                // Disconnect control is terminal, not gameplay traffic. Do not let the
                // artificial receive delay keep a peer alive after the inner transport has
                // already published its terminal event.
                if (msg.Message is Messages.DisconnectMessage)
                {
                    DropDelayedConnection(msg.Connection);
                    EnqueueReceived(msg);
                    continue;
                }
                if (_delay.Count >= MaxBufferedFrames || _delayBytes >= MaxBufferedBytes)
                {
                    // Safety valve: a huge delay must never let the buffer grow without
                    // bound. Release the oldest frame undelayed and warn once; newer frames
                    // keep their delay so the test stays as close to the setting as it can.
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        CoopPlugin.Log?.LogWarning("LagTransport: delay buffer reached "
                            + MaxBufferedFrames + " frames - releasing the oldest frames without delay");
                    }
                    var released = _delay.Dequeue();
                    EnqueueReady(released.Message, released.Bytes);
                    _delayBytes = Math.Max(0, _delayBytes - released.Bytes);
                }
                var extra = jitter > 0 ? _random.Next(-jitter, jitter + 1) : 0;
                var delayMs = lag + extra;
                if (delayMs < 0)
                {
                    delayMs = 0;
                }

                var messageBytes = EstimateBytes(msg);
                _delay.Enqueue(new Pending
                {
                    ReleaseAt = now + (long)(delayMs * TicksPerMs),
                    Message = msg,
                    Bytes = messageBytes,
                });
                _delayBytes += messageBytes;
            }

            while (_delay.Count > 0 && _delay.Peek().ReleaseAt <= now)
            {
                var released = _delay.Dequeue();
                EnqueueReady(released.Message, released.Bytes);
                _delayBytes = Math.Max(0, _delayBytes - released.Bytes);
            }
            if (_delay.Count < MaxBufferedFrames / 2 && _delayBytes < MaxBufferedBytes / 2)
            {
                _overflowWarned = false;
            }
        }

        private void EnqueueReceived(InMsg message)
        {
            EnqueueReady(message, EstimateBytes(message));
        }

        private void EnqueueReady(InMsg message, int bytes)
        {
            lock (_incomingGate)
            {
                var terminal = message.Message is Messages.DisconnectMessage
                    || message.Message is Messages.ByeMessage;
                while (terminal && _incoming.Count > 0
                    && (_incoming.Count >= MaxBufferedFrames
                        || _incomingBytes > MaxBufferedBytes - bytes))
                {
                    if (!_incoming.TryDequeue(out var evicted))
                        break;
                    _incomingBytes = Math.Max(0, _incomingBytes - evicted.Bytes);
                }

                if (_incoming.Count >= MaxBufferedFrames
                    || bytes < Msg.MinimumFrameSize
                    || _incomingBytes > MaxBufferedBytes - bytes)
                {
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        CoopPlugin.Log?.LogWarning(
                            "LagTransport: ready incoming queue reached its bounded admission budget");
                    }
                    return;
                }

                _incoming.Enqueue(new ReadyMessage { Message = message, Bytes = bytes });
                _incomingBytes += bytes;
            }
        }

        private void DropDelayedConnection(PeerConnection connection)
        {
            if (connection == null || _delay.Count == 0)
            {
                return;
            }

            var keep = new Queue<Pending>(_delay.Count);
            while (_delay.Count > 0)
            {
                var pending = _delay.Dequeue();
                if (pending.Message.Connection == null
                    || !ReferenceEquals(pending.Message.Connection, connection))
                {
                    keep.Enqueue(pending);
                    continue;
                }
                _delayBytes = Math.Max(0, _delayBytes - pending.Bytes);
            }
            while (keep.Count > 0)
            {
                _delay.Enqueue(keep.Dequeue());
            }
        }

        private static int ClampMs(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > MaxDelayMs ? MaxDelayMs : value;
        }

        private static int EstimateBytes(InMsg message)
        {
            return message.Message == null ? Msg.MinimumFrameSize : NetMessageCodec.Encode(message.Message).Length;
        }

        public void Dispose()
        {
            lock (_incomingGate)
            {
                while (_incoming.TryDequeue(out _))
                {
                }
                _incomingBytes = 0;
            }
            _delay.Clear();
            _delayBytes = 0;
            _inner.Dispose();
        }
    }
}
