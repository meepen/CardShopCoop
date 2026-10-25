using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Host authority for the two access signs and the session-wide state they drive.</summary>
    [ServerBehaviour]
    public sealed class StoreAccessHostBehaviour : CoopBehaviour
    {
        private const byte ShopSign = 0;
        private const byte WarehouseSign = 1;

        private static StoreAccessHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            StoreAccessInterop.Reset();
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("dev.meepen.cardshopcoop.store-access.host");
            try
            {
                _harmony.CreateClassProcessor(typeof(SignChangedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignChangedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoreOpenedPatch)).Patch();
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        /// <summary>Seeds the session value before the transport starts. The generic
        /// extended-information baseline delivers it to every joiner, so there is deliberately no
        /// per-peer join message. The guest gates its sign click on this value (an optimistic
        /// toggle needs an authoritative baseline to replay over); without the seed the host only
        /// had a value after its own first sign change or day roll-over, so a guest could not be
        /// the one to open the shop during the tutorial.</summary>
        [OnSessionStarted]
        private void SeedState()
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            if (!_context.InGame())
            {
                CoopPlugin.Log.LogWarning("[store-access] host has no loaded shop at session start; "
                    + "the first sign change or day roll-over will publish the access state.");
                return;
            }

            ExtendedInformationModule.SeedGlobal(typeof(StoreAccessState),
                BuildState(Guid.Empty, rejected: false));
            CoopPlugin.Log.LogInfo("[store-access] seeded shop="
                + (CPlayerData.m_IsShopOpen ? "open" : "closed") + " warehouse="
                + (CPlayerData.m_IsWarehouseDoorClosed ? "closed" : "open") + ".");
        }

        /// <summary>True while a client intent is driving the host's vanilla click, so the local
        /// sign hook does not publish a second, uncorrelated state.</summary>
        internal bool ApplyingIntent => _applyingIntent;

        [IntentValidator]
        private IntentValidation ValidateToggle(CoopMessageContext context,
            StoreAccessToggleIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            if (!context.IsAuthenticated || !context.InGame)
            {
                return IntentValidation.Deny("player is not in the game");
            }

            if (context.Connection.State != ConnectionState.FullyJoined)
            {
                return IntentValidation.Deny("player is still joining");
            }

            if (message.Which > WarehouseSign)
            {
                return IntentValidation.Deny("unknown access sign");
            }

            return FindSign(message.Which) == null
                ? IntentValidation.Deny("the access sign is not available")
                : IntentValidation.Allow();
        }

        [IntentRejected]
        private void ToggleRejected(CoopMessageContext context, StoreAccessToggleIntent message,
            string reason)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("[store-access] rejected toggle sign=" + message.Which
                + ": " + reason);
            PublishState(message.ActionId, rejected: true);
        }

        [MessageHandler]
        private void HandleToggle(CoopMessageContext context, StoreAccessToggleIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            if (message.Which > WarehouseSign)
            {
                CoopPlugin.Log.LogWarning("[store-access] unknown sign " + message.Which
                    + " for toggle " + message.ActionId + ".");
                PublishState(message.ActionId, rejected: true);
                Rollback(context.ConnectionId, message.ActionId);
                return;
            }

            var sign = FindSign(message.Which);
            if (sign == null)
            {
                CoopPlugin.Log.LogWarning("[store-access] sign=" + message.Which
                    + " disappeared before toggle " + message.ActionId + ".");
                PublishState(message.ActionId, rejected: true);
                Rollback(context.ConnectionId, message.ActionId);
                return;
            }

            var before = ReadFlag(message.Which);
            _applyingIntent = true;
            try
            {
                // The vanilla click applies the flag, plays the host sign, and enforces the
                // game's own tutorial/swap gates. It is the same path a local host click uses.
                sign.OnMouseButtonUp();
            }
            finally
            {
                _applyingIntent = false;
            }

            if (ReadFlag(message.Which) == before)
            {
                // The vanilla gate refused the click (for example the shop cannot be opened yet
                // or the sign is still swapping). Publish the unchanged authority under the
                // action id so the requester retires its optimistic toggle, then roll it back.
                CoopPlugin.Log.LogWarning("[store-access] toggle sign=" + message.Which
                    + " made no change; rolling back " + message.ActionId + ".");
                PublishState(message.ActionId, rejected: true);
                Rollback(context.ConnectionId, message.ActionId);
                return;
            }

            PublishState(message.ActionId, rejected: false);
        }

        private static InteractableObject FindSign(byte which)
            => which == ShopSign
                ? StoreAccessInterop.FindOpenSign()
                : StoreAccessInterop.FindWarehouseSign();

        private static bool ReadFlag(byte which)
            => which == ShopSign
                ? CPlayerData.m_IsShopOpen
                : CPlayerData.m_IsWarehouseDoorClosed;

        private void Rollback(int connectionId, Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }

            ActionLedger.Rollback(_context, connectionId, actionId);
        }

        /// <summary>Publishes the host's current access flags as the one session-wide value. The
        /// store owns the broadcast, baseline and client apply; the host has no handler, exactly
        /// like the register's authoritative snapshot.</summary>
        private void PublishState(Guid actionId, bool rejected)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || CoopCore.Instance?.Net == null)
            {
                return;
            }

            Global<StoreAccessState>.Value = BuildState(actionId, rejected);
            CoopPlugin.Log.LogInfo("[store-access] state shop="
                + (CPlayerData.m_IsShopOpen ? "open" : "closed") + " warehouse="
                + (CPlayerData.m_IsWarehouseDoorClosed ? "closed" : "open")
                + " action=" + actionId + (rejected ? " (rejection)" : "") + ".");
        }

        private static StoreAccessState BuildState(Guid actionId, bool rejected)
            => new()
            {
                IsShopOpen = CPlayerData.m_IsShopOpen,
                IsWarehouseDoorClosed = CPlayerData.m_IsWarehouseDoorClosed,
                ActionId = actionId,
                Rejected = rejected,
            };

        private void OnSceneLoaded(Scene _, LoadSceneMode __) => StoreAccessInterop.Reset();

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            StoreAccessInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnMouseButtonUp")]
        private static class SignChangedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state) => __state = CPlayerData.m_IsShopOpen;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                if (_active == null || _active._applyingIntent
                    || CPlayerData.m_IsShopOpen == __state)
                {
                    return;
                }

                _active.PublishState(Guid.Empty, rejected: false);
            }
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp")]
        private static class WarehouseSignChangedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
                => __state = CPlayerData.m_IsWarehouseDoorClosed;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                if (_active == null || _active._applyingIntent
                    || CPlayerData.m_IsWarehouseDoorClosed == __state)
                {
                    return;
                }

                _active.PublishState(Guid.Empty, rejected: false);
            }
        }

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnDayStarted")]
        private static class StoreOpenedPatch
        {
            // Vanilla resets m_IsShopOpen at the roll-over. Publish both access flags so the guest
            // does not run its own reset (it blocks OnDayStarted) and every late joiner gets the
            // closed state in the extended-information baseline.
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || _active._applyingIntent)
                {
                    return;
                }

                _active.PublishState(Guid.Empty, rejected: false);
            }
        }
    }
}
