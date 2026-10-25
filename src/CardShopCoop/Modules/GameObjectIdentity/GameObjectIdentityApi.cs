using System.Collections.Generic;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.World;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>The only public identity surface for feature modules.</summary>
    public static class GameObjectIdentityApi
    {
        public static bool TryGetId(object entity, out GameObjectId id)
            => GameObjectIdentityCache.TryGetId(entity, out id);

        public static bool TryGetBoundId(object entity, out GameObjectId id)
            => GameObjectIdentityCache.TryGetBoundId(entity, out id);

        public static bool TryResolve(GameObjectId id, out object entity)
            => GameObjectIdentityCache.TryResolve(id, out entity);

        /// <summary>Resolves the physical packaging box behind an identity. A Guid box resolves
        /// to its own object; a furniture identity resolves to the packaging box that currently
        /// holds its placed object.</summary>
        public static bool TryResolveBox(GameObjectId id, out InteractablePackagingBox box)
        {
            box = null;
            if (!TryResolve(id, out var entity) || entity == null)
            {
                return false;
            }

            if (entity is InteractablePackagingBox packaging)
            {
                box = packaging;
                return true;
            }

            if (entity is InteractableObject furniture)
            {
                box = furniture.GetPackagingBoxShelf();
                return box != null;
            }

            return false;
        }

        /// <summary>Resolves a box Guid against both box and furniture identities. Furniture and
        /// item/card box Guids are both session-unique, so one Guid selects exactly one box.</summary>
        public static bool TryResolveBox(System.Guid id, out InteractablePackagingBox box)
        {
            box = null;
            return id != System.Guid.Empty
                && (TryResolveBox(GameObjectId.Box(id), out box)
                    || TryResolveBox(GameObjectId.Furniture(id), out box));
        }

        public static bool BindHost(object entity, GameObjectId id)
            => GameObjectIdentityCache.Bind(entity, id, GameObjectOwnership.Host);

        public static bool BindClient(object entity, GameObjectId id)
            => GameObjectIdentityCache.Bind(entity, id, GameObjectOwnership.Client);

        public static bool BindClientProposed(object entity, GameObjectId id)
            => GameObjectIdentityCache.Bind(entity, id, GameObjectOwnership.ClientProposed);

        public static bool Unbind(object entity)
            => GameObjectIdentityCache.Unbind(entity, out _);

        public static IReadOnlyList<GameObjectId> Snapshot()
            => GameObjectIdentityCache.Snapshot();

        public static void Reset()
            => GameObjectIdentityCache.Reset();

        internal static void HostPublishAdded(EntityDescriptor descriptor, bool adopted = false,
            int ownerConnectionId = 0)
            => GameObjectIdentityHostBehaviour.Active?.PublishAdded(descriptor, adopted,
                ownerConnectionId);

        internal static void HostPublishRemoved(EntityRemovedMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (message.Replacement == null || message.Replacement.Id != message.Id)
            {
                ExtendedInformationModule.OnEntityRemoved(message.Id, message.Mode);
            }

            GameObjectIdentityHostBehaviour.Active?.PublishRemoved(message);
        }

        internal static void HostPublishRemoved(GameObjectId id, EntityRemovalMode mode)
            => HostPublishRemoved(new EntityRemovedMessage { Id = id, Mode = mode });

        internal static void SendJoinBaseline(PeerConnection connection)
            => GameObjectIdentityHostBehaviour.Active?.SendBaseline(connection);

        internal static void ClientApplyAdded(EntityAddedMessage message)
        {
            var descriptor = message?.Descriptor;
            if (descriptor == null || !descriptor.Id.IsValid)
            {
                return;
            }

            if (TryResolve(descriptor.Id, out var existing))
            {
                GameObjectIdentityClientBehaviour.Active?.CancelPending(descriptor.Id);
                BindClient(existing, descriptor.Id);
                ApplyDescriptorToExisting(existing, descriptor);
                return;
            }

            if (!GameObjectIdentityAdapters.TryCreate(descriptor, out var entity) || entity == null)
            {
                GameObjectIdentityClientBehaviour.Active?.QueuePending(descriptor);
                return;
            }

            BindClient(entity, descriptor.Id);
            GameObjectIdentityClientBehaviour.Active?.CancelPending(descriptor.Id);
        }

        internal static void ClientApplyRemoved(EntityRemovedMessage message)
        {
            if (message == null || !message.Id.IsValid)
            {
                return;
            }

            GameObjectIdentityClientBehaviour.Active?.CancelPending(message.Id);

            if (message.Replacement == null || message.Replacement.Id != message.Id)
            {
                ExtendedInformationModule.OnEntityRemoved(message.Id, message.Mode);
            }

            if (TryResolve(message.Id, out var entity))
            {
                GameObjectIdentityAdapters.TryDestroy(entity);
                Unbind(entity);
            }

            if (message.Replacement != null && message.Replacement.Id.IsValid)
            {
                ClientApplyAdded(new EntityAddedMessage
                {
                    Descriptor = message.Replacement,
                    Adopted = true,
                });
            }
        }

        /// <summary>A creation descriptor also carries authoritative state for an entity this
        /// peer already has (its own predicted spawn, or a save-loaded object). Apply it instead
        /// of only re-binding, or the peer keeps its local spawn pose/contents.</summary>
        private static void ApplyDescriptorToExisting(object entity, EntityDescriptor descriptor)
        {
            if (entity is InteractableObject obj && descriptor is PlacementEntityDescriptor placement)
            {
                PlacementIdentityAdapter.ApplyClientDescriptor(obj, placement);
            }
            else if (entity is InteractablePackagingBox box
                && descriptor is PackagingBoxEntityDescriptor boxDescriptor)
            {
                BoxIdentityLifecycle.ApplyDescriptorToBoxForCreate(box, boxDescriptor);
            }
        }

        internal static void ClientApplyBaseline(EntityBaselineMessage message)
        {
            if (message?.Entries == null)
            {
                return;
            }

            GameObjectIdentityClientBehaviour.Active?.ClearPending();
            WorldClientBehaviour.SetHostWarehouseBackend(message.HostUsesRecordWarehouse);
            BoxIdentityLifecycle.BeginClientBaseline();

            for (var i = 0; i < message.Entries.Count; i++)
            {
                var descriptor = message.Entries[i];
                if (descriptor == null || !descriptor.Id.IsValid)
                {
                    continue;
                }

                if (TryResolve(descriptor.Id, out var entity))
                {
                    ApplyDescriptorToExisting(entity, descriptor);
                    continue;
                }

                if (!GameObjectIdentityAdapters.TryCreate(descriptor, out entity)
                    || entity == null)
                {
                    GameObjectIdentityClientBehaviour.Active?.QueuePending(descriptor);
                    continue;
                }

                BindClient(entity, descriptor.Id);
                GameObjectIdentityClientBehaviour.Active?.CancelPending(descriptor.Id);
            }

            BoxIdentityLifecycle.CompleteClientBaseline();
        }
    }
}
