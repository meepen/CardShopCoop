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
        public Connection Connection;
        // Kept as a derived view while the game-facing handlers finish migrating.
        public int ConnectionId
        {
            get
            {
                return Connection == null ? 0 : Connection.Id;
            }
        }
        public CoopRole Role;
        public bool InGame;
        public ICoopTransport Transport;
    }

    public sealed class MessageRouter
    {
        private sealed class Route
        {
            public MsgType Type;
            public MessagePolicy Policy;
            public Action<MessageContext, INetMessage> Handler;
            // Retry/heal metadata replaces the former per-MsgType switches in CoopCore.
            // Only the types registered here are retried; a bounded failure calls
            // Heal so the owning module re-baselines instead of replaying forever.
            public bool Retryable;
            public Action Heal;
        }
        private readonly Dictionary<Type, Route> _routes = new Dictionary<Type, Route>();
        private readonly Dictionary<MsgType, Route> _byType = new Dictionary<MsgType, Route>();

        public MessageRouter Register<T>(Action<MessageContext, T> handler,
            MessagePolicy policy = MessagePolicy.Any, bool retryable = false, Action heal = null) where T : INetMessage
        {
            if (handler == null)
                throw new ArgumentNullException("handler");
            var metadata = (NetworkMessageAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(NetworkMessageAttribute));
            if (metadata == null)
                throw new InvalidOperationException(typeof(T).Name + " is missing [NetworkMessage]");
            var route = new Route
            {
                Type = metadata.Type,
                Policy = policy == MessagePolicy.Any ? metadata.Policy : policy,
                Handler = (context, message) => handler(context, (T)message),
                Retryable = retryable,
                Heal = heal
            };
            _routes[typeof(T)] = route;
            _byType[route.Type] = route;
            return this;
        }

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            if (context == null || message == null)
                return false;
            Route route;
            if (!_routes.TryGetValue(message.GetType(), out route))
                return false;
            // Disconnect is the terminal event itself. It must still reach the handler after
            // transport records its reason, otherwise the state transition suppresses the very
            // reason that Core/UI/modules are supposed to receive.
            if (context.Connection != null && context.Connection.State == ConnectionState.Disconnected
                && route.Type != MsgType.Disconnect)
                return false;
            if (context.Connection != null && context.Connection.IsDisconnectingOrDisconnected
                && route.Type == MsgType.Disconnect)
            {
                route.Handler(context, message);
                return true;
            }
            if (context.Connection != null
                && (route.Type == MsgType.Ping || route.Type == MsgType.Pong)
                && !IsKeepalivePhase(context.Connection.State))
                return false;
            // InGameOnly is a scene gate, not an authentication gate.  Before the
            // handshake/world transfer completes, only the deliberately small control
            // lane may reach a handler; every gameplay, economy and state route is
            // rejected even when its metadata forgot InGameOnly.
            if (context.Connection != null && context.Connection.State != ConnectionState.FullyJoined
                && !IsPreJoinControl(message.Type)
                && !IsClientBaselineFrame(route, context))
                return false;
            bool authenticatedFullyJoined = IsAuthenticatedFullyJoined(context, message.Type);
            // The Any policy on FullyJoined is only a direction exception.  It must not
            // admit the signal in Handshaking, after completion, or on a stale connection.
            if (message.Type == MsgType.FullyJoined && !authenticatedFullyJoined)
                return false;
            if (!authenticatedFullyJoined && !Allowed(route.Policy, context))
                return false;
            route.Handler(context, message);
            return true;
        }

        public bool IsRegistered(INetMessage message)
        {
            return message != null && _routes.ContainsKey(message.GetType());
        }

        /// <summary>True only when this route is role-allowed but waiting for the game scene.</summary>
        public bool IsTransientInGameGate(MessageContext context, INetMessage message)
        {
            if (context == null || message == null)
                return false;
            if (!_routes.TryGetValue(message.GetType(), out var route))
                return false;
            if (!RoleAllowed(route.Policy, context))
                return false;
            return (route.Policy & MessagePolicy.InGameOnly) != 0 && !context.InGame;
        }

        /// <summary>True when a failed dispatch of this type should be retried.</summary>
        public bool IsRetryable(MsgType type)
        {
            return _byType.TryGetValue(type, out var route) && route.Retryable;
        }

        /// <summary>Ask the owning module to re-baseline after a dropped message.</summary>
        public void Heal(MsgType type)
        {
            if (_byType.TryGetValue(type, out var route))
                route.Heal?.Invoke();
        }

        private static bool Allowed(MessagePolicy policy, MessageContext context)
        {
            return RoleAllowed(policy, context)
                && (context.Connection == null
                    || (context.Connection.State == ConnectionState.FullyJoined
                        || IsClientBaselinePolicy(policy, context)))
                && ((policy & MessagePolicy.InGameOnly) == 0 || context.InGame);
        }

        private static bool RoleAllowed(MessagePolicy policy, MessageContext context)
        {
            if ((policy & MessagePolicy.HostOnly) != 0 && context.Role != CoopRole.Host)
                return false;
            if ((policy & MessagePolicy.ClientOnly) != 0 && context.Role != CoopRole.Client)
                return false;
            return true;
        }

        private static bool IsPreJoinControl(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello:
                case MsgType.Welcome:
                case MsgType.SaveChunk:
                case MsgType.SaveDone:
                case MsgType.BundleChunk:
                case MsgType.BundleDone:
                case MsgType.EnumSync:
                case MsgType.FullyJoined:
                case MsgType.FullyJoinedAck:
                case MsgType.Disconnect:
                case MsgType.Bye:
                case MsgType.Ping:
                case MsgType.Pong:
                    return true;
                default:
                    return false;
            }
        }

        // The host deliberately sends the authoritative baseline before FullyJoinedAck.
        // Those frames are reliable and ordered ahead of the ACK, but the connection must
        // remain Transferring until the ACK is consumed.  Admit only host->client routes
        // here; client gameplay traffic must not gain a pre-join escape hatch.
        private static bool IsClientBaselineFrame(Route route, MessageContext context)
        {
            return context.Role == CoopRole.Client
                && context.Connection != null
                && context.Connection.State == ConnectionState.Transferring
                && (route.Policy & MessagePolicy.ClientOnly) != 0
                && IsAuthoritativeBaseline(route.Type);
        }

        private static bool IsClientBaselinePolicy(MessagePolicy policy, MessageContext context)
        {
            return context.Role == CoopRole.Client
                && context.Connection != null
                && context.Connection.State == ConnectionState.Transferring
                && (policy & MessagePolicy.ClientOnly) != 0;
        }

        // FullyJoined is the one control whose wire direction is opposite the generic
        // role policy: a client sends it to a host.  Do not turn that exception into a
        // general pre-join escape hatch.  The connection object is the transport's
        // authenticated identity, must still be the exact active object, and must be
        // immediately after the world transfer.
        private static bool IsAuthenticatedFullyJoined(MessageContext context, MsgType type)
        {
            if (type != MsgType.FullyJoined || context.Role != CoopRole.Host
                || context.Connection == null
                || context.Connection.State != ConnectionState.Transferring
                || context.Transport == null)
                return false;
            foreach (var active in context.Transport.Connections)
                if (ReferenceEquals(active, context.Connection))
                    return true;
            return false;
        }

        private static bool IsAuthoritativeBaseline(MsgType type)
        {
            switch (type)
            {
                case MsgType.CoinSet:
                case MsgType.DayTime:
                case MsgType.ProgressSet:
                case MsgType.ShelfDelta:
                case MsgType.PriceList:
                case MsgType.CardShelfDelta:
                case MsgType.RegisterState:
                case MsgType.RegisterCart:
                case MsgType.ObjMoveDelta:
                case MsgType.ShopName:
                case MsgType.LightState:
                case MsgType.PopState:
                case MsgType.LicenseState:
                case MsgType.StaffState:
                case MsgType.ShopState:
                case MsgType.SettingsState:
                case MsgType.MarketState:
                case MsgType.ReportState:
                case MsgType.ContainerState:
                case MsgType.TournamentState:
                case MsgType.GradingState:
                case MsgType.TradeState:
                case MsgType.TableState:
                case MsgType.PlayerModelState:
                case MsgType.BoxSnapshot:
                case MsgType.WarehouseState:
                case MsgType.PlayTableMatchState:
                case MsgType.Roster:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsKeepalivePhase(ConnectionState state)
        {
            return state == ConnectionState.Handshaking
                || state == ConnectionState.Transferring
                || state == ConnectionState.FullyJoined;
        }
    }
}
