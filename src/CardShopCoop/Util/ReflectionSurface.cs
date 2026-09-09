using System;
using System.Collections.Generic;
using System.Reflection;

namespace CardShopCoop.Util
{
    /// <summary>Single resolution point for reflected game members. Required members fail during
    /// type initialization; optional members only produce a one-shot, greppable diagnostic.</summary>
    internal static class ReflectionSurface
    {
        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static readonly object Gate = new object();

        internal static Type OptionalType(string name, string assembly)
        {
            Type result = ModParity.ResolveType(name, assembly);
            if (result == null) Missing("type", null, name + ", " + assembly, false);
            return result;
        }

        private static void Missing(string kind, Type type, string name, bool required)
        {
            string key = (required ? "required" : "optional") + ":" + kind + ":" +
                (type == null ? "<null>" : type.FullName) + "." + name;
            bool report;
            lock (Gate) report = Reported.Add(key);
            if (!report) return;

            string message = "coop: reflection surface changed: " + kind + " " +
                (type == null ? "<null>" : type.FullName) + "." + name +
                (required ? " (required)" : " (optional; feature disabled)");
            if (required)
            {
                if (CoopPlugin.Log != null) CoopPlugin.Log.LogError(message);
                throw new MissingMemberException(message);
            }
            if (CoopPlugin.Log != null) CoopPlugin.Log.LogWarning(message);
        }

        internal static FieldInfo RequiredField(Type type, string name)
        {
            var result = type == null ? null : type.GetField(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null) Missing("field", type, name, true);
            return result;
        }

        internal static MethodInfo RequiredMethod(Type type, string name, params Type[] args)
        {
            var result = ResolveMethod(type, name, args);
            if (result == null) Missing("method", type, name, true);
            return result;
        }

        internal static PropertyInfo OptionalProperty(Type type, string name)
        {
            var result = type == null ? null : type.GetProperty(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null) Missing("property", type, name, false);
            return result;
        }

        internal static FieldInfo OptionalField(Type type, string name)
        {
            var result = type == null ? null : type.GetField(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null) Missing("field", type, name, false);
            return result;
        }

        internal static MethodInfo OptionalMethod(Type type, string name, params Type[] args)
        {
            var result = ResolveMethod(type, name, args);
            if (result == null) Missing("method", type, name, false);
            return result;
        }

        private static MethodInfo ResolveMethod(Type type, string name, Type[] args)
        {
            if (type == null) return null;

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
                foreach (MethodInfo candidate in type.GetMethods(flags))
                {
                    if (candidate.Name != name) continue;
                    if (first == null) first = candidate;
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
