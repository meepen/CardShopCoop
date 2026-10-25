using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// The single read/write surface for a session-wide value. <typeparamref name="T"/> must be a
    /// class marked <see cref="GlobalInformationAttribute"/> and should be immutable (get-only or
    /// init properties), so a value read through <see cref="Value"/> cannot be edited invisibly.
    /// Reading goes to the store; the setter publishes through the normal host-authority flow, so a
    /// client's setter is an intent and the value is replaced when the host echo arrives.
    ///
    /// <code>
    /// Global&lt;ShopTaxRate&gt;.Value = new ShopTaxRate { Percent = 5 };
    /// var tax = Global&lt;ShopTaxRate&gt;.Value;
    /// Global&lt;ShopTaxRate&gt;.Mutate(t => new ShopTaxRate { Percent = (t?.Percent ?? 0) + 1 });
    /// Global&lt;ShopTaxRate&gt;.Value = null;
    /// </code>
    /// </summary>
    public static class Global<T> where T : class
    {
        public static T Value
        {
            get => CoopApi.GetGlobalInformation<T>();
            set
            {
                if (value == null)
                {
                    CoopApi.RemoveGlobalInformation<T>();
                    return;
                }

                CoopApi.SetGlobalInformation(value);
            }
        }

        /// <summary>Reads the current value, applies <paramref name="update"/>, and publishes the
        /// result in one write. The return value is the requested value; on a client the store
        /// still shows the old value until the host echo arrives.</summary>
        public static T Mutate(Func<T, T> update)
        {
            if (update == null)
            {
                throw new ArgumentNullException(nameof(update));
            }

            var next = update(Value);
            Value = next;
            return next;
        }
    }
}
