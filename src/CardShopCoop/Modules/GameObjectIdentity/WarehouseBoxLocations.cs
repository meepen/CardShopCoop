using System;
using System.Collections.Generic;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Identity-owned location registry for warehouse entries. A warehouse entry is either a live
    /// <see cref="InteractablePackagingBox"/> or a value stored in a
    /// <see cref="ShelfCompartment"/>; stored values have no live object and are never bound in
    /// <see cref="GameObjectIdentityCache"/>. This registry is the only place that maps a warehouse
    /// Guid to a live box or to a (shelf, compartment, slot) location, and back. It stores
    /// identity/location only - never item contents.
    /// </summary>
    internal static class WarehouseBoxLocations
    {
        private readonly struct LocationKey : IEquatable<LocationKey>
        {
            internal readonly int ShelfInstanceId;
            internal readonly int CompartmentIndex;
            internal readonly int Slot;

            internal LocationKey(WarehouseShelf shelf, int compartmentIndex, int slot)
            {
                ShelfInstanceId = shelf == null ? 0 : shelf.GetInstanceID();
                CompartmentIndex = compartmentIndex;
                Slot = slot;
            }

            public bool Equals(LocationKey other)
                => ShelfInstanceId == other.ShelfInstanceId
                    && CompartmentIndex == other.CompartmentIndex
                    && Slot == other.Slot;

            public override bool Equals(object obj) => obj is LocationKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = ShelfInstanceId;
                    hash = (hash * 397) ^ CompartmentIndex;
                    hash = (hash * 397) ^ Slot;
                    return hash;
                }
            }
        }

        private sealed class Entry
        {
            internal Guid Id;
            internal InteractablePackagingBox Live;
            internal WarehouseShelf Shelf;
            internal int CompartmentIndex = -1;
            internal int Slot = -1;
            internal long Sequence;
        }

        /// <summary>Resolved location of one warehouse id.</summary>
        internal readonly struct Location
        {
            internal readonly InteractablePackagingBox Live;
            internal readonly WarehouseShelf Shelf;
            internal readonly int CompartmentIndex;
            internal readonly int Slot;

            internal Location(InteractablePackagingBox live, WarehouseShelf shelf,
                int compartmentIndex, int slot)
            {
                Live = live;
                Shelf = shelf;
                CompartmentIndex = compartmentIndex;
                Slot = slot;
            }

            internal bool HasLiveBox => Live != null;

            internal bool HasStoredLocation => Shelf != null && CompartmentIndex >= 0;
        }

        private static readonly Dictionary<Guid, Entry> ById = new();
        private static readonly Dictionary<LocationKey, Guid> ByLocation = new();
        // Ordered ids for a compartment whose entries may have no live object (record backend).
        // The order mirrors the game's stored record list after the canonical sort.
        private static readonly Dictionary<long, List<Guid>> CompartmentOrder = new();
        private static long _nextSequence;

        internal static void Reset()
        {
            ById.Clear();
            ByLocation.Clear();
            CompartmentOrder.Clear();
            _nextSequence = 0;
        }

        /// <summary>Monotonic insertion order for tie-breaking the canonical record sort.</summary>
        internal static long NextSequence() => ++_nextSequence;

        internal static long SequenceOf(Guid id)
            => id != Guid.Empty && ById.TryGetValue(id, out var entry) ? entry.Sequence : 0L;

        /// <summary>Registers an entry as a live object. Binding the object in the identity cache
        /// stays with the caller/lifecycle owner; this registry only records the location.</summary>
        internal static void RegisterLive(Guid id, InteractablePackagingBox live, WarehouseShelf shelf,
            int compartmentIndex, int slot)
        {
            if (id == Guid.Empty || live == null)
            {
                return;
            }

            RemoveLocationKeys(id);
            var entry = GetOrCreate(id);
            entry.Live = live;
            entry.Shelf = shelf;
            entry.CompartmentIndex = compartmentIndex;
            entry.Slot = slot;
            if (shelf != null && compartmentIndex >= 0 && slot >= 0)
            {
                ByLocation[new LocationKey(shelf, compartmentIndex, slot)] = id;
            }
        }

        /// <summary>Registers one stored value. The entry stays valid with no live object.</summary>
        internal static void RegisterStored(Guid id, WarehouseShelf shelf, int compartmentIndex,
            int slot, long sequence)
        {
            if (id == Guid.Empty || shelf == null || compartmentIndex < 0)
            {
                return;
            }

            RemoveLocationKeys(id);
            var entry = GetOrCreate(id);
            entry.Live = null;
            entry.Shelf = shelf;
            entry.CompartmentIndex = compartmentIndex;
            entry.Slot = slot;
            if (sequence != 0)
            {
                entry.Sequence = sequence;
            }
            else if (entry.Sequence == 0)
            {
                entry.Sequence = NextSequence();
            }

            if (slot >= 0)
            {
                ByLocation[new LocationKey(shelf, compartmentIndex, slot)] = id;
            }
        }

        /// <summary>Appends a stored value at the end of a compartment's ordered list.</summary>
        internal static int AppendStored(Guid id, WarehouseShelf shelf, int compartmentIndex,
            long sequence)
        {
            if (id == Guid.Empty || shelf == null || compartmentIndex < 0)
            {
                return -1;
            }

            var key = CompartmentKey(shelf, compartmentIndex);
            if (!CompartmentOrder.TryGetValue(key, out var order))
            {
                CompartmentOrder[key] = order = new List<Guid>();
            }

            RemoveFromOrder(order, id);
            var slot = order.Count;
            order.Add(id);
            RegisterStored(id, shelf, compartmentIndex, slot, sequence);
            return slot;
        }

        /// <summary>Pops the last stored value of a compartment, returning its identity.</summary>
        internal static bool TryPopStoredTop(WarehouseShelf shelf, int compartmentIndex, out Guid id)
        {
            id = Guid.Empty;
            if (shelf == null || compartmentIndex < 0
                || !CompartmentOrder.TryGetValue(CompartmentKey(shelf, compartmentIndex),
                    out var order) || order.Count == 0)
            {
                return false;
            }

            var last = order.Count - 1;
            id = order[last];
            order.RemoveAt(last);
            RemoveLocationKeys(id);
            ByLocation.Remove(new LocationKey(shelf, compartmentIndex, last));
            if (ById.TryGetValue(id, out var entry))
            {
                entry.Live = null;
                entry.Shelf = null;
                entry.CompartmentIndex = -1;
                entry.Slot = -1;
            }

            return true;
        }

        /// <summary>Copies the ordered id list for a compartment (record-backed entries).</summary>
        internal static void CopyCompartmentOrder(WarehouseShelf shelf, int compartmentIndex,
            List<Guid> destination)
        {
            destination.Clear();
            if (shelf != null && compartmentIndex >= 0
                && CompartmentOrder.TryGetValue(CompartmentKey(shelf, compartmentIndex),
                    out var order))
            {
                destination.AddRange(order);
            }
        }

        /// <summary>Replaces a compartment's ordered identity list and location slots. Existing
        /// sequences are kept unless <paramref name="resetSequences"/> asks for wire order to be
        /// authoritative (a baseline/state-set); then the first wire entry is newest.</summary>
        internal static void SetCompartmentOrder(WarehouseShelf shelf, int compartmentIndex,
            IReadOnlyList<Guid> ids, bool resetSequences)
        {
            if (shelf == null || compartmentIndex < 0)
            {
                return;
            }

            RemoveCompartmentLocationKeys(shelf, compartmentIndex);
            var key = CompartmentKey(shelf, compartmentIndex);
            var order = new List<Guid>(ids?.Count ?? 0);
            if (!CompartmentOrder.TryGetValue(key, out var previous))
            {
                CompartmentOrder[key] = previous = new List<Guid>();
            }

            for (var i = 0; ids != null && i < ids.Count; i++)
            {
                var id = ids[i];
                if (id == Guid.Empty || order.Contains(id))
                {
                    continue;
                }

                order.Add(id);
                var entry = GetOrCreate(id);
                entry.Live = null;
                entry.Shelf = shelf;
                entry.CompartmentIndex = compartmentIndex;
                entry.Slot = order.Count - 1;
                if (resetSequences)
                {
                    // Negative, never zero (zero means "unassigned"). Descending order therefore
                    // preserves the wire order and is always older than a later local add.
                    entry.Sequence = -(long)i - 1;
                }
                else if (entry.Sequence == 0)
                {
                    // A record that predates the identity module (a save load) gets a monotonic
                    // insertion sequence in list order. Ties then sort newest-first, the record rule.
                    entry.Sequence = NextSequence();
                }

                ByLocation[new LocationKey(shelf, compartmentIndex, entry.Slot)] = id;
            }

            // Forget locations of entries that left this compartment.
            for (var i = 0; i < previous.Count; i++)
            {
                var id = previous[i];
                if (!order.Contains(id) && ById.TryGetValue(id, out var entry)
                    && ReferenceEquals(entry.Shelf, shelf)
                    && entry.CompartmentIndex == compartmentIndex)
                {
                    entry.Shelf = null;
                    entry.CompartmentIndex = -1;
                    entry.Slot = -1;
                }
            }

            previous.Clear();
            previous.AddRange(order);
        }

        internal static bool TryGetId(WarehouseShelf shelf, int compartmentIndex, int slot,
            out Guid id)
        {
            id = Guid.Empty;
            if (shelf == null || compartmentIndex < 0 || slot < 0)
            {
                return false;
            }

            // The record order is authoritative when it has an entry at this slot.
            if (CompartmentOrder.TryGetValue(CompartmentKey(shelf, compartmentIndex),
                    out var order) && slot < order.Count && order[slot] != Guid.Empty)
            {
                id = order[slot];
                return true;
            }

            // Fall back to a live representation's position; the registry location index is only
            // a hint. The game may have arranged it after the registry recorded the slot.
            ShelfCompartment compartment = null;
            var storage = shelf.GetStorageCompartmentList();
            for (var i = 0; storage != null && i < storage.Count; i++)
            {
                var candidate = storage[i]?.GetShelfCompartment();
                if (candidate != null && candidate.GetIndex() == compartmentIndex)
                {
                    compartment = candidate;
                    break;
                }
            }

            var boxes = compartment?.GetInteractablePackagingBoxList();
            if (boxes != null && slot < boxes.Count && boxes[slot] != null
                && GameObjectIdentityCache.TryGetBoundId(boxes[slot], out var bound)
                && bound.Kind == GameObjectKind.PackagingBox && bound.GuidValue != Guid.Empty)
            {
                id = bound.GuidValue;
                RegisterLive(id, boxes[slot], shelf, compartmentIndex, slot);
                return true;
            }

            if (ByLocation.TryGetValue(new LocationKey(shelf, compartmentIndex, slot), out id)
                && id != Guid.Empty)
            {
                return true;
            }

            return false;
        }

        internal static bool TryGetLocation(Guid id, out Location location)
        {
            location = default;
            if (id == Guid.Empty || !ById.TryGetValue(id, out var entry))
            {
                return false;
            }

            // Unity's overloaded null check reports a destroyed object as null; HasLiveBox then
            // falls through to the stored location (or reports an unknown entry).
            location = new Location(entry.Live, entry.Shelf, entry.CompartmentIndex, entry.Slot);
            return location.HasLiveBox || location.HasStoredLocation;
        }

        /// <summary>Removes every identity/location mapping for a warehouse id.</summary>
        internal static void Forget(Guid id)
        {
            if (id == Guid.Empty || !ById.TryGetValue(id, out var entry))
            {
                return;
            }

            RemoveLocationKeys(id);
            ById.Remove(id);
            if (entry.Shelf != null && entry.CompartmentIndex >= 0
                && CompartmentOrder.TryGetValue(CompartmentKey(entry.Shelf,
                    entry.CompartmentIndex), out var order))
            {
                RemoveFromOrder(order, id);
            }
        }

        /// <summary>Removes only the stored/live location, keeping the identity known. Used when a
        /// stored entry becomes a held live box (take) or is about to be re-registered.</summary>
        internal static void ClearLocation(Guid id)
        {
            if (id == Guid.Empty || !ById.TryGetValue(id, out var entry))
            {
                return;
            }

            RemoveLocationKeys(id);
            if (entry.Shelf != null && entry.CompartmentIndex >= 0
                && CompartmentOrder.TryGetValue(CompartmentKey(entry.Shelf,
                    entry.CompartmentIndex), out var order))
            {
                order.Remove(id);
            }

            entry.Shelf = null;
            entry.CompartmentIndex = -1;
            entry.Slot = -1;
        }

        internal static bool Contains(Guid id) => id != Guid.Empty && ById.ContainsKey(id);

        /// <summary>Grows/shrinks a compartment's ordered identity list to the current stored-record
        /// count, minting identities for records that predate the identity module (a save load).</summary>
        internal static List<Guid> EnsureCompartmentSize(WarehouseShelf shelf, int compartmentIndex,
            int count)
        {
            var ids = new List<Guid>();
            CopyCompartmentOrder(shelf, compartmentIndex, ids);
            while (ids.Count < count)
            {
                ids.Add(Guid.NewGuid());
            }

            if (ids.Count > count)
            {
                ids.RemoveRange(count, ids.Count - count);
            }

            SetCompartmentOrder(shelf, compartmentIndex, ids, false);
            return ids;
        }

        private static Entry GetOrCreate(Guid id)
        {
            if (!ById.TryGetValue(id, out var entry))
            {
                ById[id] = entry = new Entry { Id = id };
            }

            return entry;
        }

        private static void RemoveLocationKeys(Guid id)
        {
            if (!ById.TryGetValue(id, out var entry) || entry.Shelf == null
                || entry.CompartmentIndex < 0 || entry.Slot < 0)
            {
                return;
            }

            ByLocation.Remove(new LocationKey(entry.Shelf, entry.CompartmentIndex, entry.Slot));
        }

        private static void RemoveCompartmentLocationKeys(WarehouseShelf shelf, int compartmentIndex)
        {
            var stale = new List<LocationKey>();
            foreach (var pair in ByLocation)
            {
                if (pair.Key.ShelfInstanceId == shelf.GetInstanceID()
                    && pair.Key.CompartmentIndex == compartmentIndex)
                {
                    stale.Add(pair.Key);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                ByLocation.Remove(stale[i]);
            }
        }

        private static void RemoveFromOrder(List<Guid> order, Guid id)
        {
            for (var i = order.Count - 1; i >= 0; i--)
            {
                if (order[i] == id)
                {
                    order.RemoveAt(i);
                }
            }
        }

        private static long CompartmentKey(WarehouseShelf shelf, int compartmentIndex)
            => ((long)shelf.GetInstanceID() << 32) | (uint)compartmentIndex;
    }
}
