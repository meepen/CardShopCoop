using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>Typed extended-information store and host-authoritative network flow.</summary>
    internal static class ExtendedInformationModule
    {
        private static readonly HashSet<string> LoggedTypeWarnings =
            new(StringComparer.Ordinal);

        private readonly struct HandlerKey : IEquatable<HandlerKey>
        {
            internal readonly Type OwnerType;
            internal readonly Type InformationType;

            internal HandlerKey(Type ownerType, Type informationType)
            {
                OwnerType = ownerType;
                InformationType = informationType;
            }

            public bool Equals(HandlerKey other)
                => OwnerType == other.OwnerType && InformationType == other.InformationType;

            public override bool Equals(object obj) => obj is HandlerKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((OwnerType?.GetHashCode() ?? 0) * 397)
                        ^ (InformationType?.GetHashCode() ?? 0);
                }
            }
        }

        private sealed class HandlerRegistration
        {
            internal object Target;
            internal MethodInfo Method;
            internal int OwnerIndex;
            internal int InformationIndex;
            internal Func<object, object, object> BuildPayload;
        }

        private sealed class GlobalHandlerRegistration
        {
            internal object Target;
            internal MethodInfo Method;
            internal Func<object, object, object> BuildPayload;
        }

        private static readonly Dictionary<HandlerKey, List<HandlerRegistration>> Handlers = new();
        private static readonly Dictionary<Type, List<GlobalHandlerRegistration>> GlobalHandlers =
            new();

        internal static void UnregisterAttributedHandlers(object target)
        {
            if (target == null)
            {
                return;
            }

            var empty = new List<HandlerKey>();
            foreach (var pair in Handlers)
            {
                var registrations = pair.Value;
                for (var i = registrations.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(registrations[i].Target, target))
                    {
                        registrations.RemoveAt(i);
                    }
                }

                if (registrations.Count == 0)
                {
                    empty.Add(pair.Key);
                }
            }

            for (var i = 0; i < empty.Count; i++)
            {
                Handlers.Remove(empty[i]);
            }

            var emptyGlobal = new List<Type>();
            foreach (var pair in GlobalHandlers)
            {
                var registrations = pair.Value;
                for (var i = registrations.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(registrations[i].Target, target))
                    {
                        registrations.RemoveAt(i);
                    }
                }

                if (registrations.Count == 0)
                {
                    emptyGlobal.Add(pair.Key);
                }
            }

            for (var i = 0; i < emptyGlobal.Count; i++)
            {
                GlobalHandlers.Remove(emptyGlobal[i]);
            }
        }

        /// <summary>
        /// Validates every attributed handler on the target and returns the commits. Scanning does
        /// not mutate the registry, so a failure leaves the previous registration intact and the
        /// caller can commit only after its message registrations succeeded.
        /// </summary>
        internal static List<Action> CollectAttributedHandlers(object target)
        {
            var pending = new List<Action>();
            if (target == null)
            {
                return pending;
            }

            var methods = target.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.GetCustomAttribute<GlobalInformationHandlerAttribute>() != null)
                {
                    var globalParameters = method.GetParameters();
                    if (method.IsStatic || method.ReturnType != typeof(void)
                        || globalParameters.Length != 1
                        || !TryReadPayload(globalParameters[0].ParameterType, true,
                            out var globalType, out var globalBuild))
                    {
                        throw new InvalidOperationException(
                            "Invalid global-information handler signature: "
                            + method.DeclaringType?.FullName + "." + method.Name
                            + " (expected one global information value or InformationChange<T>)");
                    }

                    ExtendedInformationCatalog.Register(globalType);
                    var globalRegistration = new GlobalHandlerRegistration
                    {
                        Target = target,
                        Method = method,
                        BuildPayload = globalBuild,
                    };
                    pending.Add(() => AddGlobalHandler(globalType, globalRegistration));
                    continue;
                }

                if (method.GetCustomAttribute<ExtendedInformationHandlerAttribute>() == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (method.IsStatic || method.ReturnType != typeof(void)
                    || parameters.Length != 2)
                {
                    throw new InvalidOperationException(
                        "Invalid extended-information handler signature: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                var informationIndex = -1;
                var ownerIndex = -1;
                Type informationType = null;
                Func<object, object, object> buildPayload = null;
                for (var p = 0; p < parameters.Length; p++)
                {
                    if (TryReadPayload(parameters[p].ParameterType, false,
                        out var candidateType, out var candidateBuild))
                    {
                        if (informationType != null)
                        {
                            throw new InvalidOperationException(
                                "Extended-information handler has more than one information "
                                + "parameter: " + method.DeclaringType?.FullName + "."
                                + method.Name);
                        }

                        informationIndex = p;
                        informationType = candidateType;
                        buildPayload = candidateBuild;
                    }
                    else
                    {
                        if (ownerIndex >= 0)
                        {
                            throw new InvalidOperationException(
                                "Extended-information handler has more than one owner "
                                + "parameter: " + method.DeclaringType?.FullName + "."
                                + method.Name);
                        }

                        ownerIndex = p;
                    }
                }

                if (informationType == null || buildPayload == null || ownerIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Invalid extended-information handler signature: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                if (!ExtendedInformationCodec.IsExtendedInformation(
                    informationType, out var ownerType))
                {
                    throw new InvalidOperationException(
                        "Extended-information handler information type must be a concrete "
                        + "ExtendGameObjectInformation<T> subclass: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                if (!ExtendedInformationCodec.SupportsOwnerType(ownerType))
                {
                    throw new InvalidOperationException(
                        "Extended-information handler owner type has no identity adapter: "
                        + method.DeclaringType?.FullName + "." + method.Name
                        + " (owner " + ownerType.FullName + ")");
                }

                var ownerParameter = parameters[ownerIndex];
                if (ownerParameter.ParameterType.IsValueType
                    || !ExtendedInformationCodec.SupportsOwnerType(
                        ownerParameter.ParameterType)
                    || !ownerParameter.ParameterType.IsAssignableFrom(ownerType))
                {
                    throw new InvalidOperationException(
                        "Invalid extended-information owner parameter: "
                        + method.DeclaringType?.FullName + "." + method.Name
                        + " (parameter " + ownerParameter.ParameterType.FullName + ")");
                }

                var key = new HandlerKey(ownerType, informationType);
                var registration = new HandlerRegistration
                {
                    Target = target,
                    Method = method,
                    OwnerIndex = ownerIndex,
                    InformationIndex = informationIndex,
                    BuildPayload = buildPayload,
                };
                ExtendedInformationCatalog.Register(informationType);
                pending.Add(() => AddHandler(key, registration));
            }

            return pending;
        }

        /// <summary>Accepts either the raw information value or InformationChange&lt;T&gt;. The
        /// former is the current value only; the latter carries previous/current/removed.</summary>
        private static bool TryReadPayload(Type parameterType, bool global,
            out Type informationType, out Func<object, object, object> buildPayload)
        {
            informationType = null;
            buildPayload = null;
            if (parameterType == null)
            {
                return false;
            }

            var candidate = parameterType;
            var isChange = parameterType.IsGenericType
                && parameterType.GetGenericTypeDefinition() == typeof(InformationChange<>);
            if (isChange)
            {
                candidate = parameterType.GetGenericArguments()[0];
            }

            if (candidate == null || candidate.IsValueType || candidate.IsAbstract
                || candidate.ContainsGenericParameters)
            {
                return false;
            }

            if (global
                ? !ExtendedInformationCodec.IsGlobalInformation(candidate)
                : !ExtendedInformationCodec.IsExtendedInformation(candidate, out _))
            {
                return false;
            }

            informationType = candidate;
            buildPayload = isChange ? CreateChangeFactory(candidate) : CurrentPayload;
            return true;
        }

        private static object CurrentPayload(object previous, object current) => current;

        private static Func<object, object, object> CreateChangeFactory(Type informationType)
        {
            var method = typeof(ExtendedInformationModule).GetMethod(nameof(MakeChange),
                BindingFlags.Static | BindingFlags.NonPublic);
            var closed = method.MakeGenericMethod(informationType);
            return (Func<object, object, object>)closed.CreateDelegate(
                typeof(Func<object, object, object>));
        }

        private static object MakeChange<T>(object previous, object current) where T : class
            => new InformationChange<T>((T)previous, (T)current);

        internal static void CommitAttributedHandlers(List<Action> pending)
        {
            if (pending == null)
            {
                return;
            }

            for (var i = 0; i < pending.Count; i++)
            {
                pending[i]();
            }
        }

        private static void AddHandler(HandlerKey key, HandlerRegistration registration)
        {
            if (!Handlers.TryGetValue(key, out var registrations))
            {
                Handlers[key] = registrations = new List<HandlerRegistration>();
            }

            for (var i = 0; i < registrations.Count; i++)
            {
                if (ReferenceEquals(registrations[i].Target, registration.Target)
                    && registrations[i].Method == registration.Method)
                {
                    return;
                }
            }

            registrations.Add(registration);
        }

        private static void AddGlobalHandler(Type informationType,
            GlobalHandlerRegistration registration)
        {
            if (!GlobalHandlers.TryGetValue(informationType, out var registrations))
            {
                GlobalHandlers[informationType] = registrations =
                    new List<GlobalHandlerRegistration>();
            }

            for (var i = 0; i < registrations.Count; i++)
            {
                if (ReferenceEquals(registrations[i].Target, registration.Target)
                    && registrations[i].Method == registration.Method)
                {
                    return;
                }
            }

            registrations.Add(registration);
        }

        private static void Notify(object owner, Type informationType, object previous, object current)
        {
            if (owner == null || informationType == null || Handlers.Count == 0)
            {
                return;
            }

            // Handlers may unregister or register in response to a change, so snapshot the
            // matching registrations before invoking any user code.
            var ownerType = owner.GetType();
            List<HandlerRegistration> matches = null;
            foreach (var pair in Handlers)
            {
                if (pair.Key.InformationType != informationType
                    || !pair.Key.OwnerType.IsAssignableFrom(ownerType))
                {
                    continue;
                }

                matches ??= new List<HandlerRegistration>();
                matches.AddRange(pair.Value);
            }

            if (matches == null)
            {
                return;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var registration = matches[i];
                try
                {
                    var arguments = new object[2];
                    arguments[registration.OwnerIndex] = owner;
                    arguments[registration.InformationIndex] =
                        registration.BuildPayload(previous, current);
                    InvokeHandler(registration.Target, registration.Method, arguments, current);
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("[identity] extended-information handler failed for "
                        + informationType.FullName + ": " + error);
                }
            }
        }

        private static void NotifyGlobal(Type informationType, object previous, object current)
        {
            if (informationType == null
                || !GlobalHandlers.TryGetValue(informationType, out var registrations))
            {
                return;
            }

            // Snapshot before invoking user code, same as Notify.
            var matches = new List<GlobalHandlerRegistration>(registrations);
            for (var i = 0; i < matches.Count; i++)
            {
                try
                {
                    var registration = matches[i];
                    var payload = registration.BuildPayload(previous, current);
                    InvokeHandler(registration.Target, registration.Method, new[] { payload }, current);
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("[identity] global-information handler failed for "
                        + informationType.FullName + ": " + error);
                }
            }
        }

        /// <summary>Invokes one information handler, wrapping it in the action ledger when the
        /// <paramref name="current"/> value is an <see cref="IActionValue"/>. The action decision
        /// comes from the current value, not from the handler parameter shape: a change handler
        /// receives <see cref="InformationChange{T}"/>, which is a struct and can never itself be
        /// an <see cref="IActionValue"/>.</summary>
        private static void InvokeHandler(object target, MethodInfo method, object[] arguments,
            object current)
        {
            // A handler applies authority. Mark it on the owning behaviour so the module's Harmony
            // observers skip the resulting game changes instead of forwarding them as local
            // actions; the module keeps no suppression flag of its own.
            var owner = target as CoopBehaviour;
            owner?.BeginAuthorityDispatch();
            try
            {
                if (CoopCore.Role == CoopRole.Client && current is IActionValue value)
                {
                    ActionLedger.ApplyValue(value, () => method.Invoke(target, arguments));
                    return;
                }

                method.Invoke(target, arguments);
            }
            finally
            {
                owner?.EndAuthorityDispatch();
            }
        }

        /// <summary>Routes one change to the object handler set or the session-wide handler set.
        /// A global entry has no live owner, and an unresolved object entry is stored but not
        /// notified (it is replayed when the identity binds).</summary>
        private static void NotifyFor(GameObjectId id, object owner, Type informationType,
            object previous, object current)
        {
            if (id.Kind == GameObjectKind.Global)
            {
                NotifyGlobal(informationType, previous, current);
                return;
            }

            if (owner != null)
            {
                Notify(owner, informationType, previous, current);
            }
        }

        internal static object Get(Type informationType, object gameObject)
        {
            MainThreadGuard.RequireMainThread("extended information get");
            if (!Validate(informationType, gameObject, out var id, out _))
            {
                return null;
            }

            return GetFor(id, informationType);
        }

        internal static object GetGlobal(Type informationType)
        {
            MainThreadGuard.RequireMainThread("global information get");
            RequireGlobalInformationType(informationType);
            return GetFor(GameObjectId.Global(), informationType);
        }

        private static object GetFor(GameObjectId id, Type informationType)
            => ExtendedInformationStore.TryGet(id, informationType, out var information)
                ? information : null;

        internal static void Set(Type informationType, object gameObject, object information)
        {
            MainThreadGuard.RequireMainThread("extended information set");
            RequireConcrete(informationType, information, "SetExtendedInformation");
            if (!Validate(informationType, gameObject, out var id, out var owner))
            {
                return;
            }

            ApplySet(id, owner, informationType, information);
        }

        internal static void SetGlobal(Type informationType, object information)
        {
            MainThreadGuard.RequireMainThread("global information set");
            RequireGlobalInformationType(informationType);
            RequireConcrete(informationType, information, "SetGlobalInformation");
            ApplySet(GameObjectId.Global(), null, informationType, information);
        }

        /// <summary>Host-only: seeds a session-wide value before the transport exists, without
        /// broadcasting or applying a client echo. The generic join baseline sends it. This is for
        /// a module whose baseline must exist before the first peer can join; normal changes go
        /// through <see cref="SetGlobal"/>.</summary>
        internal static void SeedGlobal(Type informationType, object information)
        {
            MainThreadGuard.RequireMainThread("global information seed");
            RequireGlobalInformationType(informationType);
            RequireConcrete(informationType, information, "SeedGlobalInformation");
            var id = GameObjectId.Global();
            ExtendedInformationStore.TryGet(id, informationType, out var previous);
            ExtendedInformationStore.Set(id, informationType, information);
            NotifyFor(id, null, informationType, previous, information);
        }

        /// <summary>Host-only: seeds an object-attached value before the transport exists,
        /// without broadcasting. This is the object counterpart of <see cref="SeedGlobal"/>;
        /// a session-start seed must not call the normal host path, which broadcasts and throws
        /// when <c>Net</c> is still null. The generic join baseline sends it to each peer.</summary>
        internal static void Seed(Type informationType, object gameObject, object information)
        {
            MainThreadGuard.RequireMainThread("extended information seed");
            RequireConcrete(informationType, information, "SeedExtendedInformation");
            if (!Validate(informationType, gameObject, out var id, out var owner))
            {
                return;
            }

            ExtendedInformationStore.TryGet(id, informationType, out var previous);
            ExtendedInformationStore.Set(id, informationType, information);
            NotifyFor(id, owner, informationType, previous, information);
        }

        /// <summary>The one host-authority flow for object-attached and session-wide values. A
        /// global is simply the entry keyed by <see cref="GameObjectId.Global"/>, so the two paths
        /// cannot drift apart in validation, echo, or notification.</summary>
        private static void ApplySet(GameObjectId id, object owner, Type informationType,
            object information)
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                var value = ValueFor(informationType, information);
                ExtendedInformationStore.TryGet(id, informationType, out var previous);
                ExtendedInformationStore.Set(id, informationType, information);
                BroadcastSet(id, value);
                NotifyFor(id, owner, informationType, previous, information);
                return;
            }

            if (CoopCore.Role == CoopRole.Client)
            {
                RequireClientWritable(informationType);
                SendIntent(ExtendedInformationIntentMessage.Set, id,
                    ExtendedInformationCatalog.IdFor(informationType),
                    ValueFor(informationType, information));
                return;
            }

            ExtendedInformationStore.TryGet(id, informationType, out var localPrevious);
            ExtendedInformationStore.Set(id, informationType, information);
            NotifyFor(id, owner, informationType, localPrevious, information);
        }

        internal static bool Remove(Type informationType, object gameObject)
        {
            MainThreadGuard.RequireMainThread("extended information remove");
            if (!Validate(informationType, gameObject, out var id, out var owner))
            {
                return false;
            }

            return ApplyRemove(id, owner, informationType);
        }

        internal static bool RemoveGlobal(Type informationType)
        {
            MainThreadGuard.RequireMainThread("global information remove");
            RequireGlobalInformationType(informationType);
            return ApplyRemove(GameObjectId.Global(), null, informationType);
        }

        private static bool ApplyRemove(GameObjectId id, object owner, Type informationType)
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                var typeId = ExtendedInformationCatalog.IdFor(informationType);
                ExtendedInformationStore.TryGet(id, informationType, out var previous);
                var removed = ExtendedInformationStore.Remove(id, informationType);
                if (removed)
                {
                    BroadcastRemoved(id, typeId);
                    NotifyFor(id, owner, informationType, previous, null);
                }

                return removed;
            }

            if (CoopCore.Role == CoopRole.Client)
            {
                RequireClientWritable(informationType);
                SendIntent(ExtendedInformationIntentMessage.Remove, id,
                    ExtendedInformationCatalog.IdFor(informationType), null);
                return true;
            }

            ExtendedInformationStore.TryGet(id, informationType, out var localPrevious);
            var localRemoved = ExtendedInformationStore.Remove(id, informationType);
            if (localRemoved)
            {
                NotifyFor(id, owner, informationType, localPrevious, null);
            }

            return localRemoved;
        }

        private static void RequireConcrete(Type informationType, object information,
            string operation)
        {
            if (information == null)
            {
                throw new ArgumentNullException(nameof(information));
            }

            if (information.GetType() != informationType)
            {
                throw new ArgumentException(
                    operation + "<" + informationType.Name + "> was given a "
                    + information.GetType().Name + ". Use the concrete information type; a base "
                    + "type would key the store by the base and serialize only its fields, so a "
                    + "remote peer would silently lose the derived fields.",
                    nameof(information));
            }
        }

        private static void RequireClientWritable(Type informationType)
        {
            if (ExtendedInformationCodec.IsHostOnly(informationType))
            {
                throw new InvalidOperationException(
                    "Extended information " + informationType.FullName + " is host-only; a client "
                    + "must use its module's validated intent lane.");
            }
        }

        internal static void HostHandleIntent(ExtendedInformationIntentMessage message)
        {
            if (message == null || !message.Id.IsValid
                || message.Id.Kind == GameObjectKind.None)
            {
                return;
            }

            var type = ResolveTypeForWire(message.TypeId, "extended-information intent");
            if (type == null)
            {
                return;
            }

            if (ExtendedInformationCodec.IsHostOnly(type))
            {
                LogTypeWarningOnce(ExtendedInformationCodec.KeyFor(type),
                    "host-only extended information cannot be written by a client intent");
                return;
            }

            var global = message.Id.Kind == GameObjectKind.Global;
            if (global ? !ValidateGlobalType(type) : !ValidateType(type))
            {
                return;
            }

            object owner = null;
            if (!global)
            {
                if (!GameObjectIdentityApi.TryResolve(message.Id, out owner) || owner == null
                    || !ExtendedInformationCodec.IsExtendedInformation(type, out var ownerType)
                    || !ownerType.IsInstanceOfType(owner))
                {
                    return;
                }
            }

            var id = message.Id;
            if (message.Operation == ExtendedInformationIntentMessage.Remove)
            {
                ExtendedInformationStore.TryGet(id, type, out var previous);
                if (!ExtendedInformationStore.Remove(id, type))
                {
                    return;
                }

                BroadcastRemoved(id, message.TypeId);
                NotifyFor(id, owner, type, previous, null);
                return;
            }

            if (message.Operation != ExtendedInformationIntentMessage.Set
                || message.Value?.Information == null
                || message.Value.TypeId != message.TypeId)
            {
                return;
            }

            var information = message.Value.Information;
            ExtendedInformationStore.TryGet(id, type, out var objectPrevious);
            ExtendedInformationStore.Set(id, type, information);
            BroadcastSet(id, ValueFor(message.TypeId, information));
            NotifyFor(id, owner, type, objectPrevious, information);
        }

        internal static void ClientHandleSet(ExtendedInformationSetMessage message)
        {
            if (message == null || message.Id.Kind == GameObjectKind.None
                || message.Value == null)
            {
                return;
            }

            var type = ResolveTypeForWire(message.Value.TypeId, "extended-information set");
            var global = message.Id.Kind == GameObjectKind.Global;
            if (type == null || message.Value.Information == null
                || (global ? !ValidateGlobalType(type) : !ValidateType(type)))
            {
                return;
            }

            // Resolve before storing. TryResolve can bind the owner, and Bind replays the store's
            // pending baseline to handlers; doing that before this value is stored avoids a
            // duplicate first-value notification for the same set.
            object owner = null;
            var resolved = global || GameObjectIdentityApi.TryResolve(message.Id, out owner);
            ExtendedInformationStore.TryGet(message.Id, type, out var previous);
            ExtendedInformationStore.Set(message.Id, type, message.Value.Information);
            NotifyFor(message.Id, resolved ? owner : null, type, previous,
                message.Value.Information);
        }

        internal static void ClientHandleRemoved(ExtendedInformationRemovedMessage message)
        {
            if (message == null || message.Id.Kind == GameObjectKind.None)
            {
                return;
            }

            var type = ResolveTypeForWire(message.TypeId, "extended-information removal");
            var global = message.Id.Kind == GameObjectKind.Global;
            if (type == null || (global ? !ValidateGlobalType(type) : !ValidateType(type)))
            {
                return;
            }

            object owner = null;
            var resolved = global || GameObjectIdentityApi.TryResolve(message.Id, out owner);
            ExtendedInformationStore.TryGet(message.Id, type, out var previous);
            if (!ExtendedInformationStore.Remove(message.Id, type))
            {
                return;
            }

            NotifyFor(message.Id, resolved ? owner : null, type, previous, null);
        }

        internal static ExtendedInformationBaselineMessage BuildBaseline()
            => new ExtendedInformationBaselineMessage
            {
                Entries = ExtendedInformationStore.Snapshot(type =>
                {
                    if (ExtendedInformationCatalog.TryGetId(type, out var id))
                    {
                        return id;
                    }

                    LogTypeWarningOnce(ExtendedInformationCodec.KeyFor(type),
                        "extended-information baseline skipped a type missing from the session "
                        + "catalog");
                    return null;
                }),
            };

        internal static void ClientApplyBaseline(ExtendedInformationBaselineMessage message)
        {
            if (message?.Entries == null)
            {
                return;
            }

            for (var i = 0; i < message.Entries.Count; i++)
            {
                var entry = message.Entries[i];
                if (entry?.Value == null || entry.Value.Information == null)
                {
                    continue;
                }

                var type = ResolveTypeForWire(
                    entry.Value.TypeId, "extended-information baseline");
                var global = entry.Id.Kind == GameObjectKind.Global;
                if (type == null || !entry.Id.IsValid
                    || (global ? !ValidateGlobalType(type) : !ValidateType(type)))
                {
                    continue;
                }

                // Resolve before storing for the same reason as ClientHandleSet: a first bind
                // replays whatever the store already holds, so the value must not be visible yet.
                object owner = null;
                var resolved = global || GameObjectIdentityApi.TryResolve(entry.Id, out owner);
                ExtendedInformationStore.Set(entry.Id, type, entry.Value.Information);
                NotifyFor(entry.Id, resolved ? owner : null, type, null, entry.Value.Information);
            }
        }

        /// <summary>
        /// A client just bound an object that already owns stored information (the join baseline
        /// can arrive before the identity module materializes the owner). Replay it as a first
        /// value so object handlers can apply the baseline exactly as a live set would.
        /// </summary>
        internal static void OnEntityBound(object owner, GameObjectId id)
        {
            if (owner == null || !id.IsValid)
            {
                return;
            }

            var entries = ExtendedInformationStore.Entries(id);
            for (var i = 0; i < entries.Count; i++)
            {
                Notify(owner, entries[i].Key, null, entries[i].Value);
            }
        }

        internal static void OnEntityUnbound(GameObjectId id)
        {
            if (id.IsValid)
            {
                ExtendedInformationStore.RemoveAll(id);
            }
        }

        /// <summary>
        /// Removes extended information only when the entity is permanently removed. A
        /// representation transition (for example a packaging box becoming a stored warehouse
        /// record) keeps the same GameObjectId and must preserve its information.
        /// </summary>
        internal static void OnEntityRemoved(GameObjectId id, EntityRemovalMode mode)
        {
            if (!id.IsValid || mode == EntityRemovalMode.Stored)
            {
                return;
            }

            NotifyRemoved(id);
            ExtendedInformationStore.RemoveAll(id);
        }

        private static void NotifyRemoved(GameObjectId id)
        {
            if (!GameObjectIdentityCache.TryResolveBound(id, out var owner) || owner == null)
            {
                return;
            }

            var entries = ExtendedInformationStore.Entries(id);
            for (var i = 0; i < entries.Count; i++)
            {
                Notify(owner, entries[i].Key, entries[i].Value, null);
            }
        }

        internal static void ResetEntities()
        {
            ExtendedInformationStore.ResetEntities();
            LoggedTypeWarnings.Clear();
        }

        internal static void ResetAll()
        {
            ExtendedInformationStore.ResetAll();
            LoggedTypeWarnings.Clear();
        }

        private static Type ResolveTypeForWire(ushort typeId, string lane)
        {
            var type = ExtendedInformationCatalog.TypeFor(typeId);
            if (type == null)
            {
                LogTypeWarningOnce("#" + typeId, lane + " references catalog id " + typeId
                    + " which this build does not know");
                return null;
            }

            if (!ExtendedInformationCodec.IsExtendedInformation(type, out _)
                && !ExtendedInformationCodec.IsGlobalInformation(type))
            {
                LogTypeWarningOnce(ExtendedInformationCodec.KeyFor(type),
                    lane + " resolved to an unsupported extended-information type");
                return null;
            }

            return type;
        }

        private static void LogTypeWarningOnce(string typeKey, string reason)
        {
            var key = string.IsNullOrEmpty(typeKey) ? "<empty>" : typeKey;
            if (!LoggedTypeWarnings.Add(key))
            {
                return;
            }

            CoopPlugin.Log.LogWarning("[identity] " + reason + ": " + key
                + " (further entries for this key will be dropped without another log).");
        }

        private static bool Validate(Type informationType, object gameObject, out GameObjectId id,
            out object owner)
        {
            id = default;
            owner = null;
            var ownerType = RequireOwnerType(informationType);
            if (gameObject == null)
            {
                return false;
            }

            if (!ownerType.IsInstanceOfType(gameObject))
            {
                throw new ArgumentException(
                    "Extended information " + informationType.FullName + " is declared for "
                    + ownerType.FullName + " but was used on "
                    + gameObject.GetType().FullName + ".", nameof(gameObject));
            }

            if (!GameObjectIdentityApi.TryGetId(gameObject, out id))
            {
                CoopPlugin.Log.LogWarning("[identity] extended information "
                    + informationType.FullName + " skipped for " + Describe(gameObject)
                    + ": the object has no replicated identity.");
                return false;
            }

            owner = gameObject;
            return true;
        }

        private static bool ValidateType(Type type)
            => type != null && !type.IsAbstract && !type.ContainsGenericParameters
                && ExtendedInformationCodec.IsExtendedInformation(type, out var ownerType)
                && ExtendedInformationCodec.SupportsOwnerType(ownerType);

        private static Type RequireOwnerType(Type informationType)
        {
            if (informationType == null || informationType.IsAbstract
                || informationType.ContainsGenericParameters
                || !ExtendedInformationCodec.IsExtendedInformation(
                    informationType, out var ownerType))
            {
                throw new ArgumentException(
                    "Extended information type must be a concrete ExtendGameObjectInformation<T> "
                    + "subclass; got "
                    + (informationType == null ? "<null>" : informationType.FullName) + ".",
                    nameof(informationType));
            }

            if (!ExtendedInformationCodec.SupportsOwnerType(ownerType))
            {
                throw new ArgumentException(
                    "Extended information owner type has no identity adapter: "
                    + ownerType.FullName + " (information " + informationType.FullName + ").",
                    nameof(informationType));
            }

            return ownerType;
        }

        private static bool ValidateGlobalType(Type type)
            => type != null && !type.IsAbstract && !type.ContainsGenericParameters
                && ExtendedInformationCodec.IsGlobalInformation(type);

        private static void RequireGlobalInformationType(Type informationType)
        {
            if (!ValidateGlobalType(informationType))
            {
                throw new ArgumentException(
                    "Global information type must be a concrete class marked "
                    + "[GlobalInformation]; got "
                    + (informationType == null ? "<null>" : informationType.FullName) + ".",
                    nameof(informationType));
            }
        }

        private static string Describe(object gameObject)
        {
            var behaviour = gameObject as UnityEngine.Object;
            return behaviour == null
                ? gameObject.GetType().Name
                : behaviour.name + " (" + gameObject.GetType().Name + ")";
        }

        private static ExtendedInformationValue ValueFor(Type type, object information)
            => new()
            {
                TypeId = ExtendedInformationCatalog.IdFor(type),
                Information = information,
            };

        private static ExtendedInformationValue ValueFor(ushort typeId, object information)
            => new()
            {
                TypeId = typeId,
                Information = information,
            };

        private static void SendIntent(byte operation, GameObjectId id, ushort typeId,
            ExtendedInformationValue value)
        {
            var context = Context();
            if (context == null)
            {
                var type = ExtendedInformationCatalog.TypeFor(typeId);
                if (type == null)
                {
                    return;
                }

                object owner = null;
                if (id.Kind != GameObjectKind.Global)
                {
                    GameObjectIdentityApi.TryResolve(id, out owner);
                }

                if (operation == ExtendedInformationIntentMessage.Remove)
                {
                    ExtendedInformationStore.TryGet(id, type, out var removedPrevious);
                    if (ExtendedInformationStore.Remove(id, type))
                    {
                        NotifyFor(id, owner, type, removedPrevious, null);
                    }

                    return;
                }

                if (value?.Information == null)
                {
                    return;
                }

                ExtendedInformationStore.TryGet(id, type, out var previous);
                ExtendedInformationStore.Set(id, type, value.Information);
                NotifyFor(id, owner, type, previous, value.Information);
                return;
            }

            context.Send(new ExtendedInformationIntentMessage
            {
                Operation = operation,
                Id = id,
                TypeId = typeId,
                Value = value,
            });
        }

        private static void BroadcastSet(GameObjectId id, ExtendedInformationValue value)
        {
            Context()?.Broadcast(new ExtendedInformationSetMessage
            {
                Id = id,
                Value = value,
            });
        }

        private static void BroadcastRemoved(GameObjectId id, ushort typeId)
        {
            Context()?.Broadcast(new ExtendedInformationRemovedMessage
            {
                Id = id,
                TypeId = typeId,
            });
        }

        private static ICoopContext Context()
            => CoopCore.Instance?.LiveContext;
    }
}
