using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host-side owner for shared world state.</summary>
    [ServerBehaviour]
    public sealed partial class WorldHostBehaviour : CoopBehaviour
    {
        private static WorldHostBehaviour _instance;
        private bool _shutdown;
        private bool _initialized;
        private BoxNetworkInteraction _boxNetworkInteraction;

        internal static WorldHostBehaviour Active => _instance;
        private PlayerBoxInteraction _playerBoxInteraction;
        private ShelfInteraction _shelfInteraction;
        private WarehouseShelfInteraction _warehouseShelfInteraction;
        private WorldCardInteraction _cardInteraction;
        private long _nextBaselineId = 1;
        // Per-connection frozen identity manifests captured when each guest's save is written.
        // A later join never clobbers an earlier one, and each baseline reads only its own.
        private readonly Dictionary<int, WorldTransferManifest> _transferManifests = new();

        private void OnEnable()
        {
            if (_initialized)
            {
                return;
            }


            _instance = this;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            // One send seam. Host state is broadcast to every peer, or sent to the single peer
            // that needs it; a relay excludes the sender. No helper keeps a connection id.
            _boxNetworkInteraction = new BoxNetworkInteraction(true, Context.Broadcast,
                (peer, message) => peer.Send(message));
            _playerBoxInteraction = new PlayerBoxInteraction(_boxNetworkInteraction, Context.Broadcast);
            _shelfInteraction = new ShelfInteraction(Context, true);
            _warehouseShelfInteraction = new WarehouseShelfInteraction(true, Context.Broadcast,
                (peer, message) => peer.Send(message), _boxNetworkInteraction, _playerBoxInteraction,
                () => Context.InGame);
            _cardInteraction = new WorldCardInteraction(RuntimeContext, true, _boxNetworkInteraction,
                _playerBoxInteraction)
            {
                BroadcastOverride = Context.Broadcast,
                RelayOverride = Context.BroadcastExcept,
                SendOverride = (peer, message) => peer.Send(message),
            };
            _cardInteraction.Containers.BroadcastState = Context.Broadcast;
            _cardInteraction.Containers.SendToClient = (peer, message) => peer.Send(message);
            _cardInteraction.Containers.RelayState = Context.BroadcastExcept;
            _cardInteraction.Containers.InGameProvider = RuntimeContext.InGame;
            InstallBoxNetworkInteractionPatches();
            InstallPlayerBoxInteractionPatches();
            InstallPlayerBoxPresenceHook();
            InstallPlayerShelfInteractionPatches();
            InstallWarehouseShelfInteractionPatches();
            InstallCardInteractionPatches();
            InstallCardDisplayPatches();
            InstallPlacement();
            InstallPlacementHold();
            InstallWorkbench();
            _initialized = true;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            UninstallPlayerBoxPresenceHook();
            ShutdownPlacementHold();
            ShutdownPlacement();
            ShutdownWorkbench();
            ResetPlayerBoxInteractionState();
            ResetPlayerShelfInteractionState();
            _boxNetworkInteraction?.Dispose();
            _boxNetworkInteraction = null;
            _warehouseShelfInteraction?.Reset();
            _warehouseShelfInteraction = null;
            _cardInteraction?.Shutdown();
            _cardInteraction = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            _nextBaselineId = 1;
            _transferManifests.Clear();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }


        }

        private void OnDestroy() => Shutdown();

        private void Update()
        {
            if (!_shutdown)
            {
                _cardInteraction?.Tick();
                TickPlacementHold();
            }
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
        {
            WorldMarketInteraction.InvalidateContentCache();
            _cardInteraction?.FlushWorldReady();
            _warehouseShelfInteraction?.Reset();
        }

        internal static WorldCardInteraction ActiveCards => _instance?._cardInteraction;

        internal static void ResetCardState() => _instance?._cardInteraction?.Reset();

        /// <summary>Resolves the stable world box id for a worker's carried box, if the host box
        /// network has one. Used by the Npc worker sync so clients can retire the real object.</summary>
        internal static bool TryGetHeldBoxId(InteractablePackagingBox box, out Guid boxNetworkId)
        {
            boxNetworkId = Guid.Empty;
            return box != null && _instance?._boxNetworkInteraction != null
                && _instance._boxNetworkInteraction.TryGetId(box, out boxNetworkId);
        }

        /// <summary>Authoritative open/closed flag of a carried box, read directly from the field
        /// so the whole open-close animation window reports the true state.</summary>
        internal static bool IsHeldBoxOpen(InteractablePackagingBox box)
            => box is InteractablePackagingBox_Item item && BoxNetworkInteraction.IsBoxOpen(item);

        /// <summary>Host: a worker stopped carrying this box. Re-announce its authoritative pose so
        /// guests lift their parked copy back into the world (unless it has since been stored or
        /// destroyed, which its own channel already handled).</summary>
        internal static void NotifyWorkerReleasedBox(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.HostRefreshReleasedWorkerBox(boxNetworkId);

        /// <summary>Freeze the identity manifest for one connection at the instant its world save
        /// is serialized. The box and placement baselines for that connection read it back by
        /// connection id, so a later join can never clobber an earlier one's snapshot and a host
        /// that keeps playing while the guest loads cannot shift a slot.</summary>
        internal static void CaptureTransferManifest(int connectionId)
        {
            var host = _instance;
            if (host == null)
            {
                CoopPlugin.Log.LogWarning(
                    "World transfer manifest could not be captured: the world host is not active.");
                return;
            }

            if (connectionId <= 0)
            {
                // A caller without a connection scope (the public API) still gets the save, but
                // there is no per-connection state to attach a manifest to.
                return;
            }

            var manifest = new WorldTransferManifest();
            host._boxNetworkInteraction?.CaptureTransferManifest(manifest);
            host._placementSlots.CaptureTransferManifest(manifest);
            host._transferManifests[connectionId] = manifest;
            CoopPlugin.Log.LogInfo("[transfer] captured identity manifest for conn " + connectionId
                + " (boxes=" + manifest.BoxSlotCount + ", placements=" + manifest.PlacementSlotCount
                + ").");
        }

        /// <summary>The frozen manifest for a connection, or null when none was captured (a
        /// scene-reload re-baseline). Reads without removing: a scene reload must not silently
        /// invalidate a snapshot still needed by a concurrently-joining peer.</summary>
        private WorldTransferManifest TransferManifestFor(int connectionId)
            => connectionId > 0 && _transferManifests.TryGetValue(connectionId, out var manifest)
                ? manifest : null;

        /// <summary>Drop a departed connection's frozen manifest; it can no longer baseline.</summary>
        [OnClientDisconnected]
        private void ForgetTransferManifest(PeerConnection connection, DisconnectInfo _)
        {
            if (connection != null)
            {
                _transferManifests.Remove(connection.Id);
            }
        }

        /// <summary>A peer finished joining and may receive gameplay traffic: send it the complete
        /// shared-world baseline for its own world.</summary>
        [OnFullyJoined]
        private void OnPeerFullyJoined(PeerConnection connection)
        {
            var peer = PeerHandle.ForConnection(connection);
            if (peer != null && Context.InGame)
            {
                SendWorldBaseline(peer);
            }
        }

        private void SendWorldBaseline(ICoopPeer peer)
        {
            var baselineId = _nextBaselineId++;
            if (_nextBaselineId <= 0)
            {
                _nextBaselineId = 1;
            }

            Util.MemoryProbe.Log("world-baseline:before peer=" + peer.Name);
            // Every baseline message is for this one joining peer only.
            Action<INetMessage> send = peer.Send;
            send(new WorldBaselineStartMessage
            {
                BaselineId = baselineId,
            });
            // Carried boxes are not part of the box descriptor: a box a peer is already holding
            // would otherwise freeze at the transfer-time pose on the joiner instead of riding the
            // holder's avatar. Announce each hold after the descriptors that created the boxes.
            AppendPlayerBoxHoldBaseline(send);
            _warehouseShelfInteraction?.AppendBaselineMessages(send);
            _cardInteraction?.AppendBaselineMessages(send);
            AppendWorkbenchBaseline(send);
            AppendCardDisplayBaseline(send);
            send(new WorldBaselineCompleteMessage
            {
                BaselineId = baselineId,
            });
            Util.MemoryProbe.Log("world-baseline:after peer=" + peer.Name);
        }
    }
}
