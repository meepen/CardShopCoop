using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
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
        private Harmony _harmony;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.light.host");
                _harmony.CreateClassProcessor(typeof(LightSwitchPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(LightReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Light host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
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
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
                _fullyJoined.Remove(connection.Id);
        }

        [MessageHandler(typeof(LightSwitchIntentMessage))]
        private void HandleIntent(MessageContext context, LightSwitchIntentMessage message)
        {
            if (!IsPeerMessage(context))
                return;

            if (!LightInterop.IsSceneReady(LightInterop.FindSceneManager()))
            {
                PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                return;
            }
            LightSwitchState.Apply(message.IsActive);
            BroadcastState(message.PredictionId);
        }

        private void BroadcastState(Guid predictionId)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !LightSwitchState.TryGet(out var isActive))
                return;

            _context.Broadcast(new LightSwitchStateMessage
            {
                PredictionId = predictionId,
                IsActive = isActive,
            });
        }

        private void SendState(int peer)
        {
            if (_shutdown || _context == null || !_context.InGame()
                || !LightSwitchState.TryGet(out var isActive))
                return;

            _context.Send(peer, new LightSwitchStateMessage
            {
                PredictionId = Guid.Empty,
                IsActive = isActive,
            });
        }

        private void SendStateToJoined()
        {
            foreach (var peer in new List<int>(_fullyJoined))
                SendState(peer);
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
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
                _active = null;
            _fullyJoined.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableLightSwitch), "OnMouseButtonUp")]
        private static class LightSwitchPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.BroadcastState(Guid.Empty);
        }

        [HarmonyPatch(typeof(LightManager), "Awake")]
        private static class LightReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.SendStateToJoined();
        }
    }
}
