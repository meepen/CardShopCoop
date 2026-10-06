using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Guest access-sign intents and authoritative mesh/state application.</summary>
    [ClientBehaviour]
    public sealed class StoreAccessClientBehaviour : CoopBehaviour
    {
        private static StoreAccessClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private StoreAccessStateMessage _pendingState;
        // The latest authoritative delta whose values are already applied but whose visible sign
        // could not be painted yet (the world/sign was still loading). See HandleDelta.
        private StoreAccessDeltaMessage _pendingDelta;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            var lifecycle = false;
            try
            {
                ResetSessionState();
                StoreAccessInterop.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("com.zwhit.cardshopcoop.store-access.client");
                _harmony.CreateClassProcessor(typeof(OpenSignPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(OpenSignReadyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(WarehouseSignReadyPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(GuestDayStartedBlockPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Store access client initialization failed: " + exception);
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
                ResetSessionState();
                StoreAccessInterop.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(StoreAccessStateMessage))]
        private void HandleState(MessageContext context, StoreAccessStateMessage message)
        {
            if (_shutdown)
                return;

            // The join baseline is superseded by any delta that already arrived.
            _pendingState = message;
            _pendingDelta = null;
            TryApplyPending();
        }

        [MessageHandler(typeof(StoreAccessDeltaMessage))]
        private void HandleDelta(MessageContext context, StoreAccessDeltaMessage message)
        {
            if (_shutdown)
            {
                return;
            }

            // ApplyAuthoritative: the host's absolute value always lands, and it is reconciled in
            // layers so a newer local toggle still awaiting its own decision is undone and
            // replayed instead of being stranded under this older state (a pending action owns
            // the entity until it resolves). An empty or already-retired id just applies.
            PredictionApi.ApplyAuthoritative(message.PredictionId,
                () => ApplyDeltaAuthoritative(message));
        }

        /// <summary>Applies one authoritative delta: flags always move, visuals/pending handling
        /// follow the same rules as before. Runs as the authoritative apply inside
        /// <see cref="PredictionApi.ApplyAuthoritative(Guid, Action)"/>, so newer same-sign
        /// predictions have already been undone and will be replayed on top of this state.</summary>
        private void ApplyDeltaAuthoritative(StoreAccessDeltaMessage message)
        {
            var shopChanged = message.HasShopOpen && CPlayerData.m_IsShopOpen != message.IsShopOpen;
            var warehouseChanged = message.HasWarehouseDoorClosed
                && CPlayerData.m_IsWarehouseDoorClosed != message.IsWarehouseDoorClosed;
            if (message.HasShopOpen)
                CPlayerData.m_IsShopOpen = message.IsShopOpen;
            if (message.HasWarehouseDoorClosed)
                CPlayerData.m_IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;

            // The join baseline may still be waiting for the scene. A delta only carries one
            // sign, so fold it into that pending state rather than dropping either update.
            if (_pendingState != null)
            {
                if (message.HasShopOpen)
                    _pendingState.IsShopOpen = message.IsShopOpen;
                if (message.HasWarehouseDoorClosed)
                    _pendingState.IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;
                _pendingState.Animate = false;
                return;
            }

            if (_context != null && _context.InGame() && DeltaSignsReady(message))
            {
                ApplyDeltaVisuals(message, shopChanged, warehouseChanged);
                _pendingDelta = null;
                LogDelta("applied", message);
            }
            else
            {
                // The values are authoritative now, but the visible sign cannot be painted yet
                // (the world/sign is still loading). Hold the delta so the ready hooks repaint
                // it, instead of moving the flag and leaving the visible sign behind.
                _pendingDelta = message;
                LogDelta("held until the sign is ready", message);
            }
        }

        private static void LogDelta(string action, StoreAccessDeltaMessage message)
            => CoopPlugin.Log.LogInfo("[store-access] delta " + action + " shop="
                + (message.HasShopOpen ? (message.IsShopOpen ? "open" : "closed") : "-")
                + " warehouse=" + (message.HasWarehouseDoorClosed
                    ? (message.IsWarehouseDoorClosed ? "closed" : "open") : "-") + ".");

        /// <summary>True when every surface a delta carries can be resolved right now. The
        /// warehouse access can live on the sign or, when the build/scene has no sign object, on
        /// the unlock-room manager, exactly like the baseline apply path handles it.</summary>
        private static bool DeltaSignsReady(StoreAccessDeltaMessage message)
            => (!message.HasShopOpen || StoreAccessInterop.FindOpenSign() != null)
                && (!message.HasWarehouseDoorClosed
                    || StoreAccessInterop.FindWarehouseSign() != null
                    || StoreAccessInterop.FindUnlockRoomManager() != null);

        private void TryApplyPending()
        {
            if (_shutdown || _context == null || !_context.InGame())
                return;

            if (_pendingState != null && StoreAccessInterop.IsSceneReady())
            {
                Apply(_pendingState);
                _pendingState = null;
                _pendingDelta = null;
            }

            if (_pendingDelta != null && DeltaSignsReady(_pendingDelta))
            {
                // Refresh only: the values were applied when the delta arrived, and animating a
                // change that happened while the world was loading would replay it at the wrong
                // moment.
                if (_pendingDelta.HasShopOpen)
                    StoreAccessInterop.RefreshOpenMesh(StoreAccessInterop.FindOpenSign());
                if (_pendingDelta.HasWarehouseDoorClosed)
                {
                    var warehouseSign = StoreAccessInterop.FindWarehouseSign();
                    if (warehouseSign != null)
                        StoreAccessInterop.RefreshWarehouseMesh(warehouseSign);
                    else
                        StoreAccessInterop.RefreshWarehouseAccess(
                            SceneRef<UnlockRoomManager>.Get());
                }

                LogDelta("painted onto the ready sign", _pendingDelta);
                _pendingDelta = null;
            }
        }

        private static void Apply(StoreAccessStateMessage message)
        {
            CPlayerData.m_IsShopOpen = message.IsShopOpen;
            CPlayerData.m_IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;
            var openSign = StoreAccessInterop.FindOpenSign();
            if (!message.Animate || !StoreAccessInterop.PlayOpenAnimation(openSign))
                StoreAccessInterop.RefreshOpenMesh(openSign);

            var warehouseSign = StoreAccessInterop.FindWarehouseSign();
            if (warehouseSign != null && (!message.Animate
                || !StoreAccessInterop.PlayWarehouseAnimation(warehouseSign)))
                StoreAccessInterop.RefreshWarehouseMesh(warehouseSign);
            else if (warehouseSign == null)
                StoreAccessInterop.RefreshWarehouseAccess(SceneRef<UnlockRoomManager>.Get());
        }

        private static void ApplyDeltaVisuals(StoreAccessDeltaMessage message, bool shopChanged,
            bool warehouseChanged)
        {
            if (message.HasShopOpen)
            {
                var openSign = StoreAccessInterop.FindOpenSign();
                if (!(shopChanged && message.Animate
                    && StoreAccessInterop.PlayOpenAnimation(openSign)))
                {
                    // Repaint when the value moved (animation refused or not requested), and also
                    // when the local value already matched: the visible sign may have missed an
                    // earlier authoritative change, and a matching flag must not hide that. A
                    // swap in progress is the local player's own animation of the same value, so
                    // leave it alone.
                    if (shopChanged || !StoreAccessInterop.IsOpenSwapping(openSign))
                        StoreAccessInterop.RefreshOpenMesh(openSign);
                }
            }

            if (message.HasWarehouseDoorClosed)
            {
                var warehouseSign = StoreAccessInterop.FindWarehouseSign();
                if (warehouseSign == null)
                {
                    // Some scenes surface the warehouse access through the room manager only,
                    // exactly like the baseline apply path.
                    StoreAccessInterop.RefreshWarehouseAccess(SceneRef<UnlockRoomManager>.Get());
                }
                else if (!(warehouseChanged && message.Animate
                    && StoreAccessInterop.PlayWarehouseAnimation(warehouseSign)))
                {
                    if (warehouseChanged
                        || !StoreAccessInterop.IsWarehouseSwapping(warehouseSign))
                        StoreAccessInterop.RefreshWarehouseMesh(warehouseSign);
                }
            }
        }

        /// <summary>The vanilla sign click already toggled the state and started its animation;
        /// forward the intent so the host applies and echoes it. This is a post-hoc prediction
        /// whose redo and undo are BOTH the toggle itself: a toggle is its own inverse, so the
        /// layered rejection in PredictionApi composes correctly even when a rollback arrives
        /// without an authoritative refresh. Captured absolute before/after values are not
        /// inverses under that layering - with two toggles in flight, an older rejection replays
        /// the newer prediction from authority and the newer rejection's absolute undo would
        /// restore the older prediction's optimistic value. A prediction owns exactly ONE sign's
        /// key, so an undo can never touch the other sign's in-flight toggle.</summary>
        private void ForwardToggle(byte which, bool before, bool after)
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame() || after == before)
                return;

            // One prediction key per sign so an undo of one sign's prediction does not touch the
            // other sign's in-flight toggle.
            var predictionKey = which == 0 ? "store-access:shop" : "store-access:warehouse";
            PredictionApi.Predict(
                predictionKey,
                predictionId => _context.Send(1, new StoreAccessToggleMessage
                {
                    PredictionId = predictionId,
                    Which = which,
                }),
                () => ToggleLocal(which),
                () => ToggleLocal(which));
        }

        /// <summary>Flips one sign's live flag and repaints it. Both directions of a toggle
        /// prediction use this: the host's intent is a toggle too, so this is the exact redo and
        /// the exact inverse.</summary>
        private static void ToggleLocal(byte which)
        {
            if (which == 0)
            {
                ApplyLocal(0, !CPlayerData.m_IsShopOpen);
                return;
            }

            ApplyLocal(1, !CPlayerData.m_IsWarehouseDoorClosed);
        }

        private static void ApplyLocal(byte which, bool value)
        {
            if (which == 0)
            {
                CPlayerData.m_IsShopOpen = value;
                StoreAccessInterop.RefreshOpenMesh(StoreAccessInterop.FindOpenSign());
                return;
            }

            CPlayerData.m_IsWarehouseDoorClosed = value;
            StoreAccessInterop.RefreshWarehouseMesh(StoreAccessInterop.FindWarehouseSign());
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => TryApplyPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            StoreAccessInterop.Reset();
            TryApplyPending();
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
                ResetSessionState();
        }

        private void ResetSessionState()
        {
            _joined = false;
            _pendingState = null;
            _pendingDelta = null;
        }

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
            ResetSessionState();
            StoreAccessInterop.Reset();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnMouseButtonUp")]
        private static class OpenSignPatch
        {
            // Let vanilla run so the sign plays its own animation; forward the result afterwards.
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
                => __state = CPlayerData.m_IsShopOpen;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(0, __state, CPlayerData.m_IsShopOpen);
        }

        /// <summary>Guest: the shop sign's day-start reset is host-authoritative - the host's
        /// StoreAccess delta carries the closed state and this client applies it - so the guest
        /// does not run the local sign/flag reset at the roll-over.</summary>
        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnDayStarted")]
        private static class GuestDayStartedBlockPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp")]
        private static class WarehouseSignPatch
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state)
                => __state = CPlayerData.m_IsWarehouseDoorClosed;

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(1, __state, CPlayerData.m_IsWarehouseDoorClosed);
        }

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnEnable")]
        private static class OpenSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnEnable")]
        private static class WarehouseSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyPending();
        }
    }
}
