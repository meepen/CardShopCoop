using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Extended-information accessors for the NPC owner types. The value is keyed by the NPC's
    /// stable identity; the generic parameter must be the concrete
    /// <see cref="ExtendGameObjectInformation{TOwner}"/> subclass.
    /// </summary>
    public static class NpcInformationExtensions
    {
        /// <summary>Returns the extended information of this type, or null when none is present or
        /// co-op is not loaded.</summary>
        public static T GetExtendedInformation<T>(this Customer customer) where T : class
            => CoopApi.GetExtendedInformation<T>(customer);

        public static void SetExtendedInformation<T>(this Customer customer, T information)
            where T : class
        {
            if (customer == null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            CoopApi.SetExtendedInformation(customer, information);
        }

        public static bool RemoveExtendedInformation<T>(this Customer customer) where T : class
        {
            if (customer == null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            return CoopApi.RemoveExtendedInformation<T>(customer);
        }

        /// <summary>Returns the extended information of this type, or null when none is present or
        /// co-op is not loaded.</summary>
        public static T GetExtendedInformation<T>(this Worker worker) where T : class
            => CoopApi.GetExtendedInformation<T>(worker);

        public static void SetExtendedInformation<T>(this Worker worker, T information)
            where T : class
        {
            if (worker == null)
            {
                throw new ArgumentNullException(nameof(worker));
            }

            CoopApi.SetExtendedInformation(worker, information);
        }

        public static bool RemoveExtendedInformation<T>(this Worker worker) where T : class
        {
            if (worker == null)
            {
                throw new ArgumentNullException(nameof(worker));
            }

            return CoopApi.RemoveExtendedInformation<T>(worker);
        }
    }
}
