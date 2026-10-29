using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Runtime;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>Client side of the generic identity lifecycle.</summary>
    [ClientBehaviour]
    public sealed partial class GameObjectIdentityClientBehaviour : CoopBehaviour
    {
        internal static GameObjectIdentityClientBehaviour Active
        {
            get; private set;
        }

        private readonly Dictionary<GameObjectId, EntityDescriptor> _pending = new();
        private CoopRuntimeContext _context;

        private void OnEnable()
        {
            _context = RuntimeContext;
            Active = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
        }

        private void OnDisable()
        {
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            _pending.Clear();
            _context = null;
        }

        internal void QueuePending(EntityDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.Id.IsValid)
            {
                return;
            }

            _pending[descriptor.Id] = descriptor;
        }

        internal void CancelPending(GameObjectId id)
        {
            if (id.IsValid)
            {
                _pending.Remove(id);
            }
        }

        internal void ClearPending() => _pending.Clear();

        /// <summary>Retries descriptors that could not be created when their baseline arrived.
        /// The identity baseline can be dispatched before a late scene object (a warehouse shelf
        /// or decoration) exists, and the scene-loaded/world-ready retries have already fired by
        /// then. The host's WorldBaselineComplete marker is the last baseline message, so it is
        /// the only guaranteed point after every descriptor and every module baseline has been
        /// delivered.</summary>
        internal void RetryPendingNow() => RetryPending();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            // Placement keys are scene-local. The host resets them from its own scene-loaded
            // hook, but that hook is host-only; without this a client keeps every placement it
            // ever bound, stale keys collide with the next world's host keys, and locators drift.
            PlacementObjectIdentity.Reset();
            BoxIdentityLifecycle.InvalidateSceneSlots();
            RetryPending();
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _) => RetryPending();

        /// <summary>A session can end without a scene load (return to title, kick, host quit).
        /// Those statics belong to the scene/session, not the process.</summary>
        [OnSessionStopped]
        private void ResetSessionState()
        {
            _pending.Clear();
            PlacementObjectIdentity.Reset();
        }

        private void RetryPending()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            // A scene-loaded callback can still run after the session was torn down: the
            // registry destroys behaviours with Object.Destroy, so OnDisable (and Active=null)
            // is deferred to the end of the frame while the Title scene's sceneLoaded chain runs.
            // Materializing a descriptor there would call RestockManager.SpawnPackageBoxItem
            // outside a game session and fabricate a persistent (singleton)RestockManager. Only
            // retry while this is the live client and the world is actually in game.
            if (_context == null || CoopCore.Role != CoopRole.Client || !_context.InGame())
            {
                return;
            }

            var pending = new List<EntityDescriptor>(_pending.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                var descriptor = pending[i];
                try
                {
                    if (GameObjectIdentityApi.TryResolve(descriptor.Id, out _))
                    {
                        _pending.Remove(descriptor.Id);
                        continue;
                    }

                    if (!GameObjectIdentityAdapters.TryCreate(descriptor, out var entity)
                        || entity == null)
                    {
                        continue;
                    }

                    GameObjectIdentityApi.BindClient(entity, descriptor.Id);
                    _pending.Remove(descriptor.Id);
                }
                catch (Exception error)
                {
                    // A single malformed or mod-dependent descriptor must not prevent the rest
                    // of the pending set from materializing. It stays pending for the next
                    // lifecycle signal.
                    CoopPlugin.Log?.LogError("[identity] pending descriptor " + descriptor.Id
                        + " could not be created: " + error);
                }
            }
        }


    }
}
