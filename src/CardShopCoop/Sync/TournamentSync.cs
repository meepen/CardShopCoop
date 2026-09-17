using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors tournament DATA and scheduling host->client (MsgType.TournamentState).
    /// The customer bracket only exists in the host simulation, so the joiner gets
    /// CPlayerData.m_TournamentData (schedule, fee, sign-ups, round, prize catalog)
    /// plus a per-customer digest of CustomerTournamentData - enough for the phone
    /// app (HostTournamentScreen reads m_TournamentData live on open) and for the
    /// physical pairing board, which we drive directly through TournamentPairingScreen
    /// because RefreshAllCustomerData wants live Customer objects the joiner never has.
    /// There are NO client ops: scheduling is host-only, so the joiner's confirm/cancel
    /// buttons are blocked with a "the host schedules tournaments" toast instead of
    /// being forwarded. Prize shelf CONTENTS are synced elsewhere (CardShelfSync).
    /// </summary>
    public class TournamentSync : TickableCoopModule
    {
        public static TournamentSync Instance;
        /// <summary>TournamentPrizeShelf.m_ScreenMesh (the shelf's little tournament display) is
        /// absent from the Game Pass Assembly-CSharp, which made a direct field access fail to
        /// COMPILE against that build - one cosmetic toggle taking the whole universal DLL down
        /// with it. Resolved once through reflection instead, so a missing or renamed field just
        /// disables the show/hide. Null when the field isn't there; every use is null-guarded.
        /// (Reflection change contributed by Jburne10 for the Game Pass build.)</summary>
        private static readonly FieldInfo FiScreenMesh =
            AccessTools.Field(typeof(TournamentPrizeShelf), "m_ScreenMesh");

        /// <summary>Set by CoopCore: host -> clients state broadcast.</summary>
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;

        /// <summary>True while ClientApplyState writes CPlayerData.m_TournamentData, so
        /// no patch mistakes the authoritative copy for a local scheduling action.</summary>
        public static bool ApplyingRemote;

        private const float SweepSliceSeconds = 0.5f;
        // BuildState caps the bracket at 64 entries; eight-entry batches therefore fit exactly
        // in the eight hash slots below, and SliceCount's maximum is 9 + 8 = 17 slices.
        private const int BracketBatchSize = 8;
        private float _sweepTimer;
        private int _sweepCursor;
        private int _bracketEpoch = 1;
        private int _lastHeaderHash;
        private bool _hasHeader;
        private readonly int[] _lastPrizeHash = new int[8];
        private readonly bool[] _hasPrize = new bool[8];
        private readonly int[] _lastBracketHash = new int[8];
        private readonly bool[] _hasBracket = new bool[8];
        private int _clientBracketEpoch;
        private readonly List<PairingEntry> _clientBracket = new List<PairingEntry>();
        private int _clientRenderHash;
        private bool _hasClientRenderHash;
        public TournamentSync()
        {
            Instance = this;
        }
        public override void Start()
        {
            Instance = this;
        }

        // NEVER CSingleton<CustomerManager>.Instance: touched while no real manager
        // exists (client reload loading screen, host mid-session save load) the getter
        // fabricates a fake empty DontDestroyOnLoad manager that shadows the real one
        // for the rest of the run (see WorldSync.ResolveShelfManager). Static because
        // the wire writer and hash are static; fake-null re-resolves after scene loads.
        private static CustomerManager _cm;

        private static CustomerManager Cm()
        {
            if (_cm == null)
                _cm = UnityEngine.Object.FindObjectOfType<CustomerManager>();
            return _cm;
        }

        public override string Name => "tournament";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Instance, this))
                Instance = null;
            ApplyingRemote = false;
            _cm = null;
        }

        public override void Reset()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _bracketEpoch = 1;
            _hasHeader = false;
            for (int i = 0; i < 8; i++)
            {
                _hasPrize[i] = false;
                _hasBracket[i] = false;
            }
            _clientBracketEpoch = 0;
            _clientBracket.Clear();
            _clientRenderHash = 0;
            _hasClientRenderHash = false;
            _cm = null;
        }

        public override void ForceResend()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _hasHeader = false;
            for (int i = 0; i < 8; i++)
            {
                _hasPrize[i] = false;
                _hasBracket[i] = false;
            }
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Scheduling, cancelling and prize setup all mutate m_TournamentData and the
            // prize shelf plan - host-only decisions, since only the host's customer sim
            // can actually run the event. The joiner's phone screen stays readable (it
            // renders the synced data) but its buttons do nothing except explain why.
            Try(h, typeof(HostTournamentScreen), "OnPressConfirm",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressCancel",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "ConfirmCancelTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPrizeSetup",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            // Game 1.0 added player sign-up / sign-out buttons. They mutate
            // CPlayerData.m_IsPlayerRegisteredForTournament and the signed-up count, neither of
            // which is on the tournament wire, so a guest's press would desync. Host-only.
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignOutTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ScheduleBlockPrefix)));
            // These methods own the authoritative mutations in both game builds. Postfixes push
            // only the affected block; the sweep below is the bounded loss-recovery path.
            Try(h, typeof(HostTournamentScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(HeaderChangedPostfix)));
            Try(h, typeof(HostTournamentScreen), "ConfirmCancelTournament",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(HeaderChangedPostfix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(HeaderChangedPostfix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignOutTournament",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(HeaderChangedPostfix)));
            Try(h, typeof(HostTournamentSelectPrizeScreen), "OnPressSelectPrizeItem",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(PrizeChangedPostfix)));
            Try(h, typeof(HostTournamentSelectPrizeScreen), "OnPressSelectPrizeCard",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(PrizeChangedPostfix)));
            Try(h, typeof(CustomerManager), "OnDayStarted",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(DayStartedPrefix)),
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(DayStartedPostfix)));
            Try(h, typeof(CustomerManager), "OnCustomerFinishTournamentRound",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(BracketChangedPostfix)));
        }

        public static bool ScheduleBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "the host schedules tournaments";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        private static void Notify(TournamentSync self, int kind)
        {
            if (self == null || ApplyingRemote || CoopCore.Role != CoopRole.Host || !CoopCore.InSessionWorld)
                return;
            self.Guarded("change", () =>
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null)
                    return;
                var state = self.BuildState(td);
                if (kind == 0)
                    self.SendSliceIfChanged(state, 0);
                else if (kind == 1)
                {
                    for (int i = 0; i < Mathf.Min(8, state.PrizeSlots.Count); i++)
                        self.SendSliceIfChanged(state, i + 1);
                }
                else
                {
                    int offset = 9;
                    int batches = (state.Bracket.Count + BracketBatchSize - 1) / BracketBatchSize;
                    for (int i = 0; i < batches; i++)
                        self.SendSliceIfChanged(state, offset + i);
                }
            });
        }

        public static void HeaderChangedPostfix() => Notify(FindInstance(), 0);
        public static void PrizeChangedPostfix() => Notify(FindInstance(), 1);
        public static void BracketChangedPostfix() => Notify(FindInstance(), 2);
        /// <summary>Advance the identity before vanilla clears the old bracket.</summary>
        public static void DayStartedPrefix()
        {
            var self = FindInstance();
            if (self != null)
                self._bracketEpoch++;
        }

        public static void DayStartedPostfix()
        {
            var self = FindInstance();
            Notify(self, 0);
            Notify(self, 2);
        }

        private static TournamentSync FindInstance()
        {
            return Instance;
        }

        private void SendSliceIfChanged(TournamentStateMessage state, int index)
        {
            if (BroadcastState == null)
                return;
            if (index > 0 && index <= 8 && index > state.PrizeSlots.Count)
                return;
            int hash = SliceHash(state, index);
            int slot = index - 1;
            bool changed;
            if (index == 0)
            {
                changed = !_hasHeader || _lastHeaderHash != hash;
                _lastHeaderHash = hash;
                _hasHeader = true;
            }
            else if (slot < 8)
            {
                changed = !_hasPrize[slot] || _lastPrizeHash[slot] != hash;
                _lastPrizeHash[slot] = hash;
                _hasPrize[slot] = true;
            }
            else
            {
                int b = slot - 8;
                changed = !_hasBracket[b] || _lastBracketHash[b] != hash;
                _lastBracketHash[b] = hash;
                _hasBracket[b] = true;
            }
            if (!changed)
                return;
            SendSlice(state, index);
        }

        /// <summary>Periodic re-assertion deliberately bypasses all change detection.</summary>
        private void SendSliceUnconditionally(TournamentStateMessage state, int index)
        {
            if (index > 0 && index <= 8 && index > state.PrizeSlots.Count)
                return;
            SendSlice(state, index);
        }

        private void SendSlice(TournamentStateMessage state, int index)
        {
            int slot = index - 1;
            var slice = new TournamentStateMessage { Full = false, Index = index, BracketEpoch = state.BracketEpoch };
            if (index == 0)
                CopyHeader(state, slice);
            else if (slot < state.PrizeSlots.Count)
                slice.PrizeSlots.Add(state.PrizeSlots[slot]);
            else
            {
                int start = (slot - 8) * BracketBatchSize;
                for (int i = start; i < start + BracketBatchSize && i < state.Bracket.Count; i++)
                    slice.Bracket.Add(state.Bracket[i]);
            }
            BroadcastState(slice);
        }

        private static void CopyHeader(TournamentStateMessage a, TournamentStateMessage b)
        {
            b.Flags = a.Flags;
            b.MaxPlayerCount = a.MaxPlayerCount;
            b.SignedUpCustomerCount = a.SignedUpCustomerCount;
            b.FinishedCurrentRoundCustomerCount = a.FinishedCurrentRoundCustomerCount;
            b.CurrentRound = a.CurrentRound;
            b.MaxRound = a.MaxRound;
            b.Fee = a.Fee;
            b.TotalValue = a.TotalValue;
            b.IsPlayerRegistered = a.IsPlayerRegistered;
            b.PlayerIsTournamentCustomer = a.PlayerIsTournamentCustomer;
            b.PlayerIsTournamentWin = a.PlayerIsTournamentWin;
            b.PlayerHasRegisteredResult = a.PlayerHasRegisteredResult;
            b.PlayerTournamentCustomerPlayTableIndex = a.PlayerTournamentCustomerPlayTableIndex;
            b.PlayerTournamentWinCount = a.PlayerTournamentWinCount;
            b.PlayerTournamentWinPoints = a.PlayerTournamentWinPoints;
            b.PlayerTournamentPlacementIndex = a.PlayerTournamentPlacementIndex;
        }

        private static int SliceHash(TournamentStateMessage s, int index)
        {
            unchecked
            {
                int h = 17 + index * 31 + s.BracketEpoch;
                if (index == 0)
                    return HeaderHash(s, h);
                int p = index - 1;
                if (p < 8)
                {
                    if (p < s.PrizeSlots.Count)
                    {
                        var x = s.PrizeSlots[p];
                        for (int i = 0; i < x.Prizes.Count; i++)
                        {
                            var e = x.Prizes[i];
                            h = h * 31 + (e.HasCard ? 1 : 0);
                            h = h * 31 + (int)e.ItemType;
                            h = h * 31 + e.Count;
                        }
                    }
                    return h;
                }
                int start = (p - 8) * BracketBatchSize;
                for (int i = start; i < start + BracketBatchSize && i < s.Bracket.Count; i++)
                {
                    var e = s.Bracket[i];
                    h = h * 31 + e.SortedIndex;
                    h = h * 31 + e.ModelIndex;
                    h = h * 31 + e.Flags;
                    h = h * 31 + e.WinCount;
                    h = h * 31 + e.WinPoints;
                    h = h * 31 + e.OMW;
                    h = h * 31 + e.OOMW;
                }
                return h;
            }
        }

        private static int HeaderHash(TournamentStateMessage s, int h)
        {
            unchecked
            {
                h = h * 31 + s.Flags;
                h = h * 31 + s.MaxPlayerCount;
                h = h * 31 + s.SignedUpCustomerCount;
                h = h * 31 + s.CurrentRound;
                h = h * 31 + s.MaxRound;
                h = h * 31 + (int)(s.Fee * 100);
                h = h * 31 + (int)(s.TotalValue * 100);
                h = h * 31 + (s.IsPlayerRegistered ? 1 : 0);
                h = h * 31 + (s.PlayerIsTournamentCustomer ? 1 : 0);
                h = h * 31 + (s.PlayerIsTournamentWin ? 1 : 0);
                h = h * 31 + (s.PlayerHasRegisteredResult ? 1 : 0);
                h = h * 31 + s.PlayerTournamentCustomerPlayTableIndex;
                h = h * 31 + s.PlayerTournamentWinCount;
                h = h * 31 + s.PlayerTournamentWinPoints;
                h = h * 31 + s.PlayerTournamentPlacementIndex;
                return h;
            }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
        }

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
                var td = CPlayerData.m_TournamentData;
                if (td == null)
                    return;
                int total = SliceCount(td);
                if (total == 0)
                {
                    _sweepCursor = 0;
                    return;
                }
                if (_sweepCursor >= total)
                    _sweepCursor = 0;
                var state = BuildState(td);
                int index = _sweepCursor++ % total;
                // The sweep is deliberately unconditional: its job is to repair a lost
                // message even when the host-side value has not changed since the last pass.
                SendSliceUnconditionally(state, index);
            });
        }

        public override void FullUpdate(Connection connection)
        {
            int connId = connection.Id;
            if (CoopCore.Role != CoopRole.Host || SendToClient == null || !CoopCore.InSessionWorld)
                return;
            Guarded("full", () =>
            {
                var message = BuildState(CPlayerData.m_TournamentData);
                message.Full = true;
                message.Index = -1;
                SendToClient(connId, message);
            });
        }

        private static int SliceCount(TournamentData td)
        {
            var cm = Cm();
            int bracket = cm == null || cm.m_TournamentSortedCustomerList == null ? 0 : Mathf.Min(64, cm.m_TournamentSortedCustomerList.Count);
            return 9 + (bracket + BracketBatchSize - 1) / BracketBatchSize;
        }

        // No HostApplyOp / SendOp: the joiner never sends tournament ops - scheduling
        // is blocked client-side with a toast rather than forwarded.

        // ---------------- client ----------------

        public void ClientApplyState(TournamentStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(TournamentStateMessage message)
        {
            if (message.BracketEpoch < _clientBracketEpoch)
            {
                CoopPlugin.Log.LogWarning("TournamentSync: ignored stale tournament state epoch "
                    + message.BracketEpoch + " (current " + _clientBracketEpoch + ")");
                return;
            } // a delayed slice from the previous bracket must not roll state back
            bool fullEpochChanged = message.Full && message.BracketEpoch != _clientBracketEpoch;
            if (fullEpochChanged)
            {
                _clientBracket.Clear();
                _clientBracketEpoch = message.BracketEpoch;
            }
            var td = CPlayerData.m_TournamentData;
            if (td == null)
            {
                CPlayerData.m_TournamentData = td = new TournamentData();
            }

            if (!message.Full)
            {
                ApplySlice(message, td);
                return;
            }
            byte flags = message.Flags;
            td.m_IsHostingTournament = (flags & 1) != 0;
            bool wasDay = td.m_IsTournamentDay;
            bool wasOver = td.m_IsTournamentDayOver;
            td.m_IsTournamentDay = (flags & 2) != 0;
            td.m_IsTournamentDayOver = (flags & 4) != 0;
            td.m_TournamentMaxPlayerCount = message.MaxPlayerCount;
            td.m_TournamentSignedUpCustomerCount = message.SignedUpCustomerCount;
            td.m_TournamentFinishedCurrentRoundCustomerCount = message.FinishedCurrentRoundCustomerCount;
            td.m_TournamentCurrentRound = message.CurrentRound;
            td.m_TournamentMaxRound = message.MaxRound;
            td.m_TournamentFee = message.Fee;
            td.m_TournamentTotalValue = message.TotalValue;

            // Player participation: host-authoritative. Sign-up/sign-out are blocked on clients,
            // so without this the guest's registration flag and player round data would go stale.
            CPlayerData.m_IsPlayerRegisteredForTournament = message.IsPlayerRegistered;
            var pdata = CPlayerData.m_PlayerTournamentData;
            if (pdata == null)
                CPlayerData.m_PlayerTournamentData = pdata = new CustomerTournamentData();
            pdata.m_IsTournamentCustomer = message.PlayerIsTournamentCustomer;
            pdata.m_IsTournamentWin = message.PlayerIsTournamentWin;
            pdata.m_HasRegisteredTournamentResult = message.PlayerHasRegisteredResult;
            pdata.m_TournamentCustomerPlayTableIndex = message.PlayerTournamentCustomerPlayTableIndex;
            pdata.m_TournamentWinCount = message.PlayerTournamentWinCount;
            pdata.m_TournamentWinPoints = message.PlayerTournamentWinPoints;
            pdata.m_TournamentPlacementIndex = message.PlayerTournamentPlacementIndex;

            // prize catalog: mutate the vanilla 4-slot list in place so screens that
            // index m_PrizeDataList[i] never see it shorter than they expect
            if (td.m_PrizeDataList == null)
                td.m_PrizeDataList = new List<TournamentPrizeDataList>();
            int lists = message.PrizeSlots.Count;
            while (td.m_PrizeDataList.Count < lists)
                td.m_PrizeDataList.Add(new TournamentPrizeDataList { m_PrizeDataList = new List<TournamentPrizeData>() });
            for (int i = 0; i < lists; i++)
            {
                var slot = td.m_PrizeDataList[i];
                if (slot.m_PrizeDataList == null)
                    slot.m_PrizeDataList = new List<TournamentPrizeData>();
                slot.m_PrizeDataList.Clear();
                var dtoSlot = message.PrizeSlots[i];
                for (int j = 0; j < dtoSlot.Prizes.Count; j++)
                {
                    var pe = dtoSlot.Prizes[j];
                    var p = new TournamentPrizeData();
                    if (pe.HasCard)
                        p.m_CardData = pe.Card;
                    // host id -> ours (already translated by the DTO deserialize); a prize
                    // from a pack only the host has becomes EItemType.None and the prize slot
                    // just shows nothing, which is what an unresolvable prize did before
                    // translation existed
                    p.m_ItemType = pe.ItemType;
                    p.m_Count = pe.Count;
                    slot.m_PrizeDataList.Add(p);
                }
            }

            // bracket digest
            _clientBracket.Clear();
            _clientBracketEpoch = message.BracketEpoch;
            int n = message.Bracket.Count;
            var digest = new List<PairingEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var be = message.Bracket[i];
                var e = new PairingEntry();
                e.SortedIndex = be.SortedIndex;
                e.ModelIndex = be.ModelIndex;
                byte f = be.Flags;
                e.IsFemale = (f & 1) != 0;
                e.IsWin = (f & 2) != 0;
                e.HasResult = (f & 4) != 0;
                e.WinCount = be.WinCount;
                e.WinPoints = be.WinPoints;
                e.OMW = be.OMW;
                e.OOMW = be.OOMW;
                digest.Add(e);
            }

            RefreshBoards(td, digest, fullEpochChanged
                || wasDay != td.m_IsTournamentDay || wasOver != td.m_IsTournamentDayOver);
        }

        private void ApplySlice(TournamentStateMessage message, TournamentData td)
        {
            int prizeCount = 8;
            if (message.Index <= prizeCount)
            {
                if (message.Index == 0)
                {
                    bool epochChanged = message.BracketEpoch > _clientBracketEpoch;
                    if (epochChanged)
                    {
                        _clientBracket.Clear();
                        _clientBracketEpoch = message.BracketEpoch;
                    }
                    bool wasDay = td.m_IsTournamentDay;
                    bool wasOver = td.m_IsTournamentDayOver;
                    ApplyHeader(message, td);
                    RefreshBoards(td, _clientBracket,
                        epochChanged || wasDay != td.m_IsTournamentDay || wasOver != td.m_IsTournamentDayOver);
                    return;
                }
                int slotIndex = message.Index - 1;
                if (td.m_PrizeDataList == null)
                    td.m_PrizeDataList = new List<TournamentPrizeDataList>();
                while (td.m_PrizeDataList.Count <= slotIndex)
                    td.m_PrizeDataList.Add(new TournamentPrizeDataList { m_PrizeDataList = new List<TournamentPrizeData>() });
                var list = td.m_PrizeDataList[slotIndex].m_PrizeDataList;
                list.Clear();
                var source = message.PrizeSlots.Count == 0 ? null : message.PrizeSlots[0];
                if (source != null)
                    for (int i = 0; i < source.Prizes.Count; i++)
                    {
                        var p = source.Prizes[i];
                        list.Add(new TournamentPrizeData { m_CardData = p.HasCard ? p.Card : null, m_ItemType = p.ItemType, m_Count = p.Count });
                    }
                return;
            }
            bool bracketEpochChanged = _clientBracketEpoch != message.BracketEpoch;
            if (bracketEpochChanged)
            {
                _clientBracket.Clear();
                _clientBracketEpoch = message.BracketEpoch;
            }
            int batch = message.Index - 9;
            if (batch < 0 || batch >= _lastBracketHash.Length)
            {
                CoopPlugin.Log.LogWarning("TournamentSync: bracket slice is outside the 64-entry bracket cap: "
                    + message.Index);
                return;
            }
            for (int i = 0; i < message.Bracket.Count; i++)
            {
                var b = message.Bracket[i];
                int found = _clientBracket.FindIndex(x => x.SortedIndex == b.SortedIndex);
                var e = ToPairingEntry(b);
                if (found >= 0)
                    _clientBracket[found] = e;
                else
                    _clientBracket.Add(e);
            }
            RefreshBoards(td, _clientBracket, bracketEpochChanged);
        }

        private static void ApplyHeader(TournamentStateMessage message, TournamentData td)
        {
            td.m_IsHostingTournament = (message.Flags & 1) != 0;
            td.m_IsTournamentDay = (message.Flags & 2) != 0;
            td.m_IsTournamentDayOver = (message.Flags & 4) != 0;
            td.m_TournamentMaxPlayerCount = message.MaxPlayerCount;
            td.m_TournamentSignedUpCustomerCount = message.SignedUpCustomerCount;
            td.m_TournamentFinishedCurrentRoundCustomerCount = message.FinishedCurrentRoundCustomerCount;
            td.m_TournamentCurrentRound = message.CurrentRound;
            td.m_TournamentMaxRound = message.MaxRound;
            td.m_TournamentFee = message.Fee;
            td.m_TournamentTotalValue = message.TotalValue;
            CPlayerData.m_IsPlayerRegisteredForTournament = message.IsPlayerRegistered;
            var pd = CPlayerData.m_PlayerTournamentData ?? (CPlayerData.m_PlayerTournamentData = new CustomerTournamentData());
            pd.m_IsTournamentCustomer = message.PlayerIsTournamentCustomer;
            pd.m_IsTournamentWin = message.PlayerIsTournamentWin;
            pd.m_HasRegisteredTournamentResult = message.PlayerHasRegisteredResult;
            pd.m_TournamentCustomerPlayTableIndex = message.PlayerTournamentCustomerPlayTableIndex;
            pd.m_TournamentWinCount = message.PlayerTournamentWinCount;
            pd.m_TournamentWinPoints = message.PlayerTournamentWinPoints;
            pd.m_TournamentPlacementIndex = message.PlayerTournamentPlacementIndex;
        }

        private static PairingEntry ToPairingEntry(TournamentBracketEntry b)
        {
            return new PairingEntry
            {
                SortedIndex = b.SortedIndex,
                ModelIndex = b.ModelIndex,
                IsFemale = (b.Flags & 1) != 0,
                IsWin = (b.Flags & 2) != 0,
                HasResult = (b.Flags & 4) != 0,
                WinCount = b.WinCount,
                WinPoints = b.WinPoints,
                OMW = b.OMW,
                OOMW = b.OOMW
            };
        }

        /// <summary>Client: the pairing board and shelf screen mesh are normally driven
        /// by day-start events, which the mod suppresses on the joiner - so we gate them
        /// here, exactly the way TournamentPrizeShelf.CheckTournamentScreenVisibility does.</summary>
        private void RefreshBoards(TournamentData td, List<PairingEntry> digest, bool visibilityChanged)
        {
            var cm = Cm();
            bool showBoard = td.m_IsTournamentDay || td.m_IsTournamentDayOver;

            // Visibility is independent of the pairing screen reference. The screen can be
            // created after a header arrives, so do not consume a day transition before the
            // board root and shelf meshes have had a chance to be activated.
            if (cm != null)
            {
                try
                {
                    if (cm.m_TournamentPairingScreen != null)
                        cm.m_TournamentPairingScreen.gameObject.SetActive(showBoard);
                    var shelves = ShelfManager.GetTournamentPrizeShelfList();
                    for (int i = 0; i < shelves.Count; i++)
                    {
                        if (shelves[i] == null || FiScreenMesh == null)
                            continue;
                        var mesh = FiScreenMesh.GetValue(shelves[i]) as GameObject;
                        if (mesh != null)
                            mesh.SetActive(showBoard);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync board vis: " + e.Message); }
            }
            if (cm == null || cm.m_TournamentPairingScreen == null)
                return;
            var screen = cm.m_TournamentPairingScreen;
            if (showBoard && !screen.gameObject.activeSelf)
                screen.gameObject.SetActive(true);
            int renderHash = RenderHash(digest, td.m_TournamentCurrentRound, td.m_TournamentMaxRound,
                td.m_TournamentMaxPlayerCount);
            if (!visibilityChanged && _hasClientRenderHash && renderHash == _clientRenderHash)
                return;
            _clientRenderHash = renderHash;
            _hasClientRenderHash = true;
            if (!showBoard)
            {
                screen.ShowPairingScreen(isShow: false, 0);
                return;
            }

            // full repaint: ShowPairingScreen resets the panels, then we repopulate from
            // the digest with fabricated CustomerTournamentData - UpdateCustomerData only
            // reads the scalar fields we carry
            screen.ShowPairingScreen(isShow: true, td.m_TournamentMaxPlayerCount);
            screen.UpdateCurrentRound(td.m_TournamentCurrentRound, td.m_TournamentMaxRound);
            int panels = screen.m_TournamentPairingUIGrpList != null ? screen.m_TournamentPairingUIGrpList.Count : 0;
            for (int i = 0; i < digest.Count; i++)
            {
                var e = digest[i];
                if (e.SortedIndex / 2 >= panels)
                    continue;
                screen.OnCustomerRegisterStart(e.SortedIndex, e.ModelIndex, e.IsFemale);
                var ctd = new CustomerTournamentData
                {
                    m_TournamentCustomerSortedIndex = e.SortedIndex,
                    m_IsTournamentWin = e.IsWin,
                    m_HasRegisteredTournamentResult = e.HasResult,
                    m_TournamentWinCount = e.WinCount,
                    m_TournamentWinPoints = e.WinPoints,
                    m_TournamentOMW = e.OMW,
                    m_TournamentOOMW = e.OOMW,
                };
                screen.m_TournamentPairingUIGrpList[e.SortedIndex / 2].UpdateCustomerData(ctd);
            }
        }

        private static int RenderHash(List<PairingEntry> digest, int currentRound, int maxRound, int maxPlayerCount)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + currentRound;
                hash = hash * 31 + maxRound;
                hash = hash * 31 + maxPlayerCount;
                for (int i = 0; i < digest.Count; i++)
                {
                    var e = digest[i];
                    hash = hash * 31 + e.SortedIndex;
                    hash = hash * 31 + e.ModelIndex;
                    hash = hash * 31 + (e.IsFemale ? 1 : 0);
                    hash = hash * 31 + (e.IsWin ? 1 : 0);
                    hash = hash * 31 + (e.HasResult ? 1 : 0);
                    hash = hash * 31 + e.WinCount;
                    hash = hash * 31 + e.WinPoints;
                    hash = hash * 31 + e.OMW;
                    hash = hash * 31 + e.OOMW;
                }
                return hash;
            }
        }

        private struct PairingEntry
        {
            public int SortedIndex;
            public int ModelIndex;
            public bool IsFemale;
            public bool IsWin;
            public bool HasResult;
            public int WinCount;
            public int WinPoints;
            public int OMW;
            public int OOMW;
        }

        // ---------------- wire / hash ----------------

        private TournamentStateMessage BuildState(TournamentData td)
        {
            var msg = new TournamentStateMessage
            {
                BracketEpoch = _bracketEpoch,
                Flags = (byte)((td.m_IsHostingTournament ? 1 : 0)
                             | (td.m_IsTournamentDay ? 2 : 0)
                             | (td.m_IsTournamentDayOver ? 4 : 0)),
                MaxPlayerCount = td.m_TournamentMaxPlayerCount,
                SignedUpCustomerCount = td.m_TournamentSignedUpCustomerCount,
                FinishedCurrentRoundCustomerCount = td.m_TournamentFinishedCurrentRoundCustomerCount,
                CurrentRound = td.m_TournamentCurrentRound,
                MaxRound = td.m_TournamentMaxRound,
                Fee = td.m_TournamentFee,
                TotalValue = td.m_TournamentTotalValue,
                IsPlayerRegistered = CPlayerData.m_IsPlayerRegisteredForTournament,
            };

            var pd = CPlayerData.m_PlayerTournamentData;
            if (pd != null)
            {
                msg.PlayerIsTournamentCustomer = pd.m_IsTournamentCustomer;
                msg.PlayerIsTournamentWin = pd.m_IsTournamentWin;
                msg.PlayerHasRegisteredResult = pd.m_HasRegisteredTournamentResult;
                msg.PlayerTournamentCustomerPlayTableIndex = pd.m_TournamentCustomerPlayTableIndex;
                msg.PlayerTournamentWinCount = pd.m_TournamentWinCount;
                msg.PlayerTournamentWinPoints = pd.m_TournamentWinPoints;
                msg.PlayerTournamentPlacementIndex = pd.m_TournamentPlacementIndex;
            }

            var lists = td.m_PrizeDataList;
            int lc = lists != null ? Mathf.Min(lists.Count, 8) : 0;
            for (int i = 0; i < lc; i++)
            {
                var slot = new TournamentPrizeSlot();
                var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                int ec = inner != null ? Mathf.Min(inner.Count, 64) : 0;
                for (int j = 0; j < ec; j++)
                {
                    var p = inner[j];
                    bool hasCard = p != null && p.m_CardData != null;
                    slot.Prizes.Add(new TournamentPrizeEntry
                    {
                        HasCard = hasCard,
                        Card = hasCard ? p.m_CardData : null,
                        // item prizes are EItemTypes (a modded id space) - the card above
                        // already goes through the WriteCard chokepoint
                        ItemType = p != null ? p.m_ItemType : (EItemType)0,
                        Count = p != null ? p.m_Count : 0,
                    });
                }
                msg.PrizeSlots.Add(slot);
            }

            // bracket digest straight from the host's live sorted list (the same list
            // the vanilla pairing board renders from)
            var cm = Cm();
            var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
            int n = sorted != null ? Mathf.Min(sorted.Count, 64) : 0;
            for (int i = 0; i < n; i++)
            {
                var c = sorted[i];
                var ctd = c != null ? c.GetCustomerTournamentData() : null;
                if (ctd == null)
                {
                    var player = CPlayerData.m_PlayerTournamentData;
                    if (player != null && player.m_IsTournamentCustomer
                        && player.m_TournamentCustomerSortedIndex == i)
                    {
                        msg.Bracket.Add(new TournamentBracketEntry
                        {
                            SortedIndex = (byte)Mathf.Clamp(player.m_TournamentCustomerSortedIndex, 0, 255),
                            ModelIndex = player.m_CharacterModelIndex,
                            Flags = (byte)((player.m_IsFemale ? 1 : 0)
                                | (player.m_IsTournamentWin ? 2 : 0)
                                | (player.m_HasRegisteredTournamentResult ? 4 : 0)),
                            WinCount = player.m_TournamentWinCount,
                            WinPoints = player.m_TournamentWinPoints,
                            OMW = player.m_TournamentOMW,
                            OOMW = player.m_TournamentOOMW,
                        });
                    }
                    continue;
                }
                msg.Bracket.Add(new TournamentBracketEntry
                {
                    SortedIndex = (byte)Mathf.Clamp(ctd.m_TournamentCustomerSortedIndex, 0, 255),
                    ModelIndex = c.GetCustomerModelIndex(),
                    Flags = (byte)((c.m_IsFemale ? 1 : 0)
                                 | (ctd.m_IsTournamentWin ? 2 : 0)
                                 | (ctd.m_HasRegisteredTournamentResult ? 4 : 0)),
                    WinCount = ctd.m_TournamentWinCount,
                    WinPoints = ctd.m_TournamentWinPoints,
                    OMW = ctd.m_TournamentOMW,
                    OOMW = ctd.m_TournamentOOMW,
                });
            }
            return msg;
        }

    }
}



