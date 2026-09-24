using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.Presence
{
    public readonly struct PeerPresence
    {
        public Vector3 Position
        {
            get;
        }
        public TimeSpan Age
        {
            get;
        }
        /// <summary>Vanilla's advertised held-item slot and its held type IDs.</summary>
        public byte Hold
        {
            get;
        }
        public IReadOnlyList<int> HoldTypes
        {
            get;
        }
        public PeerPresence(Vector3 position, TimeSpan age, byte hold, IReadOnlyList<int> holdTypes)
        {
            Position = position;
            Age = age;
            Hold = hold;
            var count = holdTypes == null ? 0 : holdTypes.Count;
            var copy = new int[count];
            if (holdTypes != null)
            {
                for (var i = 0; i < copy.Length; i++)
                    copy[i] = holdTypes[i];
            }
            HoldTypes = copy;
        }
    }

    public interface IPeerPresence
    {
        bool TryGet(int connectionId, out PeerPresence presence);
        void RecordAuthenticatedPosition(PeerConnection connection, Vector3 position);
        void RecordAuthenticatedHold(PeerConnection connection, byte hold, IReadOnlyList<int> holdTypes);
        void Clear(int connectionId);
        void ClearAll();
    }

    /// <summary>Host-owned, receipt-time presence. It deliberately accepts no relay identity.</summary>
    public sealed class PeerPresenceProvider : IPeerPresence
    {
        private sealed class Entry
        {
            public Vector3 Position; public byte Hold; public int[] HoldTypes; public long Receipt;
        }
        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly object _gate = new object();
        private readonly long _expiryTicks;
        public PeerPresenceProvider(TimeSpan expiry)
        {
            if (expiry <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiry));
            _expiryTicks = (long)(expiry.TotalSeconds * Stopwatch.Frequency);
        }
        public bool TryGet(int connectionId, out PeerPresence presence)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(connectionId, out entry) || now - entry.Receipt >= _expiryTicks)
                {
                    _entries.Remove(connectionId);
                    presence = default(PeerPresence);
                    return false;
                }
                presence = new PeerPresence(entry.Position,
                    TimeSpan.FromSeconds((double)(now - entry.Receipt) / Stopwatch.Frequency), entry.Hold, entry.HoldTypes);
                return true;
            }
        }
        public void RecordAuthenticatedPosition(PeerConnection connection, Vector3 position)
        {
            if (connection == null || connection.State != ConnectionState.FullyJoined || !Finite(position))
                return;
            lock (_gate)
            {
                if (_entries.TryGetValue(connection.Id, out var entry))
                {
                    entry.Position = position;
                    entry.Receipt = Stopwatch.GetTimestamp();
                    return;
                }

                _entries[connection.Id] = new Entry { Position = position, Receipt = Stopwatch.GetTimestamp() };
            }
        }

        /// <summary>Records the advertised hold slot and its type IDs. The hold payload rides its
        /// own reliable message, so this preserves the last position and refreshes the receipt.</summary>
        public void RecordAuthenticatedHold(PeerConnection connection, byte hold, IReadOnlyList<int> holdTypes)
        {
            if (connection == null || connection.State != ConnectionState.FullyJoined)
                return;
            var copy = CopyHoldTypes(holdTypes);
            lock (_gate)
            {
                if (_entries.TryGetValue(connection.Id, out var entry))
                {
                    entry.Hold = hold;
                    entry.HoldTypes = copy;
                    entry.Receipt = Stopwatch.GetTimestamp();
                    return;
                }

                _entries[connection.Id] = new Entry { Hold = hold, HoldTypes = copy, Receipt = Stopwatch.GetTimestamp() };
            }
        }
        public void Clear(int connectionId)
        {
            lock (_gate)
            {
                _entries.Remove(connectionId);
            }
        }
        public void ClearAll()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }
        private static int[] CopyHoldTypes(IReadOnlyList<int> values)
        {
            var count = values == null ? 0 : values.Count;
            var copy = new int[count];
            for (var i = 0; i < count; i++)
                copy[i] = values[i];
            return copy;
        }
        private static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x)
            && !float.IsNaN(p.y) && !float.IsInfinity(p.y)
            && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
    }
}
