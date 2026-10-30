using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Guest access-sign intents and authoritative state application.</summary>
    [ClientBehaviour]
    public sealed class StoreAccessClientBehaviour : CoopBehaviour
    {
        private const byte ShopSign = 0;
        private const byte WarehouseSign = 1;

        private static StoreAccessClientBehaviour _active;
        private bool _shutdown;
        private bool _initialized;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _active = this;
            StoreAccessInterop.Reset();
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded += OnSceneLoaded;
            try
            {
                Harmony.CreateClassProcessor(typeof(OpenSignPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(WarehouseSignPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(OpenSignReadyPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(WarehouseSignReadyPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(GuestDayStartedBlockPatch)).Patch();
            }
            catch
            {
                Shutdown();
                throw;
            }

            // A module re-enabled inside a live session can already have a stored value even when
            // no sign-ready hook fires again. The normal join path is the global-information
            // baseline itself; this only heals the re-enable case.
            ApplyStoredState();
            _initialized = true;
        }

        /// <summary>The host's authoritative access flags. The store owns the baseline and the
        /// wire; this handler only projects the value onto the local game state and signs. The
        /// value implements <see cref="IActionValue"/>, so a pending local toggle is reversed on a
        /// rejection before this authoritative re-anchor is applied.</summary>
        [GlobalInformationHandler]
        private void HandleState(InformationChange<StoreAccessState> change)
        {
            if (_shutdown)
            {
                return;
            }

            ApplyState(change.Current, change.Previous);
        }

        private static void ApplyState(StoreAccessState state, StoreAccessState previous)
        {
            if (state == null)
            {
                return;
            }

            var shopChanged = CPlayerData.m_IsShopOpen != state.IsShopOpen;
            var warehouseChanged = CPlayerData.m_IsWarehouseDoorClosed
                != state.IsWarehouseDoorClosed;
            CPlayerData.m_IsShopOpen = state.IsShopOpen;
            CPlayerData.m_IsWarehouseDoorClosed = state.IsWarehouseDoorClosed;

            // A first value is a baseline/reset: repaint without replaying an old swap. A
            // rejection re-anchor always repaints without animating; the optimistic swap is
            // already being undone by the ledger. A baseline or rejection must always repaint,
            // even if a stale swap flag survived a sign disable/re-enable.
            var animate = !state.Rejected && previous != null;
            var forceRefresh = previous == null || state.Rejected;

            // Repaint both surfaces on every authoritative state. The repaint is idempotent and
            // cheap, and it guarantees the faces cannot stay inverted after a first toggle, a
            // rejection where previous == state, or a stale local swap. A running local swap is
            // still allowed to finish (Apply*Sign skips it unless authority actually changed).
            ApplyShopSign(shopChanged, animate, forceRefresh);
            ApplyWarehouseSign(warehouseChanged, animate, forceRefresh);

            CoopPlugin.Log.LogInfo("[store-access] apply shop="
                + (state.IsShopOpen ? "open" : "closed") + " warehouse="
                + (state.IsWarehouseDoorClosed ? "closed" : "open")
                + " action=" + state.ActionId + (state.Rejected ? " (rejection)" : "") + ".");
        }

        private static void ApplyShopSign(bool changed, bool animate, bool forceRefresh)
        {
            var sign = StoreAccessInterop.FindOpenSign();
            if (sign == null)
            {
                return;
            }

            // A swap that this apply starts owns the visible face: its vanilla coroutine paints
            // the mesh at the animation's midpoint. Repainting here would cut that swap in half
            // and snap the sign around before the animation plays.
            if (changed && animate && StoreAccessInterop.PlayOpenAnimation(sign))
            {
                return;
            }

            // A swap already in progress is the local player's own animation of this same value
            // (their click toggled the flag before the host echo arrived). Leave it running. The
            // coroutine reads the authoritative flag that was just written, so the face still
            // lands on the right side. Outside a swap, repaint from the flag: authority must
            // never leave a stale face behind after a sign disable/re-enable.
            // A rejection re-anchor must repaint even when the pending undo already touched the
            // sign: the undo flips from the local value, while this repaint projects the host's
            // absolute value onto the face. This also heals a first toggle whose local swap was
            // interrupted or never started.
            if (forceRefresh || changed || !StoreAccessInterop.IsOpenSwapping(sign))
            {
                StoreAccessInterop.RefreshOpenMesh(sign);
            }
        }

        private static void ApplyWarehouseSign(bool changed, bool animate, bool forceRefresh)
        {
            var sign = StoreAccessInterop.FindWarehouseSign();
            if (sign == null)
            {
                // Some scenes surface the warehouse access through the room manager only.
                StoreAccessInterop.RefreshWarehouseAccess(StoreAccessInterop.FindUnlockRoomManager());
                return;
            }

            // Same rule as the shop sign: a swap this apply starts paints itself, and a local
            // swap of the same value must be allowed to finish.
            if (changed && animate && StoreAccessInterop.PlayWarehouseAnimation(sign))
            {
                return;
            }

            if (forceRefresh || changed || !StoreAccessInterop.IsWarehouseSwapping(sign))
            {
                StoreAccessInterop.RefreshWarehouseMesh(sign);
            }
        }

        /// <summary>Repaints the signs from the stored authoritative value without animating.
        /// Used when a sign becomes available after the value was already applied.</summary>
        private static void ApplyStoredState()
            => ApplyState(Global<StoreAccessState>.Value, null);

        /// <summary>A sign click is only meaningful once the host can accept its toggle and the
        /// authoritative access value is present. Before that the client would flip its local
        /// sign while the host stayed on the old side, and no later event would correct it.</summary>
        internal bool CanToggleNow()
        {
            if (_shutdown)
            {
                return true;
            }

            var host = Context?.Host;
            if (host != null && host.State != ConnectionState.FullyJoined)
            {
                CoopPlugin.Log.LogInfo("[store-access] sign click ignored: host connection is "
                    + host.State + ".");
                return false;
            }

            if (Global<StoreAccessState>.Value == null)
            {
                // The host seeds this value at session start and the join baseline carries it, so
                // this only happens during the join window. Log it: a silently dropped click is
                // exactly what made the missing tutorial baseline hard to diagnose.
                CoopPlugin.Log.LogWarning("[store-access] sign click ignored: the host's access "
                    + "state baseline has not arrived yet.");
                return false;
            }

            return true;
        }

        /// <summary>The vanilla sign click already toggled the state and started its animation;
        /// forward the intent so the host applies and echoes it. A toggle is its own inverse, so
        /// the same operation is both the ledger redo and undo: this composes correctly under a
        /// layered rejection and never restores a stale absolute value.</summary>
        private void ForwardToggle(byte which, bool before, bool after)
        {
            if (_shutdown || !RuntimeContext.InGame() || after == before)
            {
                return;
            }

            // No join callback: the guest's own connection to the host is the authoritative
            // "gameplay traffic is allowed" signal. Before the FullyJoined ack the sign cannot
            // drive a shared state change anyway.
            var host = Context?.Host;
            if (host == null || host.State != ConnectionState.FullyJoined)
            {
                return;
            }

            Context.Send(new StoreAccessToggleIntent
            {
                Which = which,
                Before = before,
            });
        }

        /// <summary>Flips one access flag and repaints its sign.</summary>
        internal static void ApplyLocal(byte which, bool value)
        {
            if (which == ShopSign)
            {
                CPlayerData.m_IsShopOpen = value;
                StoreAccessInterop.RefreshOpenMesh(StoreAccessInterop.FindOpenSign());
                return;
            }

            CPlayerData.m_IsWarehouseDoorClosed = value;
            var sign = StoreAccessInterop.FindWarehouseSign();
            if (sign != null)
            {
                StoreAccessInterop.RefreshWarehouseMesh(sign);
            }
            else
            {
                StoreAccessInterop.RefreshWarehouseAccess(
                    StoreAccessInterop.FindUnlockRoomManager());
            }
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _) => ApplyStoredState();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            StoreAccessInterop.Reset();
            ApplyStoredState();
        }

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

            StoreAccessInterop.Reset();
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnMouseButtonUp")]
        private static class OpenSignPatch
        {
            // Let vanilla run so the sign plays its own animation; forward the result afterwards.
            // Until the join state is ready, block the local flip instead of toggling a sign the
            // host will never see.
            [HarmonyPrefix]
            private static bool Prefix(out bool __state)
            {
                __state = CPlayerData.m_IsShopOpen;
                return _active == null || _active.CanToggleNow();
            }

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(ShopSign, __state, CPlayerData.m_IsShopOpen);
        }

        /// <summary>Guest: the shop sign's day-start reset is host-authoritative - the host
        /// publishes the closed state through the access-sign value - so the guest does not run
        /// the local sign/flag reset at the roll-over.</summary>
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
            private static bool Prefix(out bool __state)
            {
                __state = CPlayerData.m_IsWarehouseDoorClosed;
                return _active == null || _active.CanToggleNow();
            }

            [HarmonyPostfix]
            private static void Postfix(bool __state)
                => _active?.ForwardToggle(WarehouseSign, __state,
                    CPlayerData.m_IsWarehouseDoorClosed);
        }

        [HarmonyPatch(typeof(InteractableOpenCloseSign), "OnEnable")]
        private static class OpenSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => ApplyStoredState();
        }

        [HarmonyPatch(typeof(InteractableWarehouseAllowEnterSign), "OnEnable")]
        private static class WarehouseSignReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => ApplyStoredState();
        }
    }
}
