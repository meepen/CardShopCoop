using System;
using HarmonyLib;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Hooks the game's loading-screen lifecycle so CoopCore can keep the game running while its
    /// window is unfocused during a scene or save load.
    ///
    /// The game opens its <c>LoadingScreen</c> from <c>CGameManager.LoadLobbySceneAsync</c> /
    /// <c>DelayLoadLobbyScene</c> two seconds BEFORE it starts loading the scene, and the load
    /// itself is driven by Unity's async scene machinery. The game never lowers
    /// <c>Application.runInBackground</c> at runtime (<c>CGameManager.changeFramerate</c> is dead
    /// code that is never started), so it stays at Unity's Player Settings default; an unfocused
    /// window can therefore pause the player and stall that load. There is no event for "loading
    /// started" - only <c>CEventPlayer_FinishHideLoadingScreen</c> for the end - so the start is
    /// taken from the game's own <c>LoadingScreen.OpenScreen</c> entry point, which is also what
    /// makes the load window cover the whole wait rather than just the scene swap.
    ///
    /// The patch body reads <c>CoopCore</c>, so it is installed from <c>CoopCore.Awake</c> -
    /// never before CoopCore has loaded. That keeps a core type-load failure from leaving a live
    /// patch that would throw at first JIT inside a game method (the same ordering rule the
    /// CoopPlugin comment documents for every other patch).
    /// </summary>
    internal static class GameLoadingGuard
    {
        internal static void Install()
        {
            var harmony = new Harmony("dev.meepen.cardshopcoop.game-loading");
            var original = AccessTools.Method(typeof(LoadingScreen), "OpenScreen");
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("[loading] probe target missing: LoadingScreen.OpenScreen");
                return;
            }

            try
            {
                harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(GameLoadingGuard), nameof(ReportLoadingScreenOpen)));
            }
            catch (Exception error)
            {
                // Optional guard: losing it only means an unfocused load can still pause. It must
                // never abort CoopCore.Awake and disable co-op for the run.
                CoopPlugin.Log.LogWarning("[loading] patch failed for LoadingScreen.OpenScreen: "
                    + error.Message);
            }

            // The character preload holds a shop-scene AsyncOperation at Unity's 0.9 boundary.
            // Its additive activation must not be mistaken for the player entering a shop:
            // CGameManager would create default save data, set m_IsGameLevel, and mark
            // m_InitLoaded so the next Continue skips its save. The preloader only suppresses the
            // exact activation it owns; every ordinary Single load is untouched.
            var levelFinished = AccessTools.Method(typeof(CGameManager), "OnLevelFinishedLoading");
            if (levelFinished == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[loading] probe target missing: CGameManager.OnLevelFinishedLoading");
            }
            else
            {
                try
                {
                    harmony.Patch(levelFinished,
                        prefix: new HarmonyMethod(typeof(UI.CoopCharacterPrefabPreloader),
                            nameof(UI.CoopCharacterPrefabPreloader.SuppressPreloadLevelLoad)));
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogWarning(
                        "[loading] preload level-guard patch failed: " + error.Message);
                }
            }
        }

        private static void ReportLoadingScreenOpen()
        {
            CoopCore.NotifyGameLoadingStarted();
            // The player has committed to a load. A preload held at 0.9 blocks every later scene
            // load, so it must be released before the game calls LoadSceneAsync two seconds later.
            UI.CoopCharacterPrefabPreloader.OnGameLoadCommitted();
        }
    }
}
