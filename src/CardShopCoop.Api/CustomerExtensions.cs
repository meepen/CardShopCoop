using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Convenience accessors for extended information on a replicated customer NPC.
    /// The value is keyed by the customer's stable identity; the generic parameter must be the
    /// concrete <see cref="ExtendGameObjectInformation{TOwner}"/> subclass and the value must be
    /// that concrete type.
    /// </summary>
    public static class CustomerExtensions
    {
        /// <summary>Returns the extended information of this type attached to the customer, or null
        /// when none is present or co-op is not loaded.</summary>
        public static T GetExtendedInformation<T>(this Customer customer) where T : class
            => CoopApi.GetExtendedInformation<T>(customer);

        /// <summary>Stores or replaces extended information on the customer. The host applies and
        /// broadcasts it; a client forwards the request to the host and applies the host echo.
        /// <typeparamref name="T"/> must be the value's concrete type.</summary>
        public static void SetExtendedInformation<T>(this Customer customer, T information)
            where T : class
        {
            if (customer == null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            CoopApi.SetExtendedInformation(customer, information);
        }

        /// <summary>Removes one extended-information entry from the customer. Returns true when the
        /// request was accepted by the local binding; a client request is applied on the host echo.</summary>
        public static bool RemoveExtendedInformation<T>(this Customer customer) where T : class
        {
            if (customer == null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            return CoopApi.RemoveExtendedInformation<T>(customer);
        }
    }
}
