using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>Connection context for a join baseline so per-kind descriptors can stamp the
    /// save-time slot the guest already has.</summary>
    internal static class GameObjectIdentityBaselineContext
    {
        internal static int ConnectionId
        {
            get; set;
        }
    }

    public sealed partial class GameObjectIdentityHostBehaviour
    {
        internal void PublishAdded(EntityDescriptor descriptor, bool adopted, int ownerConnectionId)
        {
            if (descriptor == null || descriptor.Id.Kind == GameObjectKind.None)
            {
                return;
            }

            _context?.Broadcast(new EntityAddedMessage
            {
                Descriptor = descriptor,
                Adopted = adopted,
                OwnerConnectionId = ownerConnectionId,
            });
        }

        internal void PublishRemoved(EntityRemovedMessage message)
        {
            if (message == null || message.Id.Kind == GameObjectKind.None)
            {
                return;
            }

            _context?.Broadcast(message);
        }

        internal void SendBaseline(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            Util.MemoryProbe.Log("identity-baseline:before");
            var entries = new List<EntityDescriptor>();
            // A host can start a session inside an already-loaded shop. By join time every
            // feature owner is alive, so bind any world object that predates the identity hooks
            // before the baseline snapshot is taken.
            NpcHostLifecycle.BindCurrentPopulation();
            PlacementObjectIdentity.BindLiveObjects();
            BoxIdentityLifecycle.BindLiveBoxes();
            GameObjectIdentityHostHooks.BindExistingDecorations();
            var ids = GameObjectIdentityCache.Snapshot();
            GameObjectIdentityBaselineContext.ConnectionId = connection.Id;
            try
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    if (GameObjectIdentityApi.TryResolve(ids[i], out var entity)
                        && GameObjectIdentityAdapters.TryDescribe(entity, out var descriptor))
                    {
                        entries.Add(descriptor);
                    }
                }
            }
            finally
            {
                GameObjectIdentityBaselineContext.ConnectionId = 0;
            }

            _context?.Send(connection.Id, new EntityBaselineMessage
            {
                BaselineId = 1,
                Complete = true,
                Entries = entries,
            });
            Util.MemoryProbe.Log("identity-baseline:after entries=" + entries.Count);
        }
    }

    public sealed partial class GameObjectIdentityClientBehaviour
    {
        [MessageHandler]
        private void HandleEntityAdded(MessageContext context, EntityAddedMessage message)
            => GameObjectIdentityApi.ClientApplyAdded(message);

        [MessageHandler]
        private void HandleEntityRemoved(MessageContext context, EntityRemovedMessage message)
            => GameObjectIdentityApi.ClientApplyRemoved(message);

        [MessageHandler]
        private void HandleEntityBaseline(MessageContext context, EntityBaselineMessage message)
            => GameObjectIdentityApi.ClientApplyBaseline(message);
    }
}
