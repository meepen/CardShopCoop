using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Register;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>Lifecycle guard attached to scene-owned NPC roots. Unity invokes OnEnable
    /// synchronously after every later SetActive(true), including activations performed by
    /// vanilla loading coroutines.</summary>
    internal sealed class NpcNativeActivationGuard : MonoBehaviour
    {
        private void OnEnable()
            => NpcClientBehaviour.SuppressNativeActivation(gameObject);
    }

    /// <summary>Unity-owned client puppet updates, suppression hooks, and NPC message handlers.</summary>
    [ClientBehaviour]
    public sealed class NpcClientBehaviour : CoopBehaviour
    {
        private static NpcClientBehaviour _active;
        internal static event Action<int, int, Customer> ExistingCustomerPuppetReady;
        internal static event Action<int> CustomerCapacityChanged;
        internal static event Action<int, int, Customer> ExistingCustomerChanged;
        // The customer "something changed, re-check" signals are coalesced (see CoalescedEvent):
        // they are raised from inside game calls that a subscriber's own apply drives
        // (ActivateCustomer, attach/detach), so a plain event let that subscriber re-enter itself
        // through this class and recurse.
        private static readonly CoalescedEvent _customerManagerReady = new();
        private static readonly CoalescedEvent _customerPoolChanged = new();
        private static readonly CoalescedEvent _workerManagerReady = new();
        // The per-slot signals name an index a subscriber acts on, so they cannot be folded into one
        // delivery. They queue here and are delivered from the frame loop instead (see
        // FlushPendingCustomerSignals): a subscriber that drives a call back into this class can only
        // queue more, never re-enter.
        private static readonly HashSet<int> _pendingCapacityChanged = new();
        private static readonly Dictionary<int, ExistingCustomerChange> _pendingExistingCustomerChanged = new();
        private bool _shutdown;
        private bool _initialized;

        private readonly struct ExistingCustomerChange
        {
            internal readonly int Generation;
            internal readonly Customer Customer;

            internal ExistingCustomerChange(int generation, Customer customer)
            {
                Generation = generation;
                Customer = customer;
            }
        }

        internal static event Action CustomerManagerReady
        {
            add => _customerManagerReady.Handlers += value;
            remove => _customerManagerReady.Handlers -= value;
        }

        internal static event Action WorkerManagerReady
        {
            add => _workerManagerReady.Handlers += value;
            remove => _workerManagerReady.Handlers -= value;
        }

        internal static event Action CustomerPoolChanged
        {
            add => _customerPoolChanged.Handlers += value;
            remove => _customerPoolChanged.Handlers -= value;
        }

        public int PuppetCount => _puppets.Count;

        private void OnEnable()
        {
            if (_initialized)
            {
                return;
            }
            _active = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Harmony.CreateClassProcessor(typeof(CustomerManagerPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CustomerPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CustomerStartPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CustomerActivationPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CustomerDeactivationPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(WorkerManagerPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(CustomerManagerStartPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(WorkerManagerStartPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(WorkerActivationPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(WorkerUpdatePatch)).Patch();
            SuppressInitialPopulation(SceneRef<CustomerManager>.Get());
            SignalCustomerManagerReady();
            SignalWorkerManagerReady();
            _initialized = true;
        }

        private void Update()
        {
            using (Util.PerfProbe.Sample("module.npc.client-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            var inGame = RuntimeContext.InGame();
            var delta = Time.deltaTime;
            TickPuppets(delta, inGame);
            FlushPendingCustomerSignals();
        }

        /// <summary>Delivers the per-slot customer signals queued since the last frame. They are
        /// delivered here rather than from the raise site so a subscriber that synchronously drives
        /// a call back into this class can only queue more - an <see cref="Update"/> drain can never
        /// be re-entered the way the raise path could. One frame's delay is invisible for a
        /// "this slot changed, re-check" retry.</summary>
        private static void FlushPendingCustomerSignals()
        {
            if (_pendingCapacityChanged.Count > 0)
            {
                var indexes = new List<int>(_pendingCapacityChanged);
                _pendingCapacityChanged.Clear();
                for (var i = 0; i < indexes.Count; i++)
                {
                    CustomerCapacityChanged?.Invoke(indexes[i]);
                }
            }

            if (_pendingExistingCustomerChanged.Count > 0)
            {
                var changes = new List<KeyValuePair<int, ExistingCustomerChange>>(
                    _pendingExistingCustomerChanged);
                _pendingExistingCustomerChanged.Clear();
                for (var i = 0; i < changes.Count; i++)
                {
                    ExistingCustomerChanged?.Invoke(changes[i].Key, changes[i].Value.Generation,
                        changes[i].Value.Customer);
                }
            }
        }

        private void SuppressInitialPopulation(CustomerManager manager)
        {
            var cm = manager ?? SceneRef<CustomerManager>.Get();
            var customers = cm?.GetCustomerList();
            if (customers != null)
            {
                for (var i = 0; i < customers.Count; i++)
                {
                    var customer = customers[i];
                    TrackNativeRoot(customer?.gameObject);
                    if (customer != null && !RegisterClientBehaviour.IsCarrier(customer)
                        && !IsExistingCustomer(i, customer) && customer.gameObject.activeSelf)
                    {
                        customer.gameObject.SetActive(false);
                    }
                }
            }
            var workers = NpcInterop.Workers;
            if (workers == null)
            {
                return;
            }
            for (var i = 0; i < workers.Count; i++)
            {
                TrackNativeRoot(workers[i]?.gameObject);
                if (workers[i] != null && workers[i].gameObject.activeSelf)
                {
                    workers[i].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>The host connection dropped: deactivate every customer mirror and drop the
        /// puppet props immediately so the scene is not left with its simulated population while
        /// the session teardown unwinds.</summary>
        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                Reset();
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Reset();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }

        private void OnDestroy() => Shutdown();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (!_shutdown && ReferenceEquals(_active, this))
            {
                Reset();
            }
        }

        [MessageHandler]
        private void HandleState(MessageContext context, NpcStateDeltaMessage message)
        {
            if (!TryToSnapshot(message, out var state))
            {
                return;
            }

            if (!RuntimeContext.InGame() || !IsSceneReady())
            {
                BufferPendingState(state);
                return;
            }

            ApplyNpcState(state);
        }

        [MessageHandler]
        private void HandleSpeech(MessageContext context, NpcSpeechMessage message)
        {
            if (message == null || message.Customer == null)
            {
                CoopPlugin.Log.LogWarning("Npc client: dropped speech bubble with no live customer");
                return;
            }

            ShowSpeech(message, RuntimeContext.InGame());
        }

        [MessageHandler]
        private void HandleMoney(MessageContext context, NpcMoneyPopupMessage message)
        {
            if (message == null || message.Customer == null)
            {
                CoopPlugin.Log.LogWarning("Npc client: dropped money popup with no live customer");
                return;
            }

            ShowMoneyPopup(message, RuntimeContext.InGame());
        }

        /// <summary>Resolves a transient delta's live NPC into the internal snapshot. The wire
        /// carries only the object; kind, pool index and incarnation come from the identity
        /// module's existing helpers. A delta whose object no longer exists is dropped rather
        /// than guessed at from stale identity fields.</summary>
        private static bool TryToSnapshot(NpcStateDeltaMessage message, out NpcSnapshot snapshot)
        {
            snapshot = default;
            if (message == null)
            {
                CoopPlugin.Log.LogWarning("Npc client: dropped state delta with no message body");
                return false;
            }

            if (message.Customer != null)
            {
                if (!TryGetWireIdentity(message.Customer, out var index, out var identity))
                {
                    CoopPlugin.Log.LogWarning(
                        "Npc client: dropped customer state delta for an unbound customer");
                    return false;
                }

                snapshot = ToSnapshot(message, KindCustomer, index, identity);
                return true;
            }

            if (message.Worker != null)
            {
                if (!TryGetWireIdentity(message.Worker, out var index, out var identity))
                {
                    CoopPlugin.Log.LogWarning(
                        "Npc client: dropped worker state delta for an unbound worker");
                    return false;
                }

                if (message.HoldBox != null
                    && !WorldClientBehaviour.TryGetBoxId(message.HoldBox, out _))
                {
                    CoopPlugin.Log.LogWarning(
                        "Npc client: dropped worker state delta for an unbound held box");
                    return false;
                }

                snapshot = ToSnapshot(message, KindWorker, index, identity);
                return true;
            }

            CoopPlugin.Log.LogWarning("Npc client: dropped state delta with no live NPC object");
            return false;
        }

        private static NpcSnapshot ToSnapshot(NpcStateDeltaMessage message, byte kind, int index,
            int identity)
            => new()
            {
                Kind = kind,
                Index = (ushort)index,
                Identity = identity,
                Position = message.Position,
                Yaw = message.Yaw,
                Speed = message.Speed,
                Flags = message.Flags,
                ActionSequence = message.ActionSequence,
                ActionKind = message.ActionKind,
                HoldBig = message.HoldBig,
                HoldItemType = message.HoldItemType,
                HoldBox = message.HoldBox,
                HoldBoxNetworkId = HoldBoxId(message.HoldBox),
                HoldBoxOpened = message.HoldBoxOpened,
                HostTime = message.HostTime,
            };

        /// <summary>The live held box's bound stable identity, for the internal puppet/world
        /// bookkeeping only. The wire carries the object, never this id.</summary>
        private static Guid HoldBoxId(InteractablePackagingBox box)
            => box != null && WorldClientBehaviour.TryGetBoxId(box, out var id) ? id : Guid.Empty;

        internal static bool IsSceneReady()
        {
            return NpcInterop.CustomerManager?.GetCustomerList() != null
                && NpcInterop.WorkerManager != null && NpcInterop.Workers != null;
        }

        /// <summary>Client puppet primitive: materialize the visual body for one NPC incarnation
        /// and apply its authoritative spawn pose. Identity binding stays with the identity
        /// module; this returns the object that should be bound (or null when not ready).</summary>
        internal static object SpawnPuppet(in NpcSnapshot snapshot)
        {
            var client = _active;
            if (client == null || !client.RuntimeContext.InGame()
                || !IsSceneReady() || snapshot.Identity <= 0
                || string.IsNullOrEmpty(snapshot.CharName))
            {
                return null;
            }

            return client.SpawnPuppetCore(snapshot);
        }

        private object SpawnPuppetCore(in NpcSnapshot snapshot)
        {
            // Customers name themselves "Female<model>" / "Male<model>"; workers rely on the
            // explicit flag. Accept either source so a missing/incorrect flag cannot spawn a
            // female NPC from the male prefab.
            var female = snapshot.Female || HasFemaleNamePrefix(snapshot.CharName);
            if (snapshot.Kind == KindCustomer)
            {
                using (Util.PerfProbe.Sample("module.npc.identity.capacity"))
                {
                    EnsureCustomerCapacity(snapshot.Index, female);
                }
            }

            var key = (snapshot.Kind << 16) | snapshot.Index;
            if (!_puppets.TryGetValue(key, out var puppet))
            {
                puppet = new Puppet();
                _puppets.Add(key, puppet);
            }

            // A pooled NPC returns with the same prefab, name and gender for every visit; only
            // its generation (the wire Identity) advances. A clone's look is a pure function of
            // gender and character name, so a same-look reincarnation can reuse it. Rebuilding
            // here re-instantiated and fully re-dressed the character on every customer spawn
            // (~7 ms each) even though nothing visible changed.
            var reusedIncarnation = puppet.HasIdentity && puppet.Identity != snapshot.Identity
                && SameDressedLook(puppet, snapshot.Kind, female, snapshot.CharName);

            if (puppet.HasIdentity && puppet.Identity != snapshot.Identity && !reusedIncarnation)
            {
                using (Util.PerfProbe.Sample("module.npc.identity.destroy"))
                {
                    ClearPuppetLook(puppet);
                }
            }

            puppet.Kind = snapshot.Kind;

            if (reusedIncarnation)
            {
                ResetPuppetForIncarnation(puppet, snapshot.Position, snapshot.Index);
            }

            using (Util.PerfProbe.Sample("module.npc.identity.redress"))
            {
                Redress(puppet, snapshot.CharName, snapshot.Position, female, snapshot.Kind,
                    snapshot.Index, snapshot.Identity);
            }

            ApplyNpcState(snapshot);
            return GetPuppetEntity(puppet);
        }

        /// <summary>Detach an outgoing visual from its slot and blank every look-bound reference.
        /// The identity module retires the old object's binding separately.</summary>
        private void ClearPuppetLook(Puppet puppet)
        {
            ParkOrDestroyVisual(puppet);
            puppet.Go = null;
            puppet.Custom = null;
            puppet.CharName = "";
            puppet.Identity = 0;
            puppet.HasIdentity = false;
            puppet.BufCount = 0;
            puppet.GrabSequence = UnsetActionSequence;
        }

        private static object GetPuppetEntity(Puppet puppet)
        {
            if (puppet == null)
            {
                return null;
            }

            if (puppet.Customer != null)
            {
                return puppet.Customer;
            }

            return puppet.Go?.GetComponent<Worker>();
        }

        /// <summary>The object currently bound for this NPC slot, if any. Used by the identity
        /// module to unbind a replaced visual before binding the new incarnation.</summary>
        internal static bool TryGetPuppetEntity(byte kind, ushort index, out object entity)
        {
            entity = null;
            var client = _active;
            if (client == null)
            {
                return false;
            }

            var key = (kind << 16) | index;
            if (!client._puppets.TryGetValue(key, out var puppet))
            {
                return false;
            }

            entity = GetPuppetEntity(puppet);
            return entity != null;
        }

        /// <summary>Client puppet primitive: retire the visual body and any borrowed real carrier
        /// for one NPC incarnation. The identity module owns the cache unbind.</summary>
        internal static bool DespawnPuppet(object entity)
        {
            var client = _active;
            if (client == null || entity == null)
            {
                return false;
            }

            return client.DespawnPuppetCore(entity);
        }

        private bool DespawnPuppetCore(object entity)
        {
            foreach (var pair in _puppets)
            {
                var puppet = pair.Value;
                if (puppet == null || !puppet.HasIdentity)
                {
                    continue;
                }

                if ((entity is Customer customer && ReferenceEquals(puppet.Customer, customer))
                    || (entity is Worker worker && puppet.Go != null
                        && ReferenceEquals(puppet.Go.GetComponent<Worker>(), worker)))
                {
                    RemovePuppet((byte)(pair.Key >> 16), (ushort)(pair.Key & 0xffff),
                        puppet.Identity);
                    return true;
                }
            }

            return false;
        }

        private void RemovePuppet(byte kind, ushort index, int identity)
        {
            if (kind == KindCustomer && _existing.TryGetValue(index, out var existing)
                && existing != null && existing.Generation == identity)
            {
                DetachExistingCustomer(index, existing.Customer);
            }

            _pendingStates.Remove(new NpcDeltaKey(kind, index, identity));

            var key = (kind << 16) | index;
            if (!_puppets.TryGetValue(key, out var puppet)
                || (puppet.HasIdentity && puppet.Identity != identity))
            {
                return;
            }

            ParkOrDestroyVisual(puppet);
            _puppets.Remove(key);
        }

        /// <summary>Customers encode their gender in the model name ("Female3"/"Male7"); the
        /// workforce does not, so it relies on the explicit flag.</summary>
        private static bool HasFemaleNamePrefix(string charName)
            => !string.IsNullOrEmpty(charName)
                && charName.StartsWith("Female", StringComparison.Ordinal);

        /// <summary>Dresses the real pooled customer for an identity in place - the same
        /// name-based resolution the puppet clones run (CharacterName + Initialize, which
        /// re-resolves the preset and destroys/replaces each hair and apparel slot), so the
        /// actual NPC object carries the synced look. Borrowers then reuse the dressed NPC
        /// directly instead of cloning a lookalike. A member already wearing the name is left
        /// untouched - no re-dress churn on repeat visits.</summary>
        private static void DressRealCustomer(ushort index, string charName)
        {
            if (string.IsNullOrEmpty(charName))
            {
                return;
            }

            var customers = NpcInterop.CustomerManager?.GetCustomerList();
            if (customers == null || index >= customers.Count)
            {
                return;
            }

            var custom = customers[index]?.m_CharacterCustom;
            if (custom == null || (custom.m_HasInit && custom.CharacterName == charName))
            {
                return;
            }

            custom.CharacterName = charName;
            custom.Initialize();
        }

        /// <summary>A puppet already wearing exactly the look an identity packet describes.
        /// Appearance is a pure function of gender and character name (the game resolves the
        /// preset by name), so equal values mean no re-dress is needed even when the wire
        /// identity advanced for a new visit by the same pooled NPC.</summary>
        private static bool SameDressedLook(Puppet puppet, byte kind, bool female, string charName)
        {
            return puppet.Go != null && puppet.Custom != null
                && puppet.Kind == kind && puppet.Female == female
                && puppet.CharName == charName;
        }

        /// <summary>Reuse a clone for a new incarnation of the same-looking pooled NPC: reset
        /// the per-visit motion and animation state and snap it to the host's spawn point,
        /// matching what <see cref="Spawn"/> establishes for a fresh clone.</summary>
        private void ResetPuppetForIncarnation(Puppet p, Vector3 position, ushort index)
        {
            ReleaseWorkerBoxProp(p);
            ResetPuppetIncarnationState(p, position);
            if (p.Go != null)
            {
                p.Go.transform.position = position;
                p.Go.SetActive(!IsPuppetSuppressed(p.Kind, index));
            }
        }

        /// <summary>Reset every per-incarnation motion/animation field. Shared by the reused-look
        /// fast path and by pool adoption so a freshly adopted clone starts exactly like one that
        /// was reset in place. Does not touch clone-bound references (the clone is already
        /// attached) or the worker box prop (released separately by the callers).</summary>
        private static void ResetPuppetIncarnationState(Puppet p, Vector3 prevRenderedPos)
        {
            p.BufHead = 0;
            p.BufCount = 0;
            p.Flags = default;
            p.AppliedFlags = -1;
            p.AppliedAnimSpeed = float.NaN;
            p.AnimSpeed = 0f;
            p.RenderYaw = 0f;
            p.PrevRenderedPos = prevRenderedPos;
            p.HoldBig = false;
            p.HoldItemType = EItemType.None;
            p.HoldBoxNetworkId = Guid.Empty;
            p.HoldBoxOpened = false;
            p.AppliedHeldBoxNetworkId = Guid.Empty;
            p.GrabSequence = UnsetActionSequence;
        }

        /// <summary>Whether a puppet for this entity must stay hidden because the register or
        /// trade carrier renders the real pooled customer instead of the clone.</summary>
        private bool IsPuppetSuppressed(byte kind, ushort index)
        {
            return kind == KindCustomer && SuppressedCustomer.Contains(index)
                && (!_existing.TryGetValue(index, out var existing) || !existing.KeepPuppetVisible);
        }

        /// <summary>Commit the incarnation a puppet now wears. Appearance is already applied by
        /// the time this runs; the identity module binds the object afterwards.</summary>
        private static void CommitPuppetIdentity(Puppet puppet, int identity, bool female)
        {
            puppet.Identity = identity;
            puppet.HasIdentity = true;
            puppet.Female = female;
        }

        private void SignalCustomerManagerReady()
        {
            if (NpcInterop.CustomerManager?.GetCustomerList() == null)
            {
                return;
            }

            _customerManagerReady.Raise();
            _customerPoolChanged.Raise();
        }

        private void SignalWorkerManagerReady()
        {
            if (NpcInterop.WorkerManager == null || NpcInterop.Workers == null)
            {
                return;
            }

            _workerManagerReady.Raise();
        }

        [HarmonyPatch(typeof(CustomerManager), "Update")]
        private static class CustomerManagerPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => _active == null;
        }

        [HarmonyPatch(typeof(Customer), "Update")]
        private static class CustomerPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => _active == null;
        }

        // Vanilla Customer.Start force-hides the trade prompt and the interact collider
        // (m_InteractCollider.SetActive(false)). On the client the pool is kept inactive until a
        // module borrows a customer as an interaction carrier, so the carrier's Start is deferred
        // to that first activation - i.e. it runs AFTER the borrowing module enabled the collider.
        // A trade carrier needs that collider to be clickable, and unlike the host there is no
        // Customer.Update driving it back on (that is suppressed right above), so the carrier ends
        // up visible with a "!" but no click volume. The client owns the whole customer lifecycle,
        // so skip the vanilla Start exactly like Update: its only other effect,
        // m_DefaultStartEndModifier, is write-only in both supported builds.
        [HarmonyPatch(typeof(Customer), "Start")]
        private static class CustomerStartPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => _active == null;
        }

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        private static class CustomerActivationPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                _active?.TrackNativeRoot(__instance?.gameObject);
                _customerPoolChanged.Raise();
                return _active == null;
            }
        }

        [HarmonyPatch(typeof(Customer), "DeactivateCustomer")]
        private static class CustomerDeactivationPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                return _active == null || !IsExistingCustomer(__instance);
            }
        }

        [HarmonyPatch(typeof(WorkerManager), "ActivateWorker")]
        private static class WorkerManagerPatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => _active == null;
        }

        [HarmonyPatch(typeof(CustomerManager), "Start")]
        private static class CustomerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CustomerManager __instance)
            {
                _active?.SuppressInitialPopulation(__instance);
                _active?.SignalCustomerManagerReady();
            }
        }

        [HarmonyPatch(typeof(WorkerManager), "Start")]
        private static class WorkerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _active?.SuppressInitialPopulation(null);
                _active?.SignalWorkerManagerReady();
            }
        }

        [HarmonyPatch(typeof(Worker), "ActivateWorker")]
        private static class WorkerActivationPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Worker __instance)
            {
                _active?.TrackNativeRoot(__instance?.gameObject);
                return _active == null;
            }
        }

        [HarmonyPatch(typeof(Worker), "Update")]
        private static class WorkerUpdatePatch
        {
            [HarmonyPrefix]
            private static bool Prefix() => _active == null;
        }

        private const byte KindCustomer = 0;
        private const byte KindWorker = 1;

        /// <summary>Sentinel for "no action sequence applied yet" on a freshly spawned or
        /// re-identified puppet. The first state packet adopts the host's sequence without
        /// replaying an animation, and later packets fire only on a real sequence change.</summary>
        private const int UnsetActionSequence = int.MinValue;

        /// <summary>Render this far in the past (~1.2x send interval) so two bracketing
        /// snapshots almost always exist.</summary>
        private const float InterpDelay = 0.15f;

        // string-keyed animator calls hash the name on every call; cache the ids once
        private static readonly int HashMoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int HashHoldingBag = Animator.StringToHash("HoldingBag");
        private static readonly int HashHandingOverCash = Animator.StringToHash("HandingOverCash");
        private static readonly int HashIsSitting = Animator.StringToHash("IsSitting");
        private static readonly int HashIsPlaying = Animator.StringToHash("IsPlaying");
        private static readonly int HashIsHoldingBox = Animator.StringToHash("IsHoldingBox");
        private static readonly int HashIsBeingSprayed = Animator.StringToHash("IsBeingSprayed");
        private static readonly MethodInfo MiEvaluateWorkerAttribute =
            AccessTools.Method(typeof(Worker), "EvaluateWorkerAttribute");
        private static readonly MethodInfo MiEvaluateSkillLevel =
            AccessTools.Method(typeof(Worker), "EvaluateSkillLevel");

        [Flags]
        private enum NpcFlags : ushort
        {
            None = 0,
            HoldingBag = 1,
            HandingOverCash = 2,
            IsSitting = 4,
            IsPlaying = 8,
            IsHoldingBox = 16,
            Smelly = 32,
            Exclaim = 64,   // the red "!" trade-prompt mesh is showing
            Female = 128,   // puppet should spawn from the female prefab (workers esp.)
            Sprayed = 256,  // the customer's "IsBeingSprayed" animator reaction is playing
            Cleaned = 512,  // the customer's clean puff FX is showing
        }


        private sealed class ExistingCustomer
        {
            public Customer Customer;
            public int RootInstanceId;
            public int Generation;
            public readonly Snap[] Buf = new Snap[4];
            public int BufHead;
            public int BufCount;
            public NpcFlags Flags;
            public int AppliedFlags = -1;
            public Vector3 PrevRenderedPos;
            public float RenderYaw;
            public float AnimSpeed;
            public float AppliedAnimSpeed = float.NaN;
            public bool KeepPuppetVisible;
            public bool PuppetReadyNotified;
            // Last ActionSequence applied to this mirror's animator. int.MinValue means the
            // first state packet for the incarnation adopts the sequence without firing.
            public int GrabSequence = UnsetActionSequence;
        }

        private readonly Dictionary<int, ExistingCustomer> _existing = new();
        private readonly HashSet<int> _nativeNpcRoots = new();
        private readonly HashSet<int> _allowedNativeRoots = new();
        private readonly HashSet<int> _puppetRoots = new();
        private readonly HashSet<int> _knownNonNpcRoots = new();

        public void Reset()
        {
            foreach (var mirror in _existing.Values)
            {
                DeactivateCustomerRoot(mirror?.Customer);
            }
            _existing.Clear();
            ClearPuppets();
            _pendingCapacityChanged.Clear();
            _pendingExistingCustomerChanged.Clear();
            _now = 0f;
            _clockOffset = 0f;
            s_diagCm = null;
            _nativeNpcRoots.Clear();
            _allowedNativeRoots.Clear();
            _knownNonNpcRoots.Clear();
        }

        private static void DeactivateCustomerRoot(Customer customer)
        {
            // A Unity component can be CLR-non-null after its native object was destroyed. Check
            // Unity liveness before reading gameObject; cleanup must continue for every mirror.
            if (ReferenceEquals(customer, null) || customer == null)
            {
                return;
            }

            var root = customer.gameObject;
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }
        }

        private void TrackNativeRoot(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            var id = root.GetInstanceID();
            _nativeNpcRoots.Add(id);
            _knownNonNpcRoots.Remove(id);
            if (root.GetComponent<NpcNativeActivationGuard>() == null)
            {
                root.AddComponent<NpcNativeActivationGuard>();
            }
        }

        internal static void SuppressNativeActivation(GameObject root)
        {
            if (_active != null && _active.ShouldSuppressNativeRoot(root, value: true))
            {
                root.SetActive(false);
            }
        }

        private bool ShouldSuppressNativeRoot(GameObject root, bool value)
        {
            if (!value || root == null || _shutdown)
            {
                return false;
            }

            // Mod-owned visual clones (a character preview or player avatar) are not vanilla
            // pool spawns even though they still carry a Customer component while activating.
            if (root.GetComponent<CoopOwnedVisual>() != null)
            {
                return false;
            }

            var id = root.GetInstanceID();
            if (_puppetRoots.Contains(id) || _allowedNativeRoots.Contains(id))
            {
                return false;
            }

            if (!_nativeNpcRoots.Contains(id) && !_knownNonNpcRoots.Contains(id))
            {
                // Newly instantiated vanilla pool entries are not necessarily present in the
                // manager's list when the first SetActive arrives. Discover that one object at
                // its lifecycle boundary, then cache the result; this is not a population scan.
                var customer = root.GetComponent<Customer>();
                var worker = customer == null ? root.GetComponent<Worker>() : null;
                if (customer != null || worker != null)
                {
                    TrackNativeRoot(root);
                }
                else
                {
                    _knownNonNpcRoots.Add(id);
                }
            }

            return _nativeNpcRoots.Contains(id);
        }

        private void RegisterPuppetRoot(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            var id = root.GetInstanceID();
            _puppetRoots.Add(id);
            _nativeNpcRoots.Remove(id);
            _knownNonNpcRoots.Remove(id);
        }

        private void UnregisterPuppetRoot(int rootInstanceId)
        {
            if (rootInstanceId != 0)
            {
                _puppetRoots.Remove(rootInstanceId);
            }
        }

        private void DestroyPuppetObject(Puppet puppet)
        {
            if (puppet == null)
            {
                return;
            }

            var root = puppet.Go;
            var rootInstanceId = puppet.RootInstanceId;
            puppet.RootInstanceId = 0;
            puppet.Go = null;
            puppet.Anim = null;
            puppet.Custom = null;
            UnregisterPuppetRoot(rootInstanceId);

            // The CLR wrapper may outlive the native object. The Unity null check is safe, but
            // no member (including GetInstanceID) is read from a destroyed wrapper.
            if (!ReferenceEquals(root, null) && root != null)
            {
                Destroy(root);
            }
        }

        public static Transform GetWorkerHoldAnchor(int index)
        {
            if (_active == null)
            {
                return null;
            }

            var key = (KindWorker << 16) | index;
            return _active._puppets.TryGetValue(key, out var p) ? p.HoldBox : null;
        }

        public static Worker GetWorkerPuppet(int index)
        {
            if (_active == null)
            {
                return null;
            }

            var key = (KindWorker << 16) | index;
            return _active._puppets.TryGetValue(key, out var p) && p.Go != null
                ? p.Go.GetComponent<Worker>() : null;
        }

        /// <summary>Show a stripped cosmetic box on a worker puppet. The synchronized
        /// gameplay object is intentionally never attached to the puppet; instead the real world
        /// box the worker took is parked (see <see cref="WorldClientBehaviour.ApplyWorkerHeldBox"/>)
        /// and this prop carries the visual, including its open/closed state.</summary>
        public static void SetWorkerBoxVisual(int index, bool visible, bool isBig, EItemType itemType,
            Guid boxNetworkId, bool opened)
        {
            if (_active == null)
            {
                return;
            }

            var key = (KindWorker << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var p) || p.Go == null)
            {
                return;
            }

            if (!visible)
            {
                _active.ReleaseWorkerBoxProp(p);
                return;
            }

            if (p.BoxProp != null && p.BoxPropBig == isBig && p.BoxPropType == itemType)
            {
                p.BoxProp.SetActive(true);
            }
            else
            {
                _active.ReleaseWorkerBoxProp(p);
                var rm = SceneRef<RestockManager>.Get();
                var prefab = isBig ? rm.m_PackageBoxPrefab : rm.m_PackageBoxSmallPrefab;

                var holder = new GameObject("CoopWorkerBoxProp_tmp");
                holder.SetActive(false);
                var clone = Instantiate(prefab.gameObject, holder.transform);

                // Capture the open/closed visual groups from the box component before every
                // MonoBehaviour is stripped; the child GameObjects survive that removal.
                var sourceBox = clone.GetComponent<InteractablePackagingBox_Item>();
                if (sourceBox != null)
                {
                    p.BoxPropStaticMesh = sourceBox.m_StaticMeshGrp;
                    p.BoxPropRigMesh = sourceBox.m_RigMeshGrp;
                    p.BoxPropOpen = sourceBox.m_OpenBox;
                    p.BoxPropClosed = sourceBox.m_ClosedBox;
                    p.BoxPropOutlineOpen = sourceBox.m_OutlineOpenBox;
                    p.BoxPropOutlineClosed = sourceBox.m_OutlineClosedBox;
                }
                else
                {
                    p.BoxPropStaticMesh = null;
                    p.BoxPropRigMesh = null;
                    p.BoxPropOpen = null;
                    p.BoxPropClosed = null;
                    p.BoxPropOutlineOpen = null;
                    p.BoxPropOutlineClosed = null;
                }

                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb != null)
                    {
                        DestroyImmediate(mb);
                    }
                }

                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb != null)
                    {
                        DestroyImmediate(rb);
                    }
                }

                foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null)
                    {
                        DestroyImmediate(col);
                    }
                }

                clone.transform.SetParent(p.HoldBox, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.name = "CoopWorkerBoxProp";
                clone.SetActive(true);
                Destroy(holder);
                p.BoxProp = clone;
                p.BoxPropBig = isBig;
                p.BoxPropType = itemType;
            }

            ApplyWorkerBoxOpenState(p, opened);

            // The real world box the worker took must leave its shelf slot; this prop represents
            // the carried box. Only touch the World module when the held box id changes.
            if (boxNetworkId != Guid.Empty)
            {
                if (boxNetworkId != p.AppliedHeldBoxNetworkId)
                {
                    p.AppliedHeldBoxNetworkId = boxNetworkId;
                    WorldClientBehaviour.ApplyWorkerHeldBox(boxNetworkId);
                }
            }
            else
            {
                p.AppliedHeldBoxNetworkId = Guid.Empty;
            }
        }

        private static void ApplyWorkerBoxOpenState(Puppet p, bool opened)
        {
            // Mirror InteractablePackagingBox_Item.ResetToggleOpenClose exactly. The stripped clone
            // never runs Awake, so the static/rig groups must be forced to the steady state or the
            // open/closed children are toggled on a group that is not visible.
            SetActive(p.BoxPropStaticMesh, true);
            SetActive(p.BoxPropRigMesh, false);
            SetActive(p.BoxPropOpen, opened);
            SetActive(p.BoxPropClosed, !opened);
            SetActive(p.BoxPropOutlineOpen, opened);
            SetActive(p.BoxPropOutlineClosed, !opened);
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }

        /// <summary>Copies authoritative staff settings onto the inert worker component
        /// inside the local visual puppet so the original WorkerInteractUIScreen can use
        /// its normal data and labels.</summary>
        public static void RefreshWorkerUi(int index, WorkerSaveData data)
        {
            if (_active == null || data == null)
            {
                return;
            }

            var key = (KindWorker << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var p) || p.Go == null)
            {
                return;
            }

            var worker = p.Go.GetComponent<Worker>();
            if (worker == null)
            {
                return;
            }

            {
                worker.m_PrimaryTask = data.primaryTask;
                worker.m_SecondaryTask = data.secondaryTask;
                worker.m_WorkerTask = data.workerTask;
                worker.m_CurrentState = data.currentState;
                worker.m_IsBonusBoosted = data.isBonusBoosted;
                worker.m_BonusBoostedCount = data.bonusBoostedCount;
                worker.SetRestockShelfWithNoLabel(data.isFillShelfWithoutLabel);
                worker.UpdateSetPriceOption(data.isRoundUpPrice, data.isAvoidSetCardPrice, data.setPriceMultiplier);
                worker.UpdateSetCardPriceOption(data.isRoundUpCardPrice, data.isAvoidSetCardPriceWhileRestock, data.setCardPriceMultiplier);
                if (data.cardPackItemTypeEnabledList != null)
                {
                    // A visual puppet does not run the live worker initialization path, so the
                    // list can be empty. Grow it to the authoritative length before applying, or
                    // the pack-refill screen has no index to toggle and Confirm sends nothing.
                    // the pack-refill screen has no index to toggle and Confirm sends nothing.
                    var enabled = worker.GetCardPackItemTypeEnabledList();
                    while (enabled.Count < data.cardPackItemTypeEnabledList.Count)
                    {
                        enabled.Add(true);
                    }

                    for (var i = 0; i < data.cardPackItemTypeEnabledList.Count; i++)
                    {
                        worker.SetCardPackItemTypeEnabled(i, data.cardPackItemTypeEnabledList[i]);
                    }
                }
                if (data.expList != null)
                {
                    worker.m_ExpList.Clear();
                    worker.m_ExpList.AddRange(data.expList);
                }
                MiEvaluateSkillLevel?.Invoke(worker, null);
            }
        }

        /// <summary>Client: a worker puppet is built from the gender-correct manager prefab and
        /// intentionally does not run Worker.InitializeCharacter, so its
        /// m_CardPackItemTypeEnabledList stays empty. The pack-refill option
        /// screen indexes that list on every toggle and on Confirm, so an empty list makes
        /// every option untickable and sends no change. Grow it to the card-pack count (the
        /// game's default is enabled); the next authoritative RefreshWorkerUi sets the values.</summary>
        private static void EnsureCardPackList(Worker worker)
        {
            if (worker == null)
            {
                return;
            }

            // NEVER CSingleton<InventoryBase>.Instance: touched while no real manager
            // exists it fabricates a fake empty DontDestroyOnLoad InventoryBase that
            // shadows the real one for the rest of the run (see WorldGradingInteraction.Inv).
            var ib = Inv();
            var enabled = worker.GetCardPackItemTypeEnabledList();
            var count = ib.m_StockItemData_SO.m_CardPackItemTypeList.Count;
            while (enabled.Count < count)
            {
                enabled.Add(true);
            }
        }

        // Unity fake-null makes the cached lookup re-resolve after a scene load.
        private static InventoryBase Inv()
            => SceneRef<InventoryBase>.Get();

        // ---------------- client: puppets ----------------

        private struct Snap
        {
            public Vector3 Pos;
            public float Yaw;
            public float Speed;
            public NpcFlags Flags;
            public float Time; // host time mapped onto the local _now timeline
        }

        private class Puppet
        {
            public GameObject Go;
            public bool VisualFromLive; // visual came from a live scene/pooled source, not the manager template
            // Unity can destroy the native object while retaining this managed wrapper. Keep the
            // id captured while the root was alive so cleanup never has to call GetInstanceID()
            // on a fake-null wrapper.
            public int RootInstanceId;
            public Animator Anim;
            public CC.CharacterCustomization Custom;
            // The clone's own (behaviour-disabled) Customer component: its fields reference the
            // clone's animator and prop children, so presentation applies through the one shared
            // routine instead of the per-prop captures below. Null for worker puppets.
            public Customer Customer;
            public GameObject Bag;
            public GameObject Cash;
            public GameObject CardFan;
            public GameObject CardSingle;
            public GameObject Smelly;
            public GameObject Clean;     // the clean puff FX after a spray
            public GameObject Exclaim;   // the red "!" trade prompt mesh
            public bool Female;          // which prefab this puppet was spawned from
            public byte Kind;
            public int Identity;
            public bool HasIdentity;
            public string CharName = "";
            public readonly Snap[] Buf = new Snap[4]; // ring buffer, newest at BufHead
            public int BufHead;
            public int BufCount;
            public NpcFlags Flags;
            public int AppliedFlags = -1; // -1 forces the first animator/prop push
            public float RenderYaw;
            public Vector3 PrevRenderedPos;
            public float AnimSpeed;
            public float AppliedAnimSpeed = float.NaN;
            public Transform HoldBox;
            public GameObject BoxProp;
            public bool BoxPropBig;
            public EItemType BoxPropType;
            public GameObject BoxPropStaticMesh;
            public GameObject BoxPropRigMesh;
            public GameObject BoxPropOpen;
            public GameObject BoxPropClosed;
            public GameObject BoxPropOutlineOpen;
            public GameObject BoxPropOutlineClosed;
            public Guid HoldBoxNetworkId;
            public bool HoldBoxOpened;
            public Guid AppliedHeldBoxNetworkId;
            public bool HoldBig;
            public EItemType HoldItemType;
            // Last ActionSequence applied to this puppet's animator. int.MinValue means the
            // first state packet for the incarnation adopts the sequence without firing.
            public int GrabSequence = UnsetActionSequence;
        }

        /// <summary>A dressed clone parked in the look-keyed pool. Holds exactly the references
        /// bound to the clone's GameObject; nothing here is per-incarnation state. The owning
        /// <see cref="Puppet"/> slot is blanked when a visual is parked so a slot never points at
        /// an inactive clone.</summary>
        private sealed class PooledVisual
        {
            public VisualKey Key; // pool bookkeeping: which look this visual wears
            public GameObject Go;
            // Whether this clone's visual hierarchy came from a live scene/pooled source
            // (possibly carrying an appearance mod) rather than the manager template.
            public bool FromLiveSource;
            public int RootInstanceId;
            public Animator Anim;
            public CC.CharacterCustomization Custom;
            public Customer Customer;
            public GameObject Bag;
            public GameObject Cash;
            public GameObject CardFan;
            public GameObject CardSingle;
            public GameObject Smelly;
            public GameObject Clean;
            public GameObject Exclaim;
            public Transform HoldBox;
        }

        /// <summary>The look a clone wears. Appearance is a pure function of gender, kind and the
        /// character name (the game resolves the preset by name), so two clones with the same key
        /// are visually interchangeable.</summary>
        private readonly struct VisualKey : IEquatable<VisualKey>
        {
            internal VisualKey(byte kind, bool female, string charName)
            {
                Kind = kind;
                Female = female;
                CharName = charName ?? "";
            }

            internal byte Kind
            {
                get;
            }
            internal bool Female
            {
                get;
            }
            internal string CharName
            {
                get;
            }

            public bool Equals(VisualKey other)
                => Kind == other.Kind && Female == other.Female
                    && string.Equals(CharName, other.CharName, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is VisualKey other && Equals(other);

            public override int GetHashCode()
                => ((Kind * 397) ^ (Female ? 1 : 0)) * 397
                    ^ StringComparer.Ordinal.GetHashCode(CharName);

            public override string ToString()
                => "kind=" + Kind + " female=" + Female + " name=" + CharName;
        }

        private void ReleaseWorkerBoxProp(Puppet p)
        {
            if (p == null)
            {
                return;
            }

            var prop = p.BoxProp;
            p.BoxProp = null;
            p.BoxPropBig = false;
            p.BoxPropType = EItemType.None;
            p.BoxPropStaticMesh = null;
            p.BoxPropRigMesh = null;
            p.BoxPropOpen = null;
            p.BoxPropClosed = null;
            p.BoxPropOutlineOpen = null;
            p.BoxPropOutlineClosed = null;
            // Force the next hold of this (or any) box to notify the World module again.
            p.AppliedHeldBoxNetworkId = Guid.Empty;
            if (ReferenceEquals(prop, null) || prop == null)
            {
                return;
            }

            Destroy(prop);
        }

        private readonly Dictionary<int, Puppet> _puppets = new();
        private readonly Dictionary<NpcDeltaKey, NpcSnapshot> _pendingStates = new();
        private float _now;
        private float _clockOffset;
        private bool _clockInit;

        // Look-keyed pool of dressed clones parked when an incarnation/look ends. The pool is
        // bounded and evicts the least-recently-used visual; entries stay registered in
        // _puppetRoots while parked so native activation suppression keeps ignoring them.
        private const int VisualPoolCapacity = 12;
        private readonly Dictionary<VisualKey, List<PooledVisual>> _visualPool = new();
        private readonly List<PooledVisual> _visualPoolOrder = new(); // LRU, index 0 = oldest

        /// <summary>Detach a clone from its slot and move it into the pool. Deactivates the
        /// object, releases the worker box prop parented under it, and blanks every clone-bound
        /// field on the slot. The root id is intentionally NOT unregistered: a parked clone must
        /// stay in _puppetRoots until it is evicted or the pool is destroyed.</summary>
        private PooledVisual CaptureVisual(Puppet p)
        {
            var visual = new PooledVisual
            {
                Go = p.Go,
                RootInstanceId = p.RootInstanceId,
                Anim = p.Anim,
                Custom = p.Custom,
                Customer = p.Customer,
                Bag = p.Bag,
                Cash = p.Cash,
                CardFan = p.CardFan,
                CardSingle = p.CardSingle,
                Smelly = p.Smelly,
                Clean = p.Clean,
                Exclaim = p.Exclaim,
                HoldBox = p.HoldBox,
                FromLiveSource = p.VisualFromLive,
            };
            p.Go?.SetActive(false);

            ReleaseWorkerBoxProp(p);

            p.Go = null;
            p.RootInstanceId = 0;
            p.Anim = null;
            p.Custom = null;
            p.Customer = null;
            p.Bag = null;
            p.Cash = null;
            p.CardFan = null;
            p.CardSingle = null;
            p.Smelly = null;
            p.Clean = null;
            p.Exclaim = null;
            p.HoldBox = null;
            p.VisualFromLive = false;
            return visual;
        }

        /// <summary>Park a dressed clone for later reuse, or hard-destroy it when no real look
        /// exists. Parking is only valid for an initialized look (Go and Custom alive with a
        /// committed name); anything else is torn down and its root unregistered.</summary>
        private void ParkOrDestroyVisual(Puppet p)
        {
            if (p == null)
            {
                return;
            }

            if (p.Go == null || p.Custom == null || !p.HasIdentity
                || string.IsNullOrEmpty(p.CharName))
            {
                DestroyPuppetObject(p);
                return;
            }

            var visual = CaptureVisual(p);
            var key = new VisualKey(p.Kind, p.Female, p.CharName);
            visual.Key = key;
            EvictVisualsToCapacity();
            if (!_visualPool.TryGetValue(key, out var list))
            {
                list = new List<PooledVisual>();
                _visualPool.Add(key, list);
            }

            list.Add(visual);
            _visualPoolOrder.Add(visual);
            CoopPlugin.Log.LogDebug("[npc-pool] park " + key + " count=" + _visualPoolOrder.Count);
        }

        /// <summary>Evict least-recently-used visuals until one more can be parked within
        /// capacity, destroying each evicted clone and unregistering its root.</summary>
        private void EvictVisualsToCapacity()
        {
            while (_visualPoolOrder.Count >= VisualPoolCapacity)
            {
                var victim = _visualPoolOrder[0];
                _visualPoolOrder.RemoveAt(0);
                RemoveFromVisualPool(victim);
                var go = victim.Go;
                var rootInstanceId = victim.RootInstanceId;
                victim.Go = null;
                victim.RootInstanceId = 0;
                UnregisterPuppetRoot(rootInstanceId);
                CoopPlugin.Log.LogDebug("[npc-pool] evict " + victim.Key
                    + " count=" + _visualPoolOrder.Count);
                if (go != null)
                {
                    Destroy(go);
                }
            }
        }

        private void RemoveFromVisualPool(PooledVisual visual)
        {
            if (!_visualPool.TryGetValue(visual.Key, out var list))
            {
                return;
            }

            list.Remove(visual);
            if (list.Count == 0)
            {
                _visualPool.Remove(visual.Key);
            }
        }

        /// <summary>Pop the most recently parked clone for a look AND source, dropping (and
        /// retrying past) entries whose native object was destroyed under us. Entries built from
        /// the other source are left parked: a live-sourced clone may carry an appearance mod's
        /// hierarchy that a template clone lacks, and vice versa. Unity's fake-null makes a
        /// destroyed wrapper liveness-checkable without throwing.</summary>
        private bool TryTakeVisual(VisualKey key, bool fromLiveSource, out PooledVisual visual)
        {
            if (_visualPool.TryGetValue(key, out var list))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var candidate = list[i];
                    if (candidate.FromLiveSource != fromLiveSource)
                    {
                        continue;
                    }

                    list.RemoveAt(i);
                    if (list.Count == 0)
                    {
                        _visualPool.Remove(key);
                    }

                    _visualPoolOrder.Remove(candidate);
                    if (candidate.Go != null)
                    {
                        visual = candidate;
                        return true;
                    }

                    UnregisterPuppetRoot(candidate.RootInstanceId);
                }
            }

            visual = null;
            return false;
        }

        /// <summary>Reattach a parked clone to a slot: copy the clone-bound references back,
        /// restore the display name, move it to the spawn point and make it visible.</summary>
        private void ApplyVisual(Puppet p, PooledVisual visual, string charName, Vector3 pos)
        {
            p.Go = visual.Go;
            p.RootInstanceId = visual.RootInstanceId;
            p.Anim = visual.Anim;
            p.Custom = visual.Custom;
            p.Customer = visual.Customer;
            p.Bag = visual.Bag;
            p.Cash = visual.Cash;
            p.CardFan = visual.CardFan;
            p.CardSingle = visual.CardSingle;
            p.Smelly = visual.Smelly;
            p.Clean = visual.Clean;
            p.Exclaim = visual.Exclaim;
            p.HoldBox = visual.HoldBox;
            p.VisualFromLive = visual.FromLiveSource;
            p.Go.name = "CoopNpc_" + charName;
            p.Go.transform.position = pos;
            p.Go.SetActive(true);
        }

        /// <summary>Destroy every parked clone. Called from teardown before _puppetRoots is
        /// cleared so a scene unload cannot leave dangling references.</summary>
        private void DestroyVisualPool()
        {
            for (var i = 0; i < _visualPoolOrder.Count; i++)
            {
                var visual = _visualPoolOrder[i];
                var go = visual.Go;
                var rootInstanceId = visual.RootInstanceId;
                visual.Go = null;
                visual.RootInstanceId = 0;
                UnregisterPuppetRoot(rootInstanceId);
                if (go != null)
                {
                    Destroy(go);
                }
            }

            _visualPoolOrder.Clear();
            _visualPool.Clear();
        }

        private readonly struct NpcDeltaKey : IEquatable<NpcDeltaKey>
        {
            internal NpcDeltaKey(byte kind, ushort index, int identity)
            {
                Kind = kind;
                Index = index;
                Identity = identity;
            }

            internal byte Kind
            {
                get;
            }
            internal ushort Index
            {
                get;
            }
            internal int Identity
            {
                get;
            }

            public bool Equals(NpcDeltaKey other)
                => Kind == other.Kind && Index == other.Index && Identity == other.Identity;

            public override bool Equals(object obj) => obj is NpcDeltaKey other && Equals(other);

            public override int GetHashCode()
                => ((Kind * 397) ^ Index) * 397 ^ Identity;
        }

        /// <summary>Client: customer list indices whose puppet clone must NOT render, because
        /// the register's carrier (a real, active pool customer) IS that served customer and
        /// shows its own real interactable cash. Populated by the Register module; cleared on reset.</summary>
        public static readonly HashSet<int> SuppressedCustomer = new();

        private static CustomerManager s_diagCm;

        /// <summary>Client diagnostic: active local NPCs which are not intentional register
        /// carriers or client customer mirrors.</summary>
        public static int CountUnexpectedActiveNpcs()
        {
            s_diagCm = SceneRef<CustomerManager>.Get();

            var n = 0;
            if (s_diagCm != null)
            {
                var list = s_diagCm.GetCustomerList();
                for (var i = 0; i < list.Count; i++)
                {
                    var customer = list[i];
                    if (customer != null && customer.gameObject.activeSelf
                        && !RegisterClientBehaviour.IsCarrier(customer) && !IsExistingCustomer(customer))
                    {
                        n++;
                    }
                }
            }
            var workers = NpcInterop.Workers;
            if (workers != null)
            {
                for (var i = 0; i < workers.Count; i++)
                {
                    if (workers[i] != null && workers[i].gameObject.activeSelf)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        public static int ExistingMirrorCount => _active == null ? 0 : _active._existing.Count;

        public void ClearPuppets()
        {
            foreach (var p in _puppets.Values)
            {
                ReleaseWorkerBoxProp(p);
                // Remove the stored id before checking the native root. The registry is
                // independent of the GameObject's native lifetime.
                DestroyPuppetObject(p);
            }
            _puppets.Clear();
            DestroyVisualPool();
            // Also clear ids that no longer have a Puppet wrapper (for example, a failed spawn
            // destroyed by Unity between two lifecycle callbacks).
            _puppetRoots.Clear();
            _pendingStates.Clear();
            SuppressedCustomer.Clear();
            _clockInit = false;
        }

        /// <summary>Client-only: show a host-selected customer speech bubble over the live
        /// customer the message carries. The handler has already dropped a message without one.</summary>
        public void ShowSpeech(NpcSpeechMessage message, bool inGame)
        {
            if (!inGame)
            {
                return;
            }

            var spawner = SceneRef<PricePopupSpawner>.Get();
            if (spawner == null)
            {
                return;
            }

            spawner.ShowTextPopup(message.Text, message.OffsetUp, message.Customer.transform);
        }

        /// <summary>Mirrors the host's green add-money popup on the live customer the message
        /// carries. The handler has already dropped a message without one.</summary>
        public void ShowMoneyPopup(NpcMoneyPopupMessage message, bool inGame)
        {
            if (!inGame)
            {
                return;
            }

            var spawner = SceneRef<PricePopupSpawner>.Get();
            if (spawner == null)
            {
                return;
            }

            spawner.ShowPricePopup(message.Amount, message.OffsetUp, message.Customer.transform);
        }

        /// <summary>The GameObject instance id of a customer root. Native activation
        /// suppression is keyed on the root <see cref="GameObject"/>, so carriers must be
        /// whitelisted with the same id. <c>Customer.GetInstanceID()</c> would return the
        /// component's own id and never match the root the guard sees.</summary>
        private static int CustomerRootId(Customer customer)
            => !ReferenceEquals(customer, null) && customer != null && customer.gameObject != null
                ? customer.gameObject.GetInstanceID() : 0;

        public static void DetachExistingCustomer(int index, Customer customer)
        {
            if (_active == null)
            {
                return;
            }

            var key = (KindCustomer << 16) | index;
            // The list index can be reused by a newer pooled customer. Only tear down the mirror
            // registry entries that still belong to the customer being detached, so an old
            // teardown can never hide or de-suppress a newer customer that took the same slot.
            var ownsMirror = _active._existing.TryGetValue(index, out var existing)
                && (customer == null || ReferenceEquals(existing.Customer, customer));
            var customerRootId = CustomerRootId(customer);
            if (ownsMirror && existing.RootInstanceId != 0)
            {
                _active._allowedNativeRoots.Remove(existing.RootInstanceId);
            }
            else if (customerRootId != 0)
            {
                _active._allowedNativeRoots.Remove(customerRootId);
            }
            if (ownsMirror)
            {
                _active._existing.Remove(index);
                SuppressedCustomer.Remove(index);
                if (_active._puppets.TryGetValue(key, out var puppet) && puppet.Go != null && puppet.BufCount > 0)
                {
                    puppet.Go.SetActive(true);
                }
            }
            // The caller owns the real carrier it passes in: always take it off the scene.
            // A ready puppet was revealed above; if none exists yet, the next snapshot spawns it.
            DeactivateCustomerRoot(customer);
            _customerPoolChanged.Raise();
            _pendingExistingCustomerChanged[index] =
                new ExistingCustomerChange(existing?.Generation ?? 0, customer);
        }

        public static void AttachExistingCustomer(int index, int generation, Customer customer, bool keepPuppetVisible = false)
        {
            if (_active == null || customer == null)
            {
                return;
            }

            _active._allowedNativeRoots.Add(CustomerRootId(customer));
            _active.TrackNativeRoot(customer.gameObject);

            if (!_active._existing.TryGetValue(index, out var existing) || existing.Generation != generation)
            {
                if (existing != null && existing.RootInstanceId != 0
                    && !ReferenceEquals(existing.Customer, customer))
                {
                    _active._allowedNativeRoots.Remove(existing.RootInstanceId);
                }

                existing = new ExistingCustomer
                {
                    Customer = customer,
                    RootInstanceId = CustomerRootId(customer),
                    Generation = generation,
                    PrevRenderedPos = customer.transform.position,
                    RenderYaw = customer.transform.eulerAngles.y,
                    KeepPuppetVisible = keepPuppetVisible,
                };
                _active._existing[index] = existing;
            }
            else
            {
                if (existing.RootInstanceId != 0
                    && !ReferenceEquals(existing.Customer, customer))
                {
                    _active._allowedNativeRoots.Remove(existing.RootInstanceId);
                    existing.PuppetReadyNotified = false;
                }

                existing.Customer = customer;
                existing.RootInstanceId = CustomerRootId(customer);
                existing.KeepPuppetVisible = keepPuppetVisible;
            }
            var key = (KindCustomer << 16) | index;
            if (_active._puppets.TryGetValue(key, out var puppet))
            {
                if (!keepPuppetVisible && puppet.Go != null)
                {
                    // A real-body borrow (the register) takes over the walking mirror's exact
                    // tracked pose and snapshot history: the real NPC continues the puppet's
                    // trajectory instead of teleporting to its seeded spot or gliding in from
                    // it. The puppet itself is parked below and keeps buffering for the
                    // hand-back at detach.
                    customer.transform.position = puppet.Go.transform.position;
                    customer.transform.rotation = puppet.Go.transform.rotation;
                    existing.PrevRenderedPos = puppet.PrevRenderedPos;
                    existing.RenderYaw = puppet.RenderYaw;
                    existing.AnimSpeed = puppet.AnimSpeed;
                    existing.AppliedAnimSpeed = puppet.AppliedAnimSpeed;
                    existing.BufHead = puppet.BufHead;
                    existing.BufCount = puppet.BufCount;
                    for (var i = 0; i < puppet.Buf.Length; i++)
                    {
                        existing.Buf[i] = puppet.Buf[i];
                    }
                }

                // keepPuppetVisible borrows that render the clone (trade) reveal it the same
                // frame the borrow takes the customer - hiding it here left a one-to-two frame
                // hole before the puppet appeared. Reveal only a body that already wears this
                // incarnation's look; a stale or still-unnamed body stays hidden for the
                // identity pass to rebuild and reveal through the snapshot path.
                puppet.Go?.SetActive(keepPuppetVisible && puppet.HasIdentity
                    && puppet.Identity == generation);
            }
            _customerPoolChanged.Raise();
            _pendingExistingCustomerChanged[index] =
                new ExistingCustomerChange(generation, customer);

            // The client suppresses vanilla Customer.ActivateCustomer (and Start), so a pooled
            // customer's interaction and prop children carry whatever state the prefab or a
            // previous incarnation left behind. This borrow just made the real body the visible
            // one, so present it in the vanilla activation state (flags = none) through the one
            // shared routine; the authoritative flag application then applies on top from the
            // snapshots - the pose handoff above deliberately leaves AppliedFlags at the
            // force-apply sentinel so that first push is unconditional.
            ApplyPresentation(customer, (NpcFlags)0);
            customer.m_InteractCollider?.SetActive(false);
        }

        public static bool IsExistingCustomer(Customer customer)
        {
            if (_active == null || customer == null)
            {
                return false;
            }

            foreach (var mirror in _active._existing.Values)
            {
                if (ReferenceEquals(mirror.Customer, customer))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsExistingCustomerPuppetReady(int index, int generation)
        {
            if (_active == null || !_active._existing.TryGetValue(index, out var existing)
                || existing.Generation != generation || !existing.KeepPuppetVisible)
            {
                return false;
            }

            var key = (KindCustomer << 16) | index;
            return _active._puppets.TryGetValue(key, out var puppet)
                && puppet.Go != null && puppet.HasIdentity && puppet.Identity == generation
                && puppet.Go.activeSelf;
        }

        internal static bool TryGetCustomerGeneration(int index, out int generation)
        {
            generation = 0;
            if (_active == null)
            {
                return false;
            }

            if (_active._existing.TryGetValue(index, out var existing) && existing != null)
            {
                generation = existing.Generation;
                return generation > 0;
            }

            var key = (KindCustomer << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity)
            {
                return false;
            }

            generation = puppet.Identity;
            return generation > 0;
        }

        internal static bool TryGetWireIdentity(Customer customer, out int index, out int identity)
        {
            index = -1;
            identity = 0;
            if (_active == null || customer == null)
            {
                return false;
            }

            // The wire may carry a puppet clone (not a pooled manager member) when a customer
            // field is serialized from the presentation object; resolve it from its slot first.
            foreach (var pair in _active._puppets)
            {
                var puppet = pair.Value;
                if (puppet != null && puppet.HasIdentity
                    && ReferenceEquals(puppet.Customer, customer))
                {
                    index = (int)(pair.Key & 0xffff);
                    identity = puppet.Identity;
                    return identity > 0;
                }
            }

            return NpcInterop.TryGetCustomerIndex(customer, out index)
                && TryGetCustomerGeneration(index, out identity);
        }

        internal static bool TryGetWireIdentity(Worker worker, out int index, out int identity)
        {
            index = worker == null ? -1 : worker.m_WorkerIndex;
            identity = 0;
            if (_active == null || worker == null || index < 0)
            {
                return false;
            }

            var key = (KindWorker << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity)
            {
                return false;
            }

            identity = puppet.Identity;
            return identity > 0;
        }

        internal static bool TryResolveCustomer(int index, int identity, out Customer customer)
        {
            customer = null;
            if (_active == null || index < 0 || identity <= 0)
            {
                return false;
            }

            if (_active._existing.TryGetValue(index, out var existing) && existing != null
                && existing.Generation == identity)
            {
                customer = existing.Customer;
                return customer != null;
            }

            var key = (KindCustomer << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity
                || puppet.Identity != identity)
            {
                return false;
            }

            customer = puppet.Customer;
            return customer != null;
        }

        internal static bool TryResolveWorker(int index, int identity, out Worker worker)
        {
            worker = null;
            if (_active == null || index < 0 || identity <= 0)
            {
                return false;
            }

            var key = (KindWorker << 16) | index;
            if (!_active._puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity
                || puppet.Identity != identity || puppet.Go == null)
            {
                return false;
            }

            worker = puppet.Go.GetComponent<Worker>();
            return worker != null;
        }

        private bool IsExistingCustomer(int index, Customer customer)
        {
            return customer != null && _existing.TryGetValue(index, out var mirror)
                && ReferenceEquals(mirror.Customer, customer);
        }

        /// <summary>Grows the scene-owned customer pool through the game's public factory so
        /// an authoritative host list index also exists locally. The factory is present in both
        /// supported game baselines and appends in index order while leaving the new carrier
        /// inactive. New capacity is tracked immediately so vanilla cannot expose an extra native
        /// customer beside its synchronized puppet. The slot is also reconciled to the host's
        /// authoritative gender: the Steam and Game Pass builds bake the same pool bodies in a
        /// different order, so an index can name a male customer on the host and a female body on
        /// the guest (whose preset table then falls back to the default model).</summary>
        private static void EnsureCustomerCapacity(int index, bool female)
        {
            // The host's index is a ushort on the wire. The game's pool is tens of bodies, so a
            // malformed/cross-build descriptor (for example -1 serialized as 65535) must not turn
            // into 65k full customer prefab instantiations inside a baseline handler.
            const int MaxCustomerPoolIndex = 256;
            var manager = NpcInterop.CustomerManager;
            var customers = manager?.GetCustomerList();
            if (customers == null || index < 0 || index > MaxCustomerPoolIndex)
            {
                CoopPlugin.Log.LogError("npc: customer slot " + index
                    + " is outside the local pool limit " + MaxCustomerPoolIndex
                    + "; dropping the spawn.");
                return;
            }

            var grew = false;
            while (customers.Count <= index)
            {
                var customer = manager.GetNewCustomerPrefab(female);
                if (customer == null)
                {
                    CoopPlugin.Log.LogError("npc: customer factory returned no body for slot "
                        + index + "; dropping the spawn.");
                    return;
                }

                _active.TrackNativeRoot(customer.gameObject);
                customer.gameObject.SetActive(false);
                grew = true;
            }

            EnsureCustomerSlotGender(customers, index, female);

            if (grew)
            {
                // Runtime pool growth must announce itself so client modules holding state for
                // this slot (trade/register carriers, deodorant state) can apply it. A customer's
                // identity can reach the host's incremental scan after its counter trade offer, so
                // this is the only signal the guest gets that the slot its offer refers to exists.
                _customerPoolChanged.Raise();
                _pendingCapacityChanged.Add(index);
            }
        }

        /// <summary>Keeps the local pool index authoritative for the host's customer. Both
        /// supported pools contain the same bodies, but the Steam and Game Pass builds order the
        /// male and female entries differently, so the body at a host index can be the opposite
        /// gender. Swap it with a free body of the authoritative gender before the identity is
        /// dressed; the old
        /// body moves to that free slot and is reconciled if the host ever uses it. Never move a
        /// body an active register/trade carrier is borrowing.</summary>
        private static void EnsureCustomerSlotGender(List<Customer> customers, int index, bool female)
        {
            var slot = customers[index];
            if (slot == null || slot.m_IsFemale == female)
            {
                return;
            }

            if (slot.gameObject.activeSelf || SuppressedCustomer.Contains(index)
                || (_active != null && _active._existing.ContainsKey(index)))
            {
                CoopPlugin.Log.LogError("npc: customer slot " + index
                    + " is in use but has the wrong gender; cannot reconcile.");
                return;
            }

            for (var i = 0; i < customers.Count; i++)
            {
                var candidate = customers[i];
                if (candidate == null || candidate.m_IsFemale != female
                    || candidate.gameObject.activeSelf
                    || SuppressedCustomer.Contains(i)
                    || (_active != null && _active._existing.ContainsKey(i))
                    || HasCustomerIdentity(i))
                {
                    continue;
                }

                // Keep customers[index], and the matching child order, pointed at the body the
                // host's index names: identity resolution, the register carrier and every state
                // delta address the slot by this index.
                customers[index] = candidate;
                customers[i] = slot;
                candidate.transform.SetSiblingIndex(index);
                slot.transform.SetSiblingIndex(i);
                _customerPoolChanged.Raise();
                CoopPlugin.Log.LogInfo("npc: slot " + index + " swapped to a "
                    + (female ? "female" : "male") + " body from slot " + i + ".");
                return;
            }

            CoopPlugin.Log.LogError("npc: no free " + (female ? "female" : "male")
                + " customer body for slot " + index + "; the carrier will resolve the wrong model.");
        }

        /// <summary>A pool index that already has an incarnation must keep its body: moving it
        /// would silently reskin the customer that index already represents. Only unbound slots
        /// that are not materialized yet are interchangeable.</summary>
        private static bool HasCustomerIdentity(int index)
        {
            if (_active == null)
            {
                return false;
            }

            var key = (KindCustomer << 16) | index;
            return _active._puppets.TryGetValue(key, out var puppet) && puppet.HasIdentity;
        }

        private void BufferPendingState(in NpcSnapshot state)
        {
            _pendingStates[new NpcDeltaKey(state.Kind, state.Index, state.Identity)] = state;
        }

        /// <summary>Apply the newest transient pose buffered for an incarnation that the identity
        /// module just materialized.</summary>
        internal static void ApplyPendingState(byte kind, ushort index, int identity)
        {
            var client = _active;
            if (client == null)
            {
                return;
            }

            var key = new NpcDeltaKey(kind, index, identity);
            if (!client._pendingStates.TryGetValue(key, out var state))
            {
                return;
            }

            client._pendingStates.Remove(key);
            client.ApplyNpcState(state);
        }

        /// <summary>Apply one authoritative pose. State never creates an NPC; a pose whose
        /// incarnation is not materialized yet is retained until the identity lifecycle creates
        /// it.</summary>
        private bool ApplyNpcState(in NpcSnapshot state)
        {
            var kind = state.Kind;
            var index = state.Index;
            var identity = state.Identity;
            var key = (kind << 16) | index;

            // map host time onto the local timeline; low-pass the offset so per-packet
            // network jitter cannot corrupt snapshot spacing (snap on init / big jumps)
            var rawOffset = _now - state.HostTime;
            if (!_clockInit || Mathf.Abs(rawOffset - _clockOffset) > 1f)
            {
                _clockOffset = rawOffset;
                _clockInit = true;
            }
            else
            {
                _clockOffset += 0.1f * (rawOffset - _clockOffset);
            }

            var snap = new Snap
            {
                Pos = state.Position,
                Yaw = state.Yaw,
                Speed = state.Speed,
                Flags = (NpcFlags)state.Flags,
                Time = state.HostTime + _clockOffset,
            };

            if (kind == KindCustomer && _existing.TryGetValue(index, out var mirror)
                && mirror != null && mirror.Generation == identity)
            {
                mirror.BufHead = (mirror.BufHead + 1) & 3;
                mirror.Buf[mirror.BufHead] = snap;
                if (mirror.BufCount < 4)
                {
                    mirror.BufCount++;
                }
                mirror.Flags = (NpcFlags)state.Flags;
                if (mirror.Customer != null)
                {
                    ApplyActionTrigger(mirror.Customer.m_Anim, kind, state.ActionKind,
                        state.ActionSequence, ref mirror.GrabSequence);
                }

                // A pooled customer is the interaction carrier. Keep its visual puppet alive and
                // fed with the same snapshot even while the carrier is on screen, so handing the
                // customer back (register sale finish, trade end) reveals a ready puppet instead
                // of a hole that the client has to rebuild from scratch.
                _puppets.TryGetValue(key, out var visual);
                if (visual == null || visual.Go == null || !visual.HasIdentity
                    || visual.Identity != identity)
                {
                    // The identity lifecycle owns the body for another (or a not-yet-created)
                    // incarnation; a pose must not rebuild or relabel it.
                    mirror.PuppetReadyNotified = false;
                }
                else
                {
                    visual.Flags = (NpcFlags)state.Flags;
                    visual.BufHead = (visual.BufHead + 1) & 3;
                    visual.Buf[visual.BufHead] = snap;
                    if (visual.BufCount < 4)
                    {
                        visual.BufCount++;
                    }
                    ApplyActionTrigger(visual.Anim, kind, state.ActionKind,
                        state.ActionSequence, ref visual.GrabSequence);
                    // Register carriers render the real customer, so their puppet stays hidden
                    // (still alive and buffered) until DetachExistingCustomer shows it; trade
                    // carriers keep the puppet visible.
                    visual.Go?.SetActive(mirror.KeepPuppetVisible);
                    if (mirror.KeepPuppetVisible && visual.Go != null
                        && !mirror.PuppetReadyNotified)
                    {
                        mirror.PuppetReadyNotified = true;
                        ExistingCustomerPuppetReady?.Invoke(index, identity, mirror.Customer);
                    }
                }

                return true;
            }

            if (!_puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity
                || puppet.Identity != identity)
            {
                BufferPendingState(state);
                return false;
            }

            if (kind == KindWorker)
            {
                puppet.HoldBig = state.HoldBig;
                puppet.HoldItemType = state.HoldItemType;
                puppet.HoldBoxNetworkId = state.HoldBoxNetworkId;
                puppet.HoldBoxOpened = state.HoldBoxOpened;
            }

            puppet.BufHead = (puppet.BufHead + 1) & 3;
            puppet.Buf[puppet.BufHead] = snap;
            if (puppet.BufCount < 4)
            {
                puppet.BufCount++;
            }

            puppet.Flags = (NpcFlags)state.Flags;
            ApplyActionTrigger(puppet.Anim, kind, state.ActionKind, state.ActionSequence,
                ref puppet.GrabSequence);
            if (kind == KindCustomer && SuppressedCustomer.Contains(index) && puppet.Go != null)
            {
                puppet.Go.SetActive(false);
            }

            return true;
        }

        /// <summary>Fires an NPC action animation once per authoritative sequence change. The
        /// host increments the sequence only when the NPC actually enters a take/scan action, so
        /// re-sending the same sequence in every state packet must not re-trigger the animation -
        /// that is what left every puppet stuck holding the grab pose. A fresh or re-identified
        /// puppet adopts the first sequence without firing, so joining mid-action starts already
        /// settled instead of replaying one grab.</summary>
        private static void ApplyActionTrigger(Animator anim, byte kind, byte actionKind,
            int actionSequence, ref int appliedSequence)
        {
            if (anim == null)
            {
                return;
            }

            if (actionSequence == appliedSequence)
            {
                return;
            }

            var firstApply = appliedSequence == UnsetActionSequence;
            appliedSequence = actionSequence;
            if (firstApply)
            {
                return;
            }

            anim.SetTrigger(kind == KindWorker ? "ScanItem"
                : actionKind == 2 ? "GrabItemHigh" : "GrabItem");
        }

        /// <summary>The single customer presentation routine: applies one authoritative flag set to
        /// a customer object's animator parameters and prop children. Every customer surface goes
        /// through it - walking puppet clones, the borrowed real customer at the register, and the
        /// spawn/attach reset (flags = none is vanilla's activation presentation state, i.e. the
        /// prop block of Customer.ActivateCustomer). Vanilla's own per-state writes live inside
        /// the Update state machine the client cannot run (it would run the host's AI), so this
        /// mapping is the one irreducible sync surface. The per-frame MoveSpeed blend stays with
        /// the interpolation loops.</summary>
        private static void ApplyPresentation(Customer customer, NpcFlags flags)
        {
            if (customer.m_Anim == null)
            {
                return;
            }

            customer.m_Anim.SetBool(HashHoldingBag, (flags & NpcFlags.HoldingBag) != 0);
            customer.m_Anim.SetBool(HashHandingOverCash, (flags & NpcFlags.HandingOverCash) != 0);
            customer.m_Anim.SetBool(HashIsSitting, (flags & NpcFlags.IsSitting) != 0);
            customer.m_Anim.SetBool(HashIsPlaying, (flags & NpcFlags.IsPlaying) != 0);
            customer.m_Anim.SetBool(HashIsHoldingBox, (flags & NpcFlags.IsHoldingBox) != 0);
            customer.m_Anim.SetBool(HashIsBeingSprayed, (flags & NpcFlags.Sprayed) != 0);
            customer.m_ShoppingBagTransform?.gameObject.SetActive(
                (flags & NpcFlags.HoldingBag) != 0);
            customer.m_CustomerCash?.gameObject.SetActive(
                (flags & NpcFlags.HandingOverCash) != 0);
            customer.m_GameCardFanOut?.SetActive((flags & NpcFlags.IsPlaying) != 0);
            customer.m_GameCardSingle?.SetActive((flags & NpcFlags.IsPlaying) != 0);
            customer.m_SmellyFX?.SetActive((flags & NpcFlags.Smelly) != 0);
            customer.m_CleanFX?.SetActive((flags & NpcFlags.Cleaned) != 0);
            customer.m_ExclaimationMesh?.SetActive((flags & NpcFlags.Exclaim) != 0);
        }

        /// <summary>On a wardrobe change, re-dress the existing clone in place via the
        /// game's own Initialize() (m_HasInit routes to LoadFromJSON, which re-applies
        /// hair/apparel for the new name). Full respawn only when there is no clone yet
        /// or the male/female prefab no longer matches.</summary>
        private void Redress(Puppet puppet, string charName, Vector3 pos, bool female, byte kind,
            ushort index, int identity)
        {
            // The look belongs to the real NPC, and this is the one chokepoint every look
            // application flows through (spawn identity, join baseline, re-dress): whenever
            // a customer puppet is dressed, the real pooled customer for that index is
            // dressed in place with the same authoritative name. Borrowers (the register)
            // then reuse an already-dressed NPC instead of cloning a lookalike. A member
            // already wearing the name is left untouched; on a real change the game's own
            // setHair/setApparel destroy and replace each slot.
            if (kind == KindCustomer)
            {
                DressRealCustomer(index, charName);
            }

            ReDress(puppet, charName, pos, female, kind, index, identity);
        }

        private void ReDress(Puppet p, string charName, Vector3 pos, bool female, byte kind,
            ushort index, int identity)
        {
            var genderChanged = p.Go != null && p.Female != female;
            if (p.Go == null || p.Custom == null || genderChanged)
            {
                if (p.Go != null)
                {
                    ParkOrDestroyVisual(p);
                    p.Go = null;
                    p.CharName = "";
                    p.Identity = 0;
                    p.HasIdentity = false;
                }

                p.Kind = kind;
                Spawn(p, charName, pos, female, kind, index, identity);
                return;
            }

            if (p.CharName == charName)
            {
                // Already wearing this exact look; only the incarnation advanced. Skip the
                // JSON round-trip and hair/apparel mesh rebuild inside
                // CharacterCustomization.Initialize(), which is otherwise unconditional.
                CommitPuppetIdentity(p, identity, female);
                return;
            }

            // The look changed but not the gender: prefer swapping in a parked clone for the new
            // look over re-dressing this clone in place, which pays a JSON round-trip and a full
            // hair/apparel mesh rebuild. Only a clone built from the same kind of source is
            // interchangeable: a live-sourced body may carry an appearance mod's hierarchy that a
            // template body lacks (and vice versa), so pass the current clone's source flag. On a
            // source mismatch (or any miss) the existing in-place Initialize() path runs.
            var newKey = new VisualKey(kind, female, charName);
            PooledVisual pooled = null;
            using (Util.PerfProbe.Sample("module.npc.identity.pool"))
            {
                TryTakeVisual(newKey, p.VisualFromLive, out pooled);
            }
            if (pooled != null)
            {
                CoopPlugin.Log.LogDebug("[npc-pool] take " + newKey
                    + " count=" + _visualPoolOrder.Count);
                ParkOrDestroyVisual(p);
                p.Female = female;
                ApplyVisual(p, pooled, charName, pos);
                ResetPuppetIncarnationState(p, pos);
                SeedWorkerUi(p, index);
                CommitSpawnTail(p, charName, pos, identity, female);
                return;
            }

            CharacterClone.PrepareForDressing(p.Custom, null, charName, "npc puppet re-dress");
            p.CharName = charName;
            p.Female = female;
            p.Go.name = "CoopNpc_" + charName;
            CommitPuppetIdentity(p, identity, female);
        }

        /// <summary>Client only. Interpolate puppets; despawn ones the host stopped sending.</summary>
        public void TickPuppets(float dt, bool inGame)
        {
            // Freeze the local clock while out of game so interpolation resumes from the
            // same local timeline after the scene becomes active again.
            if (!inGame || dt <= 0f)
            {
                return;
            }

            _now += dt;

            var renderTime = _now - InterpDelay;
            // frame-rate-independent blend factors (never dt*k, which overshoots at low fps)
            var posBlend = 1f - Mathf.Exp(-18f * dt);
            var yawBlend = 1f - Mathf.Exp(-14f * dt);
            var speedBlend = 1f - Mathf.Exp(-8f * dt);

            foreach (var kv in _puppets)
            {
                var p = kv.Value;
                if (kv.Key < 65536 && SuppressedCustomer.Contains(kv.Key)
                    && (!_existing.TryGetValue(kv.Key, out var existingVisual) || !existingVisual.KeepPuppetVisible)
                    && p.Go != null)
                {
                    p.Go.SetActive(false);
                }
                if (p.Go == null || p.BufCount == 0)
                {
                    continue;
                }

                Sample(p, renderTime, out var target, out var targetYaw);

                var t = p.Go.transform;
                var snap = (t.position - target).sqrMagnitude > 25f; // teleports (spawn, seat snap)
                var newPos = snap ? target : Vector3.Lerp(t.position, target, posBlend);
                t.position = newPos;
                p.RenderYaw = snap ? targetYaw : Mathf.LerpAngle(p.RenderYaw, targetYaw, yawBlend);
                t.rotation = Quaternion.Euler(0f, p.RenderYaw, 0f);

                // drive the walk cycle from what the puppet actually did this frame, not
                // the host's speed - that is what keeps feet and translation in sync
                var rendered = snap ? 0f : Mathf.Min((newPos - p.PrevRenderedPos).magnitude / dt, 10f);
                p.PrevRenderedPos = newPos;
                p.AnimSpeed = Mathf.Lerp(p.AnimSpeed, rendered, speedBlend);
                if (p.AnimSpeed < 0.05f)
                {
                    p.AnimSpeed = 0f;
                }

                if (p.Anim != null)
                {
                    if (float.IsNaN(p.AppliedAnimSpeed)
                        || Mathf.Abs(p.AppliedAnimSpeed - p.AnimSpeed) > 0.01f)
                    {
                        p.Anim.SetFloat(HashMoveSpeed, p.AnimSpeed);
                        p.AppliedAnimSpeed = p.AnimSpeed;
                    }
                }
                if ((int)p.Flags != p.AppliedFlags)
                {
                    if (p.Customer != null)
                    {
                        // Customer clones present through the same single routine as the real
                        // customer - the routine drives the clone's own animator and prop
                        // children, which is what the p.Bag/p.Cash captures reference.
                        ApplyPresentation(p.Customer, p.Flags);
                    }
                    else
                    {
                        // Worker clones have no Customer component; their flag surface is the
                        // animator parameters and the trade prompt.
                        if (p.Anim != null)
                        {
                            p.Anim.SetBool(HashHoldingBag, (p.Flags & NpcFlags.HoldingBag) != 0);
                            p.Anim.SetBool(HashHandingOverCash,
                                (p.Flags & NpcFlags.HandingOverCash) != 0);
                            p.Anim.SetBool(HashIsSitting, (p.Flags & NpcFlags.IsSitting) != 0);
                            p.Anim.SetBool(HashIsPlaying, (p.Flags & NpcFlags.IsPlaying) != 0);
                            p.Anim.SetBool(HashIsHoldingBox,
                                (p.Flags & NpcFlags.IsHoldingBox) != 0);
                            p.Anim.SetBool(HashIsBeingSprayed, (p.Flags & NpcFlags.Sprayed) != 0);
                        }

                        Toggle(p.Exclaim, (p.Flags & NpcFlags.Exclaim) != 0);
                    }

                    p.AppliedFlags = (int)p.Flags;
                }
                if (p.Kind == KindWorker)
                {
                    if ((p.Flags & NpcFlags.IsHoldingBox) != 0)
                    {
                        SetWorkerBoxVisual((int)(kv.Key & 0xffff), true, p.HoldBig, p.HoldItemType,
                            p.HoldBoxNetworkId, p.HoldBoxOpened);
                    }
                    else
                    {
                        var releasedBoxId = p.AppliedHeldBoxNetworkId;
                        ReleaseWorkerBoxProp(p);
                        if (releasedBoxId != Guid.Empty)
                        {
                            WorldClientBehaviour.RestoreWorkerDroppedBox(releasedBoxId);
                        }
                    }
                }
            }
            foreach (var kv in _existing)
            {
                var mirror = kv.Value;
                if (mirror.Customer == null || mirror.BufCount == 0)
                {
                    continue;
                }

                SampleExisting(mirror, renderTime, out var target, out var targetYaw);
                var existingPosBlend = 1f - Mathf.Exp(-18f * dt);
                var existingYawBlend = 1f - Mathf.Exp(-14f * dt);
                var existingSpeedBlend = 1f - Mathf.Exp(-8f * dt);
                var transform = mirror.Customer.transform;
                var snap = (transform.position - target).sqrMagnitude > 25f;
                var newPos = snap ? target : Vector3.Lerp(transform.position, target, existingPosBlend);
                transform.position = newPos;
                mirror.RenderYaw = snap ? targetYaw : Mathf.LerpAngle(mirror.RenderYaw, targetYaw, existingYawBlend);
                transform.rotation = Quaternion.Euler(0f, mirror.RenderYaw, 0f);
                var rendered = snap ? 0f : Mathf.Min((newPos - mirror.PrevRenderedPos).magnitude / dt, 10f);
                mirror.PrevRenderedPos = newPos;
                mirror.AnimSpeed = Mathf.Lerp(mirror.AnimSpeed, rendered, existingSpeedBlend);
                if (mirror.AnimSpeed < 0.05f)
                {
                    mirror.AnimSpeed = 0f;
                }

                if (mirror.Customer.m_Anim != null
                    && (float.IsNaN(mirror.AppliedAnimSpeed)
                        || Mathf.Abs(mirror.AppliedAnimSpeed - mirror.AnimSpeed) > 0.01f))
                {
                    mirror.Customer.m_Anim.SetFloat(HashMoveSpeed, mirror.AnimSpeed);
                    mirror.AppliedAnimSpeed = mirror.AnimSpeed;
                }
                if ((int)mirror.Flags != mirror.AppliedFlags)
                {
                    ApplyPresentation(mirror.Customer, mirror.Flags);
                    mirror.AppliedFlags = (int)mirror.Flags;
                }
            }
        }

        private static void SampleExisting(ExistingCustomer mirror, float renderTime,
            out Vector3 target, out float targetYaw)
        {
            var newest = mirror.Buf[mirror.BufHead];
            if (newest.Time <= renderTime)
            {
                var velocity = Vector3.zero;
                if (mirror.BufCount >= 2)
                {
                    var previous = mirror.Buf[(mirror.BufHead + 3) & 3];
                    var span = newest.Time - previous.Time;
                    if (span > 0.001f)
                    {
                        velocity = (newest.Pos - previous.Pos) / span;
                        velocity.y = 0f;
                        velocity = Vector3.ClampMagnitude(velocity, 5f);
                    }
                }
                var extrapolation = Mathf.Min(renderTime - newest.Time, 0.25f);
                velocity *= Mathf.Exp(-3f * extrapolation);
                target = newest.Pos + velocity * extrapolation;
                targetYaw = newest.Yaw;
                return;
            }
            var newer = newest;
            for (var i = 1; i < mirror.BufCount; i++)
            {
                var older = mirror.Buf[(mirror.BufHead - i + 4) & 3];
                if (older.Time <= renderTime)
                {
                    var span = newer.Time - older.Time;
                    var blend = span > 0.0001f ? (renderTime - older.Time) / span : 1f;
                    target = Vector3.Lerp(older.Pos, newer.Pos, blend);
                    targetYaw = Mathf.LerpAngle(older.Yaw, newer.Yaw, blend);
                    return;
                }
                newer = older;
            }
            target = newer.Pos;
            targetYaw = newer.Yaw;
        }

        /// <summary>Interpolate between the two snapshots bracketing renderTime. If the
        /// buffer is dry (newest snapshot older than renderTime) extrapolate from the
        /// newest, capped at 250 ms with decaying velocity so a stopped stream eases to a
        /// halt instead of gliding off.</summary>
        private static void Sample(Puppet p, float renderTime, out Vector3 target, out float targetYaw)
        {
            var newest = p.Buf[p.BufHead];
            if (newest.Time <= renderTime)
            {
                var v = Vector3.zero;
                if (p.BufCount >= 2)
                {
                    var prev = p.Buf[(p.BufHead + 3) & 3];
                    var span = newest.Time - prev.Time;
                    if (span > 0.001f)
                    {
                        v = (newest.Pos - prev.Pos) / span;
                        v.y = 0f;
                        v = Vector3.ClampMagnitude(v, 5f);
                    }
                }
                var ex = Mathf.Min(renderTime - newest.Time, 0.25f);
                v *= Mathf.Exp(-3f * ex);
                target = newest.Pos + v * ex;
                targetYaw = newest.Yaw;
                return;
            }

            // scan newest -> oldest for the first snapshot at or before renderTime
            var newer = newest;
            for (var k = 1; k < p.BufCount; k++)
            {
                var older = p.Buf[(p.BufHead - k + 4) & 3];
                if (older.Time <= renderTime)
                {
                    var span = newer.Time - older.Time;
                    var u = span > 0.0001f ? (renderTime - older.Time) / span : 1f;
                    target = Vector3.Lerp(older.Pos, newer.Pos, u);
                    targetYaw = Mathf.LerpAngle(older.Yaw, newer.Yaw, u);
                    return;
                }
                newer = older;
            }
            // renderTime predates the whole buffer (fresh puppet): hold the oldest snapshot
            target = newer.Pos;
            targetYaw = newer.Yaw;
        }

        private static void Toggle(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on)
            {
                go.SetActive(on);
            }
        }

        private static void EnsureWorkerCollections(Worker worker)
        {
            if (worker == null)
            {
                return;
            }

            // Worker.InitializeCharacter also initializes the live worker's AI-facing state.
            // A puppet must not run that routine against an already-initialized scene worker, so
            // only rebuild the small data collections that the interaction screen indexes.
            while (worker.m_TaskLevel.Count < 100)
            {
                worker.m_TaskLevel.Add(0);
            }

            while (worker.m_ExpList.Count < 100)
            {
                worker.m_ExpList.Add(0);
            }

            EnsureCardPackList(worker);
        }

        /// <summary>Client: the live scene worker occupying a synchronized list slot, whose visual
        /// hierarchy an appearance mod may already have replaced. Null when the slot has no worker
        /// yet, or when its prefab gender no longer matches the snapshot (the manager template is
        /// then the safe gender-correct source).</summary>
        private static Worker FindLiveWorker(ushort index, bool female)
        {
            var workers = NpcInterop.Workers;
            if (workers == null || index >= workers.Count)
            {
                return null;
            }

            var worker = workers[index];
            return worker != null && worker.m_IsFemale == female ? worker : null;
        }

        private void Spawn(Puppet p, string charName, Vector3 pos, bool femaleHint, byte kind,
            ushort index, int identity)
        {
            var female = femaleHint;
            p.Female = female;
            p.Kind = kind;

            GameObject prefabObject;
            CC.CharacterCustomization sourceCustom = null;
            Transform sourceRoot = null;
            var clonedLive = false;
            using (Util.PerfProbe.Sample("module.npc.identity.spawn.source"))
            {
                if (kind == KindWorker)
                {
                    var workerManager = SceneRef<WorkerManager>.Get();
                    // Appearance mods (Sexy Workers and friends) replace the visual hierarchy on the
                    // live Worker objects in WorkerManager.m_WorkerList after Start and never touch
                    // the manager's template prefabs. Clone the live slot so the replacement model,
                    // skeleton, animator, sockets and bones survive; the template is only the
                    // fallback when this slot has no live worker yet (early join / mod not installed).
                    var liveWorker = FindLiveWorker(index, female);
                    if (liveWorker != null)
                    {
                        prefabObject = liveWorker.gameObject;
                        sourceCustom = liveWorker.m_CharacterCustom;
                        sourceRoot = liveWorker.transform;
                        clonedLive = true;
                    }
                    else
                    {
                        var prefab = female ? workerManager.m_WorkerFemalePrefab : workerManager.m_WorkerPrefab;
                        prefabObject = prefab.gameObject;
                        sourceCustom = prefab.m_CharacterCustom;
                        sourceRoot = prefab.transform;
                    }
                }
                else
                {
                    var customerManager = SceneRef<CustomerManager>.Get();
                    // A live pool member carries the wardrobe tables and animator the preset was
                    // authored against; the template can expose fewer slots, which makes
                    // CharacterCustomization dress throw on some game builds. Prefer one that has
                    // never dressed; an already-dressed member still works because the clone
                    // preparation rebuilds its generated collections.
                    if (CharacterClone.TryFindPooledCustomer(customerManager, female,
                        out var liveCustomer))
                    {
                        prefabObject = liveCustomer.gameObject;
                        sourceCustom = liveCustomer.m_CharacterCustom;
                        sourceRoot = liveCustomer.transform;
                        clonedLive = true;
                    }
                    else
                    {
                        var prefab = female ? customerManager.m_CustomerFemalePrefab
                            : customerManager.m_CustomerPrefab;
                        prefabObject = prefab.gameObject;
                        sourceCustom = prefab?.m_CharacterCustom;
                        sourceRoot = prefab?.transform;
                    }
                }
            }

            // A look once built is reused forever: adopt a parked clone for this exact look and
            // skip Instantiate + CharacterCustomization.Initialize entirely. Only a clone built
            // from the same kind of source is interchangeable: a live-sourced clone may carry an
            // appearance mod's hierarchy that the template clone lacks, so the take is filtered by
            // the source computed above. On a miss the existing construction path below runs
            // unchanged with those same source variables.
            PooledVisual pooled = null;
            if (!string.IsNullOrEmpty(charName))
            {
                using (Util.PerfProbe.Sample("module.npc.identity.pool"))
                {
                    TryTakeVisual(new VisualKey(kind, female, charName), clonedLive, out pooled);
                }
            }

            if (pooled != null)
            {
                CoopPlugin.Log.LogDebug("[npc-pool] take " + pooled.Key
                    + " count=" + _visualPoolOrder.Count);
                ApplyVisual(p, pooled, charName, pos);
                ResetPuppetIncarnationState(p, pos);
                SeedWorkerUi(p, index);
                CommitSpawnTail(p, charName, pos, identity, female);
                return;
            }

            GameObject clone;
            int cloneInstanceId;
            using (Util.PerfProbe.Sample("module.npc.identity.spawn.instantiate"))
            {
                var holder = new GameObject("CoopNpcHolder_tmp");
                holder.SetActive(false);
                clone = Instantiate(prefabObject, holder.transform);
                cloneInstanceId = clone.GetInstanceID();
                RegisterPuppetRoot(clone);
                clone.transform.SetParent(null, worldPositionStays: false);
                clone.transform.position = pos;
                Destroy(holder);
            }

            var cust = clone.GetComponent<Customer>();
            var worker = clone.GetComponent<Worker>();
            if (cust != null)
            {
                cust.m_IsFemale = female;
            }

            if (worker != null)
            {
                // WorkerManager.ActivateWorker is deliberately not used on clients: it starts
                // the real AI loop and changes worker counts. Keep the component only for the
                // interaction screen and rebuild its UI collections on this clone.
                worker.m_IsFemale = female;
                worker.m_WorkerIndex = index;
            }
            p.Custom = cust != null ? cust.m_CharacterCustom
                : worker?.m_CharacterCustom;
            if (p.Custom != null)
            {
                using (Util.PerfProbe.Sample("module.npc.identity.spawn.dress"))
                {
                    if (clonedLive && kind == KindWorker)
                    {
                        // A live worker is already the employee's dressed look, and an appearance mod
                        // may have replaced its entire visual hierarchy. Re-running Initialize() would
                        // rebuild the now-hidden vanilla wardrobe (and can index the wrong gender's
                        // wardrobe tables), so only mirror the authoritative name on the inert
                        // component. Its look is complete and authoritative as-is.
                        //
                        // ReDress can still Initialize() this clone later when its look changes, so
                        // give it the same collection contract now: point its private lists at the
                        // inherited generated children instead of leaving them empty.
                        CharacterClone.RebuildInheritedCollections(p.Custom, sourceCustom, sourceRoot,
                            clone.transform);
                        p.Custom.CharacterName = charName;
                    }
                    else
                    {
                        // Template or live customer: make the clone a valid wardrobe template
                        // (rebuilding inherited generated collections when the source was already
                        // dressed) before the game dresses it.
                        CharacterClone.PrepareForDressing(p.Custom, sourceCustom, sourceRoot,
                            clone.transform, charName, "npc puppet");
                    }
                }
            }

            if (worker != null)
            {
                EnsureWorkerCollections(worker);
                MiEvaluateWorkerAttribute?.Invoke(worker, null);
                MiEvaluateSkillLevel?.Invoke(worker, null);
            }

            // Start() may call Initialize() when a source prefab has Autoload set. The
            // puppet has already been dressed explicitly; suppress that second lifecycle
            // pass on the clone before it is activated.
            p.Custom.Autoload = false;

            // capture prop children BEFORE stripping the Customer script
            if (cust != null)
            {
                p.Customer = cust;
                p.Bag = cust.m_ShoppingBagTransform?.gameObject;
                p.Cash = cust.m_CustomerCash?.gameObject;
                p.CardFan = cust.m_GameCardFanOut;
                p.CardSingle = cust.m_GameCardSingle;
                p.Smelly = cust.m_SmellyFX; // plain child FX object, survives the strip
                p.Clean = cust.m_CleanFX; // clean puff FX shown after a spray
                p.Exclaim = cust.m_ExclaimationMesh; // the "!" trade prompt, driven by flags below
                // Vanilla activation presentation (flags = none): everything stays off until the
                // authoritative snapshots say otherwise.
                ApplyPresentation(cust, (NpcFlags)0);
                cust.m_InteractCollider?.SetActive(false);
            }

            else if (worker != null)
            {
                // Worker appearance mods replace the visual hierarchy and animator on
                // the live Worker component; never leave its interaction marker visible.
                p.Exclaim = worker.m_ExclaimationMesh;
                p.HoldBox = worker.m_HoldBoxLoc;
                p.Exclaim?.SetActive(false);
            }

            // CharacterCustomization must survive the strip so wardrobe changes can
            // re-dress in place instead of Destroy+Instantiate churn
            using (Util.PerfProbe.Sample("module.npc.identity.spawn.strip"))
            {
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null)
                    {
                        continue;
                    }

                    var tn = mb.GetType().Name;
                    if (tn == "Worker" || tn == "Customer" || tn == "WorkerCollider"
                        || tn == "NavMeshAgent" || tn == "NavMeshObstacle" || tn == "Seeker"
                        || tn == "FunnelModifier" || tn == "InteractableObject")
                    {
                        var behaviour = mb as Behaviour;
                        if (behaviour != null)
                        {
                            behaviour.enabled = false;
                        }
                    }
                }
                foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                {
                    col.enabled = false;
                }

                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.isKinematic = true;
                    rb.detectCollisions = false;
                }
            }

            // Keep exactly the vanilla worker interaction surface on the puppet. The
            // Worker component remains behaviour-disabled, but WorkerCollider.OnMousePress
            // can still open the original WorkerInteractUIScreen without enabling AI.
            if (worker != null && worker.m_WorkerCollider != null)
            {
                worker.m_WorkerCollider.enabled = true;
                foreach (var col in worker.m_WorkerCollider.GetComponents<Collider>())
                {
                    col.enabled = true;
                }
            }

            // A staff snapshot can arrive before this puppet is spawned. Seed the
            // vanilla UI model from the already-downloaded save immediately; later
            // StaffState packets continue to refresh it authoritatively.
            using (Util.PerfProbe.Sample("module.npc.identity.spawn.finish"))
            {
                clone.name = "CoopNpc_" + charName;
                p.VisualFromLive = clonedLive;
                p.Go = clone;
                p.RootInstanceId = cloneInstanceId;
                // This must run AFTER p.Go is assigned: RefreshWorkerUi looks the puppet up
                // through that field, so seeding before it is a silent no-op and the puppet
                // keeps the template's all-enabled pack-refill defaults until an unrelated
                // StaffState change happens to arrive.
                SeedWorkerUi(p, index);

                // Prefer the Animator reference owned by the cloned Worker. Mods may leave
                // the original Animator disabled beside a replacement Animator in the same
                // hierarchy; GetComponentInChildren alone can select the wrong one.
                p.Anim = worker != null && worker.m_Anim != null
                    ? worker.m_Anim : clone.GetComponentInChildren<Animator>(true);
                CommitSpawnTail(p, charName, pos, identity, female);
            }
        }

        /// <summary>Seed the inert Worker component's UI model from the already-downloaded staff
        /// save. A staff snapshot can arrive before the puppet is spawned; later StaffState
        /// packets continue to refresh it authoritatively. RefreshWorkerUi resolves the puppet via
        /// _puppets, so the slot must already carry this index and p.Go must be assigned.</summary>
        private void SeedWorkerUi(Puppet p, ushort index)
        {
            var saved = CPlayerData.m_WorkerSaveDataList;
            if (p.Kind == KindWorker && saved != null && index < saved.Count)
            {
                RefreshWorkerUi(index, saved[index]);
            }
        }

        /// <summary>Shared commit of the per-spawn fields. Used by the construction path and by
        /// pool adoption so a reused clone starts exactly like a freshly built one. The display
        /// name is committed only when the clone actually wears the look (the caller has already
        /// set Custom.CharacterName); leaving it unset on a failed dress makes the next
        /// name-bearing packet ReDress instead of accepting a stale model.</summary>
        private void CommitSpawnTail(Puppet p, string charName, Vector3 pos, int identity,
            bool female)
        {
            p.CharName = charName;
            p.Female = female;
            CommitPuppetIdentity(p, identity, female);
            p.PrevRenderedPos = pos;
            p.RenderYaw = 0f;
            p.AnimSpeed = 0f;
            p.AppliedFlags = -1;
            p.Go.SetActive(true);
        }

    }
}
