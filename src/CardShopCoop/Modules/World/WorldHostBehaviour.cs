using System;
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
        private int _furniturePurchaseSpawnDepth;
        private readonly System.Collections.Generic.Stack<InteractablePackagingBox_Shelf>
            _pendingFurniturePurchaseBoxes = new();

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
                SendWorldTo, _boxNetworkInteraction);
            _cardInteraction = new WorldCardInteraction(_context, true, _boxNetworkInteraction);
            _cardInteraction.BroadcastOverride = BroadcastWorld;
            _cardInteraction.RelayOverride = RelayWorldExcept;
            _cardInteraction.SendOverride = SendWorldTo;
            _cardInteraction.Containers.BroadcastState = BroadcastWorld;
            _cardInteraction.Containers.SendToClient = SendWorldTo;
            _cardInteraction.Containers.InGameProvider = _context.InGame;
            _harmony = new Harmony("com.zwhit.cardshopcoop.world.host");
            InstallBoxNetworkInteractionPatches();
            _boxNetworkInteraction.RegisterHostSceneBoxes();
            InstallPlayerBoxInteractionPatches();
            InstallPlayerShelfInteractionPatches();
            InstallWarehouseShelfInteractionPatches();
            InstallCardInteractionPatches();
            InstallCardDisplayPatches();
            InstallPlacement();
            InstallPlacementHold();
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            ShutdownPlacementHold();
            ShutdownPlacement();
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
        internal static bool TryGetHeldBoxId(InteractablePackagingBox box, out long boxNetworkId)
        {
            boxNetworkId = 0;
            return box != null && _instance?._boxNetworkInteraction != null
                && _instance._boxNetworkInteraction.TryGetId(box, out boxNetworkId);
        }

        /// <summary>Authoritative open/closed flag of a carried box, read directly from the field
        /// so the whole open-close animation window reports the true state.</summary>
        internal static bool IsHeldBoxOpen(InteractablePackagingBox box)
            => box is InteractablePackagingBox_Item item && BoxNetworkInteraction.IsBoxOpen(item);

        /// <summary>Apply one validated client intent on the host. Successful operations publish
        /// their authoritative state or the small gameplay handoff they require.</summary>
        internal bool ExecuteWorldCommand(MessageContext context, WorldMessage command,
            Func<bool> apply)
        {
            if (context?.Connection == null || !WorldMessageMetadata.IsValidIntent(command))
            {
                CoopPlugin.Log.LogWarning("Rejected world intent with an invalid identity.");
                if (_context != null && context?.Connection != null)
                {
                    PredictionHost.Reject(_context, context.ConnectionId,
                        command?.PredictionId ?? System.Guid.Empty);
                }

                return false;
            }

            return PredictionHost.Resolve(_context, context.ConnectionId,
                command.PredictionId, apply);
        }

        internal void RejectWorldIntent(MessageContext context, WorldMessage command)
        {
            if (_context == null || context?.Connection == null
                || command?.PredictionId == System.Guid.Empty)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("Rejected world intent before host execution.");
            PredictionHost.Reject(_context, context.ConnectionId, command.PredictionId);
        }

        [OnFullyJoined]
        private void SendWarehouseBaseline(PeerConnection connection)
        {
            if (connection != null && _context.InGame())
                SendWorldBaseline(connection.Id);
        }

        internal bool IsFullyJoinedSender(MessageContext context)
            => context?.Connection != null
                && context.Connection.State == ConnectionState.FullyJoined;

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
            _boxNetworkInteraction?.AppendBaselineMessages(message => SendWorldTo(connectionId,
                message));
            _warehouseShelfInteraction?.AppendBaselineMessages(message => SendWorldTo(connectionId,
                message));
            SendWorldTo(connectionId, new BoxBaselineCompleteMessage());
            _cardInteraction?.AppendBaselineMessages(message => SendWorldTo(connectionId,
                message));
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

            var ids = _context.ConnectionIds?.Invoke();
            for (var i = 0; ids != null && i < ids.Count; i++)
            {
                if (ids[i] != excludedConnectionId)
                    SendWorldTo(ids[i], worldMessage);
            }
        }

        private void PrepareState(WorldMessage message)
        {
            message.StableEntityId ??= WorldMessageMetadata.StableEntityId(message);
        }

    }
}
