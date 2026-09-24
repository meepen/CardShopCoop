using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
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
        private static PlayTableClientBehaviour _active;
        private static int _nextMatchNonce;
        private readonly Dictionary<int, PlayTableEntry> _visualEntries = new();
        private readonly Dictionary<int, bool> _occupied = new();
        private readonly Dictionary<string, PlayTableMatchEntry> _matchesById = new();
        private readonly Dictionary<int, string> _matchByTable = new();
        private readonly Dictionary<long, AppliedVisual> _applied = new();
        private readonly Dictionary<int, LaunchState> _launches = new();
        private readonly Dictionary<int, LaunchState> _pendingStarts = new();
        private readonly Dictionary<string, PlayTableVisualDeltaMessage> _pendingVisuals = new();

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private PlayTableBaselineMessage _latestBaseline;
        private bool _shutdown;
        private int _applyingState;
        private int _applyingPrediction;

        private sealed class AppliedVisual
        {
            internal InteractablePlayTable Table;
            internal TableGameItemSet Set;
            internal SeatState State;
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

        private sealed class LaunchState
        {
            internal InteractablePlayTable Table;
            internal PlayTableSeatSnapshot Before;
            internal PlayTableMatchEntry Match;
            internal bool Entered;
            internal bool StartedSent;
            internal float StartedAt;
        }

        internal static PlayTableClientBehaviour Active => _active;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            PlacementApi.StructureChanged += OnPlacementStructureChanged;
            _context.Messages.RegisterAttributedHandlers(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("com.zwhit.cardshopcoop.play-table.client");
            ApplyPatches();
        }

        [MessageHandler(typeof(PlayTableBaselineMessage))]
        private void HandleBaseline(MessageContext _, PlayTableBaselineMessage message)
        {
            _latestBaseline = message;
            ApplyLatestBaseline();
        }

        [MessageHandler(typeof(PlayTableVisualDeltaMessage))]
        private void HandleVisualDelta(MessageContext _, PlayTableVisualDeltaMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            if (message.Operation != PlayTableVisualDeltaMessage.SeatUpdate)
            {
                SupersedePendingVisuals(message.TableKey);
            }

            if (!TryApplyVisualDelta(message))
            {
                DeferVisualDelta(message);
            }
        }

        [MessageHandler(typeof(PlayTableMatchDeltaMessage))]
        private void HandleMatchDelta(MessageContext _, PlayTableMatchDeltaMessage message)
        {
            if (_shutdown || message == null)
            {
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
            _occupied.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            for (var i = 0; i < _latestBaseline.Tables.Count; i++)
            {
                var entry = CopyEntry(_latestBaseline.Tables[i]);
                _visualEntries[entry.TableKey] = entry;
                _occupied[entry.TableKey] = entry.Occupied;
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
                    _pendingVisuals.Remove(VisualKey(pending[i]));
                }
            }

            pending = new List<PlayTableVisualDeltaMessage>(_pendingVisuals.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Operation == PlayTableVisualDeltaMessage.SeatUpdate
                    && TryApplyVisualDelta(pending[i]))
                {
                    _pendingVisuals.Remove(VisualKey(pending[i]));
                }
            }
        }

        private void DeferVisualDelta(PlayTableVisualDeltaMessage message)
        {
            var key = VisualKey(message);
            if (_pendingVisuals.TryGetValue(key, out var previous))
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            _pendingVisuals[key] = message;
        }

        private void SupersedePendingVisuals(int tableKey)
        {
            var superseded = new List<string>();
            foreach (var pair in _pendingVisuals)
            {
                if (pair.Value.TableKey == tableKey)
                {
                    PredictionApi.ConfirmSuperseded(pair.Value.PredictionId);
                    superseded.Add(pair.Key);
                }
            }

            for (var i = 0; i < superseded.Count; i++)
                _pendingVisuals.Remove(superseded[i]);
        }

        private static string VisualKey(PlayTableVisualDeltaMessage message)
            => message.Operation == PlayTableVisualDeltaMessage.SeatUpdate
                ? message.TableKey + ":seat:" + message.Seat
                : message.TableKey + ":table";

        private bool TryApplyVisualDelta(PlayTableVisualDeltaMessage message)
        {
            var table = PlacementApi.ResolveObjectByKey(message.TableKey) as InteractablePlayTable;
            if (message.Operation != PlayTableVisualDeltaMessage.TableRemove && table == null)
            {
                return false;
            }

            if (message.Operation == PlayTableVisualDeltaMessage.SeatUpdate
                && !_visualEntries.ContainsKey(message.TableKey))
            {
                return false;
            }

            _applyingState++;
            try
            {
                PredictionApi.ApplyAuthoritative(message.PredictionId,
                    () => ApplyVisualDelta(message, table));
            }
            finally
            {
                _applyingState--;
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
                    _visualEntries.Remove(message.TableKey);
                    _occupied.Remove(message.TableKey);
                    ClearAppliedTable(message.TableKey);
                    return;
                case PlayTableVisualDeltaMessage.TableUpsert:
                    _visualEntries[message.TableKey] = new PlayTableEntry
                    {
                        TableKey = message.TableKey,
                        Index = message.TableIndex,
                        Occupied = message.Occupied,
                        Boxed = message.Boxed,
                        Seats = CopySeats(message.Seats),
                    };
                    _occupied[message.TableKey] = message.Occupied;
                    return;
                case PlayTableVisualDeltaMessage.SeatUpdate:
                    if (!_visualEntries.TryGetValue(message.TableKey, out var entry))
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
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
            {
                if (message.Operation == PlayTableMatchDeltaMessage.Upsert)
                {
                    AddMatch(message.Match);
                    if (message.Match.OwnerConn == CoopCore.LocalConnectionId
                        && !_launches.ContainsKey(message.Match.TableKey)
                        && _pendingStarts.TryGetValue(message.Match.TableKey, out var pending))
                    {
                        _pendingStarts.Remove(message.Match.TableKey);
                        _launches[message.Match.TableKey] = pending;
                        if (!pending.Entered)
                        {
                            PlayTableInterop.ReserveSeat(pending.Table, pending.Match.Seat, out _);
                        }
                    }
                }
                else if (message.Operation == PlayTableMatchDeltaMessage.Release)
                {
                    if (_matchesById.TryGetValue(message.MatchId, out var prior))
                    {
                        _matchesById.Remove(message.MatchId);
                        _matchByTable.Remove(prior.TableKey);
                        RemoveLaunchLocal(prior.TableKey, false);
                    }
                }
                else
                {
                    throw new InvalidOperationException("unknown play-table match delta operation");
                }

                ReconcileLaunches();
            });
        }

        internal bool HasActiveMatch(int tableKey) => _matchByTable.ContainsKey(tableKey);

        internal bool TryGetMatch(int tableKey, out PlayTableMatchEntry entry)
        {
            if (_matchByTable.TryGetValue(tableKey, out var matchId)
                && _matchesById.TryGetValue(matchId, out entry))
            {
                return true;
            }

            entry = null;
            return false;
        }

        private void ReconcileLaunches()
        {
            foreach (var pair in new List<int>(_launches.Keys))
            {
                var launch = _launches[pair];
                if (!TryGetMatch(pair, out var live)
                    || live.OwnerConn != CoopCore.LocalConnectionId
                    || live.MatchId != launch.Match.MatchId)
                {
                    RemoveLaunchLocal(pair, false);
                    continue;
                }

                launch.Match = live;
                if (!launch.Entered)
                {
                    BeginAcceptedLaunch(pair, launch);
                }
            }
        }

        private void BeginAcceptedLaunch(int tableKey, LaunchState launch)
        {
            if (launch == null || launch.Table == null
                || !PlayTablePlacementInterop.TryGetTableKey(launch.Table, out var actualKey)
                || actualKey != tableKey || PlayTableInterop.HasEnteredTable(launch.Table))
            {
                RemoveLaunchLocal(tableKey, false);
                return;
            }

            var manager = SceneRef<PlayCardGameManager>.Get();
            if (manager?.m_PlayTableGame == null)
            {
                return;
            }

            PlayCardGameManager.SetPlayTable(launch.Table, launch.Match.SideA);
            MarkLaunchEntered(launch.Table);
        }

        private void MarkLaunchEntered(InteractablePlayTable table)
        {
            if (!PlayTablePlacementInterop.TryGetTableKey(table, out var tableKey)
                || !_launches.TryGetValue(tableKey, out var launch) || launch.Entered)
            {
                return;
            }

            if (!PlayTableInterop.HasEnteredTable(table))
            {
                RemoveLaunchLocal(tableKey, false);
                return;
            }

            launch.Entered = true;
            launch.StartedAt = Time.realtimeSinceStartup;
            var priorPhase = launch.Match.Phase;
            if (!launch.StartedSent)
            {
                PredictionApi.Predict(
                    PredictionScope,
                    predictionId => SendMatchIntent(PlayTableMatchIntentMessage.OpStarted,
                        launch.Match, predictionId),
                    () =>
                    {
                        launch.StartedSent = true;
                        launch.Match.Phase = PlayTableMatchEntry.StateStarted;
                        AddMatch(launch.Match);
                    },
                    () =>
                    {
                        launch.StartedSent = false;
                        launch.Match.Phase = priorPhase;
                        AddMatch(launch.Match);
                    });
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

            var tableKey = launch.Match.TableKey;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendMatchIntent(PlayTableMatchIntentMessage.OpRelease,
                    launch.Match, predictionId),
                () =>
                {
                    RemoveMatchLocal(launch.Match);
                    RemoveLaunchLocal(tableKey, false);
                },
                () =>
                {
                    AddMatch(launch.Match);
                    RestoreLaunch(launch);
                });
        }

        private void SendCancel(LaunchState launch)
        {
            if (launch == null)
            {
                return;
            }

            var tableKey = launch.Match.TableKey;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendMatchIntent(PlayTableMatchIntentMessage.OpCancel,
                    launch.Match, predictionId),
                () =>
                {
                    RemoveMatchLocal(launch.Match);
                    RemoveLaunchLocal(tableKey, false);
                },
                () =>
                {
                    AddMatch(launch.Match);
                    RestoreLaunch(launch);
                });
        }

        private void SendMatchIntent(byte op, PlayTableMatchEntry match, Guid predictionId,
            int deckCardCount = 0)
        {
            _context.Send(1, new PlayTableMatchIntentMessage
            {
                PredictionId = predictionId,
                Op = op,
                MatchId = match.MatchId,
                TableKey = match.TableKey,
                TableIndex = match.TableIndex,
                Seat = match.Seat,
                SideA = match.SideA,
                DeckCardCount = deckCardCount,
            });
        }

        private bool TryGetLaunch(PlayTableGame game, out LaunchState launch)
        {
            launch = null;
            var table = PlayTableInterop.CurrentTable(game);
            return PlayTablePlacementInterop.TryGetTableKey(table, out var tableKey)
                && _launches.TryGetValue(tableKey, out launch);
        }

        private void RequestMatch(InteractablePlayTable table, BeforeClickSnapshot before)
        {
            if (table == null || before == null
                || !PlayTablePlacementInterop.TryGetTableKey(table, out var tableKey))
            {
                PlayTableInterop.RestoreSeat(table, before?.Values, before?.Seat ?? 0);
                return;
            }

            var tableIndex = PlayTableInterop.TableIndex(PlayTableInterop.FindShelfManager(), table);
            var match = new PlayTableMatchEntry
            {
                MatchId = "playtable-" + CoopCore.LocalConnectionId + "-" + NextMatchNonce(),
                OwnerConn = CoopCore.LocalConnectionId,
                TableKey = tableKey,
                TableIndex = (byte)tableIndex,
                Seat = before.Seat,
                SideA = before.Seat == 0,
                Phase = PlayTableMatchEntry.StateReserved,
            };
            var launch = new LaunchState
            {
                Table = table,
                Before = before.Values,
                Match = match,
            };
            _pendingStarts[tableKey] = launch;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendMatchIntent(PlayTableMatchIntentMessage.OpStart, match,
                    predictionId, PlayTableInterop.DeckCardCount()),
                () =>
                {
                    if (!PlayTableInterop.ReserveSeat(table, before.Seat, out _))
                    {
                        throw new InvalidOperationException("predicted play-table seat reservation failed");
                    }

                    _launches[tableKey] = launch;
                },
                () => RemoveLaunchLocal(tableKey, false));
        }

        private bool SendKickIntent(byte target)
        {
            var tables = PlayTableInterop.Tables(PlayTableInterop.FindShelfManager());
            if (tables == null || target >= tables.Count
                || !PlayTablePlacementInterop.TryGetTableKey(tables[target], out var key))
            {
                return false;
            }

            var table = tables[target];
            var before = CaptureEntry(table, key);
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendTableIntent(PlayTableIntentMessage.IntentKickTable, target,
                    key, predictionId),
                () => ApplyLocalKick(table),
                () => RestoreEntry(table, before));
            return true;
        }

        private bool SendBoxIntent(InteractablePlayTable table)
        {
            if (!PlayTablePlacementInterop.TryGetTableKey(table, out var key))
            {
                return false;
            }

            var index = PlayTableInterop.TableIndex(PlayTableInterop.FindShelfManager(), table);
            var wasBoxed = table.GetIsBoxedUp();
            var priorPosition = table.transform.position;
            var priorRotation = table.transform.rotation;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendTableIntent(PlayTableIntentMessage.IntentBoxTable,
                    (byte)index, key, predictionId),
                () =>
                {
                    _applyingPrediction++;
                    try
                    {
                        table.BoxUpObject(true);
                    }
                    finally
                    {
                        _applyingPrediction--;
                    }
                },
                () =>
                {
                    if (!wasBoxed && table.GetIsBoxedUp())
                    {
                        PlacementInterop.PlaceBoxedObject(table, priorPosition, priorRotation);
                    }
                });
            return true;
        }

        private void SendTableIntent(byte action, byte target, int key, Guid predictionId)
        {
            _context.Send(1, new PlayTableIntentMessage
            {
                PredictionId = predictionId,
                Action = action,
                Target = target,
                ObjectKey = key,
            });
        }

        private void RemoveLaunchLocal(int tableKey, bool sendCancel)
        {
            if (!_launches.TryGetValue(tableKey, out var launch))
            {
                return;
            }

            _launches.Remove(tableKey);
            if (sendCancel)
            {
                SendCancel(launch);
            }

            if (!launch.Entered)
            {
                PlayTableInterop.RestoreSeat(launch.Table, launch.Before, launch.Match.Seat);
            }
        }

        private void RestoreLaunch(LaunchState launch)
        {
            if (launch == null)
            {
                return;
            }

            _launches[launch.Match.TableKey] = launch;
            if (!launch.Entered)
            {
                PlayTableInterop.ReserveSeat(launch.Table, launch.Match.Seat, out _);
            }
        }

        private void RemoveMatchLocal(PlayTableMatchEntry match)
        {
            if (match == null)
            {
                return;
            }

            _matchesById.Remove(match.MatchId);
            if (_matchByTable.TryGetValue(match.TableKey, out var matchId)
                && matchId == match.MatchId)
            {
                _matchByTable.Remove(match.TableKey);
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
                var table = PlacementApi.ResolveObjectByKey(pair.Key) as InteractablePlayTable;
                if (table == null || PlayTableInterop.IsLocalGameplayTable(table, HasLocalLaunch))
                {
                    continue;
                }

                var sets = table.m_TableGameItemSetList;
                var seats = pair.Value.Seats;
                for (var seat = 0; seat < seats.Count && seat < sets.Count; seat++)
                {
                    ApplySeat(pair.Key, seat, table, sets[seat], new SeatState
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

        private void ApplySeat(int tableKey, int seat, InteractablePlayTable table,
            TableGameItemSet set, SeatState want)
        {
            var visualKey = ((long)tableKey << 8) | (byte)seat;
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

        private void ClearAppliedTable(int tableKey)
        {
            var remove = new List<long>();
            foreach (var pair in _applied)
            {
                if ((int)(pair.Key >> 8) == tableKey)
                {
                    if (pair.Value.Set != null)
                    {
                        pair.Value.Set.gameObject.SetActive(false);
                    }

                    remove.Add(pair.Key);
                }
            }

            for (var i = 0; i < remove.Count; i++)
            {
                _applied.Remove(remove[i]);
            }
        }

        private bool HasLocalLaunch(int tableKey) => _launches.ContainsKey(tableKey);

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
            _occupied.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            ClearPendingVisuals();
            foreach (var pair in new List<int>(_launches.Keys))
            {
                RemoveLaunchLocal(pair, false);
            }

            _pendingStarts.Clear();
            ClearMirrors();
        }

        private static PlayTableEntry CaptureEntry(InteractablePlayTable table, int key)
        {
            var sets = table?.m_TableGameItemSetList;
            var entry = new PlayTableEntry
            {
                TableKey = key,
                Occupied = table != null && table.GetCurrentPlayerCount() > 0,
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
            _applyingPrediction++;
            try
            {
                PlayTableInterop.InvokeStopTableGame(table);
            }
            finally
            {
                _applyingPrediction--;
            }
        }

        private void RestoreEntry(InteractablePlayTable table, PlayTableEntry entry)
        {
            if (table == null || entry == null)
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

            _visualEntries[entry.TableKey] = CopyEntry(entry);
            _occupied[entry.TableKey] = entry.Occupied;
        }

        private void AddMatch(PlayTableMatchEntry match)
        {
            if (match == null)
            {
                return;
            }

            var copy = CopyMatch(match);
            _matchesById[copy.MatchId] = copy;
            _matchByTable[copy.TableKey] = copy.MatchId;
        }

        private static PlayTableEntry CopyEntry(PlayTableEntry source)
            => source == null ? null : new PlayTableEntry
            {
                TableKey = source.TableKey,
                Index = source.Index,
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
                TableKey = source.TableKey,
                TableIndex = source.TableIndex,
                Seat = source.Seat,
                SideA = source.SideA,
                Phase = source.Phase,
            };

        private void ApplyPatches()
        {
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "StartMoveObject"),
                new HarmonyMethod(typeof(MovePatch), nameof(MovePatch.Prefix)), null);
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "BoxUpObject"),
                new HarmonyMethod(typeof(BoxPatch), nameof(BoxPatch.Prefix)), null);
            Patch(AccessTools.Method(typeof(PlayTableGame), "SetPlayTable",
                    new[] { typeof(InteractablePlayTable), typeof(bool) }),
                new HarmonyMethod(typeof(SetPlayTablePatch), nameof(SetPlayTablePatch.Prefix)),
                new HarmonyMethod(typeof(SetPlayTablePatch), nameof(SetPlayTablePatch.Postfix)));
            Patch(AccessTools.Method(typeof(PlayTableGame), "FinishLeaveGame", new[] { typeof(bool) }),
                null, new HarmonyMethod(typeof(FinishLeavePatch), nameof(FinishLeavePatch.Postfix)));
            Patch(AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp"),
                new HarmonyMethod(typeof(SeatPatch), nameof(SeatPatch.Prefix)), null);
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

            _harmony.Patch(original, prefix, postfix);
        }

        private static class MovePatch
        {
            internal static bool Prefix(InteractablePlayTable __instance)
            {
                var client = _active;
                if (client == null || __instance == null)
                {
                    return true;
                }

                if (!PlayTablePlacementInterop.TryGetTableKey(__instance, out var key))
                {
                    // Unidentified table: defer to vanilla rather than blocking a state change we
                    // cannot route. Blocking here is what hung the loader's box-up (see BoxPatch).
                    return true;
                }

                if (client.HasActiveMatch(key) || client.HasLocalLaunch(key)
                    || PlayTableInterop.IsLocalGameplayTable(__instance, client.HasLocalLaunch))
                {
                    return false;
                }

                if (!__instance.GetIsTournamentPlayTable()
                    && client._occupied.TryGetValue(key, out var occupied) && occupied)
                {
                    var index = PlayTableInterop.TableIndex(PlayTableInterop.FindShelfManager(),
                        __instance);
                    client.SendKickIntent((byte)index);
                    return false;
                }

                return true;
            }
        }

        private static class BoxPatch
        {
            internal static bool Prefix(InteractablePlayTable __instance, bool holdBox)
            {
                var client = _active;
                if (client == null || __instance == null || client._applyingPrediction != 0)
                {
                    return true;
                }

                if (!holdBox)
                {
                    // The vanilla loader boxes up a saved table here (and this is the "box in
                    // place" path) before placement identities are registered, then dereferences
                    // the packaging box BoxUpObject creates. Never suppress it: a blocked box-up
                    // leaves GetPackagingBoxShelf() null and the load coroutine null-references.
                    return true;
                }

                if (!PlayTablePlacementInterop.TryGetTableKey(__instance, out var key))
                {
                    return true;
                }

                if (client.HasActiveMatch(key) || client.HasLocalLaunch(key))
                {
                    return false;
                }

                client.SendBoxIntent(__instance);
                return false;
            }
        }

        private static class SetPlayTablePatch
        {
            internal static bool Prefix(InteractablePlayTable playTable)
            {
                var client = _active;
                return client == null || (PlayTablePlacementInterop.TryGetTableKey(playTable,
                    out var key) && client._launches.TryGetValue(key, out var launch)
                    && launch.Match.OwnerConn == CoopCore.LocalConnectionId);
            }

            internal static void Postfix(InteractablePlayTable playTable)
                => _active?.MarkLaunchEntered(playTable);
        }

        private static class FinishLeavePatch
        {
            internal static void Postfix(PlayTableGame __instance)
                => _active?.HandleFinishLeave(__instance);
        }

        private static class SeatPatch
        {
            internal static bool Prefix(InteractablePlayTable __instance)
            {
                var client = _active;
                if (client == null || __instance == null
                    || !PlayTableInterop.TryGetClickedSeat(__instance, out var seat))
                {
                    return false;
                }

                if (!PlayTablePlacementInterop.TryGetTableKey(__instance, out var key)
                    || client.HasActiveMatch(key) || client.HasLocalLaunch(key))
                {
                    return false;
                }

                client.RequestMatch(__instance, new BeforeClickSnapshot
                {
                    Seat = (byte)seat,
                    Values = PlayTableInterop.CaptureSeat(__instance, (byte)seat),
                });
                return false;
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
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearMirrors();
            _harmony?.UnpatchSelf();
            _launches.Clear();
            _pendingStarts.Clear();
            _visualEntries.Clear();
            _occupied.Clear();
            _matchesById.Clear();
            _matchByTable.Clear();
            ClearPendingVisuals();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void ClearPendingVisuals()
        {
            foreach (var delta in _pendingVisuals.Values)
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            _pendingVisuals.Clear();
        }
    }
}
