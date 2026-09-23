using System;
using HarmonyLib;

namespace CardShopCoop.Modules.SessionInput
{
    internal static class SessionInputPatches
    {
        internal static void Apply(Harmony harmony)
        {
            // CMF reads its own raw mouse/gamepad axes, independently of the game's camera
            // controller. The session window and the game's modal canvases must stop that path.
            TryPatch(harmony, typeof(CMF.CameraMouseInput), "GetHorizontalCameraInput",
                prefix: new HarmonyMethod(typeof(SessionInputPatches), nameof(CameraInputPrefix)));
            TryPatch(harmony, typeof(CMF.CameraMouseInput), "GetVerticalCameraInput",
                prefix: new HarmonyMethod(typeof(SessionInputPatches), nameof(CameraInputPrefix)));

            // MouseCursorLock runs its own raw-input loop and can relock the cursor after
            // InteractionPlayerController.ShowCursor(). Guard only that exact loop while the
            // co-op window owns the visible input surface.
            TryPatch(harmony, typeof(CMF.MouseCursorLock), "Update",
                prefix: new HarmonyMethod(typeof(SessionInputPatches), nameof(MouseCursorLockPrefix)));

            TryPatch(harmony, typeof(PauseScreen), "OpenScreen",
                prefix: new HarmonyMethod(typeof(SessionInputPatches), nameof(PauseOpenPrefix)),
                postfix: new HarmonyMethod(typeof(SessionInputPatches), nameof(PauseOpenPostfix)));
            TryPatch(harmony, typeof(PauseScreen), "CloseScreen",
                postfix: new HarmonyMethod(typeof(SessionInputPatches), nameof(PauseClosePostfix)));
        }

        internal static bool CameraInputPrefix(ref float __result)
        {
            if (!SessionInputRuntime.WindowBlocksInput && !SessionInputRuntime.PauseMenuOpen()
                && !SessionInputRuntime.CheatMenuFreezeActive())
            {
                return true;
            }

            __result = 0f;
            return false;
        }

        internal static bool MouseCursorLockPrefix()
        {
            return !SessionInputRuntime.WindowBlocksInput
                && !SessionInputRuntime.PauseMenuOpen()
                && !SessionInputRuntime.CheatMenuFreezeActive();
        }

        internal static void PauseOpenPostfix()
        {
            SessionInputRuntime.PauseChanged();
        }

        internal static void PauseOpenPrefix()
        {
            SessionInputRuntime.PauseOpening();
        }

        internal static void PauseClosePostfix()
        {
            SessionInputRuntime.PauseClosed();
        }

        private static void TryPatch(Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("SessionInput patch target missing: " + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("SessionInput patch failed for " + type.Name + "." + method
                    + ": " + e.Message);
            }
        }
    }
}
