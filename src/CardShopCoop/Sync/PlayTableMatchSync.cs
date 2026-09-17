using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Authoritative playable-table reservation registry.  The state model is the
    /// only ordering and reconciliation authority on both roles.</summary>
    public sealed class PlayTableMatchSync : TickableCoopModule
    {
        private const float SweepSliceSeconds = 0.5f;
        private const double ReservationTtlSeconds = 60.0;
        private const int ReleasedMatchIdLimit = 64;

        public static PlayTableMatchSync Active;
        public Action<INetMessage> SendOp;
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;

        private sealed class Reservation
        {
            public PlayTableRecord Record;
            public DateTime AcceptedAtUtc;
        }

        private readonly PlayTableStateModel _state = new PlayTableStateModel();
        private readonly Dictionary<int, Reservation> _host = new Dictionary<int, Reservation>();
        private readonly Dictionary<string, PlayTableRecord> _released = new Dictionary<string, PlayTableRecord>();
        private readonly Queue<string> _releasedOrder = new Queue<string>();
        private readonly Dictionary<int, long> _revisions = new Dictionary<int, long>();
        private readonly PlayTableRequestReplayGuard _requestReplay = new PlayTableRequestReplayGuard();
        private float _sweepTimer;
        private int _sweepCursor;

        public PlayTableMatchSync()
        {
            Active = this;
        }
        public override string Name => nameof(PlayTableMatchSync);
        public override void Start()
        {
            Active = this;
        }

        protected override void OnHostTick(in SyncFrame frame)
        {
            if (CoopCore.InSessionWorld)
                PruneExpiredHostReservations();
        }

        public override void Reset()
        {
            _host.Clear();
            _released.Clear();
            _releasedOrder.Clear();
            _revisions.Clear();
            _requestReplay.Reset();
            _state.Reset(CoopCore.SessionGeneration);
            _sweepTimer = 0f;
            _sweepCursor = 0;
        }

        public override void ForceResend()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            _host.Clear();
            _released.Clear();
            _releasedOrder.Clear();
            _revisions.Clear();
            _requestReplay.Reset();
            if (ReferenceEquals(Active, this))
                Active = null;
        }

        public void Reserve(PlayTableMatchRequest request, int ownerConn)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            Reserve(request.MatchId, ownerConn, request.TableKey, request.TableIndex, request.Seat, request.SideA);
        }

        public void Reserve(string matchId, int ownerConn, int tableKey, byte tableIndex, byte seat, bool sideA)
        {
            RequireHost(nameof(Reserve));
            if (string.IsNullOrEmpty(matchId))
                throw new ArgumentException("A reservation requires MatchId.", nameof(matchId));

            Reservation ownerPrior = FindOwnerReservation(ownerConn);
            if (ownerPrior != null && ownerPrior.Record.TableKey != tableKey)
                Release(ownerPrior.Record.TableKey, ownerPrior.Record.MatchId, "replaced");
            if (_host.TryGetValue(tableKey, out var tablePrior))
                Release(tableKey, tablePrior.Record.MatchId, "replaced");

            var record = NewRecord(matchId, ownerConn, tableKey, tableIndex, seat, sideA,
                PlayTableRecord.PhaseReserved, NextRevision(tableKey));
            _host[tableKey] = new Reservation { Record = record, AcceptedAtUtc = DateTime.UtcNow };
            CoopPlugin.Log.LogInfo($"PlayTableMatchSync: reserving table {tableKey} for owner {ownerConn} revision={record.Revision}");
            BroadcastRecord(record);
        }

        public bool Release(int tableKey, string matchId, string reason)
        {
            RequireHost(nameof(Release));
            if (!_host.TryGetValue(tableKey, out var reservation) || reservation.Record.MatchId != matchId)
                return false;
            PlayTableRecord old = reservation.Record;
            _host.Remove(tableKey);
            PlayTableRecord tombstone = old.Copy();
            tombstone.Phase = PlayTableRecord.PhaseReleased;
            tombstone.Revision = NextRevision(tableKey, old.Revision);
            if (!_released.ContainsKey(old.MatchId))
                _releasedOrder.Enqueue(old.MatchId);
            _released[old.MatchId] = tombstone.Copy();
            while (_releasedOrder.Count > ReleasedMatchIdLimit)
                _released.Remove(_releasedOrder.Dequeue());
            CoopPlugin.Log.LogInfo($"PlayTableMatchSync: releasing table {tableKey} owner {old.OwnerConn} reason={reason} revision={tombstone.Revision}");
            BroadcastRecord(tombstone);
            return true;
        }

        public bool MarkHostMatchStarted(int tableKey, string matchId, long expectedRevision = 0)
        {
            RequireHost(nameof(MarkHostMatchStarted));
            if (!_host.TryGetValue(tableKey, out var reservation)
                || reservation.Record.MatchId != matchId
                || (expectedRevision > 0 && reservation.Record.Revision != expectedRevision))
                return false;
            reservation.Record.Phase = PlayTableRecord.PhaseStarted;
            reservation.Record.Revision = NextRevision(tableKey, reservation.Record.Revision);
            reservation.AcceptedAtUtc = DateTime.UtcNow;
            BroadcastRecord(reservation.Record);
            return true;
        }

        /// <summary>Refreshes a started match lease without changing its revision or
        /// broadcasting state. The router authenticates the sender before calling this.</summary>
        public bool RenewHostMatchLease(int tableKey, string matchId, long epoch, long revision, int ownerConn)
        {
            RequireHost(nameof(RenewHostMatchLease));
            if (!_host.TryGetValue(tableKey, out var reservation)
                || reservation.Record.OwnerConn != ownerConn
                || reservation.Record.MatchId != matchId
                || reservation.Record.Epoch != epoch
                || reservation.Record.Revision != revision
                || reservation.Record.Phase != PlayTableRecord.PhaseStarted)
                return false;
            reservation.AcceptedAtUtc = DateTime.UtcNow;
            return true;
        }

        public void ApplyResult(PlayTableMatchResult result, int sender = -1)
        {
            RequireHost(nameof(ApplyResult));
            if (result == null || !_host.TryGetValue(FindTableByMatch(result.MatchId), out var reservation))
                return;
            if (sender >= 0 && reservation.Record.OwnerConn != sender)
                return;
            if (reservation.Record.Epoch != result.Epoch || reservation.Record.Revision != result.Revision)
            {
                CoopPlugin.Log.LogWarning($"PlayTableMatchSync: stale result ignored match={result.MatchId} epoch={result.Epoch} revision={result.Revision}");
                return;
            }
            // Consequence processing intentionally remains outside this registry (T5 seam).
            Release(reservation.Record.TableKey, reservation.Record.MatchId, "result");
        }

        public bool TryGetHostReservationByMatch(string matchId, out int ownerConn, out int tableKey)
        {
            tableKey = FindTableByMatch(matchId);
            if (tableKey != 0 && _host.TryGetValue(tableKey, out var reservation)
                && reservation.Record.MatchId == matchId)
            {
                ownerConn = reservation.Record.OwnerConn;
                return true;
            }
            ownerConn = -1;
            tableKey = 0;
            return false;
        }

        public bool TryGetHostMatch(int tableKey, out PlayTableMatchEntry entry)
        {
            if (_host.TryGetValue(tableKey, out var reservation))
            {
                entry = ToEntry(reservation.Record);
                return true;
            }
            entry = null;
            return false;
        }

        /// <summary>Authenticates a cancel against either the live reservation or its
        /// release tombstone. This makes duplicate cancel packets harmless without allowing
        /// an arbitrary old match id to release a different reservation.</summary>
        public bool IsAuthenticatedCancel(int tableKey, string matchId, int ownerConn, long epoch,
            out PlayTableMatchEntry live)
        {
            live = null;
            if (_host.TryGetValue(tableKey, out var reservation))
            {
                var record = reservation.Record;
                if (record.MatchId != matchId || record.OwnerConn != ownerConn)
                    return false;
                // A locally-created pending request has no observed host epoch yet. This
                // exception is intentionally limited to the live, unstarted reservation;
                // once started, the exact host epoch is mandatory.
                if (record.Epoch != epoch
                    && !(epoch == 0 && record.Phase == PlayTableRecord.PhaseReserved))
                    return false;
                live = ToEntry(record);
                return true;
            }
            return !string.IsNullOrEmpty(matchId) && _released.TryGetValue(matchId, out var released)
                && released.TableKey == tableKey && released.OwnerConn == ownerConn
                && (released.Epoch == epoch || epoch == 0);
        }

        public bool TryAcceptRequestSequence(int ownerConn, long sequence)
            => _requestReplay.TryAccept(ownerConn, sequence);

        public bool HasHostReservationByOwner(int connId)
        {
            foreach (var r in _host.Values)
                if (r.Record.OwnerConn == connId)
                    return true;
            return false;
        }

        public bool HasHostActiveMatch(int tableKey) => _host.ContainsKey(tableKey);

        public bool TryGetMatch(int tableKey, out PlayTableMatchEntry entry)
        {
            if (_state.TryGet(tableKey, out var record) && !record.IsReleased)
            {
                entry = ToEntry(record);
                return true;
            }
            entry = null;
            return false;
        }

        public bool HasActiveMatch(int tableKey) => TryGetMatch(tableKey, out _);
        public int? GetMatchOwner(int tableKey) => TryGetMatch(tableKey, out var e) ? (int?)e.OwnerConn : null;

        public List<PlayTableMatchEntry> GetOwnAppliedMatches(int connId)
        {
            var result = new List<PlayTableMatchEntry>();
            foreach (var pair in _state.Tables)
                if (!pair.Value.IsReleased && pair.Value.OwnerConn == connId)
                    result.Add(ToEntry(pair.Value));
            return result;
        }

        public void OnPlayerDisconnect(int connId)
        {
            RequireHost(nameof(OnPlayerDisconnect));
            var release = new List<PlayTableRecord>();
            foreach (var r in _host.Values)
                if (r.Record.OwnerConn == connId)
                    release.Add(r.Record);
            foreach (var r in release)
                Release(r.TableKey, r.MatchId, "disconnect");
            _requestReplay.RemoveOwner(connId);
        }

        public void PruneExpiredHostReservations()
        {
            RequireHost(nameof(PruneExpiredHostReservations));
            var now = DateTime.UtcNow;
            var expired = new List<PlayTableRecord>();
            foreach (var r in _host.Values)
                if ((now - r.AcceptedAtUtc).TotalSeconds >= ReservationTtlSeconds)
                    expired.Add(r.Record);
            foreach (var r in expired)
                Release(r.TableKey, r.MatchId, "ttl-expired");
        }

        public void ClientApplyState(PlayTableMatchState message)
        {
            if (message == null)
                return;
            var frame = new PlayTableStateFrame { Epoch = message.Epoch, Full = message.Full, TableRevisions = message.TableRevisions };
            if (message.Matches != null)
                foreach (var entry in message.Matches)
                    if (entry != null)
                        frame.Tables.Add(FromEntry(entry));
            PlayTableApplyResult result = _state.Apply(frame);
            if (result == PlayTableApplyResult.Invalid)
                CoopPlugin.Log.LogWarning("PlayTableMatchSync: invalid state frame rejected");
            else if (result == PlayTableApplyResult.Applied)
                PlayTableSync.OnMatchStateReconciled();
        }

        public override void PeriodicUpdate(float delta)
        {
            if (CoopCore.Role != CoopRole.Host || !CoopCore.InSessionWorld || _host.Count == 0)
                return;
            _sweepTimer += delta;
            if (_sweepTimer < SweepSliceSeconds)
                return;
            _sweepTimer = 0f;
            var values = new List<Reservation>(_host.Values);
            if (_sweepCursor >= values.Count)
                _sweepCursor = 0;
            BroadcastRecord(values[_sweepCursor++].Record);
        }

        public override void FullUpdate(Connection connection)
        {
            int connId = connection.Id;
            if (CoopCore.Role != CoopRole.Host || SendToClient == null)
                return;
            var message = new PlayTableMatchState { Epoch = CoopCore.SessionGeneration, Full = true };
            foreach (var r in _host.Values)
                message.Matches.Add(ToEntry(r.Record));
            foreach (var pair in _host)
                message.TableRevisions[pair.Key] = pair.Value.Record.Revision;
            foreach (var pair in _revisions)
                message.TableRevisions[pair.Key] = pair.Value;
            SendToClient(connId, message);
        }

        private long NextRevision(int tableKey, long prior = 0)
        {
            if (_state.TryGet(tableKey, out var current) && current.Revision > prior)
                prior = current.Revision;
            if (_revisions.TryGetValue(tableKey, out long known) && known > prior)
                prior = known;
            long next = prior + 1;
            _revisions[tableKey] = next;
            return next;
        }

        private void BroadcastRecord(PlayTableRecord record)
        {
            var message = new PlayTableMatchState { Epoch = record.Epoch, Full = false };
            message.Matches.Add(ToEntry(record));
            message.TableRevisions[record.TableKey] = record.Revision;
            BroadcastState?.Invoke(message);
            _state.Apply(new PlayTableStateFrame { Epoch = record.Epoch, Tables = new List<PlayTableRecord> { record.Copy() }, TableRevisions = new Dictionary<int, long> { [record.TableKey] = record.Revision } });
        }

        private int FindTableByMatch(string matchId)
        {
            foreach (var pair in _host)
                if (pair.Value.Record.MatchId == matchId)
                    return pair.Key;
            return 0;
        }
        private Reservation FindOwnerReservation(int owner)
        {
            foreach (var r in _host.Values)
                if (r.Record.OwnerConn == owner)
                    return r;
            return null;
        }
        private static PlayTableRecord NewRecord(string id, int owner, int key, byte index, byte seat, bool side, int phase, long revision)
            => new PlayTableRecord { MatchId = id, OwnerConn = owner, TableKey = key, TableIndex = index, Seat = seat, SideA = side, Phase = phase, Epoch = CoopCore.SessionGeneration, Revision = revision };
        private static PlayTableMatchEntry ToEntry(PlayTableRecord r)
            => new PlayTableMatchEntry { MatchId = r.MatchId, Epoch = r.Epoch, Revision = r.Revision, OwnerConn = r.OwnerConn, TableKey = r.TableKey, TableIndex = r.TableIndex, Seat = r.Seat, SideA = r.SideA, Phase = r.Phase };
        private static PlayTableRecord FromEntry(PlayTableMatchEntry e)
            => new PlayTableRecord { MatchId = e.MatchId, Epoch = e.Epoch, Revision = e.Revision, OwnerConn = e.OwnerConn, TableKey = e.TableKey, TableIndex = e.TableIndex, Seat = e.Seat, SideA = e.SideA, Phase = e.Phase };
        private static void RequireHost(string operation)
        {
            if (CoopCore.Role != CoopRole.Host)
                throw new InvalidOperationException("PlayTableMatchSync host operation on client: " + operation);
        }
    }
}
