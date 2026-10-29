using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Util;
using UnityEngine;
using CoopLog = CardShopCoop.Api.CoopLog;
using CardShopCoop.Net;

namespace CardShopCoop.Runtime
{
    internal sealed class CoopBehaviourRegistry
    {
        /// <summary>
        /// One discovered behaviour. External session behaviours are created and bound during
        /// session preparation but stay inactive until the transport chain is up: their Unity
        /// lifecycle and session-start events must not observe a session without a live transport.
        /// </summary>
        private sealed class Entry
        {
            internal MonoBehaviour Behaviour;
            internal bool External;
            internal bool Activated;
        }

        private readonly List<Entry> _behaviours = new List<Entry>();
        private readonly Dictionary<Type, List<MethodInfo>> _events = new();
        private readonly HashSet<Type> _createdTypes = new HashSet<Type>();
        private readonly GameObject _parent;
        private readonly CoopRuntimeContext _context;
        private bool _disposed;
        private bool _sessionStarted;

        private CoopBehaviourRegistry(GameObject parent, CoopRuntimeContext context)
        {
            _parent = parent;
            _context = context;
        }

        public static CoopBehaviourRegistry CreateServer(GameObject parent, CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, Discover<ServerBehaviourAttribute>(true),
                activateOnCreate: false);
        }

        public static CoopBehaviourRegistry CreateClient(GameObject parent, CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, Discover<ClientBehaviourAttribute>(true),
                activateOnCreate: false);
        }

        public static CoopBehaviourRegistry CreatePersistent(GameObject parent,
            CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, Discover<PersistentBehaviourAttribute>(true),
                activateOnCreate: true);
        }

        /// <summary>
        /// Adds external <c>[PersistentBehaviour]</c> types discovered after this registry was
        /// built. The persistent registry is created during Awake, before dependents are loaded,
        /// so external persistent behaviours are attached here from the first frame.
        /// </summary>
        public void AddExternalPersistent()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                foreach (var type in DiscoverIn(ExternalCoopMods.Instance.Assemblies,
                    typeof(PersistentBehaviourAttribute)))
                {
                    if (_createdTypes.Contains(type))
                    {
                        continue;
                    }

                    try
                    {
                        AddType(type, true);
                        CoopPlugin.Log?.LogInfo("[api] created external persistent behaviour "
                            + type.FullName);
                    }
                    catch (Exception error)
                    {
                        CoopPlugin.Log?.LogError("[api] external persistent behaviour "
                            + type.FullName + " failed to start: " + error);
                    }
                }
            }
            catch (Exception error)
            {
                // The enumeration itself (assembly reflection) must not escape into Update.
                CoopPlugin.Log?.LogError("[api] external persistent behaviour scan failed: " + error);
            }
        }

        private static CoopBehaviourRegistry CreateInternal(GameObject parent,
            CoopRuntimeContext context, IEnumerable<Type> types, bool activateOnCreate)
        {
            var registry = new CoopBehaviourRegistry(parent, context);
            try
            {
                foreach (var type in types)
                {
                    if (type.Assembly == typeof(CoopBehaviourRegistry).Assembly)
                    {
                        registry.AddType(type, false);
                        continue;
                    }

                    try
                    {
                        registry.AddType(type, false);
                    }
                    catch (Exception error)
                    {
                        // A broken external mod must not abort session activation for everyone.
                        // The persistent path isolates per type the same way.
                        CoopPlugin.Log?.LogError("[api] external behaviour " + type.FullName
                            + " failed to start; skipping it: " + error);
                    }
                }

                // Live registries prepare inactive: CoopCore freezes the EIM catalog and then
                // activates the core behaviours, then activates external session behaviours once
                // the transport chain is started. The persistent registry has no session transport
                // to wait for, so its process-lifetime behaviours activate immediately.
                if (activateOnCreate)
                {
                    registry.ActivateAll();
                }
                return registry;
            }
            catch (Exception startupError)
            {
                try
                {
                    registry.Dispose();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Co-op behaviour startup and rollback failed.",
                        startupError, cleanupError);
                }
                throw;
            }
        }

        private void AddType(Type type, bool activate)
        {
            if (!_createdTypes.Add(type))
            {
                return;
            }

            GameObject child = null;
            MonoBehaviour behaviour = null;
            Entry entry = null;
            try
            {
                child = new GameObject(type.Name);
                child.transform.SetParent(_parent.transform, false);
                child.SetActive(false);
                behaviour = (child.AddComponent(type)) as MonoBehaviour;
                if (behaviour == null)
                {
                    throw new InvalidOperationException(type.FullName + " is not a MonoBehaviour.");
                }

                // Built-in modules receive the rich context; external mods receive the same live
                // session through the public base. A plain MonoBehaviour is left to read
                // CoopApi.Context itself.
                if (behaviour is CoopBehaviour internalBehaviour)
                {
                    internalBehaviour.SetRuntimeContext(_context);
                }
                else if (behaviour is Api.CoopBehaviour externalBehaviour)
                {
                    externalBehaviour.SetContext(_context);
                }

                // Register events before adding to the dispatch list so a signature failure cannot
                // leave a half-added target behind. Attributed handlers are part of the same
                // lifecycle: the registry binds them before OnEnable, so a behaviour never has to
                // call RegisterAttributedHandlers itself.
                RegisterBehaviourEvents(behaviour);
                _context.Messages.RegisterAttributedHandlers(behaviour);
                entry = new Entry
                {
                    Behaviour = behaviour,
                    External = type.Assembly != typeof(CoopBehaviourRegistry).Assembly,
                };
                _behaviours.Add(entry);

                // Activation last, so the context, event map and handlers are complete before
                // OnEnable runs. Only the persistent registry activates during creation; a live
                // registry waits for CoopCore's ordered ActivateInternal/ActivateExternal calls.
                if (activate)
                {
                    ActivateEntry(entry);

                    // A persistent behaviour added after the session started (the first-frame
                    // external scan) still gets the start event. The registry-wide SessionStarted
                    // call already fired for the behaviours that existed then.
                    if (_sessionStarted)
                    {
                        InvokeFor<OnSessionStartedAttribute>(behaviour);
                    }
                }
            }
            catch
            {
                _createdTypes.Remove(type);
                if (entry != null)
                {
                    _behaviours.Remove(entry);
                }
                if (behaviour != null)
                {
                    try
                    {
                        _context.Messages.UnregisterAttributedHandlers(behaviour);
                    }
                    catch (Exception cleanupError)
                    {
                        CoopPlugin.Log?.LogWarning("[api] could not roll back handlers for "
                            + type.FullName + ": " + cleanupError.Message);
                    }
                }
                if (child != null)
                {
                    UnityEngine.Object.Destroy(child);
                }
                throw;
            }
        }

        /// <summary>Activates the core module behaviours. Called after CoopCore has frozen the
        /// extended-information catalog and before the transport starts, preserving the internal
        /// pre-transport seed contract.</summary>
        public void ActivateInternal()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var entry in _behaviours)
            {
                if (!entry.External)
                {
                    ActivateEntry(entry);
                }
            }
        }

        /// <summary>
        /// Activates external session behaviours once the transport chain is started. Handlers were
        /// already bound during preparation; only the Unity lifecycle and session-start event are
        /// deferred, so a mod's OnEnable/OnSessionStarted can safely use Context.Send/Broadcast and
        /// extended information.
        /// </summary>
        public void ActivateExternal()
        {
            if (_disposed)
            {
                return;
            }

            List<MonoBehaviour> activated = null;
            foreach (var entry in _behaviours)
            {
                if (!entry.External || entry.Activated)
                {
                    continue;
                }

                try
                {
                    if (ActivateEntry(entry))
                    {
                        activated ??= new List<MonoBehaviour>();
                        activated.Add(entry.Behaviour);
                    }
                }
                catch (Exception error)
                {
                    // A broken external mod must not take the session down.
                    CoopPlugin.Log?.LogError("[api] external behaviour "
                        + entry.Behaviour?.GetType().FullName + " failed to activate: " + error);
                }
            }

            if (activated == null)
            {
                return;
            }

            CoopPlugin.Log?.LogInfo("[api] activated " + activated.Count
                + " external session behaviour(s) after transport ready");
            if (!_sessionStarted)
            {
                return;
            }

            // Deliver after every external OnEnable has run, matching the core ordering.
            for (var i = 0; i < activated.Count; i++)
            {
                InvokeFor<OnSessionStartedAttribute>(activated[i]);
            }
        }

        private bool ActivateEntry(Entry entry)
        {
            if (entry == null || entry.Activated)
            {
                return false;
            }

            entry.Behaviour.gameObject.SetActive(true);
            entry.Activated = true;
            return true;
        }

        private void ActivateAll()
        {
            foreach (var entry in _behaviours)
            {
                ActivateEntry(entry);
            }
        }

        private void RegisterBehaviourEvents(MonoBehaviour behaviour)
        {
            foreach (var method in behaviour.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = method.GetCustomAttributes().FirstOrDefault(a =>
                    a is OnClientJoinedAttribute
                    || a is OnClientDisconnectedAttribute || a is OnFullyJoinedAttribute
                    || a is OnSessionStartedAttribute || a is OnSessionStoppedAttribute);
                if (attribute == null)
                {
                    continue;
                }

                Validate(attribute, method);
                var key = attribute.GetType();
                if (!_events.TryGetValue(key, out var methods))
                {
                    methods = new List<MethodInfo>();
                    _events.Add(key, methods);
                }
                if (!methods.Contains(method))
                {
                    methods.Add(method);
                }
            }
        }

        public void ClientJoined(PeerConnection connection) => Invoke<OnClientJoinedAttribute>(connection);
        public void ClientDisconnected(PeerConnection connection, DisconnectInfo info)
            => Invoke<OnClientDisconnectedAttribute>(connection, info);
        public void FullyJoined(PeerConnection connection) => Invoke<OnFullyJoinedAttribute>(connection);

        /// <summary>Starts the session once. Live and persistent runtimes each own a registry;
        /// the persistent registry is created before any session, so it receives this when a
        /// session actually starts.</summary>
        public void SessionStarted()
        {
            if (_sessionStarted)
            {
                return;
            }

            _sessionStarted = true;
            Invoke<OnSessionStartedAttribute>();
        }

        /// <summary>Stops the session once. This makes the persistent runtime's session-stop
        /// event idempotent even though the same registry is later disposed at plugin unload.</summary>
        public void SessionStopped()
        {
            if (!_sessionStarted)
            {
                return;
            }

            _sessionStarted = false;
            Invoke<OnSessionStoppedAttribute>();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            List<Exception> errors = null;
            for (var i = _behaviours.Count - 1; i >= 0; i--)
            {
                var entry = _behaviours[i];
                if (entry?.Behaviour == null)
                {
                    continue;
                }

                // A behaviour that never activated never ran its OnEnable, so it has nothing to
                // shut down. Handlers are still removed and the object destroyed below.
                if (entry.Activated)
                {
                    try
                    {
                        var shutdown = entry.Behaviour.GetType().GetMethod("Shutdown",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        shutdown?.Invoke(entry.Behaviour, null);
                    }
                    catch (Exception error)
                    {
                        errors ??= new List<Exception>();
                        errors.Add(error);
                    }
                }

                try
                {
                    // Registry-owned final safety net: guarantees handler removal even when a
                    // behaviour has no feature-specific shutdown implementation.
                    _context?.Messages.UnregisterAttributedHandlers(entry.Behaviour);
                }
                catch (Exception error)
                {
                    errors ??= new List<Exception>();
                    errors.Add(error);
                }
                finally
                {
                    UnityEngine.Object.Destroy(entry.Behaviour.gameObject);
                }
            }
            _behaviours.Clear();
            _events.Clear();
            _createdTypes.Clear();
            _sessionStarted = false;
            if (errors != null)
            {
                throw new AggregateException("One or more co-op behaviours failed to shut down.", errors);
            }
        }

        /// <summary>Invokes one lifecycle event on one behaviour. Used for a behaviour added after
        /// the session already started; a handler failure is logged, not allowed to abort the
        /// late-add scan.</summary>
        private void InvokeFor<TAttribute>(MonoBehaviour target) where TAttribute : Attribute
        {
            if (target == null || !_events.TryGetValue(typeof(TAttribute), out var methods))
            {
                return;
            }

            foreach (var method in methods.ToArray())
            {
                if (method.DeclaringType == null
                    || !method.DeclaringType.IsAssignableFrom(target.GetType()))
                {
                    continue;
                }

                try
                {
                    method.Invoke(target, Array.Empty<object>());
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogError("[api] " + typeof(TAttribute).Name + " failed for "
                        + target.GetType().FullName + ": " + error);
                }
            }
        }

        private void Invoke<TAttribute>(params object[] args) where TAttribute : Attribute
        {
            if (!_events.TryGetValue(typeof(TAttribute), out var methods))
            {
                return;
            }
            List<Exception> errors = null;
            var targets = _behaviours.ToArray();
            foreach (var method in methods)
            {
                // One invocation per matching instance. Deduplicating methods keeps a shared base
                // method from firing twice on the same instance, and iterating all instances (not
                // FirstOrDefault) means siblings that inherit it each get their turn.
                foreach (var entry in targets)
                {
                    // A deferred external session behaviour has not run OnEnable yet. It receives
                    // its session-start event from ActivateExternal when the transport is ready.
                    if (entry == null || !entry.Activated || entry.Behaviour == null
                        || method.DeclaringType == null
                        || !method.DeclaringType.IsAssignableFrom(entry.Behaviour.GetType()))
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(entry.Behaviour, args);
                    }
                    catch (Exception error)
                    {
                        errors ??= new List<Exception>();
                        errors.Add(error);
                    }
                }
            }
            if (errors != null)
            {
                throw new AggregateException("One or more co-op behaviour event handlers failed.", errors);
            }
        }

        private static IEnumerable<Type> Discover<TAttribute>(bool includeCore)
            where TAttribute : Attribute
            => DiscoverIn(AssembliesToScan(includeCore), typeof(TAttribute));

        private static IEnumerable<Type> DiscoverIn(IEnumerable<Assembly> assemblies,
            Type attributeType)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeTypes(assembly))
                {
                    // IsAssignableTo, not IsAssignableFrom: a type whose field signature names a
                    // type from a missing/stub assembly throws TypeLoadException on load, and this
                    // scan sees CONTRIBUTED assemblies too (the Steam transport is one), so it must
                    // skip such a type rather than abort.
                    if (!ReflectionSurface.IsAssignableTo(type, typeof(MonoBehaviour))
                        || type.IsAbstract)
                    {
                        continue;
                    }

                    // Match on the attribute's full name so an external assembly that (against the
                    // guidance) shipped its own copy of the contract still lines up.
                    if (!HasAttribute(type, attributeType) || !ModuleCatalog.IsEnabled(type))
                    {
                        continue;
                    }

                    yield return type;
                }
            }
        }

        private static bool HasAttribute(Type type, Type attributeType)
        {
            foreach (var attribute in ReflectionSurface.SafeAttributes(type))
            {
                if (attribute == null)
                {
                    continue;
                }

                var candidate = attribute.GetType();
                if (candidate == attributeType
                    || candidate.FullName == attributeType.FullName)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<Assembly> AssembliesToScan(bool includeCore)
        {
            var core = typeof(CoopBehaviourRegistry).Assembly;
            if (includeCore)
            {
                yield return core;
            }

            var external = ExternalCoopMods.Instance.Assemblies;
            for (var i = 0; i < external.Count; i++)
            {
                if (external[i] != null && external[i] != core && IsScannable(external[i]))
                {
                    yield return external[i];
                }
            }
        }

        private const string SteamAssemblyName = "CardShopCoop.Steam";

        /// <summary>
        /// CardShopCoop.Steam is ours and binds the real Steamworks wrapper. On a build that ships the
        /// stub instead (the Game Pass one) none of its transport types can load, so there is nothing
        /// to discover - and asking for its types produced a page of TypeLoadExceptions in the log on
        /// every start. Skipping the scan is what turns an expected absence into no line at all; the
        /// assembly stays in <see cref="ExternalCoopMods"/> because this only decides what behaviour
        /// discovery reads, not what the mod set is.
        /// </summary>
        private static bool IsScannable(Assembly assembly)
            => !(string.Equals(assembly.GetName().Name, SteamAssemblyName, StringComparison.Ordinal)
                && !PlatformProbe.SteamworksPresent);

        private static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                var types = exception.Types.Where(type => type != null).ToArray();
                var failed = exception.LoaderExceptions.Where(error => error != null).ToArray();

                // Warning with a summary, not an Error with the whole list: an assembly that only partly
                // loads reports that once per unresolvable field, which is thousands of characters
                // saying one thing. Once per assembly, and the count plus first reason is what matters.
                CoopLog.WarnOnce("assembly-types:" + assembly.GetName().Name,
                    "Co-op behaviour discovery loaded " + types.Length
                    + " types from " + assembly.GetName().Name + " and skipped " + failed.Length
                    + " that cannot load on this build"
                    + (failed.Length > 0 ? " (first reason: " + Shorten(failed[0].Message) + ")" : "")
                    + ".");
                return types;
            }
        }

        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "no detail";
            }

            const int Limit = 180;
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length <= Limit ? message : message.Substring(0, Limit) + "...";
        }

        private static void Validate(Attribute attribute, MethodInfo method)
        {
            var expected = attribute switch
            {
                OnClientJoinedAttribute => new[] { typeof(PeerConnection) },
                OnClientDisconnectedAttribute => new[] { typeof(PeerConnection), typeof(DisconnectInfo) },
                OnFullyJoinedAttribute => new[] { typeof(PeerConnection) },
                OnSessionStartedAttribute => Type.EmptyTypes,
                OnSessionStoppedAttribute => Type.EmptyTypes,
                _ => throw new InvalidOperationException("Unknown co-op behaviour event attribute.")
            };
            var parameters = method.GetParameters();
            if (method.IsStatic || method.ReturnType != typeof(void)
                || !parameters.Select(p => p.ParameterType).SequenceEqual(expected))
            {
                throw new InvalidOperationException("Invalid co-op behaviour event signature: "
                    + method.DeclaringType?.FullName + "." + method.Name);
            }
        }
    }
}
