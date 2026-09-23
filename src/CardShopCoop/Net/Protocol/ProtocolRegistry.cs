using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CardShopCoop.Net.Protocol
{
    /// <summary>
    /// Process-wide protocol catalog. Descriptors and handler bindings are open during bootstrap
    /// and are captured together when a session starts. Changes made during a session are queued
    /// for the next session without changing the peer-visible snapshot.
    /// </summary>
    public sealed class ProtocolRegistry : IDisposable
    {
        private sealed class DescriptorRegistration
        {
            public MessageDescriptor Descriptor;
            public ProtocolRegistrationOwner Owner;
            public bool Retired;
        }

        private sealed class HandlerRegistration
        {
            public ProtocolHandlerBinding Binding;
            public ProtocolRegistrationOwner Owner;
            public bool Retired;
        }

        private sealed class RegistrationHandle : IProtocolRegistration
        {
            private readonly Action _dispose;
            private readonly Func<RegistrationState> _state;
            private int _disposed;

            public ProtocolRegistrationOwner Owner
            {
                get;
            }

            public RegistrationState State
            {
                get
                {
                    return VolatileState();
                }
            }

            public RegistrationHandle(ProtocolRegistrationOwner owner, Action dispose,
                Func<RegistrationState> state)
            {
                Owner = owner;
                _dispose = dispose;
                _state = state;
            }

            private RegistrationState VolatileState()
            {
                return _state();
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _dispose();
                }
            }
        }

        private sealed class SessionLease : IProtocolSessionLease
        {
            private readonly ProtocolRegistry _registry;
            private readonly ProtocolSnapshot _snapshot;
            private int _disposed;

            public int Generation => _snapshot.Generation;

            public ProtocolSnapshot Snapshot => _snapshot;

            public SessionLease(ProtocolRegistry registry, ProtocolSnapshot snapshot)
            {
                _registry = registry;
                _snapshot = snapshot;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
                    _registry.EndSession(this);
            }
        }

        private sealed class CoreOwnerHolder
        {
            public readonly ProtocolRegistrationOwner Owner
                = new ProtocolRegistrationOwner("CardShopCoop", true);
        }

        private static readonly CoreOwnerHolder Core = new();
        public static ProtocolRegistry Default
        {
            get;
        } = new ProtocolRegistry();

        private readonly object _gate = new();
        private readonly Dictionary<string, DescriptorRegistration> _activeDescriptors
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DescriptorRegistration> _pendingDescriptors
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HandlerRegistration> _handlers
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HandlerRegistration> _pendingHandlers
            = new(StringComparer.Ordinal);
        private ProtocolSnapshot _snapshot;
        private bool _frozen;
        private SessionLease _sessionLease;
        private int _generation;
        private bool _coreAssemblyRegistered;
        private bool _disposed;

        public event Action<MessageRejection> Rejected;

        public ProtocolRegistry()
        {
            _snapshot = new ProtocolSnapshot(0, false,
                new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal),
                new Dictionary<string, ProtocolHandlerBinding>(StringComparer.Ordinal));
        }

        public bool IsFrozen
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    return _frozen;
                }
            }
        }

        public ProtocolSnapshot CurrentSnapshot
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    return _snapshot;
                }
            }
        }

        public static ProtocolRegistrationOwner CreateOwner(string id)
        {
            return new ProtocolRegistrationOwner(id);
        }

        internal static ProtocolRegistrationOwner CoreOwner
        {
            get
            {
                return Core.Owner;
            }
        }

        /// <summary>
        /// Starts one exclusive session and publishes registrations pending from a prior session.
        /// The returned lease keeps the resulting snapshot immutable until it is disposed.
        /// </summary>
        public IProtocolSessionLease BeginSession()
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_sessionLease != null || _frozen)
                {
                    throw new InvalidOperationException(
                        "A protocol session is already active for this registry");
                }

                var sessionDescriptors = new Dictionary<string, MessageDescriptor>(
                    StringComparer.Ordinal);
                var nextDescriptors = new Dictionary<string, DescriptorRegistration>(
                    StringComparer.Ordinal);
                foreach (var pair in _activeDescriptors)
                {
                    if (!pair.Value.Retired)
                    {
                        nextDescriptors.Add(pair.Key, pair.Value);
                        sessionDescriptors.Add(pair.Key, pair.Value.Descriptor);
                    }
                }
                foreach (var pair in _pendingDescriptors)
                {
                    if (!pair.Value.Retired)
                    {
                        nextDescriptors.Add(pair.Key, pair.Value);
                        sessionDescriptors.Add(pair.Key, pair.Value.Descriptor);
                    }
                }

                if (sessionDescriptors.Count > ProtocolSnapshot.MaxMessageCount)
                {
                    throw new ProtocolRegistrationException("A protocol snapshot cannot contain more than "
                        + ProtocolSnapshot.MaxMessageCount + " messages");
                }

                var nextHandlers = new Dictionary<string, HandlerRegistration>(
                    StringComparer.Ordinal);
                foreach (var pair in _handlers)
                {
                    if (!pair.Value.Retired && sessionDescriptors.ContainsKey(pair.Key))
                        nextHandlers.Add(pair.Key, pair.Value);
                }
                foreach (var pair in _pendingHandlers)
                {
                    if (!pair.Value.Retired && sessionDescriptors.ContainsKey(pair.Key))
                        nextHandlers.Add(pair.Key, pair.Value);
                }

                _activeDescriptors.Clear();
                foreach (var pair in nextDescriptors)
                {
                    _activeDescriptors.Add(pair.Key, pair.Value);
                }
                _pendingDescriptors.Clear();

                _handlers.Clear();
                foreach (var pair in nextHandlers)
                {
                    _handlers.Add(pair.Key, pair.Value);
                }
                _pendingHandlers.Clear();

                _generation++;
                _frozen = true;
                RebuildSnapshotLocked(sessionDescriptors);
                _sessionLease = new SessionLease(this, _snapshot);
                return _sessionLease;
            }
        }

        private void EndSession(SessionLease lease)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_sessionLease, lease))
                    return;

                // Keep the lease's snapshot object untouched, but publish a new unfrozen view.
                // Registrations made while the lease was active remain pending until the next
                // BeginSession call and therefore are deliberately absent from this view.
                _sessionLease = null;
                _frozen = false;
                RebuildSnapshotLocked();
            }
        }

        /// <summary>
        /// Creates an owner-scoped transaction containing every attributed message in the
        /// supplied assembly. No AppDomain or dependency assembly is scanned.
        /// </summary>
        public IProtocolRegistration RegisterAssembly(Assembly assembly,
            ProtocolRegistrationOwner owner)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
            RequireOwner(owner);
            lock (_gate)
            {
                ThrowIfDisposedLocked();
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                var detail = new List<string>();
                if (error.LoaderExceptions != null)
                {
                    for (var i = 0; i < error.LoaderExceptions.Length; i++)
                    {
                        if (error.LoaderExceptions[i] != null)
                        {
                            detail.Add(error.LoaderExceptions[i].Message);
                        }
                    }
                }
                throw new ProtocolRegistrationException(
                    "Could not load every type from assembly " + assembly.FullName
                    + "; registration was aborted: " + string.Join(" | ", detail), error);
            }

            var descriptors = new List<MessageDescriptor>();
            for (var i = 0; i < types.Length; i++)
            {
                var type = types[i];
                NetworkMessageAttribute attribute;
                try
                {
                    attribute = (NetworkMessageAttribute)Attribute.GetCustomAttribute(type,
                        typeof(NetworkMessageAttribute));
                }
                catch (Exception error)
                {
                    throw CoreDiscoveryFailure(assembly,
                        "could not inspect attributes on " + type.FullName, error);
                }
                if (attribute == null)
                {
                    continue;
                }
                if (!typeof(INetMessage).IsAssignableFrom(type))
                {
                    throw new ProtocolRegistrationException(type.FullName
                        + " has [NetworkMessage] but is not an INetMessage");
                }
                descriptors.Add(CreateDescriptor(type, attribute));
            }

            return RegisterDescriptors(descriptors, owner);
        }

        public IProtocolRegistration RegisterAssembly(Assembly assembly, string ownerId)
        {
            return RegisterAssembly(assembly, CreateOwner(ownerId));
        }

        public IProtocolRegistration RegisterMessage(MessageDescriptor descriptor,
            ProtocolRegistrationOwner owner)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            RequireOwner(owner);
            return RegisterDescriptors(new[] { descriptor }, owner);
        }

        public IProtocolRegistration RegisterMessage(MessageDescriptor descriptor, string ownerId)
        {
            return RegisterMessage(descriptor, CreateOwner(ownerId));
        }

        public IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, ProtocolRegistrationOwner owner)
        {
            return RegisterHandlers(new[]
            {
                new ProtocolHandlerRegistration(messageType, handler),
            }, owner);
        }

        public IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, string ownerId)
        {
            return RegisterHandler(messageType, handler, CreateOwner(ownerId));
        }

        public IProtocolRegistration RegisterHandlers(
            IReadOnlyList<ProtocolHandlerRegistration> registrations,
            ProtocolRegistrationOwner owner)
        {
            if (registrations == null)
            {
                throw new ArgumentNullException(nameof(registrations));
            }
            RequireOwner(owner);
            if (registrations.Count == 0)
            {
                throw new ArgumentException("At least one handler is required", nameof(registrations));
            }

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var pending = new List<HandlerRegistration>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < registrations.Count; i++)
                {
                    var registration = registrations[i]
                        ?? throw new ProtocolRegistrationException("Null handler registration");
                    if (!typeof(INetMessage).IsAssignableFrom(registration.MessageType)
                        || registration.MessageType.IsInterface
                        || registration.MessageType.IsAbstract
                        || registration.MessageType.IsGenericType)
                    {
                        throw new ProtocolRegistrationException("A handler requires a concrete INetMessage type");
                    }
                    var descriptorRegistration = FindDescriptorLocked(registration.MessageType);
                    if (descriptorRegistration == null || descriptorRegistration.Retired)
                    {
                        throw new ProtocolRegistrationException("No message descriptor is registered for "
                            + registration.MessageType?.FullName);
                    }
                    var descriptor = descriptorRegistration.Descriptor;
                    var hasLiveHandler = _handlers.TryGetValue(descriptor.WireName,
                        out var activeHandler) && !activeHandler.Retired;
                    var hasPendingHandler = _pendingHandlers.ContainsKey(descriptor.WireName);
                    if (!names.Add(descriptor.WireName)
                        || hasLiveHandler || hasPendingHandler)
                    {
                        throw new ProtocolRegistrationException("Duplicate network handler for "
                            + descriptor.WireName);
                    }

                    pending.Add(new HandlerRegistration
                    {
                        Owner = owner,
                        Binding = CreateBinding(descriptor, registration.Handler, owner),
                    });
                }

                for (var i = 0; i < pending.Count; i++)
                {
                    var token = pending[i].Binding.MessageType.FullName;
                    if (_frozen)
                        _pendingHandlers.Add(token, pending[i]);
                    else
                        _handlers.Add(token, pending[i]);
                }
                if (!_frozen)
                    RebuildSnapshotLocked();
                return CreateHandlerHandle(owner, pending);
            }
        }

        public bool TryGetDescriptor(Type messageType, out MessageDescriptor descriptor)
        {
            return CurrentSnapshot.TryGet(messageType, out descriptor);
        }

        /// <summary>Looks in the bootstrap/pending catalog as well as the current session.</summary>
        public bool TryGetRegisteredDescriptor(Type messageType, out MessageDescriptor descriptor)
        {
            descriptor = null;
            if (messageType == null || messageType.FullName == null)
            {
                return false;
            }
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var registration = FindDescriptorLocked(messageType);
                if (registration == null || registration.Retired)
                {
                    return false;
                }
                descriptor = registration.Descriptor;
                return true;
            }
        }

        public bool TryGetDescriptor(string wireName, out MessageDescriptor descriptor)
        {
            return CurrentSnapshot.TryGet(wireName, out descriptor);
        }

        internal bool TryGetHandler(Type messageType, out ProtocolHandlerBinding handler)
        {
            return CurrentSnapshot.TryGetHandler(messageType, out handler);
        }

        internal void RaiseRejection(MessageRejection rejection)
        {
            Rejected?.Invoke(rejection);
        }

        internal static void EnsureCoreAssemblyRegistered(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
            lock (Default._gate)
            {
                Default.ThrowIfDisposedLocked();
                if (Default._coreAssemblyRegistered)
                {
                    return;
                }
                // Do not use the public assembly-registration path here. The built-in assembly
                // contains optional Steamworks transport infrastructure, so its type graph can
                // be partially loadable on a LAN-only install. External registration remains
                // deliberately fail-fast in RegisterAssembly above.
                var descriptors = Default.DiscoverCoreDescriptors(assembly);
                Default.RegisterDescriptors(descriptors, Core.Owner);
                Default._coreAssemblyRegistered = true;
            }
        }

        public void Dispose()
        {
            List<ProtocolHandlerBinding> deactivate;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                deactivate = new List<ProtocolHandlerBinding>(_handlers.Count
                    + _pendingHandlers.Count);
                foreach (var registration in _handlers.Values)
                    deactivate.Add(registration.Binding);
                foreach (var registration in _pendingHandlers.Values)
                    deactivate.Add(registration.Binding);
                _disposed = true;
                _activeDescriptors.Clear();
                _pendingDescriptors.Clear();
                _handlers.Clear();
                _pendingHandlers.Clear();
                _sessionLease = null;
                _frozen = false;
                _snapshot = new ProtocolSnapshot(_generation, false,
                    new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal),
                    new Dictionary<string, ProtocolHandlerBinding>(StringComparer.Ordinal));
                Rejected = null;
            }

            for (var i = 0; i < deactivate.Count; i++)
                deactivate[i].DeactivateAndWait();
        }

        private IProtocolRegistration RegisterDescriptors(IList<MessageDescriptor> descriptors,
            ProtocolRegistrationOwner owner)
        {
            if (descriptors == null || descriptors.Count == 0)
            {
                throw new ProtocolRegistrationException("At least one message descriptor is required");
            }

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (CountLiveDescriptorsLocked() + descriptors.Count > ProtocolSnapshot.MaxMessageCount)
                {
                    throw new ProtocolRegistrationException("A protocol snapshot cannot contain more than "
                        + ProtocolSnapshot.MaxMessageCount + " messages");
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < descriptors.Count; i++)
                {
                    var descriptor = descriptors[i]
                        ?? throw new ProtocolRegistrationException("Null message descriptor");
                    if (!names.Add(descriptor.WireName))
                    {
                        throw new ProtocolRegistrationException("Duplicate network message registration: "
                            + descriptor.WireName);
                    }
                    var hasActiveDescriptor = _activeDescriptors.TryGetValue(descriptor.WireName,
                        out var activeDescriptor) && !activeDescriptor.Retired;
                    if (hasActiveDescriptor || _pendingDescriptors.ContainsKey(descriptor.WireName))
                    {
                        var existing = hasActiveDescriptor
                            ? activeDescriptor : _pendingDescriptors[descriptor.WireName];
                        throw new ProtocolRegistrationException("Duplicate network message registration for "
                            + descriptor.WireName + " (already owned by " + existing.Owner.Id + ")");
                    }
                }

                var registrations = new List<DescriptorRegistration>();
                var pending = _frozen;
                for (var i = 0; i < descriptors.Count; i++)
                {
                    var registration = new DescriptorRegistration
                    {
                        Descriptor = descriptors[i],
                        Owner = owner,
                    };
                    registrations.Add(registration);
                    if (pending)
                    {
                        _pendingDescriptors.Add(descriptors[i].WireName, registration);
                    }
                    else
                    {
                        if (_activeDescriptors.TryGetValue(descriptors[i].WireName,
                            out var retiredActive) && retiredActive.Retired)
                        {
                            _activeDescriptors.Remove(descriptors[i].WireName);
                        }
                        if (_handlers.TryGetValue(descriptors[i].WireName,
                            out var retiredHandler) && retiredHandler.Retired)
                        {
                            _handlers.Remove(descriptors[i].WireName);
                        }
                        _activeDescriptors.Add(descriptors[i].WireName, registration);
                    }
                }
                if (!_frozen)
                    RebuildSnapshotLocked();
                return CreateDescriptorHandle(owner, registrations);
            }
        }

        private IList<MessageDescriptor> DiscoverCoreDescriptors(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                types = GetSafeCoreTypes(assembly, error);
            }

            var descriptors = new List<MessageDescriptor>();
            for (var i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null)
                {
                    // GetSafeCoreTypes rejects every partial-load shape except the one
                    // explicitly proven to be optional infrastructure.
                    throw CoreDiscoveryFailure(assembly,
                        "a null type remained after validating a partial type load", null);
                }

                var attribute = (NetworkMessageAttribute)Attribute.GetCustomAttribute(type,
                    typeof(NetworkMessageAttribute));
                if (attribute == null)
                {
                    continue;
                }
                if (!typeof(INetMessage).IsAssignableFrom(type))
                {
                    throw CoreDiscoveryFailure(assembly, type.FullName
                        + " has [NetworkMessage] but is not an INetMessage", null);
                }
                descriptors.Add(CreateDescriptor(type, attribute));
            }

            return descriptors;
        }

        private static Type[] GetSafeCoreTypes(Assembly assembly,
            ReflectionTypeLoadException error)
        {
            var types = error.Types;
            if (types == null)
            {
                throw CoreDiscoveryFailure(assembly,
                    "ReflectionTypeLoadException did not provide a type list", error);
            }

            var unloadableCount = 0;
            for (var i = 0; i < types.Length; i++)
            {
                if (types[i] == null)
                {
                    unloadableCount++;
                }
            }

            var loaderExceptions = error.LoaderExceptions;
            if (unloadableCount == 0 || loaderExceptions == null
                || loaderExceptions.Length != unloadableCount)
            {
                throw CoreDiscoveryFailure(assembly,
                    "the partial type load could not account for every unloadable type", error);
            }

            for (var i = 0; i < loaderExceptions.Length; i++)
            {
                if (!IsOptionalSteamworksInfrastructureFailure(assembly,
                    loaderExceptions[i]))
                {
                    throw CoreDiscoveryFailure(assembly,
                        "an unloadable type may contain a network message", error);
                }
            }

            var message = "Core protocol discovery skipped " + unloadableCount
                + " unloadable Steamworks infrastructure type(s); all loadable protocol"
                + " messages remain subject to normal attributed discovery";
            CoopPlugin.Log?.LogWarning(message);
            var loadableTypes = new List<Type>(types.Length - unloadableCount);
            for (var i = 0; i < types.Length; i++)
            {
                if (types[i] != null)
                {
                    loadableTypes.Add(types[i]);
                }
            }
            return loadableTypes.ToArray();
        }

        private static bool IsOptionalSteamworksInfrastructureFailure(Assembly assembly,
            Exception error)
        {
            // The assembly identity is intentional: this is the maintained core boundary where
            // the optional Steamworks reference is confined to transport infrastructure. A
            // caller-supplied assembly never receives this exception, so it remains fail-fast.
            if (assembly != typeof(MessageRegistry).Assembly
                || !(error is FileNotFoundException missing))
            {
                return false;
            }

            if (ContainsOptionalSteamworksAssemblyName(missing.FileName))
            {
                return true;
            }

            return ContainsOptionalSteamworksAssemblyName(error.Message);
        }

        private static bool ContainsOptionalSteamworksAssemblyName(string value)
        {
            return value != null && value.IndexOf("com.rlabrecque.steamworks.net",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ProtocolRegistrationException CoreDiscoveryFailure(Assembly assembly,
            string detail, Exception error)
        {
            var message = "Could not safely discover core protocol messages from assembly "
                + assembly.FullName + ": " + detail;
            CoopPlugin.Log?.LogError(message);
            return error == null
                ? new ProtocolRegistrationException(message)
                : new ProtocolRegistrationException(message, error);
        }

        private IProtocolRegistration CreateDescriptorHandle(ProtocolRegistrationOwner owner,
            IList<DescriptorRegistration> registrations)
        {
            var disposed = false;
            return new RegistrationHandle(owner, () =>
            {
                var deactivate = new List<ProtocolHandlerBinding>();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposed = true;
                        return;
                    }
                    if (disposed)
                    {
                        return;
                    }
                    disposed = true;
                    for (var i = 0; i < registrations.Count; i++)
                    {
                        var registration = registrations[i];
                        registration.Retired = true;
                        if (_handlers.TryGetValue(registration.Descriptor.WireName, out var handler))
                        {
                            // A descriptor owner unload must never leave a callable delegate
                            // attached to the retired wire contract.
                            handler.Retired = true;
                            deactivate.Add(handler.Binding);
                            if (!_frozen)
                                _handlers.Remove(registration.Descriptor.WireName);
                        }
                        if (_pendingHandlers.TryGetValue(registration.Descriptor.WireName,
                            out var pendingHandler))
                        {
                            pendingHandler.Retired = true;
                            deactivate.Add(pendingHandler.Binding);
                            _pendingHandlers.Remove(registration.Descriptor.WireName);
                        }
                        if (_pendingDescriptors.TryGetValue(registration.Descriptor.WireName,
                            out var pending) && ReferenceEquals(pending, registration))
                        {
                            _pendingDescriptors.Remove(registration.Descriptor.WireName);
                        }
                        if (!_frozen && _activeDescriptors.TryGetValue(registration.Descriptor.WireName,
                            out var active) && ReferenceEquals(active, registration))
                        {
                            _activeDescriptors.Remove(registration.Descriptor.WireName);
                        }
                    }
                    if (!_frozen)
                        RebuildSnapshotLocked();
                }
                for (var i = 0; i < deactivate.Count; i++)
                    deactivate[i].DeactivateAndWait();
            }, () =>
            {
                lock (_gate)
                {
                    if (disposed || _disposed)
                    {
                        return RegistrationState.Disposed;
                    }
                    for (var i = 0; i < registrations.Count; i++)
                    {
                        if (_pendingDescriptors.ContainsKey(registrations[i].Descriptor.WireName))
                        {
                            return RegistrationState.PendingNextSession;
                        }
                    }
                    return RegistrationState.Active;
                }
            });
        }

        private IProtocolRegistration CreateHandlerHandle(ProtocolRegistrationOwner owner,
            IList<HandlerRegistration> registrations)
        {
            var disposed = false;
            return new RegistrationHandle(owner, () =>
            {
                var deactivate = new List<ProtocolHandlerBinding>();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        disposed = true;
                        return;
                    }
                    if (disposed)
                    {
                        return;
                    }
                    disposed = true;
                    for (var i = 0; i < registrations.Count; i++)
                    {
                        var registration = registrations[i];
                        registration.Retired = true;
                        var name = registration.Binding.MessageType.FullName;
                        if (_handlers.TryGetValue(name, out var active)
                            && ReferenceEquals(active, registration))
                        {
                            deactivate.Add(registration.Binding);
                            if (!_frozen)
                                _handlers.Remove(name);
                        }
                        if (_pendingHandlers.TryGetValue(name, out var pending)
                            && ReferenceEquals(pending, registration))
                        {
                            deactivate.Add(pending.Binding);
                            _pendingHandlers.Remove(name);
                        }
                    }
                    if (!_frozen)
                        RebuildSnapshotLocked();
                }
                for (var i = 0; i < deactivate.Count; i++)
                    deactivate[i].DeactivateAndWait();
            }, () =>
            {
                lock (_gate)
                {
                    if (disposed || _disposed)
                    {
                        return RegistrationState.Disposed;
                    }
                    for (var i = 0; i < registrations.Count; i++)
                    {
                        var registration = registrations[i];
                        if (registration.Retired)
                        {
                            return RegistrationState.Disposed;
                        }
                        var name = registration.Binding.MessageType.FullName;
                        if (_pendingHandlers.TryGetValue(name, out var pending)
                            && ReferenceEquals(pending, registration)
                            && !pending.Retired)
                        {
                            return RegistrationState.PendingNextSession;
                        }
                    }
                    return RegistrationState.Active;
                }
            });
        }

        private void RebuildSnapshotLocked(IDictionary<string, MessageDescriptor> frozenDescriptors = null)
        {
            var descriptors = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
            if (frozenDescriptors != null)
            {
                foreach (var pair in frozenDescriptors)
                {
                    descriptors[pair.Key] = pair.Value;
                }
            }
            else if (_frozen)
            {
                // A frozen descriptor catalog is immutable for this session. Descriptor
                // handles may retire it, but the old entry remains until the next freeze.
                foreach (var pair in _snapshot.Descriptors)
                {
                    descriptors[pair.Key] = pair.Value;
                }
            }
            else
            {
                foreach (var pair in _activeDescriptors)
                {
                    if (!pair.Value.Retired)
                    {
                        descriptors[pair.Key] = pair.Value.Descriptor;
                    }
                }
            }

            var handlers = new Dictionary<string, ProtocolHandlerBinding>(StringComparer.Ordinal);
            foreach (var pair in _handlers)
            {
                if (descriptors.ContainsKey(pair.Key) && !pair.Value.Retired)
                {
                    handlers[pair.Key] = pair.Value.Binding;
                }
            }
            _snapshot = new ProtocolSnapshot(_generation, _frozen, descriptors, handlers);
        }

        private DescriptorRegistration FindDescriptorLocked(Type messageType)
        {
            if (messageType == null || messageType.FullName == null)
            {
                return null;
            }
            if (_activeDescriptors.TryGetValue(messageType.FullName, out var active)
                && !active.Retired && active.Descriptor.MessageType == messageType)
            {
                return active;
            }
            if (_pendingDescriptors.TryGetValue(messageType.FullName, out var pending)
                && !pending.Retired && pending.Descriptor.MessageType == messageType)
            {
                return pending;
            }
            return null;
        }

        private int CountLiveDescriptorsLocked()
        {
            var count = 0;
            foreach (var pair in _activeDescriptors)
            {
                if (!pair.Value.Retired)
                {
                    count++;
                }
            }
            foreach (var pair in _pendingDescriptors)
            {
                if (!pair.Value.Retired)
                {
                    count++;
                }
            }
            return count;
        }

        private static MessageDescriptor CreateDescriptor(Type type,
            NetworkMessageAttribute attribute)
        {
            return new MessageDescriptor(type, attribute.Reliability);
        }

        private static ProtocolHandlerBinding CreateBinding(MessageDescriptor descriptor,
            ProtocolMessageHandler handler, ProtocolRegistrationOwner owner)
        {
            return new ProtocolHandlerBinding(descriptor.MessageType, handler, owner);
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ProtocolRegistry));
            }
        }

        private static void RequireOwner(ProtocolRegistrationOwner owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
        }

    }

    public sealed class ProtocolRegistrationException : InvalidOperationException
    {
        public ProtocolRegistrationException(string message) : base(message)
        {
        }

        public ProtocolRegistrationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
