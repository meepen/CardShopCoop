using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a host method that observes a denied client intent. The method takes one context
    /// parameter, one <c>INetMessage</c> parameter, and one string reason parameter, in any order.
    /// It runs before the generic action rollback, so a module can re-anchor the client with
    /// authoritative state under the rejected action id (the register checkout pattern).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class IntentRejectedAttribute : Attribute
    {
    }
}
