using CardShopCoop.Modules.Npc;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// NPC lifecycle for the identity module. The host binds an NPC incarnation in
    /// <see cref="Customer.ActivateCustomer"/> / <see cref="Worker.ActivateWorker"/> and broadcasts
    /// the polymorphic descriptor; the client materializes the puppet from that descriptor and
    /// retires it from <see cref="EntityRemovedMessage"/>. The Npc module only collects state and
    /// renders the puppet.
    /// </summary>
    internal static partial class GameObjectIdentityHostHooks
    {
        private static partial void InstallNpc(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(CustomerManagerStartPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CustomerActivationPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CustomerDeactivationPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CustomerSaveLoadedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(WorkerManagerStartPatch)).Patch();
            harmony.CreateClassProcessor(typeof(WorkerActivationPatch)).Patch();
            harmony.CreateClassProcessor(typeof(WorkerDeactivationPatch)).Patch();

            // Hosting can start after the shop scene already populated its NPC pools. Bind the
            // live population now; the Harmony hooks cover everything that spawns later.
            NpcHostLifecycle.BindCurrentPopulation();
        }

        [HarmonyPatch(typeof(CustomerManager), "Start")]
        private static class CustomerManagerStartPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(CustomerManager __instance)
                => NpcHostLifecycle.CustomersRebuilt(__instance);
        }

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        private static class CustomerActivationPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Customer __instance)
                => NpcHostLifecycle.CustomerChanged(__instance, "Customer.ActivateCustomer");
        }

        [HarmonyPatch(typeof(Customer), "DeactivateCustomer")]
        private static class CustomerDeactivationPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Customer __instance)
                => NpcHostLifecycle.CustomerChanged(__instance, "Customer.DeactivateCustomer");
        }

        [HarmonyPatch(typeof(Customer), "LoadCustomerSaveData")]
        private static class CustomerSaveLoadedPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Customer __instance)
                => NpcHostLifecycle.CustomerSaveLoaded(__instance);
        }

        [HarmonyPatch(typeof(WorkerManager), "Start")]
        private static class WorkerManagerStartPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix()
                => NpcHostLifecycle.WorkersRebuilt();
        }

        [HarmonyPatch(typeof(Worker), "ActivateWorker")]
        private static class WorkerActivationPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Worker __instance)
                => NpcHostLifecycle.WorkerChanged(__instance, "Worker.ActivateWorker");
        }

        [HarmonyPatch(typeof(Worker), "DeactivateWorker")]
        private static class WorkerDeactivationPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Worker __instance)
                => NpcHostLifecycle.WorkerChanged(__instance, "Worker.DeactivateWorker");
        }
    }

    /// <summary>Host-side NPC lifecycle orchestration. All identity binding and lifecycle
    /// publication happens here; <see cref="NpcHostBehaviour"/> only tracks state.</summary>
    internal static class NpcHostLifecycle
    {
        /// <summary>Called once the Npc host behaviour has its state tracker online. Covers the
        /// enable order where the identity behaviour starts before the NPC behaviour.</summary>
        internal static void NpcTrackingReady() => BindCurrentPopulation();

        internal static void CustomersRebuilt(CustomerManager manager)
        {
            var customers = manager?.GetCustomerList();
            for (var i = 0; customers != null && i < customers.Count; i++)
            {
                Sync(customers[i], "CustomerManager.Start");
            }
        }

        internal static void WorkersRebuilt()
        {
            var workers = NpcInterop.Workers;
            for (var i = 0; workers != null && i < workers.Count; i++)
            {
                Sync(workers[i], "WorkerManager.Start");
            }
        }

        internal static void CustomerChanged(Customer customer, string source)
            => Sync(customer, source);

        /// <summary>A restored customer can receive its saved look after
        /// <c>ActivateCustomer</c> has already run (tournament model data loads afterwards). If the
        /// activation snapshot could not describe it, publish now; a customer that already has an
        /// identity is left alone, so this never creates a second incarnation.</summary>
        internal static void CustomerSaveLoaded(Customer customer)
        {
            if (customer == null || !NpcHostBehaviour.IsNpcTracked(customer)
                || GameObjectIdentityApi.TryGetBoundId(customer, out _))
            {
                return;
            }

            Sync(customer, "Customer.LoadCustomerSaveData");
        }

        internal static void WorkerChanged(Worker worker, string source)
            => Sync(worker, source);

        /// <summary>Bind the NPCs that are already alive when the host module comes online
        /// (hosting usually starts after the shop scene finished loading). Inactive pool members
        /// are tracked for state collection but never bound: they have no incarnation yet.</summary>
        internal static void BindCurrentPopulation()
        {
            if (GameObjectIdentityHostBehaviour.Active == null || NpcHostBehaviour.Active == null)
            {
                return;
            }

            var customers = NpcInterop.CustomerManager?.GetCustomerList();
            for (var i = 0; customers != null && i < customers.Count; i++)
            {
                Sync(customers[i], "identity.module-start");
            }

            var workers = NpcInterop.Workers;
            for (var i = 0; workers != null && i < workers.Count; i++)
            {
                Sync(workers[i], "identity.module-start");
            }
        }

        private static void Sync(object entity, string source)
        {
            if (entity == null || NpcHostBehaviour.Active == null)
            {
                return;
            }

            var kind = KindOf(entity);
            if (kind == GameObjectKind.None)
            {
                return;
            }

            var change = NpcHostBehaviour.TrackNpc(entity, source, out var index, out var identity);
            if (identity <= 0)
            {
                return;
            }

            var id = GameObjectId.Npc(kind, index, identity);
            if (change == NpcLifecycleChange.Removed)
            {
                GameObjectIdentityApi.HostPublishRemoved(new EntityRemovedMessage
                {
                    Id = id,
                    Mode = EntityRemovalMode.Despawn,
                });
                GameObjectIdentityApi.Unbind(entity);
                CoopPlugin.Log.LogDebug("[identity] npc removed " + id);
                return;
            }

            if (change == NpcLifecycleChange.None)
            {
                if (!NpcHostBehaviour.IsNpcActive(entity))
                {
                    return;
                }

                if (GameObjectIdentityApi.TryGetBoundId(entity, out var bound) && bound == id)
                {
                    // Already bound and published by an earlier scan or hook.
                    return;
                }
            }

            if (!NpcHostBehaviour.TryGetSnapshot(entity, out var snapshot))
            {
                return;
            }

            if (!GameObjectIdentityApi.BindHost(entity, id))
            {
                return;
            }

            // Binding is local; announcing needs a session. At host start the pool is bound before
            // the transport exists, and blaming that on "no live transport" used to abort the whole
            // identity Install (placement/box/decoration hooks never installed). The join baseline
            // describes every bound entity, so a pre-session bind needs no announcement.
            if (CoopCore.Instance?.Net == null)
            {
                return;
            }

            GameObjectIdentityApi.HostPublishAdded(NpcIdentityAdapter.ToDescriptor(snapshot));
            CoopPlugin.Log.LogDebug("[identity] npc added " + id + " name=" + snapshot.CharName);
        }

        private static GameObjectKind KindOf(object entity)
            => entity is Customer ? GameObjectKind.NpcCustomer
                : entity is Worker ? GameObjectKind.NpcWorker
                : GameObjectKind.None;
    }

    /// <summary>Per-kind adapter for the existing NPC identity space:
    /// <c>(kind, pool index, incarnation)</c>.</summary>
    internal static class NpcIdentityAdapter
    {
        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            if (entity is Customer customer)
            {
                int index;
                int identity;
                var found = CoopCore.Role == CoopRole.Host
                    ? NpcHostBehaviour.TryGetWireIdentity(customer, out index, out identity)
                    : NpcClientBehaviour.TryGetWireIdentity(customer, out index, out identity);
                id = found
                    ? GameObjectId.Npc(GameObjectKind.NpcCustomer, index, identity)
                    : default;
                return found;
            }

            if (entity is Worker worker)
            {
                int index;
                int identity;
                var found = CoopCore.Role == CoopRole.Host
                    ? NpcHostBehaviour.TryGetWireIdentity(worker, out index, out identity)
                    : NpcClientBehaviour.TryGetWireIdentity(worker, out index, out identity);
                id = found
                    ? GameObjectId.Npc(GameObjectKind.NpcWorker, index, identity)
                    : default;
                return found;
            }

            id = default;
            return false;
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            entity = null;
            if (id.Kind == GameObjectKind.NpcCustomer && TryResolveCustomer(id, out var customer))
            {
                entity = customer;
                return true;
            }

            if (id.Kind == GameObjectKind.NpcWorker && TryResolveWorker(id, out var worker))
            {
                entity = worker;
                return true;
            }

            return false;
        }

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
        {
            descriptor = null;
            if (NpcHostBehaviour.Active == null
                || !NpcHostBehaviour.TryGetSnapshot(entity, out var snapshot))
            {
                return false;
            }

            descriptor = ToDescriptor(snapshot);
            return true;
        }

        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
        {
            entity = null;
            if (descriptor is not NpcEntityDescriptor npc)
            {
                return false;
            }

            if (!NpcClientBehaviour.IsSceneReady())
            {
                // The identity client queues this descriptor and retries once the world is ready.
                return false;
            }

            var snapshot = FromDescriptor(npc);
            NpcClientBehaviour.TryGetPuppetEntity(snapshot.Kind, snapshot.Index, out var previous);
            var created = NpcClientBehaviour.SpawnPuppet(snapshot);
            if (created == null)
            {
                return false;
            }

            // A rebuild replaces the visual object. Retire the old object's binding so the old
            // incarnation cannot resolve to a parked clone.
            if (previous != null && !ReferenceEquals(previous, created))
            {
                GameObjectIdentityApi.Unbind(previous);
            }

            NpcClientBehaviour.ApplyPendingState(snapshot.Kind, snapshot.Index, snapshot.Identity);
            entity = created;
            CoopPlugin.Log.LogDebug("[identity] npc created " + descriptor.Id + " name=" + npc.NpcName);
            return true;
        }

        internal static bool Destroy(object entity) => NpcClientBehaviour.DespawnPuppet(entity);

        internal static NpcEntityDescriptor ToDescriptor(in NpcSnapshot snapshot)
            => new()
            {
                NpcKind = snapshot.Kind,
                NpcPoolIndex = snapshot.Index,
                NpcIncarnation = snapshot.Identity,
                NpcFemale = snapshot.Female,
                NpcName = snapshot.CharName,
                Position = snapshot.Position,
                NpcYaw = snapshot.Yaw,
                NpcSpeed = snapshot.Speed,
                NpcFlags = snapshot.Flags,
                NpcActionSequence = snapshot.ActionSequence,
                NpcActionKind = snapshot.ActionKind,
                NpcHoldBig = snapshot.HoldBig,
                NpcHoldItemType = snapshot.HoldItemType,
                NpcHoldBoxNetworkId = snapshot.HoldBoxNetworkId,
                NpcHoldBoxOpened = snapshot.HoldBoxOpened,
            };

        internal static NpcSnapshot FromDescriptor(NpcEntityDescriptor descriptor)
            => new()
            {
                Kind = descriptor.NpcKind,
                Index = (ushort)descriptor.NpcPoolIndex,
                Identity = descriptor.NpcIncarnation,
                Female = descriptor.NpcFemale,
                CharName = descriptor.NpcName,
                Position = descriptor.Position,
                Yaw = descriptor.NpcYaw,
                Speed = descriptor.NpcSpeed,
                Flags = descriptor.NpcFlags,
                ActionSequence = descriptor.NpcActionSequence,
                ActionKind = descriptor.NpcActionKind,
                HoldBig = descriptor.NpcHoldBig,
                HoldItemType = descriptor.NpcHoldItemType,
                HoldBoxNetworkId = descriptor.NpcHoldBoxNetworkId,
                HoldBoxOpened = descriptor.NpcHoldBoxOpened,
                HostTime = Time.unscaledTime,
            };

        private static bool TryResolveCustomer(GameObjectId id, out Customer customer)
            => CoopCore.Role == CoopRole.Host
                ? NpcHostBehaviour.TryResolveCustomer(id.NpcIndex, id.NpcIdentity, out customer)
                : NpcClientBehaviour.TryResolveCustomer(id.NpcIndex, id.NpcIdentity, out customer);

        private static bool TryResolveWorker(GameObjectId id, out Worker worker)
            => CoopCore.Role == CoopRole.Host
                ? NpcHostBehaviour.TryResolveWorker(id.NpcIndex, id.NpcIdentity, out worker)
                : NpcClientBehaviour.TryResolveWorker(id.NpcIndex, id.NpcIdentity, out worker);
    }
}
