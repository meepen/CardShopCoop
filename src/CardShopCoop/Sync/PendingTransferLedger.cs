using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>One client-originated item transfer awaiting an authoritative result.
    /// TKey is the target identity (a box id or a shelf-compartment key).</summary>
    internal struct PendingTransfer<TKey>
    {
        public TKey Target;
        public int RequestedDelta;
        public int TransferType; // local EItemType id
        public int EscrowToken;  // > 0 for a take, 0 for an add
        public float SentAt;
    }

    /// <summary>
    /// Shared ledger for client-originated item transfers. Owns wire-sequence allocation,
    /// the 15s TTL prune, the take escrow token, and the pending-add reservation set, so a
    /// box and a shelf cannot drift apart in how they track the same protocol.
    /// </summary>
    internal sealed class PendingTransferLedger<TKey>
    {
        private const float TtlSeconds = 15f;
        private readonly Dictionary<uint, PendingTransfer<TKey>> _entries
            = new Dictionary<uint, PendingTransfer<TKey>>();
        private readonly HashSet<TKey> _pendingAdds = new HashSet<TKey>();
        private readonly List<uint> _prune = new List<uint>();
        private uint _seq;

        public int Count => _entries.Count;

        /// <summary>Raised for each entry the TTL prune removes, after the entry is out of
        /// the live table. Module-specific cleanup (e.g. a resync) belongs here.</summary>
        public Action<PendingTransfer<TKey>> Expired;

        /// <summary>Record a new transfer. A negative delta escrows the just-taken items out
        /// of the hand; a positive delta reserves the target against authoritative content
        /// overwrites until the result arrives. Returns the wire sequence to send.</summary>
        public uint Begin(TKey target, int requestedDelta, int transferType)
        {
            Prune();
            uint seq = ++_seq;
            if (seq == 0)
                seq = ++_seq; // 0 means "no transfer" on the wire
            int token = requestedDelta < 0
                ? HandEscrow.ReserveTake(transferType, -requestedDelta)
                : 0;
            if (requestedDelta < 0 && token == 0)
                CoopPlugin.Log.LogWarning($"PendingTransferLedger.Begin: take of {-requestedDelta} type {transferType} could not be reserved; rejection will not be reconciliable");
            if (requestedDelta > 0)
                _pendingAdds.Add(target);
            _entries[seq] = new PendingTransfer<TKey>
            {
                Target = target,
                RequestedDelta = requestedDelta,
                TransferType = transferType,
                EscrowToken = token,
                SentAt = Time.time,
            };
            return seq;
        }

        /// <summary>Remove and return the entry for a result. False when unknown. Releases
        /// the add reservation for the target when no other add still targets it.</summary>
        public bool TryResolve(uint seq, out PendingTransfer<TKey> entry)
        {
            if (!_entries.TryGetValue(seq, out entry))
                return false;
            _entries.Remove(seq);
            ReleaseAdd(entry.Target);
            return true;
        }

        /// <summary>True while an add for this target is unresolved; authoritative content
        /// must not overwrite it.</summary>
        public bool IsAddReserved(TKey target)
        {
            return _pendingAdds.Contains(target);
        }

        /// <summary>Resolve the escrow of a take from the host's accepted delta.</summary>
        public void ResolveTake(in PendingTransfer<TKey> entry, int acceptedDelta)
        {
            HandEscrow.ResolveTake(entry.EscrowToken, Mathf.Max(0, -acceptedDelta));
        }

        /// <summary>Drop the add reservation for a target once no live add targets it.</summary>
        public void ReleaseAdd(TKey target)
        {
            foreach (var entry in _entries.Values)
                if (EqualityComparer<TKey>.Default.Equals(entry.Target, target)
                    && entry.RequestedDelta > 0)
                    return;
            _pendingAdds.Remove(target);
        }

        /// <summary>Drop entries whose result never arrived. Remove each from the live table
        /// BEFORE resolving so ReleaseAdd cannot see the expiring entry as still pending.</summary>
        public void Prune()
        {
            if (_entries.Count == 0)
                return;
            _prune.Clear();
            foreach (var kv in _entries)
                if (Time.time - kv.Value.SentAt > TtlSeconds)
                    _prune.Add(kv.Key);
            for (int i = 0; i < _prune.Count; i++)
            {
                uint seq = _prune[i];
                if (!_entries.TryGetValue(seq, out var entry))
                    continue;
                _entries.Remove(seq);
                if (entry.RequestedDelta < 0)
                    HandEscrow.ExpireTake(entry.EscrowToken);
                else
                    ReleaseAdd(entry.Target);
                Expired?.Invoke(entry);
            }
        }

        /// <summary>Session/scene teardown: optimistically release takes and drop all
        /// bookkeeping. Does not discard a live item (HandEscrow owns that decision).</summary>
        public void Clear()
        {
            foreach (var entry in _entries.Values)
                if (entry.RequestedDelta < 0)
                    HandEscrow.ExpireTake(entry.EscrowToken);
            _entries.Clear();
            _pendingAdds.Clear();
            _seq = 0;
        }
    }
}
