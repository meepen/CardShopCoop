using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Generic;
using CardShopCoop.Util;
using System.Reflection;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Net.Connection;
using CardShopCoop.Api;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.Actions;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Net
{
    public sealed class MessageContext
    {
        public PeerConnection Connection;
        public bool InGame;
        public ICoopTransport Transport;

        // Core supplies the bounded-dispatch work estimate. External callers that dispatch
        // directly use the safe unit default.
        internal int WorkCost = 1;

        public int ConnectionId
        {
            get
            {
                return Connection == null ? 0 : Connection.Id;
            }
        }

        public bool IsAuthenticated
        {
            get
            {
                return Connection != null
                    && (Connection.State == ConnectionState.Transferring
                        || Connection.State == ConnectionState.FullyJoined);
            }
        }
    }

    /// <summary>
    /// A reliable message handler threw. The router discards that one message and leaves the
    /// connection up, so this carries the failure out to the core instead of a session-killing
    /// exception: the core surfaces it to the player, and the cause survives the frame's
    /// continued dispatch.
    /// </summary>
    public sealed class HandlerFailure
    {
        public MessageDescriptor Descriptor
        {
            get;
        }

        public MessageContext Context
        {
            get;
        }

        public Exception Error
        {
            get;
        }

        public string WireName => Descriptor?.WireName;

        public Type MessageType => Descriptor?.MessageType;

        public HandlerFailure(MessageDescriptor descriptor, MessageContext context, Exception error)
        {
            Descriptor = descriptor;
            Context = context;
            Error = error;
        }
    }

    /// <summary>
    /// How one dispatch ended. The core needs to tell a genuine protocol rejection (an unknown,
    /// unauthorized, or unhandled pre-activation frame - a peer violation) from a message whose
    /// handler ran and threw: only the former may trigger the pre-activation protocol-violation
    /// disconnect, because a thrown handler is a bug on THIS side, not misbehavior by the peer.
    /// </summary>
    internal enum DispatchOutcome
    {
        Handled,
        Rejected,
        HandlerFailed,
    }

    public sealed class MessageRouter : ICoopMessageRegistry
    {
        private sealed class TargetRegistration
        {
            public object Target;
            public IProtocolRegistration Registration;
        }

        private abstract class IntentRegistration
        {
            internal object Target;
            internal MethodInfo Method;
            internal Type MessageType;
            internal int ContextIndex;
            internal int MessageIndex;
            internal bool ExternalContext;
        }

        private sealed class IntentValidatorRegistration : IntentRegistration
        {
        }

        private sealed class IntentRejectionRegistration : IntentRegistration
        {
            internal int ReasonIndex;
        }

        private sealed class PendingIntentRegistrations
        {
            internal readonly List<IntentValidatorRegistration> Validators = new();
            internal readonly List<IntentRejectionRegistration> Rejections = new();
        }

        private readonly List<TargetRegistration> _targetRegistrations = new();
        // Every target that committed successfully, including behaviours with only extended-
        // information handlers or intent validators and no message handler.
        private readonly List<object> _registeredTargets = new();
        // Message type -> the behaviour that owns its handler, so Dispatch can mark the
        // authoritative apply on that behaviour (OnApply + handler) without every module keeping
        // a suppression flag. Only attributed module handlers appear here; an external handler
        // registered directly has no behaviour owner and is simply not marked.
        private readonly Dictionary<Type, CoopBehaviour> _handlerOwners = new();
        private readonly Dictionary<Type, List<IntentValidatorRegistration>> _intentValidators = new();
        private readonly Dictionary<Type, List<IntentRejectionRegistration>> _intentRejections = new();
        private readonly object _targetLock = new();

        /// <summary>Contextual rejection seam for diagnostics, metrics, or disconnect policy.</summary>
        public event Action<MessageRejection> Rejected;

        /// <summary>Raised after a reliable handler throws and its message is discarded. The
        /// connection is deliberately kept, so this is the seam the core uses to surface the
        /// failure (and any rate-limited diagnostics) to the player.</summary>
        public event Action<HandlerFailure> HandlerFailed;

        public MessageRouter()
        {
            MessageRegistry.EnsureInitialized();
        }

        public void RegisterAttributedHandlers(object target)
        {
            RegisterAttributedHandlers(target, false);
        }

        /// <summary>
        /// Registers handlers owned by the core lifecycle. This is intentionally internal: an
        /// extension can register handlers, but cannot obtain the pre-authentication privilege.
        /// </summary>
        internal void RegisterCoreAttributedHandlers(object target)
        {
            RegisterAttributedHandlers(target, true);
        }

        private void RegisterAttributedHandlers(object target, bool coreOwner)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            MessageRegistry.EnsureInitialized();

            // Binding is idempotent per target. The behaviour registry binds before OnEnable, and
            // older integrations still register themselves there; a second call must be a no-op
            // rather than a duplicate-handler failure.
            lock (_targetLock)
            {
                for (var i = 0; i < _registeredTargets.Count; i++)
                {
                    if (ReferenceEquals(_registeredTargets[i], target))
                    {
                        return;
                    }
                }
            }

            // Probe: which behaviours actually REACH registration on this process. A module whose
            // handlers are missing never got this far, which separates "the behaviour was never enabled"
            // from "it was enabled and bound nothing" - and those are the two candidates left for the
            // client dropping every message. Cheap, and only until that is settled.
            CoopPlugin.Log?.LogInfo("handler registration: " + target.GetType().FullName
                + (coreOwner ? " (core)" : ""));
            var methods = target.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            var pending = new List<ProtocolHandlerRegistration>();
            var pendingTypes = new HashSet<Type>();
            var pendingOwners = new Dictionary<Type, CoopBehaviour>();
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var attribute = method.GetCustomAttribute<MessageHandlerAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var contextIndex = -1;
                var messageIndex = -1;
                var messageType = null as Type;
                for (var p = 0; p < parameters.Length; p++)
                {
                    var parameterType = parameters[p].ParameterType;
                    if (parameterType == typeof(MessageContext)
                        || parameterType == typeof(CoopMessageContext))
                    {
                        if (contextIndex >= 0)
                        {
                            throw new InvalidOperationException("Network handler has more than one "
                                + "context parameter: " + method.DeclaringType?.FullName + "."
                                + method.Name);
                        }

                        contextIndex = p;
                        continue;
                    }

                    if (typeof(INetMessage).IsAssignableFrom(parameterType))
                    {
                        if (messageType != null)
                        {
                            throw new InvalidOperationException("Network handler has more than one "
                                + "message parameter: " + method.DeclaringType?.FullName + "."
                                + method.Name);
                        }

                        messageIndex = p;
                        messageType = parameterType;
                        continue;
                    }

                    throw new InvalidOperationException("Invalid network handler parameter "
                        + parameterType.FullName + " on " + method.DeclaringType?.FullName + "."
                        + method.Name);
                }

                if (method.IsStatic || method.ReturnType != typeof(void)
                    || parameters.Length != 2 || contextIndex < 0 || messageIndex < 0
                    || messageType == null)
                {
                    throw new InvalidOperationException("Invalid network handler signature: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                if (attribute.MessageType != null && attribute.MessageType != messageType)
                {
                    throw new InvalidOperationException("Network handler message type does not "
                        + "match its signature: " + method.DeclaringType?.FullName + "."
                        + method.Name);
                }

                if (!pendingTypes.Add(messageType))
                {
                    throw new InvalidOperationException("Duplicate network handler for "
                        + messageType.FullName + " in one target");
                }
                if (!ProtocolRegistry.Default.TryGetRegisteredDescriptor(messageType,
                    out _))
                {
                    throw new InvalidOperationException("No registered network descriptor for "
                        + messageType.FullName);
                }

                var isExternal = parameters[contextIndex].ParameterType
                    == typeof(CoopMessageContext);
                if (target is CoopBehaviour handlerOwner)
                {
                    // Publish owner marks only after the registry accepts the whole batch.
                    pendingOwners[messageType] = handlerOwner;
                }

                pending.Add(new ProtocolHandlerRegistration(messageType,
                    isExternal
                        ? (context, message) =>
                        {
                            var arguments = new object[2];
                            arguments[contextIndex] = ToPublicContext(context);
                            arguments[messageIndex] = message;
                            method.Invoke(target, arguments);
                        }
                : (context, message) =>
                {
                    var arguments = new object[2];
                    arguments[contextIndex] = context;
                    arguments[messageIndex] = message;
                    method.Invoke(target, arguments);
                }));
            }

            var pendingInformationHandlers =
                ExtendedInformationModule.CollectAttributedHandlers(target);
            // Validate every validator/rejection signature before anything is committed.
            var pendingIntents = CollectIntentRegistrations(target);

            IProtocolRegistration handlerRegistration = null;
            try
            {
                if (pending.Count > 0)
                {
                    var owner = coreOwner
                        ? ProtocolRegistry.CoreOwner
                        : ProtocolRegistry.CreateOwner(
                            "handlers:" + target.GetType().Assembly.GetName().Name);
                    lock (_targetLock)
                    {
                        handlerRegistration = ProtocolRegistry.Default.RegisterHandlers(
                            pending, owner);
                    }
                }

                lock (_targetLock)
                {
                    foreach (var pair in pendingOwners)
                    {
                        _handlerOwners[pair.Key] = pair.Value;
                    }
                    if (handlerRegistration != null)
                    {
                        _targetRegistrations.Add(new TargetRegistration
                        {
                            Target = target,
                            Registration = handlerRegistration,
                        });
                    }
                    CommitIntentRegistrationsLocked(pendingIntents);
                    _registeredTargets.Add(target);
                }

                ExtendedInformationModule.CommitAttributedHandlers(pendingInformationHandlers);
            }
            catch
            {
                // The registry accepted the batch but a later commit failed (or one of the
                // commitments itself threw). Put every side of the registration back.
                if (handlerRegistration != null)
                {
                    try
                    {
                        handlerRegistration.Dispose();
                    }
                    catch (Exception cleanupError)
                    {
                        CoopPlugin.Log?.LogWarning("handler registration rollback failed: "
                            + cleanupError.Message);
                    }
                }

                lock (_targetLock)
                {
                    foreach (var pair in pendingOwners)
                    {
                        if (_handlerOwners.TryGetValue(pair.Key, out var current)
                            && ReferenceEquals(current, target))
                        {
                            _handlerOwners.Remove(pair.Key);
                        }
                    }
                    for (var i = _targetRegistrations.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(_targetRegistrations[i].Target, target))
                        {
                            _targetRegistrations.RemoveAt(i);
                        }
                    }
                    RemoveIntentTargets(_intentValidators, target);
                    RemoveIntentTargets(_intentRejections, target);
                    for (var i = _registeredTargets.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(_registeredTargets[i], target))
                        {
                            _registeredTargets.RemoveAt(i);
                        }
                    }
                }

                try
                {
                    ExtendedInformationModule.UnregisterAttributedHandlers(target);
                }
                catch (Exception cleanupError)
                {
                    CoopPlugin.Log?.LogWarning("extended-information registration rollback "
                        + "failed: " + cleanupError.Message);
                }
                throw;
            }
        }

        internal void RegisterIntentValidators(object target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var pending = CollectIntentRegistrations(target);
            lock (_targetLock)
            {
                CommitIntentRegistrationsLocked(pending);
            }
        }

        private static PendingIntentRegistrations CollectIntentRegistrations(object target)
        {
            var pending = new PendingIntentRegistrations();
            var methods = target.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.GetCustomAttribute<IntentValidatorAttribute>() != null)
                {
                    pending.Validators.Add(CreateIntentValidator(method, target));
                }

                if (method.GetCustomAttribute<IntentRejectedAttribute>() != null)
                {
                    pending.Rejections.Add(CreateIntentRejection(method, target));
                }
            }

            return pending;
        }

        private void CommitIntentRegistrationsLocked(PendingIntentRegistrations pending)
        {
            for (var i = 0; i < pending.Validators.Count; i++)
            {
                AddIntentValidator(pending.Validators[i]);
            }
            for (var i = 0; i < pending.Rejections.Count; i++)
            {
                AddIntentRejection(pending.Rejections[i]);
            }
        }

        internal void UnregisterIntentValidators(object target)
        {
            if (target == null)
            {
                return;
            }

            lock (_targetLock)
            {
                RemoveIntentTargets(_intentValidators, target);
                RemoveIntentTargets(_intentRejections, target);
            }
        }

        private static IntentValidatorRegistration CreateIntentValidator(MethodInfo method,
            object target)
        {
            var parameters = method.GetParameters();
            if (method.IsStatic || method.ReturnType != typeof(IntentValidation)
                || parameters.Length != 2)
            {
                throw new InvalidOperationException("Invalid intent validator signature: "
                    + method.DeclaringType?.FullName + "." + method.Name);
            }

            ParseIntentParameters(method, parameters, false, out var contextIndex,
                out var messageIndex, out var reasonIndex, out var messageType);
            return new IntentValidatorRegistration
            {
                Target = target,
                Method = method,
                MessageType = messageType,
                ContextIndex = contextIndex,
                MessageIndex = messageIndex,
                ExternalContext = parameters[contextIndex].ParameterType == typeof(CoopMessageContext),
            };
        }

        private static IntentRejectionRegistration CreateIntentRejection(MethodInfo method,
            object target)
        {
            var parameters = method.GetParameters();
            if (method.IsStatic || method.ReturnType != typeof(void) || parameters.Length != 3)
            {
                throw new InvalidOperationException("Invalid intent rejection signature: "
                    + method.DeclaringType?.FullName + "." + method.Name);
            }

            ParseIntentParameters(method, parameters, true, out var contextIndex,
                out var messageIndex, out var reasonIndex, out var messageType);
            return new IntentRejectionRegistration
            {
                Target = target,
                Method = method,
                MessageType = messageType,
                ContextIndex = contextIndex,
                MessageIndex = messageIndex,
                ReasonIndex = reasonIndex,
                ExternalContext = parameters[contextIndex].ParameterType == typeof(CoopMessageContext),
            };
        }

        private static void ParseIntentParameters(MethodInfo method, ParameterInfo[] parameters,
            bool rejection, out int contextIndex, out int messageIndex, out int reasonIndex,
            out Type messageType)
        {
            contextIndex = -1;
            messageIndex = -1;
            reasonIndex = -1;
            messageType = null;
            for (var p = 0; p < parameters.Length; p++)
            {
                var parameterType = parameters[p].ParameterType;
                if (parameterType == typeof(MessageContext)
                    || parameterType == typeof(CoopMessageContext))
                {
                    if (contextIndex >= 0)
                    {
                        throw InvalidIntentSignature(method, "more than one context parameter");
                    }

                    contextIndex = p;
                    continue;
                }

                if (rejection && parameterType == typeof(string))
                {
                    if (reasonIndex >= 0)
                    {
                        throw InvalidIntentSignature(method, "more than one reason parameter");
                    }

                    reasonIndex = p;
                    continue;
                }

                if (typeof(INetMessage).IsAssignableFrom(parameterType)
                    && !parameterType.IsInterface && !parameterType.IsAbstract
                    && !parameterType.IsGenericType)
                {
                    if (messageIndex >= 0)
                    {
                        throw InvalidIntentSignature(method, "more than one message parameter");
                    }

                    messageIndex = p;
                    messageType = parameterType;
                    continue;
                }

                throw InvalidIntentSignature(method, "invalid parameter " + parameterType.FullName);
            }

            if (contextIndex < 0 || messageIndex < 0 || messageType == null
                || (rejection && reasonIndex < 0)
                || (!rejection && reasonIndex >= 0))
            {
                throw InvalidIntentSignature(method, "expected a context and an intent");
            }

            if (!ProtocolRegistry.Default.TryGetRegisteredDescriptor(messageType, out _))
            {
                throw new InvalidOperationException("Intent validation message type is not "
                    + "registered: " + messageType.FullName + " on "
                    + method.DeclaringType?.FullName + "." + method.Name);
            }
        }

        private static InvalidOperationException InvalidIntentSignature(MethodInfo method,
            string reason)
            => new("Invalid intent handler signature on " + method.DeclaringType?.FullName
                + "." + method.Name + ": " + reason);

        private void AddIntentValidator(IntentValidatorRegistration registration)
        {
            if (!_intentValidators.TryGetValue(registration.MessageType, out var registrations))
            {
                _intentValidators[registration.MessageType] = registrations =
                    new List<IntentValidatorRegistration>();
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

        private void AddIntentRejection(IntentRejectionRegistration registration)
        {
            if (!_intentRejections.TryGetValue(registration.MessageType, out var registrations))
            {
                _intentRejections[registration.MessageType] = registrations =
                    new List<IntentRejectionRegistration>();
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

        private static void RemoveIntentTargets<T>(Dictionary<Type, List<T>> registrations,
            object target) where T : IntentRegistration
        {
            List<Type> empty = null;
            foreach (var pair in registrations)
            {
                var list = pair.Value;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(list[i].Target, target))
                    {
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    empty ??= new List<Type>();
                    empty.Add(pair.Key);
                }
            }

            if (empty != null)
            {
                for (var i = 0; i < empty.Count; i++)
                {
                    registrations.Remove(empty[i]);
                }
            }
        }

        private DispatchOutcome? ValidateIntent(MessageContext context, INetMessage message,
            MessageDescriptor descriptor)
        {
            if (CoopCore.Role != CoopRole.Host)
            {
                return null;
            }

            List<IntentValidatorRegistration> validators;
            lock (_targetLock)
            {
                if (!_intentValidators.TryGetValue(message.GetType(), out var registered)
                    || registered.Count == 0)
                {
                    return null;
                }

                validators = new List<IntentValidatorRegistration>(registered);
            }

            var publicContext = ToPublicContext(context);
            for (var i = 0; i < validators.Count; i++)
            {
                var registration = validators[i];
                IntentValidation validation;
                try
                {
                    var arguments = new object[2];
                    arguments[registration.ContextIndex] = registration.ExternalContext
                        ? publicContext : context;
                    arguments[registration.MessageIndex] = message;
                    validation = (IntentValidation)registration.Method.Invoke(
                        registration.Target, arguments);
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("intent validator failed for "
                        + registration.MessageType.FullName + " on connection "
                        + context.ConnectionId + "; denying the intent: " + error);
                    validation = IntentValidation.Deny("host validator failed");
                }

                if (validation.Allowed)
                {
                    continue;
                }

                var reason = string.IsNullOrEmpty(validation.Reason)
                    ? "rejected by host" : validation.Reason;
                CoopPlugin.Log.LogWarning("intent " + descriptor.WireName + " from connection "
                    + context.ConnectionId + " rejected: " + reason);
                InvokeIntentRejections(context, publicContext, message, reason);

                if (message is ActionMessage action
                    && action.ActionId != Guid.Empty)
                {
                    var live = CoopCore.Instance?.LiveContext;
                    if (live != null)
                    {
                        ActionLedger.Rollback(live, context.ConnectionId, action.ActionId);
                    }
                }

                return DispatchOutcome.Handled;
            }

            return null;
        }

        private void InvokeIntentRejections(MessageContext context,
            CoopMessageContext publicContext, INetMessage message, string reason)
        {
            List<IntentRejectionRegistration> rejections;
            lock (_targetLock)
            {
                if (!_intentRejections.TryGetValue(message.GetType(), out var registered)
                    || registered.Count == 0)
                {
                    return;
                }

                rejections = new List<IntentRejectionRegistration>(registered);
            }

            for (var i = 0; i < rejections.Count; i++)
            {
                var registration = rejections[i];
                var owner = registration.Target as CoopBehaviour;
                owner?.BeginAuthorityDispatch();
                try
                {
                    var arguments = new object[3];
                    arguments[registration.ContextIndex] = registration.ExternalContext
                        ? publicContext : context;
                    arguments[registration.MessageIndex] = message;
                    arguments[registration.ReasonIndex] = reason;
                    registration.Method.Invoke(registration.Target, arguments);
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("intent rejection hook failed for "
                        + registration.MessageType.FullName + ": " + error);
                }
                finally
                {
                    owner?.EndAuthorityDispatch();
                }
            }
        }

        public IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, ProtocolRegistrationOwner owner = null)
        {
            owner ??= ProtocolRegistry.CreateOwner("router:explicit");
            return ProtocolRegistry.Default.RegisterHandler(messageType, handler, owner);
        }

        public void UnregisterAttributedHandlers(object target)
        {
            if (target == null)
            {
                return;
            }

            ExtendedInformationModule.UnregisterAttributedHandlers(target);
            UnregisterIntentValidators(target);

            List<IProtocolRegistration> registrations = null;
            lock (_targetLock)
            {
                for (var i = _targetRegistrations.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_targetRegistrations[i].Target, target))
                    {
                        registrations ??= new List<IProtocolRegistration>();
                        registrations.Add(_targetRegistrations[i].Registration);
                        _targetRegistrations.RemoveAt(i);
                    }
                }

                List<Type> staleOwners = null;
                foreach (var pair in _handlerOwners)
                {
                    if (ReferenceEquals(pair.Value, target))
                    {
                        staleOwners ??= new List<Type>();
                        staleOwners.Add(pair.Key);
                    }
                }

                for (var i = 0; staleOwners != null && i < staleOwners.Count; i++)
                {
                    _handlerOwners.Remove(staleOwners[i]);
                }

                for (var i = _registeredTargets.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_registeredTargets[i], target))
                    {
                        _registeredTargets.RemoveAt(i);
                    }
                }
            }

            if (registrations == null)
            {
                return;
            }
            for (var i = 0; i < registrations.Count; i++)
            {
                registrations[i].Dispose();
            }
        }

        private static CoopMessageContext ToPublicContext(MessageContext context)
        {
            var canonical = -1;
            if (context.Connection != null)
            {
                canonical = CoopCore.Role == CoopRole.Client && context.Connection.Id == 1
                    ? 0 : context.Connection.Id;
            }

            var sender = context.Connection == null ? null
                : PresenceApi.ResolvePlayerRef(canonical)
                    ?? new PlayerRef(canonical, context.Connection.Name,
                        canonical == 0, false);
            return new CoopMessageContext(context.Connection, context.InGame, sender);
        }

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            return Dispatch(context, message, false) == DispatchOutcome.Handled;
        }

        /// <summary>
        /// Internal core lifecycle path for handshake/control dispatch before the generic
        /// authenticated-session gate is established. External assemblies cannot call it. Unlike
        /// the public <see cref="Dispatch(MessageContext, INetMessage)"/> it reports the exact
        /// outcome, so the core can keep a thrown handler from being read as a peer violation.
        /// </summary>
        internal DispatchOutcome DispatchCore(MessageContext context, INetMessage message)
        {
            return Dispatch(context, message, true);
        }

        public bool IsRegistered(INetMessage message)
        {
            if (message == null)
            {
                return false;
            }
            return ProtocolRegistry.Default.CurrentSnapshot.TryGetHandler(message.GetType(),
                out _);
        }

        private DispatchOutcome Dispatch(MessageContext context, INetMessage message, bool corePath)
        {
            if (context == null)
            {
                return Reject(null, null, "message context is null", context);
            }
            if (message == null)
            {
                return Reject(null, null, "message is null", context);
            }

            var snapshot = ProtocolRegistry.Default.CurrentSnapshot;
            if (!snapshot.TryGet(message.GetType(), out var descriptor))
            {
                RollbackAction(context, message);
                return Reject(message.GetType().FullName, message.GetType(),
                    "no descriptor in the current protocol snapshot", context);
            }
            if (!snapshot.TryGetHandler(message.GetType(), out var handler))
            {
                Msg.LogDecodeWarning(descriptor.WireName,
                    "coop: no handler bound for " + descriptor.WireName + " on connection "
                    + context.ConnectionId + "; message dropped (module disabled or "
                    + "RegisterAttributedHandlers was not called)");
                RollbackAction(context, message);
                return Reject(descriptor.WireName, descriptor.MessageType,
                    "no runtime handler is bound", context);
            }

            var authorization = ProtocolAuthorization.Authorize(context,
                corePath && handler.IsCoreOwner);
            if (!authorization.Allowed)
            {
                RollbackAction(context, message);
                return Reject(descriptor.WireName, descriptor.MessageType,
                    authorization.Reason, context);
            }

            var validationOutcome = ValidateIntent(context, message, descriptor);
            if (validationOutcome.HasValue)
            {
                return validationOutcome.Value;
            }

            var timing = PerfProbe.StartHandlerMetric();
            var completed = false;
            var actionApplied = false;
            var hostAction = CoopCore.Role == CoopRole.Host && message is ActionMessage;
            var owner = HandlerOwner(message);
            if (hostAction)
            {
                // The action's OnApply runs before the handler, so give it the authenticated
                // sender up front. Null on a client; never serialized.
                var actionContext = ToPublicContext(context);
                ((ActionMessage)message).Sender = actionContext.Sender;
                ((ActionMessage)message).Connection = actionContext.Connection;
            }

            owner?.BeginAuthorityDispatch();
            try
            {
                if (hostAction)
                {
                    ((ActionMessage)message).OnApply();
                    actionApplied = true;
                }

                handler.Invoke(context, message);
                completed = true;
                return DispatchOutcome.Handled;
            }
            catch (Exception error)
            {
                var typeName = descriptor.WireName;
                if (descriptor.Reliability == Reliability.Reliable)
                {
                    // A reliable handler is no longer allowed to take the session down. Drop the
                    // one message it could not process, keep the connection, and hand the failure
                    // to the core so the player is told instead of being silently disconnected.
                    CoopPlugin.Log.LogError("reliable handler failed for " + typeName
                        + " on connection " + context.ConnectionId
                        + "; discarding the message and keeping the session: " + error);
                    if (hostAction && actionApplied)
                    {
                        // The host applied the action; only the publication failed. Do not ask the
                        // client to undo a change the host kept; retire the optimistic action and
                        // let the module's next authoritative publish reconcile any missing
                        // host-computed fields.
                        AcceptAppliedAction(context, message);
                    }
                    else
                    {
                        RollbackAction(context, message);
                    }
                    RaiseHandlerFailed(new HandlerFailure(descriptor, context, error));
                    return DispatchOutcome.HandlerFailed;
                }

                CoopPlugin.Log.LogWarning("transient handler failed for " + typeName
                    + " on connection " + context.ConnectionId + "; dropping message: " + error);
                if (hostAction && actionApplied)
                {
                    AcceptAppliedAction(context, message);
                }
                else
                {
                    RollbackAction(context, message);
                }
                return DispatchOutcome.HandlerFailed;
            }
            finally
            {
                owner?.EndAuthorityDispatch();
                PerfProbe.EndHandlerMetric(descriptor.MessageType, context.WorkCost,
                    timing, !completed);
            }
        }

        /// <summary>The behaviour that owns this message type's attributed handler, so the
        /// dispatch can mark the authoritative apply (OnApply + handler) on it. Null for a handler
        /// registered directly without a behaviour owner.</summary>
        private CoopBehaviour HandlerOwner(INetMessage message)
        {
            if (message == null)
            {
                return null;
            }

            lock (_targetLock)
            {
                return _handlerOwners.TryGetValue(message.GetType(), out var owner) ? owner : null;
            }
        }

        /// <summary>Runs the <see cref="HandlerFailed"/> observers. This is a boundary between the
        /// router and pluggable subscribers, so one observer throwing must not escape into the
        /// dispatch loop (where a reliable message's exception would be read as a dispatch failure
        /// and disconnect the peer the failure was just reported against).</summary>
        private void RaiseHandlerFailed(HandlerFailure failure)
        {
            try
            {
                HandlerFailed?.Invoke(failure);
            }
            catch (Exception subscriberError)
            {
                CoopPlugin.Log.LogError("HandlerFailed observer threw; the failure was still "
                    + "discarded and the session kept: " + subscriberError);
            }
        }

        /// <summary>The host could not process a client action (module disabled, unauthorized, or
        /// the handler failed). Tell the client to reverse it; otherwise its ledger entry would
        /// stay pending forever and freeze that entity's snapshots.</summary>
        private static void RollbackAction(MessageContext context, INetMessage message)
        {
            if (CoopCore.Role != CoopRole.Host || context == null
                || message is not ActionMessage action || action.ActionId == Guid.Empty)
            {
                return;
            }

            var live = CoopCore.Instance?.LiveContext;
            if (live != null)
            {
                ActionLedger.Rollback(live, context.ConnectionId, action.ActionId);
            }
        }

        private static void AcceptAppliedAction(MessageContext context, INetMessage message)
        {
            if (CoopCore.Role != CoopRole.Host || context == null
                || message is not ActionMessage action || action.ActionId == Guid.Empty)
            {
                return;
            }

            var live = CoopCore.Instance?.LiveContext;
            if (live != null)
            {
                ActionLedger.Accept(live, context.ConnectionId, action.ActionId);
            }
        }

        private DispatchOutcome Reject(string wireName, Type messageType, string reason,
            MessageContext context)
        {
            var rejection = new MessageRejection(wireName, messageType, reason, context);
            Rejected?.Invoke(rejection);
            ProtocolRegistry.Default.RaiseRejection(rejection);
            return DispatchOutcome.Rejected;
        }
    }
}
