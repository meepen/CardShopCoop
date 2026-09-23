using System;
using System.IO;
using System.Reflection;
using CardShopCoop.Net.Protocol;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Public facade over the protocol registry. Named framing puts the descriptor's
    /// Type.FullName on the wire; compact framing resolves through the immutable session snapshot.
    /// The registry applies one internal framing ceiling to every payload.
    /// </summary>
    public static class MessageRegistry
    {
        // This is a framing/resource ceiling, not part of a message descriptor or public policy.
        // Keep this single source shared by inbound validation and outbound frame construction.
        internal const int MaxSerializedPayloadBytes = Msg.MaxFrameSize - (8 * 1024);

        static MessageRegistry()
        {
            // Core discovery has a narrowly scoped optional-dependency path. External callers
            // continue through the strict public RegisterAssembly API below.
            ProtocolRegistry.EnsureCoreAssemblyRegistered(typeof(MessageRegistry).Assembly);
        }

        public static ProtocolSnapshot CurrentSnapshot
        {
            get
            {
                EnsureInitialized();
                return ProtocolRegistry.Default.CurrentSnapshot;
            }
        }

        public static IProtocolRegistration RegisterAssembly(Assembly assembly,
            ProtocolRegistrationOwner owner)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterAssembly(assembly, owner);
        }

        public static IProtocolRegistration RegisterAssembly(Assembly assembly, string ownerId)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterAssembly(assembly, ownerId);
        }

        public static IProtocolRegistration RegisterMessage(MessageDescriptor descriptor,
            ProtocolRegistrationOwner owner)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterMessage(descriptor, owner);
        }

        public static IProtocolRegistration RegisterMessage(MessageDescriptor descriptor,
            string ownerId)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterMessage(descriptor, ownerId);
        }

        public static IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, ProtocolRegistrationOwner owner)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterHandler(messageType, handler, owner);
        }

        public static IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, string ownerId)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterHandler(messageType, handler, ownerId);
        }

        public static IProtocolRegistration RegisterHandlers(
            System.Collections.Generic.IReadOnlyList<ProtocolHandlerRegistration> registrations,
            ProtocolRegistrationOwner owner)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.RegisterHandlers(registrations, owner);
        }

        public static IProtocolSessionLease BeginSession()
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.BeginSession();
        }

        internal static void EnsureInitialized()
        {
            // Referencing the type is sufficient to run its static constructor. Keeping this
            // method explicit makes the bootstrap dependency visible to router callers.
        }

        public static INetMessage Deserialize(string name, byte[] payload, int offset, int count,
            out Type type)
        {
            EnsureInitialized();
            if (!ProtocolRegistry.Default.CurrentSnapshot.TryGet(name, out var descriptor))
            {
                Msg.LogDecodeWarning(name, "network: no DTO registered for message " + name
                    + " - frame dropped");
                throw new InvalidDataException("No DTO registered for " + name);
            }
            type = descriptor.MessageType;
            return Deserialize(descriptor, payload, offset, count);
        }

        internal static INetMessage Deserialize(MessageDescriptor descriptor, byte[] payload,
            int offset, int count)
        {
            EnsureInitialized();
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            if (payload == null || offset < 0 || count < 0 || offset > payload.Length - count)
            {
                throw new InvalidDataException("Invalid payload bounds for " + descriptor.WireName);
            }
            if (count > MaxSerializedPayloadBytes)
            {
                Msg.LogDecodeWarning(descriptor.WireName,
                    "network: payload for " + descriptor.WireName + " exceeds the protocol limit"
                    + " - frame dropped");
                throw new InvalidDataException("Payload exceeds the protocol limit for "
                    + descriptor.WireName);
            }

            return WireCodec.Deserialize(descriptor.MessageType, payload, offset, count);
        }

        /// <summary>Serializes through the same descriptor snapshot used for decoding.</summary>
        public static byte[] Serialize(INetMessage message)
        {
            EnsureInitialized();
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (!ProtocolRegistry.Default.CurrentSnapshot.TryGet(message.GetType(), out var descriptor))
            {
                throw new InvalidDataException("No network message registration for "
                    + message.GetType().FullName);
            }
            var payload = WireCodec.Serialize(message);
            if (payload.Length > MaxSerializedPayloadBytes)
            {
                throw new InvalidDataException("Payload exceeds the protocol limit for "
                    + descriptor.WireName);
            }
            return payload;
        }

        public static string NameFor(INetMessage message)
        {
            EnsureInitialized();
            if (message == null
                || !ProtocolRegistry.Default.CurrentSnapshot.TryGet(message.GetType(), out var descriptor))
            {
                throw new InvalidDataException("No network message registration for "
                    + (message == null ? "null" : message.GetType().FullName));
            }
            return descriptor.WireName;
        }

        public static bool TryGetDescriptor(string name, out MessageDescriptor descriptor)
        {
            EnsureInitialized();
            return ProtocolRegistry.Default.CurrentSnapshot.TryGet(name, out descriptor);
        }

        internal static bool TryGetMessageType(string name, out Type type)
        {
            EnsureInitialized();
            if (ProtocolRegistry.Default.CurrentSnapshot.TryGet(name, out var descriptor))
            {
                type = descriptor.MessageType;
                return true;
            }
            type = null;
            return false;
        }
    }
}
