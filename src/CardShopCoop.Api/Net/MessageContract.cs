using System;

namespace CardShopCoop.Net.Protocol
{
    /// <summary>
    /// Selects the transport lane for a message. Reliable is the safe default; transient
    /// messages are explicitly opt-in and are never promoted to the reliable lane.
    /// </summary>
    public enum Reliability
    {
        Reliable,
        Transient,
    }
}

namespace CardShopCoop.Net
{
    /// <summary>Typed representation of one protocol message. Payload serialization is
    /// centralized; message DTOs contain data only.</summary>
    public interface INetMessage
    {
    }

    /// <summary>Marks a concrete <see cref="INetMessage"/> for assembly discovery.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NetworkMessageAttribute : Attribute
    {
        public Protocol.Reliability Reliability
        {
            get;
            set;
        } = Protocol.Reliability.Reliable;
    }

    /// <summary>Marks a method as the runtime handler for one registered message type.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class MessageHandlerAttribute : Attribute
    {
        public Type MessageType
        {
            get;
        }

        public MessageHandlerAttribute(Type messageType)
        {
            MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));
        }
    }
}
