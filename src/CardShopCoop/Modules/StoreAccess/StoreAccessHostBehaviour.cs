using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Host authority for the two access signs and their late-join state.</summary>
    [ServerBehaviour]
    public sealed class StoreAccessHostBehaviour : CoopBehaviour
    {
        private static StoreAccessHostBehaviour _active;
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applyingIntent;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                StoreAccessInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.store-access.host");
                _harmony.CreateClassProcessor(typeof(SignChangedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignChangedPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(StoreOpenedPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Store access host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                StoreAccessInterop.Reset();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
                return;

            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
                _fullyJoined.Remove(connection.Id);
        }

        [MessageHandler(typeof(StoreAccessToggleMessage))]
        private void HandleToggle(MessageContext context, StoreAccessToggleMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
                return;

            var peer = context.Connection.Id;
            var accepted = false;
            try
            {
                if (message.Which > 1)
                {
                    Rollback(context, message);
                }
                else if (!StoreAccessInterop.IsSceneReady())
                {
                    Rollback(context, message);
                }
                else if (message.Which == 0)
                {
                    var sign = StoreAccessInterop.FindOpenSign();
                    if (sign == null)
                        Rollback(context, message);
                    else
                    {
                        var before = CPlayerData.m_IsShopOpen;
                        _applyingIntent = true;
                        try
                        {
                            // Run the vanilla click so the host sign animates exactly like a local
                            // click. It is a no-op while the previous swap is still playing or the
                            // tutorial gate blocks, in which case there is nothing to confirm.
                            sign.OnMouseButtonUp();
                        }
                        finally
                        {
                            _applyingIntent = false;
                        }

                        accepted = CPlayerData.m_IsShopOpen != before;
                        if (!accepted)
                            Rollback(context, message);
                    }
                }
                else
                {
                    var sign = StoreAccessInterop.FindWarehouseSign();
                    if (sign == null)
                        Rollback(context, message);
                    else
                    {
                        var before = CPlayerData.m_IsWarehouseDoorClosed;
                        _applyingIntent = true;
                        try
                        {
                            sign.OnMouseButtonUp();
                        }
                        finally
                        {
                            _applyingIntent = false;
                        }

                        accepted = CPlayerData.m_IsWarehouseDoorClosed != before;
                        if (!accepted)
                            Rollback(context, message);
                    }
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Store access toggle failed for peer " + peer + ": " + exception);
                Rollback(context, message);
            }

            if (accepted)
                BroadcastDelta(BuildDelta(message.PredictionId, message.Which == 0, message.Which == 1));
        }

        private void BroadcastDelta(StoreAccessDeltaMessage message)
        {
            if (_shutdown || _context == null || !_context.InGame() || message == null)
                return;

            _context.Broadcast(message);
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame())
                return;

            _context.Send(peer, BuildState(false));
        }

        private static StoreAccessDeltaMessage BuildDelta(Guid predictionId,
            bool shopOpen, bool warehouseClosed)
        {
            return new StoreAccessDeltaMessage
            {
                PredictionId = predictionId,
                HasShopOpen = shopOpen,
                IsShopOpen = CPlayerData.m_IsShopOpen,
                HasWarehouseDoorClosed = warehouseClosed,
                IsWarehouseDoorClosed = CPlayerData.m_IsWarehouseDoorClosed,
                Animate = true,
            };
        }

        private static void Rollback(MessageContext context, StoreAccessToggleMessage message)
        {
            if (context?.Connection != null && message != null
                && message.PredictionId != Guid.Empty)
                PredictionApi.Rollback(_active._context, context.Connection.Id, message.PredictionId);
        }

        private static StoreAccessStateMessage BuildState(bool animate)
            => new StoreAccessStateMessage
            {
                Animate = animate,
                IsShopOpen = CPlayerData.m_IsShopOpen,
                IsWarehouseDoorClosed = CPlayerData.m_IsWarehouseDoorClosed,
            };

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            StoreAccessInterop.Reset();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            _fullyJoined.Clear();
            StoreAccessInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnMouseButtonUp")]
        private static class SignChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta(Guid.Empty, true, false));
            }
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp")]
        private static class WarehouseSignChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta(Guid.Empty, false, true));
            }
        }

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnDayStarted")]
        private static class StoreOpenedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || _active._applyingIntent)
                    return;
                _active.BroadcastDelta(BuildDelta(Guid.Empty, true, true));
            }
        }
    }
}
