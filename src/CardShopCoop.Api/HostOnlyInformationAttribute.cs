using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Marks an extended-information type that only the host may write. A client
    /// <c>SetExtendedInformation</c> throws and a client <c>ExtendedInformationIntentMessage</c> for
    /// this type is dropped by the host. Use it for a value that is host-authoritative gameplay
    /// state and has its own validated intent lane (the register snapshot is the reference).
    /// A host may still set it normally.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class HostOnlyInformationAttribute : Attribute
    {
    }
}
