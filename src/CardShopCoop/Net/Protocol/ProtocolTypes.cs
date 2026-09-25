using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CardShopCoop.Net;

namespace CardShopCoop.Net.Protocol
{
    /// <summary>
    /// The immutable catalog entry for one message type. Wire identity is always and only
    /// <see cref="Type.FullName"/>; transport reliability is the sole message policy.
    /// </summary>
    public sealed class MessageDescriptor
    {
        public Type MessageType
        {
            get;
        }

        public string WireName
        {
            get;
        }

        public Reliability Reliability
        {
            get;
        }

        public MessageDescriptor(Type messageType, Reliability reliability = Reliability.Reliable)
        {
            if (messageType == null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }
            if (!typeof(INetMessage).IsAssignableFrom(messageType)
                || messageType.IsInterface || messageType.IsAbstract || messageType.IsGenericType)
            {
                throw new ArgumentException("A message descriptor requires a concrete INetMessage type",
                    nameof(messageType));
            }
            if (string.IsNullOrEmpty(messageType.FullName))
            {
                throw new ArgumentException("A message descriptor requires a type with a FullName",
                    nameof(messageType));
            }
            if (!Enum.IsDefined(typeof(Reliability), reliability))
            {
                throw new ArgumentOutOfRangeException(nameof(reliability));
            }

            MessageType = messageType;
            WireName = messageType.FullName;
            Reliability = reliability;
        }

        public override string ToString()
        {
            return WireName + " (" + Reliability + ")";
        }
    }

    /// <summary>An opaque owner identity used to scope registration lifetimes.</summary>
    public sealed class ProtocolRegistrationOwner
    {
        private readonly bool _isCoreOwner;

        public string Id
        {
            get;
        }

        public static ProtocolRegistrationOwner Create(string id)
        {
            return new ProtocolRegistrationOwner(id);
        }

        internal ProtocolRegistrationOwner(string id)
            : this(id, false)
        {
        }

        internal ProtocolRegistrationOwner(string id, bool isCoreOwner)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A protocol owner needs an id", nameof(id));
            }
            Id = id.Trim();
            _isCoreOwner = isCoreOwner;
        }

        internal bool IsCoreOwner
        {
            get
            {
                return _isCoreOwner;
            }
        }
    }

    public delegate void ProtocolMessageHandler(MessageContext context, INetMessage message);

    public enum RegistrationState
    {
        Active,
        PendingNextSession,
        Disposed,
    }

    public interface IProtocolRegistration : IDisposable
    {
        ProtocolRegistrationOwner Owner
        {
            get;
        }

        RegistrationState State
        {
            get;
        }
    }

    /// <summary>
    /// Exclusive lifetime of one immutable protocol session snapshot. Registrations made while
    /// this lease is active are queued for the next lease and cannot change <see cref="Snapshot"/>.
    /// </summary>
    public interface IProtocolSessionLease : IDisposable
    {
        int Generation
        {
            get;
        }

        ProtocolSnapshot Snapshot
        {
            get;
        }
    }

    public sealed class ProtocolHandlerRegistration
    {
        public Type MessageType
        {
            get;
        }

        public ProtocolMessageHandler Handler
        {
            get;
        }

        public ProtocolHandlerRegistration(Type messageType, ProtocolMessageHandler handler)
        {
            MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }
    }

    public sealed class ProtocolHandlerBinding
    {
        private readonly ProtocolMessageHandler _handler;
        private readonly object _lifetimeGate = new object();
        private readonly Dictionary<int, int> _inFlightByThread = new Dictionary<int, int>();
        private bool _active = true;
        private int _inFlight;

        public Type MessageType
        {
            get;
        }

        public string OwnerId
        {
            get;
        }

        internal bool IsCoreOwner
        {
            get;
        }

        internal ProtocolHandlerBinding(Type messageType, ProtocolMessageHandler handler,
            ProtocolRegistrationOwner owner)
        {
            MessageType = messageType;
            _handler = handler;
            OwnerId = owner.Id;
            IsCoreOwner = owner.IsCoreOwner;
        }

        internal bool Invoke(MessageContext context, INetMessage message)
        {
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            lock (_lifetimeGate)
            {
                if (!_active)
                    return false;
                _inFlight++;
                _inFlightByThread.TryGetValue(threadId, out var threadCount);
                _inFlightByThread[threadId] = threadCount + 1;
            }

            try
            {
                _handler(context, message);
                return true;
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    _inFlight--;
                    _inFlightByThread[threadId] = _inFlightByThread[threadId] - 1;
                    if (_inFlightByThread[threadId] == 0)
                        _inFlightByThread.Remove(threadId);
                    if (_inFlight == 0)
                        System.Threading.Monitor.PulseAll(_lifetimeGate);
                }
            }
        }

        /// <summary>
        /// Prevents future invocations and waits for invocations on other threads to finish.
        /// A handler may dispose its own registration reentrantly; waiting for itself would deadlock.
        /// </summary>
        internal void DeactivateAndWait()
        {
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            lock (_lifetimeGate)
            {
                _active = false;
                _inFlightByThread.TryGetValue(threadId, out var ownInvocations);
                while (_inFlight > ownInvocations)
                    System.Threading.Monitor.Wait(_lifetimeGate);
            }
        }
    }

    public sealed class MessageRejection
    {
        public string WireName
        {
            get;
        }

        public Type MessageType
        {
            get;
        }

        public string Reason
        {
            get;
        }

        public MessageContext Context
        {
            get;
        }

        internal MessageRejection(string wireName, Type messageType, string reason, MessageContext context)
        {
            WireName = wireName;
            MessageType = messageType;
            Reason = reason;
            Context = context;
        }

        public override string ToString()
        {
            return "Rejected " + (WireName ?? "<unknown>") + ": " + Reason;
        }
    }

    internal sealed class ProtocolAuthorizationResult
    {
        public bool Allowed
        {
            get;
        }

        public string Reason
        {
            get;
        }

        private ProtocolAuthorizationResult(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        public static ProtocolAuthorizationResult Allow()
        {
            return new ProtocolAuthorizationResult(true, null);
        }

        public static ProtocolAuthorizationResult Deny(string reason)
        {
            return new ProtocolAuthorizationResult(false, reason ?? "authorization failed");
        }
    }

    /// <summary>Immutable descriptor and handler view captured for one session.</summary>
    public sealed class ProtocolSnapshot
    {
        public const int MaxMessageCount = UInt16.MaxValue + 1;

        private readonly IReadOnlyDictionary<string, MessageDescriptor> _descriptors;
        private readonly IReadOnlyDictionary<string, ProtocolHandlerBinding> _handlers;
        private readonly IReadOnlyList<string> _orderedWireNames;
        private readonly IReadOnlyDictionary<string, UInt16> _idsByName;
        private readonly IReadOnlyDictionary<UInt16, MessageDescriptor> _descriptorsById;

        public int Generation
        {
            get;
        }

        public bool IsFrozen
        {
            get;
        }

        public IReadOnlyDictionary<string, MessageDescriptor> Descriptors
        {
            get
            {
                return _descriptors;
            }
        }

        /// <summary>
        /// The session's one deterministic message order. Names are sorted with
        /// <see cref="StringComparer.Ordinal"/> and IDs are their zero-based indexes.
        /// </summary>
        public IReadOnlyList<string> OrderedWireNames
        {
            get
            {
                return _orderedWireNames;
            }
        }

        internal ProtocolSnapshot(int generation, bool isFrozen,
            IDictionary<string, MessageDescriptor> descriptors,
            IDictionary<string, ProtocolHandlerBinding> handlers)
        {
            if (descriptors == null)
            {
                throw new ArgumentNullException(nameof(descriptors));
            }
            if (descriptors.Count > MaxMessageCount)
            {
                throw new ProtocolRegistrationException("A protocol snapshot cannot contain more than "
                    + MaxMessageCount + " messages");
            }

            Generation = generation;
            IsFrozen = isFrozen;
            _descriptors = new ReadOnlyDictionary<string, MessageDescriptor>(
                new Dictionary<string, MessageDescriptor>(descriptors, StringComparer.Ordinal));
            _handlers = new ReadOnlyDictionary<string, ProtocolHandlerBinding>(
                new Dictionary<string, ProtocolHandlerBinding>(handlers, StringComparer.Ordinal));

            var orderedNames = new List<string>(descriptors.Keys);
            orderedNames.Sort(StringComparer.Ordinal);
            _orderedWireNames = new ReadOnlyCollection<string>(orderedNames);

            var idsByName = new Dictionary<string, UInt16>(orderedNames.Count, StringComparer.Ordinal);
            var descriptorsById = new Dictionary<UInt16, MessageDescriptor>(orderedNames.Count);
            for (var i = 0; i < orderedNames.Count; i++)
            {
                var id = checked((UInt16)i);
                var name = orderedNames[i];
                idsByName.Add(name, id);
                descriptorsById.Add(id, _descriptors[name]);
            }
            _idsByName = new ReadOnlyDictionary<string, UInt16>(idsByName);
            _descriptorsById = new ReadOnlyDictionary<UInt16, MessageDescriptor>(descriptorsById);
        }

        public bool TryGet(string wireName, out MessageDescriptor descriptor)
        {
            descriptor = null;
            return wireName != null && _descriptors.TryGetValue(wireName, out descriptor);
        }

        public bool TryGet(Type messageType, out MessageDescriptor descriptor)
        {
            descriptor = null;
            return messageType != null && messageType.FullName != null
                && _descriptors.TryGetValue(messageType.FullName, out descriptor)
                && descriptor.MessageType == messageType;
        }

        public bool TryGet(UInt16 id, out MessageDescriptor descriptor)
        {
            return _descriptorsById.TryGetValue(id, out descriptor);
        }

        public bool TryGetId(string wireName, out UInt16 id)
        {
            id = default(UInt16);
            return wireName != null && _idsByName.TryGetValue(wireName, out id);
        }

        public bool TryGetId(Type messageType, out UInt16 id)
        {
            id = default(UInt16);
            return messageType != null && messageType.FullName != null
                && _idsByName.TryGetValue(messageType.FullName, out id)
                && _descriptorsById[id].MessageType == messageType;
        }

        public bool TryGetName(UInt16 id, out string wireName)
        {
            wireName = null;
            if (!_descriptorsById.TryGetValue(id, out var descriptor))
            {
                return false;
            }

            wireName = descriptor.WireName;
            return true;
        }

        public bool TryGetMessageType(UInt16 id, out Type messageType)
        {
            messageType = null;
            if (!_descriptorsById.TryGetValue(id, out var descriptor))
            {
                return false;
            }

            messageType = descriptor.MessageType;
            return true;
        }

        public bool TryGetHandler(Type messageType, out ProtocolHandlerBinding handler)
        {
            handler = null;
            return messageType != null && messageType.FullName != null
                && _handlers.TryGetValue(messageType.FullName, out handler)
                && handler.MessageType == messageType;
        }
    }
}
