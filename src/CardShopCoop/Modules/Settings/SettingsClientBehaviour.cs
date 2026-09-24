using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Settings
{
    /// <summary>Guest presentation and intent forwarding for shop settings.</summary>
    [ClientBehaviour]
    public sealed class SettingsClientBehaviour : CoopBehaviour
    {
        private const byte OpGameEvent = 3;
        private const byte OpGameEventFee = 4;
        private const byte OpCashier = 5;
        private const byte OpTableNumber = 6;
        private static SettingsClientBehaviour _active;
        private static bool _applyingRemote;

        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private SettingsStateMessage _pendingFullState;
        private readonly Dictionary<string, SettingsStateMessage> _pendingStates = new();
        private bool _shutdown;
        private bool _joined;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.settings.client");
                Patch(typeof(GameEventFormatPatch));
                Patch(typeof(GameEventResetPatch));
                Patch(typeof(GameEventFeePatch));
                Patch(typeof(CashierCheckoutPatch));
                Patch(typeof(CashierTradePatch));
                Patch(typeof(TableNumberPatch));
                Patch(typeof(CashierInitPatch));
                Patch(typeof(TableInitPatch));
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Settings client initialization failed: " + error);
                _harmony?.UnpatchSelf();
                _harmony = null;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
                SceneManager.sceneLoaded -= OnSceneLoaded;
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

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => TryApplyPendingState();

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPendingState();

        private void TryApplyPendingState()
        {
            if (_shutdown || !_context.InGame()
                || !SettingsInterop.IsSceneReady)
            {
                return;
            }

            if (_pendingFullState != null)
            {
                var full = _pendingFullState;
                _pendingFullState = null;
                PredictionApi.ApplyAuthoritative(full.PredictionId, () => ApplyState(full));
            }

            if (_pendingStates.Count > 0)
            {
                var states = new List<KeyValuePair<string, SettingsStateMessage>>(_pendingStates);
                for (var i = 0; i < states.Count; i++)
                {
                    var state = states[i].Value;
                    if (!CanApply(state))
                    {
                        // The referenced scene object (a just-purchased play table or cashier) has
                        // not been materialized here yet. Keep the latest authoritative value
                        // pending and apply it from that object's init hook; indexing a shorter
                        // list here used to throw straight into session recovery.
                        continue;
                    }

                    _pendingStates.Remove(states[i].Key);
                    PredictionApi.ApplyAuthoritative(state.PredictionId, () => ApplyState(state));
                }
            }
        }

        /// <summary>True when a partial mutation's keyed element exists locally. Tombstones and
        /// list-shaped payloads never need the element (they only trim the local tail), and a
        /// full state is applied with per-element skips.</summary>
        private static bool CanApply(SettingsStateMessage message)
        {
            if (message == null || message.Full || message.ItemIndex < 0 || message.Tombstone)
            {
                return true;
            }

            return message.Index switch
            {
                6 => CashierExists(message.ItemIndex),
                7 => TableExists(message.ItemIndex),
                _ => true,
            };
        }

        private static bool CashierExists(int index)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            return counters != null && index >= 0 && index < counters.Count && counters[index] != null;
        }

        private static bool TableExists(int index)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            return tables != null && index >= 0 && index < tables.Count && tables[index] != null;
        }

        [MessageHandler(typeof(SettingsStateMessage))]
        private void HandleState(MessageContext context, SettingsStateMessage message)
        {
            if (_shutdown)
                return;
            if (message.Full)
            {
                ClearPendingStates();
                _pendingFullState = message;
            }
            else
            {
                var key = StateKey(message);
                if (_pendingStates.TryGetValue(key, out var previous))
                    PredictionApi.ConfirmSuperseded(previous.PredictionId);
                _pendingStates[key] = message;
            }
            TryApplyPendingState();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id != 1)
            {
                return;
            }

            ClearPendingStates();
        }

        private bool ApplyState(SettingsStateMessage message)
        {
            _applyingRemote = true;
            try
            {
                if (!message.Full)
                {
                    return ApplyPartial(message);
                }

                CPlayerData.m_GameEventFormat = (EGameEventFormat)message.GameEventFormat;
                CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)message.PendingGameEventFormat;
                CPlayerData.m_GameEventExpansionType = message.GameEventExpansion;
                CPlayerData.m_PendingGameEventExpansionType = message.PendingGameEventExpansion;

                ApplyFees(message.GameEventPrices, message.GameEventPrices.Count);
                ApplyCashiers(message.CashierFlags, message.CashierFlags.Count);
                ApplyTables(message.TableNumbers, message.TableNumbers.Count);
                return true;
            }
            finally
            {
                _applyingRemote = false;
            }

        }

        /// <summary>Applies one partial mutation. Returns false when the keyed element is not
        /// materialized locally yet so the caller can retry from the object's init hook instead
        /// of applying against a shorter list.</summary>
        private static bool ApplyPartial(SettingsStateMessage message)
        {
            if (message.Index == 4)
            {
                CPlayerData.m_GameEventFormat = (EGameEventFormat)message.GameEventFormat;
                CPlayerData.m_PendingGameEventFormat = (EGameEventFormat)message.PendingGameEventFormat;
                CPlayerData.m_GameEventExpansionType = message.GameEventExpansion;
                CPlayerData.m_PendingGameEventExpansionType = message.PendingGameEventExpansion;
            }
            else if (message.Index == 5)
            {
                var count = message.GameEventPriceCount;
                ResizeFees(count);
                if (message.ItemIndex >= 0)
                {
                    if (!message.Tombstone && message.ItemIndex < CPlayerData.m_SetGameEventPriceList.Count
                        && message.GameEventPrices.Count > 0)
                    {
                        CPlayerData.m_SetGameEventPriceList[message.ItemIndex] = message.GameEventPrices[0];
                    }
                }
                else
                {
                    ApplyFees(message.GameEventPrices, count);
                }
            }
            else if (message.Index == 6)
            {
                var count = message.CashierCount;
                ApplyCashierTail(count);
                if (message.ItemIndex >= 0)
                {
                    if (!message.Tombstone && message.CashierFlags.Count > 0
                        && !ApplyCashier(message.ItemIndex, message.CashierFlags[0]))
                    {
                        return false;
                    }
                }
                else
                {
                    ApplyCashiers(message.CashierFlags, count);
                }
            }
            else if (message.Index == 7)
            {
                var count = message.TableCount;
                ApplyTableTail(count);
                if (message.ItemIndex >= 0)
                {
                    if (!message.Tombstone && message.TableNumbers.Count > 0
                        && !ApplyTable(message.ItemIndex, message.TableNumbers[0]))
                    {
                        return false;
                    }
                }
                else
                {
                    ApplyTables(message.TableNumbers, count);
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(message.Index), message.Index,
                    "Unknown authoritative settings discriminator.");
            }

            return true;
        }

        private static string StateKey(SettingsStateMessage message)
            => message.Index + ":" + message.ItemIndex;

        private void ClearPendingStates()
        {
            foreach (var state in _pendingStates.Values)
                PredictionApi.ConfirmSuperseded(state.PredictionId);
            _pendingStates.Clear();
            if (_pendingFullState != null)
                PredictionApi.ConfirmSuperseded(_pendingFullState.PredictionId);
            _pendingFullState = null;
        }

        private static bool ApplyCashier(int index, byte flags)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            if (counters == null || index < 0 || index >= counters.Count || counters[index] == null)
            {
                return false;
            }

            var counter = counters[index];
            var checkout = (flags & 1) != 0;
            var trade = (flags & 2) != 0;
            counter.SetCanCheckout(checkout);
            counter.SetCanTradeCard(trade);
            return true;
        }

        private static bool ApplyTable(int index, byte number)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            if (tables == null || index < 0 || index >= tables.Count || tables[index] == null)
            {
                return false;
            }

            tables[index].SetTournamentPlayTableNumber(number);
            return true;
        }

        private static void ResizeFees(int count)
        {
            var fees = CPlayerData.m_SetGameEventPriceList;
            while (fees.Count > count)
            {
                fees.RemoveAt(fees.Count - 1);
            }

            while (fees.Count < count)
            {
                fees.Add(0f);
            }
        }

        private static void ApplyFees(List<float> values, int count)
        {
            var fees = CPlayerData.m_SetGameEventPriceList;
            ResizeFees(count);
            for (var i = 0; i < values.Count && i < fees.Count; i++)
            {
                fees[i] = values[i];
            }
        }

        private static void ApplyCashiers(List<byte> flags, int count)
        {
            ApplyCashierTail(count);
            for (var i = 0; i < flags.Count; i++)
                ApplyCashier(i, flags[i]);
        }

        private static void ApplyCashierTail(int count)
        {
            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            if (counters == null)
            {
                return;
            }

            for (var i = count < 0 ? 0 : count; i < counters.Count; i++)
            {
                if (counters[i] != null)
                {
                    counters[i].SetCanCheckout(true);
                    counters[i].SetCanTradeCard(true);
                }
            }
        }

        private static void ApplyTables(List<byte> numbers, int count)
        {
            ApplyTableTail(count);
            for (var i = 0; i < numbers.Count; i++)
                ApplyTable(i, numbers[i]);
        }

        private static void ApplyTableTail(int count)
        {
            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            if (tables == null)
            {
                return;
            }

            for (var i = count < 0 ? 0 : count; i < tables.Count; i++)
            {
                if (tables[i] != null)
                {
                    tables[i].SetTournamentPlayTableNumber(0);
                }
            }
        }

        private static void SendGameEventIntent(EGameEventFormat previousFormat,
            ECardExpansionType previousExpansion)
        {
            if (_applyingRemote || _active == null || _active._shutdown
                || !_active._context.InGame())
            {
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpGameEvent,
                Index = (int)CPlayerData.m_PendingGameEventFormat,
                Expansion = CPlayerData.m_PendingGameEventExpansionType,
            }, () =>
                ApplyUndo(() =>
                {
                    CPlayerData.m_PendingGameEventFormat = previousFormat;
                    CPlayerData.m_PendingGameEventExpansionType = previousExpansion;
                }));
        }

        private static void SendGameEventFeeIntent(EGameEventFormat format, float fee, float previousFee)
        {
            if (_applyingRemote || _active == null || _active._shutdown
                || !_active._context.InGame())
            {
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpGameEventFee,
                Index = (int)format,
                Fee = fee,
            }, () => ApplyUndo(() => PriceChangeManager.SetGameEventPrice(format, previousFee)));
        }

        private static void SendCashierIntent(InteractableCashierCounter counter, byte previousFlags)
        {
            if (_applyingRemote || _active == null || _active._shutdown
                || !_active._context.InGame() || counter == null)
            {
                return;
            }

            var counters = SettingsInterop.FindShelfManager()?.m_CashierCounterList;
            var index = counters?.IndexOf(counter) ?? -1;
            if (index < 0)
            {
                CoopPlugin.Log.LogWarning("Settings client: cashier mutation has no stable identity");
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpCashier,
                CashierIndex = (byte)index,
                CashierFlags = (byte)((counter.CanCheckout() ? 1 : 0)
                    | (counter.CanTradeCard() ? 2 : 0)),
            }, () => ApplyUndo(() =>
            {
                counter.SetCanCheckout((previousFlags & 1) != 0);
                counter.SetCanTradeCard((previousFlags & 2) != 0);
            }));
        }

        private static void SendTableIntent(InteractablePlayTable table, int number, int previousNumber)
        {
            if (_applyingRemote || _active == null || _active._shutdown
                || !_active._context.InGame() || table == null)
            {
                return;
            }

            var tables = SettingsInterop.FindShelfManager()?.m_PlayTableList;
            var index = tables?.IndexOf(table) ?? -1;
            if (index < 0)
            {
                CoopPlugin.Log.LogWarning("Settings client: table mutation has no stable identity");
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpTableNumber,
                TableIndex = (byte)index,
                TableNumber = number,
            }, () => ApplyUndo(() => table.SetTournamentPlayTableNumber(previousNumber)));
        }

        private void SendIntent(SettingsOpMessage message, Action undo)
        {
            if (_shutdown || message == null || !_joined || !_context.InGame() || _applyingRemote)
            {
                return;
            }

            PredictionApi.Predict("settings",
                predictionId =>
                {
                    message.PredictionId = predictionId;
                    _context.Send(1, message);
                },
                () => { }, undo ?? (() => { }));
        }

        private static void ApplyUndo(Action undo)
        {
            _applyingRemote = true;
            try
            {
                undo();
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            TryApplyPendingState();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            ClearPendingStates();
            _joined = false;
            _applyingRemote = false;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(SetGameEventFormatScreen), "OnPressConfirmBtn")]
        private static class GameEventFormatPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out FormatBefore __state)
            {
                __state = new FormatBefore
                {
                    Format = CPlayerData.m_PendingGameEventFormat,
                    Expansion = CPlayerData.m_PendingGameEventExpansionType,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(FormatBefore __state)
                => SendGameEventIntent(__state.Format, __state.Expansion);
        }

        [HarmonyPatch(typeof(SetGameEventScreen), "OnPressReset")]
        private static class GameEventResetPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out FormatBefore __state)
            {
                __state = new FormatBefore
                {
                    Format = CPlayerData.m_PendingGameEventFormat,
                    Expansion = CPlayerData.m_PendingGameEventExpansionType,
                };
            }

            [HarmonyPostfix]
            private static void Postfix(FormatBefore __state)
                => SendGameEventIntent(__state.Format, __state.Expansion);
        }

        [HarmonyPatch(typeof(PriceChangeManager), "SetGameEventPrice")]
        private static class GameEventFeePatch
        {
            [HarmonyPrefix]
            private static void Prefix(EGameEventFormat gameEventFormat, out float __state)
                => __state = PriceChangeManager.GetGameEventPrice(gameEventFormat);

            [HarmonyPostfix]
            private static void Postfix(EGameEventFormat gameEventFormat, float price, float __state)
                => SendGameEventFeeIntent(gameEventFormat, price, __state);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCanCheckout")]
        private static class CashierCheckoutPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, out byte __state)
                => __state = (byte)((__instance.CanCheckout() ? 1 : 0)
                    | (__instance.CanTradeCard() ? 2 : 0));

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, byte __state)
                => SendCashierIntent(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCanTradeCard")]
        private static class CashierTradePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, out byte __state)
                => __state = (byte)((__instance.CanCheckout() ? 1 : 0)
                    | (__instance.CanTradeCard() ? 2 : 0));

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, byte __state)
                => SendCashierIntent(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractablePlayTable), "Awake")]
        private static class TableInitPatch
        {
            // A play table bought at runtime is materialized after the settings mutation that
            // carries its number. Awake resets the number to 0 (after ShelfManager.InitPlayTable
            // registers it), so retry the deferred state once Awake has finished.
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPendingState();
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "Awake")]
        private static class CashierInitPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPendingState();
        }

        [HarmonyPatch(typeof(InteractablePlayTable), "SetTournamentPlayTableNumber")]
        private static class TableNumberPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePlayTable __instance, out int __state)
                => __state = __instance.GetTournamentPlayTableNumber();

            [HarmonyPostfix]
            private static void Postfix(InteractablePlayTable __instance, int tableNumber, int __state)
                => SendTableIntent(__instance, tableNumber, __state);
        }

        private struct FormatBefore
        {
            internal EGameEventFormat Format;
            internal ECardExpansionType Expansion;
        }

    }
}
