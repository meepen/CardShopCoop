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

        private struct SeatState
        {
            public bool Active;
            public bool PlayerSeat; // game 1.0: real-player seat
            public int PlayMat, DeckBox, Comic; // EItemType values

            public bool Same(SeatState o)
            {
                return Active == o.Active && PlayerSeat == o.PlayerSeat && PlayMat == o.PlayMat
                    && DeckBox == o.DeckBox && Comic == o.Comic;
            }
        }

        /// <summary>Set by CoopCore: host -> clients state broadcast (MsgType.TableState).</summary>
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;
        public static PlayTableSync Active;
        private static PlayerIntentBus _intents;

        private ShelfManager _sm;
        private float _sweepTimer;
        private int _sweepCursor;

        // client: last state applied per (tableIdx<<8 | seat), so a heal broadcast
        // does not re-run SpecificSetup (it re-randomizes deckbox/comic positions
        // every call - reapplying unchanged data would make the props jump around)
        private readonly Dictionary<int, SeatState> _applied = new Dictionary<int, SeatState>();
        private readonly Dictionary<int, bool> _occupied = new Dictionary<int, bool>();

        // TableState remains host->client-only. The kick is a separate single-shot intent;
        // the joiner never edits the local mirror or charges money.
        public PlayTableSync()
        {
            Active = this;
        }

        public override string Name => nameof(PlayTableSync);

        public override void Start() => Active = this;
        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public void RegisterIntents(PlayerIntentBus bus)
        {
            if (bus == null)
                throw new ArgumentNullException("bus");
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
                h.Patch(stopped, postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(TableChangedPostfix)));
            var playerSat = AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp");
            if (playerSat == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.OnRightMouseButtonUp");
            else
                h.Patch(playerSat, postfix: new HarmonyMethod(typeof(PlayTableSync), nameof(TableChangedPostfix)));

            // Game 1.0 adds a playable player-vs-player duel started from the table's
            // right-click (OnRightMouseButtonUp -> PlayCardGameManager.SetPlayTable). Its match
            // state is NOT synchronized, so a client starting one locally would diverge from the
            // host's shop. Gate it host-only until real duel sync exists.
            var duel = AccessTools.Method(typeof(InteractablePlayTable), "OnRightMouseButtonUp");
            if (duel == null)
                CoopPlugin.Log.LogWarning("PlayTableSync patch target missing: InteractablePlayTable.OnRightMouseButtonUp");
            else
                h.Patch(duel, prefix: new HarmonyMethod(typeof(PlayTableSync), nameof(BlockClientPlayerDuelPrefix)));
        }

        /// <summary>Client: refuse to begin a local player duel - its game state is unsynchronized
        /// and would diverge from the host. The host owns player matches for now.</summary>
        public static bool BlockClientPlayerDuelPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "player duels are not synchronized yet - the host runs them";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
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

        public override void Reset()
        {
            ClearMirrors();
            _applied.Clear();
            _occupied.Clear();
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _sm = null;
        }

        public override void ForceResend()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Active, this))
                Active = null;
            _intents = null;
        }

        private ShelfManager Sm()
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
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
                    SendTableNow(index);
                }
            });
        }

        public override void FullUpdate(int connId)
        {
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

        private void SendTableNow(int index)
        {
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables == null || index < 0 || index >= tables.Count || index >= MaxTables || BroadcastState == null)
                return;
            var entry = BuildEntry(tables[index], index);
            BroadcastState(new TableStateMessage
            {
                Full = false,
                Index = index,
                Tables = new List<TableEntry> { entry }
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
            var msg = new TableStateMessage();
            for (int i = 0; i < count; i++)
                msg.Tables.Add(BuildEntry(tables[i], i));
            return msg;
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
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            for (int i = 0; i < message.Tables.Count; i++)
            {
                var entry = message.Tables[i];
                int tableIdx = entry.Index;
                _occupied[tableIdx] = entry.Occupied;
                var seats = entry.Seats;
                InteractablePlayTable table =
                    (tables != null && tableIdx < tables.Count) ? tables[tableIdx] : null;
                // the JOINER may be playing the minigame at this table right now -
                // never stomp their own session's props (covers m_IsPlayerOccupied too)
                bool skipTable = table == null || table.GetHasStartPlayerPlayCard();
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
        }

        public static bool StartMoveObjectPrefix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role != CoopRole.Client || __instance == null)
                return true;
            if (__instance.GetHasStartPlayerPlayCard())
                return true;
            if (!__instance.GetIsTournamentPlayTable() && Active != null
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

        private void ApplySeat(int tableIdx, int seat, TableGameItemSet set, SeatState want)
        {
            if (set == null)
                return;
            int key = (tableIdx << 8) | seat;
            if (_applied.TryGetValue(key, out var have) && have.Same(want)
                && set.gameObject.activeSelf == want.Active)
                return;
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
                _applied[key] = want;
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
                var list = table != null ? table.m_IsPlayerSeat : null;
                if (list != null && seat >= 0 && seat < list.Count)
                    list[seat] = playerSeat;
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        /// <summary>Client: hide every mirror we activated (disconnect / scene reset).
        /// Only seats we tracked are touched, and only to deactivate - a joiner's own
        /// live minigame table is never in _applied (skipped at apply time).</summary>
        private void ClearMirrors()
        {
            if (_applied.Count == 0)
                return;
            var sm = _sm; // cached only - never FindObjectOfType during teardown
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables != null)
            {
                foreach (var kv in _applied)
                {
                    if (!kv.Value.Active)
                        continue;
                    int tableIdx = kv.Key >> 8;
                    int seat = kv.Key & 0xFF;
                    try
                    {
                        if (tableIdx >= tables.Count || tables[tableIdx] == null)
                            continue;
                        var sets = tables[tableIdx].m_TableGameItemSetList;
                        if (sets != null && seat < sets.Count && sets[seat] != null)
                            sets[seat].gameObject.SetActive(false);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
            }
            _applied.Clear();
        }
    }
}
