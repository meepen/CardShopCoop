using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks a method that observes extended-information changes for one game object type. The
    /// handler has two parameters: one entity parameter and one information parameter, in either
    /// order. The information parameter may be the raw value or
    /// <see cref="InformationChange{T}"/>; both types are inferred. The raw value is the current
    /// value (null on removal); the change payload carries previous/current/removed and
    /// first-value/baseline information.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ExtendedInformationHandlerAttribute : Attribute
    {
    }
}
