using System;
using System.Collections;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Settings
{
    /// <summary>Host authority for the non-decoration shop settings channel.</summary>
    [ServerBehaviour]
    public sealed class SettingsHostBehaviour : CoopBehaviour
    {
        private const byte OpGameEvent = 3;
        private const byte OpGameEventFee = 4;
        private const byte OpCashier = 5;
        private const byte OpTableNumber = 6;
        private const int MaxWireListCount = 255;

        private static SettingsHostBehaviour _active;
        private static bool _applyingRemote;

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
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.settings.host");
                Patch(typeof(GameEventFormatPatch));
                Patch(typeof(GameEventResetPatch));
                Patch(typeof(GameEventFeePatch));
                Patch(typeof(CashierCheckoutPatch));
                Patch(typeof(CashierTradePatch));
                Patch(typeof(TableNumberPatch));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Settings host initialization failed: " + error);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                _context = null;
                throw;
            }
        }

        private void Patch(Type patchType)
        {
            _harmony.CreateClassProcessor(patchType).Patch();
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State)
                || !_context.InGame() || !SettingsInterop.IsSceneReady)
            {
                return;
            }

            _context.Send(connection.Id, BuildFullState());
        }

        [MessageHandler(typeof(SettingsOpMessage))]
        private void HandleOperation(MessageContext context, SettingsOpMessage message)
        {
            if (!IsFullyJoinedSender(context))
            {
                return;
            }

            if (message == null)
            {
                CoopPlugin.Log.LogWarning("Settings: rejected null authenticated operation from "
                    + context.Connection.Id + ".");
                return;
            }

            if (!TryValidateOperation(message, out var reason))
            {
                CoopPlugin.Log.LogWarning("Settings: rejected malformed authenticated operation from "
                    + context.Connection.Id + ": " + reason);
                if (message.PredictionId != Guid.Empty)
                    PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                return;
            }

            switch (message.Op)
            {
                case OpGameEvent:
                    CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)message.Index;
                    CPlayerData.m_PendingGameEventExpansionType = message.Expansion;
                    BroadcastMutation(4, -1, message.PredictionId);
                    break;
                case OpGameEventFee:
                    _applyingRemote = true;
                    try
                    {
                        PriceChangeManager.SetGameEventPrice((EGameEventFormat)message.Index,
                            message.Fee);
                    }
                    finally
                    {
                        _applyingRemote = false;
                    }

                    BroadcastMutation(5, message.Index, message.PredictionId);
                    break;
                case OpCashier:
                    ApplyCashier(message.CashierIndex, message.CashierFlags);
                    BroadcastMutation(6, message.CashierIndex, message.PredictionId);
                    break;
                case OpTableNumber:
                    ApplyTableNumber(message.TableIndex, message.TableNumber);
                    BroadcastMutation(7, message.TableIndex, message.PredictionId);
                    break;
                default:
                    CoopPlugin.Log.LogWarning("Settings: unknown sub-op " + message.Op);
                    break;
            }

        }

        private static bool IsValidEventIndex(int index, bool allowNone)
        {
            return index >= (allowNone ? (int)EGameEventFormat.None : 0)
                && index < (int)EGameEventFormat.MAX;
        }

        private static bool IsValidExpansion(ECardExpansionType expansion)
        {
            var value = (int)expansion;
            // The loaded enum is the membership truth (EPL mints its expansions into it at
            // prepatch), so this accepts vanilla and modded expansions alike. Exclude the MAX
            // sentinel, which is a real member but not a usable expansion.
            return value >= (int)ECardExpansionType.None
                && value != (int)ECardExpansionType.MAX
                && CatalogApi.IsDefinedEnumValue(EnumKind.CardExpansion, value);
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool TryValidateOperation(SettingsOpMessage message, out string reason)
        {
            reason = null;
            switch (message.Op)
            {
                case OpGameEvent:
                    if (!IsValidEventIndex(message.Index, allowNone: true))
                    {
                        reason = "game event format is outside the enum range";
                    }
                    else if (message.Index >= 0 && (CPlayerData.m_SetGameEventPriceList == null
                        || message.Index >= CPlayerData.m_SetGameEventPriceList.Count
                        || message.Index >= MaxWireListCount))
                    {
                        reason = "game event format list index is out of bounds";
                    }
                    else if (!IsValidExpansion(message.Expansion))
                    {
                        reason = "game event expansion is outside the enum range";
                    }

                    return reason == null;

                case OpGameEventFee:
                    if (!IsValidEventIndex(message.Index, allowNone: false))
                    {
                        reason = "game event fee format is outside the enum range";
                    }
                    else if (!IsFinite(message.Fee) || message.Fee < 0f)
                    {
                        reason = "game event fee is not a finite non-negative value";
                    }
                    else if (CPlayerData.m_SetGameEventPriceList == null
                        || message.Index >= CPlayerData.m_SetGameEventPriceList.Count
                        || message.Index >= MaxWireListCount)
                    {
                        reason = "game event fee list index is out of bounds";
                    }

                    return reason == null;

                case OpCashier:
                    var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
                    if ((message.CashierFlags & ~3) != 0)
                    {
                        reason = "cashier flags contain unknown bits";
                    }
                    else if (counters == null || message.CashierIndex >= counters.Count
                        || message.CashierIndex >= MaxWireListCount || counters[message.CashierIndex] == null)
                    {
                        reason = "cashier list index is out of bounds";
                    }

                    return reason == null;

                case OpTableNumber:
                    var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
                    if (message.TableNumber < 0 || message.TableNumber > byte.MaxValue)
                    {
                        reason = "table number is outside the wire range";
                    }
                    else if (tables == null || message.TableIndex >= tables.Count
                        || message.TableIndex >= MaxWireListCount || tables[message.TableIndex] == null)
                    {
                        reason = "table list index is out of bounds";
                    }

                    return reason == null;

                default:
                    reason = "unknown settings operation";
                    return false;
            }
        }

        private void ApplyCashier(byte index, byte flags)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            if (counters == null || index >= counters.Count || counters[index] == null)
            {
                return;
            }

            var checkout = (flags & 1) != 0;
            var trade = (flags & 2) != 0;
            _applyingRemote = true;
            try
            {
                if (counters[index].CanCheckout() != checkout)
                {
                    counters[index].SetCanCheckout(checkout);
                }

                if (counters[index].CanTradeCard() != trade)
                {
                    counters[index].SetCanTradeCard(trade);
                }
            }
            finally
            {
                _applyingRemote = false;
            }

        }

        private void ApplyTableNumber(byte index, int number)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            if (tables == null || index >= tables.Count || tables[index] == null)
            {
                return;
            }

            _applyingRemote = true;
            try
            {
                if (tables[index].GetTournamentPlayTableNumber() != number)
                {
                    tables[index].SetTournamentPlayTableNumber(Mathf.Max(0, number));
                }
            }
            finally
            {
                _applyingRemote = false;
            }

        }

        private bool IsFullyJoinedSender(MessageContext context)
        {
            return !_shutdown && _context.InGame()
                && context?.Connection != null
                && context.Connection.State == ConnectionState.FullyJoined;
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        private void BroadcastMutation(byte kind, int index, Guid predictionId = default)
        {
            if (_shutdown || !_context.InGame())
            {
                return;
            }

            _context.Broadcast(BuildMutation(kind, index, predictionId));
        }

        private static SettingsStateMessage BuildFullState()
        {
            var message = new SettingsStateMessage
            {
                GameEventFormat = (int)CPlayerData.m_GameEventFormat,
                PendingGameEventFormat = (int)CPlayerData.m_PendingGameEventFormat,
                GameEventExpansion = CPlayerData.m_GameEventExpansionType,
                PendingGameEventExpansion = CPlayerData.m_PendingGameEventExpansionType,
                GameEventPriceCount = Math.Min(CPlayerData.m_SetGameEventPriceList?.Count ?? 0,
                    MaxWireListCount),
                CashierCount = Math.Min(SettingsInterop.FindShelfManager()?.m_CashierCounterList?.Count ?? 0,
                    MaxWireListCount),
                TableCount = Math.Min(SettingsInterop.FindShelfManager()?.m_PlayTableList?.Count ?? 0,
                    MaxWireListCount),
            };

            var fees = CPlayerData.m_SetGameEventPriceList;
            for (var i = 0; fees != null && i < Mathf.Min(fees.Count, 255); i++)
            {
                message.GameEventPrices.Add(fees[i]);
            }

            CopyCashiers(message.CashierFlags);
            CopyTables(message.TableNumbers);
            return message;
        }

        private static SettingsStateMessage BuildMutation(int index, int itemIndex,
            Guid predictionId = default)
        {
            var message = new SettingsStateMessage
            {
                Full = false,
                PredictionId = predictionId,
                Index = index,
                ItemIndex = itemIndex,
            };
            if (index == 4)
            {
                message.GameEventFormat = (int)CPlayerData.m_GameEventFormat;
                message.PendingGameEventFormat = (int)CPlayerData.m_PendingGameEventFormat;
                message.GameEventExpansion = CPlayerData.m_GameEventExpansionType;
                message.PendingGameEventExpansion = CPlayerData.m_PendingGameEventExpansionType;
            }
            else if (index == 5)
            {
                var fees = CPlayerData.m_SetGameEventPriceList;
                message.GameEventPriceCount = Math.Min(fees?.Count ?? 0, MaxWireListCount);
                if (fees != null && itemIndex >= 0 && itemIndex < fees.Count)
                {
                    message.GameEventPrices.Add(fees[itemIndex]);
                }
            }
            else if (index == 6)
            {
                var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
                message.CashierCount = Math.Min(counters?.Count ?? 0, MaxWireListCount);
                if (counters != null && itemIndex >= 0 && itemIndex < counters.Count)
                {
                    var counter = counters[itemIndex];
                    message.CashierFlags.Add(counter == null ? (byte)3 : (byte)(
                        (counter.CanCheckout() ? 1 : 0) | (counter.CanTradeCard() ? 2 : 0)));
                }
            }
            else if (index == 7)
            {
                var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
                message.TableCount = Math.Min(tables?.Count ?? 0, MaxWireListCount);
                if (tables != null && itemIndex >= 0 && itemIndex < tables.Count)
                {
                    var table = tables[itemIndex];
                    message.TableNumbers.Add((byte)Mathf.Clamp(
                        table == null ? 0 : table.GetTournamentPlayTableNumber(), 0, 255));
                }
            }

            if (itemIndex >= 0 && ((index == 5 && itemIndex >= message.GameEventPriceCount)
                || (index == 6 && itemIndex >= message.CashierCount)
                || (index == 7 && itemIndex >= message.TableCount)))
            {
                message.Tombstone = true;
            }

            return message;
        }

        private static void CopyCashiers(List<byte> destination)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            for (var i = 0; counters != null && i < Mathf.Min(counters.Count, 255); i++)
            {
                var flags = (byte)3;
                if (counters[i] != null)
                {
                    flags = (byte)((counters[i].CanCheckout() ? 1 : 0)
                        | (counters[i].CanTradeCard() ? 2 : 0));
                }

                destination.Add(flags);
            }
        }

        private static void CopyTables(List<byte> destination)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            for (var i = 0; tables != null && i < Mathf.Min(tables.Count, 255); i++)
            {
                var number = tables[i] == null ? 0 : tables[i].GetTournamentPlayTableNumber();
                destination.Add((byte)Mathf.Clamp(number, 0, 255));
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
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _applyingRemote = false;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private static void BroadcastGameEvent()
        {
            if (_applyingRemote || _active == null || _active._shutdown)
            {
                return;
            }

            _active.BroadcastMutation(4, -1, Guid.Empty);
        }

        private static void BroadcastGameEventFee(EGameEventFormat format)
        {
            if (_applyingRemote || _active == null || _active._shutdown)
            {
                return;
            }

            _active.BroadcastMutation(5, (int)format, Guid.Empty);
        }

        private static void BroadcastCashier(InteractableCashierCounter counter)
        {
            if (_applyingRemote || _active == null || _active._shutdown)
            {
                return;
            }

            _active.PublishCashier(counter, "cashier setting mutation");
        }

        private static void BroadcastTable(InteractablePlayTable table)
        {
            if (_applyingRemote || _active == null || _active._shutdown)
            {
                return;
            }

            _active.PublishTable(table, "table number mutation");
        }

        private void PublishCashier(InteractableCashierCounter counter, string source)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            var index = counters?.IndexOf(counter) ?? -1;
            if (counter == null || index < 0 || index > 254)
            {
                CoopPlugin.Log.LogWarning("Settings host: " + source
                    + " could not resolve a stable cashier identity");
                return;
            }

            BroadcastMutation(6, index);
        }

        private void PublishTable(InteractablePlayTable table, string source)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            var index = tables?.IndexOf(table) ?? -1;
            if (table == null || index < 0 || index > 254)
            {
                CoopPlugin.Log.LogWarning("Settings host: " + source
                    + " could not resolve a stable table identity");
                return;
            }

            BroadcastMutation(7, index);
        }

        [HarmonyPatch(typeof(SetGameEventFormatScreen), "OnPressConfirmBtn")]
        private static class GameEventFormatPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => BroadcastGameEvent();
        }

        [HarmonyPatch(typeof(SetGameEventScreen), "OnPressReset")]
        private static class GameEventResetPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => BroadcastGameEvent();
        }

        [HarmonyPatch(typeof(PriceChangeManager), "SetGameEventPrice")]
        private static class GameEventFeePatch
        {
            [HarmonyPostfix]
            private static void Postfix(EGameEventFormat gameEventFormat)
                => BroadcastGameEventFee(gameEventFormat);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCanCheckout")]
        private static class CashierCheckoutPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => BroadcastCashier(__instance);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCanTradeCard")]
        private static class CashierTradePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => BroadcastCashier(__instance);
        }

        [HarmonyPatch(typeof(InteractablePlayTable), "SetTournamentPlayTableNumber")]
        private static class TableNumberPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePlayTable __instance)
                => BroadcastTable(__instance);
        }

        /// <summary>DelayGoNextDay applies the selected format and expansion after its waits.
        /// Relay the authoritative values at that mutation boundary on both game baselines.</summary>
        [HarmonyPatch(typeof(EndOfDayReportScreen), "DelayGoNextDay")]
        private static class DelayGoNextDayPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ref IEnumerator __result)
            {
                if (__result != null)
                {
                    __result = Relay(__result);
                }
            }

            private static IEnumerator Relay(IEnumerator inner)
            {
                while (true)
                {
                    var format = CPlayerData.m_GameEventFormat;
                    var expansion = CPlayerData.m_GameEventExpansionType;
                    if (!inner.MoveNext())
                    {
                        yield break;
                    }

                    if (format != CPlayerData.m_GameEventFormat
                        || expansion != CPlayerData.m_GameEventExpansionType)
                    {
                        if (_active != null && !_active._shutdown)
                        {
                            _active.BroadcastMutation(4, -1);
                        }
                    }

                    yield return inner.Current;
                }
            }
        }
    }
}
