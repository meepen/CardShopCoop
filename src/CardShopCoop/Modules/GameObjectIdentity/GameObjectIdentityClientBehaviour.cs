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

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            BoxIdentityLifecycle.InvalidateSceneSlots();
            RetryPending();
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _) => RetryPending();

        private void RetryPending()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var pending = new List<EntityDescriptor>(_pending.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                var descriptor = pending[i];
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
        }


    }
}
