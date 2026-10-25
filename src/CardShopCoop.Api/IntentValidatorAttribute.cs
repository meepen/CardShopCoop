using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a host method that decides whether a client's intent is allowed. The method takes one
    /// context parameter (<see cref="CoopMessageContext"/> or the internal
    /// <c>MessageContext</c>) and one <c>INetMessage</c> parameter, in either order, and returns
    /// <see cref="IntentValidation"/>. It runs on the host before the handler and before any
    /// mutation. A denial runs the <see cref="IntentRejectedAttribute"/> hook and rolls back the
    /// intent's action id when present.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class IntentValidatorAttribute : Attribute
    {
    }
}
