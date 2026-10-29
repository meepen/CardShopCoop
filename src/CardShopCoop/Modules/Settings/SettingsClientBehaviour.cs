using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
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

            var handlersRegistered = false;
            try
            {
                RuntimeContext.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.settings.client");
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
                    RuntimeContext.Messages.UnregisterAttributedHandlers(this);
                }

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

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
            if (_shutdown || !RuntimeContext.InGame()
                || !SettingsInterop.IsSceneReady)
            {
                return;
            }

            if (_pendingFullState != null)
            {
                var full = _pendingFullState;
                _pendingFullState = null;
                // Confirm, not AckOrApply: the host's state is authoritative and can carry a
                // clamped/transformed value the optimistic local run did not produce.
                ActionLedger.Confirm(full.ActionId, () => ApplyState(full));
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
                    // Confirm: retire the actor's prediction and apply the host's absolute value
                    // (the host may clamp or otherwise transform the requested value).
                    ActionLedger.Confirm(state.ActionId, () => ApplyState(state));
                }
            }
        }

        /// <summary>True when a partial mutation's live element exists locally. Tombstones never
        /// need the element (they only trim the local tail), and a full state is applied with
        /// per-element skips. A partial whose live object is null was already dropped when it was
        /// decoded.</summary>
        private static bool CanApply(SettingsStateMessage message)
        {
            if (message == null || message.Full || message.Tombstone)
            {
                return true;
            }

            return message.Index switch
            {
                6 => message.Cashier != null,
                7 => message.Table != null,
                _ => true,
            };
        }

        [MessageHandler]
        private void HandleState(MessageContext context, SettingsStateMessage message)
        {
            if (_shutdown)
            {
                return;
            }


            if (!message.Full && message.Index == 6 && message.Cashier == null)
            {
                CoopPlugin.Log.LogWarning("Settings client: dropped cashier state with no live counter.");
                ActionLedger.Ack(message.ActionId);
                return;
            }

            if (!message.Full && message.Index == 7 && message.Table == null)
            {
                CoopPlugin.Log.LogWarning("Settings client: dropped table state with no live table.");
                ActionLedger.Ack(message.ActionId);
                return;
            }

            if (message.Full)
            {
                ClearPendingStates();
                _pendingFullState = message;
            }
            else
            {
                var key = StateKey(message);
                if (_pendingStates.TryGetValue(key, out var previous))
                {
                    ActionLedger.Ack(previous.ActionId);
                }


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
                ApplyCashiers(message.CashierFlags, message.Cashiers, message.CashierFlags.Count);
                ApplyTables(message.TableNumbers, message.Tables, message.TableNumbers.Count);
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
                // The authoritative length trims/resets local tails; the live counter then carries
                // the one counter's flags. There is no index or key on the wire to fall back to.
                ApplyCashierTail(message.CashierCount);
                if (!message.Tombstone && message.CashierFlags.Count > 0)
                {
                    ApplyCashier(message.Cashier, message.CashierFlags[0]);
                }
            }
            else if (message.Index == 7)
            {
                ApplyTableTail(message.TableCount);
                if (!message.Tombstone && message.TableNumbers.Count > 0)
                {
                    ApplyTable(message.Table, message.TableNumbers[0]);
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
        {
            if (message.Index == 6)
            {
                return "6:" + (message.Cashier == null ? 0 : message.Cashier.GetInstanceID());
            }

            if (message.Index == 7)
            {
                return "7:" + (message.Table == null ? 0 : message.Table.GetInstanceID());
            }

            return message.Index + ":" + message.ItemIndex;
        }

        private void ClearPendingStates()
        {
            foreach (var state in _pendingStates.Values)
            {
                ActionLedger.Ack(state.ActionId);
            }


            _pendingStates.Clear();
            if (_pendingFullState != null)
            {
                ActionLedger.Ack(_pendingFullState.ActionId);
            }


            _pendingFullState = null;
        }

        /// <summary>Applies one counter's flags to the live counter the host named. A null object
        /// is an unresolved entity and is logged and dropped.</summary>
        private static void ApplyCashier(InteractableCashierCounter counter, byte flags)
        {
            if (counter == null)
            {
                CoopPlugin.Log.LogWarning("Settings client: dropped cashier flags with no live counter.");
                return;
            }

            counter.SetCanCheckout((flags & 1) != 0);
            counter.SetCanTradeCard((flags & 2) != 0);
        }

        /// <summary>Applies one table's number to the live table the host named.</summary>
        private static void ApplyTable(InteractablePlayTable table, byte number)
        {
            if (table == null)
            {
                CoopPlugin.Log.LogWarning("Settings client: dropped table number with no live table.");
                return;
            }

            table.SetTournamentPlayTableNumber(number);
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

        /// <summary>Applies a full baseline's cashier flags to the live counters the host named. The
        /// authoritative length still trims the local tail, but each flagged entry is applied to
        /// its object, never by list position. A null entry is logged and dropped.</summary>
        private static void ApplyCashiers(List<byte> flags,
            List<InteractableCashierCounter> counters, int count)
        {
            ApplyCashierTail(count);
            for (var i = 0; i < flags.Count && i < counters.Count; i++)
            {
                ApplyCashier(counters[i], flags[i]);
            }
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

        /// <summary>Applies a full baseline's table numbers to the live tables, mirroring
        /// <see cref="ApplyCashiers"/>: position never addresses an entry, and a null entry is
        /// logged and dropped.</summary>
        private static void ApplyTables(List<byte> numbers, List<InteractablePlayTable> tables,
            int count)
        {
            ApplyTableTail(count);
            for (var i = 0; i < numbers.Count && i < tables.Count; i++)
            {
                ApplyTable(tables[i], numbers[i]);
            }
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
                tables[i]?.SetTournamentPlayTableNumber(0);
            }
        }

        private static void SendGameEventIntent(EGameEventFormat previousFormat,
            ECardExpansionType previousExpansion)
        {
            if (_applyingRemote || _active == null || _active._shutdown
                || !_active.RuntimeContext.InGame())
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
                || !_active.RuntimeContext.InGame())
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
                || !_active.RuntimeContext.InGame() || counter == null)
            {
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpCashier,
                Cashier = counter,
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
                || !_active.RuntimeContext.InGame() || table == null)
            {
                return;
            }

            _active.SendIntent(new SettingsOpMessage
            {
                Op = OpTableNumber,
                Table = table,
                TableNumber = number,
            }, () => ApplyUndo(() => table.SetTournamentPlayTableNumber(previousNumber)));
        }

        private void SendIntent(SettingsOpMessage message, Action undo)
        {
            if (_shutdown || message == null || !_joined || !RuntimeContext.InGame() || _applyingRemote)
            {
                return;
            }

            // Post-hoc prediction: the game already applied the mutation in the postfix that
            // called here, so only a rejection's undo closure is needed.
            ActionLedger.Submit("settings",
                actionId =>
                {
                    message.ActionId = actionId;
                    RuntimeContext.Send(1, message);
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
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            ClearPendingStates();
            _joined = false;
            _applyingRemote = false;
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
