using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Tournament
{
    /// <summary>Host source of tournament state and event-driven bracket deltas.</summary>
    [ServerBehaviour]
    public sealed class TournamentHostBehaviour : CoopBehaviour
    {
        private const int PrizeSlotCount = 8;
        private const int BracketBatchSize = 8;
        private const int MaxBracketEntries = 64;

        private static TournamentHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            try
            {
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tournament.host.runtime");
                ApplyPatches(_harmony);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tournament host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State)
                || !_context.InGame() || !TryBuildState(out var baseline))
            {
                return;
            }

            baseline.Full = true;
            _context.Send(connection.Id, baseline);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private static CustomerManager CustomerManagerInScene()
        {
            return SceneRef<CustomerManager>.Get();
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        private void NotifyHeader()
        {
            if (_shutdown || !_context.InGame() || !TryBuildState(out var state))
            {
                return;
            }

            Publish(new TournamentDeltaMessage
            {
                Kind = 0,
                Header = BuildHeaderState(CPlayerData.m_TournamentData),
            });
        }

        private void NotifyPrizes()
        {
            if (_shutdown || !_context.InGame() || !TryBuildState(out var state))
            {
                return;
            }

            for (var i = 0; i < state.PrizeSlots.Count; i++)
            {
                Publish(new TournamentDeltaMessage
                {
                    Kind = 1,
                    PrizeIndex = i,
                    Prize = state.PrizeSlots[i],
                });
            }
        }

        private void NotifyPrize(int index)
        {
            if (_shutdown || !_context.InGame() || !TryBuildState(out var state)
                || index < 0 || index >= state.PrizeSlots.Count)
                return;
            Publish(new TournamentDeltaMessage
            {
                Kind = 1,
                PrizeIndex = index,
                Prize = state.PrizeSlots[index],
            });
        }

        private void NotifyBracket()
        {
            if (_shutdown || !_context.InGame() || !TryBuildState(out var state))
            {
                return;
            }

            for (var start = 0; start < Math.Max(1, state.Bracket.Count); start += BracketBatchSize)
            {
                var delta = new TournamentDeltaMessage
                {
                    Kind = 2,
                    BracketStart = start,
                    BracketCount = state.Bracket.Count,
                };
                for (var i = start; i < start + BracketBatchSize && i < state.Bracket.Count; i++)
                    delta.Bracket.Add(state.Bracket[i]);
                Publish(delta);
            }
        }

        private bool TryBuildState(out TournamentStateMessage state)
        {
            state = null;
            if (CPlayerData.m_TournamentData == null)
            {
                return false;
            }

            state = BuildState(CPlayerData.m_TournamentData);
            return true;
        }

        private void Publish(TournamentDeltaMessage message)
        {
            if (_shutdown || message == null || !_context.InGame())
            {
                return;
            }

            _context.Broadcast(message);
        }

        private TournamentStateMessage BuildState(TournamentData tournament)
        {
            var state = BuildHeaderState(tournament);
            AddPrizeSlots(state, tournament);
            AddBracketEntries(state);
            return state;
        }

        private TournamentStateMessage BuildHeaderState(TournamentData tournament)
        {
            var state = new TournamentStateMessage
            {
                Flags = (byte)((tournament.m_IsHostingTournament ? 1 : 0)
                    | (tournament.m_IsTournamentDay ? 2 : 0)
                    | (tournament.m_IsTournamentDayOver ? 4 : 0)),
                MaxPlayerCount = tournament.m_TournamentMaxPlayerCount,
                SignedUpCustomerCount = tournament.m_TournamentSignedUpCustomerCount,
                FinishedCurrentRoundCustomerCount = tournament.m_TournamentFinishedCurrentRoundCustomerCount,
                CurrentRound = tournament.m_TournamentCurrentRound,
                MaxRound = tournament.m_TournamentMaxRound,
                Fee = tournament.m_TournamentFee,
                TotalValue = tournament.m_TournamentTotalValue,
                IsPlayerRegistered = CPlayerData.m_IsPlayerRegisteredForTournament,
            };

            var player = CPlayerData.m_PlayerTournamentData;
            state.PlayerState = TournamentInterop.BuildPlayerState(player);
            if (player != null)
            {
                state.PlayerIsTournamentCustomer = state.PlayerState.IsTournamentCustomer;
                state.PlayerIsTournamentWin = state.PlayerState.IsTournamentWin;
                state.PlayerHasRegisteredResult = state.PlayerState.HasRegisteredTournamentResult;
                state.PlayerTournamentCustomerPlayTableIndex =
                    state.PlayerState.TournamentCustomerPlayTableIndex;
                state.PlayerTournamentWinCount = state.PlayerState.TournamentWinCount;
                state.PlayerTournamentWinPoints = state.PlayerState.TournamentWinPoints;
                state.PlayerTournamentPlacementIndex = state.PlayerState.TournamentPlacementIndex;
            }

            return state;
        }

        private static void AddPrizeSlots(TournamentStateMessage state, TournamentData tournament)
        {
            var prizes = tournament?.m_PrizeDataList;
            var prizeCount = prizes == null ? 0 : Math.Min(PrizeSlotCount, prizes.Count);
            for (var i = 0; i < prizeCount; i++)
            {
                var slot = new TournamentPrizeSlot();
                var entries = prizes[i]?.m_PrizeDataList;
                var entryCount = entries == null ? 0 : Math.Min(entries.Count, 64);
                for (var j = 0; j < entryCount; j++)
                {
                    var prize = entries[j];
                    var hasCard = prize != null && prize.m_CardData != null;
                    slot.Prizes.Add(new TournamentPrizeEntry
                    {
                        HasCard = hasCard,
                        Card = hasCard ? prize.m_CardData : null,
                        ItemType = prize == null ? (EItemType)0 : prize.m_ItemType,
                        Count = prize == null ? 0 : prize.m_Count,
                    });
                }

                state.PrizeSlots.Add(slot);
            }
        }

        private static void AddBracketEntries(TournamentStateMessage state)
        {
            var manager = CustomerManagerInScene();
            var sorted = manager?.m_TournamentSortedCustomerList;
            var bracketCount = sorted == null ? 0 : Math.Min(MaxBracketEntries, sorted.Count);
            var player = CPlayerData.m_PlayerTournamentData;
            for (var i = 0; i < bracketCount; i++)
            {
                var customer = sorted[i];
                var data = customer?.GetCustomerTournamentData();
                if (data == null)
                {
                    if (player != null && player.m_IsTournamentCustomer
                        && player.m_TournamentCustomerSortedIndex == i)
                    {
                        state.Bracket.Add(new TournamentBracketEntry
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

                state.Bracket.Add(new TournamentBracketEntry
                {
                    SortedIndex = (byte)Mathf.Clamp(data.m_TournamentCustomerSortedIndex, 0, 255),
                    ModelIndex = customer.GetCustomerModelIndex(),
                    Flags = (byte)((customer.m_IsFemale ? 1 : 0)
                        | (data.m_IsTournamentWin ? 2 : 0)
                        | (data.m_HasRegisteredTournamentResult ? 4 : 0)),
                    WinCount = data.m_TournamentWinCount,
                    WinPoints = data.m_TournamentWinPoints,
                    OMW = data.m_TournamentOMW,
                    OOMW = data.m_TournamentOOMW,
                });
            }
        }

        private static void ApplyPatches(Harmony harmony)
        {
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressConfirm", null,
                nameof(TournamentChangedPostfix));
            TryPatch(harmony, typeof(HostTournamentScreen), "ConfirmCancelTournament", null,
                nameof(TournamentChangedPostfix));
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament", null,
                nameof(TournamentChangedPostfix));
            TryPatch(harmony, typeof(HostTournamentScreen), "OnPressPlayerSignOutTournament", null,
                nameof(TournamentChangedPostfix));
            TryPatch(harmony, typeof(HostTournamentSelectPrizeScreen), "OnPressSelectPrizeItem", null,
                nameof(PrizeChangedPostfix));
            TryPatch(harmony, typeof(HostTournamentSelectPrizeScreen), "OnPressSelectPrizeCard", null,
                nameof(PrizeChangedPostfix));
            TryPatch(harmony, typeof(CustomerManager), "OnDayStarted", nameof(DayStartedPrefix),
                nameof(DayStartedPostfix));
            TryPatch(harmony, typeof(CustomerManager), "OnCustomerFinishTournamentRound", null,
                nameof(BracketChangedPostfix));
        }

        private static void TryPatch(Harmony harmony, Type type, string method, string prefix,
            string postfix)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("Tournament patch target missing: " + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(TournamentHostBehaviour), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(TournamentHostBehaviour), postfix));
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Tournament patch failed " + type.Name + "." + method
                    + ": " + exception.Message);
            }
        }

        private static void TournamentChangedPostfix()
        {
            if (_active == null)
            {
                return;
            }

            _active.NotifyHeader();
            _active.NotifyBracket();
        }

        private static void PrizeChangedPostfix(HostTournamentSelectPrizeScreen __instance)
        {
            if (_active == null)
                return;
            var index = TournamentInterop.PrizeIndex(__instance);
            if (index >= 0)
                _active.NotifyPrize(index);
            else
                _active.NotifyPrizes();
        }

        private static void BracketChangedPostfix()
        {
            // Completing the last customer in a round changes the tournament counters and
            // current-round header as well as the sorted bracket.
            _active?.NotifyHeader();
            _active?.NotifyBracket();
        }

        private static void DayStartedPrefix()
        {
            if (_active != null)
            {
            }
        }

        private static void DayStartedPostfix()
        {
            _active?.NotifyHeader();
            _active?.NotifyBracket();
        }
    }
}
