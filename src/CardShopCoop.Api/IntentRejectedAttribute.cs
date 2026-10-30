using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a host method that observes a denied client intent. The method takes one context
    /// parameter, one <c>INetMessage</c> parameter, and one string reason parameter, in any order.
    /// It runs before the framework rejects the action, so a module can attach authoritative
    /// state to the rejection response.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class IntentRejectedAttribute : Attribute
    {
    }
}
