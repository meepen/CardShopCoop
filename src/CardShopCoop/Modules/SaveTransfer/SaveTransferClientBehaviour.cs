using System;
using System.IO;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Messages;
using CardShopCoop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>Client owner for the authenticated host world transfer.</summary>
    [ClientBehaviour]
    public sealed class SaveTransferClientBehaviour : CoopBehaviour
    {
        internal const int ChunkSize = 128 * 1024;

        private static SaveTransferClientBehaviour _active;
        private CoopRuntimeContext _context;
        private MemoryStream _saveBuffer;
        private MemoryStream _bundleBuffer;
        private byte[] _pendingSave;
        private int _saveExpected;
        private int _bundleExpected;
        private int _hostSlot;
        private int _sessionGeneration;
        private bool _sidecarsComplete;
        private string _sidecarWarning;
        private bool _saveDone;
        private bool _worldRequested;
        private bool _transferFailed;
        private bool _worldArrived;
        private float _reloadStartedAt;
        private bool _awaitingFullyJoinedAck;
        private float _disconnectAfterFailureAt;
        private string _failureReason;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }
            if (_active != null && !ReferenceEquals(_active, this))
            {
                throw new InvalidOperationException("A save-transfer client owner is already active.");
            }
            _active = this;
            _context = RuntimeContext;
            _context.Messages.RegisterAttributedHandlers(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldLoaded);
        }

        /// <summary>Core calls this after it validates the host Welcome metadata.</summary>
        public static bool TryBeginClientTransfer(int hostSlot, int saveLength, int bundleLength,
            bool sidecarsComplete = true, string sidecarWarning = null)
        {
            var owner = _active;
            if (owner == null || owner._shutdown || saveLength <= 0
                || saveLength > SaveTransferStorage.MaxTransferBytes
                || bundleLength < 0 || bundleLength > SaveTransferStorage.MaxTransferBytes
                || hostSlot < 0 || hostSlot > 99)
            {
                return false;
            }
            if (owner._saveBuffer != null || owner._bundleBuffer != null || owner._worldRequested)
            {
                owner.RejectTransfer("duplicate world transfer authorization");
                return false;
            }

            owner._saveExpected = saveLength;
            owner._bundleExpected = bundleLength;
            owner._hostSlot = hostSlot;
            owner._sidecarsComplete = sidecarsComplete;
            owner._sidecarWarning = BoundWarning(sidecarWarning);
            owner._sessionGeneration = SaveTransferRuntime.SessionGeneration;
            if (owner._sessionGeneration < 0
                || !SaveTransferRuntime.IsSessionGeneration(owner._sessionGeneration))
            {
                owner.RejectTransfer("the session generation is no longer active");
                return false;
            }
            owner._saveBuffer = new MemoryStream(Math.Min(saveLength, SaveTransferStorage.MaxTransferBytes));
            owner._bundleBuffer = new MemoryStream(Math.Min(bundleLength, SaveTransferStorage.MaxTransferBytes));
            owner._pendingSave = null;
            owner._saveDone = false;
            owner._worldRequested = false;
            owner._transferFailed = false;
            owner._awaitingFullyJoinedAck = false;
            owner._disconnectAfterFailureAt = 0f;
            owner._failureReason = null;
            SaveTransferRuntime.SetGuestBorrowedWorld(true);
            var status = "Downloading host shop (" + (saveLength + bundleLength) / 1024 + " KB)...";
            if (!sidecarsComplete)
            {
                status += " Some mod data was omitted.";
                CoopPlugin.Log.LogWarning("coop: host sidecar transfer is partial"
                    + (string.IsNullOrEmpty(owner._sidecarWarning) ? "" : ": "
                        + owner._sidecarWarning));
            }
            owner.SetStatus(status);
            return true;
        }

        public static bool IsReloading => SaveTransferRuntime.ClientReloading;
        public static bool PreloadHold => SaveTransferRuntime.ClientReloading;

        private void Update()
        {
            if (_disconnectAfterFailureAt > 0f
                && Time.realtimeSinceStartup >= _disconnectAfterFailureAt)
            {
                _disconnectAfterFailureAt = 0f;
                _context?.Disconnect?.Invoke(1,
                    new DisconnectInfo("world transfer failed: " + _failureReason, false,
                        "transfer_failed", true, ConnectionState.Transferring));
                return;
            }
        }

        private void OnWorldLoaded(CEventPlayer_GameDataFinishLoaded _)
        {
            if (_shutdown || !_worldArrived || !SaveTransferRuntime.ClientReloading)
            {
                return;
            }

            SaveTransferRuntime.SetReloading(false);
            _worldArrived = false;
            var elapsed = Time.realtimeSinceStartup - _reloadStartedAt;
            SetStatus("World loaded - co-op sync resumed.");
            CoopPlugin.Log.LogInfo("Join world load completed in " + elapsed.ToString("F2")
                + "s; requesting authoritative baseline");
            _awaitingFullyJoinedAck = true;
            SaveTransferRuntime.EmitFullyJoined();
        }

        internal static bool TryConsumeFullyJoinedAcknowledgement(PeerConnection connection)
        {
            var owner = _active;
            if (owner == null || connection == null || connection.Id != 1
                || !owner._awaitingFullyJoinedAck)
            {
                return false;
            }

            owner._awaitingFullyJoinedAck = false;
            return true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (SaveTransferRuntime.ClientReloading && !string.Equals(scene.name, "Title",
                StringComparison.Ordinal))
            {
                _worldArrived = true;
                _reloadStartedAt = Time.realtimeSinceStartup;
            }
        }

        [MessageHandler(typeof(SaveChunkMessage))]
        private void HandleSaveChunk(MessageContext _, SaveChunkMessage message)
        {
            if (!CanReceive() || _saveBuffer == null || _saveDone || message == null)
            {
                return;
            }
            var data = message.Data;
            if (data == null || data.Length <= 0 || data.Length > ChunkSize
                || message.Offset != _saveBuffer.Length
                || _saveBuffer.Length + data.Length > _saveExpected)
            {
                RejectTransfer("invalid save chunk offset or length");
                return;
            }
            _saveBuffer.Write(data, 0, data.Length);
            SetStatus("Downloading host shop... " + (_saveBuffer.Length * 100 / _saveExpected) + "%");
        }

        [MessageHandler(typeof(SaveDoneMessage))]
        private void HandleSaveDone(MessageContext _, SaveDoneMessage message)
        {
            if (!CanReceive() || _saveBuffer == null || _saveDone || message == null)
            {
                return;
            }
            if (message.TotalLength != _saveExpected || _saveBuffer.Length != _saveExpected)
            {
                RejectTransfer("save transfer length did not match its authorization");
                return;
            }

            try
            {
                _pendingSave = SaveTransferStorage.GunzipCapped(_saveBuffer.ToArray(),
                    SaveTransferStorage.MaxExpandedBytes);
                SaveTransferStorage.ValidateSavePayload(_pendingSave);
                _saveDone = true;
                _saveBuffer.Dispose();
                _saveBuffer = null;
                SetStatus("Shop received - downloading mod data...");
            }
            catch (Exception error)
            {
                RejectTransfer("world download could not be unpacked: " + error.Message);
            }
        }

        [MessageHandler(typeof(BundleChunkMessage))]
        private void HandleBundleChunk(MessageContext _, BundleChunkMessage message)
        {
            if (!CanReceive() || _bundleBuffer == null || !_saveDone || _worldRequested
                || message == null)
            {
                return;
            }
            var data = message.Data;
            if (data == null || data.Length <= 0 || data.Length > ChunkSize
                || message.Offset != _bundleBuffer.Length
                || _bundleBuffer.Length + data.Length > _bundleExpected)
            {
                RejectTransfer("invalid mod-data chunk offset or length");
                return;
            }
            _bundleBuffer.Write(data, 0, data.Length);
            if (_bundleExpected > 0)
            {
                SetStatus("Downloading mod data... " + (_bundleBuffer.Length * 100 / _bundleExpected) + "%");
            }
        }

        [MessageHandler(typeof(BundleDoneMessage))]
        private void HandleBundleDone(MessageContext _, BundleDoneMessage message)
        {
            if (!CanReceive() || !_saveDone || _pendingSave == null || _bundleBuffer == null
                || _worldRequested || message == null)
            {
                return;
            }
            if (message.TotalLength != _bundleExpected || _bundleBuffer.Length != _bundleExpected)
            {
                RejectTransfer("mod-data transfer length did not match its authorization");
                return;
            }

            byte[] bundle;
            try
            {
                bundle = _bundleExpected == 0 ? new byte[0]
                    : SaveTransferStorage.GunzipCapped(_bundleBuffer.ToArray(),
                        SaveTransferStorage.MaxExpandedBytes);
            }
            catch (Exception error)
            {
                RejectTransfer("mod data could not be unpacked: " + error.Message);
                return;
            }

            if (!_sidecarsComplete)
            {
                CoopPlugin.Log.LogWarning("coop: applying a partial sidecar bundle"
                    + (string.IsNullOrEmpty(_sidecarWarning) ? "" : ": " + _sidecarWarning));
            }

            _worldRequested = true;
            _saveDone = false;
            _saveBuffer?.Dispose();
            _bundleBuffer.Dispose();
            _saveBuffer = null;
            _bundleBuffer = null;
            var save = _pendingSave;
            _pendingSave = null;
            var generation = _sessionGeneration;
            SaveTransferRuntime.SetReloading(true);
            _worldArrived = false;
            _reloadStartedAt = Time.realtimeSinceStartup;
            SetStatus("World received - loading...");

            if (bundle.Length == 0)
            {
                ApplySave(save, generation);
                return;
            }

            try
            {
                var sidecarRoot = Application.persistentDataPath;
                SaveTransferSidecars.ApplyBundleAsync(bundle, _hostSlot, SaveTransferStorage.CoopSlot,
                    generation, sidecarRoot, () => ApplySave(save, generation), error => RejectTransfer(
                        "could not apply mod data: " + error.Message));
            }
            catch (Exception error)
            {
                RejectTransfer("could not start mod-data apply: " + error.Message);
            }
        }

        private void ApplySave(byte[] save, int generation)
        {
            if (_shutdown || !SaveTransferRuntime.IsSessionGeneration(generation))
            {
                return;
            }
            try
            {
                SaveTransferStorage.ApplyAndLoadAsync(save, generation, () => { }, error =>
                    RejectTransfer("could not apply the received world: " + error.Message));
            }
            catch (Exception error)
            {
                RejectTransfer("could not start world apply: " + error.Message);
            }
        }

        private bool CanReceive()
        {
            return !_shutdown && !_transferFailed
                && SaveTransferRuntime.IsSessionGeneration(_sessionGeneration);
        }

        private void RejectTransfer(string reason)
        {
            if (_transferFailed)
            {
                return;
            }
            _transferFailed = true;
            _saveBuffer?.Dispose();
            _bundleBuffer?.Dispose();
            _saveBuffer = null;
            _bundleBuffer = null;
            _pendingSave = null;
            SaveTransferRuntime.SetReloading(false);
            SetStatus("World transfer failed: " + reason);
            SaveTransferRuntime.ReportFailure(reason);
            _failureReason = reason;
            _disconnectAfterFailureAt = Time.realtimeSinceStartup + 0.25f;
            _context.Send(1, new TransferFailureMessage { Reason = reason });
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

        private void SetStatus(string status)
        {
            _context?.SetStatusLine?.Invoke(status, 8f);
        }

        internal static void EmitFullyJoinedFallback()
        {
            var owner = _active;
            if (owner == null || owner._shutdown)
            {
                return;
            }
            owner._context.Send(1, new FullyJoinedMessage());
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldLoaded);
            _context?.Messages.UnregisterAttributedHandlers(this);
            _saveBuffer?.Dispose();
            _bundleBuffer?.Dispose();
            _saveBuffer = null;
            _bundleBuffer = null;
            _pendingSave = null;
            _sidecarWarning = null;
            _disconnectAfterFailureAt = 0f;
            _failureReason = null;
            _worldArrived = false;
            _worldRequested = false;
            _awaitingFullyJoinedAck = false;
            SaveTransferRuntime.SetReloading(false);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
