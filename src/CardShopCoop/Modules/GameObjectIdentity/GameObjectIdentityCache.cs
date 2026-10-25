using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    internal enum GameObjectOwnership : byte
    {
        Host = 1,
        Client = 2,
        ClientProposed = 3,
    }

    /// <summary>
    /// The single object/identity cache. ByObject and ById are two indexes of the same mapping;
    /// no feature module keeps its own identity map.
    /// </summary>
    internal static class GameObjectIdentityCache
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();

            public new bool Equals(object left, object right) => ReferenceEquals(left, right);

            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly Dictionary<object, GameObjectId> ByObject =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<GameObjectId, object> ById = new();
        private static readonly Dictionary<GameObjectId, GameObjectOwnership> OwnershipById = new();

        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            MainThreadGuard.RequireMainThread("game object identity lookup");
            if (TryGetBoundId(entity, out id))
            {
                return true;
            }

            if (entity != null && GameObjectIdentityAdapters.TryGetId(entity, out id))
            {
                // A derived id can already name a different live entity. A furniture packaging
                // box is a view of the placed furniture: both carry the furniture's id, but the
                // furniture stays canonical in ById so placement resolution still returns it.
                if (ById.TryGetValue(id, out var canonical) && !ReferenceEquals(canonical, entity)
                    && IsAlive(canonical))
                {
                    ByObject[entity] = id;
                    return true;
                }

                Bind(entity, id, CurrentOwnership());
                return true;
            }

            id = default;
            return false;
        }

        internal static bool TryGetBoundId(object entity, out GameObjectId id)
        {
            MainThreadGuard.RequireMainThread("game object identity lookup");
            id = default;
            if (entity == null || !ByObject.TryGetValue(entity, out id) || !id.IsValid)
            {
                return false;
            }

            if (IsAlive(entity))
            {
                return true;
            }

            ByObject.Remove(entity);
            var canonical = ById.TryGetValue(id, out var current)
                && ReferenceEquals(current, entity);
            if (canonical)
            {
                ById.Remove(id);
                OwnershipById.Remove(id);
                // A packaging-box view shares the furniture's canonical id. Retiring the view
                // must not clear the still-live furniture's extended information.
                ExtendedInformationModule.OnEntityUnbound(id);
            }

            id = default;
            return false;
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            MainThreadGuard.RequireMainThread("game object identity resolve");
            if (TryResolveBound(id, out entity))
            {
                return true;
            }

            if (!id.IsValid || !GameObjectIdentityAdapters.TryResolve(id, out entity)
                || !IsAlive(entity))
            {
                entity = null;
                return false;
            }

            Bind(entity, id, CurrentOwnership());
            return true;
        }

        internal static bool TryResolveBound(GameObjectId id, out object entity)
        {
            MainThreadGuard.RequireMainThread("game object identity resolve");
            entity = null;
            if (!id.IsValid || !ById.TryGetValue(id, out entity))
            {
                return false;
            }

            if (IsAlive(entity))
            {
                return true;
            }

            ById.Remove(id);
            OwnershipById.Remove(id);
            if (entity != null)
            {
                ByObject.Remove(entity);
            }

            ExtendedInformationModule.OnEntityUnbound(id);
            entity = null;
            return false;
        }

        internal static bool Bind(object entity, GameObjectId id, GameObjectOwnership ownership)
        {
            MainThreadGuard.RequireMainThread("game object identity bind");
            if (entity == null || !id.IsValid)
            {
                return false;
            }

            if (ByObject.TryGetValue(entity, out var previousId))
            {
                if (previousId == id)
                {
                    OwnershipById[id] = ownership;
                    return true;
                }

                ById.Remove(previousId);
                OwnershipById.Remove(previousId);
                ExtendedInformationModule.OnEntityUnbound(previousId);
            }

            if (ById.TryGetValue(id, out var previousEntity)
                && !ReferenceEquals(previousEntity, entity))
            {
                ByObject.Remove(previousEntity);
                if (previousEntity != null)
                {
                    CoopPlugin.Log.LogWarning("[identity] id " + id + " moved from "
                        + previousEntity.GetType().Name + " to " + entity.GetType().Name + ".");
                }
            }

            ByObject[entity] = id;
            ById[id] = entity;
            OwnershipById[id] = ownership;
            ExtendedInformationModule.OnEntityBound(entity, id);
            return true;
        }

        internal static bool Unbind(object entity, out GameObjectId id)
        {
            MainThreadGuard.RequireMainThread("game object identity unbind");
            id = default;
            if (entity == null || !ByObject.TryGetValue(entity, out id))
            {
                return false;
            }

            ByObject.Remove(entity);
            var canonical = ById.TryGetValue(id, out var current)
                && ReferenceEquals(current, entity);
            if (canonical)
            {
                ById.Remove(id);
                OwnershipById.Remove(id);
                ExtendedInformationModule.OnEntityUnbound(id);
            }

            return true;
        }

        internal static GameObjectOwnership OwnershipOf(GameObjectId id)
            => OwnershipById.TryGetValue(id, out var ownership) ? ownership : GameObjectOwnership.Host;

        internal static IReadOnlyList<GameObjectId> Snapshot()
        {
            var result = new List<GameObjectId>(ById.Count);
            foreach (var pair in ById)
            {
                if (IsAlive(pair.Value))
                {
                    result.Add(pair.Key);
                }
            }

            return result;
        }

        internal static void Reset()
        {
            ByObject.Clear();
            ById.Clear();
            OwnershipById.Clear();
            ExtendedInformationModule.ResetEntities();
        }

        private static GameObjectOwnership CurrentOwnership()
            => CoopCore.Role == CoopRole.Host
                ? GameObjectOwnership.Host
                : CoopCore.Role == CoopRole.Client
                    ? GameObjectOwnership.Client
                    : GameObjectOwnership.Client;

        private static bool IsAlive(object entity)
        {
            if (entity == null)
            {
                return false;
            }

            if (entity is UnityEngine.Object unityObject)
            {
                return unityObject != null;
            }

            return true;
        }
    }
}
