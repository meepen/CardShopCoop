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
        private readonly List<MonoBehaviour> _behaviours;
        private readonly Dictionary<Type, List<MethodInfo>> _events = new();
        private bool _disposed;

        private CoopBehaviourRegistry(List<MonoBehaviour> behaviours)
        {
            _behaviours = behaviours;
            foreach (var behaviour in behaviours)
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
                    methods.Add(method);
                }
            }
        }

        public static CoopBehaviourRegistry CreateServer(GameObject parent, CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, DiscoverServer());
        }

        public static CoopBehaviourRegistry CreateClient(GameObject parent, CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, DiscoverClient());
        }

        public static CoopBehaviourRegistry CreatePersistent(GameObject parent,
            CoopRuntimeContext context)
        {
            if (parent == null || context == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : nameof(context));
            }

            return CreateInternal(parent, context, DiscoverPersistent());
        }

        private static CoopBehaviourRegistry CreateInternal(GameObject parent,
            CoopRuntimeContext context, IEnumerable<Type> types)
        {
            var behaviours = new List<MonoBehaviour>();
            foreach (var type in types)
            {
                var child = new GameObject(type.Name);
                child.transform.SetParent(parent.transform, false);
                child.SetActive(false);
                var behaviour = child.AddComponent(type) as CoopBehaviour;
                if (behaviour == null)
                {
                    throw new InvalidOperationException(type.FullName + " is not a CoopBehaviour.");
                }
                behaviour.SetRuntimeContext(context);
                behaviours.Add(behaviour);
            }

            var registry = new CoopBehaviourRegistry(behaviours);
            try
            {
                foreach (var behaviour in behaviours)
                {
                    behaviour.gameObject.SetActive(true);
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
                        ((_behaviours[i] as CoopBehaviour))?.ShutdownLifecycle();
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
                var target = _behaviours.FirstOrDefault(b => b != null && b.GetType() == method.DeclaringType);
                if (target != null)
                {
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

        private static IEnumerable<Type> DiscoverServer()
        {
            var assembly = typeof(CoopBehaviourRegistry).Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
                CoopPlugin.Log.LogError("Co-op behaviour discovery loaded " + types.Length
                    + " types with reflection errors: "
                    + string.Join(" | ", exception.LoaderExceptions.Where(error => error != null)
                        .Select(error => error.ToString())));
            }
            foreach (var type in types)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
                {
                    continue;
                }
                if (type.GetCustomAttribute<ServerBehaviourAttribute>() != null
                    && ModuleCatalog.IsEnabled(type))
                {
                    yield return type;
                }
            }
        }

        private static IEnumerable<Type> DiscoverClient()
        {
            var assembly = typeof(CoopBehaviourRegistry).Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
                CoopPlugin.Log.LogError("Co-op client behaviour discovery loaded " + types.Length
                    + " types with reflection errors: "
                    + string.Join(" | ", exception.LoaderExceptions.Where(error => error != null)
                        .Select(error => error.ToString())));
            }
            foreach (var type in types)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
                {
                    continue;
                }
                if (type.GetCustomAttribute<ClientBehaviourAttribute>() != null
                    && ModuleCatalog.IsEnabled(type))
                {
                    yield return type;
                }
            }
        }

        private static IEnumerable<Type> DiscoverPersistent()
        {
            var assembly = typeof(CoopBehaviourRegistry).Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
                CoopPlugin.Log.LogError("Persistent co-op behaviour discovery loaded " + types.Length
                    + " types with reflection errors: "
                    + string.Join(" | ", exception.LoaderExceptions.Where(error => error != null)
                        .Select(error => error.ToString())));
            }

            foreach (var type in types)
            {
                if (typeof(CoopBehaviour).IsAssignableFrom(type) && !type.IsAbstract
                    && type.GetCustomAttribute<PersistentBehaviourAttribute>() != null
                    && ModuleCatalog.IsEnabled(type))
                {
                    yield return type;
                }
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
