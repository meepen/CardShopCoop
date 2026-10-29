using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
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
        private Harmony _harmony;
        private bool _shutdown;
        private BoxNetworkInteraction _boxNetworkInteraction;
        private PlayerBoxInteraction _playerBoxInteraction;
        private ShelfInteraction _shelfInteraction;
        private WarehouseShelfInteraction _warehouseShelfInteraction;
        private WorldCardInteraction _cardInteraction;

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _instance = this;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            RuntimeContext.Messages.RegisterAttributedHandlers(this);
            _boxNetworkInteraction = new BoxNetworkInteraction(false, _ => { }, SendWorldCommand);
            _playerBoxInteraction = new PlayerBoxInteraction(_boxNetworkInteraction,
                SendWorldCommand);
            _shelfInteraction = new ShelfInteraction(SendWorldCommand);
            _warehouseShelfInteraction = new WarehouseShelfInteraction(false, _ => { },
                SendWorldCommand, _boxNetworkInteraction, _playerBoxInteraction,
                () => RuntimeContext.InGame());
            _cardInteraction = new WorldCardInteraction(RuntimeContext, false, _boxNetworkInteraction,
                _playerBoxInteraction)
            {
                SendOverride = SendWorldCommand
            };
            // Guards for in-flight graded binder removals live as long as their prediction; the
            // tracker's retirement event is the one deterministic cleanup point.
            ActionLedger.ActionSettled += WorldCardInteraction.OnActionSettled;
            _cardInteraction.Containers.SendOp = message => SendWorldCommand(1, message);
            _cardInteraction.Containers.InGameProvider = RuntimeContext.InGame;
            _cardInteraction.Containers.ReloadingProvider = RuntimeContext.PreloadHold;
            // The join container baseline can name a placement the guest rebuilds only after it,
            // so the container module holds those records until the placement layer reports the
            // object. This is the same late-join signal PlayTable, Register and CardDisplay use.
            PlacementApi.StructureChanged += OnPlacementStructureChanged;
            _harmony = new Harmony("dev.meepen.cardshopcoop.world.client");
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
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            PlacementApi.StructureChanged -= OnPlacementStructureChanged;
            UninstallPlayerBoxPresenceHook();
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
            ActionLedger.ActionSettled -= WorldCardInteraction.OnActionSettled;
            _cardInteraction?.Shutdown();
            _cardInteraction = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }

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

        /// <summary>The placement layer rebuilt or re-posed an object. Records the container
        /// module deferred because their target did not exist yet can now be applied, and warehouse
        /// deltas whose shelf arrived late are retried.</summary>
        private void OnPlacementStructureChanged(int _)
        {
            _cardInteraction?.Containers.FlushClientState();
            _warehouseShelfInteraction?.FlushClientState();
        }

        [OnFullyJoined]
        private void SendWorldReady(PeerConnection connection)
        {
            if (RuntimeContext.InGame())
            {
                _cardInteraction?.FlushWorldReady();
                _warehouseShelfInteraction?.FlushClientState();
                RequestPlacementBaseline(connection);
            }
        }

        internal static WorldCardInteraction ActiveCards => _instance?._cardInteraction;

        private void SendWorldCommand(int connectionId, INetMessage message)
        {
            if (message is not WorldMessage worldMessage)
            {
                throw new InvalidOperationException("World client can only send WorldMessage values.");
            }

            // Direct sends from the interaction classes are fire-and-forget intents (a shelf
            // removal, a covered-hold drop, the workbench bundle, a legacy card forward). They
            // still need a real prediction id: the host's intent ledger rejects an empty one.
            // None of them has the id pending on any client, so an echo is applied exactly as a
            // remote change, never consumed as a prediction.
            if (worldMessage.ActionId == Guid.Empty)
            {
                worldMessage.ActionId = Guid.NewGuid();
            }

            RuntimeContext.Send(connectionId, worldMessage);
        }

        // A fire-and-forget forward (a covered-hold drop, an item-box state mirror) still needs a
        // real prediction id: the host's intent ledger requires one and treats an empty id as an
        // unauthenticated, dedupe-unscoped message. It is not a prediction here - no client has it
        // pending - so every valid peer simply applies the echo.
        internal static bool SendClientIntent(WorldMessage command)
            => SendClientIntent(command,
                command == null || command.ActionId == Guid.Empty
                    ? Guid.NewGuid() : command.ActionId);

        internal static bool SendClientIntent(WorldMessage command, Guid actionId)
        {
            if (_instance == null || command == null)
            {
                return false;
            }

            command.ActionId = actionId;
            _instance.RuntimeContext.Send(1, command);
            return true;
        }

        internal static void BeginWorldBaseline(long baselineId)
        {
            if (_instance == null)
            {
                return;
            }

            _instance._playerBoxInteraction?.Reset();
            _instance._shelfInteraction?.Reset();
            _instance._warehouseShelfInteraction?.Reset();
            _instance._workbenchInteraction?.Reset();
            _instance.ResetCardDisplayState();
            // The new baseline re-sends every container it knows, so any record that never found its
            // target under the old baseline must not linger and later bind to a reused index.
            _instance._cardInteraction?.Containers.ClearPendingRecords();
        }

        internal static bool IsKnownPackagingBox(InteractablePackagingBox box)
            => _instance?._boxNetworkInteraction?.IsKnownBox(box) == true;

        /// <summary>Client: a worker is carrying this world box. Retire the real object from its
        /// warehouse slot; the Npc worker prop draws the carried box.</summary>
        internal static void ApplyWorkerHeldBox(Guid boxNetworkId)
            => _instance?._warehouseShelfInteraction?.ApplyWorkerHeldBox(boxNetworkId);

        /// <summary>Client: the worker put this box down; restore the real object to a live state.</summary>
        internal static void RestoreWorkerDroppedBox(Guid boxNetworkId)
            => _instance?._warehouseShelfInteraction?.RestoreWorkerDroppedBox(boxNetworkId);

        internal static void ResetCardState()
        {
            _instance?._cardInteraction?.Reset();
        }
    }
}
