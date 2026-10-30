using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Convenience accessors for extended information on a replicated worker NPC.
    /// The value is keyed by the worker's stable identity; the generic parameter must be the
    /// concrete <see cref="ExtendGameObjectInformation{TOwner}"/> subclass and the value must be
    /// that concrete type.
    /// </summary>
    public static class WorkerExtensions
    {
        /// <summary>Returns the extended information of this type attached to the worker, or null
        /// when none is present or co-op is not loaded.</summary>
        public static T GetExtendedInformation<T>(this Worker worker) where T : class
            => CoopApi.GetExtendedInformation<T>(worker);

        /// <summary>Stores or replaces extended information on the worker. The host applies and
        /// broadcasts it; a client forwards the request to the host and applies the host echo.
        /// <typeparamref name="T"/> must be the value's concrete type.</summary>
        public static void SetExtendedInformation<T>(this Worker worker, T information)
            where T : class
        {
            if (worker == null)
            {
                throw new ArgumentNullException(nameof(worker));
            }

            CoopApi.SetExtendedInformation(worker, information);
        }

        /// <summary>Removes one extended-information entry from the worker. Returns true when the
        /// request was accepted by the local binding; a client request is applied on the host echo.</summary>
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
