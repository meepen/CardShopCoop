using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// Translates the HOST's numeric enum ids inside a transferred native save into this client's
    /// runtime ids, using the same handshake-built <see cref="CatalogIdMap"/> map the wire uses.
    ///
    /// The host is authoritative and ships its save file untouched, so every gated enum id in it is
    /// a host id. The client loads that save natively, then this pass rewrites the gated enum fields
    /// (and lists of them) in place BEFORE the game's PropagateLoadData copies them into CPlayerData.
    /// Only gated enum-typed fields are touched; no registry file is read or written.
    /// </summary>
    internal static class SaveEnumRemap
    {
        private static int _armed;

        /// <summary>Arm a one-shot remap for the next native save load. Called on the client
        /// immediately before it triggers the load of the borrowed host world.</summary>
        public static void Arm() => Interlocked.Exchange(ref _armed, 1);

        public static void Disarm() => Interlocked.Exchange(ref _armed, 0);

        /// <summary>Consume the one-shot arm. Kept separate from <see cref="Apply"/> so the load
        /// postfix can check-and-clear before doing any reflection work.</summary>
        public static bool TryConsume() => Interlocked.Exchange(ref _armed, 0) == 1;

        private const int MaxDepth = 8;

        /// <summary>Rewrite every gated enum value in <paramref name="root"/> from host space to
        /// this process's space. Safe to call when no map exists (the identity translation).</summary>
        public static void Apply(object root)
        {
            if (root == null)
            {
                return;
            }

            var stats = new RemapStats();
            Walk(root, new HashSet<object>(ReferenceComparer.Instance), 0, stats);
            CoopPlugin.Log.LogInfo("save enum remap: changed " + stats.Changed
                + " gated value(s)." + stats.Describe());
        }

        private sealed class RemapStats
        {
            public int Changed;
            private readonly List<string> _samples = new();

            public void Record(string label, int hostValue, int localValue)
            {
                Changed++;
                if (_samples.Count < 12)
                {
                    _samples.Add(label + " " + hostValue + "->" + localValue);
                }
            }

            public string Describe()
                => _samples.Count == 0 ? "" : " " + string.Join("; ", _samples);
        }

        private static void Walk(object node, HashSet<object> seen, int depth, RemapStats stats)
        {
            if (node == null || depth > MaxDepth)
            {
                return;
            }

            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string)
                || type.IsArray || !IsGameSaveType(type) || !seen.Add(node))
            {
                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object value;
                try
                {
                    value = field.GetValue(node);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value == null)
                {
                    continue;
                }

                if (TryKindForEnum(field.FieldType, out var fieldKind))
                {
                    SetEnumField(field, node, field.FieldType, fieldKind, value, stats);
                    continue;
                }

                if (!field.FieldType.IsGenericType
                    || field.FieldType.GetGenericTypeDefinition() != typeof(List<>))
                {
                    if (IsGameSaveType(field.FieldType))
                    {
                        Walk(value, seen, depth + 1, stats);
                    }
                    continue;
                }

                var elementType = field.FieldType.GetGenericArguments()[0];
                if (value is not IList list || list.Count == 0)
                {
                    continue;
                }

                if (TryKindForEnum(elementType, out var elementKind))
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var element = list[i];
                        if (element == null)
                        {
                            continue;
                        }

                        var hostValue = SafeToInt32(element);
                        var localValue = CatalogIdMap.FromHostValue(elementKind, hostValue);
                        if (hostValue != localValue)
                        {
                            list[i] = Enum.ToObject(elementType, localValue);
                            stats.Record(field.DeclaringType.Name + "." + field.Name + "[" + i + "]",
                                hostValue, localValue);
                        }
                    }
                }
                else if (IsGameSaveType(elementType))
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        Walk(list[i], seen, depth + 1, stats);
                    }
                }
            }
        }

        private static void SetEnumField(FieldInfo field, object node, Type enumType, EnumKind kind,
            object value, RemapStats stats)
        {
            try
            {
                var hostValue = SafeToInt32(value);
                var localValue = CatalogIdMap.FromHostValue(kind, hostValue);
                if (hostValue == localValue)
                {
                    return;
                }

                field.SetValue(node, Enum.ToObject(enumType, localValue));
                stats.Record(field.DeclaringType.Name + "." + field.Name, hostValue, localValue);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("save enum remap: " + enumType.Name + "." + field.Name
                    + ": " + error.Message);
            }
        }

        private static int SafeToInt32(object value)
        {
            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static bool TryKindForEnum(Type type, out EnumKind kind)
        {
            for (var i = 0; i <= (int)EnumKind.CollectionPack; i++)
            {
                var candidate = (EnumKind)i;
                try
                {
                    if (CatalogIdMap.WireType(candidate) == type)
                    {
                        kind = candidate;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // An unresolvable kind simply cannot match this type.
                }
            }

            kind = EnumKind.ItemType;
            return false;
        }

        /// <summary>Only recurse into the game's own save-model classes (everything in
        /// Assembly-CSharp). The game's types live in the GLOBAL namespace, so namespace cannot be
        /// used as the filter; the assembly identity is what separates them from UnityEngine,
        /// System, and mod types.</summary>
        private static bool IsGameSaveType(Type type)
        {
            return type != null && type.IsClass && !type.IsAbstract
                && type.Assembly == typeof(CGameData).Assembly;
        }

        /// <summary>Reference identity comparer - HashSet&lt;object&gt; would otherwise use
        /// Equals, which some save-model classes may override.</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
