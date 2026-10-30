using System;

namespace CardShopCoop.Attributes
{
    /// <summary>
    /// Marks a behaviour that exists on the HOST for the lifetime of a co-op session. Discovered
    /// from the CardShopCoop assembly and from any external mod that soft-depends on it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ServerBehaviourAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a behaviour that exists on a JOINER for the lifetime of a co-op session.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ClientBehaviourAttribute : Attribute
    {
    }

    /// <summary>Marks a behaviour that exists for the lifetime of the plugin process rather
    /// than only while a host/client session is active. It still receives
    /// <c>[OnSessionStarted]</c> and <c>[OnSessionStopped]</c> once for each session while it
    /// remains alive across them.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PersistentBehaviourAttribute : Attribute
    {
    }

    /// <summary>
    /// Invoked when a client enters the session: on the host, after a joining client passes
    /// admission and begins the world transfer; on a client, after the host accepts this client
    /// (its Welcome is processed). Fires before [OnFullyJoined]; application traffic is not yet
    /// allowed at this point.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnClientJoinedAttribute : Attribute
    {
    }

    /// <summary>
    /// Invoked when a peer leaves the session. The method takes an <c>ICoopPeer</c> and a
    /// <see cref="CardShopCoop.Net.Connection.DisconnectInfo"/> parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnClientDisconnectedAttribute : Attribute
    {
    }

    /// <summary>
    /// Invoked when a peer is fully in the game and application traffic is allowed. On the host,
    /// this is the point to send a join baseline to the <c>ICoopPeer</c> the method receives.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnFullyJoinedAttribute : Attribute
    {
    }

    /// <summary>
    /// Invoked once for this peer when a co-op session starts. A session behaviour receives it
    /// after the transport starts and the message and extended-information catalogs are frozen; a
    /// persistent behaviour receives it before the transport starts.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnSessionStartedAttribute : Attribute
    {
    }

    /// <summary>
    /// Invoked once for this peer when the co-op session ends. A persistent behaviour receives it
    /// after the transport stops.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnSessionStoppedAttribute : Attribute
    {
    }
}
