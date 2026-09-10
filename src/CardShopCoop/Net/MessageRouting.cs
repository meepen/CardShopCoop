using System;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    [Flags]
    public enum MessagePolicy
    {
        Any = 0, HostOnly = 1, ClientOnly = 2, InGameOnly = 4, HostOnlyInGame = 5, ClientOnlyInGame = 6
    }
    public enum Delivery
    {
        Reliable, Transient
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NetworkMessageAttribute : Attribute
    {
        public MsgType Type
        {
            get; private set;
        }
        public Delivery Delivery
        {
            get; set;
        }
        public MessagePolicy Policy
        {
            get; set;
        }
        public NetworkMessageAttribute(MsgType type)
        {
            Type = type;
        }
    }

    public sealed class MessageContext
    {
        public int ConnectionId;
        public CoopRole Role;
        public bool InGame;
        public ICoopTransport Transport;
    }

    public sealed class MessageRouter
    {
        private sealed class Route
        {
            public MessagePolicy Policy;
            public Action<MessageContext, INetMessage> Handler;
        }
        private readonly Dictionary<Type, Route> _routes = new Dictionary<Type, Route>();

        public MessageRouter Register<T>(Action<MessageContext, T> handler,
            MessagePolicy policy = MessagePolicy.Any) where T : INetMessage
        {
            if (handler == null)
                throw new ArgumentNullException("handler");
            var metadata = (NetworkMessageAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(NetworkMessageAttribute));
            _routes[typeof(T)] = new Route
            {
                Policy = policy == MessagePolicy.Any && metadata != null ? metadata.Policy : policy,
                Handler = (context, message) => handler(context, (T)message)
            };
            return this;
        }

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            if (context == null || message == null)
                return false;
            Route route;
            if (!_routes.TryGetValue(message.GetType(), out route))
                return false;
            if (!Allowed(route.Policy, context))
                return true;
            route.Handler(context, message);
            return true;
        }

        private static bool Allowed(MessagePolicy policy, MessageContext context)
        {
            if ((policy & MessagePolicy.HostOnly) != 0 && context.Role != CoopRole.Host)
                return false;
            if ((policy & MessagePolicy.ClientOnly) != 0 && context.Role != CoopRole.Client)
                return false;
            return (policy & MessagePolicy.InGameOnly) == 0 || context.InGame;
        }
    }
}
