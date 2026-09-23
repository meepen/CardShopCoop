using CardShopCoop.Attributes;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>Owns save protections for the whole plugin process. The patch reads the current
    /// role and borrowed-world latch at invocation time, so disconnects cannot create a save gap.</summary>
    [PersistentBehaviour]
    public sealed class SaveTransferProcessBehaviour : SaveTransferBehaviour
    {
        private void OnEnable() => StartSaveTransfer("process");

        private void Update()
        {
            // A disconnect can leave the guest walking in the borrowed scene while Core has
            // already returned to Role.None. Never clear this latch until the title screen is
            // reached; this is the process-lifetime half of the save guard.
            SaveTransferRuntime.ClearBorrowedWorldIfSafe(RuntimeContext != null && RuntimeContext.InGame());
        }

        internal void Shutdown() => StopSaveTransfer();

        private void OnDestroy() => StopSaveTransfer();
    }

    public abstract class SaveTransferBehaviour : CoopBehaviour
    {
        private static SaveTransferBehaviour _active;
        private HarmonyLib.Harmony _harmony;
        private bool _shutdown;

        protected void StartSaveTransfer(string role)
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            if (_active != null && !ReferenceEquals(_active, this))
            {
                throw new System.InvalidOperationException(
                    "SaveTransfer is already active for another session.");
            }

            _active = this;
            _harmony = new HarmonyLib.Harmony("com.zwhit.cardshopcoop.save-transfer." + role);
            SaveTransferPatches.Apply(_harmony);
        }

        protected void StopSaveTransfer()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }
    }
}
