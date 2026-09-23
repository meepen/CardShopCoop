using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>Handshake metadata produced by the transfer owner for Core to authorize/send.</summary>
    public sealed class SaveTransferOffer
    {
        public int HostSlot
        {
            get; internal set;
        }
        public int SaveLength
        {
            get; internal set;
        }
        public int BundleLength
        {
            get; internal set;
        }
        public bool SidecarsComplete
        {
            get; internal set;
        }
        public string SidecarWarning
        {
            get; internal set;
        }

        internal byte[] SavePayload
        {
            get; set;
        }
        internal byte[] BundlePayload
        {
            get; set;
        }
    }

    /// <summary>
    /// Host owner. Core authenticates the peer and sends its Welcome/handshake metadata through
    /// <see cref="TryAuthorizeAndSend"/>; this owner then produces and reliably streams the two
    /// module-local payloads in order.
    /// </summary>
    [ServerBehaviour]
    public sealed class SaveTransferHostBehaviour : CoopBehaviour
    {
        private static SaveTransferHostBehaviour _active;
        private readonly HashSet<int> _sending = new();
        private readonly HashSet<int> _readyForAcknowledgement = new();
        private readonly HashSet<int> _aborted = new();
        private readonly Dictionary<int, float> _completionDeadlines = new();
        private readonly object _sendingLock = new();
        // This is a bounded transfer-lifecycle timer, not a scan of world or module state.
        private const float CompletionDeadlineSeconds = 120f;
        private CoopRuntimeContext _context;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }
            if (_active != null && !ReferenceEquals(_active, this))
            {
                throw new InvalidOperationException("A save-transfer host owner is already active.");
            }
            _active = this;
            _context = RuntimeContext;
            _context.Messages.RegisterAttributedHandlers(this);
        }

        private void Update()
        {
            if (_shutdown)
            {
                return;
            }

            List<int> expired = null;
            var now = Time.realtimeSinceStartup;
            lock (_sendingLock)
            {
                foreach (var deadline in _completionDeadlines)
                {
                    if (now < deadline.Value)
                    {
                        continue;
                    }
                    expired ??= new List<int>();
                    expired.Add(deadline.Key);
                    _aborted.Add(deadline.Key);
                    _sending.Remove(deadline.Key);
                    _readyForAcknowledgement.Remove(deadline.Key);
                }
                if (expired != null)
                {
                    for (var i = 0; i < expired.Count; i++)
                    {
                        _completionDeadlines.Remove(expired[i]);
                    }
                }
            }

            if (expired == null)
            {
                return;
            }
            for (var i = 0; i < expired.Count; i++)
            {
                var connectionId = expired[i];
                var reason = "world transfer completion timed out for connection " + connectionId;
                CoopPlugin.Log.LogWarning("coop: " + reason);
                _context.Disconnect?.Invoke(connectionId,
                    new DisconnectInfo(reason, false, "transfer_timeout", true,
                        ConnectionState.Transferring));
            }
        }

        /// <summary>
        /// Called by Core after Hello authentication has passed and the connection entered
        /// Transferring. The callback must queue Core's Welcome frame before it returns.
        /// </summary>
        public static bool TryAuthorizeAndSend(PeerConnection target,
            Func<SaveTransferOffer, bool> sendHandshakeOffer, Action afterOfferQueued = null)
        {
            var owner = _active;
            if (owner == null || owner._shutdown || target == null
                || target.State != ConnectionState.Transferring
                || sendHandshakeOffer == null)
            {
                return false;
            }
            lock (owner._sendingLock)
            {
                if (!owner._sending.Add(target.Id))
                {
                    CoopPlugin.Log.LogWarning("coop: duplicate save transfer authorization for connection "
                        + target.Id + " ignored");
                    return false;
                }
                owner._aborted.Remove(target.Id);
                owner._completionDeadlines[target.Id] = Time.realtimeSinceStartup
                    + CompletionDeadlineSeconds;
            }

            SaveTransferOffer offer;
            var sidecarRoot = (string)null;
            try
            {
                // Unity/game reads and the synchronous save must happen before the worker. Core
                // calls this API from its main-thread dispatch path.
                var save = SaveTransferStorage.BuildHostPayload();
                // Capture the Unity-owned path here. Enumeration and all sidecar file reads are
                // deliberately deferred to the worker after SaveGameData has completed.
                sidecarRoot = Application.persistentDataPath;

                offer = new SaveTransferOffer
                {
                    HostSlot = SaveTransferStorage.HostSnapshotSlot,
                    SavePayload = save,
                    BundlePayload = new byte[0],
                    SidecarsComplete = false,
                };
            }
            catch (Exception error)
            {
                lock (owner._sendingLock)
                {
                    owner._sending.Remove(target.Id);
                    owner._completionDeadlines.Remove(target.Id);
                }
                CoopPlugin.Log.LogError("coop: host snapshot failed: " + error);
                SaveTransferRuntime.FailTransfer(target, error.Message);
                return false;
            }

            var generation = SaveTransferRuntime.SessionGeneration;
            var thread = new Thread(() =>
            {
                var transferQueued = false;
                try
                {
                    if (!SaveTransferRuntime.IsSessionGeneration(generation))
                    {
                        return;
                    }
                    if (owner._shutdown || target.State != ConnectionState.Transferring)
                    {
                        return;
                    }

                    try
                    {
                        var sidecars = SaveTransferSidecars.BuildBundleWithMetadata(
                            SaveTransferStorage.HostSnapshotSlot, sidecarRoot);
                        offer.BundlePayload = sidecars.Payload;
                        offer.SidecarsComplete = sidecars.Complete;
                        offer.SidecarWarning = sidecars.Warning;
                    }
                    catch (Exception error)
                    {
                        offer.BundlePayload = new byte[0];
                        offer.SidecarsComplete = false;
                        offer.SidecarWarning = BoundWarning("sidecar bundle could not be read: "
                            + error.Message);
                        CoopPlugin.Log.LogWarning("Sidecar bundle failed (sending base save only): "
                            + error);
                    }

                    if (owner._shutdown || owner.IsAborted(target.Id)
                        || !SaveTransferRuntime.IsSessionGeneration(generation))
                    {
                        return;
                    }

                    var savePayload = Msg.Gzip(offer.SavePayload);
                    var bundlePayload = offer.BundlePayload.Length == 0
                        ? new byte[0] : Msg.Gzip(offer.BundlePayload);
                    if (savePayload.Length <= 0 || savePayload.Length > SaveTransferStorage.MaxTransferBytes)
                    {
                        throw new InvalidDataException("Compressed world transfer exceeds its safety limit.");
                    }
                    if (bundlePayload.Length > SaveTransferStorage.MaxTransferBytes)
                    {
                        offer.BundlePayload = new byte[0];
                        offer.SidecarsComplete = false;
                        offer.SidecarWarning = BoundWarning(
                            "sidecar bundle exceeded the compressed transfer limit and was omitted");
                        bundlePayload = new byte[0];
                        CoopPlugin.Log.LogWarning("Sidecar bundle exceeded the compressed transfer limit; "
                            + "sending the base save with sidecars marked incomplete");
                    }
                    offer.SaveLength = savePayload.Length;
                    offer.BundleLength = bundlePayload.Length;

                    if (!SaveTransferRuntime.TryEnqueueMainThread(() => SendTransferOnMainThread(
                        owner, target, generation, offer, savePayload, bundlePayload,
                        sendHandshakeOffer, afterOfferQueued)))
                    {
                        throw new InvalidOperationException(
                            "the main-thread queue rejected the world transfer");
                    }

                    transferQueued = true;
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("coop: world send failed: " + error);
                    if (!owner._shutdown && !owner.IsAborted(target.Id))
                    {
                        SaveTransferRuntime.FailTransfer(target, error.Message);
                    }
                }
                finally
                {
                    lock (owner._sendingLock)
                    {
                        if (!transferQueued)
                        {
                            owner._sending.Remove(target.Id);
                            owner._completionDeadlines.Remove(target.Id);
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CoopWorldSend"
            };
            thread.Start();
            return true;
        }

        private static void SendTransferOnMainThread(SaveTransferHostBehaviour owner,
            PeerConnection target, int generation, SaveTransferOffer offer, byte[] savePayload,
            byte[] bundlePayload, Func<SaveTransferOffer, bool> sendHandshakeOffer,
            Action afterOfferQueued)
        {
            try
            {
                SendHandshakeOffer(owner, target, generation, offer, sendHandshakeOffer);
                afterOfferQueued?.Invoke();
                SendChunks(owner, target, generation, savePayload, true);
                SendDirect(owner, target, generation,
                    new SaveDoneMessage { TotalLength = savePayload.Length });
                SendChunks(owner, target, generation, bundlePayload, false);
                SendDirect(owner, target, generation,
                    new BundleDoneMessage { TotalLength = bundlePayload.Length });
                lock (owner._sendingLock)
                {
                    owner._sending.Remove(target.Id);
                    if (!owner._aborted.Contains(target.Id))
                    {
                        owner._readyForAcknowledgement.Add(target.Id);
                    }
                }

                CoopPlugin.Log.LogInfo("coop: transfer sent to connection " + target.Id + " (save "
                    + savePayload.Length / 1024 + " KB, bundle " + bundlePayload.Length / 1024
                    + " KB)");
            }
            catch (Exception error)
            {
                var shouldReport = false;
                lock (owner._sendingLock)
                {
                    shouldReport = !owner._aborted.Contains(target.Id);
                    owner._aborted.Add(target.Id);
                    owner._sending.Remove(target.Id);
                    owner._readyForAcknowledgement.Remove(target.Id);
                    owner._completionDeadlines.Remove(target.Id);
                }

                if (!owner._shutdown && shouldReport)
                {
                    SaveTransferRuntime.FailTransfer(target, error.Message);
                }

                throw;
            }
        }

        internal static bool TryConsumeTransferAcknowledgement(PeerConnection connection)
        {
            var owner = _active;
            if (owner == null || connection == null)
            {
                return false;
            }

            lock (owner._sendingLock)
            {
                // The main-thread send action removes _sending only after every transfer frame,
                // including BundleDone, has been admitted. This closes the window where an early
                // lifecycle acknowledgement could advance the host before the transfer was queued.
                var accepted = !owner._sending.Contains(connection.Id)
                    && !owner._aborted.Contains(connection.Id)
                    && owner._readyForAcknowledgement.Remove(connection.Id);
                if (accepted)
                {
                    owner._completionDeadlines.Remove(connection.Id);
                }
                return accepted;
            }
        }

        [MessageHandler(typeof(TransferFailureMessage))]
        private void HandleTransferFailure(MessageContext context, TransferFailureMessage message)
        {
            if (_shutdown || context?.Connection == null
                || context.Connection.State != ConnectionState.Transferring)
            {
                return;
            }

            var reason = string.IsNullOrWhiteSpace(message?.Reason)
                ? "the client rejected the world transfer" : message.Reason;
            var connectionId = context.Connection.Id;
            lock (_sendingLock)
            {
                _aborted.Add(connectionId);
                _sending.Remove(connectionId);
                _readyForAcknowledgement.Remove(connectionId);
                _completionDeadlines.Remove(connectionId);
            }
            CoopPlugin.Log.LogWarning("coop: client rejected world transfer for connection "
                + connectionId + ": " + reason);
            _context.Disconnect?.Invoke(connectionId,
                new DisconnectInfo("client rejected world transfer: " + reason, true,
                    "transfer_failed", false, ConnectionState.Transferring));
        }

        private static void SendChunks(SaveTransferHostBehaviour owner, PeerConnection target,
            int generation, byte[] payload, bool save)
        {
            const int chunkSize = SaveTransferClientBehaviour.ChunkSize;
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, payload.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(payload, offset, chunk, 0, length);
                if (save)
                {
                    SendDirect(owner, target, generation,
                        new SaveChunkMessage { Offset = offset, Data = chunk });
                }
                else
                {
                    SendDirect(owner, target, generation,
                        new BundleChunkMessage { Offset = offset, Data = chunk });
                }
            }
        }

        private static void SendDirect(SaveTransferHostBehaviour owner,
            PeerConnection target, int generation, INetMessage message)
        {
            if (owner._shutdown || owner.IsAborted(target.Id)
                || target.State != ConnectionState.Transferring
                || !SaveTransferRuntime.IsSessionGeneration(generation))
            {
                throw new InvalidOperationException("world transfer session ended before send");
            }

            owner._context.Send(target.Id, message);
        }

        private static void SendHandshakeOffer(SaveTransferHostBehaviour owner,
            PeerConnection target, int generation, SaveTransferOffer offer,
            Func<SaveTransferOffer, bool> sendHandshakeOffer)
        {
            if (owner._shutdown || owner.IsAborted(target.Id)
                || target.State != ConnectionState.Transferring
                || !SaveTransferRuntime.IsSessionGeneration(generation))
            {
                throw new InvalidOperationException("world transfer session ended before Welcome");
            }

            if (!sendHandshakeOffer(offer))
            {
                throw new InvalidOperationException("Welcome admission was rejected");
            }
        }

        private bool IsAborted(int connectionId)
        {
            lock (_sendingLock)
            {
                return _aborted.Contains(connectionId);
            }
        }

        private static string BoundWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                return null;
            }
            warning = warning.Trim();
            return warning.Length <= 512 ? warning : warning.Substring(0, 509) + "...";
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                lock (_sendingLock)
                {
                    _sending.Remove(connection.Id);
                    _readyForAcknowledgement.Remove(connection.Id);
                    _aborted.Add(connection.Id);
                    _completionDeadlines.Remove(connection.Id);
                }
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            lock (_sendingLock)
            {
                _sending.Clear();
                _readyForAcknowledgement.Clear();
                _aborted.Clear();
                _completionDeadlines.Clear();
            }
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
