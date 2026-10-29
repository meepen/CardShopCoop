using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Host authority for the shop-light state.</summary>
    [ServerBehaviour]
    public sealed class LightHostBehaviour : CoopBehaviour
    {
        private static LightHostBehaviour _active;
        private readonly HashSet<int> _fullyJoined = new();
        private CoopRuntimeContext _context;
        private bool _shutdown;
        private bool _initialized;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }


            _context = RuntimeContext;
            var registered = false;
            var lifecycle = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                Harmony.CreateClassProcessor(typeof(LightSwitchPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(LightReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Light host initialization failed: " + exception);
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
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
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }


            _fullyJoined.Add(connection.Id);
            SendState(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _fullyJoined.Remove(connection.Id);
            }

        }

        [MessageHandler]
        private void HandleIntent(MessageContext context, LightSwitchIntentMessage message)
        {
            if (!IsPeerMessage(context))
            {
                return;
            }


            if (!LightInterop.IsSceneReady(LightInterop.FindSceneManager()))
            {
                ActionLedger.Rollback(_context, context.Connection.Id, message.ActionId);
                return;
            }
            LightSwitchState.Apply(message.IsActive);
            BroadcastState(message.ActionId);
        }

        private void BroadcastState(Guid actionId)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }


            _context.Broadcast(new LightSwitchStateMessage
            {
                ActionId = actionId,
                IsActive = isActive,
            });
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }


            _context.Send(peer, new LightSwitchStateMessage
            {
                ActionId = Guid.Empty,
                IsActive = isActive,
            });
        }

        private void SendStateToJoined()
        {
            foreach (var peer in new List<int>(_fullyJoined))
            {
                SendState(peer);
            }

        }

        /// <summary>Every live toggle - the physical switch click and mod-driven automation alike -
        /// runs through LightManager.ToggleShopLight. Apply the module's full surface locally
        /// (automation does not update the switch models vanilla's click touches) and publish the
        /// new state. The save-load restore and the day-rollover reset write the group directly
        /// and are covered by the ready baselines and the GameTime mirror.</summary>
        private void OnShopLightToggled()
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }

            LightSwitchState.Apply(isActive);
            BroadcastState(Guid.Empty);
        }

        private bool IsPeerMessage(MessageContext context)
            => !_shutdown && context?.Connection != null
                && _fullyJoined.Contains(context.Connection.Id);

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => SendStateToJoined();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => SendStateToJoined();

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


            _fullyJoined.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        // The switch click and every mod-driven automation path run through ToggleShopLight, so
        // hooking this mutation instead of the click keeps automation in sync, installed or not.
        [HarmonyPatch(typeof(LightManager), "ToggleShopLight")]
        private static class LightSwitchPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.OnShopLightToggled();
        }

        [HarmonyPatch(typeof(LightManager), "Awake")]
        private static class LightReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.SendStateToJoined();
        }
    }
}
