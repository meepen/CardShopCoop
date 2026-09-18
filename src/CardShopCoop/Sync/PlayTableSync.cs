using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors the on-table visuals of customer card matches host->client
    /// (MsgType.TableState, host->client ONLY - there are no ops).
    ///
    /// RESEARCH NOTE (decompiled/InteractablePlayTable.cs, TableGameItemSet.cs,
    /// Customer.cs): a customer match does NOT lay out real CardData cards on the
    /// table. The visible layout is exactly two things:
    ///   1. per-seat TableGameItemSet child objects on the table prefab
    ///      (m_TableGameItemSetList[seat]): a playmat, a deck box and a comic book
    ///      mesh, configured from TableGameItemSetData (3 EItemTypes) via
    ///      SpecificSetup(...) and toggled with gameObject.SetActive - see
    ///      InteractablePlayTable.CustomerHasReached / LoadData / StopTableGame;
    ///   2. the card fan / single card props on the CUSTOMER prefab
    ///      (Customer.m_GameCardFanOut / m_GameCardSingle) - those already ride
    ///      NpcSync's IsPlaying flag on the puppets, so they are out of scope here.
    /// There are no per-slot Card3dUI groups, dice or coins on the table, so the
    /// digest is per table: per seat (active?, playmat/deckbox/comic EItemTypes) -
    /// no Msg.WriteCard / Card3dUISpawner needed at all. Applying uses the game's
    /// own SpecificSetup recipe on the client's SAME table prefab children, so
    /// nothing is spawned, pooled or registered: pure visuals on existing objects.
    /// </summary>
    public class PlayTableSync : TickableCoopModule
    {
        // A partial is one table, identified by TableEntry.Index. The sweep spreads a pass over
        // ten seconds; normal shops have roughly 8-20 tables, so this is 2 messages per second.
        private const float SweepSliceSeconds = 0.5f;
        private const float SweepCycleSeconds = 10f;
        private const int MaxTables = 250;         // wire: table count is a byte
        private const int MaxSeats = 8;            // vanilla tables have 2; hard cap
        private const byte IntentKindPlayTable = 1;
        private const byte IntentKickTable = 1;
        private const int PlayTableIdentityKind = 6;
        private static int _nextMatchNonce;
        private static long _nextRequestSequence;
        private static float _lastDuelGateLog = -100f;
        private sealed class SeatSnapshot
        {
            public bool PlayerOccupied;
            public bool SeatOccupied;
            public bool SeatBooked;
            public bool QueueOccupied;
            public bool PlayerSeat;
        }

        private sealed class BeforeClickSnapshot
        {
            public int OccupiedMask;
            public byte Seat;
            public SeatSnapshot Values;
        }
        private static readonly Dictionary<InteractablePlayTable, BeforeClickSnapshot> _seatMasksBeforeClick =
            new Dictionary<InteractablePlayTable, BeforeClickSnapshot>();
        private enum LaunchPhase
        {
            Pending, AwaitingEntry, Active
        }
        private sealed class LaunchState
        {
            public InteractablePlayTable Table;
            public SeatSnapshot Before;
            public PlayTableMatchEntry Match;
            public float ChangedAt;
            public LaunchPhase Phase;
            public bool StartedSent;
            public float LastLeaseSent;
            public bool ResultReported;
        }
        private static readonly Dictionary<int, LaunchState> _launches =
            new Dictionary<int, LaunchState>();
        private const float MatchRequestTimeoutSeconds = 10f;
        private const float AwaitingEntryTimeoutSeconds = 3f;
        private struct SeatState
        {
            public bool Active;
            public bool PlayerSeat;
            public int PlayMat, DeckBox, Comic;
            public bool Same(SeatState other)
            {
                return Active == other.Active && PlayerSeat == other.PlayerSeat
                    && PlayMat == other.PlayMat && DeckBox == other.DeckBox && Comic == other.Comic;
            }
        }
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;
        public static PlayTableSync Active;
        private static PlayerIntentBus _intents;
        private ShelfManager _sm;
        private float _sweepTimer;
        private int _sweepCursor;
        private sealed class AppliedVisual
        {
            public InteractablePlayTable Table;
            public TableGameItemSet Set;
            public SeatState State;
        }
        private readonly Dictionary<int, AppliedVisual> _applied = new Dictionary<int, AppliedVisual>();
        private readonly Dictionary<int, bool> _occupied = new Dictionary<int, bool>();
        private readonly PlayTableStateModel _visualState = new PlayTableStateModel();
        private readonly Dictionary<int, long> _hostRevisions = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _hostTableIdentities = new Dictionary<int, int>();
        private readonly Dictionary<int, TableEntry> _visualEntries = new Dictionary<int, TableEntry>();
        public override string Name => nameof(PlayTableSync);
        public PlayTableSync()
        {
            Active = this;
        }
        public override void Start()
        {
            Active = this;
        }

        public void RegisterIntents(PlayerIntentBus bus)
        {
            if (bus == null)
                throw new ArgumentNullException(nameof(bus));
            _intents = bus;
            bus.Register(IntentKindPlayTable, IntentKickTable, HostKickTable);
        }

        public static void ApplyPatches(Harmony h)
        {
            var original = AccessTools.Method(typeof(InteractablePlayTable), "StartMoveObject");
            if (original == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.StartMoveObject");
            else
                h.Patch(original, prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(StartMoveObjectPrefix)));

            // These methods own the authoritative table mutations in both supported builds.
            // CustomerHasReached places the seat set-pieces; StopTableGame clears them; the
            // right-click path marks the real player's seat and starts their match.
            var seated = AccessTools.Method(typeof(InteractablePlayTable), "CustomerHasReached");
            if (seated == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.CustomerHasReached");
            else
                h.Patch(seated, postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(TableChangedPostfix)));
            var stopped = AccessTools.Method(typeof(InteractablePlayTable), "StopTableGame");
            if (stopped == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.StopTableGame");
            else
                h.Patch(stopped, postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(StopTableGamePostfix)));
            var playerSat = AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp");
            if (playerSat == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.OnRightMouseButtonUp");
            else
                h.Patch(playerSat, postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(TableChangedPostfix)));

            // SetPlayTable is the point at which vanilla enters duel mode and starts DelayStart.
            // Gating StartPlayerCardGame is too late: the UI and coroutine already exist by then.
            var duel = AccessTools.Method(typeof(PlayTableGame), "SetPlayTable",
                new[] { typeof(InteractablePlayTable), typeof(bool) });
            if (duel == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: PlayTableGame.SetPlayTable(InteractablePlayTable, bool)");
            else
                h.Patch(duel, prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(SetPlayTablePrefix)));

            var quit = AccessTools.Method(typeof(PlayTableGame), "FinishLeaveGame",
                new[] { typeof(bool) });
            if (quit == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: PlayTableGame.FinishLeaveGame(bool)");
            else
                h.Patch(quit, prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(CancelOnLeavePrefix)));

            var capture = AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp");
            if (capture != null)
                h.Patch(capture,
                    prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(CaptureSeatSelectionPrefix)),
                    postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(PlayerSeatSelectedPostfix)));
        }

        /// <summary>Client: prevent vanilla from entering duel mode until the host has rostered
        /// this client's reservation for the exact table being entered.</summary>
        public static bool SetPlayTablePrefix(InteractablePlayTable playTable)
        {
            if (CoopCore.Role != CoopRole.Client || !CoopCore.InSessionWorld)
                return true;
            if (TryGetTableKey(playTable, out int tableKey)
                && PlayTableMatchSync.Active != null
                && PlayTableMatchSync.Active.TryGetMatch(tableKey, out var match)
                && match.OwnerConn == CoopCore.LocalConnectionId)
                return true;

            if (Time.realtimeSinceStartup - _lastDuelGateLog > 1f)
            {
                _lastDuelGateLog = Time.realtimeSinceStartup;
                CoopPlugin.Log.LogWarning($"PlayTableSync: blocked duel start; table key={tableKeyForLog(playTable)} is not owned by this client");
            }
            return false;
        }

        private static int tableKeyForLog(InteractablePlayTable table)
            => TryGetTableKey(table, out int key) ? key : 0;

        private static bool TryGetTableKey(InteractablePlayTable table, out int key)
        {
            return PlacedObjectIdentity.TryMakeObjectKey(PlayTableIdentityKind, table, out key);
        }

        private static void CaptureSeatSelectionPrefix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            if (TryGetClickedSeat(__instance, out _))
                _seatMasksBeforeClick[__instance] = CaptureSeatSnapshot(__instance);
            else
                _seatMasksBeforeClick.Remove(__instance);
        }

        private static void PlayerSeatSelectedPostfix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            if (!_seatMasksBeforeClick.TryGetValue(__instance, out var beforeSnapshot))
                return;
            _seatMasksBeforeClick.Remove(__instance);
            if (!TryGetTableKey(__instance, out int tableKey)
                || PlayTableMatchSync.Active == null)
            {
                RestoreSeatSnapshot(__instance, beforeSnapshot.Values, beforeSnapshot.Seat);
                return;
            }
            if (PlayTableMatchSync.Active.TryGetMatch(tableKey, out var existing)
                && existing.OwnerConn == CoopCore.LocalConnectionId)
            {
                RestoreSeatSnapshot(__instance, beforeSnapshot.Values, beforeSnapshot.Seat);
                return;
            }
            if (_launches.ContainsKey(tableKey))
            {
                RestoreSeatSnapshot(__instance, beforeSnapshot.Values, beforeSnapshot.Seat);
                return;
            }

            var seats = __instance.m_IsSeatOccupied;
            int selected = beforeSnapshot.Seat;
            if (seats == null
                || selected >= seats.Count
                || !seats[selected])
            {
                RestoreSeatSnapshot(__instance, beforeSnapshot.Values, beforeSnapshot.Seat);
                return;
            }

            var sm = Active != null ? Active.Sm() : UnityEngine.Object.FindFirstObjectByType<ShelfManager>();
            int tableIndex = sm != null && sm.m_PlayTableList != null
                ? sm.m_PlayTableList.IndexOf(__instance) : -1;
            if (tableIndex < 0 || tableIndex > byte.MaxValue)
            {
                RestoreSeatSnapshot(__instance, beforeSnapshot.Values, beforeSnapshot.Seat);
                return;
            }
            int deckCount = 0;
            if (CPlayerData.m_DeckCompactCardDataList != null
                && CPlayerData.m_CurrentSelectedDeckIndex >= 0
                && CPlayerData.m_CurrentSelectedDeckIndex < CPlayerData.m_DeckCompactCardDataList.Count
                && CPlayerData.m_DeckCompactCardDataList[CPlayerData.m_CurrentSelectedDeckIndex] != null)
                deckCount = CPlayerData.m_DeckCompactCardDataList[CPlayerData.m_CurrentSelectedDeckIndex].GetTotalCardCount();
            CoopPlugin.Log.LogInfo($"PlayTableSync: requesting match table={tableKey} seat={selected} deck={deckCount}");
            string matchId = "playtable-" + CoopCore.LocalConnectionId + "-" + NextMatchNonce();
            PlayTableMatchSync.Active.SendOp?.Invoke(new PlayTableMatchRequest
            {
                Op = PlayTableMatchRequest.OpStart,
                RequestSequence = NextRequestSequence(),
                MatchId = matchId,
                TableKey = tableKey,
                TableIndex = (byte)tableIndex,
                Seat = (byte)selected,
                SideA = selected == 0,
                DeckCardCount = deckCount,
            });
            _launches[tableKey] = new LaunchState
            {
                Table = __instance,
                Before = beforeSnapshot.Values,
                Match = new PlayTableMatchEntry
                {
                    MatchId = matchId,
                    TableKey = tableKey,
                    TableIndex = (byte)tableIndex,
                    Seat = (byte)selected,
                    SideA = selected == 0
                },
                ChangedAt = Time.realtimeSinceStartup,
                Phase = LaunchPhase.Pending
            };
        }

        private static int NextMatchNonce()
        {
            if (_nextMatchNonce == int.MaxValue)
                _nextMatchNonce = 0;
            return ++_nextMatchNonce;
        }

        private static long NextRequestSequence()
        {
            if (_nextRequestSequence == long.MaxValue)
                _nextRequestSequence = 0;
            return ++_nextRequestSequence;
        }

        private static BeforeClickSnapshot CaptureSeatSnapshot(InteractablePlayTable table)
        {
            var seats = table.m_IsSeatOccupied;
            if (!TryGetClickedSeat(table, out int clickedSeat))
                return null;
            int mask = SeatMask(seats);
            var snapshot = new BeforeClickSnapshot
            {
                OccupiedMask = mask,
                Seat = (byte)clickedSeat,
                Values = CaptureSeatValues(table, (byte)clickedSeat),
            };
            return snapshot;
        }

        private static SeatSnapshot CaptureSeatValues(InteractablePlayTable table, byte seat)
        {
            return new SeatSnapshot
            {
                PlayerOccupied = table != null && (bool)(AccessTools.Field(typeof(InteractablePlayTable), "m_IsPlayerOccupied")
                    ?.GetValue(table) ?? false),
                SeatOccupied = ReadSeat(table != null ? table.m_IsSeatOccupied : null, seat),
                SeatBooked = ReadSeat(table != null ? table.m_IsSeatBooked : null, seat),
                QueueOccupied = ReadSeat(table != null ? table.m_IsQueueOccupied : null, seat),
                PlayerSeat = ReadSeat(table != null ? table.m_IsPlayerSeat : null, seat),
            };
        }

        private static bool TryGetClickedSeat(InteractablePlayTable table, out int seat)
        {
            seat = -1;
            var occupied = table != null ? table.m_IsSeatOccupied : null;
            var controller = SceneRef<InteractionPlayerController>.Get();
            var collider = controller != null ? controller.m_PlayerCollider : null;
            if (table == null || occupied == null || occupied.Count < 2 || collider == null)
                return false;
            Vector3 vector = table.transform.position - collider.transform.position;
            vector.y = 0f;
            if (Vector3.Dot(vector.normalized, table.transform.right) > 0f)
            {
                if (occupied[1] && !occupied[0])
                    seat = 0;
            }
            else if (occupied[0] && !occupied[1])
                seat = 1;
            return seat >= 0;
        }

        private static bool ReadSeat(List<bool> values, byte seat)
            => values != null && seat < values.Count && values[seat];

        private static int SeatMask(List<bool> seats)
        {
            int mask = 0;
            if (seats != null)
                for (int i = 0; i < Mathf.Min(seats.Count, MaxSeats); i++)
                    if (seats[i])
                        mask |= 1 << i;
            return mask;
        }

        private static void RestoreSeatSnapshot(InteractablePlayTable table, SeatSnapshot snapshot, byte seat)
        {
            if (table == null || snapshot == null)
                return;
            AccessTools.Field(typeof(InteractablePlayTable), "m_IsPlayerOccupied")?.SetValue(table,
                snapshot.PlayerOccupied);
            RestoreSeat(table.m_IsSeatOccupied, snapshot.SeatOccupied, seat);
            RestoreSeat(table.m_IsSeatBooked, snapshot.SeatBooked, seat);
            RestoreSeat(table.m_IsQueueOccupied, snapshot.QueueOccupied, seat);
            RestoreSeat(table.m_IsPlayerSeat, snapshot.PlayerSeat, seat);
        }

        private static void RestoreSeat(List<bool> target, bool value, byte seat)
        {
            if (target == null || seat >= target.Count)
                return;
            target[seat] = value;
        }

        /// <summary>T5 calls this immediately after sending a completed-match result.</summary>
        public static void MarkResultReported(int tableKey, string matchId)
        {
            if (_launches.TryGetValue(tableKey, out var launch) && launch.Match.MatchId == matchId)
                launch.ResultReported = true;
        }

        public static void OnMatchStateReconciled()
        {
            Active?.ReconcileLaunches();
        }

        private static void CancelOnLeavePrefix(PlayTableGame __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return;
            var game = SceneRef<PlayCardGameManager>.Get();
            var table = game?.m_PlayTableGame == null ? null :
                AccessTools.Field(typeof(PlayTableGame), "m_CurrentInteractablePlayTable")?.GetValue(game.m_PlayTableGame) as InteractablePlayTable;
            if (!TryGetTableKey(table, out int key) || !_launches.TryGetValue(key, out var launch)
                || launch.ResultReported || PlayTableMatchSync.Active == null)
                return;
            SendCancel(launch.Match);
            RestoreSeatSnapshot(launch.Table, launch.Before, launch.Match.Seat);
            _launches.Remove(key);
        }

        private void ReconcileLaunches()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            var remove = new List<int>();
            foreach (var pair in _launches)
            {
                var launch = pair.Value;
                if (!PlayTableMatchSync.Active.TryGetMatch(pair.Key, out var live)
                    || live.OwnerConn != CoopCore.LocalConnectionId
                    || live.MatchId != launch.Match.MatchId)
                {
                    if (!HasEnteredTable(launch.Table))
                    {
                        SendCancel(launch.Match);
                        RestoreSeatSnapshot(launch.Table, launch.Before, launch.Match.Seat);
                    }
                    remove.Add(pair.Key);
                    continue;
                }
                launch.Match = live;
                if (launch.Phase == LaunchPhase.Pending)
                    BeginAcceptedLaunch(pair.Key, launch);
            }
            foreach (int key in remove)
                _launches.Remove(key);
        }

        private static void BeginAcceptedLaunch(int tableKey, LaunchState launch)
        {
            if (launch == null || launch.Table == null || !CoopCore.InSessionWorld
                || !TryGetTableKey(launch.Table, out int actualKey) || actualKey != tableKey
                || HasEnteredTable(launch.Table))
            {
                if (launch != null)
                {
                    SendCancel(launch.Match);
                    RestoreSeatSnapshot(launch.Table, launch.Before, launch.Match.Seat);
                }
                _launches.Remove(tableKey);
                return;
            }
            var manager = SceneRef<PlayCardGameManager>.Get();
            if (manager == null || manager.m_PlayTableGame == null)
            {
                CoopPlugin.Log.LogWarning($"PlayTableSync: accepted launch deferred; scene manager absent table={tableKey}");
                return;
            }
            launch.Phase = LaunchPhase.AwaitingEntry;
            launch.ChangedAt = Time.realtimeSinceStartup;
            try
            {
                PlayCardGameManager.SetPlayTable(launch.Table, launch.Match.SideA);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogError($"PlayTableSync: launch failed table={tableKey}: {e.Message}");
                SendCancel(launch.Match);
                RestoreSeatSnapshot(launch.Table, launch.Before, launch.Match.Seat);
                _launches.Remove(tableKey);
            }
        }

        private void CheckLaunches()
        {
            if (_launches.Count == 0 || PlayTableMatchSync.Active == null)
                return;
            var remove = new List<int>();
            foreach (var pair in _launches)
            {
                var launch = pair.Value;
                if (!PlayTableMatchSync.Active.TryGetMatch(pair.Key, out var live)
                    || live.MatchId != launch.Match.MatchId)
                {
                    remove.Add(pair.Key);
                    continue;
                }
                launch.Match = live;
                if (launch.Phase == LaunchPhase.Pending)
                {
                    BeginAcceptedLaunch(pair.Key, launch);
                    continue;
                }
                if (HasEnteredTable(launch.Table))
                {
                    launch.Phase = LaunchPhase.Active;
                    if (!launch.StartedSent)
                    {
                        launch.StartedSent = true;
                        launch.LastLeaseSent = Time.realtimeSinceStartup;
                        PlayTableMatchSync.Active.SendOp?.Invoke(new PlayTableMatchRequest
                        {
                            Op = PlayTableMatchRequest.OpStarted,
                            RequestSequence = NextRequestSequence(),
                            Epoch = live.Epoch,
                            Revision = live.Revision,
                            MatchId = live.MatchId,
                            TableKey = live.TableKey,
                            TableIndex = live.TableIndex,
                            Seat = live.Seat,
                            SideA = live.SideA
                        });
                    }
                    else if (Time.realtimeSinceStartup - launch.LastLeaseSent >= 1f)
                    {
                        launch.LastLeaseSent = Time.realtimeSinceStartup;
                        // OpStarted doubles as the authenticated lightweight possession lease.
                        PlayTableMatchSync.Active.SendOp?.Invoke(new PlayTableMatchRequest
                        {
                            Op = PlayTableMatchRequest.OpStarted,
                            RequestSequence = NextRequestSequence(),
                            Epoch = live.Epoch,
                            Revision = live.Revision,
                            MatchId = live.MatchId,
                            TableKey = live.TableKey,
                            TableIndex = live.TableIndex,
                            Seat = live.Seat,
                            SideA = live.SideA
                        });
                    }
                }
                else if (Time.realtimeSinceStartup - launch.ChangedAt >= AwaitingEntryTimeoutSeconds)
                {
                    SendCancel(live);
                    RestoreSeatSnapshot(launch.Table, launch.Before, live.Seat);
                    remove.Add(pair.Key);
                }
            }
            foreach (int key in remove)
                _launches.Remove(key);
        }

        private static void SendCancel(PlayTableMatchEntry match)
        {
            if (match == null)
                return;
            PlayTableMatchSync.Active?.SendOp?.Invoke(new PlayTableMatchRequest
            {
                Op = PlayTableMatchRequest.OpCancel,
                RequestSequence = NextRequestSequence(),
                Epoch = match.Epoch,
                Revision = match.Revision,
                MatchId = match.MatchId,
                TableKey = match.TableKey,
                TableIndex = match.TableIndex,
                Seat = match.Seat,
                SideA = match.SideA
            });
        }

        private static void TableChangedPostfix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                var self = Active;
                if (self != null)
                {
                    self.Guarded("change", () => self.SendTableNow(__instance));
                }
            }
        }

        private static void StopTableGamePostfix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role == CoopRole.Host && CoopCore.InSessionWorld
                && TryGetTableKey(__instance, out int tableKey)
                && PlayTableMatchSync.Active != null
                && PlayTableMatchSync.Active.TryGetHostMatch(tableKey, out var match))
            {
                CoopPlugin.Log.LogInfo($"PlayTableSync: releasing stopped table reservation table={tableKey} match={match.MatchId}");
                PlayTableMatchSync.Active.Release(tableKey, match.MatchId, "table-stopped");
            }
            TableChangedPostfix(__instance);
        }

        private static bool HasEnteredTable(InteractablePlayTable table)
        {
            var gameManager = SceneRef<PlayCardGameManager>.Get();
            var game = gameManager != null ? gameManager.m_PlayTableGame : null;
            var modeField = AccessTools.Field(typeof(PlayTableGame), "m_IsPlayTableGameMode");
            var currentField = AccessTools.Field(typeof(PlayTableGame), "m_CurrentInteractablePlayTable");
            var currentTable = game != null ? currentField?.GetValue(game) as InteractablePlayTable : null;
            return game != null && modeField != null && (bool)modeField.GetValue(game)
                && ReferenceEquals(currentTable, table);
        }

        public override void Reset()
        {
            CancelTrackedLaunches();
            _nextRequestSequence = 0;
            ClearMirrors();
            _applied.Clear();
            _occupied.Clear();
            _visualState.Reset(Math.Max(0, CoopCore.SessionGeneration));
            _hostRevisions.Clear();
            _hostTableIdentities.Clear();
            _visualEntries.Clear();
            _launches.Clear();
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _sm = null;
            _seatMasksBeforeClick.Clear();
        }

        public override void ForceResend()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
        }

        public override void Dispose()
        {
            CancelTrackedLaunches();
            base.Dispose();
            if (ReferenceEquals(Active, this))
                Active = null;
            _intents = null;
            _seatMasksBeforeClick.Clear();
        }

        private static void CancelTrackedLaunches()
        {
            foreach (var pair in new List<KeyValuePair<int, LaunchState>>(_launches))
            {
                var launch = pair.Value;
                if (!launch.ResultReported)
                    SendCancel(launch.Match);
                if (!HasEnteredTable(launch.Table))
                    RestoreSeatSnapshot(launch.Table, launch.Before, launch.Match.Seat);
            }
            _launches.Clear();
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindFirstObjectByType<ShelfManager>();
            return _sm;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
        }

        /// <summary>Gradually re-assert one table. This is host/session guarded because the
        /// tick pipeline invokes it for clients and outside a loaded shop too.</summary>
        public override void PeriodicUpdate(float delta)
        {
            if (CoopCore.Role == CoopRole.Client)
            {
                CheckLaunches();
                var expired = new List<int>();
                foreach (var pair in _launches)
                    if (pair.Value.Phase == LaunchPhase.Pending
                        && Time.realtimeSinceStartup - pair.Value.ChangedAt >= MatchRequestTimeoutSeconds)
                    {
                        SendCancel(pair.Value.Match);
                        RestoreSeatSnapshot(pair.Value.Table, pair.Value.Before, pair.Value.Match.Seat);
                        expired.Add(pair.Key);
                    }
                foreach (int key in expired)
                    _launches.Remove(key);
                return;
            }
            if (CoopCore.Role != CoopRole.Host || BroadcastState == null || !CoopCore.InSessionWorld)
                return;
            _sweepTimer += delta;
            if (_sweepTimer < SweepSliceSeconds)
                return;
            _sweepTimer = 0f;
            Guarded("sweep", () =>
            {
                var sm = Sm();
                var tables = sm != null ? sm.m_PlayTableList : null;
                if (tables == null)
                    return;
                int total = Mathf.Min(tables.Count, MaxTables);
                if (total <= 0)
                {
                    _sweepCursor = 0;
                    return;
                }
                if (_sweepCursor >= total)
                    _sweepCursor = 0;
                int slicesPerCycle = Mathf.Max(1,
                    Mathf.RoundToInt(SweepCycleSeconds / SweepSliceSeconds));
                int perSlice = Mathf.Max(1, (total + slicesPerCycle - 1) / slicesPerCycle);
                for (int n = 0; n < perSlice; n++)
                {
                    int index = _sweepCursor;
                    _sweepCursor = (_sweepCursor + 1) % total;
                    // Deliberately unconditional: this is the lost-frame repair path. The
                    // push-on-change hooks remain separate and immediate below.
                    SendTableNow(index, false);
                }
            });
        }

        public override void FullUpdate(Connection connection)
        {
            int connId = connection.Id;
            if (CoopCore.Role != CoopRole.Host || SendToClient == null)
                return;
            Guarded("full", () =>
            {
                var sm = Sm();
                var tables = sm != null ? sm.m_PlayTableList : null;
                if (tables == null)
                    return;
                int count = Mathf.Min(tables.Count, MaxTables);
                SendToClient(connId, BuildState(tables, count));
            });
        }

        private void SendTableNow(InteractablePlayTable table)
        {
            var sm = Sm();
            int index = sm != null && sm.m_PlayTableList != null
                ? sm.m_PlayTableList.IndexOf(table) : -1;
            if (index >= 0 && index < MaxTables)
                SendTableNow(index);
        }

        private void SendTableNow(int index, bool mutation = true)
        {
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables == null || index < 0 || index >= tables.Count || index >= MaxTables || BroadcastState == null)
                return;
            EnsureHostTableIdentities(tables, Mathf.Min(tables.Count, MaxTables));
            var entry = BuildEntry(tables[index], index);
            long revision = mutation ? NextHostRevision(index) : CurrentHostRevision(index);
            entry.Revision = revision;
            BroadcastState(new TableStateMessage
            {
                Epoch = CoopCore.SessionGeneration,
                Full = false,
                Index = index,
                Tables = new List<TableEntry> { entry },
                TableRevisions = new Dictionary<byte, long> { [(byte)index] = revision }
            });
        }

        /// <summary>Game 1.0: read whether the host table marks this seat as a real player's.</summary>
        private static bool IsPlayerSeat(InteractablePlayTable table, int seat)
        {
            try
            {
                var list = table != null ? table.m_IsPlayerSeat : null;
                return list != null && seat >= 0 && seat < list.Count && list[seat];
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        private static SeatState HostSeat(TableGameItemSet set)
        {
            if (set == null || !set.gameObject.activeSelf)
                return default;
            var data = set.m_TableGameItemSetData;
            return new SeatState
            {
                Active = true,
                PlayMat = data != null ? (int)data.playMatType : 0,
                DeckBox = data != null ? (int)data.deckBoxType : 0,
                Comic = data != null ? (int)data.comicBookType : 0,
            };
        }

        private static TableStateMessage BuildState(List<InteractablePlayTable> tables, int count)
        {
            if (Active != null)
                Active.EnsureHostTableIdentities(tables, count);
            var msg = new TableStateMessage { Epoch = CoopCore.SessionGeneration };
            for (int i = 0; i < count; i++)
            {
                var entry = BuildEntry(tables[i], i);
                // Full snapshots are also the visual baseline. The caller assigns the
                // persistent watermark before sending, so omitted tables cannot resurrect.
                entry.Revision = Active != null ? Active.CurrentHostRevision(i) : 1;
                msg.Tables.Add(entry);
                msg.TableRevisions[(byte)i] = entry.Revision;
            }
            if (Active != null)
                foreach (var pair in Active._hostRevisions)
                    if (pair.Key >= 0 && pair.Key < MaxTables)
                        msg.TableRevisions[(byte)pair.Key] = pair.Value;
            return msg;
        }

        private void EnsureHostTableIdentities(List<InteractablePlayTable> tables, int count)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int identity = tables[i] != null && TryGetTableKey(tables[i], out int key) ? key : 0;
                seen.Add(i);
                if (!_hostTableIdentities.TryGetValue(i, out int old))
                    _hostTableIdentities[i] = identity;
                else if (old != identity)
                {
                    _hostTableIdentities[i] = identity;
                    NextHostRevision(i);
                }
                CurrentHostRevision(i);
            }
            var removed = new List<int>();
            foreach (var pair in _hostTableIdentities)
                if (!seen.Contains(pair.Key))
                    removed.Add(pair.Key);
            foreach (int i in removed)
            {
                _hostTableIdentities.Remove(i);
                NextHostRevision(i);
            }
        }

        private long NextHostRevision(int tableIndex)
        {
            if (!_hostRevisions.TryGetValue(tableIndex, out long revision))
                revision = 0;
            _hostRevisions[tableIndex] = ++revision;
            return revision;
        }

        private long CurrentHostRevision(int tableIndex)
        {
            if (!_hostRevisions.TryGetValue(tableIndex, out long revision) || revision <= 0)
                revision = 1;
            _hostRevisions[tableIndex] = revision;
            return revision;
        }

        private static TableEntry BuildEntry(InteractablePlayTable table, int index)
        {
            var sets = table != null ? table.m_TableGameItemSetList : null;
            int seats = sets != null ? Mathf.Min(sets.Count, MaxSeats) : 0;
            // Entries are JSON on the wire; their size varies with seat contents.
            var entry = new TableEntry
            {
                Index = (byte)index,
                Occupied = table != null && table.GetCurrentPlayerCount() > 0
            };
            for (int s = 0; s < seats; s++)
            {
                var st = HostSeat(sets[s]);
                var seat = new TableSeatEntry { Active = st.Active, PlayerSeat = IsPlayerSeat(table, s) };
                if (st.Active)
                {
                    seat.PlayMat = (EItemType)st.PlayMat;
                    seat.DeckBox = (EItemType)st.DeckBox;
                    seat.Comic = (EItemType)st.Comic;
                }
                entry.Seats.Add(seat);
            }
            return entry;
        }

        // ---------------- client ----------------

        public void ClientApplyState(TableStateMessage message)
        {
            Guarded("apply", () => ClientApplyInner(message));
        }

        private void ClientApplyInner(TableStateMessage message)
        {
            if (message == null || message.Tables == null || message.TableRevisions == null)
                return;
            if (message.Epoch < 0)
                return;
            var seenEntries = new HashSet<byte>();
            foreach (var entry in message.Tables)
            {
                if (entry == null || entry.Revision <= 0
                    || !message.TableRevisions.TryGetValue(entry.Index, out long watermark)
                    || watermark != entry.Revision || !seenEntries.Add(entry.Index)
                    || entry.Seats == null)
                    return;
                foreach (var seat in entry.Seats)
                    if (seat == null)
                        return;
            }
            var frame = new PlayTableStateFrame
            {
                Epoch = message.Epoch,
                Full = message.Full,
                TableRevisions = new Dictionary<int, long>()
            };
            foreach (var pair in message.TableRevisions)
                frame.TableRevisions[pair.Key + 1] = pair.Value;
            for (int i = 0; i < message.Tables.Count; i++)
            {
                var entry = message.Tables[i];
                if (entry == null)
                    continue;
                frame.Tables.Add(new PlayTableRecord
                {
                    TableKey = entry.Index + 1,
                    TableIndex = entry.Index,
                    Epoch = message.Epoch,
                    Revision = entry.Revision,
                    Phase = PlayTableRecord.PhaseReserved
                });
            }
            // The reducer only carries the roster identity/revision. Validate the actual visual
            // payload separately before changing either mirror, so equal-revision conflicts are
            // rejected atomically instead of being selected by packet arrival order.
            foreach (var entry in message.Tables)
            {
                if (entry == null || !_visualEntries.TryGetValue(entry.Index, out var prior)
                    || prior.Revision != entry.Revision)
                    continue;
                if (!SameVisual(prior, entry))
                {
                    CoopPlugin.Log.LogWarning($"PlayTableSync: equal-revision visual conflict rejected index={entry.Index} revision={entry.Revision}");
                    return;
                }
            }
            var applyResult = _visualState.Apply(frame);
            if (applyResult == PlayTableApplyResult.Invalid || applyResult == PlayTableApplyResult.Stale)
                return;
            foreach (var entry in message.Tables)
            {
                if (entry == null || !_visualState.TryGet(entry.Index + 1, out var state)
                    || state.Revision != entry.Revision)
                    continue;
                if (_visualEntries.TryGetValue(entry.Index, out var prior)
                    && prior.Revision != entry.Revision)
                    ClearAppliedTable(entry.Index, Sm()?.m_PlayTableList);
                _visualEntries[entry.Index] = entry;
            }
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            var removedEntries = new List<int>();
            foreach (var pair in _visualEntries)
                if (!_visualState.TryGet(pair.Key + 1, out _))
                    removedEntries.Add(pair.Key);
            foreach (int index in removedEntries)
            {
                _visualEntries.Remove(index);
                _occupied.Remove(index);
                ClearAppliedTable(index, tables);
            }

            foreach (var visual in _visualEntries)
            {
                int tableIdx = visual.Key;
                TableEntry entry = visual.Value;
                _occupied[tableIdx] = entry.Occupied;
                var seats = entry.Seats;
                InteractablePlayTable table =
                    (tables != null && tableIdx < tables.Count) ? tables[tableIdx] : null;
                // the JOINER may be playing the minigame at this table right now -
                // never stomp their own session's props (covers m_IsPlayerOccupied too)
                bool skipTable = table == null || IsLocalGameplayTable(table);
                var sets = (!skipTable) ? table.m_TableGameItemSetList : null;
                for (int s = 0; s < seats.Count; s++)
                {
                    var se = seats[s];
                    var st = new SeatState { Active = se.Active, PlayerSeat = se.PlayerSeat };
                    if (st.Active)
                    {
                        // host ids -> ours (already translated by the DTO deserialize), so
                        // ApplySeat below can cast straight to a LOCAL EItemType. A set piece
                        // from a pack only the host has resolves to EItemType.None and simply
                        // paints no mesh - the seat is still shown, one-sided packs are allowed
                        st.PlayMat = (int)se.PlayMat;
                        st.DeckBox = (int)se.DeckBox;
                        st.Comic = (int)se.Comic;
                    }
                    if (skipTable || sets == null || s >= sets.Count)
                        continue;
                    ApplySeat(tableIdx, s, sets[s], st);
                }
            }
            // Full frames may omit a table. The reducer has removed it, so clear only mirrors
            // owned by this module; local gameplay and remote reservations are never touched.
            var stale = new List<int>();
            foreach (var pair in _applied)
                if (!_visualState.TryGet((pair.Key >> 8) + 1, out _))
                    stale.Add(pair.Key);
            foreach (int key in stale)
            {
                if (_applied.TryGetValue(key, out var applied)
                    && applied.Set != null && !IsLocalGameplayTable(applied.Table))
                {
                    applied.Set.gameObject.SetActive(false);
                }
                _applied.Remove(key);
            }
        }

        public static bool StartMoveObjectPrefix(InteractablePlayTable __instance)
        {
            if (__instance == null)
                return true;
            if (Active != null && TryGetTableKey(__instance, out int reservedKey)
                && PlayTableMatchSync.Active != null
                && (PlayTableMatchSync.Active.HasActiveMatch(reservedKey) || HasLocalLaunchState(reservedKey)))
                return false;
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (HasLocalGameplayState(__instance))
                return true;
            if (!__instance.GetIsTournamentPlayTable() && Active != null
                && !IsRosterManaged(__instance)
                && Active.IsOccupied(__instance))
            {
                var sm = Active.Sm();
                int index = sm != null && sm.m_PlayTableList != null
                    ? sm.m_PlayTableList.IndexOf(__instance) : -1;
                if (index >= 0 && index <= 255)
                {
                    if (_intents != null && _intents.TrySend(IntentKindPlayTable, IntentKickTable, (byte)index))
                        return false;
                }
            }
            return true;
        }

        private bool IsOccupied(InteractablePlayTable table)
        {
            Sm();
            int index = _sm != null && _sm.m_PlayTableList != null
                ? _sm.m_PlayTableList.IndexOf(table) : -1;
            if (index >= 0 && _occupied.TryGetValue(index, out var occupied))
                return occupied;
            return false;
        }

        private void HostKickTable(PlayerIntentMessage message)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            var sm = Sm();
            if (sm == null || sm.m_PlayTableList == null || message.Target >= sm.m_PlayTableList.Count)
                return;
            var table = sm.m_PlayTableList[message.Target];
            if (table == null || table.GetIsTournamentPlayTable() || table.GetCurrentPlayerCount() <= 0)
                return;
            if (TryGetTableKey(table, out int tableKey)
                && PlayTableMatchSync.Active != null
                && PlayTableMatchSync.Active.HasHostActiveMatch(tableKey))
            {
                CoopPlugin.Log.LogWarning($"PlayTableSync: kick ignored - table reserved key={tableKey} index={message.Target}");
                return;
            }
            if (StopTableGame == null)
            {
                CoopPlugin.Log.LogError("PlayTableSync: InteractablePlayTable.StopTableGame was not found");
                return;
            }
            CoopPlugin.Log.LogInfo($"PlayTableSync: client requested kick for table {message.Target}");
            StopTableGame.Invoke(table, null);
        }

        private static readonly System.Reflection.MethodInfo StopTableGame =
            AccessTools.Method(typeof(InteractablePlayTable), "StopTableGame");

        private static bool IsRosterManaged(InteractablePlayTable table)
        {
            return table != null && TryGetTableKey(table, out int key)
                && PlayTableMatchSync.Active != null
                && (PlayTableMatchSync.Active.HasActiveMatch(key) || HasLocalLaunchState(key));
        }

        private static bool IsLocalGameplayTable(InteractablePlayTable table)
        {
            return table != null && (HasEnteredTable(table)
                || (TryGetTableKey(table, out int key) && HasLocalLaunchState(key)));
        }

        private static bool HasLocalGameplayState(InteractablePlayTable table)
            => IsLocalGameplayTable(table);

        private static bool HasLocalLaunchState(int tableKey)
        {
            return _launches.ContainsKey(tableKey);
        }

        private void ApplySeat(int tableIdx, int seat, TableGameItemSet set, SeatState want)
        {
            if (set == null)
                return;
            int key = (tableIdx << 8) | seat;
            if (_applied.TryGetValue(key, out var applied) && applied.State.Same(want)
                && ReferenceEquals(applied.Table, Sm()?.m_PlayTableList != null
                    && tableIdx < Sm().m_PlayTableList.Count ? Sm().m_PlayTableList[tableIdx] : null)
                && ReferenceEquals(applied.Set, set)
                && set.gameObject.activeSelf == want.Active)
                return;
            if (applied != null && (!ReferenceEquals(applied.Set, set)
                || !ReferenceEquals(applied.Table, Sm()?.m_PlayTableList != null
                    && tableIdx < Sm().m_PlayTableList.Count ? Sm().m_PlayTableList[tableIdx] : null))
                && applied.Set != null && !IsLocalGameplayTable(applied.Table))
                applied.Set.gameObject.SetActive(false);
            try
            {
                if (want.Active)
                {
                    // the game's own recipe (InteractablePlayTable.CustomerHasReached):
                    // SpecificSetup paints the playmat/deckbox/comic meshes, then the
                    // set is activated. Purely cosmetic on the table's own children -
                    // m_HasStartPlay et al stay false, so no sim state is touched and
                    // nothing is spawned or registered anywhere.
                    var data = new TableGameItemSetData
                    {
                        playMatType = (EItemType)want.PlayMat,
                        deckBoxType = (EItemType)want.DeckBox,
                        comicBookType = (EItemType)want.Comic,
                    };
                    set.SpecificSetup(data);
                    set.gameObject.SetActive(true);
                }
                else
                {
                    set.gameObject.SetActive(false);
                }
                ApplyPlayerSeat(tableIdx, seat, want.PlayerSeat);
                _applied[key] = new AppliedVisual
                {
                    Table = Sm()?.m_PlayTableList != null && tableIdx < Sm().m_PlayTableList.Count
                        ? Sm().m_PlayTableList[tableIdx] : null,
                    Set = set,
                    State = want
                };
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"PlayTableSync seat {tableIdx}/{seat}: " + e.Message);
            }
        }

        /// <summary>Game 1.0: mirror the host's real-player seat flag onto the client table.</summary>
        private void ApplyPlayerSeat(int tableIdx, int seat, bool playerSeat)
        {
            try
            {
                var sm = Sm();
                var tables = sm != null ? sm.m_PlayTableList : null;
                if (tables == null || tableIdx < 0 || tableIdx >= tables.Count)
                    return;
                var table = tables[tableIdx];
                if (IsRosterManaged(table))
                    return;
                var list = table != null ? table.m_IsPlayerSeat : null;
                if (list != null && seat >= 0 && seat < list.Count)
                    list[seat] = playerSeat;
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private void ClearAppliedTable(int tableIdx, List<InteractablePlayTable> tables)
        {
            if (tableIdx < 0)
                return;
            for (int seat = 0; seat < MaxSeats; seat++)
            {
                int key = (tableIdx << 8) | seat;
                if (_applied.TryGetValue(key, out var applied) && applied.Set != null)
                {
                    // Use the object captured when the mirror was applied. The list entry at
                    // tableIdx may now be a different table after furniture reindexing.
                    if (!IsLocalGameplayTable(applied.Table))
                        applied.Set.gameObject.SetActive(false);
                }
                _applied.Remove(key);
            }
        }

        /// <summary>Client: hide every mirror we activated (disconnect / scene reset).
        /// Only seats we tracked are touched, and only to deactivate - a joiner's own
        /// live minigame table is never in _applied (skipped at apply time).</summary>
        private void ClearMirrors()
        {
            if (_applied.Count == 0)
                return;
            var sm = _sm; // cached only - never FindFirstObjectByType during teardown
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables != null)
            {
                foreach (var kv in _applied)
                {
                    if (!kv.Value.State.Active)
                        continue;
                    try
                    {
                        if (kv.Value.Set != null && !IsLocalGameplayTable(kv.Value.Table))
                            kv.Value.Set.gameObject.SetActive(false);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
            }
            _applied.Clear();
        }

        private static bool SameVisual(TableEntry left, TableEntry right)
        {
            if (left.Occupied != right.Occupied || left.Seats == null || right.Seats == null
                || left.Seats.Count != right.Seats.Count)
                return false;
            for (int i = 0; i < left.Seats.Count; i++)
            {
                var a = left.Seats[i];
                var b = right.Seats[i];
                if (a == null || b == null || a.Active != b.Active || a.PlayerSeat != b.PlayerSeat
                    || a.PlayMat != b.PlayMat || a.DeckBox != b.DeckBox || a.Comic != b.Comic)
                    return false;
            }
            return true;
        }
    }
}
