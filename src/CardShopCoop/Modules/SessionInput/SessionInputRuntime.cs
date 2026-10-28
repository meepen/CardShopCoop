using System;
using System.Reflection;
using CardShopCoop.Runtime;
using HarmonyLib;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.SessionInput
{
    /// <summary>
    /// Owns the process-wide modal state during a co-op session. The game has several modal
    /// implementations, each of which snapshots/restores part of the cursor and camera state;
    /// keeping their ownership here prevents one implementation from restoring over another.
    /// </summary>
    internal static class SessionInputRuntime
    {
        [Flags]
        private enum ModalOwner
        {
            None = 0,
            Window = 1,
            Pause = 2,
            Cheat = 4
        }

        private const ModalOwner VanillaModalOwners = ModalOwner.Pause | ModalOwner.Cheat;

        private static SessionInputBehaviour _active;
        private static bool _pauseMenuOpen;
        private static PauseScreen _pauseScreen;
        private static bool _cheatMenuOpen;
        private static ModalOwner _modalOwners;
        private static bool _windowModalRequested;
        private static bool _ownsWindowModal;
        private static InteractionPlayerController _windowModalController;

        private static readonly FieldInfo WalkerStopField =
            AccessTools.Field(typeof(CMF.AdvancedWalkerController), "isStopMovement");

        // Both supported game builds implement ExitUIMode by clearing m_IsInUIMode from a
        // 50ms coroutine. That is appropriate for vanilla transitions, but it leaves a stale
        // coroutine behind when this window is closed and reopened quickly. The field is private
        // in both builds, so the co-op-owned transition can be made synchronous without a direct
        // reference to build-specific game types.
        private static readonly FieldInfo UiModeField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsInUIMode");

        private static readonly FieldInfo CameraLerpField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsLerpingCameraRot");
        private static readonly FieldInfo PhoneModeField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");
        private static readonly FieldInfo DecoModeField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsDecoScreenMode");

        private static bool _ownsWalkerStop;
        private static CMF.AdvancedWalkerController _latchedWalker;
        private static bool _walkerFieldWarningLogged;

        internal static void Attach(SessionInputBehaviour behaviour)
        {
            if (_active != null && !ReferenceEquals(_active, behaviour))
            {
                throw new InvalidOperationException("SessionInput is already active for another session.");
            }

            ReleaseWindowModal();
            ReleaseWalkerOnShutdown();
            _active = behaviour;
            _pauseMenuOpen = false;
            _pauseScreen = null;
            _cheatMenuOpen = false;
            _modalOwners = ModalOwner.None;
            _windowModalRequested = false;
            _ownsWindowModal = false;
            _windowModalController = null;
            _ownsWalkerStop = false;
            _latchedWalker = null;
            _walkerFieldWarningLogged = false;
        }

        internal static void Detach(SessionInputBehaviour behaviour)
        {
            if (!ReferenceEquals(_active, behaviour))
            {
                return;
            }

            // Deactivation happens before CoopCore resets Role. If a session ends with a modal
            // canvas still visible, restore vanilla's paused state after our co-op freeze is
            // removed rather than leaving a menu that permits movement.
            var modalOpen = PauseMenuOpen() || _cheatMenuOpen;
            ReleaseWindowModal();
            ReleaseWalkerOnShutdown();
            UnityEngine.Time.timeScale = modalOpen ? 0f : 1f;

            _active = null;
            _pauseMenuOpen = false;
            _pauseScreen = null;
            _cheatMenuOpen = false;
            _modalOwners = ModalOwner.None;
            _windowModalRequested = false;
            _ownsWindowModal = false;
            _windowModalController = null;
            _ownsWalkerStop = false;
            _latchedWalker = null;
            _walkerFieldWarningLogged = false;
        }

        /// <summary>Diagnostic: logs the vanilla pause gate when the pause key is pressed, so a
        /// report of "Escape does nothing" names the exact flag that is blocking it.</summary>
        internal static void LogPauseGate(InteractionPlayerController controller)
        {
            if (controller == null)
            {
                CoopPlugin.Log.LogInfo("[pause] pause key pressed with no InteractionPlayerController.");
                return;
            }

            var inUi = UiModeField != null && UiModeField.GetValue(controller) is bool u && u;
            var phone = PhoneModeField != null && PhoneModeField.GetValue(controller) is bool p && p;
            var deco = DecoModeField != null && DecoModeField.GetValue(controller) is bool d && d;
            var pause = SceneRef<PauseScreen>.Get();
            var pauseOpen = pause != null && pause.m_ScreenGrp != null && pause.m_ScreenGrp.activeSelf;
            CoopPlugin.Log.LogInfo("[pause] pause key pressed: inUI=" + inUi + " phone=" + phone
                + " deco=" + deco + " pauseOpen=" + pauseOpen + " windowBlocks=" + WindowBlocksInput
                + " cheatFreeze=" + CheatMenuFreezeActive() + " role=" + CoopCore.Role + ".");
        }

        internal static bool PauseMenuOpen()
        {
            if (!_pauseMenuOpen)
            {
                return false;
            }

            // A scene teardown can destroy the menu without calling CloseScreen. Unity's fake
            // null then invalidates the cache and removes the input block on the next frame.
            if (_pauseScreen == null || _pauseScreen.m_ScreenGrp == null
                || !_pauseScreen.m_ScreenGrp.activeSelf)
            {
                _pauseMenuOpen = false;
                SetOwner(ModalOwner.Pause, false);
                return false;
            }

            return true;
        }

        /// <summary>True while the visible co-op window owns the input surface.</summary>
        internal static bool WindowBlocksInput => _active != null && _windowModalRequested;

        /// <summary>
        /// Requests modal ownership for the co-op window. The reconciler is the only code that
        /// claims or releases the controller UI mode, and it never releases a vanilla owner.
        /// </summary>
        internal static void SetWindowModal(bool open)
        {
            if (_active == null)
            {
                return;
            }

            _windowModalRequested = open;
            ReconcileModalOwnership();
        }

        internal static bool CheatMenuFreezeActive()
        {
            return _active != null && HasOwner(ModalOwner.Cheat) && CoopCore.Role != CoopRole.None;
        }

        /// <summary>
        /// Runs immediately before a vanilla modal snapshots cursor/camera state. Releasing only
        /// our own window state makes the snapshot represent gameplay, not the co-op overlay.
        /// </summary>
        internal static void PrepareVanillaModalOpen()
        {
            if (_active == null)
            {
                return;
            }

            ReleaseWindowModal();
        }

        /// <summary>Runs before PauseScreen decides whether its button is opening or closing.</summary>
        internal static void PauseOpening()
        {
            if (_active == null)
            {
                return;
            }

            RefreshPauseMenuOpen();
            if (!_pauseMenuOpen)
            {
                PrepareVanillaModalOpen();
            }
        }

        internal static void PauseChanged()
        {
            RefreshPauseMenuOpen();
            if (CoopCore.Role == CoopRole.None)
            {
                RestoreLocalWalkerIfIdle();
                return;
            }

            UnityEngine.Time.timeScale = 1f;
            ReconcileModalOwnership();
        }

        internal static void PauseClosed()
        {
            RefreshPauseMenuOpen();
            if (CoopCore.Role != CoopRole.None)
            {
                ReconcileModalOwnership();
            }
            else
            {
                RestoreLocalWalkerIfIdle();
            }
        }

        /// <summary>Called by DebugTools after CheatManager.SetMenuOpen actually transitions.</summary>
        internal static void CheatMenuChanged(bool open)
        {
            if (_active == null)
            {
                return;
            }

            _cheatMenuOpen = open;
            SetOwner(ModalOwner.Cheat, open);
            if (CoopCore.Role == CoopRole.None)
            {
                RestoreLocalWalkerIfIdle();
                return;
            }

            UnityEngine.Time.timeScale = 1f;
            ReconcileModalOwnership();
        }

        internal static void SessionStopped()
        {
            if (_active == null)
            {
                return;
            }

            var modalOpen = PauseMenuOpen() || _cheatMenuOpen;
            _cheatMenuOpen = false;
            SetOwner(ModalOwner.Cheat, false);
            _windowModalRequested = false;
            ReleaseWindowModal();
            ReleaseWalkerOnShutdown();
            UnityEngine.Time.timeScale = modalOpen ? 0f : 1f;
        }

        private static void RefreshPauseMenuOpen()
        {
            try
            {
                _pauseScreen = SceneRef<PauseScreen>.Get();
                _pauseMenuOpen = _pauseScreen != null && _pauseScreen.m_ScreenGrp != null
                    && _pauseScreen.m_ScreenGrp.activeSelf;
                SetOwner(ModalOwner.Pause, _pauseMenuOpen);
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
                _pauseMenuOpen = false;
                SetOwner(ModalOwner.Pause, false);
            }
        }

        /// <summary>The game's own "a shop level is loaded" flag (<c>CGameManager.m_IsGameLevel</c>).
        /// The module must not touch the controller's UI mode, cursor or walker while it is false -
        /// boot, a scene change, or a guest's world reload. Those calls read the game's CSingleton
        /// UI objects (the CenterDot) while the real ones do not exist yet, which both throws on a
        /// latched/destroyed instance and can fabricate a persistent bare singleton that shadows
        /// the real one for the rest of the run (the same hazard SceneRef exists to avoid).</summary>
        private static bool GameLevelReady()
        {
            var manager = SceneRef<CGameManager>.Get();
            return manager != null && manager.m_IsGameLevel;
        }

        private static void ReconcileModalOwnership()
        {
            RefreshPauseMenuOpen();
            if (!GameLevelReady())
            {
                // No shop level yet: leave the controller untouched. The next reconcile after the
                // level loads claims or releases the modal; the stale owner/controller from the
                // previous scene is dropped there by the controller-identity check.
                return;
            }

            // A vanilla modal (pause, cheat menu) owns the cursor and UI mode while it is open.
            // When the co-op window closes underneath it, releasing our owned modal would hide
            // the cursor and leave the still-visible pause menu without mouse focus. Keep our
            // claim alive until the vanilla owner closes, then the next reconcile releases it.
            if (HasAnyOwner(VanillaModalOwners))
            {
                if (_windowModalRequested)
                {
                    AcquireWindowModal();
                }
            }
            else if (_windowModalRequested)
            {
                AcquireWindowModal();
            }
            else
            {
                ReleaseWindowModal();
            }

            if (WindowBlocksInput || HasAnyOwner(VanillaModalOwners))
            {
                StopLocalWalkerForModal();
            }
            else
            {
                RestoreLocalWalkerIfIdle();
            }
        }

        private static bool HasOwner(ModalOwner owner)
        {
            return (_modalOwners & owner) != ModalOwner.None;
        }

        private static bool HasAnyOwner(ModalOwner owners)
        {
            return (_modalOwners & owners) != ModalOwner.None;
        }

        private static void SetOwner(ModalOwner owner, bool owned)
        {
            if (owned)
            {
                _modalOwners |= owner;
            }
            else
            {
                _modalOwners &= ~owner;
            }
        }

        private static void AcquireWindowModal()
        {
            try
            {
                var controller = SceneRef<InteractionPlayerController>.Get();
                if (controller == null)
                {
                    ReleaseOwnedWindowModal();
                    return;
                }

                if (_ownsWindowModal && !ReferenceEquals(_windowModalController, controller))
                {
                    ReleaseOwnedWindowModal();
                }

                if (!_ownsWindowModal)
                {
                    _windowModalController = controller;
                    _ownsWindowModal = true;
                    SetOwner(ModalOwner.Window, true);
                    CoopPlugin.Log.LogInfo("Co-op window opened: blocking gameplay input "
                        + "(the cursor is left exactly as the game set it up)");
                }

                // Deliberately no cursor/UI-mode calls at all. The window opens over the title
                // screen or an already-open pause menu, so the cursor and its lock state are
                // already correct. Gameplay input is blocked by the camera-input prefix and the
                // walker stop, which never touch the cursor.
                controller.ResetMousePress();
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
        }

        private static void ReleaseOwnedWindowModal()
        {
            if (!_ownsWindowModal)
            {
                SetOwner(ModalOwner.Window, false);
                _windowModalController = null;
                return;
            }

            var controller = _windowModalController;
            try
            {
                if (controller != null)
                {
                    if (UiModeField != null)
                    {
                        // Match ExitUIMode's camera/input cleanup, but do not schedule its
                        // delayed coroutine. A subsequent open can therefore enter UI mode
                        // immediately and cannot be undone by a stale 50ms callback.
                        var isCameraLerping = CameraLerpField != null
                            && CameraLerpField.GetValue(controller) is bool lerping && lerping;
                        if (isCameraLerping)
                        {
                            var angles = controller.m_CameraController.transform.localRotation.eulerAngles;
                            var x = angles.x > 180f ? angles.x - 360f : angles.x;
                            var y = angles.y > 180f ? angles.y - 360f : angles.y;
                            controller.m_CameraController.SetRotationAngles(x, y);
                        }

                        // Deliberately no cursor/UI-mode calls: closing the window must not
                        // capture or hide the cursor, and it never changed UI mode either.
                        CameraLerpField?.SetValue(controller, false);
                        controller.ResetMousePress();
                    }

                    CoopPlugin.Log.LogInfo("Co-op window closed: restoring gameplay input");
                }
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
            finally
            {
                _ownsWindowModal = false;
                _windowModalController = null;
                SetOwner(ModalOwner.Window, false);
            }
        }

        private static void StopLocalWalkerForModal()
        {
            try
            {
                var ipc = SceneRef<InteractionPlayerController>.Get();
                var walker = ipc?.m_WalkerCtrl;
                if (walker == null)
                {
                    ReleaseOwnedWalker();
                    return;
                }

                if (_latchedWalker != walker)
                {
                    // A previous scene's walker cannot be allowed to release a new scene's
                    // movement state.
                    ReleaseOwnedWalker();
                }

                if (WalkerStopField == null)
                {
                    if (!_walkerFieldWarningLogged)
                    {
                        _walkerFieldWarningLogged = true;
                        CoopPlugin.Log.LogWarning(
                            "SessionInput: AdvancedWalkerController.isStopMovement not found; movement lock left alone");
                    }
                    return;
                }

                var alreadyStopped = WalkerStopField.GetValue(walker) is bool stopped && stopped;
                if (alreadyStopped)
                {
                    return;
                }

                walker.SetStopMovement(true);
                _ownsWalkerStop = true;
                _latchedWalker = walker;
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
        }

        private static void RestoreLocalWalkerIfIdle()
        {
            if (!_ownsWalkerStop)
            {
                return;
            }

            try
            {
                var ipc = SceneRef<InteractionPlayerController>.Get();
                var walker = ipc?.m_WalkerCtrl;
                if (walker == null || _latchedWalker != walker)
                {
                    ReleaseOwnedWalker();
                    return;
                }

                if (WindowBlocksInput || PauseMenuOpen() || CheatMenuFreezeActive())
                {
                    return;
                }

                walker.SetStopMovement(false);
                _ownsWalkerStop = false;
                _latchedWalker = null;
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
        }

        private static void ReleaseWalkerOnShutdown()
        {
            if (!_ownsWalkerStop)
            {
                return;
            }

            ReleaseOwnedWalker();
        }

        private static void ReleaseOwnedWalker()
        {
            if (!_ownsWalkerStop)
            {
                _latchedWalker = null;
                return;
            }

            var walker = _latchedWalker;
            try
            {
                walker?.SetStopMovement(false);
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
            finally
            {
                _ownsWalkerStop = false;
                _latchedWalker = null;
            }
        }

        private static void ReleaseWindowModal()
        {
            ReleaseOwnedWindowModal();
        }

    }
}
