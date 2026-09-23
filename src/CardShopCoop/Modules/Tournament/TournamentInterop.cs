using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using HarmonyLib;

namespace CardShopCoop.Modules.Tournament
{
    /// <summary>
    /// Reflection surface for CustomerTournamentData. The table save payload is game-owned and
    /// must not be reached through a compile-time member layout when one game build changes it.
    /// </summary>
    internal static class TournamentInterop
    {
        private static readonly FieldInfo IsTournamentCustomer = Field("m_IsTournamentCustomer");
        private static readonly FieldInfo IsTournamentWin = Field("m_IsTournamentWin");
        private static readonly FieldInfo HasFinishCurrentTournamentRound =
            Field("m_HasFinishCurrentTournamentRound");
        private static readonly FieldInfo HasRegisteredTournamentStart =
            Field("m_HasRegisteredTournamentStart");
        private static readonly FieldInfo HasRegisteredTournamentResult =
            Field("m_HasRegisteredTournamentResult");
        private static readonly FieldInfo TournamentCustomerIndex = Field("m_TournamentCustomerIndex");
        private static readonly FieldInfo TournamentCustomerSortedIndex =
            Field("m_TournamentCustomerSortedIndex");
        private static readonly FieldInfo TournamentCustomerPlayTableIndex =
            Field("m_TournamentCustomerPlayTableIndex");
        private static readonly FieldInfo TournamentWinCount = Field("m_TournamentWinCount");
        private static readonly FieldInfo TournamentWinPoints = Field("m_TournamentWinPoints");
        private static readonly FieldInfo TournamentOMW = Field("m_TournamentOMW");
        private static readonly FieldInfo TournamentOOMW = Field("m_TournamentOOMW");
        private static readonly FieldInfo CurrentPrizeDataIndex = Field("m_CurrentPrizeDataIndex");
        private static readonly FieldInfo TournamentPlacementIndex = Field("m_TournamentPlacementIndex");
        private static readonly FieldInfo CharacterModelIndex = Field("m_CharacterModelIndex");
        private static readonly FieldInfo IsFemale = Field("m_IsFemale");
        private static readonly FieldInfo TournamentOpponentIndexList =
            Field("m_TournamentOpponentIndexList");
        private static readonly FieldInfo PrizeDataList = Field("m_PrizeDataList");
        private static readonly FieldInfo TargetPrizeData = Field("m_TargetPrizeData");
        private static readonly FieldInfo CurrentPrizeIndex = AccessTools.Field(
            typeof(HostTournamentSelectPrizeScreen), "m_CurrentPrizeIndex");

        private static FieldInfo Field(string name)
            => ReflectionSurface.RequiredField(typeof(CustomerTournamentData), name);

        internal static TournamentPlayerState BuildPlayerState(CustomerTournamentData source)
        {
            var state = new TournamentPlayerState();
            if (source == null)
            {
                return state;
            }

            state.IsTournamentCustomer = (bool)IsTournamentCustomer.GetValue(source);
            state.IsTournamentWin = (bool)IsTournamentWin.GetValue(source);
            state.HasFinishCurrentTournamentRound = (bool)HasFinishCurrentTournamentRound.GetValue(source);
            state.HasRegisteredTournamentStart = (bool)HasRegisteredTournamentStart.GetValue(source);
            state.HasRegisteredTournamentResult = (bool)HasRegisteredTournamentResult.GetValue(source);
            state.TournamentCustomerIndex = (int)TournamentCustomerIndex.GetValue(source);
            state.TournamentCustomerSortedIndex = (int)TournamentCustomerSortedIndex.GetValue(source);
            state.TournamentCustomerPlayTableIndex = (int)TournamentCustomerPlayTableIndex.GetValue(source);
            state.TournamentWinCount = (int)TournamentWinCount.GetValue(source);
            state.TournamentWinPoints = (int)TournamentWinPoints.GetValue(source);
            state.TournamentOMW = (int)TournamentOMW.GetValue(source);
            state.TournamentOOMW = (int)TournamentOOMW.GetValue(source);
            state.CurrentPrizeDataIndex = (int)CurrentPrizeDataIndex.GetValue(source);
            state.TournamentPlacementIndex = (int)TournamentPlacementIndex.GetValue(source);
            state.CharacterModelIndex = (int)CharacterModelIndex.GetValue(source);
            state.IsFemale = (bool)IsFemale.GetValue(source);

            var opponents = TournamentOpponentIndexList.GetValue(source) as List<int>;
            if (opponents != null)
            {
                state.TournamentOpponentIndexList.AddRange(opponents);
            }

            var prizes = PrizeDataList.GetValue(source) as List<TournamentPrizeData>;
            if (prizes != null)
            {
                for (var i = 0; i < prizes.Count; i++)
                {
                    state.PrizeDataList.Add(BuildPrize(prizes[i]));
                }
            }

            var target = TargetPrizeData.GetValue(source) as TournamentPrizeData;
            state.HasTargetPrizeData = target != null;
            if (target != null)
            {
                state.TargetPrizeData = BuildPrize(target);
            }

            return state;
        }

        internal static int PrizeIndex(HostTournamentSelectPrizeScreen screen)
            => screen == null || CurrentPrizeIndex == null
                ? -1 : (int)CurrentPrizeIndex.GetValue(screen);

        internal static void ApplyPlayerState(TournamentPlayerState state,
            CustomerTournamentData target)
        {
            if (state == null || target == null)
            {
                return;
            }

            IsTournamentCustomer.SetValue(target, state.IsTournamentCustomer);
            IsTournamentWin.SetValue(target, state.IsTournamentWin);
            HasFinishCurrentTournamentRound.SetValue(target, state.HasFinishCurrentTournamentRound);
            HasRegisteredTournamentStart.SetValue(target, state.HasRegisteredTournamentStart);
            HasRegisteredTournamentResult.SetValue(target, state.HasRegisteredTournamentResult);
            TournamentCustomerIndex.SetValue(target, state.TournamentCustomerIndex);
            TournamentCustomerSortedIndex.SetValue(target, state.TournamentCustomerSortedIndex);
            TournamentCustomerPlayTableIndex.SetValue(target, state.TournamentCustomerPlayTableIndex);
            TournamentWinCount.SetValue(target, state.TournamentWinCount);
            TournamentWinPoints.SetValue(target, state.TournamentWinPoints);
            TournamentOMW.SetValue(target, state.TournamentOMW);
            TournamentOOMW.SetValue(target, state.TournamentOOMW);
            CurrentPrizeDataIndex.SetValue(target, state.CurrentPrizeDataIndex);
            TournamentPlacementIndex.SetValue(target, state.TournamentPlacementIndex);
            CharacterModelIndex.SetValue(target, state.CharacterModelIndex);
            IsFemale.SetValue(target, state.IsFemale);

            var opponents = new List<int>();
            if (state.TournamentOpponentIndexList != null)
            {
                opponents.AddRange(state.TournamentOpponentIndexList);
            }
            TournamentOpponentIndexList.SetValue(target, opponents);

            var prizes = new List<TournamentPrizeData>();
            if (state.PrizeDataList != null)
            {
                for (var i = 0; i < state.PrizeDataList.Count; i++)
                {
                    prizes.Add(ToPrize(state.PrizeDataList[i]));
                }
            }
            PrizeDataList.SetValue(target, prizes);
            TargetPrizeData.SetValue(target,
                state.HasTargetPrizeData ? ToPrize(state.TargetPrizeData) : null);
        }

        private static TournamentPrizeEntry BuildPrize(TournamentPrizeData source)
        {
            var hasCard = source != null && source.m_CardData != null;
            return new TournamentPrizeEntry
            {
                HasCard = hasCard,
                Card = hasCard ? source.m_CardData : null,
                ItemType = source == null ? default : source.m_ItemType,
                Count = source == null ? 0 : source.m_Count,
            };
        }

        private static TournamentPrizeData ToPrize(TournamentPrizeEntry source)
        {
            return source == null ? null : new TournamentPrizeData
            {
                m_CardData = source.HasCard ? source.Card : null,
                m_ItemType = source.ItemType,
                m_Count = source.Count,
            };
        }
    }
}
