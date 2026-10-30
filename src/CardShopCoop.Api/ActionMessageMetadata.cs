using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace CardShopCoop.Api
{
    /// <summary>Reflection cache for the optional ActionMessage hooks. The hooks are virtual
    /// no-ops; the framework only needs to know whether a type opted into one.</summary>
    internal static class ActionMessageMetadata
    {
        private static readonly ConcurrentDictionary<Type, bool> ApplyOverrides = new();
        private static readonly ConcurrentDictionary<Type, bool> UndoOverrides = new();

        internal static bool OverridesOnApply(Type type)
        {
            if (type == null)
            {
                return false;
            }

            return ApplyOverrides.GetOrAdd(type, static candidate
                => Overrides(candidate, nameof(ActionMessage.OnApply)));
        }

        internal static bool OverridesOnUndo(Type type)
        {
            if (type == null)
            {
                return false;
            }

            return UndoOverrides.GetOrAdd(type, static candidate
                => Overrides(candidate, nameof(ActionMessage.OnUndo)));
        }

        private static bool Overrides(Type type, string method)
        {
            var info = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
            return info != null && info.DeclaringType != typeof(ActionMessage);
        }
    }
}
