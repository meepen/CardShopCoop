using System;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Bridges members whose names differ between the two game builds this one DLL must run on.
    ///
    /// Unity 2021.3.38f1 (public/default) exposes only <c>Rigidbody.velocity</c> and
    /// <c>TMP_Text.enableWordWrapping</c>. Unity 6000.0.66f2 (the 1.00 beta) deprecated both in
    /// favour of <c>Rigidbody.linearVelocity</c> and <c>TMP_Text.textWrappingMode</c>, and those
    /// replacements do not exist on 2021.3. Each member is resolved once by reflection to whichever
    /// form the running build has, so neither build sees a compile-time reference to a deprecated
    /// member and neither build can hit a MissingMethodException.
    /// </summary>
    internal static class UnityCompat
    {
        private static readonly Func<Rigidbody, Vector3> _velocityGetter;
        private static readonly Action<Rigidbody, Vector3> _velocitySetter;

        // Exactly one of these is populated: textWrappingMode on Unity 6, enableWordWrapping on 2021.3.
        private static readonly PropertyInfo _textWrapping;
        private static readonly object _wrapEnabled;
        private static readonly object _wrapDisabled;
        private static readonly PropertyInfo _legacyWordWrapping;

        static UnityCompat()
        {
            var velocity = typeof(Rigidbody).GetProperty("linearVelocity")
                           ?? typeof(Rigidbody).GetProperty("velocity");
            if (velocity == null)
                throw new MissingMemberException(typeof(Rigidbody).FullName, "linearVelocity/velocity");
            _velocityGetter = (Func<Rigidbody, Vector3>)velocity.GetGetMethod()
                .CreateDelegate(typeof(Func<Rigidbody, Vector3>));
            _velocitySetter = (Action<Rigidbody, Vector3>)velocity.GetSetMethod()
                .CreateDelegate(typeof(Action<Rigidbody, Vector3>));

            _textWrapping = typeof(TMP_Text).GetProperty("textWrappingMode");
            if (_textWrapping != null)
            {
                _wrapEnabled = Enum.Parse(_textWrapping.PropertyType, "Normal");
                _wrapDisabled = Enum.Parse(_textWrapping.PropertyType, "NoWrap");
            }
            else
            {
                _legacyWordWrapping = typeof(TMP_Text).GetProperty("enableWordWrapping");
                if (_legacyWordWrapping == null)
                    throw new MissingMemberException(typeof(TMP_Text).FullName, "textWrappingMode/enableWordWrapping");
            }
        }

        /// <summary>Linear velocity on Unity 6, the legacy velocity alias on 2021.3.</summary>
        internal static Vector3 Velocity(Rigidbody body)
        {
            return _velocityGetter(body);
        }

        /// <summary>Sets linear velocity on Unity 6, the legacy velocity alias on 2021.3.</summary>
        internal static void SetVelocity(Rigidbody body, Vector3 velocity)
        {
            _velocitySetter(body, velocity);
        }

        /// <summary>Enable/disable word wrapping through whichever TMP property the build has.</summary>
        internal static void SetWordWrapping(TMP_Text text, bool enabled)
        {
            if (_textWrapping != null)
                _textWrapping.SetValue(text, enabled ? _wrapEnabled : _wrapDisabled);
            else
                _legacyWordWrapping.SetValue(text, enabled);
        }
    }
}
