using System;
using System.Reflection;
using CardShopCoop.Runtime;
using HarmonyLib;

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
                Swallow.Log(e);
                _pauseMenuOpen = false;
                SetOwner(ModalOwner.Pause, false);
            }
        }

        private static void ReconcileModalOwnership()
        {
            RefreshPauseMenuOpen();
            if (_windowModalRequested && !HasAnyOwner(VanillaModalOwners))
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

                if (_ownsWindowModal)
                {
                    // An unrelated game transition may have called ExitUIMode while the
                    // co-op window stayed visible. Reassert our owned mode before presenting
                    // the window; this path also remains safe after a rapid reopen.
                    if (!controller.IsInUIMode())
                    {
                        controller.EnterUIMode();
                    }
                }
                else
                {
                    // A different game screen may already own UI mode. It owns the cursor in
                    // that case; only claim the mode when it is genuinely available.
                    if (controller.IsInUIMode())
                    {
                        _windowModalController = null;
                        // Do not take ownership from that screen, but the co-op window is still
                        // visible and must remain usable while the existing modal is active.
                        controller.ShowCursor();
                        return;
                    }

                    controller.EnterUIMode();
                    _windowModalController = controller;
                    _ownsWindowModal = true;
                    SetOwner(ModalOwner.Window, true);
                    CoopPlugin.Log.LogInfo(
                        "Co-op window opened: entering game UI mode to block gameplay input");
                }

                // Reapply the presentation from the central owner on every requested update.
                // This is what keeps a visible IMGUI window usable after a vanilla modal closes,
                // without making PauseScreen or CheatManager release state they do not own.
                controller.ShowCursor();
            }
            catch (Exception e)
            {
                Swallow.Log(e);
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

                        controller.HideCursor();
                        CameraLerpField?.SetValue(controller, false);
                        UiModeField.SetValue(controller, false);
                        controller.ResetMousePress();
                    }
                    else
                    {
                        // Required builds have the field. Keep a loud fallback for a future
                        // game update rather than silently leaving the controller modal.
                        CoopPlugin.Log.LogWarning(
                            "SessionInput: InteractionPlayerController.m_IsInUIMode not found; "
                            + "using vanilla ExitUIMode");
                        controller.ExitUIMode();
                    }

                    CoopPlugin.Log.LogInfo("Co-op window closed: restoring gameplay input");
                }
            }
            catch (Exception e)
            {
                Swallow.Log(e);
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
                var walker = ipc != null ? ipc.m_WalkerCtrl : null;
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
                Swallow.Log(e);
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
                var walker = ipc != null ? ipc.m_WalkerCtrl : null;
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
                Swallow.Log(e);
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
                if (walker != null)
                {
                    walker.SetStopMovement(false);
                }
            }
            catch (Exception e)
            {
                Swallow.Log(e);
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
