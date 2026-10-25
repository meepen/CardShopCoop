using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Convenience accessors for extended information on a replicated interactable object.
    /// The identity module still owns the stable object identity and the host-authoritative store.
    /// The generic parameter must be the concrete
    /// <see cref="ExtendGameObjectInformation{TOwner}"/> subclass; the value must be that concrete
    /// type.
    /// </summary>
    public static class InteractableObjectExtensions
    {
        /// <summary>Returns the extended information of this type attached to the object, or null
        /// when none is present or co-op is not loaded.</summary>
        public static T GetExtendedInformation<T>(this InteractableObject obj) where T : class
            => CoopApi.GetExtendedInformation<T>(obj);

        /// <summary>Stores or replaces extended information on the object. The host applies and
        /// broadcasts it; a client forwards the request to the host and applies the host echo.
        /// <typeparamref name="T"/> must be the value's concrete type.</summary>
        public static void SetExtendedInformation<T>(this InteractableObject obj, T information)
            where T : class
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            CoopApi.SetExtendedInformation(obj, information);
        }

        /// <summary>Removes one extended-information entry from the object. Returns true when the
        /// request was accepted by the local binding; a client request is applied on the host echo.</summary>
        public static bool RemoveExtendedInformation<T>(this InteractableObject obj) where T : class
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            return CoopApi.RemoveExtendedInformation<T>(obj);
        }
    }
}
