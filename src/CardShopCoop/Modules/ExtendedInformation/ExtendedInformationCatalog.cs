using System;
using System.Collections.Generic;
using System.Reflection;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>
    /// Session catalog for extended-information types. It mirrors the message catalog: both peers
    /// exchange the ordinally sorted type keys in the handshake and messages carry the zero-based
    /// index of that order. The index is session-local shorthand derived only from the key; the
    /// key remains the identity.
    /// </summary>
    internal static class ExtendedInformationCatalog
    {
        private static readonly HashSet<Type> Registered = new();
        private static readonly HashSet<Type> PendingNextSession = new();
        private static readonly HashSet<string> LoggedLateTypes = new(StringComparer.Ordinal);

        private static List<string> _keys;
        private static Type[] _typesById;
        private static Dictionary<Type, ushort> _idsByType;
        private static bool _frozen;

        /// <summary>The frozen key order for this session. Freezes on first use.</summary>
        internal static IReadOnlyList<string> Keys
        {
            get
            {
                FreezeIfNeeded();
                return _keys;
            }
        }

        /// <summary>
        /// Adds every extended-information type in an assembly. Called for the core assembly and
        /// for every external mod assembly that is integrated, before a session starts.
        /// </summary>
        internal static void RegisterAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                types = error.Types;
            }

            var discovered = new List<Type>();
            for (var i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null || !type.IsClass || type.IsAbstract
                    || type.ContainsGenericParameters)
                {
                    continue;
                }

                if (!ExtendedInformationCodec.IsExtendedInformation(type, out var ownerType))
                {
                    if (ExtendedInformationCodec.IsGlobalInformation(type))
                    {
                        discovered.Add(type);
                    }

                    continue;
                }

                if (!ExtendedInformationCodec.SupportsOwnerType(ownerType))
                {
                    CoopPlugin.Log?.LogWarning("[identity] extended-information type "
                        + type.FullName + " has no supported identity owner ("
                        + ownerType.FullName + ") and is not replicated.");
                    continue;
                }

                discovered.Add(type);
            }

            // Commit only after the whole assembly was inspected, so a failure leaves no partial
            // catalog behind.
            for (var i = 0; i < discovered.Count; i++)
            {
                Register(discovered[i]);
            }
        }

        /// <summary>Registers one information type. A type first seen during a session applies to
        /// the next session; the active catalog never changes.</summary>
        internal static void Register(Type type)
        {
            if (type == null)
            {
                return;
            }

            if (_frozen)
            {
                if (_idsByType.ContainsKey(type))
                {
                    return;
                }

                if (PendingNextSession.Add(type))
                {
                    var key = ExtendedInformationCodec.KeyFor(type);
                    if (LoggedLateTypes.Add(key))
                    {
                        CoopPlugin.Log?.LogWarning("[identity] extended-information type " + key
                            + " was registered during a session and applies from the next session.");
                    }
                }

                return;
            }

            Registered.Add(type);
        }

        /// <summary>Returns the session id for a type, registering it first while the catalog is
        /// still open. Throws when the type is not in the active catalog.</summary>
        internal static ushort IdFor(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (!_frozen)
            {
                Register(type);
            }

            FreezeIfNeeded();
            if (_idsByType.TryGetValue(type, out var id))
            {
                return id;
            }

            throw new InvalidOperationException(
                "Extended-information type " + ExtendedInformationCodec.KeyFor(type)
                + " is not in this session's catalog. Both peers must run the same build of the "
                + "mod that declares it; a type registered mid-session applies from the next session.");
        }

        /// <summary>Looks up an id without changing the catalog. Used for baselines, where an
        /// absent type must be skipped rather than fail the whole baseline.</summary>
        internal static bool TryGetId(Type type, out ushort id)
        {
            FreezeIfNeeded();
            id = 0;
            return type != null && _idsByType != null && _idsByType.TryGetValue(type, out id);
        }

        internal static Type TypeFor(ushort id)
        {
            FreezeIfNeeded();
            return _typesById != null && id < _typesById.Length ? _typesById[id] : null;
        }

        /// <summary>Freezes the catalog for a session. Called once every module's handlers have
        /// registered and before the transport starts, so both peers derive the same order from
        /// assembly discovery plus handler-declared types.</summary>
        internal static void BeginSession() => FreezeIfNeeded();

        /// <summary>Lifts the freeze when a session ends, so types registered while it was active
        /// join the next session's catalog.</summary>
        internal static void EndSession()
        {
            if (PendingNextSession.Count > 0)
            {
                foreach (var type in PendingNextSession)
                {
                    Registered.Add(type);
                }

                PendingNextSession.Clear();
            }

            LoggedLateTypes.Clear();
            _frozen = false;
            _keys = null;
            _typesById = null;
            _idsByType = null;
        }

        private static void FreezeIfNeeded()
        {
            if (_frozen)
            {
                return;
            }

            var entries = new List<KeyValuePair<string, Type>>(Registered.Count);
            foreach (var type in Registered)
            {
                entries.Add(new KeyValuePair<string, Type>(
                    ExtendedInformationCodec.KeyFor(type), type));
            }

            entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            if (entries.Count > ushort.MaxValue + 1)
            {
                throw new InvalidOperationException(
                    "The extended-information catalog exceeds " + (ushort.MaxValue + 1)
                    + " entries.");
            }

            _keys = new List<string>(entries.Count);
            _typesById = new Type[entries.Count];
            _idsByType = new Dictionary<Type, ushort>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0 && string.Equals(entries[i - 1].Key, entries[i].Key,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Duplicate extended-information catalog key " + entries[i].Key);
                }

                var id = checked((ushort)i);
                _keys.Add(entries[i].Key);
                _typesById[i] = entries[i].Value;
                _idsByType.Add(entries[i].Value, id);
            }

            _frozen = true;
        }
    }
}
