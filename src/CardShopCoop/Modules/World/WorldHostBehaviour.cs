using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host-side owner for shared world state.</summary>
    [ServerBehaviour]
    public sealed partial class WorldHostBehaviour : CoopBehaviour
    {
        private static WorldHostBehaviour _instance;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
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
        private int _furniturePurchaseSpawnDepth;
        private readonly Stack<InteractablePackagingBox_Shelf>
            _pendingFurniturePurchaseBoxes = new();

        // Host intent ledger. Every accepted world intent's prediction id is recorded once, keyed
        // to the connection that sent it, so a resent intent cannot double-apply and a peer cannot
        // retire another peer's prediction by naming its id. An id is a client-minted Guid, so no
        // two connections share one.
        //
        // The per-connection set is bounded. Fire-and-forget world intents (covered-hold drops,
        // item-box state mirrors, workbench bundles, legacy card forwards, forced placements) each
        // mint a fresh Guid and are never explicitly retired, so an unbounded set would grow for a
        // whole session. An entry only has to outlive the transport's resend window: the reliable
        // lane retransmits a lost intent long before this many later intents arrive. Oldest-first
        // eviction therefore keeps dedup and the cross-connection reject correct for every intent
        // still in the window while capping memory. Eviction drops the reverse-index entry too, or
        // the owner map would grow instead.
        private readonly Dictionary<int, AppliedIntentLedger> _appliedPredictions = new();
        private readonly Dictionary<Guid, int> _predictionOwners = new();

        /// <summary>One connection's bounded, insertion-ordered set of applied world-intent ids.</summary>
        private sealed class AppliedIntentLedger
        {
            // Generous relative to the transport resend window: a duplicate only ever arrives as a
            // retransmission, separated from its original by at most a handful of later intents.
            private const int Capacity = 512;
            private readonly HashSet<Guid> _ids = new();
            private readonly Queue<Guid> _order = new();

            internal IEnumerable<Guid> Ids => _order;

            /// <summary>Records an id and evicts the oldest once the cap is exceeded, reporting each
            /// evicted id so the caller drops its reverse-index entry.</summary>
            internal void Add(Guid id, Action<Guid> onEvicted)
            {
                if (!_ids.Add(id))
                {
                    return;
                }

                _order.Enqueue(id);
                while (_order.Count > Capacity)
                {
                    var evicted = _order.Dequeue();
                    _ids.Remove(evicted);
                    onEvicted(evicted);
                }
            }
        }

        private void OnEnable()
        {
            if (_context != null)
                return;

            _context = RuntimeContext;
            _instance = this;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            _context.Messages.RegisterAttributedHandlers(this);
            _boxNetworkInteraction = new BoxNetworkInteraction(true, BroadcastWorld, SendWorldTo);
            _playerBoxInteraction = new PlayerBoxInteraction(_boxNetworkInteraction, BroadcastWorld);
            _shelfInteraction = new ShelfInteraction(BroadcastWorld, () => _context.InGame());
            _warehouseShelfInteraction = new WarehouseShelfInteraction(true, BroadcastWorld,
                SendWorldTo, _boxNetworkInteraction, _playerBoxInteraction);
            _cardInteraction = new WorldCardInteraction(_context, true, _boxNetworkInteraction,
                _playerBoxInteraction)
            {
                BroadcastOverride = BroadcastWorld,
                RelayOverride = RelayWorldExcept,
                SendOverride = SendWorldTo
            };
            _cardInteraction.Containers.BroadcastState = BroadcastWorld;
            _cardInteraction.Containers.SendToClient = SendWorldTo;
            _cardInteraction.Containers.InGameProvider = _context.InGame;
            _harmony = new Harmony("com.zwhit.cardshopcoop.world.host");
            InstallBoxNetworkInteractionPatches();
            _boxNetworkInteraction.RegisterHostSceneBoxes();
            InstallPlayerBoxInteractionPatches();
            InstallPlayerBoxPresenceHook();
            InstallPlayerShelfInteractionPatches();
            InstallWarehouseShelfInteractionPatches();
            InstallCardInteractionPatches();
            InstallCardDisplayPatches();
            InstallPlacement();
            InstallPlacementHold();
            InstallWorkbench();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;

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
            _context?.Messages.UnregisterAttributedHandlers(this);
            _nextBaselineId = 1;
            _furniturePurchaseSpawnDepth = 0;
            _pendingFurniturePurchaseBoxes.Clear();
            _transferManifests.Clear();
            _appliedPredictions.Clear();
            _predictionOwners.Clear();
            if (ReferenceEquals(_instance, this))
                _instance = null;
            _harmony?.UnpatchSelf();
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

        /// <summary>Host: the next synchronously-created box binds to this creator-assigned id.
        /// Used by an intent handler before it runs the game factory (empty-box take, furniture
        /// box-up, play-table box-up). Returns false when the id is not fresh (already bound to a
        /// live box or parked for another creation); the caller must reject the originating intent
        /// rather than let the host mint a new id, which would desync the creator's pre-bound box.
        /// An empty id is accepted: the host mints one.</summary>
        internal static bool PushCreatedBoxId(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.PushHostCreatedId(boxNetworkId) == true;

        /// <summary>Host: stage the creator-assigned ids for the delivery entry the purchase is
        /// about to enqueue. The game's enqueue hook commits them to that entry, so no id outlives
        /// the entry it belongs to. Returns false when any id is not fresh; the caller must reject
        /// the originating intent rather than remint, which would desync the creator's boxes.</summary>
        internal static bool StageCreatedBoxIds(System.Collections.Generic.IReadOnlyList<Guid> boxNetworkIds)
            => _instance?._boxNetworkInteraction?.StageHostDeliveryIds(boxNetworkIds) == true;

        /// <summary>Host: release a creator id parked for a creation that did not happen, so a
        /// later, unrelated box cannot bind to it.</summary>
        internal static void CancelCreatedBoxId(Guid boxNetworkId)
            => _instance?._boxNetworkInteraction?.CancelHostCreatedId(boxNetworkId);

        /// <summary>Host: drop ids staged for a delivery entry that was never enqueued, so a later
        /// enqueue cannot commit them to an unrelated order.</summary>
        internal static void CancelStagedDeliveryIds()
            => _instance?._boxNetworkInteraction?.CancelStagedHostDeliveryIds();

        /// <summary>Host: snapshot/restore the delivery-id mirror so a rolled-back game waiting
        /// list also restores the ids bound to its entries.</summary>
        internal static BoxNetworkInteraction.DeliveryQueueState CaptureDeliveryQueueState()
            => _instance?._boxNetworkInteraction?.CaptureDeliveryQueueState();

        internal static void RestoreDeliveryQueueState(
            BoxNetworkInteraction.DeliveryQueueState state)
            => _instance?._boxNetworkInteraction?.RestoreDeliveryQueueState(state);

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
            host._placementPopulation.CaptureTransferManifest(manifest);
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

        /// <summary>Apply one validated client intent on the host. Successful operations publish
        /// their authoritative state or the small gameplay handoff they require.</summary>
        internal bool ExecuteWorldCommand(MessageContext context, WorldMessage command,
            Func<bool> apply)
        {
            if (context?.Connection == null || !WorldMessageMetadata.IsValidIntent(command))
            {
                CoopPlugin.Log.LogWarning("Rejected world intent with an invalid identity: "
                    + DescribeIntent(command) + ".");
                if (_context != null && context?.Connection != null)
                {
                    PredictionHost.Reject(_context, context.ConnectionId,
                        command?.PredictionId ?? System.Guid.Empty);
                }

                return false;
            }

            var connectionId = context.ConnectionId;
            if (_predictionOwners.TryGetValue(command.PredictionId, out var owner))
            {
                if (owner != connectionId)
                {
                    CoopPlugin.Log.LogWarning("Rejected world intent naming another connection's "
                        + "prediction: " + DescribeIntent(command) + " owner=" + owner
                        + " sender=" + connectionId + ".");
                    PredictionHost.Reject(_context, connectionId, command.PredictionId);
                    return false;
                }

                // A duplicate of an intent this connection already got applied (a transport
                // resend). Applying it a second time is the double-apply this ledger exists to
                // stop, so drop it without replying: the first application stands.
                CoopPlugin.Log.LogInfo("Dropped duplicate world intent from conn " + connectionId
                    + ": " + DescribeIntent(command) + ".");
                return false;
            }

            _predictionOwners[command.PredictionId] = connectionId;
            if (!_appliedPredictions.TryGetValue(connectionId, out var applied))
            {
                applied = new AppliedIntentLedger();
                _appliedPredictions[connectionId] = applied;
            }

            applied.Add(command.PredictionId, evicted =>
            {
                if (_predictionOwners.TryGetValue(evicted, out var evictedOwner)
                    && evictedOwner == connectionId)
                {
                    _predictionOwners.Remove(evicted);
                }
            });

            return PredictionHost.Resolve(_context, connectionId,
                command.PredictionId, apply);
        }

        /// <summary>A peer left, so its applied-prediction ids can no longer be resent or named.
        /// Drop the reverse index entries it owned and its per-connection set.</summary>
        [OnClientDisconnected]
        private void ForgetAppliedPredictions(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null
                || !_appliedPredictions.TryGetValue(connection.Id, out var applied))
            {
                return;
            }

            foreach (var predictionId in applied.Ids)
            {
                if (_predictionOwners.TryGetValue(predictionId, out var owner)
                    && owner == connection.Id)
                {
                    _predictionOwners.Remove(predictionId);
                }
            }

            _appliedPredictions.Remove(connection.Id);
        }

        internal void RejectWorldIntent(MessageContext context, WorldMessage command)
        {
            if (_context == null || context?.Connection == null
                || command?.PredictionId == System.Guid.Empty)
            {
                return;
            }

            // Say which intent and why, so a recurring "before host execution" warning is
            // diagnosable instead of a dead end: the handlers reject when the world is not in
            // game, the sender has not reached FullyJoined yet, or the payload was null.
            CoopPlugin.Log.LogWarning("Rejected world intent before host execution: "
                + DescribeIntent(command) + " inGame=" + _context.InGame()
                + " connState=" + context.Connection.State + ".");
            PredictionHost.Reject(_context, context.ConnectionId, command.PredictionId);
        }

        private static string DescribeIntent(WorldMessage command)
            => command == null
                ? "<null>"
                : command.GetType().Name + " op=" + command.OperationKind
                    + " entity=" + (command.StableEntityId ?? "<none>");

        [OnFullyJoined]
        private void SendWarehouseBaseline(PeerConnection connection)
        {
            if (connection != null && _context.InGame())
                SendWorldBaseline(connection.Id);
        }

        /// <summary>A peer allowed to act on the shared world. Transferring counts: the guest's
        /// world is loaded from the transferred save (that is exactly when it starts playing) and
        /// only the baseline/ack round-trip is still in flight, so a guest that stocks a shelf in
        /// that window must not have the action refused and rolled back. The host applies against
        /// its own authoritative state either way, and reliable ordering plus the world client's
        /// deferral of unresolved deltas keeps the baseline/delta order coherent.</summary>
        internal bool IsJoinPhaseSender(MessageContext context)
            => context?.Connection != null
                && (context.Connection.State == ConnectionState.Transferring
                    || context.Connection.State == ConnectionState.FullyJoined);

        private void SendWorldBaseline(int connectionId)
        {
            if (connectionId <= 0)
                return;

            var baselineId = _nextBaselineId++;
            if (_nextBaselineId <= 0)
                _nextBaselineId = 1;

            SendWorldTo(connectionId, new WorldBaselineStartMessage
            {
                BaselineId = baselineId,
            });
            var manifest = TransferManifestFor(connectionId);
            _boxNetworkInteraction?.AppendBaselineMessages(manifest, message => SendWorldTo(
                connectionId, message));
            // Carried boxes are not part of the box descriptor: a box a peer is already holding
            // would otherwise freeze at the transfer-time pose on the joiner instead of riding the
            // holder's avatar. Announce each hold after the descriptors that created the boxes.
            AppendPlayerBoxHoldBaseline(message => SendWorldTo(connectionId, message));
            _warehouseShelfInteraction?.AppendBaselineMessages(message => SendWorldTo(connectionId,
                message));
            SendWorldTo(connectionId, new BoxBaselineCompleteMessage());
            _cardInteraction?.AppendBaselineMessages(message => SendWorldTo(connectionId,
                message));
            AppendWorkbenchBaseline(connectionId);
            AppendCardDisplayBaseline(message => SendWorldTo(connectionId, message));
            SendWorldTo(connectionId, new WorldBaselineCompleteMessage
            {
                BaselineId = baselineId,
            });
        }

        private void BroadcastWorld(INetMessage message)
        {
            if (message is not WorldMessage worldMessage)
                throw new InvalidOperationException("World host can only broadcast WorldMessage values.");

            PrepareState(worldMessage);
            _context.Broadcast(worldMessage);
        }

        private void SendWorldTo(int connectionId, INetMessage message)
        {
            if (message is not WorldMessage worldMessage)
                throw new InvalidOperationException("World host can only send WorldMessage values.");

            PrepareState(worldMessage);
            _context.Send(connectionId, worldMessage);
        }

        private void RelayWorldExcept(int excludedConnectionId, INetMessage message)
        {
            if (message is not WorldMessage worldMessage)
                throw new InvalidOperationException("World host can only relay WorldMessage values.");

            // Relay through the transport's broadcast admission so a peer that is still joining
            // is skipped rather than treated as a hard send failure.
            PrepareState(worldMessage);
            _context.Relay(excludedConnectionId, worldMessage);
        }

        private void PrepareState(WorldMessage message)
        {
            message.StableEntityId ??= WorldMessageMetadata.StableEntityId(message);
        }

    }
}
