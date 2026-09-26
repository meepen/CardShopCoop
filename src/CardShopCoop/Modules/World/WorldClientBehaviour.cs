using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.World
{
    /// <summary>Client-side receiver for shared world state.</summary>
    [ClientBehaviour]
    public sealed partial class WorldClientBehaviour : CoopBehaviour
    {
        private static WorldClientBehaviour _instance;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private BoxNetworkInteraction _boxNetworkInteraction;
        private PlayerBoxInteraction _playerBoxInteraction;
        private ShelfInteraction _shelfInteraction;
        private WarehouseShelfInteraction _warehouseShelfInteraction;
        private WorldCardInteraction _cardInteraction;

        private void OnEnable()
        {
            if (_context != null)
                return;

            _context = RuntimeContext;
            _instance = this;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            _context.Messages.RegisterAttributedHandlers(this);
            _boxNetworkInteraction = new BoxNetworkInteraction(false, _ => { }, SendWorldCommand);
            _playerBoxInteraction = new PlayerBoxInteraction(_boxNetworkInteraction,
                SendWorldCommand);
            _shelfInteraction = new ShelfInteraction(SendWorldCommand);
            _warehouseShelfInteraction = new WarehouseShelfInteraction(false, _ => { },
                SendWorldCommand, _boxNetworkInteraction, _playerBoxInteraction);
            _cardInteraction = new WorldCardInteraction(_context, false, _boxNetworkInteraction,
                _playerBoxInteraction)
            {
                SendOverride = SendWorldCommand
            };
            _cardInteraction.Containers.SendOp = message => SendWorldCommand(1, message);
            _cardInteraction.Containers.InGameProvider = _context.InGame;
            _cardInteraction.Containers.ReloadingProvider = _context.PreloadHold;
            _harmony = new Harmony("com.zwhit.cardshopcoop.world.client");
            InstallBoxNetworkInteractionPatches();
            InstallPlayerBoxInteractionPatches();
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
            ShutdownPlacementHold();
            ShutdownPlacement();
            ShutdownCardDisplay();
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
            _context.Messages.UnregisterAttributedHandlers(this);
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
            _warehouseShelfInteraction?.FlushClientState();
            FlushPlayerShelfState();
        }

        [OnFullyJoined]
        private void SendWorldReady(PeerConnection connection)
        {
            if (_context.InGame())
            {
                _cardInteraction?.FlushWorldReady();
                _warehouseShelfInteraction?.FlushClientState();
            }
        }

        internal static WorldCardInteraction ActiveCards => _instance?._cardInteraction;

        private void SendWorldCommand(int connectionId, INetMessage message)
        {
            if (message is not WorldMessage worldMessage)
                throw new System.InvalidOperationException("World client can only send WorldMessage values.");
            WorldMessageMetadata.PrepareIntent(worldMessage);
            _context.Send(connectionId, worldMessage);
        }

        internal static bool SendClientIntent(WorldMessage command)
            => SendClientIntent(command, command?.PredictionId ?? System.Guid.Empty);

        internal static bool SendClientIntent(WorldMessage command, System.Guid predictionId)
        {
            if (_instance == null || command == null)
                return false;

            command.PredictionId = predictionId;
            WorldMessageMetadata.PrepareIntent(command);
            _instance._context.Send(1, command);
            return true;
        }

        internal static void BeginWorldBaseline(long baselineId)
        {
            if (_instance == null)
                return;

            _instance._playerBoxInteraction?.Reset();
            _instance._shelfInteraction?.Reset();
            _instance._warehouseShelfInteraction?.Reset();
            _instance._workbenchInteraction?.Reset();
            _instance._boxNetworkInteraction?.ClientBeginBaseline();
            _instance.ResetCardDisplayState();
        }

        internal static void CompleteWorldBaseline(long baselineId)
        {
            if (_instance == null)
                return;

            _instance._boxNetworkInteraction?.ClientCompleteBaseline();
        }

        internal static bool IsKnownPackagingBox(InteractablePackagingBox box)
            => _instance?._boxNetworkInteraction?.IsKnownBox(box) == true;

        /// <summary>Client: a worker is carrying this world box. Retire the real object from its
        /// warehouse slot; the Npc worker prop draws the carried box.</summary>
        internal static void ApplyWorkerHeldBox(long boxNetworkId)
            => _instance?._warehouseShelfInteraction?.ApplyWorkerHeldBox(boxNetworkId);

        /// <summary>Client: the worker put this box down; restore the real object to a live state.</summary>
        internal static void RestoreWorkerDroppedBox(long boxNetworkId)
            => _instance?._warehouseShelfInteraction?.RestoreWorkerDroppedBox(boxNetworkId);

        internal static void ResetCardState()
        {
            _instance?._cardInteraction?.Reset();
        }
    }
}
