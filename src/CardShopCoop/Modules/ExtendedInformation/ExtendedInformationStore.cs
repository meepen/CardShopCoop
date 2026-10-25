using System;
using System.Collections.Generic;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>Main-thread store keyed by the identity module's canonical object id.</summary>
    internal static class ExtendedInformationStore
    {
        private static readonly Dictionary<GameObjectId, Dictionary<Type, object>> ById = new();

        internal static bool TryGet(GameObjectId id, Type type, out object information)
        {
            MainThreadGuard.RequireMainThread("extended information lookup");
            information = null;
            return id.IsValid && ById.TryGetValue(id, out var entries)
                && entries.TryGetValue(type, out information);
        }

        internal static void Set(GameObjectId id, Type type, object information)
        {
            MainThreadGuard.RequireMainThread("extended information set");
            if (!id.IsValid || type == null || information == null)
            {
                return;
            }

            if (!ById.TryGetValue(id, out var entries))
            {
                entries = new Dictionary<Type, object>();
                ById.Add(id, entries);
            }

            entries[type] = information;
        }

        internal static bool Remove(GameObjectId id, Type type)
        {
            MainThreadGuard.RequireMainThread("extended information remove");
            if (!id.IsValid || !ById.TryGetValue(id, out var entries)
                || !entries.Remove(type))
            {
                return false;
            }

            if (entries.Count == 0)
            {
                ById.Remove(id);
            }

            return true;
        }

        internal static void RemoveAll(GameObjectId id)
        {
            MainThreadGuard.RequireMainThread("extended information remove all");
            ById.Remove(id);
        }

        /// <summary>Copies one object's entries so a bind/removal notification can invoke user
        /// handlers without holding the store open to re-entrant writes.</summary>
        internal static List<KeyValuePair<Type, object>> Entries(GameObjectId id)
        {
            MainThreadGuard.RequireMainThread("extended information entry snapshot");
            var result = new List<KeyValuePair<Type, object>>();
            if (id.IsValid && ById.TryGetValue(id, out var entries))
            {
                result.AddRange(entries);
            }

            return result;
        }

        /// <summary>Clears object-attached entries but keeps session-wide globals (a scene
        /// rebuild replaces object identities, not the session).</summary>
        internal static void ResetEntities()
        {
            MainThreadGuard.RequireMainThread("extended information entity reset");
            var remove = new List<GameObjectId>();
            foreach (var pair in ById)
            {
                if (pair.Key.Kind != GameObjectKind.Global)
                {
                    remove.Add(pair.Key);
                }
            }

            for (var i = 0; i < remove.Count; i++)
            {
                ById.Remove(remove[i]);
            }
        }

        internal static void ResetAll()
        {
            MainThreadGuard.RequireMainThread("extended information reset all");
            ById.Clear();
        }

        internal static List<ExtendedInformationEntry> Snapshot(Func<Type, ushort?> typeId)
        {
            MainThreadGuard.RequireMainThread("extended information snapshot");
            var result = new List<ExtendedInformationEntry>();
            foreach (var idPair in ById)
            {
                foreach (var entry in idPair.Value)
                {
                    var catalogId = typeId(entry.Key);
                    if (!catalogId.HasValue)
                    {
                        continue;
                    }

                    result.Add(new ExtendedInformationEntry
                    {
                        Id = idPair.Key,
                        Value = new ExtendedInformationValue
                        {
                            TypeId = catalogId.Value,
                            Information = entry.Value,
                        },
                    });
                }
            }

            return result;
        }
    }
}
