using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a method that observes changes to one global information value. The single parameter
    /// is either the raw value (the current value, null on removal) or
    /// <see cref="InformationChange{T}"/> (previous/current/removed, with a null previous for the
    /// first value and a join baseline). The value type must be a class marked
    /// <see cref="GlobalInformationAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class GlobalInformationHandlerAttribute : Attribute
    {
    }
}
