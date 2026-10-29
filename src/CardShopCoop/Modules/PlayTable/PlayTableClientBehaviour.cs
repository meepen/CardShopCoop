using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>Guest-side table mirror and predictive table/match interaction sender.</summary>
    [ClientBehaviour]
    public sealed class PlayTableClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "play-table";

        /// <summary>One action lane per live table. A rollback/replay of one table's kick must not
        /// undo and replay another table's pending action.</summary>
        private static string ScopeFor(InteractablePlayTable table)
            => table == null ? PredictionScope : PredictionScope + ":" + table.GetInstanceID();
        private const int TableLevelSeat = -1;
        private static PlayTableClientBehaviour _active;
        private static int _nextMatchNonce;
        private readonly Dictionary<InteractablePlayTable, PlayTableEntry> _visualEntries = new();
        private readonly Dictionary<string, PlayTableMatchEntry> _matchesById = new();
        private readonly Dictionary<InteractablePlayTable, string> _matchByTable = new();
        private readonly Dictionary<SeatVisualKey, AppliedVisual> _applied = new();
        private readonly Dictionary<InteractablePlayTable, LaunchState> _launches = new();
        private readonly Dictionary<SeatVisualKey, PlayTableVisualDeltaMessage> _pendingVisuals = new();

        private PlayTableBaselineMessage _latestBaseline;
        private bool _shutdown;
        private bool _initialized;
        private bool _suppressEndGameGift;

        private sealed class AppliedVisual
        {
            internal InteractablePlayTable Table;
            internal TableGameItemSet Set;
            internal SeatState State;
        }

        /// <summary>Reference-keyed (table, seat) pair for per-table visual bookkeeping. A list
        /// position or placement key would shift or go stale when furniture moves.</summary>
        private readonly struct SeatVisualKey : IEquatable<SeatVisualKey>
        {
            internal readonly InteractablePlayTable Table;
            internal readonly int Seat;

            internal SeatVisualKey(InteractablePlayTable table, int seat)
            {
                Table = table;
                Seat = seat;
            }

            public bool Equals(SeatVisualKey other)
                => ReferenceEquals(Table, other.Table) && Seat == other.Seat;

            public override bool Equals(object obj)
                => obj is SeatVisualKey other && Equals(other);

            public override int GetHashCode()
                => ((Table == null ? 0 : RuntimeHelpers.GetHashCode(Table)) * 397) ^ Seat;
        }

        private struct SeatState
        {
            internal bool Active;
            internal bool PlayerSeat;
            internal int PlayMat;
            internal int DeckBox;
            internal int Comic;
        }

        private sealed class BeforeClickSnapshot
        {
            internal byte Seat;
            internal PlayTableSeatSnapshot Values;
        }

        private sealed class TableActionCapture
        {
            internal InteractablePlayTable Table;
            internal bool WasOccupied;
            internal bool WasBoxed;
            internal PlayTableEntry Before;
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        private sealed class LaunchState
        {
            internal InteractablePlayTable Table;
            internal PlayTableSeatSnapshot Before;
            internal PlayTableMatchEntry Match;
            internal bool Entered;
            internal float StartedAt;
        }

        internal static PlayTableClientBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _active = this;
            PlacementApi.StructureChanged += OnPlacementStructureChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyPatches();
            _initialized = true;
        }

        [MessageHandler]
        private void HandleBaseline(MessageContext _, PlayTableBaselineMessage message)
        {
            _latestBaseline = message;
            ApplyLatestBaseline();
        }

        [MessageHandler]
        private void HandleVisualDelta(MessageContext _, PlayTableVisualDeltaMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            if (message.Table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped visual delta with no live table object.");
                return;
            }

            if (message.Operation != PlayTableVisualDeltaMessage.SeatUpdate)
            {
                SupersedePendingVisuals(message.Table);
            }

            if (!TryApplyVisualDelta(message))
            {
                DeferVisualDelta(message);
            }
        }

        [MessageHandler]
        private void HandleMatchDelta(MessageContext _, PlayTableMatchDeltaMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            if (message.Table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped match delta with no live table object.");
                return;
            }

            ApplyMatchDelta(message);
        }

        private void ApplyLatestBaseline()
        {
            if (_latestBaseline == null || PlayTableInterop.Tables(PlayTableInterop.FindShelfManager())
                == null)
            {
                return;
            }

            _visualEntries.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            for (var i = 0; i < _latestBaseline.Tables.Count; i++)
            {
                var entry = CopyEntry(_latestBaseline.Tables[i]);
                if (entry?.Table == null)
                {
                    CoopPlugin.Log.LogWarning(
                        "play-table: dropped baseline entry with no live table object.");
                    continue;
                }

                _visualEntries[entry.Table] = entry;
            }

            for (var i = 0; i < _latestBaseline.Matches.Count; i++)
            {
                AddMatch(_latestBaseline.Matches[i]);
            }

            _latestBaseline = null;
            ReconcileLaunches();
            ApplyVisualMirrors();
            ApplyPendingVisuals();
        }

        private void OnPlacementStructureChanged(int _)
        {
            if (!_shutdown)
            {
                ApplyPendingVisuals();
                ReconcileLaunches();
                ApplyVisualMirrors();
            }
        }

        private void ApplyPendingVisuals()
        {
            var pending = new List<PlayTableVisualDeltaMessage>(_pendingVisuals.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Operation != PlayTableVisualDeltaMessage.SeatUpdate
                    && TryApplyVisualDelta(pending[i]))
                {
                    _pendingVisuals.Remove(PendingKey(pending[i]));
                }
            }

            pending = new List<PlayTableVisualDeltaMessage>(_pendingVisuals.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Operation == PlayTableVisualDeltaMessage.SeatUpdate
                    && TryApplyVisualDelta(pending[i]))
                {
                    _pendingVisuals.Remove(PendingKey(pending[i]));
                }
            }
        }

        private void DeferVisualDelta(PlayTableVisualDeltaMessage message)
        {
            var key = PendingKey(message);
            if (_pendingVisuals.TryGetValue(key, out var previous))
            {
                ActionLedger.Ack(previous.ActionId);
            }


            _pendingVisuals[key] = message;
        }

        private void SupersedePendingVisuals(InteractablePlayTable table)
        {
            var superseded = new List<SeatVisualKey>();
            foreach (var pair in _pendingVisuals)
            {
                if (ReferenceEquals(pair.Key.Table, table))
                {
                    ActionLedger.Ack(pair.Value.ActionId);
                    superseded.Add(pair.Key);
                }
            }

            for (var i = 0; i < superseded.Count; i++)
            {
                _pendingVisuals.Remove(superseded[i]);
            }

        }

        private static SeatVisualKey PendingKey(PlayTableVisualDeltaMessage message)
            => new(message.Table,
                message.Operation == PlayTableVisualDeltaMessage.SeatUpdate
                    ? message.Seat : TableLevelSeat);

        private bool TryApplyVisualDelta(PlayTableVisualDeltaMessage message)
        {
            var table = message.Table;
            if (table == null)
            {
                return false;
            }

            if (message.Operation == PlayTableVisualDeltaMessage.SeatUpdate
                && !_visualEntries.ContainsKey(table))
            {
                return false;
            }

            using (Writes.Enter())
            {
                // The visual delta carries the host's resulting PlayTableEntry (occupied/boxed and
                // per-seat state) built from the host's table. The actor's optimistic game path
                // (kick/box/seat reserve) does not fold that into the _visualEntries mirror, so
                // AckOrApply retired the actor's own prediction and returned, leaving the mirror
                // stale and ApplyVisualMirrors re-rendering the old seats. Confirm applies it.
                ActionLedger.Confirm(message.ActionId,
                    () => ApplyVisualDelta(message, table));
            }

            ApplyVisualMirrors();
            return true;
        }

        private void ApplyVisualDelta(PlayTableVisualDeltaMessage message,
            InteractablePlayTable table)
        {
            switch (message.Operation)
            {
                case PlayTableVisualDeltaMessage.TableRemove:
                    _visualEntries.Remove(table);
                    ClearAppliedTable(table);
                    return;
                case PlayTableVisualDeltaMessage.TableUpsert:
                    _visualEntries[table] = new PlayTableEntry
                    {
                        Table = table,
                        Occupied = message.Occupied,
                        Boxed = message.Boxed,
                        Seats = CopySeats(message.Seats),
                    };
                    return;
                case PlayTableVisualDeltaMessage.SeatUpdate:
                    if (!_visualEntries.TryGetValue(table, out var entry))
                    {
                        return;
                    }

                    while (entry.Seats.Count <= message.Seat)
                    {
                        entry.Seats.Add(new PlayTableSeatEntry());
                    }

                    entry.Seats[message.Seat] = CopySeat(message.SeatState);
                    return;
                default:
                    throw new InvalidOperationException("unknown play-table visual delta operation");
            }
        }

        private void ApplyMatchDelta(PlayTableMatchDeltaMessage message)
        {
            // The actor's optimistic match start only records a local launch; it never adds the
            // match to _matchesById/_matchByTable (ReconcileLaunches waits for the host copy). The
            // host's Upsert/Release is the authoritative registration, so AckOrApply retired the
            // actor's own prediction, skipped AddMatch and left the match unregistered on its own
            // table forever. Confirm retires the prediction and always folds the host state; both
            // operations are idempotent for a remote or host-local delta.
            ActionLedger.Confirm(message.ActionId, () =>
            {
                if (message.Operation == PlayTableMatchDeltaMessage.Upsert)
                {
                    AddMatch(message.Match);
                }
                else if (message.Operation == PlayTableMatchDeltaMessage.Release)
                {
                    if (_matchesById.TryGetValue(message.MatchId, out var prior))
                    {
                        _matchesById.Remove(message.MatchId);
                        _matchByTable.Remove(prior.Table);
                        RemoveLaunchLocal(prior.Table);
                    }
                }
                else
                {
                    throw new InvalidOperationException("unknown play-table match delta operation");
                }

                ReconcileLaunches();
            });
        }

        internal bool HasActiveMatch(InteractablePlayTable table)
            => table != null && _matchByTable.ContainsKey(table);

        internal bool TryGetMatch(InteractablePlayTable table, out PlayTableMatchEntry entry)
        {
            entry = null;
            return table != null
                && _matchByTable.TryGetValue(table, out var matchId)
                && _matchesById.TryGetValue(matchId, out entry);
        }

        private void ReconcileLaunches()
        {
            foreach (var pair in new List<InteractablePlayTable>(_launches.Keys))
            {
                var launch = _launches[pair];
                if (!TryGetMatch(pair, out var live))
                {
                    // The host may not have mirrored our own sit yet; keep the local launch until
                    // it is confirmed, rejected (UndoLaunch), or superseded by another match.
                    continue;
                }

                if (live.OwnerConn != CoopCore.LocalConnectionId
                    || live.MatchId != launch.Match.MatchId)
                {
                    RemoveLaunchLocal(pair);
                    continue;
                }

                launch.Match = live;
            }
        }

        private void HandleFinishLeave(PlayTableGame game)
        {
            if (!TryGetLaunch(game, out var launch))
            {
                return;
            }

            SendRelease(launch);
        }

        private void SendRelease(LaunchState launch)
        {
            if (launch == null)
            {
                return;
            }

            // Vanilla already finished leaving the table; drop the local lease now and record the
            // one release it performed.
            var table = launch.Match.Table;
            if (table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped release for a match with no live table object.");
                return;
            }

            ActionLedger.Submit(
                ScopeFor(table),
                actionId => SendReleaseMatch(launch.Match, actionId),
                () =>
                {
                    RemoveMatchLocal(launch.Match);
                    _launches.Remove(table);
                },
                () =>
                {
                    AddMatch(launch.Match);
                    RestoreLaunch(launch);
                });
            RemoveMatchLocal(launch.Match);
            _launches.Remove(table);
        }

        private void SendStartMatch(PlayTableMatchEntry match, Guid actionId)
        {
            RuntimeContext.Send(1, new StartTableMatchIntent
            {
                ActionId = actionId,
                MatchId = match.MatchId,
                Table = match.Table,
                Seat = match.Seat,
                SideA = match.SideA,
                DeckCardCount = PlayTableInterop.DeckCardCount(),
            });
        }

        private void SendCancelMatch(PlayTableMatchEntry match, Guid actionId)
        {
            RuntimeContext.Send(1, new CancelTableMatchIntent
            {
                ActionId = actionId,
                MatchId = match.MatchId,
                Table = match.Table,
            });
        }

        private void SendMatchStarted(PlayTableMatchEntry match, Guid actionId)
        {
            RuntimeContext.Send(1, new MarkTableMatchStartedIntent
            {
                ActionId = actionId,
                MatchId = match.MatchId,
                Table = match.Table,
            });
        }

        private void SendReleaseMatch(PlayTableMatchEntry match, Guid actionId)
        {
            RuntimeContext.Send(1, new ReleaseTableMatchIntent
            {
                ActionId = actionId,
                MatchId = match.MatchId,
                Table = match.Table,
            });
        }

        private bool TryGetLaunch(PlayTableGame game, out LaunchState launch)
        {
            launch = null;
            var table = PlayTableInterop.CurrentTable(game);
            return table != null && _launches.TryGetValue(table, out launch);
        }

        private void RequestMatchObserved(InteractablePlayTable table, BeforeClickSnapshot before)
        {
            if (table == null || before == null)
            {
                PlayTableInterop.RestoreSeat(table, before?.Values, before?.Seat ?? 0);
                return;
            }

            var match = new PlayTableMatchEntry
            {
                MatchId = "playtable-" + CoopCore.LocalConnectionId + "-" + NextMatchNonce(),
                OwnerConn = CoopCore.LocalConnectionId,
                Table = table,
                Seat = before.Seat,
                SideA = before.Seat == 0,
                Phase = PlayTableMatchEntry.StateStarted,
            };
            var launch = new LaunchState
            {
                Table = table,
                Before = before.Values,
                Match = match,
                Entered = true,
                StartedAt = Time.realtimeSinceStartup,
            };
            _launches[table] = launch;

            // Vanilla seated this player and entered the game; record the one match start it
            // performed. A host rejection rolls the entry back, a host accept retires it.
            ActionLedger.Submit(
                ScopeFor(table),
                actionId => SendStartMatch(match, actionId),
                () => ReplayLaunch(table, before.Seat, launch),
                () => UndoLaunch(table, launch));

            // Vanilla already entered the game, so notify the host the lease is live and refresh
            // its reservation TTL. A notification needs no rollback and is not a second action.
            SendMatchStarted(match, Guid.Empty);
        }

        private static void ReplayLaunch(InteractablePlayTable table, byte seat, LaunchState launch)
        {
            if (table == null)
            {
                return;
            }

            PlayTableInterop.ReserveSeat(table, seat, out _);
            PlayCardGameManager.SetPlayTable(table, launch.Match.SideA);
        }

        private void UndoLaunch(InteractablePlayTable table, LaunchState launch)
        {
            RemoveMatchLocal(launch.Match);
            _launches.Remove(table);
            // Vanilla's own leave path is the only thing that unwinds a play-table entry (camera,
            // UI, table state). The host's authoritative release republishes the table afterwards.
            var manager = SceneRef<PlayCardGameManager>.Get();
            if (manager?.m_PlayTableGame != null && PlayTableInterop.HasEnteredTable(launch.Table))
            {
                // This is a rollback, not the actor's own game result: vanilla's leave path
                // evaluates an end-game gift and advances duel-win/RNG state inside its synchronous
                // prefix. Suppress exactly that reward so an undone entry mints nothing, while
                // normal play and the host's releases keep evaluating it.
                _suppressEndGameGift = true;
                try
                {
                    manager.m_PlayTableGame.FinishLeaveGame(false);
                }
                finally
                {
                    _suppressEndGameGift = false;
                }

                return;
            }

            PlayTableInterop.RestoreReservedSeat(launch.Table, launch.Before, launch.Match.Seat);
        }

        private void SendKickIntent(InteractablePlayTable table, PlayTableEntry before)
        {
            // Vanilla already ran StopTableGame on the occupied table; record the one kick it made.
            ActionLedger.Submit(
                ScopeFor(table),
                actionId => SendKick(table, actionId),
                () => ApplyLocalKick(table),
                () => RestoreEntry(table, before));
        }

        private void SendBoxIntent(InteractablePlayTable table, bool wasBoxed, Vector3 position,
            Quaternion rotation)
        {
            if (table == null)
            {
                return;
            }

            // Vanilla already boxed the table. The package it created is a view of this table and
            // shares its session Guid, so the intent names only the live table: the host boxes its
            // own copy under the same identity and hands the hold to the requester.
            ActionLedger.Submit(
                ScopeFor(table),
                actionId => RuntimeContext.Send(1, new FurnitureBoxUpRequestMessage
                {
                    ActionId = actionId,
                    ObjectType = table.m_ObjectType,
                    Position = table.transform.position,
                    Entity = table,
                }),
                () =>
                {
                    using (Writes.Enter())
                    {
                        table.BoxUpObject(true);
                    }
                },
                () =>
                {
                    if (!wasBoxed && table.GetIsBoxedUp())
                    {
                        using (Writes.Enter())
                        {
                            PlacementInterop.PlaceBoxedObject(table, position, rotation);
                        }
                    }
                });
        }

        private void SendKick(InteractablePlayTable table, Guid actionId)
        {
            RuntimeContext.Send(1, new KickTableIntent
            {
                ActionId = actionId,
                Table = table,
            });
        }

        private void RemoveLaunchLocal(InteractablePlayTable table)
        {
            if (table == null || !_launches.TryGetValue(table, out var launch))
            {
                return;
            }

            _launches.Remove(table);
            if (!launch.Entered)
            {
                PlayTableInterop.RestoreSeat(launch.Table, launch.Before, launch.Match.Seat);
            }
        }

        private void RestoreLaunch(LaunchState launch)
        {
            if (launch == null || launch.Match.Table == null)
            {
                return;
            }

            _launches[launch.Match.Table] = launch;
            if (!launch.Entered)
            {
                PlayTableInterop.ReserveSeat(launch.Table, launch.Match.Seat, out _);
            }
        }

        private void RemoveMatchLocal(PlayTableMatchEntry match)
        {
            if (match == null || match.Table == null)
            {
                return;
            }

            _matchesById.Remove(match.MatchId);
            if (_matchByTable.TryGetValue(match.Table, out var matchId)
                && matchId == match.MatchId)
            {
                _matchByTable.Remove(match.Table);
            }
        }

        private void ApplyVisualMirrors()
        {
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            if (tables == null)
            {
                return;
            }

            ClearMirrors();
            foreach (var pair in _visualEntries)
            {
                var table = pair.Key;
                if (table == null || PlayTableInterop.IsLocalGameplayTable(table, HasLocalLaunch))
                {
                    continue;
                }

                var sets = table.m_TableGameItemSetList;
                var seats = pair.Value.Seats;
                for (var seat = 0; seat < seats.Count && seat < sets.Count; seat++)
                {
                    ApplySeat(seat, table, sets[seat], new SeatState
                    {
                        Active = seats[seat].Active,
                        PlayerSeat = seats[seat].PlayerSeat,
                        PlayMat = (int)seats[seat].PlayMat,
                        DeckBox = (int)seats[seat].DeckBox,
                        Comic = (int)seats[seat].Comic,
                    });
                }
            }
        }

        private void ApplySeat(int seat, InteractablePlayTable table,
            TableGameItemSet set, SeatState want)
        {
            var visualKey = new SeatVisualKey(table, seat);
            _applied.TryGetValue(visualKey, out var applied);
            if (applied?.Set != null && (!ReferenceEquals(applied.Set, set)
                || !ReferenceEquals(applied.Table, table))
                && !PlayTableInterop.IsLocalGameplayTable(applied.Table, HasLocalLaunch))
            {
                applied.Set.gameObject.SetActive(false);
            }

            if (want.Active)
            {
                set.SpecificSetup(new TableGameItemSetData
                {
                    playMatType = (EItemType)want.PlayMat,
                    deckBoxType = (EItemType)want.DeckBox,
                    comicBookType = (EItemType)want.Comic,
                });
                set.gameObject.SetActive(true);
            }
            else
            {
                set.gameObject.SetActive(false);
            }

            ApplyPlayerSeat(table, seat, want.PlayerSeat);
            _applied[visualKey] = new AppliedVisual { Table = table, Set = set, State = want };
        }

        private void ApplyPlayerSeat(InteractablePlayTable table, int seat, bool playerSeat)
        {
            if (PlayTableInterop.IsRosterManaged(table, HasActiveMatch, HasLocalLaunch))
            {
                return;
            }

            table.m_IsPlayerSeat[seat] = playerSeat;
        }

        private void ClearMirrors()
        {
            foreach (var pair in _applied)
            {
                if (pair.Value.State.Active && pair.Value.Set != null
                    && !PlayTableInterop.IsLocalGameplayTable(pair.Value.Table, HasLocalLaunch))
                {
                    pair.Value.Set.gameObject.SetActive(false);
                }
            }

            _applied.Clear();
        }

        private void ClearAppliedTable(InteractablePlayTable table)
        {
            var remove = new List<SeatVisualKey>();
            foreach (var pair in _applied)
            {
                if (ReferenceEquals(pair.Key.Table, table))
                {
                    pair.Value.Set?.gameObject.SetActive(false);

                    remove.Add(pair.Key);
                }
            }

            for (var i = 0; i < remove.Count; i++)
            {
                _applied.Remove(remove[i]);
            }
        }

        private bool HasLocalLaunch(InteractablePlayTable table)
            => table != null && _launches.ContainsKey(table);

        private static int NextMatchNonce()
        {
            if (_nextMatchNonce == int.MaxValue)
            {
                _nextMatchNonce = 0;
            }

            return ++_nextMatchNonce;
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            _latestBaseline = null;
            _visualEntries.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            ClearPendingVisuals();
            foreach (var table in new List<InteractablePlayTable>(_launches.Keys))
            {
                RemoveLaunchLocal(table);
            }

            ClearMirrors();
        }

        /// <summary>The host owns every NPC seated at a table, so this peer's own
        /// <c>GetCurrentPlayerCount</c> never rises for a host-simulated customer. The host's
        /// object-attached game state is the authority; the visual entry is only a bootstrap
        /// fallback for the short window before that value is bound.</summary>
        private bool IsHostOccupied(InteractablePlayTable table)
        {
            if (table == null)
            {
                return false;
            }

            var state = table.GetExtendedInformation<PlayTableGameState>();
            if (state != null)
            {
                return state.PlayerCount > 0;
            }

            return _visualEntries.TryGetValue(table, out var entry) && entry.Occupied;
        }

        private static bool IsOccupied(InteractablePlayTable table)
        {
            if (table == null)
            {
                return false;
            }

            if (table.GetCurrentPlayerCount() > 0)
            {
                return true;
            }

            var client = _active;
            return client != null && client.IsHostOccupied(table);
        }

        private static PlayTableEntry CaptureEntry(InteractablePlayTable table)
        {
            var sets = table?.m_TableGameItemSetList;
            var entry = new PlayTableEntry
            {
                Table = table,
                Occupied = IsOccupied(table),
                Boxed = table != null && table.GetIsBoxedUp(),
            };
            for (var i = 0; sets != null && i < sets.Count; i++)
            {
                var set = sets[i];
                var active = set != null && set.gameObject.activeSelf;
                var data = set?.m_TableGameItemSetData;
                entry.Seats.Add(new PlayTableSeatEntry
                {
                    Active = active,
                    PlayerSeat = PlayTableInterop.IsPlayerSeat(table, i),
                    PlayMat = active && data != null ? data.playMatType : default,
                    DeckBox = active && data != null ? data.deckBoxType : default,
                    Comic = active && data != null ? data.comicBookType : default,
                });
            }

            return entry;
        }

        private void ApplyLocalKick(InteractablePlayTable table)
        {
            using (Writes.Enter())
            {
                PlayTableInterop.InvokeStopTableGame(table);
            }
        }

        private void RestoreEntry(InteractablePlayTable table, PlayTableEntry entry)
        {
            // A table the visual stream already removed must not be resurrected by an action
            // rollback; there is no mirror left to render.
            if (table == null || entry == null || !_visualEntries.ContainsKey(table))
            {
                return;
            }

            for (var i = 0; i < entry.Seats.Count
                && i < table.m_TableGameItemSetList.Count; i++)
            {
                var set = table.m_TableGameItemSetList[i];
                var seat = entry.Seats[i];
                if (seat.Active)
                {
                    set.SpecificSetup(new TableGameItemSetData
                    {
                        playMatType = seat.PlayMat,
                        deckBoxType = seat.DeckBox,
                        comicBookType = seat.Comic,
                    });
                }

                set.gameObject.SetActive(seat.Active);
                if (i < table.m_IsPlayerSeat.Count)
                {
                    table.m_IsPlayerSeat[i] = seat.PlayerSeat;
                }
            }

            _visualEntries[table] = CopyEntry(entry);
        }

        private void AddMatch(PlayTableMatchEntry match)
        {
            if (match?.Table == null)
            {
                CoopPlugin.Log.LogWarning(
                    "play-table: dropped match entry with no live table object.");
                return;
            }

            var copy = CopyMatch(match);
            _matchesById[copy.MatchId] = copy;
            _matchByTable[copy.Table] = copy.MatchId;
        }

        private static PlayTableEntry CopyEntry(PlayTableEntry source)
            => source == null ? null : new PlayTableEntry
            {
                Table = source.Table,
                Occupied = source.Occupied,
                Boxed = source.Boxed,
                Seats = CopySeats(source.Seats),
            };

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
            => source == null ? null : new PlayTableMatchEntry
            {
                MatchId = source.MatchId,
                OwnerConn = source.OwnerConn,
                Table = source.Table,
                Seat = source.Seat,
                SideA = source.SideA,
                Phase = source.Phase,
            };

        private void ApplyPatches()
        {
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "StartMoveObject"),
                new HarmonyMethod(typeof(MovePatch), nameof(MovePatch.Prefix)),
                new HarmonyMethod(typeof(MovePatch), nameof(MovePatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "BoxUpObject"),
                new HarmonyMethod(typeof(BoxPatch), nameof(BoxPatch.Prefix)),
                new HarmonyMethod(typeof(BoxPatch), nameof(BoxPatch.Postfix)));
            Patch(AccessTools.Method(typeof(PlayTableGame), "FinishLeaveGame", new[] { typeof(bool) }),
                null, new HarmonyMethod(typeof(FinishLeavePatch), nameof(FinishLeavePatch.Postfix)));
            Patch(AccessTools.Method(typeof(PlayTableGame), "EvaluateEndGameGift"),
                new HarmonyMethod(typeof(EndGameGiftPatch), nameof(EndGameGiftPatch.Prefix)), null);
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp"),
                new HarmonyMethod(typeof(SeatPatch), nameof(SeatPatch.Prefix)),
                new HarmonyMethod(typeof(SeatPatch), nameof(SeatPatch.Postfix)));
            Patch(AccessTools.Method(typeof(ShelfManager), "InitPlayTable",
                    new[] { typeof(InteractablePlayTable) }), null,
                new HarmonyMethod(typeof(TableReadyPatch), nameof(TableReadyPatch.Postfix)));
            Patch(AccessTools.Method(typeof(PlayTableGame), "Awake"), null,
                new HarmonyMethod(typeof(TableReadyPatch), nameof(TableReadyPatch.Postfix)));
        }

        private void Patch(System.Reflection.MethodInfo original, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("play-table client patch target missing");
                return;
            }

            Harmony.Patch(original, prefix, postfix);
        }

        private static class MovePatch
        {
            // Vanilla's InteractablePlayTable.StartMoveObject stops an occupied (non-tournament)
            // game instead of picking the table up, so the player needs a second attempt once the
            // table is empty. A client cannot see the host's customers in m_CurrentPlayerCount.
            // The host publishes those two gate fields as object-attached game state, so this
            // prefix predicts from the same values vanilla would have read: it asks the host to
            // end the game and skips the pickup. Once that authority says the table is empty, the
            // next attempt moves the table, exactly like vanilla.
            //
            // A locally occupied table keeps vanilla's own stop; the postfix observes the one kick
            // it performed and forwards the intent.
            internal static bool Prefix(InteractablePlayTable __instance,
                out TableActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || __instance == null)
                {
                    return true;
                }

                // A rostered player (or a launch still awaiting the host) owns the table: the host
                // refuses to move or kick it, so leave the pickup untouched. Vanilla cannot enter
                // move mode while this player is seated anyway.
                if (PlayTableInterop.IsRosterManaged(__instance, client.HasActiveMatch,
                        table => client._launches.ContainsKey(table)))
                {
                    return false;
                }

                var state = __instance.GetExtendedInformation<PlayTableGameState>();
                var localOccupied = __instance.GetCurrentPlayerCount() > 0;
                var hostOccupied = localOccupied || client.IsHostOccupied(__instance);

                // A human player seat owns the table even in a transient state where its player
                // count has already been cleared; never let vanilla pick it up.
                if (state is { PlayerOccupied: true })
                {
                    return false;
                }

                if (!hostOccupied)
                {
                    return true;
                }

                // A locally occupied table may only be moved (after the kick) when the host agrees
                // the table is free. The host's state is the same truth vanilla's own gate used.
                var tournament = state != null
                    ? state.IsTournament : __instance.GetIsTournamentPlayTable();
                if (tournament)
                {
                    return false;
                }

                if (!localOccupied)
                {
                    CoopPlugin.Log.LogInfo(
                        "play-table: first move on a host-occupied table requested a kick, table="
                        + __instance.name + ".");
                    client.SendKickIntent(__instance, CaptureEntry(__instance));
                    return false;
                }

                __state = new TableActionCapture
                {
                    Table = __instance,
                    WasOccupied = true,
                    Before = CaptureEntry(__instance),
                };
                return true;
            }

            internal static void Postfix(TableActionCapture __state)
            {
                var client = _active;
                if (client == null || __state == null || !__state.WasOccupied
                    || __state.Table.GetCurrentPlayerCount() > 0)
                {
                    return;
                }

                client.SendKickIntent(__state.Table, __state.Before);
            }
        }

        private static class BoxPatch
        {
            // Capture-only: vanilla boxes the table, and the loader's saved-table path (holdBox
            // false) must always run. The postfix observes the one box the player performed.
            internal static void Prefix(InteractablePlayTable __instance, bool holdBox,
                out TableActionCapture __state)
            {
                __state = null;
                var client = _active;
                if (client == null || __instance == null
                    || ReentrancyGuard.SuppressedForLocalObservation(client) || !holdBox)
                {
                    return;
                }

                __state = new TableActionCapture
                {
                    Table = __instance,
                    WasBoxed = __instance.GetIsBoxedUp(),
                    Position = __instance.transform.position,
                    Rotation = __instance.transform.rotation,
                };
            }

            internal static void Postfix(TableActionCapture __state)
            {
                var client = _active;
                if (client == null || __state == null || __state.WasBoxed
                    || !__state.Table.GetIsBoxedUp())
                {
                    return;
                }

                client.SendBoxIntent(__state.Table, __state.WasBoxed, __state.Position,
                    __state.Rotation);
            }
        }

        private static class FinishLeavePatch
        {
            internal static void Postfix(PlayTableGame __instance)
                => _active?.HandleFinishLeave(__instance);
        }

        /// <summary>Skips the end-game reward only while the client is unwinding a rejected entry
        /// through the game's own leave path. <see cref="PlayTableGame.FinishLeaveGame"/> runs
        /// <c>DelayExit</c> synchronously through <c>EvaluateEndGameGift</c>, so the flag set by
        /// <see cref="UndoLaunch"/> is still held when the reward would be evaluated.</summary>
        private static class EndGameGiftPatch
        {
            internal static bool Prefix() => _active == null || !_active._suppressEndGameGift;
        }

        private static class SeatPatch
        {
            // Capture-only: vanilla seats the player and enters the game. The postfix observes the
            // one sit it performed; the host accepts the lease or rolls the local entry back.
            internal static void Prefix(InteractablePlayTable __instance,
                out BeforeClickSnapshot __state)
            {
                __state = null;
                var client = _active;
                if (client == null || __instance == null
                    || !PlayTableInterop.TryGetClickedSeat(__instance, out var seat))
                {
                    return;
                }

                __state = new BeforeClickSnapshot
                {
                    Seat = (byte)seat,
                    Values = PlayTableInterop.CaptureSeat(__instance, (byte)seat),
                };
            }

            internal static void Postfix(InteractablePlayTable __instance, BeforeClickSnapshot __state)
            {
                var client = _active;
                if (client == null || __instance == null || __state == null
                    || __state.Values.SeatOccupied
                    || !PlayTableInterop.IsPlayerSeat(__instance, __state.Seat)
                    || !PlayTableInterop.HasEnteredTable(__instance))
                {
                    return;
                }

                client.RequestMatchObserved(__instance, __state);
            }
        }

        private static class TableReadyPatch
        {
            internal static void Postfix()
            {
                _active?.ApplyLatestBaseline();
                _active?.ApplyPendingVisuals();
                _active?.ReconcileLaunches();
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            PlacementApi.StructureChanged -= OnPlacementStructureChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearMirrors();
            _launches.Clear();
            _visualEntries.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            ClearPendingVisuals();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();

        private void ClearPendingVisuals()
        {
            foreach (var delta in _pendingVisuals.Values)
            {
                ActionLedger.Ack(delta.ActionId);
            }


            _pendingVisuals.Clear();
        }
    }
}
