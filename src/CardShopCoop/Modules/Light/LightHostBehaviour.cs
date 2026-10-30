using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Host authority for the shop-light state.</summary>
    [ServerBehaviour]
    public sealed class LightHostBehaviour : CoopBehaviour
    {
        private static LightHostBehaviour _active;
        private bool _shutdown;
        private bool _initialized;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }


            var lifecycle = false;
            try
            {
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


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendInitialState(PeerConnection connection)
        {
            SendState(PeerHandle.ForConnection(connection));
        }

        [MessageHandler]
        private ActionOutcome HandleIntent(CoopMessageContext context, LightSwitchIntentMessage message)
        {
            if (!IsPeerMessage(context) || message == null)
            {
                return ActionOutcome.Reject("shop-light sender is not an admitted peer");
            }


            if (!LightInterop.IsSceneReady(LightInterop.FindSceneManager()))
            {
                return ActionOutcome.Reject("shop-light scene is not ready");
            }

            LightSwitchState.Apply(message.IsActive);
            if (!LightSwitchState.TryGet(out var isActive))
            {
                return ActionOutcome.Applied();
            }

            var state = new LightSwitchStateMessage { IsActive = isActive };
            if (context.Peer != null)
            {
                Context.BroadcastExcept(context.Peer, state);
            }

            return ActionOutcome.Applied(new LightSwitchResponse { IsActive = isActive });
        }

        private void BroadcastState()
        {
            if (_shutdown || !RuntimeContext.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }


            Context.Broadcast(new LightSwitchStateMessage { IsActive = isActive });
        }

        private void SendState(ICoopPeer peer)
        {
            if (peer == null || _shutdown || !RuntimeContext.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }


            peer.Send(new LightSwitchStateMessage { IsActive = isActive });
        }

        private void SendStateToJoined()
        {
            BroadcastState();
        }

        /// <summary>Every live toggle - the physical switch click and mod-driven automation alike -
        /// runs through LightManager.ToggleShopLight. Apply the module's full surface locally
        /// (automation does not update the switch models vanilla's click touches) and publish the
        /// new state. The save-load restore and the day-rollover reset write the group directly
        /// and are covered by the ready baselines and the GameTime mirror.</summary>
        private void OnShopLightToggled()
        {
            if (_shutdown || !RuntimeContext.InGame()
                || !LightSwitchState.TryGet(out var isActive))
            {
                return;
            }

            LightSwitchState.Apply(isActive);
            BroadcastState();
        }

        private bool IsPeerMessage(CoopMessageContext context)
            => !_shutdown && context?.Peer != null && context.IsAuthenticated
                && context.Peer.State == ConnectionState.FullyJoined;

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
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


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
