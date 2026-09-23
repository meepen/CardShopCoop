using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host authority for who is currently holding a placed object for placement.</summary>
    public sealed partial class WorldHostBehaviour
    {
        private PlacementHoldInteraction _placementHold;
        private readonly List<long> _releasedHolds = new();

        internal void InstallPlacementHold()
        {
            _placementHold = new PlacementHoldInteraction(true, _context.Broadcast, _context.Send);
            _harmony.CreateClassProcessor(typeof(PlacementHoldStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlacementHoldRotatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlacementHoldFlipPatch)).Patch();
        }

        internal void ShutdownPlacementHold()
        {
            _placementHold?.Reset();
            _placementHold = null;
        }

        internal void TickPlacementHold() => _placementHold?.Tick();

        [MessageHandler(typeof(PlacementHoldBeginRequestMessage))]
        private void HandlePlacementHoldBegin(MessageContext context,
            PlacementHoldBeginRequestMessage message)
        {
            if (message == null || _placementHold == null || !_context.InGame()
                || !IsFullyJoinedSender(context))
            {
                return;
            }

            if (_placementHold.HostGrant(message.HoldKey, context.ConnectionId, out var granted))
            {
                // The host is an observer of a client's hold too, so render it here as well as
                // broadcasting to the other peers.
                _placementHold.ApplyBegin(granted);
                _context.Broadcast(granted);
            }
        }

        [MessageHandler(typeof(PlacementHoldEndRequestMessage))]
        private void HandlePlacementHoldEnd(MessageContext context,
            PlacementHoldEndRequestMessage message)
        {
            if (message == null || _placementHold == null || !_context.InGame()
                || !IsFullyJoinedSender(context))
            {
                return;
            }

            if (_placementHold.HostRelease(message.HoldKey, context.ConnectionId))
            {
                // The host rendered this client's hold when it granted it, so it must stop that
                // local render as well as telling the peers.
                _placementHold.ApplyEnd(message.HoldKey);
                _context.Broadcast(new PlacementHoldEndMessage { HoldKey = message.HoldKey });
            }
        }

        [OnClientDisconnected]
        private void ReleasePlacementHolds(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _placementHold?.ReleaseMover(connection.Id, _releasedHolds);
            }
        }

        [MessageHandler(typeof(PlacementHoldRotationRequestMessage))]
        private void HandlePlacementHoldRotation(MessageContext context,
            PlacementHoldRotationRequestMessage message)
        {
            if (message == null || _placementHold == null || !_context.InGame()
                || !IsFullyJoinedSender(context)
                || !_placementHold.IsHeldBy(message.HoldKey, context.ConnectionId))
            {
                return;
            }

            // The host renders this client's ghost too, so apply the rotation here and relay the
            // accepted value to every observer.
            var rotation = new PlacementHoldRotationMessage
            {
                HoldKey = message.HoldKey,
                Rotation = message.Rotation,
            };
            _placementHold.ApplyRotation(rotation);
            _context.Broadcast(rotation);
        }

        [HarmonyPatch(typeof(InteractableObject), "StartMoveObject")]
        private static class PlacementHoldStartPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
                => _instance?._placementHold == null
                    || _instance._placementHold.IsAllowed(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalStarted(__instance);
        }

        [HarmonyPatch(typeof(InteractableObject), "AddObjectRotation")]
        private static class PlacementHoldRotatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalRotated(__instance);
        }

        [HarmonyPatch(typeof(InteractableObject), "Flip")]
        private static class PlacementHoldFlipPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance)
                => _instance?._placementHold?.LocalRotated(__instance);
        }
    }
}
