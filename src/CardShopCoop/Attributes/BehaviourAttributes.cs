using System;

namespace CardShopCoop.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ServerBehaviourAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ClientBehaviourAttribute : Attribute
    {
    }

    /// <summary>Marks a behaviour that exists for the lifetime of the plugin process rather
    /// than only while a host/client session is active.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PersistentBehaviourAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnClientJoinedAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnClientDisconnectedAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnFullyJoinedAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnSessionStartedAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class OnSessionStoppedAttribute : Attribute
    {
    }
}
