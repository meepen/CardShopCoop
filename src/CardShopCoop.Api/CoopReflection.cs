using System;
using System.Reflection;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Reflection helper for integrating with other mods whose assemblies are optional. Required
    /// members fail loudly; optional members log once and return null so a feature disables itself
    /// instead of taking the whole mod down.
    /// </summary>
    public static class CoopReflection
    {
        /// <summary>Resolves a type from an optional assembly. Returns null and logs once when the
        /// type cannot be found.</summary>
        public static Type OptionalType(string typeName, string assemblyName)
        {
            var type = ResolveType(typeName, assemblyName);
            if (type == null)
            {
                Report("type", typeName + ", " + assemblyName, false);
            }

            return type;
        }

        /// <summary>Resolves a field by name. Returns null and logs once when the type or field is
        /// missing.</summary>
        public static FieldInfo OptionalField(Type type, string name)
        {
            var field = type?.GetField(name, Flags);
            if (field == null)
            {
                Report("field", Describe(type, name), false);
            }

            return field;
        }

        /// <summary>Resolves a method by name and optional argument types. Returns null and logs
        /// once when the type or method is missing.</summary>
        public static MethodInfo OptionalMethod(Type type, string name, params Type[] args)
        {
            var method = ResolveMethod(type, name, args);
            if (method == null)
            {
                Report("method", Describe(type, name), false);
            }

            return method;
        }

        /// <summary>Resolves a property by name. Returns null and logs once when the type or
        /// property is missing.</summary>
        public static PropertyInfo OptionalProperty(Type type, string name)
        {
            var property = type?.GetProperty(name, Flags);
            if (property == null)
            {
                Report("property", Describe(type, name), false);
            }

            return property;
        }

        /// <summary>Resolves a field by name. Throws <see cref="MissingMemberException"/> when the
        /// type or field is missing.</summary>
        public static FieldInfo RequiredField(Type type, string name)
        {
            var field = type?.GetField(name, Flags);
            if (field == null)
            {
                Report("field", Describe(type, name), true);
            }

            return field;
        }

        /// <summary>Resolves a method by name and optional argument types. Throws
        /// <see cref="MissingMemberException"/> when the type or method is missing.</summary>
        public static MethodInfo RequiredMethod(Type type, string name, params Type[] args)
        {
            var method = ResolveMethod(type, name, args);
            if (method == null)
            {
                Report("method", Describe(type, name), true);
            }

            return method;
        }

        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type ResolveType(string typeName, string assemblyName)
        {
            try
            {
                var direct = Type.GetType(typeName + ", " + assemblyName, false);
                if (direct != null)
                {
                    return direct;
                }
            }
            catch (Exception e)
            {
                CoopApi.Log?.LogWarning("[api] type probe failed for " + typeName + ": " + e.Message);
            }

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var candidate = assembly.GetType(typeName, false);
                    if (candidate != null)
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception e)
            {
                CoopApi.Log?.LogWarning("[api] assembly scan failed for " + typeName + ": " + e.Message);
            }

            return null;
        }

        private static MethodInfo ResolveMethod(Type type, string name, Type[] args)
        {
            if (type == null)
            {
                return null;
            }

            if (args == null || args.Length == 0)
            {
                MethodInfo first = null;
                foreach (var candidate in type.GetMethods(Flags))
                {
                    if (candidate.Name != name)
                    {
                        continue;
                    }

                    if (candidate.GetParameters().Length == 0)
                    {
                        return candidate;
                    }

                    first ??= candidate;
                }

                return first;
            }

            return type.GetMethod(name, Flags, null, args, null);
        }

        private static string Describe(Type type, string name)
            => (type == null ? "<null>" : type.FullName) + "." + name;

        private static void Report(string kind, string name, bool required)
        {
            var key = (required ? "required:" : "optional:") + kind + ":" + name;
            if (!CoopLog.First(key))
            {
                return;
            }

            var message = "coop api: reflection surface changed: " + kind + " " + name
                + (required ? " (required)" : " (optional; feature disabled)");
            if (required)
            {
                CoopApi.Log?.LogError(message);
                throw new MissingMemberException(message);
            }

            CoopApi.Log?.LogWarning(message);
        }
    }
}
