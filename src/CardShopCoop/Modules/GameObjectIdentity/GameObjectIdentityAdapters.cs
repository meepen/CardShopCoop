namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Thin dispatch to the per-kind identity partials. Every implementation lives inside the
    /// GameObjectIdentity module; feature modules do not register adapters.
    /// </summary>
    internal static class GameObjectIdentityAdapters
    {
        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            if (NpcIdentityAdapter.TryGetId(entity, out id))
            {
                return true;
            }

            if (PlacementIdentityAdapter.TryGetId(entity, out id))
            {
                return true;
            }

            if (BoxIdentityAdapter.TryGetId(entity, out id))
            {
                return true;
            }

            return DecorationIdentityAdapter.TryGetId(entity, out id);
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            if (NpcIdentityAdapter.TryResolve(id, out entity))
            {
                return true;
            }

            if (PlacementIdentityAdapter.TryResolve(id, out entity))
            {
                return true;
            }

            if (BoxIdentityAdapter.TryResolve(id, out entity))
            {
                return true;
            }

            return DecorationIdentityAdapter.TryResolve(id, out entity);
        }

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
        {
            if (NpcIdentityAdapter.TryDescribe(entity, out descriptor))
            {
                return true;
            }

            if (PlacementIdentityAdapter.TryDescribe(entity, out descriptor))
            {
                return true;
            }

            if (BoxIdentityAdapter.TryDescribe(entity, out descriptor))
            {
                return true;
            }

            return DecorationIdentityAdapter.TryDescribe(entity, out descriptor);
        }

        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
        {
            return descriptor switch
            {
                NpcEntityDescriptor => NpcIdentityAdapter.TryCreate(descriptor, out entity),
                PlacementEntityDescriptor => PlacementIdentityAdapter.TryCreate(descriptor, out entity),
                PackagingBoxEntityDescriptor => BoxIdentityAdapter.TryCreate(descriptor, out entity),
                DecorationEntityDescriptor =>
                    DecorationIdentityAdapter.TryCreate(descriptor, out entity),
                _ => Fail(out entity),
            };
        }

        internal static bool TryDestroy(object entity)
        {
            if (NpcIdentityAdapter.Destroy(entity))
            {
                return true;
            }

            if (PlacementIdentityAdapter.Destroy(entity))
            {
                return true;
            }

            if (BoxIdentityAdapter.Destroy(entity))
            {
                return true;
            }

            return DecorationIdentityAdapter.Destroy(entity);
        }

        private static bool Fail(out object entity)
        {
            entity = null;
            return false;
        }
    }
}
