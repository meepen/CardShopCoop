using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a class as a session-wide value. The class is the value; keep it immutable (get-only
    /// or init properties) and change it through <see cref="Global{T}"/>. Reactions belong in a
    /// <see cref="GlobalInformationHandlerAttribute"/> method, not in the value itself.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class GlobalInformationAttribute : Attribute
    {
    }
}
