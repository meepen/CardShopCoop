using CardShopCoop.Attributes;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.SessionInput
{
    /// <summary>Installs input and pause hooks for the process lifetime. The hook bodies are
    /// role-aware, so solo modal behavior remains vanilla while co-op sessions share the world.</summary>
    [PersistentBehaviour]
    public sealed class SessionInputProcessBehaviour : SessionInputBehaviour
    {
        private void OnEnable() => StartSessionInput("process");

        [OnSessionStopped]
        private void OnSessionStopped()
        {
            DebugTools.DebugToolsRuntime.CloseForSessionStop();
            SessionInputRuntime.SessionStopped();
        }

        internal void Shutdown() => StopSessionInput();

        private void OnDestroy() => StopSessionInput();
    }

    public abstract class SessionInputBehaviour : CoopBehaviour
    {
        private bool _shutdown;
        private bool _initialized;

        protected void StartSessionInput(string role)
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            SessionInputRuntime.Attach(this);
            SessionInputPatches.Apply(Harmony);
            _initialized = true;
        }

        protected void StopSessionInput()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            SessionInputRuntime.Detach(this);
        }
    }
}
