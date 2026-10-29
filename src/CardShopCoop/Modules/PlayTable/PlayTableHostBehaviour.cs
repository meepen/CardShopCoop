using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>Host validation, vanilla table mutation, and live-table state publication.</summary>
    [ServerBehaviour]
    public sealed class PlayTableHostBehaviour : CoopBehaviour
    {
        private const int MaxTables = 250;
        private const int MaxSeats = 8;

        private static PlayTableHostBehaviour _active;
        private readonly Dictionary<InteractablePlayTable, Reservation> _reservations = new();
        private readonly Dictionary<InteractablePlayTable, PlayTableEntry> _knownVisuals = new();
        private readonly HashSet<int> _fullyJoined = new();
        private bool _shutdown;
        private bool _initialized;
        private bool _suppressStoppedVisual;

        private sealed class Reservation
        {
            internal PlayTableMatchEntry Match;
            internal PlayTableSeatSnapshot SeatBefore;
        }

        internal static PlayTableHostBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _active = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyPatches();
            RefreshVisuals(false);
            _initialized = true;
        }

        /// <summary>Seeds every live table's move gate before this peer receives the table mirror.
        /// A save load restores each table's player count after the scene's tables exist, so this
        /// runs from the join handler instead of a pre-transport lifecycle hook.</summary>
        private void PublishGameStates()
        {
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            for (var i = 0; tables != null && i < tables.Count; i++)
            {
                PublishGameState(tables[i]);
            }
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _fullyJoined.Add(connection.Id);
            // The extended-information baseline is assembled independently: make sure every live
            // table's move gate is stored before this peer starts acting on the table mirror.
            PublishGameStates();
            SendBaseline(connection.Id);
        }

        [OnClientDisconnected]
        private void ReleaseConnection(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null)
            {
                return;
            }

            _fullyJoined.Remove(connection.Id);
            var releases = new List<InteractablePlayTable>();
            foreach (var pair in _reservations)
            {
                if (pair.Value.Match.OwnerConn == connection.Id)
                {
                    releases.Add(pair.Key);
                }
            }

            for (var i = 0; i < releases.Count; i++)
            {
                Release(releases[i], "disconnect", Guid.Empty);
            }
        }

        [MessageHandler]
        private ActionOutcome HandleStartMatchIntent(MessageContext messageContext,
            StartTableMatchIntent request)
        {
            if (!IsPeerMessage(messageContext) || request == null)
            {
                return ActionOutcome.Reject("match start from an invalid sender");
            }

            return StartMatch(request, messageContext.Connection.Id);
        }

        [MessageHandler]
        private ActionOutcome HandleCancelMatchIntent(MessageContext messageContext,
            CancelTableMatchIntent request)
        {
            if (!IsPeerMessage(messageContext) || request == null)
            {
                return ActionOutcome.Reject("match cancel from an invalid sender");
            }

            return CancelMatch(request, messageContext.Connection.Id);
        }

        [MessageHandler]
        private ActionOutcome HandleMatchStartedIntent(MessageContext messageContext,
            MarkTableMatchStartedIntent request)
        {
            if (!IsPeerMessage(messageContext) || request == null)
            {
                return ActionOutcome.Reject("match-started notice from an invalid sender");
            }

            return MarkMatchStarted(request, messageContext.Connection.Id);
        }

        [MessageHandler]
        private ActionOutcome HandleReleaseMatchIntent(MessageContext messageContext,
            ReleaseTableMatchIntent request)
        {
            if (!IsPeerMessage(messageContext) || request == null)
            {
                return ActionOutcome.Reject("match release from an invalid sender");
            }

            return ReleaseMatch(request, messageContext.Connection.Id);
        }

        [MessageHandler]
        private ActionOutcome HandleKickTableIntent(MessageContext messageContext, KickTableIntent message)
        {
            if (!IsPeerMessage(messageContext) || message == null)
            {
                return ActionOutcome.Reject("table kick from an invalid sender");
            }

            var owner = messageContext.Connection.Id;
            // The client names the live table it acted on. The identity converter already
            // resolved it against the host's live placement list, so a stale table arrives null
            // and is dropped instead of being matched by a list index or placement key.
            var table = message.Table;
            if (table == null)
            {
                return Reject(owner, message.ActionId, "table is not live");
            }

            if (table.GetIsTournamentPlayTable() || table.GetCurrentPlayerCount() <= 0
                || _reservations.ContainsKey(table) || PlayTableInterop.HostPlayingAt(table)
                || table.GetHasStartPlayerPlayCard())
            {
                // The requester's occupancy mirror can trail the host (a customer just finished,
                // or another client already kicked the table). A host player's own live game also
                // owns the table and must not be stopped from a guest's move. Re-anchor the
                // requester to authority before the generic rollback so the undo does not
                // resurrect stale occupancy and leave the table permanently unmovable. The gate
                // is resent even when the host's stored value is unchanged: the requester's copy
                // is the one that trailed.
                PublishGameState(table, true);
                SendTableState(owner, table, message.ActionId);
                CoopPlugin.Log.LogWarning("play-table intent rejected for connection=" + owner
                    + ": table is not occupied by a kickable game (authority resent).");
                // The authoritative table state above already settles the requester's prediction.
                return ActionOutcome.Applied();
            }

            _suppressStoppedVisual = true;
            try
            {
                PlayTableInterop.InvokeStopTableGame(table);
            }
            finally
            {
                _suppressStoppedVisual = false;
            }

            NotifyTableChanged(table, message.ActionId, true);
            return ActionOutcome.Applied();
        }

        internal bool HasActiveMatch(InteractablePlayTable table)
            => table != null && _reservations.ContainsKey(table);

        internal static bool IsPlacementMoveAllowed(InteractableObject obj)
        {
            if (obj is not InteractablePlayTable table)
            {
                return true;
            }

            return (_active == null || !_active._reservations.ContainsKey(table))
                && table.GetCurrentPlayerCount() <= 0 && !PlayTableInterop.HostPlayingAt(table);
        }

        internal static bool IsBoxUpAllowed(InteractablePlayTable table)
            => table != null && !table.GetIsBoxedUp() && table.GetCurrentPlayerCount() <= 0
                && (_active == null || !_active._reservations.ContainsKey(table))
                && !PlayTableInterop.HostPlayingAt(table)
                && table.GetTournamentPlayTableNumber() <= 0;

        private ActionOutcome StartMatch(StartTableMatchIntent request, int owner)
        {
            // The request names the live table; the identity converter resolved it against this
            // host's live placement list. A missing object is a stale table and is dropped.
            var table = request.Table;
            if (table == null)
            {
                return Reject(owner, request.ActionId, "table is not live");
            }

            if (_reservations.ContainsKey(table))
            {
                return Reject(owner, request.ActionId, "table already reserved");
            }

            foreach (var existing in _reservations.Values)
            {
                if (existing.Match.OwnerConn == owner)
                {
                    return Reject(owner, request.ActionId, "owner already has a match");
                }

                if (existing.Match.MatchId == request.MatchId)
                {
                    return Reject(owner, request.ActionId, "match id is already active");
                }
            }

            if (PlayTableInterop.HostPlayingAt(table) || request.Seat > 1)
            {
                return Reject(owner, request.ActionId, "table or seat is unavailable");
            }

            var occupied = table.m_IsSeatOccupied;
            var booked = table.m_IsSeatBooked;
            var queued = table.m_IsQueueOccupied;
            var customerSeat = request.Seat == 0 ? 1 : 0;
            if (occupied == null || booked == null || queued == null
                || occupied.Count <= request.Seat || occupied[request.Seat]
                || booked.Count <= request.Seat || booked[request.Seat]
                || queued.Count <= request.Seat || queued[request.Seat]
                || occupied.Count <= customerSeat || !occupied[customerSeat]
                || request.SideA != (request.Seat == 0))
            {
                return Reject(owner, request.ActionId,
                    "requested seat must be free and the customer seat occupied");
            }

            var maxDeck = GameInstance.GetMaxDeckCardCount();
            if (request.DeckCardCount < 1 || request.DeckCardCount > maxDeck)
            {
                return Reject(owner, request.ActionId, "invalid deck size");
            }

            if (string.IsNullOrEmpty(request.MatchId) || request.MatchId.Length > 64)
            {
                return Reject(owner, request.ActionId, "invalid match id");
            }

            if (!PlayTableInterop.ReserveSeat(table, request.Seat, out var seatBefore))
            {
                return Reject(owner, request.ActionId, "reservation race");
            }

            var reservation = new Reservation
            {
                SeatBefore = seatBefore,
                Match = new PlayTableMatchEntry
                {
                    MatchId = request.MatchId,
                    OwnerConn = owner,
                    Table = table,
                    Seat = request.Seat,
                    SideA = request.SideA,
                    Phase = PlayTableMatchEntry.StateReserved,
                },
            };
            _reservations.Add(table, reservation);
            NotifyTableChanged(table, request.ActionId, false);
            PublishMatchUpsert(reservation.Match, request.ActionId);
            return ActionOutcome.Applied();
        }

        private ActionOutcome CancelMatch(CancelTableMatchIntent request, int owner)
        {
            if (!TryGetOwnedReservation(request.Table, request.MatchId, owner, out var reservation))
            {
                return Reject(owner, request.ActionId, "cancel is not authenticated");
            }

            Release(reservation.Match.Table, "cancelled", request.ActionId);
            return ActionOutcome.Applied();
        }

        private ActionOutcome MarkMatchStarted(MarkTableMatchStartedIntent request, int owner)
        {
            if (!TryGetOwnedReservation(request.Table, request.MatchId, owner, out var reservation))
            {
                return Reject(owner, request.ActionId, "match is not owned by sender");
            }

            reservation.Match.Phase = PlayTableMatchEntry.StateStarted;
            PublishMatchUpsert(reservation.Match, request.ActionId);
            return ActionOutcome.Applied();
        }

        private ActionOutcome ReleaseMatch(ReleaseTableMatchIntent request, int owner)
        {
            if (!TryGetOwnedReservation(request.Table, request.MatchId, owner, out var reservation))
            {
                return Reject(owner, request.ActionId, "match release is not authenticated");
            }

            Release(reservation.Match.Table, "gameplay-finished", request.ActionId);
            return ActionOutcome.Applied();
        }

        private bool TryGetOwnedReservation(InteractablePlayTable table, string matchId, int owner,
            out Reservation reservation)
        {
            reservation = null;
            return table != null
                && _reservations.TryGetValue(table, out reservation)
                && reservation.Match.OwnerConn == owner
                && reservation.Match.MatchId == matchId;
        }

        private bool Release(InteractablePlayTable table, string reason, Guid actionId)
        {
            if (table == null || !_reservations.TryGetValue(table, out var reservation))
            {
                return false;
            }

            _reservations.Remove(table);
            PlayTableInterop.RestoreReservedSeat(table, reservation.SeatBefore,
                reservation.Match.Seat);
            CoopPlugin.Log.LogInfo("play-table match released table=" + table.name
                + " reason=" + reason);
            NotifyTableChanged(table, actionId, false);
            PublishMatchRelease(reservation.Match, actionId);
            return true;
        }

        private ActionOutcome Reject(int connectionId, Guid actionId, string reason)
        {
            CoopPlugin.Log.LogWarning("play-table intent rejected for connection=" + connectionId
                + " id=" + actionId + ": " + reason);
            return ActionOutcome.Reject(reason);
        }

        private PlayTableBaselineMessage BuildBaseline()
        {
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            if (tables == null)
            {
                return null;
            }

            var message = new PlayTableBaselineMessage();
            var count = Math.Min(tables.Count, MaxTables);
            for (var i = 0; i < count; i++)
            {
                if (tables[i] != null)
                {
                    message.Tables.Add(BuildTableEntry(tables[i]));
                }
            }

            foreach (var reservation in _reservations.Values)
            {
                message.Matches.Add(CopyMatch(reservation.Match));
            }

            RefreshVisuals(false);
            return message;
        }

        private void SendBaseline(int connectionId)
        {
            if (connectionId <= 0 || !RuntimeContext.InGame())
            {
                return;
            }

            var baseline = BuildBaseline();
            if (baseline != null)
            {
                RuntimeContext.Send(connectionId, baseline);
            }
        }

        private void SendBaselinesToJoined()
        {
            foreach (var connectionId in new List<int>(_fullyJoined))
            {
                SendBaseline(connectionId);
            }
        }

        private void PublishVisual(PlayTableVisualDeltaMessage delta)
        {
            if (!_shutdown && RuntimeContext.InGame())
            {
                RuntimeContext.Broadcast(delta);
            }
        }

        /// <summary>Sends one requester the live table state under a rejected action's id. The
        /// client's reducer retires that action and folds authority instead of running its undo
        /// against a snapshot the host has already moved past.</summary>
        private void SendTableState(int connectionId, InteractablePlayTable table, Guid actionId)
        {
            if (_shutdown || connectionId <= 0 || table == null || !RuntimeContext.InGame())
            {
                return;
            }

            RuntimeContext.Send(connectionId, CreateTableDelta(BuildTableEntry(table), actionId));
        }

        private void NotifyTableChanged(InteractablePlayTable table, Guid actionId,
            bool force)
        {
            if (table == null)
            {
                return;
            }

            PublishGameState(table);
            var current = BuildTableEntry(table);
            if (!_knownVisuals.TryGetValue(table, out var old))
            {
                PublishVisual(CreateTableDelta(current, actionId));
            }
            else
            {
                if (force || old.Occupied != current.Occupied || old.Boxed != current.Boxed)
                {
                    PublishVisual(CreateTableDelta(current, actionId));
                }
                else
                {
                    for (var seat = 0; seat < current.Seats.Count; seat++)
                    {
                        if (seat >= old.Seats.Count || !SameSeat(old.Seats[seat], current.Seats[seat]))
                        {
                            PublishVisual(new PlayTableVisualDeltaMessage
                            {
                                ActionId = actionId,
                                Operation = PlayTableVisualDeltaMessage.SeatUpdate,
                                Table = table,
                                Seat = (byte)seat,
                                SeatState = CopySeat(current.Seats[seat]),
                            });
                        }
                    }
                }
            }

            _knownVisuals[table] = current;
        }

        /// <summary>Publishes the vanilla move-gate fields as object-attached authority.
        /// Extended information is keyed by the table's stable identity, so a guest reads the
        /// host's real occupancy synchronously when the pickup input arrives, and the value rides
        /// the join baseline with the table instead of depending on a visual delta arriving
        /// first.</summary>
        private void PublishGameState(InteractablePlayTable table, bool force = false)
        {
            // A host has no peer before the bearer exists, so there is nothing to broadcast and
            // CoopCore.Broadcast would throw. Lifecycle hooks can still run game code this early.
            if (_shutdown || table == null || !RuntimeContext.InGame()
                || CoopCore.Instance?.Net == null
                || !GameObjectIdentityApi.TryGetId(table, out _))
            {
                return;
            }

            var playerCount = table.GetCurrentPlayerCount();
            var isTournament = table.GetIsTournamentPlayTable();
            var playerOccupied = PlayTableInterop.HostPlayingAt(table)
                || table.GetHasStartPlayerPlayCard();
            var current = table.GetExtendedInformation<PlayTableGameState>();
            if (!force && current != null && current.PlayerCount == playerCount
                && current.IsTournament == isTournament
                && current.PlayerOccupied == playerOccupied)
            {
                return;
            }

            table.SetExtendedInformation(new PlayTableGameState
            {
                PlayerCount = playerCount,
                IsTournament = isTournament,
                PlayerOccupied = playerOccupied,
            });
        }

        private void NotifyTableRemoved(InteractablePlayTable table)
        {
            if (table == null)
            {
                return;
            }

            Release(table, "table-removed", Guid.Empty);
            if (_knownVisuals.Remove(table))
            {
                PublishVisual(new PlayTableVisualDeltaMessage
                {
                    Operation = PlayTableVisualDeltaMessage.TableRemove,
                    Table = table,
                });
            }
        }

        private void RefreshVisuals(bool publish)
        {
            var current = new Dictionary<InteractablePlayTable, PlayTableEntry>();
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            var count = Math.Min(tables?.Count ?? 0, MaxTables);
            for (var i = 0; tables != null && i < count; i++)
            {
                var table = tables[i];
                if (table != null)
                {
                    current[table] = BuildTableEntry(table);
                }
            }

            var removed = new List<InteractablePlayTable>();
            foreach (var pair in _knownVisuals)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    removed.Add(pair.Key);
                }
            }

            for (var i = 0; i < removed.Count; i++)
            {
                if (publish)
                {
                    PublishVisual(new PlayTableVisualDeltaMessage
                    {
                        Operation = PlayTableVisualDeltaMessage.TableRemove,
                        Table = removed[i],
                    });
                }

                _knownVisuals.Remove(removed[i]);
            }

            foreach (var pair in current)
            {
                if (publish && !_knownVisuals.ContainsKey(pair.Key))
                {
                    PublishVisual(CreateTableDelta(pair.Value, Guid.Empty));
                }

                _knownVisuals[pair.Key] = pair.Value;
            }
        }

        private void PublishMatchUpsert(PlayTableMatchEntry match, Guid actionId)
        {
            if (match?.Table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped match upsert with no live table object.");
                return;
            }

            RuntimeContext.Broadcast(new PlayTableMatchDeltaMessage
            {
                ActionId = actionId,
                Operation = PlayTableMatchDeltaMessage.Upsert,
                MatchId = match.MatchId,
                Table = match.Table,
                Match = CopyMatch(match),
            });
        }

        private void PublishMatchRelease(PlayTableMatchEntry match, Guid actionId)
        {
            if (match?.Table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped match release with no live table object.");
                return;
            }

            RuntimeContext.Broadcast(new PlayTableMatchDeltaMessage
            {
                ActionId = actionId,
                Operation = PlayTableMatchDeltaMessage.Release,
                MatchId = match.MatchId,
                Table = match.Table,
            });
        }

        private static PlayTableVisualDeltaMessage CreateTableDelta(PlayTableEntry entry,
            Guid actionId)
            => new()
            {
                ActionId = actionId,
                Operation = PlayTableVisualDeltaMessage.TableUpsert,
                Table = entry.Table,
                Occupied = entry.Occupied,
                Boxed = entry.Boxed,
                Seats = CopySeats(entry.Seats),
            };

        private static PlayTableEntry BuildTableEntry(InteractablePlayTable table)
        {
            var sets = table?.m_TableGameItemSetList;
            var seats = Math.Min(sets?.Count ?? 0, MaxSeats);
            var entry = new PlayTableEntry
            {
                Table = table,
                Occupied = table != null && table.GetCurrentPlayerCount() > 0,
                Boxed = table != null && table.GetIsBoxedUp(),
            };
            // InteractablePlayTable.Awake calls ShelfManager.InitPlayTable (hooked by
            // TableAddedPatch) before it fills the seat lists and hides each seat's item set, so
            // a new table's sets still read active here. Report a seat Awake has not set up yet
            // as inactive, matching what Awake does next.
            var initializedSeats = table?.m_OccupiedCustomer?.Count ?? 0;
            for (var seat = 0; seat < seats; seat++)
            {
                var set = sets[seat];
                var active = set != null && set.gameObject.activeSelf && seat < initializedSeats;
                var data = set?.m_TableGameItemSetData;
                entry.Seats.Add(new PlayTableSeatEntry
                {
                    Active = active,
                    PlayerSeat = PlayTableInterop.IsPlayerSeat(table, seat),
                    PlayMat = active && data != null ? data.playMatType : default,
                    DeckBox = active && data != null ? data.deckBoxType : default,
                    Comic = active && data != null ? data.comicBookType : default,
                });
            }

            return entry;
        }

        private static bool SameSeat(PlayTableSeatEntry left, PlayTableSeatEntry right)
            => left != null && right != null && left.Active == right.Active
                && left.PlayerSeat == right.PlayerSeat && left.PlayMat == right.PlayMat
                && left.DeckBox == right.DeckBox && left.Comic == right.Comic;

        private static List<PlayTableSeatEntry> CopySeats(List<PlayTableSeatEntry> source)
        {
            var result = new List<PlayTableSeatEntry>();
            for (var i = 0; source != null && i < source.Count; i++)
            {
                result.Add(CopySeat(source[i]));
            }

            return result;
        }

        private static PlayTableSeatEntry CopySeat(PlayTableSeatEntry source)
            => source == null ? null : new PlayTableSeatEntry
            {
                Active = source.Active,
                PlayerSeat = source.PlayerSeat,
                PlayMat = source.PlayMat,
                DeckBox = source.DeckBox,
                Comic = source.Comic,
            };

        private static PlayTableMatchEntry CopyMatch(PlayTableMatchEntry source)
            => new()
            {
                MatchId = source.MatchId,
                OwnerConn = source.OwnerConn,
                Table = source.Table,
                Seat = source.Seat,
                SideA = source.SideA,
                Phase = source.Phase,
            };

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && RuntimeContext.InGame() && context?.Connection != null
                && IsJoinPhase(context.Connection.State);

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            _reservations.Clear();
            _knownVisuals.Clear();
            RefreshVisuals(false);
            SendBaselinesToJoined();
        }

        private void ApplyPatches()
        {
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "OnDestroyed"),
                new HarmonyMethod(typeof(TableRemovedPatch), nameof(TableRemovedPatch.Prefix)),
                new HarmonyMethod(typeof(TableChangedPatch), nameof(TableChangedPatch.Postfix)));
            Patch(AccessTools.Method(typeof(ShelfManager), "InitPlayTable",
                    new[] { typeof(InteractablePlayTable) }), null,
                new HarmonyMethod(typeof(TableAddedPatch), nameof(TableAddedPatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "CustomerHasReached"), null,
                new HarmonyMethod(typeof(TableChangedPatch), nameof(TableChangedPatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "StopTableGame"), null,
                new HarmonyMethod(typeof(TableStoppedPatch), nameof(TableStoppedPatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp"), null,
                new HarmonyMethod(typeof(TableChangedPatch), nameof(TableChangedPatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "SetIsTournamentPlayTable"), null,
                new HarmonyMethod(typeof(GameStateChangedPatch),
                    nameof(GameStateChangedPatch.Postfix)));
        }

        private void Patch(System.Reflection.MethodInfo original, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("play-table host patch target missing");
                return;
            }

            Harmony.Patch(original, prefix, postfix);
        }

        private static class TableChangedPatch
        {
            internal static void Postfix(InteractablePlayTable __instance)
                => _active?.NotifyTableChanged(__instance, Guid.Empty, false);
        }

        private static class TableAddedPatch
        {
            internal static void Postfix()
                => _active?.RefreshVisuals(true);
        }

        /// <summary>Only the move gate changes here; publishing a visual delta during Awake (the
        /// game sets this flag while the table is still initialising) would announce a half-built
        /// table.</summary>
        private static class GameStateChangedPatch
        {
            internal static void Postfix(InteractablePlayTable __instance)
                => _active?.PublishGameState(__instance);
        }

        private static class TableRemovedPatch
        {
            internal static void Prefix(InteractablePlayTable __instance)
                => _active?.NotifyTableRemoved(__instance);
        }

        private static class TableStoppedPatch
        {
            internal static void Postfix(InteractablePlayTable __instance)
            {
                var host = _active;
                if (host == null)
                {
                    return;
                }

                host.Release(__instance, "table-stopped", Guid.Empty);

                if (!host._suppressStoppedVisual)
                {
                    host.NotifyTableChanged(__instance, Guid.Empty, true);
                }
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _reservations.Clear();
            _knownVisuals.Clear();
            _fullyJoined.Clear();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();
    }
}
