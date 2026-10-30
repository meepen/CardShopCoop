using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.World;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Client session lifecycle, readiness and action plumbing. Snapshot rendering
    /// lives in <c>RegisterClientBehaviour.State.cs</c>, the carrier/phase projection in
    /// <c>.Mirror.cs</c>, rollback in <c>.Undo.cs</c>, observations in <c>.Observations.cs</c>,
    /// and the shared game hooks in <c>RegisterHooks.cs</c>.</summary>
    [ClientBehaviour]
    public sealed partial class RegisterClientBehaviour : CoopBehaviour
    {
        private static RegisterClientBehaviour _active;

        private readonly Dictionary<InteractableCashierCounter, LocalStation> _stations = new();
        private readonly HashSet<InteractableCashierCounter> _awaiting = new();
        // Ownership actions by id, and which of those are claims. The map lets a release be
        // observed only for a counter the store says is ours or for which our claim is in flight,
        // so a manning that was never claimed cannot send a release the host will reject.
        private readonly Dictionary<Guid, InteractableCashierCounter> _ownershipActions = new();
        private readonly HashSet<Guid> _claimActions = new();
        private readonly Dictionary<Guid, InteractableCashierCounter> _actionCounters = new();
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;
        private bool _rendering;
        private bool _rerun;

        internal static RegisterClientBehaviour Active => _active;

        /// <summary>True when a release for this counter is ours to send: the authoritative store
        /// already says this player owns it, or this player's claim is still in flight. A manning
        /// that never sent a claim must not produce a release the host would reject.</summary>
        internal bool ShouldObserveRelease(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return false;
            }

            if (StateOf(counter)?.State?.Owner?.IsSelf == true)
            {
                return true;
            }

            foreach (var pair in _ownershipActions)
            {
                if (pair.Value == counter && _claimActions.Contains(pair.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private void TrackOwnership(Guid id, InteractableCashierCounter counter, bool claim)
        {
            if (id == Guid.Empty || counter == null)
            {
                return;
            }

            _ownershipActions[id] = counter;
            if (claim)
            {
                _claimActions.Add(id);
            }
        }

        /// <summary>True when <paramref name="actionId"/> is this client's in-flight action for
        /// the counter. This replaces the old per-counter ledger query: the intent that was sent
        /// owns the counter until its own answer arrives.</summary>
        internal bool IsPendingActionFor(InteractableCashierCounter counter, Guid actionId)
            => actionId != Guid.Empty && _actionCounters.TryGetValue(actionId, out var pending)
                && ReferenceEquals(pending, counter);

        private bool HasPendingAction(InteractableCashierCounter counter)
        {
            foreach (var pending in _actionCounters.Values)
            {
                if (ReferenceEquals(pending, counter))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Settles one register action after its authoritative state (or its rejection)
        /// has been applied exactly once. Queues the counter so the store's latest snapshot is the
        /// restore script even when the host refused before resolving the counter.</summary>
        internal void Settle(RegisterIntent message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            var id = message.ActionId;
            _ownershipActions.Remove(id);
            _claimActions.Remove(id);
            _actionCounters.Remove(id);
            if (message.Counter != null)
            {
                _awaiting.Add(message.Counter);
                OnReadinessSignal();
            }
        }

        /// <summary>True while this client may turn a game action into an action. The shared
        /// reentrancy guard owns the render gate and the action layer, so the module keeps no
        /// suppression state of its own.</summary>
        internal bool CanObserve => !_shutdown && _joined && RuntimeContext.InGame()
            && !ReentrancyGuard.Suppressed(this);

        /// <summary>True while the local peer is in a running game (joined or not). A global game
        /// guard uses this rather than <see cref="CanObserve"/> because it must also hold while a
        /// replay is driving the game.</summary>
        internal bool InGame => !_shutdown && RuntimeContext.InGame();

        /// <summary>Set while the client is inside EvaluateCreditCard, so the nested OnPressSpaceBar
        /// records one card-payment action rather than a second completion.</summary>
        internal bool EvaluatingCreditCard
        {
            get; set;
        }

        internal static bool IsCarrier(Customer customer)
        {
            if (customer == null || _active == null)
            {
                return false;
            }

            foreach (var station in _active._stations.Values)
            {
                if (ReferenceEquals(station.Carrier, customer))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ForceExitManned()
        {
            var active = _active;
            if (active == null)
            {
                return;
            }

            var counters = RegisterInterop.Counters;
            for (var i = 0; counters != null && i < counters.Count; i++)
            {
                var counter = counters[i];
                if (counter != null && counter.IsMannedByPlayer())
                {
                    // Let the game perform the exit; the ExitPatch observes it and registers the
                    // single release action through the normal path.
                    counter.OnPressEsc();
                }
            }
        }

        private void OnEnable()
        {
            if (_initialized)
            {
                return;
            }

            _active = this;
            RegisterHooks.PatchClient(Harmony);
            SceneManager.sceneLoaded += OnSceneLoaded;
            NpcClientBehaviour.CustomerManagerReady += OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged += OnReadinessSignal;
            // Registers are addressed by their stable placement key; the key only resolves once
            // the placement channel has bound this peer's counter identities, so retry the pending
            // register states whenever that structure changes.
            PlacementApi.StructureChanged += OnPlacementStructureChanged;
            OnReadinessSignal();
            _initialized = true;
        }

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
            OnReadinessSignal();
        }

        private static bool IsSceneReady()
            => RegisterInterop.Counters != null
                && SceneRef<CustomerManager>.Get()?.GetCustomerList() != null
                && PlacementApi.IsPlacementIdentityReady;

        private void OnPlacementStructureChanged(int _) => OnReadinessSignal();

        /// <summary>The extended-information store is the deferred queue: a value that arrives
        /// before the counter/customer is renderable stays in the store, and this sweep retries only
        /// the counters that still need it. A nested signal (a carrier hand-back), queues one more
        /// pass so the outer sweep is never re-entered.</summary>
        private void OnReadinessSignal()
        {
            if (_shutdown)
            {
                return;
            }

            // Defer only while this behaviour is itself rendering. A handler dispatch must not
            // defer: the snapshot arrives through a handler and is rendered from here.
            if (_rendering || Writes.Active)
            {
                _rerun = true;
                return;
            }

            if (!RuntimeContext.InGame())
            {
                return;
            }

            _rendering = true;
            try
            {
                // A station whose counter was destroyed or unbound must release its borrowed body
                // even when no register message announced it (identity unbind is silent).
                PruneStations();

                if (_awaiting.Count == 0 || !IsSceneReady())
                {
                    return;
                }

                do
                {
                    _rerun = false;
                    var counters = new List<InteractableCashierCounter>(_awaiting);
                    for (var i = 0; i < counters.Count; i++)
                    {
                        var counter = counters[i];
                        if (counter == null || TryRender(counter))
                        {
                            _awaiting.Remove(counter);
                        }
                    }
                }
                while (_rerun);
            }
            finally
            {
                _rendering = false;
            }
        }

        private void PruneStations()
        {
            if (_stations.Count == 0)
            {
                return;
            }

            var counters = new List<InteractableCashierCounter>(_stations.Keys);
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                if (counter == null || !GameObjectIdentityApi.TryGetId(counter, out _))
                {
                    ReleaseStation(counter);
                }
            }
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (_shutdown)
            {
                return;
            }

            foreach (var counter in new List<InteractableCashierCounter>(_stations.Keys))
            {
                ReleaseStation(counter);
            }

            _stations.Clear();
            _awaiting.Clear();
            _ownershipActions.Clear();
            _claimActions.Clear();
            _actionCounters.Clear();
            OnReadinessSignal();
        }

        private void ReleaseStation(InteractableCashierCounter counter)
        {
            if (ReferenceEquals(counter, null)
                || !_stations.TryGetValue(counter, out var station))
            {
                return;
            }

            if (!ReferenceEquals(station.Carrier, null))
            {
                // The real NPC rendered itself the whole borrow; deactivate it wholesale and let
                // DetachExistingCustomer reveal the walking puppet for the hand-back. A destroyed
                // carrier still needs the detach so its pool slot is un-suppressed.
                if (station.Carrier != null)
                {
                    if (station.Carrier.m_CustomerCash != null)
                    {
                        station.Carrier.m_CustomerCash.gameObject.SetActive(false);
                    }

                    station.Carrier.gameObject.SetActive(false);
                }

                RegisterInterop.ReleaseContents(station.Carrier);
                NpcClientBehaviour.DetachExistingCustomer(station.CustomerIndex, station.Carrier);
            }

            if (counter != null)
            {
                RegisterInterop.ResetCheckoutVisuals(counter);
            }

            _stations.Remove(counter);
        }

        private void ShutdownContents()
        {
            var counters = RegisterInterop.Counters;
            for (var i = 0; counters != null && i < counters.Count; i++)
            {
                var counter = counters[i];
                if (counter != null && counter.IsMannedByPlayer())
                {
                    // _shutdown already suppresses observation, so this unmans without applying.
                    counter.OnPressEsc();
                }
            }

            foreach (var counter in new List<InteractableCashierCounter>(_stations.Keys))
            {
                ReleaseStation(counter);
            }

            _stations.Clear();
            _awaiting.Clear();
            _ownershipActions.Clear();
            _claimActions.Clear();
            _actionCounters.Clear();
            _joined = false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ShutdownContents();
            NpcClientBehaviour.CustomerManagerReady -= OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged -= OnReadinessSignal;
            PlacementApi.StructureChanged -= OnPlacementStructureChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();
    }
}
