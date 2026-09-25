using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
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
        internal static event Action CustomerManagerReady;
        internal static event Action WorkerManagerReady;
        internal static event Action CustomerPoolChanged;
        internal static event Action<int> CustomerCapacityChanged;
        internal static event Action<int, int, Customer> ExistingCustomerChanged;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        public int PuppetCount => _puppets.Count;

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }
            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("com.zwhit.cardshopcoop.npc.client");
            _harmony.CreateClassProcessor(typeof(CustomerManagerPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerActivationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerDeactivationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerManagerPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerManagerStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerManagerStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerActivationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerUpdatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomizationPatch)).Patch();
            SuppressInitialPopulation(SceneRef<CustomerManager>.Get());
            SignalCustomerManagerReady();
            SignalWorkerManagerReady();
        }

        private void Update()
        {
            using (CardShopCoop.Util.PerfProbe.Sample("module.npc.client-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            var inGame = _context.InGame();
            var delta = Time.deltaTime;
            TickPuppets(delta, inGame);
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

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Reset();
            _context.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
        }

        private void OnDestroy() => Shutdown();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (!_shutdown && ReferenceEquals(_active, this))
            {
                Reset();
            }
        }

        [MessageHandler(typeof(NpcStateDeltaMessage))]
        private void HandleState(MessageContext context, NpcStateDeltaMessage message)
        {
            if (!_context.InGame() || !IsNpcSceneReady())
            {
                BufferPendingState(message);
                return;
            }

            ApplyStateDelta(message);
        }

        [MessageHandler(typeof(NpcIdentityDeltaMessage))]
        private void HandleIdentity(MessageContext context, NpcIdentityDeltaMessage message)
        {
            if (!_context.InGame() || !IsNpcSceneReady())
            {
                BufferPendingIdentity(message);
                return;
            }

            ApplyIdentity(message);
            _pendingIdentities.Remove((message.Kind << 16) | message.Index);
            ApplyPendingStates(message.Kind, message.Index, message.Identity);
        }

        [MessageHandler(typeof(NpcBaselineMessage))]
        private void HandleBaseline(MessageContext context, NpcBaselineMessage message)
        {
            _pendingBaseline = message;
            CoopPlugin.Log.LogInfo("[npc] baseline received entries="
                + (message == null || message.Entries == null ? -1 : message.Entries.Count)
                + " capacity=" + (message == null ? -1 : message.CustomerCapacity)
                + " inGame=" + _context.InGame() + " sceneReady=" + IsNpcSceneReady());
            ApplyPendingBaseline();
        }

        [MessageHandler(typeof(NpcSpeechMessage))]
        private void HandleSpeech(MessageContext context, NpcSpeechMessage message)
        {
            ShowSpeech(message, _context.InGame());
        }

        [MessageHandler(typeof(NpcMoneyPopupMessage))]
        private void HandleMoney(MessageContext context, NpcMoneyPopupMessage message)
        {
            ShowMoneyPopup(message, _context.InGame());
        }

        private static bool IsNpcSceneReady()
        {
            return NpcInterop.CustomerManager?.GetCustomerList() != null
                && NpcInterop.WorkerManager != null && NpcInterop.Workers != null;
        }

        private void ApplyIdentity(NpcIdentityDeltaMessage message)
        {
            // Customers name themselves "Female<model>" / "Male<model>"; workers rely on the
            // explicit flag. Accept either source so a missing/incorrect flag cannot spawn a
            // female NPC from the male prefab.
            var female = message.Female || HasFemaleNamePrefix(message.CharName);
            if (message.Kind == KindCustomer)
            {
                EnsureCustomerCapacity(message.Index, female);
            }

            var key = (message.Kind << 16) | message.Index;
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
            var reusedIncarnation = puppet.HasIdentity && puppet.Identity != message.Identity
                && SameDressedLook(puppet, message.Kind, female, message.CharName);

            if (puppet.HasIdentity && puppet.Identity != message.Identity && !reusedIncarnation)
            {
                ClearPendingIdentity(puppet);
                DestroyPuppetObject(puppet);

                puppet.Go = null;
                puppet.Custom = null;
                puppet.CharName = "";
                puppet.Identity = 0;
                puppet.HasIdentity = false;
                puppet.BufCount = 0;
                puppet.GrabSequence = UnsetActionSequence;
            }

            puppet.Kind = message.Kind;

            if (reusedIncarnation)
            {
                ResetPuppetForIncarnation(puppet, message);
            }

            SetPendingIdentity(puppet, message.Identity, female);
            Redress(puppet, message.CharName, message.Position, female,
                message.Kind, message.Index);
        }

        /// <summary>Customers encode their gender in the model name ("Female3"/"Male7"); the
        /// workforce does not, so it relies on the explicit flag.</summary>
        private static bool HasFemaleNamePrefix(string charName)
            => !string.IsNullOrEmpty(charName)
                && charName.StartsWith("Female", StringComparison.Ordinal);

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
        private void ResetPuppetForIncarnation(Puppet p, NpcIdentityDeltaMessage message)
        {
            ReleaseWorkerBoxProp(p);
            p.BufHead = 0;
            p.BufCount = 0;
            p.Flags = default;
            p.AppliedFlags = -1;
            p.AppliedAnimSpeed = float.NaN;
            p.AnimSpeed = 0f;
            p.RenderYaw = 0f;
            p.PrevRenderedPos = message.Position;
            p.HoldBig = false;
            p.HoldItemType = EItemType.None;
            p.HoldBoxNetworkId = 0;
            p.HoldBoxOpened = false;
            p.AppliedHeldBoxNetworkId = 0;
            p.GrabSequence = UnsetActionSequence;
            if (p.Go != null)
            {
                p.Go.transform.position = message.Position;
                p.Go.SetActive(!IsPuppetSuppressed(message.Kind, message.Index));
            }
        }

        /// <summary>Whether a puppet for this entity must stay hidden because the register or
        /// trade carrier renders the real pooled customer instead of the clone.</summary>
        private bool IsPuppetSuppressed(byte kind, ushort index)
        {
            return kind == KindCustomer && SuppressedCustomer.Contains(index)
                && (!_existing.TryGetValue(index, out var existing) || !existing.KeepPuppetVisible);
        }

        private void BufferPendingIdentity(NpcIdentityDeltaMessage message)
        {
            var key = (message.Kind << 16) | message.Index;
            _pendingIdentities[key] = message;
        }

        private static void ClearPendingIdentity(Puppet puppet)
        {
            puppet.PendingIdentity = 0;
        }

        private static void SetPendingIdentity(Puppet puppet, int identity, bool female)
        {
            puppet.PendingIdentity = identity;
            puppet.PendingFemale = female;
        }

        private static void CommitIdentity(Puppet puppet)
        {
            puppet.Identity = puppet.PendingIdentity;
            puppet.HasIdentity = true;
            puppet.Female = puppet.PendingFemale;
            puppet.PendingIdentity = 0;
        }

        private void SignalCustomerManagerReady()
        {
            if (NpcInterop.CustomerManager?.GetCustomerList() == null)
            {
                return;
            }

            CustomerManagerReady?.Invoke();
            ApplyPendingBaseline();
            ApplyPendingIdentities();
            CustomerPoolChanged?.Invoke();
        }

        private void SignalWorkerManagerReady()
        {
            if (NpcInterop.WorkerManager == null || NpcInterop.Workers == null)
            {
                return;
            }

            WorkerManagerReady?.Invoke();
            ApplyPendingBaseline();
            ApplyPendingIdentities();

        }

        private void ApplyPendingBaseline()
        {
            if (_pendingBaseline == null || !_context.InGame() || !IsNpcSceneReady())
            {
                return;
            }

            var baseline = _pendingBaseline;
            CoopPlugin.Log.LogInfo("[npc] baseline applying entries=" + baseline.Entries.Count
                + " capacity=" + baseline.CustomerCapacity
                + " females=" + (baseline.CustomerFemales == null ? -1
                    : baseline.CustomerFemales.Count));
            EnsureCustomerCapacity(baseline.CustomerCapacity, baseline.CustomerFemales);

            var seen = new HashSet<int>();
            for (var i = 0; i < baseline.Entries.Count; i++)
            {
                var entry = baseline.Entries[i];
                seen.Add((entry.Kind << 16) | entry.Index);
            }

            RemovePuppetsAbsentFromBaseline(seen);
            foreach (var puppet in _puppets.Values)
            {
                puppet.BufCount = 0;
            }
            foreach (var mirror in _existing.Values)
            {
                mirror.BufCount = 0;
            }
            ApplyEntries(baseline.HostTime, baseline.Entries);
            _pendingBaseline = null;
            ApplyPendingIdentities();
            ApplyPendingStates();
            CoopPlugin.Log.LogInfo("[npc] baseline applied puppets=" + _puppets.Count
                + " mirrors=" + _existing.Count);
        }

        private void RemovePuppetsAbsentFromBaseline(HashSet<int> seen)
        {
            var stale = new List<int>();
            foreach (var pair in _puppets)
            {
                if (seen.Contains(pair.Key))
                {
                    continue;
                }

                ClearPendingIdentity(pair.Value);
                ReleaseWorkerBoxProp(pair.Value);
                DestroyPuppetObject(pair.Value);
                _pendingIdentities.Remove(pair.Key);
                stale.Add(pair.Key);
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _puppets.Remove(stale[i]);
            }
        }

        private void ApplyPendingIdentities()
        {
            if (!_context.InGame() || !IsNpcSceneReady() || _pendingIdentities.Count == 0)
            {
                return;
            }

            var pending = new List<NpcIdentityDeltaMessage>(_pendingIdentities.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                var message = pending[i];
                ApplyIdentity(message);
                var key = (message.Kind << 16) | message.Index;
                _pendingIdentities.Remove(key);
                ApplyPendingStates(message.Kind, message.Index, message.Identity);
            }
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

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        private static class CustomerActivationPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                _active?.TrackNativeRoot(__instance?.gameObject);
                CustomerPoolChanged?.Invoke();
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

        [HarmonyPatch(typeof(CC.CharacterCustomization), "Initialize")]
        private static class CustomizationPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || _active._shutdown || _active._dressing)
                {
                    return;
                }

                _active.ApplyPendingBaseline();
                _active.ApplyPendingIdentities();
            }
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
        private static readonly FieldInfo FiCustomizationHairObjects =
            AccessTools.Field(typeof(CC.CharacterCustomization), "HairObjects");
        private static readonly FieldInfo FiCustomizationApparelObjects =
            AccessTools.Field(typeof(CC.CharacterCustomization), "ApparelObjects");

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
                DeactivateCustomerRoot(mirror == null ? null : mirror.Customer);
            }
            _existing.Clear();
            ClearPuppets();
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

        private void DestroyPuppetObject(GameObject root, int rootInstanceId)
        {
            UnregisterPuppetRoot(rootInstanceId);
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
            long boxNetworkId, bool opened)
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
            if (boxNetworkId > 0)
            {
                if (boxNetworkId != p.AppliedHeldBoxNetworkId)
                {
                    p.AppliedHeldBoxNetworkId = boxNetworkId;
                    WorldClientBehaviour.ApplyWorkerHeldBox(boxNetworkId);
                }
            }
            else
            {
                p.AppliedHeldBoxNetworkId = 0;
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
            // Unity can destroy the native object while retaining this managed wrapper. Keep the
            // id captured while the root was alive so cleanup never has to call GetInstanceID()
            // on a fake-null wrapper.
            public int RootInstanceId;
            public Animator Anim;
            public CC.CharacterCustomization Custom;
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
            public long HoldBoxNetworkId;
            public bool HoldBoxOpened;
            public long AppliedHeldBoxNetworkId;
            public bool HoldBig;
            public EItemType HoldItemType;
            public int PendingIdentity;
            public bool PendingFemale;
            // Last ActionSequence applied to this puppet's animator. int.MinValue means the
            // first state packet for the incarnation adopts the sequence without firing.
            public int GrabSequence = UnsetActionSequence;
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
            p.AppliedHeldBoxNetworkId = 0;
            if (ReferenceEquals(prop, null) || prop == null)
            {
                return;
            }

            Destroy(prop);
        }

        private readonly Dictionary<int, Puppet> _puppets = new();
        private NpcBaselineMessage _pendingBaseline;
        private readonly Dictionary<int, NpcIdentityDeltaMessage> _pendingIdentities = new();
        private readonly Dictionary<NpcDeltaKey, NpcStateDeltaMessage> _pendingStates = new();
        private bool _dressing;
        private float _now;
        private float _clockOffset;
        private bool _clockInit;

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
            // Also clear ids that no longer have a Puppet wrapper (for example, a failed spawn
            // destroyed by Unity between two lifecycle callbacks).
            _puppetRoots.Clear();
            _pendingBaseline = null;
            _pendingIdentities.Clear();
            _pendingStates.Clear();
            _dressing = false;
            SuppressedCustomer.Clear();
            _clockInit = false;
        }

        private Transform ResolveCustomerAnchor(ushort index, int identity)
        {
            var key = (KindCustomer << 16) | index;
            if (_existing.TryGetValue(index, out var existing)
                && existing.Generation == identity && existing.Customer != null)
            {
                return existing.Customer.transform;
            }

            if (_puppets.TryGetValue(key, out var puppet)
                && puppet.HasIdentity && puppet.Identity == identity && puppet.Go != null)
            {
                return puppet.Go.transform;
            }

            return null;
        }

        /// <summary>Client-only: show a host-selected customer speech bubble over the
        /// corresponding visible representation. Missing puppets are intentionally ignored;
        /// speech is cosmetic and should not keep stale references alive.</summary>
        public void ShowSpeech(NpcSpeechMessage message, bool inGame)
        {
            if (!inGame)
            {
                return;
            }

            var anchor = ResolveCustomerAnchor(message.Index, message.Identity);
            if (anchor == null)
            {
                return;
            }

            var spawner = SceneRef<PricePopupSpawner>.Get();
            if (spawner == null)
            {
                return;
            }

            spawner.ShowTextPopup(message.Text, message.OffsetUp, anchor);
        }

        /// <summary>Mirrors the host's green add-money popup on the matching puppet or
        /// mirror.</summary>
        public void ShowMoneyPopup(NpcMoneyPopupMessage message, bool inGame)
        {
            if (!inGame)
            {
                return;
            }

            var anchor = ResolveCustomerAnchor(message.Index, message.Identity);
            if (anchor == null)
            {
                return;
            }

            var spawner = SceneRef<PricePopupSpawner>.Get();
            if (spawner == null)
            {
                return;
            }

            spawner.ShowPricePopup(message.Amount, message.OffsetUp, anchor);
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
            CustomerPoolChanged?.Invoke();
            ExistingCustomerChanged?.Invoke(index, existing?.Generation ?? 0, customer);
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
                puppet.Go?.SetActive(!keepPuppetVisible);
            }
            CustomerPoolChanged?.Invoke();
            ExistingCustomerChanged?.Invoke(index, generation, customer);
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
            if (_active._pendingIdentities.TryGetValue(key, out var pendingIdentity))
            {
                generation = pendingIdentity.Identity;
                return generation > 0;
            }

            if (!_active._puppets.TryGetValue(key, out var puppet) || !puppet.HasIdentity)
            {
                return false;
            }

            generation = puppet.Identity;
            return generation > 0;
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
        /// customer beside its synchronized puppet.</summary>
        private static void EnsureCustomerCapacity(int index, bool female)
        {
            var manager = NpcInterop.CustomerManager;
            var customers = manager?.GetCustomerList();
            var grew = false;
            while (customers.Count <= index)
            {
                var customer = manager.GetNewCustomerPrefab(female);
                _active.TrackNativeRoot(customer.gameObject);
                customer.gameObject.SetActive(false);
                grew = true;
            }

            if (grew)
            {
                // Runtime pool growth must announce itself exactly like the batch overload.
                // Client modules hold state that could not be applied until this slot existed
                // (trade/register carriers, deodorant state) and apply it from these events; a
                // silently grown slot left that state stranded. A customer's identity can reach
                // the host's incremental scan after its counter trade offer, so this is the only
                // signal the guest gets that the slot its offer refers to now exists.
                CustomerPoolChanged?.Invoke();
                CustomerCapacityChanged?.Invoke(index);
            }
        }

        private static void EnsureCustomerCapacity(int capacity, List<bool> females)
        {
            var manager = NpcInterop.CustomerManager;
            var customers = manager?.GetCustomerList();
            var grew = false;
            while (customers.Count < capacity)
            {
                var index = customers.Count;
                var customer = manager.GetNewCustomerPrefab(females[index]);

                _active.TrackNativeRoot(customer.gameObject);
                if (customer.gameObject.activeSelf)
                {
                    customer.gameObject.SetActive(false);
                }

                grew = true;
            }

            if (grew)
            {
                CustomerPoolChanged?.Invoke();
                CustomerCapacityChanged?.Invoke(capacity - 1);
            }
        }

        private void BufferPendingState(NpcStateDeltaMessage message)
        {
            _pendingStates[new NpcDeltaKey(message.Kind, message.Index, message.Identity)] = message;
        }

        private bool HasMatchingIdentity(NpcStateDeltaMessage message)
        {
            if (message.Kind == KindCustomer
                && TryGetCustomerGeneration(message.Index, out var generation))
                return generation == message.Identity;

            var key = (message.Kind << 16) | message.Index;
            return _puppets.TryGetValue(key, out var puppet) && puppet.HasIdentity
                && puppet.Identity == message.Identity;
        }

        private void ApplyPendingStates()
        {
            if (_pendingStates.Count == 0)
            {
                return;
            }

            var pending = new List<NpcStateDeltaMessage>(_pendingStates.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                ApplyStateDelta(pending[i]);
            }
        }

        private void ApplyPendingStates(byte kind, ushort index, int identity)
        {
            var key = new NpcDeltaKey(kind, index, identity);
            if (_pendingStates.TryGetValue(key, out var state))
            {
                ApplyStateDelta(state);
            }
        }

        private void ApplyStateDelta(NpcStateDeltaMessage message)
        {
            var key = new NpcDeltaKey(message.Kind, message.Index, message.Identity);
            if (!message.Active)
            {
                _pendingStates.Remove(key);
                RemoveEntity(message.Kind, message.Index, message.Identity);
                return;
            }

            if (!HasMatchingIdentity(message))
            {
                BufferPendingState(message);
                return;
            }

            ApplyEntries(message.HostTime, new List<NpcEntry>
            {
                new NpcEntry
                {
                    Kind = message.Kind,
                    Index = message.Index,
                    Identity = message.Identity,
                    Position = message.Position,
                    Yaw = message.Yaw,
                    Speed = message.Speed,
                    Flags = message.Flags,
                    ActionSequence = message.ActionSequence,
                    ActionKind = message.ActionKind,
                    HoldBig = message.HoldBig,
                    HoldItemType = message.HoldItemType,
                    HoldBoxNetworkId = message.HoldBoxNetworkId,
                    HoldBoxOpened = message.HoldBoxOpened,
                }
            });
            _pendingStates.Remove(key);
        }

        private void RemoveEntity(byte kind, ushort index, int identity)
        {
            var identityKey = (kind << 16) | index;
            if (_pendingIdentities.TryGetValue(identityKey, out var pendingIdentity)
                && pendingIdentity.Identity == identity)
            {
                _pendingIdentities.Remove(identityKey);
            }

            if (kind == KindCustomer && _existing.TryGetValue(index, out var existing)
                && existing.Generation == identity)
            {
                DetachExistingCustomer((int)index, existing.Customer);
            }

            var key = identityKey;
            if (!_puppets.TryGetValue(key, out var puppet)
                || (puppet.HasIdentity && puppet.Identity != identity))
            {
                return;
            }

            ReleaseWorkerBoxProp(puppet);
            DestroyPuppetObject(puppet);
            _puppets.Remove(key);
        }

        /// <summary>Client only. Apply one received NPC state update.</summary>
        private void ApplyEntries(float hostTime, IList<NpcEntry> entries)
        {
            var count = entries.Count;

            // map host time onto the local timeline; low-pass the offset so per-packet
            // network jitter cannot corrupt snapshot spacing (snap on init / big jumps)
            var rawOffset = _now - hostTime;
            if (!_clockInit || Mathf.Abs(rawOffset - _clockOffset) > 1f)
            {
                _clockOffset = rawOffset;
                _clockInit = true;
            }
            else
            {
                _clockOffset += 0.1f * (rawOffset - _clockOffset);
            }

            var snapTime = hostTime + _clockOffset;

            for (var n = 0; n < count; n++)
            {
                var ent = entries[n];
                var kind = ent.Kind;
                var index = ent.Index;
                var identity = ent.Identity;
                var hasName = ent.HasName;
                var charName = ent.CharName;
                // State-only entries carry no gender at all, so `ent.Female` defaults to false.
                // Treating that as authoritative and comparing it against the puppet's real
                // gender destroyed every female puppet on each state update (its Female flag
                // never matched the default). Only a name-bearing baseline/identity entry can
                // assert gender, and customers encode it in the name as well.
                var female = ent.Female || (hasName && HasFemaleNamePrefix(charName));
                var pos = ent.Position;
                var yaw = ent.Yaw;
                var speed = ent.Speed;
                var flags = (NpcFlags)ent.Flags;
                var actionSequence = ent.ActionSequence;
                var actionKind = ent.ActionKind;
                var key = (kind << 16) | index;
                if (kind == KindCustomer && _existing.TryGetValue(index, out var existingMirror)
                    && existingMirror.Generation == identity)
                {
                    var existing = existingMirror;
                    if (existing != null)
                    {
                        existing.BufHead = (existing.BufHead + 1) & 3;
                        existing.Buf[existing.BufHead] = new Snap
                        {
                            Pos = pos,
                            Yaw = yaw,
                            Speed = speed,
                            Flags = flags,
                            Time = snapTime
                        };
                        if (existing.BufCount < 4)
                        {
                            existing.BufCount++;
                        }
                        existing.Flags = flags;
                        if (existing.Customer != null)
                        {
                            ApplyActionTrigger(existing.Customer.m_Anim, kind, actionKind,
                                actionSequence, ref existing.GrabSequence);
                        }

                        // A pooled customer is the interaction carrier. Keep its visual puppet
                        // alive and fed with the same snapshot even while the carrier is on
                        // screen, so handing the customer back (register sale finish, trade end)
                        // reveals a ready puppet instead of a hole that the client has to rebuild
                        // from scratch - the puppet must not age out while it is hidden.
                        //
                        // This branch runs INSTEAD of the normal puppet-creation path below, so
                        // a customer whose first snapshot arrives while it is ALREADY borrowed
                        // (a register carrier claimed before any named snapshot spawned its
                        // puppet) would otherwise never get one at all: on release the real
                        // carrier is hidden and nothing is revealed until the next name-bearing
                        // refresh rebuilds a puppet, leaving the customer invisible for seconds.
                        // Ensure a hidden puppet exists here so the handoff always has a body.
                        var visualKey = (KindCustomer << 16) | index;
                        _puppets.TryGetValue(visualKey, out var visual);
                        if (visual == null || visual.Go == null)
                        {
                            existing.PuppetReadyNotified = false;
                        }
                        if (visual != null && visual.HasIdentity
                            && (visual.Identity != identity
                                || (hasName && visual.Female != female)))
                        {
                            // This list slot was reused by a newer pooled customer: drop the
                            // stale body so it is rebuilt for this identity, exactly as the
                            // normal path does.
                            ClearPendingIdentity(visual);
                            _active.DestroyPuppetObject(visual);

                            visual.Go = null;
                            visual.CharName = "";
                            visual.Identity = 0;
                            visual.HasIdentity = false;
                            visual.BufCount = 0;
                            visual.GrabSequence = UnsetActionSequence;
                            existing.PuppetReadyNotified = false;
                        }
                        if (visual == null || visual.Go == null)
                        {
                            if (hasName)
                            {
                                if (visual == null)
                                {
                                    visual = new Puppet();
                                    _puppets[visualKey] = visual;
                                }
                                visual.Kind = KindCustomer;
                                SetPendingIdentity(visual, identity, female);
                                Redress(visual, charName, pos, female, KindCustomer, index);
                            }
                        }
                        if (visual != null)
                        {
                            visual.Flags = flags;
                            visual.BufHead = (visual.BufHead + 1) & 3;
                            visual.Buf[visual.BufHead] = new Snap
                            {
                                Pos = pos,
                                Yaw = yaw,
                                Speed = speed,
                                Flags = flags,
                                Time = snapTime
                            };
                            if (visual.BufCount < 4)
                            {
                                visual.BufCount++;
                            }
                            ApplyActionTrigger(visual.Anim, kind, actionKind, actionSequence,
                                ref visual.GrabSequence);
                            // Register carriers render the real customer, so their puppet stays
                            // hidden (still alive and buffered) until DetachExistingCustomer shows
                            // it; trade carriers keep the puppet visible.
                            visual.Go?.SetActive(existing.KeepPuppetVisible);
                            if (existing.KeepPuppetVisible && visual.Go != null
                                && !existing.PuppetReadyNotified)
                            {
                                existing.PuppetReadyNotified = true;
                                ExistingCustomerPuppetReady?.Invoke(index, identity,
                                    existing.Customer);
                            }
                        }
                    }
                    continue;
                }
                // the register carrier renders this customer for real (with clickable cash);
                // do not also paint an inert clone over it
                if (!_puppets.TryGetValue(key, out var p))
                {
                    p = new Puppet();
                    _puppets[key] = p;
                }

                var identityChanged = p.HasIdentity
                    && (p.Identity != identity || (hasName && p.Female != female));
                if (identityChanged)
                {
                    ClearPendingIdentity(p);
                    _active.DestroyPuppetObject(p);

                    p.Go = null;
                    p.CharName = "";
                    p.Identity = 0;
                    p.HasIdentity = false;
                    p.BufCount = 0;
                    p.GrabSequence = UnsetActionSequence;
                }
                p.Kind = kind;
                if (kind == KindWorker)
                {
                    p.HoldBig = ent.HoldBig;
                    p.HoldItemType = ent.HoldItemType;
                    p.HoldBoxNetworkId = ent.HoldBoxNetworkId;
                    p.HoldBoxOpened = ent.HoldBoxOpened;
                }

                if (hasName)
                {
                    SetPendingIdentity(p, identity, female);
                    Redress(p, charName, pos, female, kind, index);
                }

                p.BufHead = (p.BufHead + 1) & 3;
                p.Buf[p.BufHead] = new Snap
                {
                    Pos = pos,
                    Yaw = yaw,
                    Speed = speed,
                    Flags = flags,
                    Time = snapTime,
                };
                if (p.BufCount < 4)
                {
                    p.BufCount++;
                }

                p.Flags = flags;
                ApplyActionTrigger(p.Anim, kind, actionKind, actionSequence, ref p.GrabSequence);
                if (kind == KindCustomer && SuppressedCustomer.Contains(index) && p.Go != null)
                {
                    p.Go.SetActive(false);
                }
            }
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

        private static void ApplyExistingFlags(Customer customer, NpcFlags flags, float speed)
        {
            if (customer.m_Anim == null)
            {
                return;
            }

            customer.m_Anim.SetFloat(HashMoveSpeed, speed);
            customer.m_Anim.SetBool(HashHoldingBag, (flags & NpcFlags.HoldingBag) != 0);
            customer.m_Anim.SetBool(HashHandingOverCash, (flags & NpcFlags.HandingOverCash) != 0);
            customer.m_Anim.SetBool(HashIsSitting, (flags & NpcFlags.IsSitting) != 0);
            customer.m_Anim.SetBool(HashIsPlaying, (flags & NpcFlags.IsPlaying) != 0);
            customer.m_Anim.SetBool(HashIsHoldingBox, (flags & NpcFlags.IsHoldingBox) != 0);
            customer.m_Anim.SetBool(HashIsBeingSprayed, (flags & NpcFlags.Sprayed) != 0);
            customer.m_ShoppingBagTransform?.gameObject.SetActive((flags & NpcFlags.HoldingBag) != 0);

            customer.m_CustomerCash?.gameObject.SetActive((flags & NpcFlags.HandingOverCash) != 0);

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
            ushort index)
        {
            _dressing = true;
            try
            {
                ReDress(puppet, charName, pos, female, kind, index);
            }
            finally
            {
                _dressing = false;
            }
        }

        private void ReDress(Puppet p, string charName, Vector3 pos, bool female, byte kind,
            ushort index)
        {
            var genderChanged = p.Go != null && p.Female != female;
            if (p.Go == null || p.Custom == null || genderChanged)
            {
                var pendingIdentity = p.PendingIdentity;
                if (p.Go != null)
                {
                    ClearPendingIdentity(p);
                    DestroyPuppetObject(p);
                    p.Go = null;
                    p.CharName = "";
                    p.Identity = 0;
                    p.HasIdentity = false;
                }

                p.Kind = kind;
                SetPendingIdentity(p, pendingIdentity, female);
                Spawn(p, charName, pos, female, kind, index);
                CommitIdentity(p);
                return;
            }

            if (p.CharName == charName)
            {
                // Already wearing this exact look; only the incarnation advanced. Skip the
                // JSON round-trip and hair/apparel mesh rebuild inside
                // CharacterCustomization.Initialize(), which is otherwise unconditional.
                CommitIdentity(p);
                return;
            }

            p.Custom.CharacterName = charName;
            p.Custom.Initialize();
            p.CharName = charName;
            p.Female = female;
            p.Go.name = "CoopNpc_" + charName;
            CommitIdentity(p);
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
                    if (p.Anim != null)
                    {
                        p.Anim.SetBool(HashHoldingBag, (p.Flags & NpcFlags.HoldingBag) != 0);
                        p.Anim.SetBool(HashHandingOverCash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                        p.Anim.SetBool(HashIsSitting, (p.Flags & NpcFlags.IsSitting) != 0);
                        p.Anim.SetBool(HashIsPlaying, (p.Flags & NpcFlags.IsPlaying) != 0);
                        p.Anim.SetBool(HashIsHoldingBox, (p.Flags & NpcFlags.IsHoldingBox) != 0);
                        p.Anim.SetBool(HashIsBeingSprayed, (p.Flags & NpcFlags.Sprayed) != 0);
                    }
                    Toggle(p.Bag, (p.Flags & NpcFlags.HoldingBag) != 0);
                    Toggle(p.Cash, (p.Flags & NpcFlags.HandingOverCash) != 0);
                    Toggle(p.CardFan, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.CardSingle, (p.Flags & NpcFlags.IsPlaying) != 0);
                    Toggle(p.Smelly, (p.Flags & NpcFlags.Smelly) != 0);
                    Toggle(p.Clean, (p.Flags & NpcFlags.Cleaned) != 0);
                    Toggle(p.Exclaim, (p.Flags & NpcFlags.Exclaim) != 0);
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
                        if (releasedBoxId > 0)
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
                    ApplyExistingFlags(mirror.Customer, mirror.Flags, mirror.AnimSpeed);
                    mirror.AppliedAnimSpeed = mirror.AnimSpeed;
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

        /// <summary>Discard generated customization collections on the puppet instance only.
        /// Some modded prefabs arrive serialized as already initialized even though the worker
        /// or customer manager expects its source to be a template. Rebuilding these collections
        /// on the clone gives Initialize() the same contract as an uninitialized prefab and never
        /// touches the scene-owned source.</summary>
        private static void ResetCustomizationCollection(CC.CharacterCustomization custom,
            FieldInfo field)
        {
            if (custom == null || field == null)
            {
                return;
            }

            var objects = field.GetValue(custom) as IList;
            if (objects == null)
            {
                objects = new List<GameObject>();
                field.SetValue(custom, objects);
                return;
            }

            for (var i = 0; i < objects.Count; i++)
            {
                if (objects[i] is GameObject generated && generated != null)
                {
                    Destroy(generated);
                }
            }

            objects.Clear();
        }

        private static void InitializePuppetCustomization(CC.CharacterCustomization custom,
            string charName)
        {
            // A prefab must be a template, but an appearance mod can serialize the init bit on
            // its source. Reset only the instantiated clone before its one initialization; never
            // call Initialize() on a live worker/customer customization and never alter that
            // source's private lists.
            if (custom.m_HasInit)
            {
                ResetCustomizationCollection(custom, FiCustomizationHairObjects);
                ResetCustomizationCollection(custom, FiCustomizationApparelObjects);
                custom.m_HasInit = false;
            }

            custom.CharacterName = charName;
            custom.Initialize();
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

        private void Spawn(Puppet p, string charName, Vector3 pos, bool femaleHint, byte kind, ushort index)
        {
            var female = femaleHint;
            p.Female = female;
            p.Kind = kind;
            GameObject prefabObject;
            if (kind == KindWorker)
            {
                var workerManager = SceneRef<WorkerManager>.Get();
                var prefab = female ? workerManager.m_WorkerFemalePrefab : workerManager.m_WorkerPrefab;
                // Never clone a scene worker. Its CharacterCustomization and worker data are
                // already initialized for a real employee, and reinitializing that clone can
                // index the wrong gender's wardrobe tables. The manager prefab is the
                // gender-correct, uninitialized source for both supported game builds.
                prefabObject = prefab.gameObject;
            }
            else
            {
                var customerManager = SceneRef<CustomerManager>.Get();
                var prefab = female ? customerManager.m_CustomerFemalePrefab
                    : customerManager.m_CustomerPrefab;
                // Customers have no semantic interaction component on a puppet, so use the
                // same known-good gender-correct template path as vanilla instead of borrowing
                // and reinitializing a live pool member.
                prefabObject = prefab.gameObject;
            }

            var holder = new GameObject("CoopNpcHolder_tmp");
            holder.SetActive(false);
            var clone = Instantiate(prefabObject, holder.transform);
            var cloneInstanceId = clone.GetInstanceID();
            RegisterPuppetRoot(clone);
            clone.transform.SetParent(null, worldPositionStays: false);
            clone.transform.position = pos;
            Destroy(holder);

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
            InitializePuppetCustomization(p.Custom, charName);

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
                p.Bag = cust.m_ShoppingBagTransform?.gameObject;
                p.Cash = cust.m_CustomerCash?.gameObject;
                p.CardFan = cust.m_GameCardFanOut;
                p.CardSingle = cust.m_GameCardSingle;
                p.Smelly = cust.m_SmellyFX; // plain child FX object, survives the strip
                p.Clean = cust.m_CleanFX; // clean puff FX shown after a spray
                p.Exclaim = cust.m_ExclaimationMesh; // the "!" trade prompt, driven by flags below
                Toggle(p.Bag, false);
                Toggle(p.Cash, false);
                Toggle(p.CardFan, false);
                Toggle(p.CardSingle, false);
                cust.m_CleanFX?.SetActive(false);

                cust.m_ExclaimationMesh?.SetActive(false);

                cust.m_InteractCollider?.SetActive(false);

                cust.m_SmellyFX?.SetActive(false);
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
            clone.name = "CoopNpc_" + charName;
            p.Go = clone;
            p.RootInstanceId = cloneInstanceId;
            // This must run AFTER p.Go is assigned: RefreshWorkerUi looks the puppet up
            // through that field, so seeding before it is a silent no-op and the puppet
            // keeps the template's all-enabled pack-refill defaults until an unrelated
            // StaffState change happens to arrive.
            var saved = CPlayerData.m_WorkerSaveDataList;
            if (worker != null && saved != null && index < saved.Count)
            {
                RefreshWorkerUi(index, saved[index]);
            }

            // Prefer the Animator reference owned by the cloned Worker. Mods may leave
            // the original Animator disabled beside a replacement Animator in the same
            // hierarchy; GetComponentInChildren alone can select the wrong one.
            p.Anim = worker != null && worker.m_Anim != null
                ? worker.m_Anim : clone.GetComponentInChildren<Animator>(true);
            // Commit the name only when the clone actually wears it. On a failed dress the clone
            // keeps its source pooled look, so leaving the name unset makes the next name-bearing
            // packet run ReDress against the existing clone instead of accepting the stale model.
            p.CharName = charName;
            p.PrevRenderedPos = pos;
            p.RenderYaw = 0f;
            p.AnimSpeed = 0f;
            p.AppliedFlags = -1;
            clone.SetActive(true);
        }

    }
}
