using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using UnityEngine;

namespace CardShopCoop.Runtime
{
    internal sealed class CoopBehaviourRegistry
    {
        private readonly List<MonoBehaviour> _behaviours = new List<MonoBehaviour>();
        private readonly Dictionary<Type, List<MethodInfo>> _events = new();
        private readonly HashSet<Type> _createdTypes = new HashSet<Type>();
        private readonly GameObject _parent;
        private readonly CoopRuntimeContext _context;
        private bool _disposed;

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

            return CreateInternal(parent, context, Discover<ServerBehaviourAttribute>(true));
        }

        public static CoopBehaviourRegistry CreateClient(GameObject parent, CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, Discover<ClientBehaviourAttribute>(true));
        }

        public static CoopBehaviourRegistry CreatePersistent(GameObject parent,
            CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, Discover<PersistentBehaviourAttribute>(true));
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
            CoopRuntimeContext context, IEnumerable<Type> types)
        {
            var registry = new CoopBehaviourRegistry(parent, context);
            try
            {
                foreach (var type in types)
                {
                    registry.AddType(type, false);
                }
                registry.ActivateAll();
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
            try
            {
                child = new GameObject(type.Name);
                child.transform.SetParent(_parent.transform, false);
                child.SetActive(false);
                var behaviour = (child.AddComponent(type)) as MonoBehaviour;
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
                // leave a half-added target behind.
                RegisterBehaviourEvents(behaviour);
                _behaviours.Add(behaviour);

                // Activation last, so the context and event map are complete before OnEnable runs.
                if (activate)
                {
                    behaviour.gameObject.SetActive(true);
                }
            }
            catch
            {
                _createdTypes.Remove(type);
                if (child != null)
                {
                    UnityEngine.Object.Destroy(child);
                }
                throw;
            }
        }

        private void ActivateAll()
        {
            foreach (var behaviour in _behaviours)
            {
                if (behaviour != null && !behaviour.gameObject.activeSelf)
                {
                    behaviour.gameObject.SetActive(true);
                }
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
        public void SessionStarted() => Invoke<OnSessionStartedAttribute>();
        public void SessionStopped() => Invoke<OnSessionStoppedAttribute>();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            List<Exception> errors = null;
            for (var i = _behaviours.Count - 1; i >= 0; i--)
            {
                if (_behaviours[i] != null)
                {
                    try
                    {
                        var shutdown = _behaviours[i].GetType().GetMethod("Shutdown",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        shutdown?.Invoke(_behaviours[i], null);
                    }
                    catch (Exception error)
                    {
                        errors ??= new List<Exception>();
                        errors.Add(error);
                    }
                    try
                    {
                        // Registry-owned final safety net: guarantees handler removal even when a
                        // behaviour has no feature-specific shutdown implementation.
                        _context?.Messages.UnregisterAttributedHandlers(_behaviours[i]);
                    }
                    catch (Exception error)
                    {
                        errors ??= new List<Exception>();
                        errors.Add(error);
                    }
                    finally
                    {
                        UnityEngine.Object.Destroy(_behaviours[i].gameObject);
                    }
                }
            }
            _behaviours.Clear();
            _events.Clear();
            _createdTypes.Clear();
            if (errors != null)
            {
                throw new AggregateException("One or more co-op behaviours failed to shut down.", errors);
            }
        }

        private void Invoke<TAttribute>(params object[] args) where TAttribute : Attribute
        {
            if (!_events.TryGetValue(typeof(TAttribute), out var methods))
            {
                return;
            }
            List<Exception> errors = null;
            foreach (var method in methods)
            {
                // One invocation per matching instance. Deduplicating methods keeps a shared base
                // method from firing twice on the same instance, and iterating all instances (not
                // FirstOrDefault) means siblings that inherit it each get their turn.
                foreach (var target in _behaviours)
                {
                    if (target == null || method.DeclaringType == null
                        || !method.DeclaringType.IsAssignableFrom(target.GetType()))
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(target, args);
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
                    if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
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
            foreach (var attribute in type.GetCustomAttributes(false))
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
                if (external[i] != null && external[i] != core)
                {
                    yield return external[i];
                }
            }
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                var types = exception.Types.Where(type => type != null).ToArray();
                CoopPlugin.Log.LogError("Co-op behaviour discovery loaded " + types.Length
                    + " types from " + assembly.GetName().Name + " with reflection errors: "
                    + string.Join(" | ", exception.LoaderExceptions.Where(error => error != null)
                        .Select(error => error.ToString())));
                return types;
            }
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
