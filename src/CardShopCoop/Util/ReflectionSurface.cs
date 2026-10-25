using System;
using System.Reflection;
using CardShopCoop.Api;

namespace CardShopCoop.Util
{
    /// <summary>Single resolution point for reflected game members. Required members fail during
    /// type initialization; optional members only produce a one-shot, greppable diagnostic.</summary>
    internal static class ReflectionSurface
    {
        internal static Type OptionalType(string name, string assembly)
        {
            var result = ModParity.ResolveType(name, assembly);
            if (result == null)
            {
                Missing("type", null, name + ", " + assembly, false);
            }

            return result;
        }

        private static void Missing(string kind, Type type, string name, bool required)
        {
            var key = (required ? "required" : "optional") + ":" + kind + ":" +
                (type == null ? "<null>" : type.FullName) + "." + name;
            if (!CoopLog.First(key))
            {
                return;
            }

            var message = "coop: reflection surface changed: " + kind + " " +
                (type == null ? "<null>" : type.FullName) + "." + name +
                (required ? " (required)" : " (optional; feature disabled)");
            if (required)
            {
                CoopPlugin.Log?.LogError(message);

                throw new MissingMemberException(message);
            }
            CoopPlugin.Log?.LogWarning(message);
        }

        internal static FieldInfo RequiredField(Type type, string name)
        {
            var result = type?.GetField(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null)
            {
                Missing("field", type, name, true);
            }

            return result;
        }

        internal static MethodInfo RequiredMethod(Type type, string name, params Type[] args)
        {
            var result = ResolveMethod(type, name, args);
            if (result == null)
            {
                Missing("method", type, name, true);
            }

            return result;
        }

        internal static PropertyInfo OptionalProperty(Type type, string name)
        {
            var result = type?.GetProperty(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null)
            {
                Missing("property", type, name, false);
            }

            return result;
        }

        internal static FieldInfo OptionalField(Type type, string name)
        {
            var result = type?.GetField(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null)
            {
                Missing("field", type, name, false);
            }

            return result;
        }

        internal static MethodInfo OptionalMethod(Type type, string name, params Type[] args)
        {
            var result = ResolveMethod(type, name, args);
            if (result == null)
            {
                Missing("method", type, name, false);
            }

            return result;
        }

        /// <summary>True when the type's definition can actually load in this process. Mono resolves
        /// a type's FIELD signatures while loading it, so a type whose field names a type from a
        /// missing OR stub assembly throws <see cref="TypeLoadException"/>. The Xbox / Game Pass
        /// build ships a 4 KB stub literally named <c>com.rlabrecque.steamworks.net</c>, which is
        /// why loading <c>SteamLobby</c> (fields are <c>Steamworks.CallResult&lt;T&gt;</c>) faults
        /// there - and that type now lives in the Steam transport assembly, which IS scanned as an
        /// integrating mod. There is no way to know before touching the type (the CLR resolves
        /// fields as it loads them), so the probe IS the detection: probe defensively, skip.</summary>
        internal static bool CanLoad(Type type)
        {
            if (type == null)
            {
                return false;
            }

            try
            {
                typeof(object).IsAssignableFrom(type);
                return true;
            }
            catch (Exception error)
            {
                Skip(type, error);
                return false;
            }
        }

        /// <summary>IsAssignableFrom that treats an unloadable candidate as "no" instead of letting
        /// the load fault escape into a startup scan.</summary>
        internal static bool IsAssignableTo(Type candidate, Type baseType)
        {
            if (candidate == null || baseType == null)
            {
                return false;
            }

            try
            {
                return baseType.IsAssignableFrom(candidate);
            }
            catch (Exception error)
            {
                Skip(candidate, error);
                return false;
            }
        }

        /// <summary>Custom attributes of a type, or an empty array when the type cannot load.</summary>
        internal static object[] SafeAttributes(Type type)
        {
            if (type == null)
            {
                return Array.Empty<object>();
            }

            try
            {
                return type.GetCustomAttributes(false);
            }
            catch (Exception error)
            {
                Skip(type, error);
                return Array.Empty<object>();
            }
        }

        /// <summary>One-shot diagnostic for a type an assembly-wide scan had to skip. The fail-loud
        /// policy still holds - a skipped type is always named once - but an unloadable type is not
        /// a bug in the scan, so it is a warning rather than a throw.</summary>
        private static void Skip(Type type, Exception error)
        {
            string name;
            try
            {
                name = type.FullName ?? type.Name;
            }
            catch
            {
                name = "<unknown>";
            }

            if (!CoopLog.First("type-load:" + name))
            {
                return;
            }

            CoopPlugin.Log?.LogWarning("coop: skipped a type that cannot load on this build ("
                    + name + "): " + error.Message);
        }

        private static MethodInfo ResolveMethod(Type type, string name, Type[] args)
        {
            if (type == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;

            // The patch helpers historically used AccessTools.Method(type, name),
            // which resolves by name when no signature is supplied. Preserve that
            // behavior for parameterized game methods such as AddItem and
            // SetCompactCardDataAmountList. Supplying one or more argument types
            // remains an exact-signature lookup.
            if (args == null || args.Length == 0)
            {
                MethodInfo first = null;
                MethodInfo parameterless = null;
                foreach (var candidate in type.GetMethods(flags))
                {
                    if (candidate.Name != name)
                    {
                        continue;
                    }

                    if (first == null)
                    {
                        first = candidate;
                    }

                    if (candidate.GetParameters().Length == 0)
                    {
                        parameterless = candidate;
                        break;
                    }
                }
                return parameterless ?? first;
            }

            return type.GetMethod(name, flags, null, args, null);
        }
    }
}


