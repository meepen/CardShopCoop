using System;
using System.Reflection;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.ExtendedInformation
{
    internal static class ExtendedInformationCodec
    {
        /// <summary>The identity of a type in the extended-information catalog. The wire carries
        /// the session catalog index; this key is what both peers sort to derive the order, and
        /// what diagnostics name.</summary>
        internal static string KeyFor(Type type)
            => type.FullName + ", " + type.Assembly.GetName().Name;

        internal static bool IsExtendedInformation(Type type, out Type ownerType)
        {
            ownerType = null;
            for (var current = type; current != null; current = current.BaseType)
            {
                if (!current.IsGenericType
                    || current.GetGenericTypeDefinition() != typeof(ExtendGameObjectInformation<>))
                {
                    continue;
                }

                ownerType = current.GetGenericArguments()[0];
                return true;
            }

            return false;
        }

        internal static bool IsGlobalInformation(Type type)
            => type != null
                && !IsExtendedInformation(type, out _)
                && type.GetCustomAttribute<GlobalInformationAttribute>(false) != null;

        /// <summary>True when only the host may write this information type. A client intent for
        /// it is dropped by the host; the module must expose its own validated intent lane.</summary>
        internal static bool IsHostOnly(Type type)
            => type != null
                && type.GetCustomAttribute<HostOnlyInformationAttribute>(false) != null;

        /// <summary>
        /// The identity adapters cover NPCs, placement objects, decorations, and packaging boxes.
        /// Extended information is replicated by GameObjectId, so an owner outside those kinds
        /// cannot be keyed on the wire and must be rejected as a programming error.
        /// </summary>
        internal static bool SupportsOwnerType(Type ownerType)
        {
            if (ownerType == null || ownerType.IsValueType)
            {
                return false;
            }

            return typeof(Customer).IsAssignableFrom(ownerType)
                || typeof(Worker).IsAssignableFrom(ownerType)
                || typeof(InteractablePackagingBox).IsAssignableFrom(ownerType)
                || typeof(InteractableObject).IsAssignableFrom(ownerType);
        }

    }
}
