using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>Host validation, vanilla table mutation, and keyed state publication.</summary>
    [ServerBehaviour]
    public sealed class PlayTableHostBehaviour : CoopBehaviour
    {
        private const int MaxTables = 250;
        private const int MaxSeats = 8;

        private static PlayTableHostBehaviour _active;
        private readonly Dictionary<int, Reservation> _reservations = new();
        private readonly Dictionary<int, PlayTableEntry> _knownVisuals = new();
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _suppressStoppedVisual;

        private sealed class Reservation
        {
            internal PlayTableMatchEntry Match;
            internal InteractablePlayTable Table;
            internal PlayTableSeatSnapshot SeatBefore;
        }

        internal static PlayTableHostBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("dev.meepen.cardshopcoop.play-table.host");
            ApplyPatches();
            RefreshVisuals(false);
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _fullyJoined.Add(connection.Id);
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
            var releases = new List<int>();
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

        [MessageHandler(typeof(PlayTableMatchIntentMessage))]
        private void HandleMatchIntent(MessageContext messageContext,
            PlayTableMatchIntentMessage request)
        {
            if (!IsPeerMessage(messageContext) || request == null)
            {
                return;
            }

            var owner = messageContext.Connection.Id;
            switch (request.Op)
            {
                case PlayTableMatchIntentMessage.OpStart:
                    StartMatch(request, owner);
                    break;
                case PlayTableMatchIntentMessage.OpCancel:
                    CancelMatch(request, owner);
                    break;
                case PlayTableMatchIntentMessage.OpStarted:
                    MarkMatchStarted(request, owner);
                    break;
                case PlayTableMatchIntentMessage.OpRelease:
                    ReleaseMatch(request, owner);
                    break;
                default:
                    Reject(owner, request.PredictionId, "unknown match intent");
                    break;
            }
        }

        [MessageHandler(typeof(PlayTableIntentMessage))]
        private void HandleTableIntent(MessageContext messageContext, PlayTableIntentMessage message)
        {
            if (!IsPeerMessage(messageContext) || message == null)
            {
                return;
            }

            var owner = messageContext.Connection.Id;
            if (message.Kind != PlayTableIntentMessage.IntentKindPlayTable
                || (message.Action != PlayTableIntentMessage.IntentKickTable
                    && message.Action != PlayTableIntentMessage.IntentBoxTable))
            {
                Reject(owner, message.PredictionId, "invalid table intent");
                return;
            }

            // The client addresses the table by the stable placement key it received from the
            // host. Resolve that key straight back to the live object; a list index would not
            // survive a concurrent placement, and there is no index on the wire to fall back to.
            var table = PlacementApi.ResolveObjectByKey(message.ObjectKey) as InteractablePlayTable;
            if (table == null
                || !PlayTablePlacementInterop.TryGetTableKey(table, out var computedKey)
                || computedKey != message.ObjectKey)
            {
                Reject(owner, message.PredictionId, "stale table identity");
                return;
            }

            if (message.Action == PlayTableIntentMessage.IntentKickTable)
            {
                if (table.GetIsTournamentPlayTable() || table.GetCurrentPlayerCount() <= 0
                    || _reservations.ContainsKey(message.ObjectKey))
                {
                    Reject(owner, message.PredictionId,
                        "table is not occupied by a kickable game");
                    return;
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

                NotifyTableChanged(table, message.PredictionId, true);
                return;
            }

            if (table.GetIsBoxedUp() || table.GetCurrentPlayerCount() > 0
                || _reservations.ContainsKey(message.ObjectKey)
                || PlayTableInterop.HostPlayingAt(table))
            {
                Reject(owner, message.PredictionId,
                    "occupied or reserved table cannot be boxed");
                return;
            }

            if (table.GetTournamentPlayTableNumber() > 0)
            {
                Reject(owner, message.PredictionId,
                    "tournament-numbered table cannot be boxed");
                return;
            }

            // The client is the creator and carried the package's stable id. A non-fresh id means
            // the client already bound a live box to it: adopting it would rebind that box, and
            // minting a different id would desync the client's package. Reject the intent.
            if (!WorldHostBehaviour.PushCreatedBoxId(message.BoxNetworkId))
            {
                Reject(owner, message.PredictionId, "package box id is not fresh");
                return;
            }

            WorldHostBehaviour.Active?.SetNextMutationPrediction(message.PredictionId);
            try
            {
                table.BoxUpObject(true);
            }
            finally
            {
                WorldHostBehaviour.Active?.SetNextMutationPrediction(Guid.Empty);
            }

            if (!table.GetIsBoxedUp() || table.GetPackagingBoxShelf() == null)
            {
                // The push parked the client's id for a box the game did not produce; release it so
                // a later, unrelated creation cannot bind to it. No-op once consumed.
                WorldHostBehaviour.CancelCreatedBoxId(message.BoxNetworkId);
                Reject(owner, message.PredictionId,
                    "table boxing did not produce a package box");
            }
        }

        internal bool HasActiveMatch(int tableKey) => _reservations.ContainsKey(tableKey);

        internal static bool IsPlacementMoveAllowed(InteractableObject obj)
        {
            if (obj is not InteractablePlayTable table)
            {
                return true;
            }

            if (!PlayTablePlacementInterop.TryGetTableKey(table, out var key))
            {
                return false;
            }

            return (_active == null || !_active._reservations.ContainsKey(key))
                && table.GetCurrentPlayerCount() <= 0 && !PlayTableInterop.HostPlayingAt(table);
        }

        private void StartMatch(PlayTableMatchIntentMessage request, int owner)
        {
            // Bind the reservation to the stable placement key alone. The client's key was
            // assigned by this host and returned to it, so a successful resolve plus a matching
            // recomputed key proves the local object is the one the client meant.
            var table = PlacementApi.ResolveObjectByKey(request.TableKey) as InteractablePlayTable;
            if (table == null
                || !PlayTablePlacementInterop.TryGetTableKey(table, out var computedKey)
                || request.TableKey != computedKey)
            {
                Reject(owner, request.PredictionId, "table key mismatch");
                return;
            }

            if (_reservations.ContainsKey(request.TableKey))
            {
                Reject(owner, request.PredictionId, "table already reserved");
                return;
            }

            foreach (var existing in _reservations.Values)
            {
                if (existing.Match.OwnerConn == owner)
                {
                    Reject(owner, request.PredictionId, "owner already has a match");
                    return;
                }

                if (existing.Match.MatchId == request.MatchId)
                {
                    Reject(owner, request.PredictionId, "match id is already active");
                    return;
                }
            }

            if (PlayTableInterop.HostPlayingAt(table) || request.Seat > 1)
            {
                Reject(owner, request.PredictionId, "table or seat is unavailable");
                return;
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
                Reject(owner, request.PredictionId,
                    "requested seat must be free and the customer seat occupied");
                return;
            }

            var maxDeck = GameInstance.GetMaxDeckCardCount();
            if (request.DeckCardCount < 1 || request.DeckCardCount > maxDeck)
            {
                Reject(owner, request.PredictionId, "invalid deck size");
                return;
            }

            if (string.IsNullOrEmpty(request.MatchId) || request.MatchId.Length > 64)
            {
                Reject(owner, request.PredictionId, "invalid match id");
                return;
            }

            if (!PlayTableInterop.ReserveSeat(table, request.Seat, out var seatBefore))
            {
                Reject(owner, request.PredictionId, "reservation race");
                return;
            }

            var reservation = new Reservation
            {
                Table = table,
                SeatBefore = seatBefore,
                Match = new PlayTableMatchEntry
                {
                    MatchId = request.MatchId,
                    OwnerConn = owner,
                    TableKey = request.TableKey,
                    Seat = request.Seat,
                    SideA = request.SideA,
                    Phase = PlayTableMatchEntry.StateReserved,
                },
            };
            _reservations.Add(request.TableKey, reservation);
            NotifyTableChanged(table, request.PredictionId, false);
            PublishMatchUpsert(reservation.Match, request.PredictionId);
        }

        private void CancelMatch(PlayTableMatchIntentMessage request, int owner)
        {
            if (!TryGetOwnedReservation(request, owner, out var reservation))
            {
                Reject(owner, request.PredictionId, "cancel is not authenticated");
                return;
            }

            Release(reservation.Match.TableKey, "cancelled", request.PredictionId);
        }

        private void MarkMatchStarted(PlayTableMatchIntentMessage request, int owner)
        {
            if (!TryGetOwnedReservation(request, owner, out var reservation))
            {
                Reject(owner, request.PredictionId, "match is not owned by sender");
                return;
            }

            reservation.Match.Phase = PlayTableMatchEntry.StateStarted;
            PublishMatchUpsert(reservation.Match, request.PredictionId);
        }

        private void ReleaseMatch(PlayTableMatchIntentMessage request, int owner)
        {
            if (!TryGetOwnedReservation(request, owner, out var reservation))
            {
                Reject(owner, request.PredictionId, "match release is not authenticated");
                return;
            }

            Release(reservation.Match.TableKey, "gameplay-finished", request.PredictionId);
        }

        private bool TryGetOwnedReservation(PlayTableMatchIntentMessage request, int owner,
            out Reservation reservation)
            => _reservations.TryGetValue(request.TableKey, out reservation)
                && reservation.Match.OwnerConn == owner
                && reservation.Match.MatchId == request.MatchId;

        private bool Release(int tableKey, string reason, Guid predictionId)
        {
            if (!_reservations.TryGetValue(tableKey, out var reservation))
            {
                return false;
            }

            _reservations.Remove(tableKey);
            PlayTableInterop.RestoreReservedSeat(reservation.Table, reservation.SeatBefore,
                reservation.Match.Seat);
            CoopPlugin.Log.LogInfo("play-table match released table=" + tableKey + " reason=" + reason);
            NotifyTableChanged(reservation.Table, predictionId, false);
            PublishMatchRelease(reservation.Match, predictionId);
            return true;
        }

        private void Reject(int connectionId, Guid predictionId, string reason)
        {
            CoopPlugin.Log.LogWarning("play-table intent rejected for connection=" + connectionId
                + ": " + reason);
            if (predictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, connectionId, predictionId);
            }
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
                message.Tables.Add(BuildTableEntry(tables[i]));
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
            if (connectionId <= 0 || !_context.InGame())
            {
                return;
            }

            var baseline = BuildBaseline();
            if (baseline != null)
            {
                _context.Send(connectionId, baseline);
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
            if (!_shutdown && _context.InGame())
            {
                _context.Broadcast(delta);
            }
        }

        private void NotifyTableChanged(InteractablePlayTable table, Guid predictionId,
            bool force)
        {
            if (table == null || !PlayTablePlacementInterop.TryGetTableKey(table, out var key))
            {
                return;
            }

            var current = BuildTableEntry(table);
            if (!_knownVisuals.TryGetValue(key, out var old))
            {
                PublishVisual(CreateTableDelta(current, predictionId));
            }
            else
            {
                if (force || old.Occupied != current.Occupied || old.Boxed != current.Boxed)
                {
                    PublishVisual(CreateTableDelta(current, predictionId));
                }
                else
                {
                    for (var seat = 0; seat < current.Seats.Count; seat++)
                    {
                        if (seat >= old.Seats.Count || !SameSeat(old.Seats[seat], current.Seats[seat]))
                        {
                            PublishVisual(new PlayTableVisualDeltaMessage
                            {
                                PredictionId = predictionId,
                                Operation = PlayTableVisualDeltaMessage.SeatUpdate,
                                TableKey = key,
                                Seat = (byte)seat,
                                SeatState = CopySeat(current.Seats[seat]),
                            });
                        }
                    }
                }
            }

            _knownVisuals[key] = current;
        }

        private void NotifyTableRemoved(InteractablePlayTable table)
        {
            if (table == null || !PlayTablePlacementInterop.TryGetTableKey(table, out var key))
            {
                return;
            }

            Release(key, "table-removed", Guid.Empty);
            if (_knownVisuals.Remove(key))
            {
                PublishVisual(new PlayTableVisualDeltaMessage
                {
                    Operation = PlayTableVisualDeltaMessage.TableRemove,
                    TableKey = key,
                });
            }
        }

        private void RefreshVisuals(bool publish)
        {
            var current = new Dictionary<int, PlayTableEntry>();
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            var count = Math.Min(tables?.Count ?? 0, MaxTables);
            for (var i = 0; tables != null && i < count; i++)
            {
                var table = tables[i];
                if (table != null && PlayTablePlacementInterop.TryGetTableKey(table, out var key))
                {
                    current[key] = BuildTableEntry(table);
                }
            }

            var removed = new List<int>();
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
                        TableKey = removed[i],
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

        private void PublishMatchUpsert(PlayTableMatchEntry match, Guid predictionId)
        {
            _context.Broadcast(new PlayTableMatchDeltaMessage
            {
                PredictionId = predictionId,
                Operation = PlayTableMatchDeltaMessage.Upsert,
                MatchId = match.MatchId,
                TableKey = match.TableKey,
                Match = CopyMatch(match),
            });
        }

        private void PublishMatchRelease(PlayTableMatchEntry match, Guid predictionId)
        {
            _context.Broadcast(new PlayTableMatchDeltaMessage
            {
                PredictionId = predictionId,
                Operation = PlayTableMatchDeltaMessage.Release,
                MatchId = match.MatchId,
                TableKey = match.TableKey,
            });
        }

        private static PlayTableVisualDeltaMessage CreateTableDelta(PlayTableEntry entry,
            Guid predictionId)
            => new()
            {
                PredictionId = predictionId,
                Operation = PlayTableVisualDeltaMessage.TableUpsert,
                TableKey = entry.TableKey,
                Occupied = entry.Occupied,
                Boxed = entry.Boxed,
                Seats = CopySeats(entry.Seats),
            };

        private static PlayTableEntry BuildTableEntry(InteractablePlayTable table)
        {
            var sets = table?.m_TableGameItemSetList;
            var seats = Math.Min(sets?.Count ?? 0, MaxSeats);
            PlayTablePlacementInterop.TryGetTableKey(table, out var key);
            var entry = new PlayTableEntry
            {
                TableKey = key,
                Occupied = table != null && table.GetCurrentPlayerCount() > 0,
                Boxed = table != null && table.GetIsBoxedUp(),
            };
            for (var seat = 0; seat < seats; seat++)
            {
                var set = sets[seat];
                var active = set != null && set.gameObject.activeSelf;
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
                TableKey = source.TableKey,
                Seat = source.Seat,
                SideA = source.SideA,
                Phase = source.Phase,
            };

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && _context.InGame() && context?.Connection != null
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
        }

        private void Patch(System.Reflection.MethodInfo original, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("play-table host patch target missing");
                return;
            }

            _harmony.Patch(original, prefix, postfix);
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

                if (PlayTablePlacementInterop.TryGetTableKey(__instance, out var key))
                {
                    host.Release(key, "table-stopped", Guid.Empty);
                }

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
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _reservations.Clear();
            _knownVisuals.Clear();
            _fullyJoined.Clear();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
