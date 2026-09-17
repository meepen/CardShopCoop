using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Whether a state frame changed the local model.</summary>
    public enum PlayTableApplyResult
    {
        Applied,
        Duplicate,
        Stale,
        Invalid,
    }

    /// <summary>Unity-free record for one table. Phase zero is a release tombstone.</summary>
    public sealed class PlayTableRecord
    {
        public const int PhaseReleased = 0;
        public const int PhaseReserved = 1;
        public const int PhaseStarted = 2;

        public string MatchId;
        public int OwnerConn;
        public int TableKey;
        public byte TableIndex;
        public byte Seat;
        public bool SideA;
        public int Phase;
        public long Epoch;
        public long Revision;

        public bool IsReleased => Phase == PhaseReleased;

        public PlayTableRecord Copy()
        {
            return (PlayTableRecord)MemberwiseClone();
        }

        internal bool SameValue(PlayTableRecord other)
        {
            return other != null && MatchId == other.MatchId && OwnerConn == other.OwnerConn
                && TableKey == other.TableKey && TableIndex == other.TableIndex && Seat == other.Seat
                && SideA == other.SideA && Phase == other.Phase && Epoch == other.Epoch
                && Revision == other.Revision;
        }

        internal string SortKey => string.Concat(MatchId ?? string.Empty, "|", OwnerConn, "|",
            TableKey, "|", TableIndex, "|", Seat, "|", SideA ? "1" : "0", "|", Phase);
    }

    /// <summary>Wire-shaped state independent of the game's message DTOs.</summary>
    public sealed class PlayTableStateFrame
    {
        public long Epoch;
        public bool Full;
        public List<PlayTableRecord> Tables = new List<PlayTableRecord>();

        // Full frames should include the latest revision known for every table key, including
        // omitted keys. This makes an old full frame harmless when it arrives late.
        public Dictionary<int, long> TableRevisions = new Dictionary<int, long>();
    }

    /// <summary>
    /// Deterministic reducer for the host's authoritative table roster or a client's mirror.
    /// The reducer never exposes mutable records and never interprets Unity objects.
    /// </summary>
    public sealed class PlayTableStateModel
    {
        private readonly Dictionary<int, PlayTableRecord> _tables =
            new Dictionary<int, PlayTableRecord>();
        // A full frame can remove a table without carrying a tombstone. Keep that
        // removal's revision after removing the record, otherwise a delayed partial
        // can resurrect it.
        private readonly Dictionary<int, long> _revisionWatermarks =
            new Dictionary<int, long>();

        public long Epoch
        {
            get;
            private set;
        }
        public IReadOnlyDictionary<int, PlayTableRecord> Tables
        {
            get
            {
                var snapshot = new Dictionary<int, PlayTableRecord>();
                foreach (var pair in _tables)
                    snapshot[pair.Key] = pair.Value.Copy();
                return snapshot;
            }
        }

        public void Reset(long epoch)
        {
            if (epoch < Epoch)
                throw new ArgumentOutOfRangeException(nameof(epoch), "A session epoch cannot move backwards.");
            Epoch = epoch;
            _tables.Clear();
            _revisionWatermarks.Clear();
        }

        public PlayTableApplyResult Apply(PlayTableStateFrame frame)
        {
            if (frame == null || frame.Epoch < 0 || frame.Tables == null
                || frame.TableRevisions == null)
                return PlayTableApplyResult.Invalid;
            if (frame.Epoch < Epoch)
                return PlayTableApplyResult.Stale;

            // Validate the complete frame before changing epoch or deleting anything.
            // In particular, a malformed future full frame must not clear a good roster.
            for (int i = 0; i < frame.Tables.Count; i++)
            {
                PlayTableRecord record = frame.Tables[i];
                if (record == null || record.TableKey == 0 || record.Epoch != frame.Epoch
                    || record.Revision <= 0 || record.Phase < PlayTableRecord.PhaseReleased)
                    return PlayTableApplyResult.Invalid;
            }
            foreach (var pair in frame.TableRevisions)
            {
                if (pair.Key == 0 || pair.Value <= 0)
                    return PlayTableApplyResult.Invalid;
            }

            // A frame is one atomic revision.  Two different values for the same
            // table/revision are not a conflict we can resolve deterministically:
            // reject the whole frame rather than letting arrival order choose.
            var frameRecords = new Dictionary<int, PlayTableRecord>();
            for (int i = 0; i < frame.Tables.Count; i++)
            {
                PlayTableRecord record = frame.Tables[i];
                if (frameRecords.TryGetValue(record.TableKey, out var prior)
                    && record.Revision == prior.Revision && !record.SameValue(prior))
                    return PlayTableApplyResult.Invalid;
                frameRecords[record.TableKey] = record;
                if (_tables.TryGetValue(record.TableKey, out var current)
                    && current.Revision == record.Revision && !current.SameValue(record))
                    return PlayTableApplyResult.Invalid;
            }

            if (frame.Full)
            {
                // On the current epoch, omission is destructive only when the sender
                // proves its view of that table with a watermark.
                if (frame.Epoch == Epoch)
                {
                    foreach (var pair in _tables)
                        if (!frame.TableRevisions.ContainsKey(pair.Key))
                            return PlayTableApplyResult.Invalid;
                    foreach (var pair in _revisionWatermarks)
                        if (!frame.TableRevisions.ContainsKey(pair.Key))
                            return PlayTableApplyResult.Invalid;
                }
                // A full frame's watermark is authoritative for every record it carries. Do
                // this check before changing the epoch or applying any record so a malformed
                // snapshot cannot partially replace a valid roster.
                for (int i = 0; i < frame.Tables.Count; i++)
                {
                    PlayTableRecord record = frame.Tables[i];
                    if (!frame.TableRevisions.TryGetValue(record.TableKey, out long watermark)
                        || record.Revision != watermark)
                        return PlayTableApplyResult.Invalid;
                }
            }

            if (frame.Epoch > Epoch)
            {
                Epoch = frame.Epoch;
                _tables.Clear();
                _revisionWatermarks.Clear();
            }

            bool changed = false;
            for (int i = 0; i < frame.Tables.Count; i++)
            {
                PlayTableRecord record = frame.Tables[i];
                changed |= ApplyRecord(record);
            }

            if (frame.Full)
            {
                foreach (var pair in frame.TableRevisions)
                {
                    if (!_revisionWatermarks.TryGetValue(pair.Key, out long oldWatermark)
                        || pair.Value > oldWatermark)
                        _revisionWatermarks[pair.Key] = pair.Value;
                }
            }

            if (frame.Full)
                changed |= ApplyFullOmissions(frame);
            return changed ? PlayTableApplyResult.Applied : PlayTableApplyResult.Duplicate;
        }

        private bool ApplyRecord(PlayTableRecord incoming)
        {
            if (!_tables.TryGetValue(incoming.TableKey, out var current))
            {
                if (_revisionWatermarks.TryGetValue(incoming.TableKey, out long watermark)
                    && incoming.Revision <= watermark)
                    return false;
                _tables[incoming.TableKey] = incoming.Copy();
                return true;
            }
            long currentRevision = current.Revision;
            if (_revisionWatermarks.TryGetValue(incoming.TableKey, out long omittedRevision)
                && omittedRevision > currentRevision)
                currentRevision = omittedRevision;
            if (incoming.Revision < currentRevision)
                return false;
            if (incoming.Revision == currentRevision)
            {
                if (current.SameValue(incoming))
                    return false;
                // A malformed/replayed equal-revision conflict must converge identically.
                if (string.CompareOrdinal(incoming.SortKey, current.SortKey) <= 0)
                    return false;
            }
            _tables[incoming.TableKey] = incoming.Copy();
            return true;
        }

        private bool ApplyFullOmissions(PlayTableStateFrame frame)
        {
            bool changed = false;
            var present = new HashSet<int>();
            for (int i = 0; i < frame.Tables.Count; i++)
                if (frame.Tables[i] != null && frame.Tables[i].Epoch == Epoch)
                    present.Add(frame.Tables[i].TableKey);

            var remove = new List<int>();
            foreach (var pair in _tables)
            {
                if (present.Contains(pair.Key))
                    continue;
                // Without a per-table watermark, omission is not safe to apply: the full frame
                // may be delayed. A producer can still remove it by sending its tombstone.
                if (frame.TableRevisions.TryGetValue(pair.Key, out long watermark)
                    && watermark >= pair.Value.Revision)
                    remove.Add(pair.Key);
            }
            for (int i = 0; i < remove.Count; i++)
            {
                int tableKey = remove[i];
                long watermark = frame.TableRevisions[tableKey];
                _tables.Remove(tableKey);
                if (!_revisionWatermarks.TryGetValue(tableKey, out long oldWatermark)
                    || watermark > oldWatermark)
                    _revisionWatermarks[tableKey] = watermark;
                changed = true;
            }

            // Keep watermarks even when there is no current record. This also makes
            // repeated/older full frames harmless after a newer omission was seen.
            foreach (var pair in frame.TableRevisions)
            {
                if (present.Contains(pair.Key))
                    continue;
                if (!_revisionWatermarks.TryGetValue(pair.Key, out long oldWatermark)
                    || pair.Value > oldWatermark)
                {
                    _revisionWatermarks[pair.Key] = pair.Value;
                    changed = true;
                }
            }
            return changed;
        }

        public bool TryGet(int tableKey, out PlayTableRecord record)
        {
            if (_tables.TryGetValue(tableKey, out var value))
            {
                record = value.Copy();
                return true;
            }
            record = null;
            return false;
        }
    }
}
