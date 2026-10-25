using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Register
{
    /// <summary>
    /// Host session lifecycle and patch registration. State capture lives in
    /// <c>RegisterHostBehaviour.State.cs</c>, the validated command lane in
    /// <c>RegisterHostBehaviour.Intents.cs</c>, host policy in
    /// <c>RegisterHostBehaviour.Validation.cs</c>, and the shared game hooks in
    /// <c>RegisterHooks.cs</c>.
    /// </summary>
    [ServerBehaviour]
    public sealed partial class RegisterHostBehaviour : CoopBehaviour
    {
        private static RegisterHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        internal static RegisterHostBehaviour Active => _active;

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _harmony = new Harmony("dev.meepen.cardshopcoop.register.host");
            RegisterHooks.PatchHost(_harmony);
        }

        // Fires after every behaviour's OnEnable, so the identity module has bound the existing
        // counters by the time the host seeds their snapshots. Seeding in OnEnable could run before
        // that and drop every value for lacking an identity, with no later retry.
        [OnSessionStarted]
        private void SeedCounters() => PublishAllCounters();

        // A hook-time seed can still run before the identity module has bound an existing counter
        // (discovery order is not a contract). Re-publishing when each peer finishes joining gives
        // the store a retry after that binding and sends the state to that peer as a live set even
        // if the extended-information baseline already went out.
        [OnFullyJoined]
        private void SeedForJoin(PeerConnection _) => PublishAllCounters();

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
            _harmony = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
