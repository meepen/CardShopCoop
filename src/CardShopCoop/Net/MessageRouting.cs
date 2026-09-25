using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Generic;
using CardShopCoop.Util;
using System.Reflection;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Net.Connection;
using CardShopCoop.Api;

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
    /// A reliable handler failed after the router requested peer recovery. The exception remains
    /// visible to the bounded Core dispatch loop so it can stop consuming this peer's messages
    /// for the current frame instead of treating the failed reliable message as handled.
    /// </summary>
    internal sealed class ReliableMessageHandlerException : Exception
    {
        internal ReliableMessageHandlerException(Type messageType, Exception innerException)
            : base("Reliable handler failed for " + (messageType?.FullName ?? "<unknown>"),
                innerException)
        {
            MessageType = messageType;
        }

        internal Type MessageType
        {
            get;
        }
    }

    public sealed class MessageRouter : ICoopMessageRegistry
    {
        private sealed class TargetRegistration
        {
            public object Target;
            public IProtocolRegistration Registration;
        }

        private readonly List<TargetRegistration> _targetRegistrations = new();
        private readonly object _targetLock = new();

        /// <summary>Contextual rejection seam for diagnostics, metrics, or disconnect policy.</summary>
        public event Action<MessageRejection> Rejected;

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
            var methods = target.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            var pending = new List<ProtocolHandlerRegistration>();
            var pendingTypes = new HashSet<Type>();
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var attribute = method.GetCustomAttribute<MessageHandlerAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var isExternal = parameters.Length > 0
                    && parameters[0].ParameterType == typeof(CoopMessageContext);
                if (method.IsStatic || method.ReturnType != typeof(void) || parameters.Length != 2
                    || (parameters[0].ParameterType != typeof(MessageContext) && !isExternal)
                    || !typeof(INetMessage).IsAssignableFrom(parameters[1].ParameterType)
                    || parameters[1].ParameterType != attribute.MessageType)
                {
                    throw new InvalidOperationException("Invalid network handler signature: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                var messageType = parameters[1].ParameterType;
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

                pending.Add(new ProtocolHandlerRegistration(messageType,
                    isExternal
                        ? (context, message) => method.Invoke(target,
                            new object[] { ToPublicContext(context), message })
                        : (context, message) => method.Invoke(target,
                            new object[] { context, message })));
            }

            if (pending.Count == 0)
            {
                return;
            }

            var owner = coreOwner
                ? ProtocolRegistry.CoreOwner
                : ProtocolRegistry.CreateOwner(
                    "handlers:" + target.GetType().Assembly.GetName().Name);
            lock (_targetLock)
            {
                var registration = ProtocolRegistry.Default.RegisterHandlers(pending, owner);
                _targetRegistrations.Add(new TargetRegistration
                {
                    Target = target,
                    Registration = registration,
                });
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
            => new CoopMessageContext(context.Connection, context.InGame);

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            return Dispatch(context, message, false);
        }

        /// <summary>
        /// Internal core lifecycle path for handshake/control dispatch before the generic
        /// authenticated-session gate is established. External assemblies cannot call it.
        /// </summary>
        internal bool DispatchCore(MessageContext context, INetMessage message)
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

        private bool Dispatch(MessageContext context, INetMessage message, bool corePath)
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
                return Reject(message.GetType().FullName, message.GetType(),
                    "no descriptor in the current protocol snapshot", context);
            }
            if (!snapshot.TryGetHandler(message.GetType(), out var handler))
            {
                return Reject(descriptor.WireName, descriptor.MessageType,
                    "no runtime handler is bound", context);
            }

            var authorization = ProtocolAuthorization.Authorize(context,
                corePath && handler.IsCoreOwner);
            if (!authorization.Allowed)
            {
                return Reject(descriptor.WireName, descriptor.MessageType,
                    authorization.Reason, context);
            }

            var timing = PerfProbe.StartHandlerMetric();
            var completed = false;
            try
            {
                handler.Invoke(context, message);
                completed = true;
                return true;
            }
            catch (Exception error)
            {
                var typeName = descriptor.WireName;
                if (descriptor.Reliability == Reliability.Reliable)
                {
                    CoopPlugin.Log.LogError("reliable handler failed for " + typeName
                        + " on connection " + context.ConnectionId
                        + "; requesting session recovery: " + error);
                    context.Transport?.GracefulDisconnect(context.Connection,
                        new Connection.DisconnectInfo(
                            "reliable message handler failed; session recovery required", false,
                            "handler_failed", true, context.Connection.State));
                    throw new ReliableMessageHandlerException(descriptor.MessageType, error);
                }

                CoopPlugin.Log.LogWarning("transient handler failed for " + typeName
                    + " on connection " + context.ConnectionId + "; dropping message: " + error);
                return false;
            }
            finally
            {
                PerfProbe.EndHandlerMetric(descriptor.MessageType, context.WorkCost,
                    timing, !completed);
            }
        }

        private bool Reject(string wireName, Type messageType, string reason, MessageContext context)
        {
            var rejection = new MessageRejection(wireName, messageType, reason, context);
            Rejected?.Invoke(rejection);
            ProtocolRegistry.Default.RaiseRejection(rejection);
            return false;
        }
    }
}
