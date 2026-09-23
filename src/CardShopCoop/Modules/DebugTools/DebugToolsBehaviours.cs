using CardShopCoop.Attributes;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.DebugTools
{
    /// <summary>Owns the reflected game cheat hooks for the process lifetime. Role and settings
    /// are evaluated dynamically by DebugToolsRuntime, including enabled solo mode.</summary>
    [PersistentBehaviour]
    public sealed class DebugToolsProcessBehaviour : DebugToolsBehaviour
    {
        private void OnEnable() => StartDebugTools("process");

        internal void Shutdown() => StopDebugTools();

        private void OnDestroy() => StopDebugTools();
    }

    public abstract class DebugToolsBehaviour : CoopBehaviour
    {
        private HarmonyLib.Harmony _harmony;
        private bool _shutdown;

        protected void StartDebugTools(string role)
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            DebugToolsRuntime.Attach(this);
            _harmony = new HarmonyLib.Harmony("com.zwhit.cardshopcoop.debug-tools." + role);
            DebugToolsPatches.Apply(_harmony);
            DebugToolsRuntime.BootstrapCheatManager();
        }

        protected void StopDebugTools()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            DebugToolsRuntime.Detach(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }
}
