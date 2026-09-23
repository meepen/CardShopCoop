using System;
using CardShopCoop.Net.Connection;
using UnityEngine;

namespace CardShopCoop.Runtime
{
    public abstract class CoopRuntimeBehaviour : MonoBehaviour
    {
        private CoopBehaviourRegistry _registry;
        private bool _shutdown;

        public CoopRuntimeContext Context
        {
            get; private set;
        }

        public void Initialize(CoopRuntimeContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _registry = this is ServerRuntimeBehaviour
                ? CoopBehaviourRegistry.CreateServer(gameObject, context)
                : this is ClientRuntimeBehaviour
                    ? CoopBehaviourRegistry.CreateClient(gameObject, context)
                    : throw new InvalidOperationException(
                        "Session runtime must be a server or client runtime behaviour.");
            try
            {
                _registry.SessionStarted();
            }
            catch (Exception startupError)
            {
                try
                {
                    _registry.Dispose();
                }
                catch (Exception cleanupError)
                {
                    _registry = null;
                    throw new AggregateException("Co-op session startup and rollback failed.",
                        startupError, cleanupError);
                }
                _registry = null;
                throw;
            }
        }

        public void InitializePersistent(CoopRuntimeContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _registry = CoopBehaviourRegistry.CreatePersistent(gameObject, context);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            Exception stopError = null;
            Exception disposeError = null;
            try
            {
                _registry?.SessionStopped();
            }
            catch (Exception error)
            {
                stopError = error;
            }

            try
            {
                _registry?.Dispose();
            }
            catch (Exception error)
            {
                disposeError = error;
            }
            finally
            {
                _registry = null;
                Context?.PeerPresence.ClearAll();
            }

            if (stopError != null || disposeError != null)
            {
                var errors = new System.Collections.Generic.List<Exception>();
                if (stopError != null)
                    errors.Add(stopError);
                if (disposeError != null)
                    errors.Add(disposeError);
                throw new AggregateException("Co-op runtime shutdown failed.", errors);
            }
        }

        public T GetBehaviour<T>() where T : MonoBehaviour
        {
            return _registry == null ? null : GetComponentInChildren<T>(true);
        }

        public void ClientJoined(PeerConnection connection) => _registry?.ClientJoined(connection);
        public void ClientDisconnected(PeerConnection connection, DisconnectInfo info)
        {
            Context?.PeerPresence.Clear(connection == null ? 0 : connection.Id);
            _registry?.ClientDisconnected(connection, info);
        }
        public void FullyJoined(PeerConnection connection) => _registry?.FullyJoined(connection);
        public void SessionStarted() => _registry?.SessionStarted();
        public void SessionStopped() => _registry?.SessionStopped();

        private void OnDestroy()
        {
            Shutdown();
        }
    }

    public sealed class ServerRuntimeBehaviour : CoopRuntimeBehaviour
    {
    }

    public sealed class ClientRuntimeBehaviour : CoopRuntimeBehaviour
    {
    }

    public sealed class PersistentRuntimeBehaviour : CoopRuntimeBehaviour
    {
    }
}
