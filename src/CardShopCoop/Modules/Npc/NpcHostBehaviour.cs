using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>Unity-owned host NPC collection and host-only Harmony hooks.</summary>
    [ServerBehaviour]
    public sealed class NpcHostBehaviour : CoopBehaviour
    {
        private static NpcHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _customerManagerReady;
        private bool _workerManagerReady;
        private readonly Dictionary<int, Customer> _customers = new();
        private readonly Dictionary<Customer, int> _customerIndices = new();
        private readonly Dictionary<int, Worker> _workers = new();
        private readonly Dictionary<int, PeerConnection> _joinedConnections = new();

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }
            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            _harmony = new Harmony("com.zwhit.cardshopcoop.npc.host");
            _harmony.CreateClassProcessor(typeof(CustomerManagerStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerPopulationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerUpdatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerManagerStartPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerPopulationPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerUpdatePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(WorkerActionPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(SpeechPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(MoneyPatch)).Patch();
            SceneManager.sceneLoaded += OnSceneLoaded;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            RegisterInitialPopulation(NpcInterop.CustomerManager);
            RegisterInitialWorkers(NpcInterop.WorkerManager);
            SignalCustomerManagerReady();
            SignalWorkerManagerReady();
        }

        private void Update()
        {
            if (!_context.InGame())
            {
                return;
            }

            using (CardShopCoop.Util.PerfProbe.Sample("module.npc.host-update"))
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
                _context.Broadcast(deltas[i]);
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            _joinedConnections.Clear();
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
        }

        private void OnDestroy() => Shutdown();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null
                || !IsJoinPhase(connection.State))
            {
                return;
            }

            _joinedConnections[connection.Id] = connection;
            SendBaselineWhenReady(connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection == null)
            {
                return;
            }

            _joinedConnections.Remove(connection.Id);
        }

        private void SendBaselineWhenReady(int connectionId)
        {
            if (!_context.InGame() || !_customerManagerReady || !_workerManagerReady
                || !_joinedConnections.TryGetValue(connectionId, out var connection)
                || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            SendBaseline(connectionId);
        }

        private void SendBaselinesToJoined()
        {
            if (!_context.InGame() || !_customerManagerReady || !_workerManagerReady)
            {
                return;
            }

            foreach (var pair in _joinedConnections)
            {
                if (pair.Value != null && IsJoinPhase(pair.Value.State))
                {
                    SendBaseline(pair.Key);
                }
            }
        }

        private void SendBaseline(int connectionId)
        {
            if (!_customerManagerReady || !_workerManagerReady)
            {
                return;
            }

            var baseline = BuildBaseline();
            if (baseline == null)
            {
                return;
            }

            _context.Send(connectionId, baseline);
        }

        private NpcBaselineMessage BuildBaseline()
        {
            var customers = GetCustomerManager()?.GetCustomerList();
            if (!_customerManagerReady || !_workerManagerReady || customers == null
                || NpcInterop.WorkerManager == null || NpcInterop.Workers == null)
            {
                return null;
            }

            var hostTime = Time.unscaledTime;
            var result = new NpcBaselineMessage
            {
                HostTime = hostTime,
                CustomerCapacity = customers.Count,
                CustomerFemales = new List<bool>(customers.Count),
            };
            for (var i = 0; i < customers.Count; i++)
            {
                result.CustomerFemales.Add(customers[i] != null && customers[i].m_IsFemale);
            }

            var customerIndices = new List<int>(_customers.Keys);
            customerIndices.Sort();
            for (var i = 0; i < customerIndices.Count; i++)
            {
                var index = customerIndices[i];
                if (_customers.TryGetValue(index, out var customer) && IsCustomerActive(customer))
                {
                    var entry = BuildCustomerEntry(customer, index, hostTime);
                    if (entry != null)
                    {
                        result.Entries.Add(entry);
                    }
                }
            }

            var workerIndices = new List<int>(_workers.Keys);
            workerIndices.Sort();
            for (var i = 0; i < workerIndices.Count; i++)
            {
                var index = workerIndices[i];
                if (_workers.TryGetValue(index, out var worker) && IsWorkerActive(worker))
                {
                    var entry = BuildWorkerEntry(worker, index, hostTime);
                    if (entry != null)
                    {
                        result.Entries.Add(entry);
                    }
                }
            }

            CoopPlugin.Log.LogInfo("[npc] host baseline entries=" + result.Entries.Count
                + " capacity=" + result.CustomerCapacity
                + " customersTracked=" + _customers.Count + " workersTracked=" + _workers.Count);

            return result;
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        private void SignalCustomerManagerReady()
        {
            _customerManagerReady = NpcInterop.CustomerManager?.GetCustomerList() != null;
            SendBaselinesToJoined();
        }

        private void SignalWorkerManagerReady()
        {
            _workerManagerReady = NpcInterop.WorkerManager != null && NpcInterop.Workers != null;
            SendBaselinesToJoined();
        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
        {
            SignalCustomerManagerReady();
            SignalWorkerManagerReady();
        }

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
            _customerManagerReady = false;
            _workerManagerReady = false;
            _sendTimer = 0f;
            _missingHoldFieldLogged = false;
            _npcStates.Clear();
            _customers.Clear();
            _customerIndices.Clear();
            _workers.Clear();
        }

        internal void ResetState()
        {
            _missingHoldFieldLogged = false;
            _sendTimer = 0f;
            _npcStates.Clear();
            _customers.Clear();
            _customerIndices.Clear();
            _workers.Clear();
            _joinedConnections.Clear();
        }

        public CustomerManager GetCustomerManager()
            => NpcInterop.CustomerManager;

        private const byte KindCustomer = 0;
        private const byte KindWorker = 1;

        private const float SendInterval = 0.125f;
        /// <summary>Render this far in the past (~1.2x send interval) so two bracketing
        /// snapshots almost always exist.</summary>
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
            public long LastHeldBoxId;
            public string IdentityName;
            public bool IdentityFemale;
            public int IdentityGeneration;
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

        /// <summary>Host only. Samples the live NPCs at the interpolation cadence and emits one
        /// transient delta per entity. Reliable lifecycle hooks carry identity and removal
        /// changes; this stream only carries the latest movement and animation state.</summary>
        public List<NpcStateDeltaMessage> HostCollect(float dt)
        {
            _active = this;
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

        private void RegisterInitialPopulation(CustomerManager manager)
        {
            var customers = manager?.GetCustomerList();
            for (var i = 0; customers != null && i < customers.Count; i++)
            {
                RegisterCustomer(customers[i], "CustomerManager.Start");
            }
        }

        private void RegisterInitialWorkers(WorkerManager manager)
        {
            var workers = NpcInterop.Workers;
            for (var i = 0; workers != null && i < workers.Count; i++)
            {
                RegisterWorker(workers[i], "WorkerManager.Start");
            }
        }

        private void RegisterCustomer(Customer customer, string source)
        {
            if (customer == null || !NpcInterop.TryGetCustomerIndex(customer, out var index))
            {
                LogUnresolved(KindCustomer, customer, source);
                return;
            }

            var replaced = _customers.TryGetValue(index, out var previous)
                && !ReferenceEquals(previous, customer);
            _customers[index] = customer;
            _customerIndices[customer] = index;
            var state = GetNpcData(KindCustomer, index);
            var active = IsCustomerActive(customer);
            if (active && (!state.Active || replaced))
            {
                state.Generation++;
            }

            var wasActive = state.Active;
            state.Active = active;
            if (wasActive && !active)
            {
                SendStateDelta(KindCustomer, (ushort)index, state.Generation, active: false,
                    Time.unscaledTime);
            }
            if (active)
            {
                SendCurrentIdentity(KindCustomer, index, customer, state);
            }
        }

        private void RegisterWorker(Worker worker, string source)
        {
            if (worker == null || !NpcInterop.TryGetWorkerIndex(worker, out var index))
            {
                LogUnresolved(KindWorker, worker, source);
                return;
            }

            var replaced = _workers.TryGetValue(index, out var previous)
                && !ReferenceEquals(previous, worker);
            _workers[index] = worker;
            var state = GetNpcData(KindWorker, index);
            var active = IsWorkerActive(worker);
            if (active && (!state.Active || replaced))
            {
                state.Generation++;
            }

            var wasActive = state.Active;
            state.Active = active;
            if (wasActive && !active)
            {
                SendStateDelta(KindWorker, (ushort)index, state.Generation, active: false,
                    Time.unscaledTime);
            }
            if (active)
            {
                SendCurrentIdentity(KindWorker, index, worker, state);
            }
        }

        private void ObserveCustomer(Customer customer, string source)
        {
            if (customer == null)
            {
                LogUnresolved(KindCustomer, customer, source);
                return;
            }

            if (!_customerIndices.TryGetValue(customer, out var index)
                && !NpcInterop.TryGetCustomerIndex(customer, out index))
            {
                LogUnresolved(KindCustomer, customer, source);
                return;
            }

            if (!_customers.TryGetValue(index, out var registered)
                || !ReferenceEquals(registered, customer))
            {
                RegisterCustomer(customer, source);
                return;
            }

            var state = GetNpcData(KindCustomer, index);
            var active = IsCustomerActive(customer);
            if (active && !state.Active)
            {
                state.Generation++;
            }

            var wasActive = state.Active;
            state.Active = active;
            if (wasActive && !active)
            {
                SendStateDelta(KindCustomer, (ushort)index, state.Generation, active: false,
                    Time.unscaledTime);
            }
            if (active)
            {
                SendCurrentIdentity(KindCustomer, index, customer, state);
            }
        }

        private void ObserveWorker(Worker worker, string source)
        {
            if (worker == null || !NpcInterop.TryGetWorkerIndex(worker, out var index))
            {
                LogUnresolved(KindWorker, worker, source);
                return;
            }

            if (!_workers.TryGetValue(index, out var registered)
                || !ReferenceEquals(registered, worker))
            {
                RegisterWorker(worker, source);
                return;
            }

            var state = GetNpcData(KindWorker, index);
            var active = IsWorkerActive(worker);
            if (active && !state.Active)
            {
                state.Generation++;
            }

            var wasActive = state.Active;
            state.Active = active;
            if (wasActive && !active)
            {
                SendStateDelta(KindWorker, (ushort)index, state.Generation, active: false,
                    Time.unscaledTime);
            }
            if (active)
            {
                SendCurrentIdentity(KindWorker, index, worker, state);
            }
        }

        private static void LogUnresolved(byte kind, UnityEngine.Object instance, string source)
        {
            CoopPlugin.Log.LogWarning("Npc host: " + (kind == KindCustomer ? "customer" : "worker")
                + " mutation from " + source + " has no stable identity ("
                + (instance == null ? "<destroyed>" : instance.name) + ")");
        }

        private void CustomerActivated(Customer customer)
            => RegisterCustomer(customer, "Customer.ActivateCustomer");

        private void CustomerDeactivated(Customer customer)
        {
            ObserveCustomer(customer, "Customer.DeactivateCustomer");
        }

        private void WorkerActivated(Worker worker)
            => RegisterWorker(worker, "Worker.ActivateWorker");

        private void WorkerDeactivated(Worker worker)
        {
            ObserveWorker(worker, "Worker.DeactivateWorker");
        }

        private NpcStateDeltaMessage CollectCustomerState(Customer customer, int index, float hostTime)
        {
            var state = GetNpcData(KindCustomer, index);
            var active = IsCustomerActive(customer);
            if (active && !state.Active)
            {
                state.Generation++;
                SendCurrentIdentity(KindCustomer, index, customer, state);
            }

            var wasActive = state.Active;
            state.Active = active;
            if (!active)
            {
                return wasActive ? NewInactiveDelta(KindCustomer, index, state.Generation, hostTime) : null;
            }

            var cc = customer.m_CharacterCustom;
            if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
            {
                return null;
            }

            var flags = CollectFlags(customer.m_Anim);
            flags = AddCustomerReactionFlags(customer, flags);
            var grabSequence = GetGrabSequence(index, customer.m_CurrentState);
            var actionKind = customer.m_CurrentState == ECustomerState.TournamentTakePrize
                ? (byte)2 : (byte)1;
            if (customer.IsSmelly())
            {
                flags |= NpcFlags.Smelly;
            }
            if (customer.m_ExclaimationMesh != null && customer.m_ExclaimationMesh.activeSelf)
            {
                flags |= NpcFlags.Exclaim;
            }

            return NewStateDelta(KindCustomer, index, state.Generation, hostTime,
                customer.transform, customer.m_CurrentMoveSpeed, flags, grabSequence, actionKind,
                false, 0, 0L, false);
        }

        private NpcStateDeltaMessage CollectWorkerState(Worker worker, int index, float hostTime)
        {
            var state = GetNpcData(KindWorker, index);
            var active = IsWorkerActive(worker);
            if (active && !state.Active)
            {
                state.Generation++;
                SendCurrentIdentity(KindWorker, index, worker, state);
            }

            var wasActive = state.Active;
            state.Active = active;
            if (!active)
            {
                ReleaseTrackedWorkerBox(state);
                return wasActive ? NewInactiveDelta(KindWorker, index, state.Generation, hostTime) : null;
            }

            var cc = worker.m_CharacterCustom;
            if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
            {
                return null;
            }

            var flags = CollectFlags(worker.m_Anim);
            var moveSpeed = worker.m_Anim == null ? 0f : worker.m_Anim.GetFloat(HashMoveSpeed);
            if (FiCurrentHoldItemBox == null && !_missingHoldFieldLogged)
            {
                _missingHoldFieldLogged = true;
                CoopPlugin.Log.LogError("NpcHostBehaviour: Worker hold-item field is missing; holding-box visuals disabled");
            }

            var holdBox = FiCurrentHoldItemBox == null
                ? null : FiCurrentHoldItemBox.GetValue(worker) as InteractablePackagingBox_Item;
            var holdBig = holdBox != null && holdBox.m_IsBigBox;
            var holdItemType = holdBox == null ? 0 : (int)holdBox.GetItemType();
            var holdBoxNetworkId = 0L;
            var holdBoxOpened = false;
            if (holdBox != null)
            {
                WorldHostBehaviour.TryGetHeldBoxId(holdBox, out holdBoxNetworkId);
                holdBoxOpened = WorldHostBehaviour.IsHeldBoxOpen(holdBox);
            }

            // A released box must be re-announced with its dropped pose, or the guest leaves its
            // local copy parked off-map (the vanished-on-drop bug). The refresh is broadcast here,
            // before the state delta that tells the guest to release the prop, so the ordered link
            // applies the pose first and the release only re-enables its physics.
            if (state.LastHeldBoxId > 0 && state.LastHeldBoxId != holdBoxNetworkId)
            {
                WorldHostBehaviour.NotifyWorkerReleasedBox(state.LastHeldBoxId);
            }

            state.LastHeldBoxId = holdBoxNetworkId;

            if (holdBox != null)
            {
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

            return NewStateDelta(KindWorker, index, state.Generation, hostTime, worker.transform,
                moveSpeed, flags, state.WorkerActionSequence, state.WorkerActionKind, holdBig,
                holdItemType, holdBoxNetworkId, holdBoxOpened);
        }

        private static bool IsCustomerActive(Customer customer)
            => customer != null && customer.m_IsActive && customer.gameObject.activeSelf;

        private static bool IsWorkerActive(Worker worker)
            => worker != null && worker.m_IsActive && worker.gameObject.activeSelf;

        /// <summary>Host: a tracked worker stopped holding its box. Re-announce the box so guests
        /// restore its dropped pose instead of leaving their parked copy invisible.</summary>
        private static void ReleaseTrackedWorkerBox(NpcData state)
        {
            if (state == null || state.LastHeldBoxId <= 0)
            {
                return;
            }

            WorldHostBehaviour.NotifyWorkerReleasedBox(state.LastHeldBoxId);
            state.LastHeldBoxId = 0;
        }

        private static NpcStateDeltaMessage NewInactiveDelta(byte kind, int index, int identity,
            float hostTime)
            => new NpcStateDeltaMessage
            {
                HostTime = hostTime,
                Kind = kind,
                Index = (ushort)index,
                Identity = identity,
                Active = false,
            };

        private static NpcStateDeltaMessage NewStateDelta(byte kind, int index, int identity,
            float hostTime, Transform transform, float speed, NpcFlags flags, int actionSequence,
            byte actionKind, bool holdBig, int holdItemType, long holdBoxNetworkId, bool holdBoxOpened)
            => new NpcStateDeltaMessage
            {
                HostTime = hostTime,
                Kind = kind,
                Index = (ushort)index,
                Identity = identity,
                Active = true,
                Position = transform.position,
                Yaw = transform.eulerAngles.y,
                Speed = speed,
                Flags = (ushort)flags,
                ActionSequence = actionSequence,
                ActionKind = actionKind,
                HoldBig = holdBig,
                HoldItemType = holdItemType,
                HoldBoxNetworkId = holdBoxNetworkId,
                HoldBoxOpened = holdBoxOpened,
            };

        private NpcEntry BuildCustomerEntry(Customer customer, int index, float hostTime)
        {
            var state = GetNpcData(KindCustomer, index);
            var cc = customer.m_CharacterCustom;
            if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
            {
                return null;
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

            return NewEntry(KindCustomer, index, state.Generation, cc.CharacterName,
                customer.m_IsFemale, customer.transform, customer.m_CurrentMoveSpeed, flags,
                GetGrabSequence(index, customer.m_CurrentState),
                customer.m_CurrentState == ECustomerState.TournamentTakePrize ? (byte)2 : (byte)1,
                false, 0, 0L, false);
        }

        private NpcEntry BuildWorkerEntry(Worker worker, int index, float hostTime)
        {
            var state = GetNpcData(KindWorker, index);
            var cc = worker.m_CharacterCustom;
            if (cc == null || string.IsNullOrEmpty(cc.CharacterName))
            {
                return null;
            }

            var flags = CollectFlags(worker.m_Anim);
            var moveSpeed = worker.m_Anim == null ? 0f : worker.m_Anim.GetFloat(HashMoveSpeed);
            var holdBox = FiCurrentHoldItemBox == null
                ? null : FiCurrentHoldItemBox.GetValue(worker) as InteractablePackagingBox_Item;
            var holdBoxNetworkId = 0L;
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

            return NewEntry(KindWorker, index, state.Generation, cc.CharacterName,
                worker.m_IsFemale, worker.transform, moveSpeed, flags, state.WorkerActionSequence,
                state.WorkerActionKind, holdBox != null && holdBox.m_IsBigBox,
                holdBox == null ? 0 : (int)holdBox.GetItemType(), holdBoxNetworkId, holdBoxOpened);
        }

        private static NpcEntry NewEntry(byte kind, int index, int identity, string name,
            bool female, Transform transform, float speed, NpcFlags flags, int actionSequence,
            byte actionKind, bool holdBig, int holdItemType, long holdBoxNetworkId, bool holdBoxOpened)
            => new NpcEntry
            {
                Kind = kind,
                Index = (ushort)index,
                Identity = identity,
                Female = female,
                HasName = true,
                CharName = name,
                Position = transform.position,
                Yaw = transform.eulerAngles.y,
                Speed = speed,
                Flags = (ushort)flags,
                ActionSequence = actionSequence,
                ActionKind = actionKind,
                HoldBig = holdBig,
                HoldItemType = holdItemType,
                HoldBoxNetworkId = holdBoxNetworkId,
                HoldBoxOpened = holdBoxOpened,
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

        private void SendIdentityToPeers(byte kind, ushort index, int identity, bool female,
            string name, Vector3 position)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            foreach (var pair in _joinedConnections)
            {
                var connection = pair.Value;
                if (connection == null || !IsJoinPhase(connection.State))
                {
                    continue;
                }

                _context.Send(pair.Key, new NpcIdentityDeltaMessage
                {
                    Kind = kind,
                    Index = index,
                    Identity = identity,
                    Female = female,
                    CharName = name,
                    Position = position,
                });
            }
        }

        private void SendCurrentIdentity(byte kind, int index, Customer customer, NpcData state)
        {
            if (customer == null || state == null || !state.Active || state.Generation <= 0
                || customer.m_CharacterCustom == null
                || string.IsNullOrEmpty(customer.m_CharacterCustom.CharacterName))
            {
                return;
            }

            SendCurrentIdentity(kind, index, customer.m_CharacterCustom.CharacterName,
                customer.m_IsFemale, state.Generation, customer.transform.position, state);
        }

        private void SendCurrentIdentity(byte kind, int index, Worker worker, NpcData state)
        {
            if (worker == null || state == null || !state.Active || state.Generation <= 0
                || worker.m_CharacterCustom == null
                || string.IsNullOrEmpty(worker.m_CharacterCustom.CharacterName))
            {
                return;
            }

            SendCurrentIdentity(kind, index, worker.m_CharacterCustom.CharacterName,
                worker.m_IsFemale, state.Generation, worker.transform.position, state);
        }

        private void SendCurrentIdentity(byte kind, int index, string name, bool female, int identity,
            Vector3 position, NpcData state)
        {
            if (string.IsNullOrEmpty(name) || identity <= 0 || index < 0 || index >= 256)
            {
                return;
            }

            if (state.IdentityGeneration == identity && state.IdentityName == name
                && state.IdentityFemale == female)
            {
                return;
            }

            state.IdentityName = name;
            state.IdentityFemale = female;
            state.IdentityGeneration = identity;
            SendIdentityToPeers(kind, (ushort)index, identity, female, name, position);
        }

        private void SendStateDelta(byte kind, ushort index, int identity, bool active,
            float hostTime)
        {
            if (_shutdown || !_context.InGame() || identity <= 0)
            {
                return;
            }

            _context.Broadcast(new NpcStateDeltaMessage
            {
                HostTime = hostTime,
                Kind = kind,
                Index = index,
                Identity = identity,
                Active = active,
            });
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

        public static int GetCustomerGeneration(Customer customer)
        {
            if (_active == null || customer == null)
            {
                return 0;
            }

            if (!_active._customerIndices.TryGetValue(customer, out var index)
                && !NpcInterop.TryGetCustomerIndex(customer, out index))
            {
                return 0;
            }

            var state = _active.GetNpcData(KindCustomer, index);
            if (state.Generation == 0)
            {
                state.Generation = 1;
            }
            var active = customer.m_IsActive && customer.gameObject.activeSelf;
            var wasActive = state.Active;
            if (active && !wasActive)
            {
                state.Generation++;
            }

            state.Active = active;
            return state.Generation;
        }

        /// <summary>Maps a host customer transform to its list index plus generation
        /// identity.</summary>
        public static bool TryGetCustomerIdentity(Transform transform, out ushort index, out int identity)
        {
            index = 0;
            identity = 0;
            if (_active == null || transform == null)
            {
                return false;
            }

            foreach (var pair in _active._customers)
            {
                var customer = pair.Value;
                if (customer != null && customer.transform == transform)
                {
                    index = (ushort)pair.Key;
                    identity = GetCustomerGeneration(customer);
                    return true;
                }
            }

            CoopPlugin.Log.LogWarning("Npc host: customer transform has no stable identity");
            return false;
        }


        [HarmonyPatch(typeof(CustomerManager), "Start")]
        private static class CustomerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(CustomerManager __instance)
            {
                _active?.RegisterInitialPopulation(__instance);
                _active?.SignalCustomerManagerReady();
            }
        }

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        [HarmonyPatch(typeof(Customer), "DeactivateCustomer")]
        private static class CustomerPopulationPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
            {
                if (_active == null || __instance == null)
                {
                    return;
                }

                if (__instance.m_IsActive && __instance.gameObject.activeSelf)
                {
                    _active.CustomerActivated(__instance);
                }
                else
                {
                    _active.CustomerDeactivated(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "Update")]
        private static class CustomerUpdatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
                => _active?.ObserveCustomer(__instance, "Customer.Update");
        }

        [HarmonyPatch(typeof(WorkerManager), "Start")]
        private static class WorkerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(WorkerManager __instance)
            {
                _active?.RegisterInitialWorkers(__instance);
                _active?.SignalWorkerManagerReady();
            }
        }

        [HarmonyPatch(typeof(Worker), "ActivateWorker")]
        [HarmonyPatch(typeof(Worker), "DeactivateWorker")]
        private static class WorkerPopulationPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance)
            {
                if (_active == null || __instance == null)
                {
                    return;
                }

                if (__instance.m_IsActive && __instance.gameObject.activeSelf)
                {
                    _active.WorkerActivated(__instance);
                }
                else
                {
                    _active.WorkerDeactivated(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(Worker), "Update")]
        private static class WorkerUpdatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance)
                => _active?.ObserveWorker(__instance, "Worker.Update");
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
                        && TryGetCustomerIdentity(followTransform, out var index, out var identity))
                    {
                        _active._context.Broadcast(new NpcSpeechMessage
                        {
                            Kind = 0,
                            Index = index,
                            Identity = identity,
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
                    || !TryGetCustomerIdentity(followTransform, out var index, out var identity))
                {
                    return;
                }
                _active._context.Broadcast(new NpcMoneyPopupMessage
                {
                    Index = index,
                    Identity = identity,
                    Amount = price,
                    OffsetUp = offsetUp
                });
            }
        }
    }
}
