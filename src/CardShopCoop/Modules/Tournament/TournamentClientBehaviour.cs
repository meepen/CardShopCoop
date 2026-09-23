using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Tournament
{
    /// <summary>Applies host tournament snapshots and renders the host-only bracket digest.</summary>
    [ClientBehaviour]
    public sealed class TournamentClientBehaviour : CoopBehaviour
    {
        private static TournamentClientBehaviour _active;
        private static readonly FieldInfo ScreenMesh =
            AccessTools.Field(typeof(TournamentPrizeShelf), "m_ScreenMesh");

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private TournamentStateMessage _pendingState;
        private TournamentDeltaMessage _pendingHeader;
        private readonly Dictionary<int, TournamentDeltaMessage> _pendingPrizes = new();
        private readonly Dictionary<int, TournamentBracketEntry> _pendingBracket = new();
        private int _pendingBracketCount = -1;
        private readonly List<PairingEntry> _clientBracket = new();
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            var registered = false;
            try
            {
                ResetSessionState();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tournament.client.runtime");
                ApplyPatches(_harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tournament client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                ResetSessionState();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(TournamentStateMessage))]
        private void HandleState(MessageContext context, TournamentStateMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            _pendingState = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(TournamentDeltaMessage))]
        private void HandleDelta(MessageContext context, TournamentDeltaMessage message)
        {
            if (_shutdown)
                return;
            if (message.Kind == 0)
            {
                _pendingHeader = message;
            }
            else if (message.Kind == 1)
            {
                _pendingPrizes[message.PrizeIndex] = message;
            }
            else
            {
                _pendingBracketCount = message.BracketCount;
                for (var i = 0; i < message.Bracket.Count; i++)
                {
                    var entry = message.Bracket[i];
                    _pendingBracket[entry.SortedIndex] = entry;
                }
            }
            TryApplyPending();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _pendingState = null;
            ClearPendingDeltas();
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
            _harmony = null;
            ResetSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void ResetSessionState()
        {
            _clientBracket.Clear();
            ClearPendingDeltas();
        }

        private void ClearPendingDeltas()
        {
            _pendingHeader = null;
            _pendingPrizes.Clear();
            _pendingBracket.Clear();
            _pendingBracketCount = -1;
        }

        private static CustomerManager CustomerManagerInScene()
        {
            return SceneRef<CustomerManager>.Get();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                ResetSessionState();
            }
        }

        private bool IsTournamentUiReady()
        {
            var manager = CustomerManagerInScene();
            return manager != null && manager.m_TournamentPairingScreen != null
                && manager.m_TournamentPairingScreen.m_TournamentPairingUIGrpList != null;
        }

        private void TryApplyPending()
        {
            if (_shutdown || !_context.InGame() || !IsTournamentUiReady())
            {
                return;
            }

            if (_pendingState != null)
            {
                var pending = _pendingState;
                _pendingState = null;
                ApplyMessage(pending);
            }

            if (_pendingHeader != null)
            {
                var pending = _pendingHeader;
                _pendingHeader = null;
                ApplyDelta(pending);
            }

            if (_pendingPrizes.Count > 0)
            {
                var pending = new List<TournamentDeltaMessage>(_pendingPrizes.Values);
                _pendingPrizes.Clear();
                for (var i = 0; i < pending.Count; i++)
                {
                    ApplyDelta(pending[i]);
                }
            }

            if (_pendingBracketCount >= 0)
            {
                var bracketCount = _pendingBracketCount;
                var pending = new List<TournamentBracketEntry>(_pendingBracket.Values);
                _pendingBracket.Clear();
                _pendingBracketCount = -1;
                ApplyBracketEntries(bracketCount, pending);
            }
        }

        private void ApplyMessage(TournamentStateMessage message)
        {
            ApplyFull(message);

        }

        private void ApplyDelta(TournamentDeltaMessage message)
        {
            var tournament = CPlayerData.m_TournamentData;
            if (tournament == null)
                CPlayerData.m_TournamentData = tournament = new TournamentData();
            if (message.Kind == 0)
            {
                ApplyHeader(message.Header, tournament);
            }
            else if (message.Kind == 1)
            {
                EnsurePrizeLists(tournament, message.PrizeIndex + 1);
                ReplacePrizeSlot(tournament.m_PrizeDataList[message.PrizeIndex], message.Prize);
            }
            else
            {
                ApplyBracketEntries(message.BracketCount, message.Bracket);
            }
            RefreshBoards(tournament, _clientBracket);
        }

        private void ApplyBracketEntries(int bracketCount, IList<TournamentBracketEntry> entries)
        {
            if (bracketCount <= 0)
            {
                _clientBracket.Clear();
                return;
            }

            for (var i = _clientBracket.Count - 1; i >= 0; i--)
            {
                if (_clientBracket[i].SortedIndex >= bracketCount)
                {
                    _clientBracket.RemoveAt(i);
                }
            }

            for (var i = 0; entries != null && i < entries.Count; i++)
            {
                var incoming = ToPairingEntry(entries[i]);
                var found = _clientBracket.FindIndex(value =>
                    value.SortedIndex == incoming.SortedIndex);
                if (found >= 0)
                {
                    _clientBracket[found] = incoming;
                }
                else
                {
                    _clientBracket.Add(incoming);
                }
            }

            _clientBracket.Sort((left, right) => left.SortedIndex.CompareTo(right.SortedIndex));
        }

        private void ApplyFull(TournamentStateMessage message)
        {
            _clientBracket.Clear();

            var tournament = CPlayerData.m_TournamentData;
            if (tournament == null)
            {
                CPlayerData.m_TournamentData = tournament = new TournamentData();
            }

            ApplyHeader(message, tournament);
            EnsurePrizeLists(tournament, message.PrizeSlots.Count);
            for (var i = 0; i < message.PrizeSlots.Count; i++)
            {
                ReplacePrizeSlot(tournament.m_PrizeDataList[i], message.PrizeSlots[i]);
            }

            _clientBracket.Clear();
            for (var i = 0; i < message.Bracket.Count; i++)
            {
                _clientBracket.Add(ToPairingEntry(message.Bracket[i]));
            }

            RefreshBoards(tournament, _clientBracket);
        }

        private static void ApplyHeader(TournamentStateMessage message, TournamentData tournament)
        {
            tournament.m_IsHostingTournament = (message.Flags & 1) != 0;
            tournament.m_IsTournamentDay = (message.Flags & 2) != 0;
            tournament.m_IsTournamentDayOver = (message.Flags & 4) != 0;
            tournament.m_TournamentMaxPlayerCount = message.MaxPlayerCount;
            tournament.m_TournamentSignedUpCustomerCount = message.SignedUpCustomerCount;
            tournament.m_TournamentFinishedCurrentRoundCustomerCount = message.FinishedCurrentRoundCustomerCount;
            tournament.m_TournamentCurrentRound = message.CurrentRound;
            tournament.m_TournamentMaxRound = message.MaxRound;
            tournament.m_TournamentFee = message.Fee;
            tournament.m_TournamentTotalValue = message.TotalValue;
            CPlayerData.m_IsPlayerRegisteredForTournament = message.IsPlayerRegistered;
            var player = CPlayerData.m_PlayerTournamentData
                ?? (CPlayerData.m_PlayerTournamentData = new CustomerTournamentData());
            if (message.PlayerState != null)
            {
                TournamentInterop.ApplyPlayerState(message.PlayerState, player);
            }
            else
            {
                // Accept a pre-complete header while a peer is being upgraded. New hosts always
                // send PlayerState, but the legacy fields remain a safe fallback.
                TournamentInterop.ApplyPlayerState(new TournamentPlayerState
                {
                    IsTournamentCustomer = message.PlayerIsTournamentCustomer,
                    IsTournamentWin = message.PlayerIsTournamentWin,
                    HasRegisteredTournamentResult = message.PlayerHasRegisteredResult,
                    TournamentCustomerPlayTableIndex = message.PlayerTournamentCustomerPlayTableIndex,
                    TournamentWinCount = message.PlayerTournamentWinCount,
                    TournamentWinPoints = message.PlayerTournamentWinPoints,
                    TournamentPlacementIndex = message.PlayerTournamentPlacementIndex,
                }, player);
            }
        }

        private static void EnsurePrizeLists(TournamentData tournament, int count)
        {
            if (tournament.m_PrizeDataList == null)
            {
                tournament.m_PrizeDataList = new List<TournamentPrizeDataList>();
            }

            while (tournament.m_PrizeDataList.Count < count)
            {
                tournament.m_PrizeDataList.Add(new TournamentPrizeDataList
                {
                    m_PrizeDataList = new List<TournamentPrizeData>(),
                });
            }

            while (tournament.m_PrizeDataList.Count > count)
                tournament.m_PrizeDataList.RemoveAt(tournament.m_PrizeDataList.Count - 1);
        }

        private static void ReplacePrizeSlot(TournamentPrizeDataList target, TournamentPrizeSlot source)
        {
            target.m_PrizeDataList ??= new List<TournamentPrizeData>();
            target.m_PrizeDataList.Clear();
            if (source == null)
                return;

            for (var i = 0; i < source.Prizes.Count; i++)
            {
                var prize = source.Prizes[i];
                target.m_PrizeDataList.Add(new TournamentPrizeData
                {
                    m_CardData = prize.HasCard ? prize.Card : null,
                    m_ItemType = prize.ItemType,
                    m_Count = prize.Count,
                });
            }
        }

        private void RefreshBoards(TournamentData tournament, List<PairingEntry> digest)
        {
            var manager = CustomerManagerInScene();
            var showBoard = tournament.m_IsTournamentDay || tournament.m_IsTournamentDayOver;
            manager.m_TournamentPairingScreen.gameObject.SetActive(showBoard);

            var shelves = ShelfManager.GetTournamentPrizeShelfList();
            for (var i = 0; i < shelves.Count; i++)
            {
                (ScreenMesh.GetValue(shelves[i]) as GameObject).SetActive(showBoard);
            }

            var screen = manager.m_TournamentPairingScreen;
            if (!showBoard)
            {
                screen.ShowPairingScreen(false, 0);
                return;
            }

            screen.ShowPairingScreen(true, tournament.m_TournamentMaxPlayerCount);
            screen.UpdateCurrentRound(tournament.m_TournamentCurrentRound,
                tournament.m_TournamentMaxRound);
            for (var i = 0; i < digest.Count; i++)
            {
                var entry = digest[i];
                screen.OnCustomerRegisterStart(entry.SortedIndex, entry.ModelIndex, entry.IsFemale);
                var data = new CustomerTournamentData
                {
                    m_TournamentCustomerSortedIndex = entry.SortedIndex,
                    m_IsTournamentWin = entry.IsWin,
                    m_HasRegisteredTournamentResult = entry.HasResult,
                    m_TournamentWinCount = entry.WinCount,
                    m_TournamentWinPoints = entry.WinPoints,
                    m_TournamentOMW = entry.OMW,
                    m_TournamentOOMW = entry.OOMW,
                };
                screen.m_TournamentPairingUIGrpList[entry.SortedIndex / 2]
                    .UpdateCustomerData(data);
            }
        }

        private static PairingEntry ToPairingEntry(TournamentBracketEntry entry)
        {
            return new PairingEntry
            {
                SortedIndex = entry.SortedIndex,
                ModelIndex = entry.ModelIndex,
                IsFemale = (entry.Flags & 1) != 0,
                IsWin = (entry.Flags & 2) != 0,
                HasResult = (entry.Flags & 4) != 0,
                WinCount = entry.WinCount,
                WinPoints = entry.WinPoints,
                OMW = entry.OMW,
                OOMW = entry.OOMW,
            };
        }

        private static void ApplyPatches(Harmony harmony)
        {
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressConfirm");
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressCancel");
            TryPatch(harmony, typeof(HostTournamentScreen), "ConfirmCancelTournament");
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressPrizeSetup");
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament");
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressPlayerSignOutTournament");
            TryPatch(harmony, typeof(CustomerManager), "Start", nameof(ReadinessPostfix));
            TryPatch(harmony, typeof(CustomerManager), "Init", nameof(ReadinessPostfix));
            TryPatch(harmony, typeof(TournamentPairingScreen), "Awake", nameof(ReadinessPostfix));
        }

        private static void TryPatch(Harmony harmony, Type type, string method,
            string postfix = nameof(ScheduleBlockPrefix))
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("Tournament client patch target missing: " + type.Name
                        + "." + method);
                    return;
                }

                harmony.Patch(original,
                    prefix: postfix == nameof(ScheduleBlockPrefix)
                        ? new HarmonyMethod(typeof(TournamentClientBehaviour), postfix) : null,
                    postfix: postfix == nameof(ScheduleBlockPrefix)
                        ? null : new HarmonyMethod(typeof(TournamentClientBehaviour), postfix));
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Tournament client patch failed " + type.Name + "."
                    + method + ": " + exception.Message);
            }
        }

        private static bool ScheduleBlockPrefix()
        {
            if (_active == null || _active._shutdown)
            {
                return true;
            }

            _active._context.SetStatusLine?.Invoke("the host schedules tournaments", 3f);
            return false;
        }

        private static void ReadinessPostfix()
        {
            _active?.TryApplyPending();
        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
        {
            TryApplyPending();
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TryApplyPending();
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

    }
}
