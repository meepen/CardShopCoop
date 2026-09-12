using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>One client-originated item transfer awaiting an authoritative result.
    /// TKey is the target identity (a box id or a shelf-compartment key).</summary>
    internal struct PendingTransfer<TKey>
    {
        public uint Seq;
        public TKey Target;
        public int RequestedDelta;
        public int TransferType; // local EItemType id
        public int EscrowToken;  // > 0 for a take, 0 for an add
        public float FirstSentAt;
        public float LastSentAt;
        public int Attempts;
        public bool Escalated;
    }

    /// <summary>
    /// Shared ledger for client-originated item transfers. Owns wire-sequence allocation,
    /// retry-until-ack scheduling, the take escrow token, and the pending-add reservation set,
    /// so a box and a shelf cannot drift apart in how they track the same protocol. Nothing is
    /// ever resolved by a timeout: an entry leaves only on a real result or a session teardown.
    /// </summary>
    internal sealed class PendingTransferLedger<TKey>
    {
        public const float ResendIntervalSeconds = 1f;
        public const int EscalateAttempts = 15;
        // "Stop retransmitting" threshold, not an abandon threshold: the obligation is kept and
        // the entry resolves only on a real result or teardown. Kept comfortably above any delay
        // the transport can produce (LagTransport.MaxDelayMs = 60s) so a merely-delayed result
        // still resolves normally.
        public const int HardAttempts = 300;
        public const int MaxOutstanding = 256;
        private readonly Dictionary<uint, PendingTransfer<TKey>> _entries
            = new Dictionary<uint, PendingTransfer<TKey>>();
        private readonly HashSet<TKey> _pendingAdds = new HashSet<TKey>();
        private readonly HashSet<TKey> _pendingTakes = new HashSet<TKey>();
        private int _takeCount;
        private uint _seq;

        public int Count => _entries.Count;

        /// <summary>Raised for each entry the TTL prune removes, after the entry is out of
        /// the live table. Module-specific cleanup (e.g. a resync) belongs here.</summary>
        public Action<PendingTransfer<TKey>> Resend;
        public Action<PendingTransfer<TKey>> Escalate;

        /// <summary>Record a new transfer. A negative delta escrows the just-taken items out
        /// of the hand; a positive delta reserves the target against authoritative content
        /// overwrites until the result arrives. Returns the wire sequence to send.</summary>
        public uint Begin(TKey target, int requestedDelta, int transferType)
        {
            // Only takes need the cap: each one pins a real hand item. Adds carry no escrow
            // and must always be tracked, or a failed Begin would silently drop the local add.
            // Counting takes separately keeps an add backlog from starving new takes.
            if (requestedDelta < 0 && _takeCount >= MaxOutstanding)
            {
                CoopPlugin.Log.LogError($"PendingTransferLedger.Begin: take outstanding limit {MaxOutstanding} reached; refusing transfer");
                return 0;
            }
            uint seq = ++_seq;
            if (seq == 0)
                seq = ++_seq; // 0 means "no transfer" on the wire
            int token = requestedDelta < 0
                ? HandEscrow.ReserveTake(transferType, -requestedDelta)
                : 0;
            if (requestedDelta < 0 && token == 0)
            {
                CoopPlugin.Log.LogWarning($"PendingTransferLedger.Begin: take of {-requestedDelta} type {transferType} could not be reserved; refusing to track it");
                return 0;
            }
            if (requestedDelta > 0)
                _pendingAdds.Add(target);
            else if (requestedDelta < 0)
            {
                _takeCount++;
                _pendingTakes.Add(target);
            }
            _entries[seq] = new PendingTransfer<TKey>
            {
                Seq = seq,
                Target = target,
                RequestedDelta = requestedDelta,
                TransferType = transferType,
                EscrowToken = token,
                FirstSentAt = Time.time,
                LastSentAt = Time.time,
                Attempts = 1,
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
            if (entry.RequestedDelta < 0)
            {
                _takeCount--;
                ReleaseTake(entry.Target);
            }
            ReleaseAdd(entry.Target);
            return true;
        }

        /// <summary>True while an add for this target is unresolved; authoritative content
        /// must not overwrite it.</summary>
        public bool IsAddReserved(TKey target)
        {
            return _pendingAdds.Contains(target);
        }

        /// <summary>True while a take for this target is unresolved. Authoritative content
        /// must not repaint the container to its pre-take count while the taken item is still
        /// escrowed in the hand, or the item exists in both places.</summary>
        public bool IsTakeReserved(TKey target)
        {
            return _pendingTakes.Contains(target);
        }

        public bool TryGet(uint seq, out PendingTransfer<TKey> entry) => _entries.TryGetValue(seq, out entry);

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

        /// <summary>Drop the take reservation for a target once no live take targets it.</summary>
        public void ReleaseTake(TKey target)
        {
            foreach (var entry in _entries.Values)
                if (EqualityComparer<TKey>.Default.Equals(entry.Target, target)
                    && entry.RequestedDelta < 0)
                    return;
            _pendingTakes.Remove(target);
        }

        public void Tick()
        {
            if (_entries.Count == 0)
                return;
            float now = Time.time;
            var keys = new List<uint>(_entries.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                uint seq = keys[i];
                if (!_entries.TryGetValue(seq, out var entry))
                    continue;
                if (now - entry.LastSentAt < ResendIntervalSeconds)
                    continue;

                if (entry.Attempts >= HardAttempts)
                {
                    // Stop retransmitting but KEEP the obligation, per the frozen protocol: a
                    // timeout must never discard a take (which could destroy a real hand item the
                    // host already removed) or an add. Replay once so a host that holds the ack
                    // can answer, then hold the entry for a late result; it leaves only on a real
                    // result or session teardown.
                    if (!entry.Escalated)
                    {
                        entry.Escalated = true;
                        entry.LastSentAt = now;
                        _entries[seq] = entry;
                        CoopPlugin.Log.LogError(
                            $"PendingTransferLedger: transfer seq={seq} unresolved after {entry.Attempts} attempts; keeping obligation and replaying once");
                        Escalate?.Invoke(entry);
                        Resend?.Invoke(entry);
                    }
                    continue;
                }

                entry.Attempts++;
                entry.LastSentAt = now;
                _entries[seq] = entry;
                if (entry.Attempts == EscalateAttempts)
                {
                    CoopPlugin.Log.LogWarning(
                        $"PendingTransferLedger: transfer seq={seq} escalated after {entry.Attempts} attempts; requesting resync");
                    Escalate?.Invoke(entry);
                }
                Resend?.Invoke(entry);
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
            _pendingTakes.Clear();
            _takeCount = 0;
            // Deliberately do NOT reset _seq: the host keeps its (connId, seq) ack map across a
            // client scene reload, so reusing sequence numbers could collide with a stale ack
            // and silently drop a later transfer.
        }
    }
}
