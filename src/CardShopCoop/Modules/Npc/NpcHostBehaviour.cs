using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>How one live NPC changed at a game lifecycle boundary. Identity binding and
    /// publication are owned by the identity module; this is bookkeeping only.</summary>
    internal enum NpcLifecycleChange : byte
    {
        None = 0,
        Added = 1,
        Removed = 2,
    }

    /// <summary>Unity-owned host NPC state collection and host-only feature hooks. NPC identity,
    /// spawn, despawn and baselines belong to the GameObjectIdentity module.</summary>
    [ServerBehaviour]
    public sealed class NpcHostBehaviour : CoopBehaviour
    {
        private static NpcHostBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private readonly Dictionary<int, Customer> _customers = new();
        private readonly Dictionary<int, Worker> _workers = new();

        internal static NpcHostBehaviour Active => _active;

        private void OnEnable()
        {
            if (_initialized)
            {
                return;
            }
            _active = this;
            Harmony.CreateClassProcessor(typeof(WorkerActionPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(SpeechPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(MoneyPatch)).Patch();
            SceneManager.sceneLoaded += OnSceneLoaded;

            // The identity module owns the NPC lifecycle hooks; it binds the live population as
            // soon as both behaviours are online.
            NpcHostLifecycle.NpcTrackingReady();
            _initialized = true;
        }

        private void Update()
        {
            if (!RuntimeContext.InGame())
            {
                return;
            }

            using (Util.PerfProbe.Sample("module.npc.host-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            var deltas = HostCollect(Time.deltaTime);
            if (deltas == null)
            {
                return;
            }
            for (var i = 0; i < deltas.Count; i++)
            {
                RuntimeContext.Broadcast(deltas[i]);
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
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _customers.Clear();
            _workers.Clear();
            _npcStates.Clear();
        }

        private void OnDestroy() => Shutdown();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (_shutdown)
            {
                return;
            }

            ResetSceneState();
        }

        private void ResetSceneState()
        {
            _sendTimer = 0f;
            _missingHoldFieldLogged = false;
            _npcStates.Clear();
            _customers.Clear();
            _workers.Clear();
        }

        public CustomerManager GetCustomerManager()
            => NpcInterop.CustomerManager;

        private const byte KindCustomer = 0;
        private const byte KindWorker = 1;

        private const float SendInterval = 0.125f;

        // string-keyed animator calls hash the name on every call; cache the ids once
        private static readonly int HashMoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int HashHoldingBag = Animator.StringToHash("HoldingBag");
        private static readonly int HashHandingOverCash = Animator.StringToHash("HandingOverCash");
        private static readonly int HashIsSitting = Animator.StringToHash("IsSitting");
        private static readonly int HashIsPlaying = Animator.StringToHash("IsPlaying");
        private static readonly int HashIsHoldingBox = Animator.StringToHash("IsHoldingBox");
        private static readonly int HashIsBeingSprayed = Animator.StringToHash("IsBeingSprayed");
        private static readonly FieldInfo FiCurrentHoldItemBox =
            AccessTools.Field(typeof(Worker), "m_CurrentHoldItemBox");

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

        // ---------------- host: collect & serialize ----------------

        private float _sendTimer;
        private readonly Dictionary<NpcKey, NpcData> _npcStates = new();
        private bool _missingHoldFieldLogged;

        private readonly struct NpcKey : IEquatable<NpcKey>
        {
            public NpcKey(byte kind, int index)
            {
                Kind = kind;
                Index = index;
            }

            public byte Kind
            {
                get;
            }
            public int Index
            {
                get;
            }

            public bool Equals(NpcKey other) => Kind == other.Kind && Index == other.Index;

            public override bool Equals(object obj) => obj is NpcKey other && Equals(other);

            public override int GetHashCode() => (Index * 397) ^ Kind;
        }

        private sealed class NpcData
        {
            public bool Active;
            public int Generation;
            public ECustomerState CustomerState;
            public int CustomerGrabSequence;
            public int WorkerActionSequence;
            public byte WorkerActionKind;
            public Guid LastHeldBoxId;
        }

        private NpcData GetNpcData(byte kind, int index)
        {
            var key = new NpcKey(kind, index);
            if (!_npcStates.TryGetValue(key, out var data))
            {
                data = new NpcData();
                _npcStates.Add(key, data);
            }

            return data;
        }

        /// <summary>
        /// Host-side bookkeeping at a game lifecycle boundary. Registers the live object and
        /// advances the incarnation when it becomes active. The caller (the identity module) binds
        /// and publishes the resulting identity; this method never touches the identity cache.
        /// </summary>
        internal static NpcLifecycleChange TrackNpc(object entity, string source, out int index,
            out int identity)
        {
            index = -1;
            identity = 0;
            var host = _active;
            if (host == null || entity == null)
            {
                return NpcLifecycleChange.None;
            }

            if (entity is Customer customer)
            {
                if (!NpcInterop.TryGetCustomerIndex(customer, out index))
                {
                    LogUnresolved(KindCustomer, customer, source);
                    return NpcLifecycleChange.None;
                }

                var replaced = host._customers.TryGetValue(index, out var previous)
                    && !ReferenceEquals(previous, customer);
                host._customers[index] = customer;
                return host.TrackState(host.GetNpcData(KindCustomer, index), customer.m_IsActive,
                    replaced, out identity);
            }

            if (entity is Worker worker)
            {
                if (!NpcInterop.TryGetWorkerIndex(worker, out index))
                {
                    LogUnresolved(KindWorker, worker, source);
                    return NpcLifecycleChange.None;
                }

                var replaced = host._workers.TryGetValue(index, out var previous)
                    && !ReferenceEquals(previous, worker);
                host._workers[index] = worker;
                var state = host.GetNpcData(KindWorker, index);
                var change = host.TrackState(state, worker.m_IsActive, replaced, out identity);
                if (change == NpcLifecycleChange.Removed)
                {
                    ReleaseTrackedWorkerBox(state);
                }

                return change;
            }

            return NpcLifecycleChange.None;
        }

        private NpcLifecycleChange TrackState(NpcData state, bool active, bool replaced,
            out int identity)
        {
            identity = state.Generation;
            if (active)
            {
                var appeared = !state.Active || replaced;
                if (appeared)
                {
                    state.Generation++;
                }

                state.Active = true;
                identity = state.Generation;
                return appeared ? NpcLifecycleChange.Added : NpcLifecycleChange.None;
            }

            var wasActive = state.Active;
            state.Active = false;
            return wasActive ? NpcLifecycleChange.Removed : NpcLifecycleChange.None;
        }

        internal static bool IsNpcActive(object entity)
        {
            return entity switch
            {
                Customer customer => customer != null && customer.m_IsActive,
                Worker worker => worker != null && worker.m_IsActive,
                _ => false,
            };
        }

        /// <summary>True when the entity already has host state for an active incarnation.</summary>
        internal static bool IsNpcTracked(object entity)
        {
            var host = _active;
            if (host == null || entity == null)
            {
                return false;
            }

            if (entity is Customer customer
                && NpcInterop.TryGetCustomerIndex(customer, out var customerIndex))
            {
                return host.GetNpcData(KindCustomer, customerIndex).Active;
            }

            if (entity is Worker worker
                && NpcInterop.TryGetWorkerIndex(worker, out var workerIndex))
            {
                return host.GetNpcData(KindWorker, workerIndex).Active;
            }

            return false;
        }

        /// <summary>Describes the NPC's current authoritative state for the identity descriptor
        /// and join baseline. State collection stays in this module; the descriptor shape stays in
        /// the identity module.</summary>
        internal static bool TryGetSnapshot(object entity, out NpcSnapshot snapshot)
        {
            snapshot = default;
            var host = _active;
            if (host == null || entity == null)
            {
                return false;
            }

            if (entity is Customer customer)
            {
                return NpcInterop.TryGetCustomerIndex(customer, out var index)
                    && host.BuildCustomerSnapshot(customer, index, out snapshot);
            }

            if (entity is Worker worker)
            {
                return NpcInterop.TryGetWorkerIndex(worker, out var index)
                    && host.BuildWorkerSnapshot(worker, index, out snapshot);
            }

            return false;
        }

        /// <summary>Host only. Samples the live NPCs at the interpolation cadence and emits one
        /// transient delta per active NPC. Reliable identity lifecycle hooks carry appearance
        /// and removal; this stream only carries the latest movement and animation state.</summary>
        public List<NpcStateDeltaMessage> HostCollect(float dt)
        {
            _sendTimer += dt;
            var manager = GetCustomerManager();
            var customers = manager?.GetCustomerList();
            if (manager == null || customers == null)
            {
                return null;
            }

            if (_sendTimer < SendInterval)
            {
                return null;
            }

            _sendTimer -= SendInterval; // preserve cadence across frame boundaries
            if (_sendTimer > SendInterval)
            {
                _sendTimer = SendInterval; // clamp debt after a hitch
            }

            var result = new List<NpcStateDeltaMessage>(_customers.Count + _workers.Count);
            var customerIndices = new List<int>(_customers.Keys);
            customerIndices.Sort();
            for (var i = 0; i < customerIndices.Count; i++)
            {
                var index = customerIndices[i];
                if (_customers.TryGetValue(index, out var customer))
                {
                    var delta = CollectCustomerState(customer, index, Time.unscaledTime);
                    if (delta != null)
                    {
                        result.Add(delta);
                    }
                }
            }

            var workerIndices = new List<int>(_workers.Keys);
            workerIndices.Sort();
            for (var i = 0; i < workerIndices.Count; i++)
            {
                var index = workerIndices[i];
                if (_workers.TryGetValue(index, out var worker))
                {
                    var delta = CollectWorkerState(worker, index, Time.unscaledTime);
                    if (delta != null)
                    {
                        result.Add(delta);
                    }
                }
            }

            if (result.Count == 0)
            {
                return null;
            }

            return result;
        }

        private static void LogUnresolved(byte kind, UnityEngine.Object instance, string source)
        {
            CoopPlugin.Log.LogWarning("Npc host: " + (kind == KindCustomer ? "customer" : "worker")
                + " mutation from " + source + " has no stable identity ("
                + (instance == null ? "<destroyed>" : instance.name) + ")");
        }

        private bool BuildCustomerSnapshot(Customer customer, int index, out NpcSnapshot snapshot)
        {
            snapshot = default;
            var state = GetNpcData(KindCustomer, index);
            var custom = customer.m_CharacterCustom;
            if (state.Generation <= 0 || custom == null
                || string.IsNullOrEmpty(custom.CharacterName))
            {
                return false;
            }

            var flags = CollectFlags(customer.m_Anim);
            flags = AddCustomerReactionFlags(customer, flags);
            if (customer.IsSmelly())
            {
                flags |= NpcFlags.Smelly;
            }
            if (customer.m_ExclaimationMesh != null && customer.m_ExclaimationMesh.activeSelf)
            {
                flags |= NpcFlags.Exclaim;
            }

            snapshot = new NpcSnapshot
            {
                Kind = KindCustomer,
                Index = (ushort)index,
                Identity = state.Generation,
                Female = customer.m_IsFemale,
                CharName = custom.CharacterName,
                Position = customer.transform.position,
                Yaw = customer.transform.eulerAngles.y,
                Speed = customer.m_CurrentMoveSpeed,
                Flags = (ushort)flags,
                ActionSequence = GetGrabSequence(index, customer.m_CurrentState),
                ActionKind = customer.m_CurrentState == ECustomerState.TournamentTakePrize
                    ? (byte)2 : (byte)1,
                HoldBig = false,
                HoldItemType = (EItemType)0,
                HoldBox = null,
                HoldBoxNetworkId = Guid.Empty,
                HoldBoxOpened = false,
                HostTime = Time.unscaledTime,
            };
            return true;
        }

        private bool BuildWorkerSnapshot(Worker worker, int index, out NpcSnapshot snapshot)
        {
            snapshot = default;
            var state = GetNpcData(KindWorker, index);
            var custom = worker.m_CharacterCustom;
            if (state.Generation <= 0 || custom == null
                || string.IsNullOrEmpty(custom.CharacterName))
            {
                return false;
            }

            var flags = CollectFlags(worker.m_Anim);
            var moveSpeed = worker.m_Anim == null ? 0f : worker.m_Anim.GetFloat(HashMoveSpeed);
            var holdBox = GetHeldBox(worker);
            var holdBoxNetworkId = Guid.Empty;
            var holdBoxOpened = false;
            if (holdBox != null)
            {
                WorldHostBehaviour.TryGetHeldBoxId(holdBox, out holdBoxNetworkId);
                holdBoxOpened = WorldHostBehaviour.IsHeldBoxOpen(holdBox);
                flags |= NpcFlags.IsHoldingBox;
            }
            if (worker.m_ExclaimationMesh != null && worker.m_ExclaimationMesh.activeSelf)
            {
                flags |= NpcFlags.Exclaim;
            }
            if (worker.m_IsFemale)
            {
                flags |= NpcFlags.Female;
            }

            snapshot = new NpcSnapshot
            {
                Kind = KindWorker,
                Index = (ushort)index,
                Identity = state.Generation,
                Female = worker.m_IsFemale,
                CharName = custom.CharacterName,
                Position = worker.transform.position,
                Yaw = worker.transform.eulerAngles.y,
                Speed = moveSpeed,
                Flags = (ushort)flags,
                ActionSequence = state.WorkerActionSequence,
                ActionKind = state.WorkerActionKind,
                HoldBig = holdBox != null && holdBox.m_IsBigBox,
                HoldItemType = holdBox == null ? (EItemType)0 : holdBox.GetItemType(),
                HoldBox = holdBox,
                HoldBoxNetworkId = holdBoxNetworkId,
                HoldBoxOpened = holdBoxOpened,
                HostTime = Time.unscaledTime,
            };
            return true;
        }

        private NpcStateDeltaMessage CollectCustomerState(Customer customer, int index,
            float hostTime)
        {
            if (!IsCustomerActive(customer) || !BuildCustomerSnapshot(customer, index, out var state))
            {
                return null;
            }

            var delta = NewStateDelta(state, hostTime);
            delta.Customer = customer;
            return delta;
        }

        private NpcStateDeltaMessage CollectWorkerState(Worker worker, int index, float hostTime)
        {
            var tracked = GetNpcData(KindWorker, index);
            if (!IsWorkerActive(worker))
            {
                ReleaseTrackedWorkerBox(tracked);
                return null;
            }

            if (!BuildWorkerSnapshot(worker, index, out var state))
            {
                return null;
            }

            // A released box must be re-announced with its dropped pose, or the guest leaves its
            // local copy parked off-map (the vanished-on-drop bug). The refresh is broadcast here,
            // before the state delta that tells the guest to release the prop, so the ordered link
            // applies the pose first and the release only re-enables its physics.
            if (tracked.LastHeldBoxId != Guid.Empty
                && tracked.LastHeldBoxId != state.HoldBoxNetworkId)
            {
                WorldHostBehaviour.NotifyWorkerReleasedBox(tracked.LastHeldBoxId);
            }

            tracked.LastHeldBoxId = state.HoldBoxNetworkId;

            var delta = NewStateDelta(state, hostTime);
            delta.Worker = worker;
            return delta;
        }

        private InteractablePackagingBox_Item GetHeldBox(Worker worker)
        {
            if (FiCurrentHoldItemBox == null)
            {
                if (!_missingHoldFieldLogged)
                {
                    _missingHoldFieldLogged = true;
                    CoopPlugin.Log.LogError(
                        "NpcHostBehaviour: Worker hold-item field is missing; holding-box visuals disabled");
                }

                return null;
            }

            return FiCurrentHoldItemBox.GetValue(worker) as InteractablePackagingBox_Item;
        }

        private static bool IsCustomerActive(Customer customer)
            => customer != null && customer.m_IsActive && customer.gameObject.activeSelf;

        private static bool IsWorkerActive(Worker worker)
            => worker != null && worker.m_IsActive && worker.gameObject.activeSelf;

        /// <summary>Host: a tracked worker stopped holding its box. Re-announce the box so guests
        /// restore its dropped pose instead of leaving their parked copy invisible.</summary>
        private static void ReleaseTrackedWorkerBox(NpcData state)
        {
            if (state == null || state.LastHeldBoxId == Guid.Empty)
            {
                return;
            }

            WorldHostBehaviour.NotifyWorkerReleasedBox(state.LastHeldBoxId);
            state.LastHeldBoxId = Guid.Empty;
        }

        private static NpcStateDeltaMessage NewStateDelta(in NpcSnapshot state, float hostTime)
            => new NpcStateDeltaMessage
            {
                HostTime = hostTime,
                Position = state.Position,
                Yaw = state.Yaw,
                Speed = state.Speed,
                Flags = state.Flags,
                ActionSequence = state.ActionSequence,
                ActionKind = state.ActionKind,
                HoldBig = state.HoldBig,
                HoldItemType = state.HoldItemType,
                HoldBox = state.HoldBox,
                HoldBoxOpened = state.HoldBoxOpened,
            };

        private int GetGrabSequence(int index, ECustomerState state)
        {
            var data = GetNpcData(KindCustomer, index);
            var previous = data.CustomerState;
            if (state == ECustomerState.TakingItemFromShelf
                || state == ECustomerState.TakingItemFromCardShelf
                || state == ECustomerState.TakingItemFromBulkDonationBox
                || state == ECustomerState.TournamentTakePrize)
            {
                if (previous != state)
                {
                    data.CustomerGrabSequence++;
                }
            }
            data.CustomerState = state;
            return data.CustomerGrabSequence;
        }

        private static NpcFlags CollectFlags(Animator anim)
        {
            var flags = NpcFlags.None;
            if (anim == null)
            {
                return flags;
            }

            if (anim.GetBool(HashHoldingBag))
            {
                flags |= NpcFlags.HoldingBag;
            }
            if (anim.GetBool(HashHandingOverCash))
            {
                flags |= NpcFlags.HandingOverCash;
            }
            if (anim.GetBool(HashIsSitting))
            {
                flags |= NpcFlags.IsSitting;
            }
            if (anim.GetBool(HashIsPlaying))
            {
                flags |= NpcFlags.IsPlaying;
            }
            if (anim.GetBool(HashIsHoldingBox))
            {
                flags |= NpcFlags.IsHoldingBox;
            }
            return flags;
        }

        /// <summary>Customer-only visual state the spray path writes onto the live animator and FX.
        /// The client suppresses both <c>Customer.Update</c> (which resets the spray reaction) and
        /// <c>Customer.DeodorantSprayCheck</c>, so these native fields are the only source of the
        /// reaction and have to ride the state sync instead of being re-simulated on the client.</summary>
        private static NpcFlags AddCustomerReactionFlags(Customer customer, NpcFlags flags)
        {
            if (customer.m_Anim != null && customer.m_Anim.GetBool(HashIsBeingSprayed))
            {
                flags |= NpcFlags.Sprayed;
            }
            if (customer.m_CleanFX != null && customer.m_CleanFX.activeSelf)
            {
                flags |= NpcFlags.Cleaned;
            }
            return flags;
        }

        public static void RecordWorkerAction(Worker worker)
        {
            if (_active == null || worker == null)
            {
                return;
            }

            if (!NpcInterop.TryGetWorkerIndex(worker, out var index))
            {
                CoopPlugin.Log.LogWarning("Npc host: worker action has no stable worker identity");
                return;
            }

            var state = _active.GetNpcData(KindWorker, index);
            state.WorkerActionSequence++;
            state.WorkerActionKind = 3; // ScanItem
        }

        private static CustomerManager s_diagCm;

        /// <summary>Diagnostic: how many REAL (non-puppet) NPCs are currently active in
        /// this instance's own managers. On the host that's the true crowd; on the client
        /// it should be zero (anything else is escaping suppression).</summary>
        public static int CountLocalActiveNpcs()
        {
            // cached across calls; Unity's overloaded == re-resolves after scene changes
            s_diagCm = NpcInterop.CustomerManager;

            var n = 0;
            if (s_diagCm != null)
            {
                var list = s_diagCm.GetCustomerList();
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] != null && list[i].gameObject.activeSelf)
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

        /// <summary>Host incarnation of a customer. The identity lifecycle hook owns generation
        /// advances; this read never mutates the identity.</summary>
        public static int GetCustomerGeneration(Customer customer)
        {
            if (_active == null || customer == null
                || !NpcInterop.TryGetCustomerIndex(customer, out var index))
            {
                return 0;
            }

            return _active.GetNpcData(KindCustomer, index).Generation;
        }

        internal static bool TryGetWireIdentity(Customer customer, out int index, out int identity)
        {
            index = -1;
            identity = 0;
            if (_active == null || customer == null
                || !NpcInterop.TryGetCustomerIndex(customer, out index))
            {
                return false;
            }

            identity = GetCustomerGeneration(customer);
            return identity > 0;
        }

        internal static bool TryGetWireIdentity(Worker worker, out int index, out int identity)
        {
            index = worker == null ? -1 : worker.m_WorkerIndex;
            identity = 0;
            if (_active == null || worker == null || index < 0
                || !_active._workers.TryGetValue(index, out var mapped)
                || !ReferenceEquals(mapped, worker))
            {
                return false;
            }

            identity = _active.GetNpcData(KindWorker, index).Generation;
            return identity > 0;
        }

        internal static bool TryResolveCustomer(int index, int identity, out Customer customer)
        {
            customer = null;
            if (_active == null || index < 0 || identity <= 0
                || !_active._customers.TryGetValue(index, out customer) || customer == null)
            {
                return false;
            }

            return _active.GetNpcData(KindCustomer, index).Generation == identity
                && IsCustomerActive(customer);
        }

        internal static bool TryResolveWorker(int index, int identity, out Worker worker)
        {
            worker = null;
            if (_active == null || index < 0 || identity <= 0
                || !_active._workers.TryGetValue(index, out worker) || worker == null)
            {
                return false;
            }

            return _active.GetNpcData(KindWorker, index).Generation == identity
                && IsWorkerActive(worker);
        }

        /// <summary>Maps a host popup follow transform to the live tracked customer it belongs
        /// to. Only the object crosses the wire; the popup carries no separate identity.</summary>
        private static bool TryGetTrackedCustomer(Transform transform, out Customer customer)
        {
            customer = null;
            if (_active == null || transform == null)
            {
                return false;
            }

            foreach (var pair in _active._customers)
            {
                var candidate = pair.Value;
                if (candidate != null && candidate.transform == transform)
                {
                    customer = candidate;
                    return true;
                }
            }

            CoopPlugin.Log.LogWarning("Npc host: popup transform has no tracked customer");
            return false;
        }

        [HarmonyPatch(typeof(Worker), "PlayWorkerActionAnim")]
        private static class WorkerActionPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance) => RecordWorkerAction(__instance);
        }

        [HarmonyPatch(typeof(PricePopupSpawner), "ShowTextPopup")]
        private static class SpeechPatch
        {
            [HarmonyPostfix]
            private static void Postfix(PricePopupSpawner __instance, string text, float offsetUp,
                Transform followTransform)
            {
                if (_active == null || __instance == null || string.IsNullOrEmpty(text)
                    || followTransform == null)
                {
                    return;
                }
                var popups = __instance.m_PricePopupList;
                if (popups == null)
                {
                    return;
                }
                for (var i = 0; i < popups.Count; i++)
                {
                    var popup = popups[i];
                    if (popup != null && popup.gameObject.activeSelf
                        && popup.m_FollowTransform == followTransform && popup.m_Text != null
                        && popup.m_Text.text == text
                        && TryGetTrackedCustomer(followTransform, out var customer))
                    {
                        _active.RuntimeContext.Broadcast(new NpcSpeechMessage
                        {
                            Customer = customer,
                            Text = text,
                            OffsetUp = offsetUp
                        });
                        return;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(PricePopupSpawner), "ShowPricePopup")]
        private static class MoneyPatch
        {
            [HarmonyPostfix]
            private static void Postfix(PricePopupSpawner __instance, float price, float offsetUp,
                Transform followTransform)
            {
                if (_active == null || __instance == null || price <= 0f || followTransform == null
                    || !TryGetTrackedCustomer(followTransform, out var customer))
                {
                    return;
                }
                _active.RuntimeContext.Broadcast(new NpcMoneyPopupMessage
                {
                    Customer = customer,
                    Amount = price,
                    OffsetUp = offsetUp
                });
            }
        }
    }
}
