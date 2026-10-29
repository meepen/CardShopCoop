using System;
using System.Collections.Generic;
using CardShopCoop.Modules.ExtendedInformation;
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
        public static bool TryResolveBox(Guid id, out InteractablePackagingBox box)
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

        /// <summary>Removes only the live object binding for a representation transition (for
        /// example a packaging box becoming a stored warehouse record). The identity and its
        /// extended information stay alive so the next live representation of the same id can
        /// replay them. Use <see cref="Unbind"/> for a permanent removal.</summary>
        internal static bool Detach(object entity)
            => GameObjectIdentityCache.Detach(entity, out _);

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

            // A Transferring peer still receives normal application broadcasts, and a box add
            // materializes through RestockManager.SpawnPackageBoxItem. Outside a loaded shop that
            // factory fabricates the empty (singleton)RestockManager and then throws on its null
            // spawn locations; it also freezes the save-slot snapshot before the real boxes exist.
            // The ordered entity baseline that follows resends the authoritative set, so queue
            // pre-baseline adds instead of applying them against the title/loading scene.
            if (CoopCore.Role != CoopRole.Client)
            {
                return;
            }

            var context = CoopCore.Instance?.LiveContext;
            if (context == null || !context.InGame())
            {
                GameObjectIdentityClientBehaviour.Active?.QueuePending(descriptor);
                return;
            }

            if (TryResolve(descriptor.Id, out var existing))
            {
                GameObjectIdentityClientBehaviour.Active?.CancelPending(descriptor.Id);
                BindClient(existing, descriptor.Id);
                if (ApplyDescriptorToExisting(existing, descriptor))
                {
                    return;
                }

                // The existing object cannot represent this descriptor (normally a kind/type
                // change). Retire it and materialize the authoritative shape instead of keeping a
                // stale object under the id.
                GameObjectIdentityAdapters.TryDestroy(existing);
                Unbind(existing);
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

            // A same-id replacement is a representation/pose change (a decoration move), not a
            // removal. Keep the live object and its extended information; re-pose it in place.
            var replacementInPlace = message.Replacement != null
                && message.Replacement.Id == message.Id;
            if (!replacementInPlace)
            {
                ExtendedInformationModule.OnEntityRemoved(message.Id, message.Mode);
            }

            if (replacementInPlace)
            {
                ClientApplyAdded(new EntityAddedMessage
                {
                    Descriptor = message.Replacement,
                    Adopted = true,
                });
                return;
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
        private static bool ApplyDescriptorToExisting(object entity, EntityDescriptor descriptor)
        {
            if (entity is InteractableObject obj && descriptor is PlacementEntityDescriptor placement)
            {
                PlacementIdentityAdapter.ApplyClientDescriptor(obj, placement);
                return true;
            }

            if (entity is InteractablePackagingBox box
                && descriptor is PackagingBoxEntityDescriptor boxDescriptor)
            {
                BoxIdentityLifecycle.ApplyDescriptorToBoxForCreate(box, boxDescriptor);
                return true;
            }

            if (entity is InteractableObject decoration
                && descriptor is DecorationEntityDescriptor decorationDescriptor)
            {
                return DecorationIdentityAdapter.ApplyClientDescriptor(decoration,
                    decorationDescriptor);
            }

            // NPC descriptors and any kind with no extra creation state keep the existing live
            // object; the feature module owns their authoritative state. Only a decoration whose
            // type no longer matches the id is a genuine replace-and-recreate case.
            return true;
        }

        internal static void ClientApplyBaseline(EntityBaselineMessage message)
        {
            if (message?.Entries == null)
            {
                return;
            }

            GameObjectIdentityClientBehaviour.Active?.ClearPending();
            BoxIdentityLifecycle.BeginClientBaseline();

            for (var i = 0; i < message.Entries.Count; i++)
            {
                var descriptor = message.Entries[i];
                if (descriptor == null || !descriptor.Id.IsValid)
                {
                    continue;
                }

                // One bad descriptor must not discard the whole reliable baseline: the host has
                // already promoted this peer to FullyJoined, and every module baseline is sent
                // after this one. Keep the bad entity pending for the end-of-baseline retry
                // instead of unwinding into MessageRouter and losing every later module's state.
                try
                {
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
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogError("[identity] baseline descriptor " + descriptor.Id
                        + " failed; keeping it pending: " + error);
                    GameObjectIdentityClientBehaviour.Active?.QueuePending(descriptor);
                }
            }

            BoxIdentityLifecycle.CompleteClientBaseline();
        }
    }
}
