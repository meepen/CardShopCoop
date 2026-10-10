using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Tv
{
    /// <summary>Client playback mirror and immediately predictive control forwarding.</summary>
    [ClientBehaviour]
    public sealed class TvClientBehaviour : CoopBehaviour
    {
        private const byte Next = 1;
        private const byte Previous = 2;
        private const byte Pause = 3;
        private const byte Power = 4;
        private const byte Shuffle = 5;
        private const byte Seek = 6;
        private const byte Open = 7;
        private const string PredictionScope = "tv";

        private static TvClientBehaviour _active;
        private ICoopContext _context;
        private Harmony _harmony;
        private TvBaselineMessage _latestBaseline;
        private TvMediaDeltaMessage _pendingMedia;
        private TvPauseDeltaMessage _pendingPause;
        private TvPowerDeltaMessage _pendingPower;
        private TvShuffleDeltaMessage _pendingShuffle;
        private TvSeekDeltaMessage _pendingSeek;
        private TvBarrierDeltaMessage _pendingBarrier;
        private TvPlaybackErrorDeltaMessage _pendingError;
        private bool _shutdown;
        private bool _joined;
        private bool _clientBarrier;
        private int _clientGeneration = -1;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = Context;
            var registered = false;
            try
            {
                ResetSessionState();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.tv.client.runtime");
                ApplyPatches(_harmony);
                OnControllerLifecycle();
            }
            catch (Exception exception)
            {
                CoopLog.Error("TV client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                ResetSessionState();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvBaselineMessage))]
        private void HandleBaseline(CoopMessageContext context, TvBaselineMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            if (message.Generation < _clientGeneration)
            {
                return;
            }

            _latestBaseline = message;
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvMediaDeltaMessage))]
        private void HandleMediaDelta(CoopMessageContext context, TvMediaDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            if (message.Generation < _clientGeneration
                || _pendingMedia != null && _pendingMedia.Generation > message.Generation)
            {
                // Stale media delta (older than already-applied state): retire without applying.
                CoopPredict.Ack(message.PredictionId);
                return;
            }

            Supersede(ref _pendingMedia, message);
            if (_pendingError != null && _pendingError.Generation < message.Generation)
            {
                // The newer media state supersedes the older pending error: retire it unapplied.
                CoopPredict.Ack(_pendingError.PredictionId);
                _pendingError = null;
            }
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPauseDeltaMessage))]
        private void HandlePauseDelta(CoopMessageContext context, TvPauseDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            Supersede(ref _pendingPause, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPowerDeltaMessage))]
        private void HandlePowerDelta(CoopMessageContext context, TvPowerDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            Supersede(ref _pendingPower, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvShuffleDeltaMessage))]
        private void HandleShuffleDelta(CoopMessageContext context, TvShuffleDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            Supersede(ref _pendingShuffle, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvSeekDeltaMessage))]
        private void HandleSeekDelta(CoopMessageContext context, TvSeekDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            Supersede(ref _pendingSeek, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvBarrierDeltaMessage))]
        private void HandleBarrierDelta(CoopMessageContext context, TvBarrierDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            Supersede(ref _pendingBarrier, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPlaybackErrorDeltaMessage))]
        private void HandlePlaybackError(CoopMessageContext context, TvPlaybackErrorDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            if (message.Generation < _clientGeneration
                || _pendingMedia != null && _pendingMedia.Generation > message.Generation)
            {
                // Stale error (superseded by newer/queued media): retire without applying.
                CoopPredict.Ack(message.PredictionId);
                return;
            }

            Supersede(ref _pendingError, message);
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
            _harmony = null;
            ResetSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void ResetSessionState()
        {
            TvInterop.ResetSession();
            _latestBaseline = null;
            _pendingMedia = null;
            _pendingPause = null;
            _pendingPower = null;
            _pendingShuffle = null;
            _pendingSeek = null;
            _pendingBarrier = null;
            _pendingError = null;
            _joined = false;
            _clientBarrier = false;
            _clientGeneration = -1;
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
            => ApplyLatestState();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TvInterop.NotifySceneLoaded();
            _clientGeneration = -1;
            ApplyLatestState();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                ResetSessionState();
            }
        }

        private void OnControllerLifecycle()
        {
            if (TvInterop.Present)
            {
                ApplyLatestState();
            }
        }

        private static void ReconcileWithoutController(Guid predictionId)
        {
            if (!TvInterop.ControllerReady)
            {
                // The controller cannot apply yet; retire the prediction with a no-op. The delta is
                // still retained in _pendingX and its authoritative apply runs from ApplyLatestState
                // once the controller is ready.
                CoopPredict.AckOrApply(predictionId, () => { });
            }
        }

        /// <summary>Replaces a pending delta with a newer one, retiring the superseded delta's
        /// prediction first. Without this a delta that is overwritten before ApplyLatestState runs
        /// keeps its prediction alive, so a late rollback for it reverts playback the player has
        /// already moved past.</summary>
        private static void Supersede<T>(ref T slot, T message)
            where T : class, IPredictedMessage
        {
            var previous = slot;
            if (previous != null && previous.PredictionId != Guid.Empty)
            {
                CoopPredict.Ack(previous.PredictionId);
            }

            slot = message;
        }

        private void ApplyLatestState()
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame
                || !TvInterop.Present || TvInterop.ApplyingRemote || !TvInterop.ControllerReady)
            {
                return;
            }

            if (_latestBaseline != null)
            {
                var baseline = _latestBaseline;
                if (!string.IsNullOrWhiteSpace(baseline.SourceUrl) && !TvInterop.EnrollmentReady)
                {
                    TvInterop.RequestClientEnrollment();
                    return;
                }

                ApplyBaseline(baseline);
                _latestBaseline = null;
            }

            if (_pendingMedia != null && _pendingMedia.Generation < _clientGeneration)
            {
                // Stale media delta superseded by applied state: retire without applying.
                CoopPredict.Ack(_pendingMedia.PredictionId);
                _pendingMedia = null;
            }

            if (_pendingError != null && _pendingError.Generation < _clientGeneration)
            {
                // Stale error superseded by applied state: retire without applying.
                CoopPredict.Ack(_pendingError.PredictionId);
                _pendingError = null;
            }

            if (_pendingMedia != null && !string.IsNullOrWhiteSpace(_pendingMedia.SourceUrl)
                && !TvInterop.EnrollmentReady)
            {
                TvInterop.RequestClientEnrollment();
                return;
            }

            ApplyMediaDelta();
            ApplyPauseDelta();
            ApplyPowerDelta();
            ApplyShuffleDelta();
            ApplySeekDelta();
            ApplyBarrierDelta();
            ApplyErrorDelta();
        }

        private void ApplyBaseline(TvBaselineMessage message)
        {
            _clientGeneration = message.Generation;
            _clientBarrier = message.Barrier;
            TvInterop.ApplyMediaState(message.SourceUrl, message.StreamUrl, message.StreamTitle,
                message.PlaylistUrl, message.IsLive, message.IsSegmentedVod, message.PlaylistIndex,
                message.Position, message.Paused, message.PoweredOff, message.Shuffle,
                message.Barrier, message.Resume, message.Generation);
        }

        private void ApplyMediaDelta()
        {
            if (_pendingMedia == null)
            {
                return;
            }

            var message = _pendingMedia;
            // The delta carries the host's resolved media identity (SourceUrl/StreamUrl/title/
            // playlist index) plus its own Generation and Barrier. The optimistic Open/Next/Previous
            // only re-ran the game operation and never produced those fields, so Confirm retires the
            // prediction and still applies the authoritative state.
            CoopPredict.Confirm(message.PredictionId, () =>
            {
                _clientGeneration = message.Generation;
                _clientBarrier = message.Barrier;
                TvInterop.ApplyMediaState(message.SourceUrl, message.StreamUrl, message.StreamTitle,
                    message.PlaylistUrl, message.IsLive, message.IsSegmentedVod,
                    message.PlaylistIndex, message.Position, message.Paused, message.PoweredOff,
                    message.Shuffle, message.Barrier, message.Resume, message.Generation);
            });
            _pendingMedia = null;
        }

        private void ApplyPauseDelta()
        {
            if (_pendingPause == null)
            {
                return;
            }

            var message = _pendingPause;
            // Single-bit echo of the same toggle the game already ran locally; no host-computed
            // fields, so retiring the prediction keeps the optimistic value.
            CoopPredict.AckOrApply(message.PredictionId,
                () => TvInterop.ApplyPaused(message.Paused));
            _pendingPause = null;
        }

        private void ApplyPowerDelta()
        {
            if (_pendingPower == null)
            {
                return;
            }

            var message = _pendingPower;
            // Single-bit echo of the same toggle the game already ran locally; no host-computed
            // fields, so retiring the prediction keeps the optimistic value.
            CoopPredict.AckOrApply(message.PredictionId,
                () => TvInterop.ApplyPowered(message.PoweredOff));
            _pendingPower = null;
        }

        private void ApplyShuffleDelta()
        {
            if (_pendingShuffle == null)
            {
                return;
            }

            var message = _pendingShuffle;
            // Single-bit echo of the same toggle the game already ran locally; no host-computed
            // fields, so retiring the prediction keeps the optimistic value.
            CoopPredict.AckOrApply(message.PredictionId,
                () => TvInterop.ApplyShuffle(message.Shuffle));
            _pendingShuffle = null;
        }

        private void ApplySeekDelta()
        {
            if (_pendingSeek == null)
            {
                return;
            }

            var message = _pendingSeek;
            // The host seeks from its own playback clock and returns the resulting absolute
            // Position; the client's optimistic target came from its own (drifting) clock, so
            // Confirm retires the prediction and applies the host position.
            CoopPredict.Confirm(message.PredictionId,
                () => TvInterop.ApplySeek(message.Position));
            _pendingSeek = null;
        }

        private void ApplyBarrierDelta()
        {
            if (_pendingBarrier == null)
            {
                return;
            }

            var message = _pendingBarrier;
            // Barrier is host-assigned state the client never predicts; PublishBarrierDelta always
            // sends Guid.Empty today, so this always applied, and Confirm keeps that guarantee if an
            // id is ever attached.
            CoopPredict.Confirm(message.PredictionId, () =>
            {
                _clientBarrier = message.Barrier;
                TvInterop.ApplyBarrier(message.Barrier, message.Resume);
            });
            _pendingBarrier = null;
        }

        private void ApplyErrorDelta()
        {
            if (_pendingError == null)
            {
                return;
            }

            var message = _pendingError;
            if (message.Generation < _clientGeneration)
            {
                // Stale error superseded by applied state: retire without applying.
                CoopPredict.Ack(message.PredictionId);
                _pendingError = null;
                return;
            }

            // The error carries the host Generation and marks playback the host aborted; the
            // optimistic media run could not produce that, so Confirm retires the prediction and
            // still stops/clears the failed stream.
            CoopPredict.Confirm(message.PredictionId, () =>
            {
                _clientGeneration = Math.Max(_clientGeneration, message.Generation);
                _clientBarrier = false;
                TvInterop.ApplyPlaybackError();
            });
            _pendingError = null;
        }

        private static void ApplyPatches(Harmony harmony)
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TryPatch(harmony, "OnEnable", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "Start", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeVideo", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeToUrl", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(harmony, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(StreamYouTubePostfix)));
            // The game performs every control through its own path; these observe the resulting
            // local change and register one post-hoc prediction. No vanilla input is suppressed.
            TryPatch(harmony, "HandleGlobalInput",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPostfix)));
            TryPatch(harmony, "OnQualityChoice",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPostfix)));
            TryPatch(harmony, "ToggleGlobalPause",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PausePostfix)));
            TryPatch(harmony, "RemoteNext",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(NextPostfix)));
            TryPatch(harmony, "RemotePrev",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PreviousPostfix)));
            TryPatch(harmony, "RemoteShuffle",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ShufflePostfix)));
            TryPatch(harmony, "RemotePower",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PowerPostfix)));
            TryPatch(harmony, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPostfix)));
        }

        private static void TryPatch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            try
            {
                var original = AccessTools.Method(TvInterop.OptionalControllerType, method);
                if (original == null)
                {
                    CoopLog.Warn("TV client patch target missing: " + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception exception)
            {
                CoopLog.Warn("TV client patch failed " + method + ": " + exception.Message);
            }
        }

        private static void LocalPreparedPrefix(object __instance, object source)
        {
            _active?.BeforeLocalPrepared(__instance, source);
        }

        private static void LocalPreparedPostfix(object __instance, object source)
        {
            _active?.AfterLocalPrepared(__instance, source);
        }

        private static void ControllerLifecyclePostfix(object __instance)
        {
            TvInterop.RegisterController(__instance as Component);
            _active?.OnControllerLifecycle();
        }

        private static void ChangeToUrlPostfix(string url, string title)
        {
            TvInterop.ChangeToUrlPostfix(url);
            _active?.OnControllerLifecycle();
        }

        private static void StreamYouTubePostfix(string url)
        {
            TvInterop.StreamYouTubePostfix(url);
            _active?.OnControllerLifecycle();
        }

        private void BeforeLocalPrepared(object instance, object source)
        {
            if (_clientBarrier && TvInterop.IsMainPreparedPlayer(instance, source))
            {
                TvInterop.SetSharedPausedForPrepare(false);
            }
        }

        private void AfterLocalPrepared(object instance, object source)
        {
            if (!TvInterop.IsMainPreparedPlayer(instance, source))
            {
                return;
            }

            TvInterop.ApplyPreparedPosition(instance);
            ApplyLatestState();
            if (_clientBarrier)
            {
                TvInterop.PausePreparedStream(instance);
            }
        }

        private static bool CanPredict()
        {
            return _active != null && !_active._shutdown && _active._joined
                && _active._context != null && _active._context.InGame && TvInterop.Present;
        }

        private static void SendIntent(Guid predictionId, byte op, string url = null,
            string title = null, double value = 0)
        {
            _active._context.Send(1, new TvOpMessage
            {
                PredictionId = predictionId,
                Op = op,
                Url = url,
                Title = title,
                Value = value,
            });
        }

        /// <summary>Captures the pending URL before the game clears it, so the Open the quality
        /// dialog launches can be observed and forwarded with its original (pre-resolution) URL.</summary>
        private struct QualityState
        {
            public bool Armed;
            public string Url;
            public TvInterop.PlaybackUndoState Prior;
        }

        private static void QualityPrefix(out QualityState __state)
        {
            __state = default;
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return;
            }

            var url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            __state.Armed = true;
            __state.Url = url;
            __state.Prior = TvInterop.CapturePlaybackState();
        }

        private static void QualityPostfix(QualityState __state)
        {
            if (!__state.Armed || !CanPredict() || TvInterop.ApplyingRemote)
            {
                return;
            }

            ForwardMedia(Open, __state.Prior, __state.Url, "Shared stream");
        }

        /// <summary>Observed keyboard controls. The game itself handles Alt+P/=/−/]/[ through its
        /// own <c>HandleGlobalInput</c>; this hook only forwards the changes it produces. Alt+S and
        /// Alt+O drive shuffle/power, which vanilla has no keys for, so their game methods are
        /// invoked here and the resulting change is observed like any other.</summary>
        private struct GlobalInputState
        {
            public bool Next;
            public bool Previous;
            public bool Shuffle;
            public bool Power;
            public TvInterop.PlaybackUndoState Prior;
        }

        // The seek keys are held, and the game advances the video position on its own cadence.
        // Track the position the game reports so each observed jump is forwarded once; the value is
        // read after the game has applied the previous frame's jump.
        private static double _seekObservedPosition;
        private static bool _seekHeld;

        private static void GlobalInputPrefix(out GlobalInputState __state)
        {
            __state = default;
            var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            var now = TvInterop.Position;
            var seekHeld = alt
                && (Input.GetKey(KeyCode.RightBracket) || Input.GetKey(KeyCode.LeftBracket));
            if (CanPredict() && !TvInterop.ApplyingRemote && _seekHeld
                && Math.Abs(now - _seekObservedPosition) >= 1.0)
            {
                ForwardSeek(_seekObservedPosition, now - _seekObservedPosition);
            }

            _seekObservedPosition = now;
            _seekHeld = seekHeld;

            if (!CanPredict() || TvInterop.ApplyingRemote || !alt)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Equals))
            {
                __state.Next = true;
            }
            else if (Input.GetKeyDown(KeyCode.Minus))
            {
                __state.Previous = true;
            }
            else if (Input.GetKeyDown(KeyCode.S))
            {
                __state.Shuffle = true;
            }
            else if (Input.GetKeyDown(KeyCode.O))
            {
                __state.Power = true;
            }
            else
            {
                return;
            }

            __state.Prior = TvInterop.CapturePlaybackState();
        }

        private static void GlobalInputPostfix(GlobalInputState __state)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return;
            }

            if (__state.Next)
            {
                ForwardMedia(Next, __state.Prior, null, null);
            }
            else if (__state.Previous)
            {
                ForwardMedia(Previous, __state.Prior, null, null);
            }
            else if (__state.Shuffle)
            {
                ForwardSelfApplied(Shuffle, __state.Prior);
            }
            else if (__state.Power)
            {
                ForwardSelfApplied(Power, __state.Prior);
            }
        }

        /// <summary>Captures the playback state before a control the game performs itself, so the
        /// postfix can register one observed prediction and replay it if the host rejects it.</summary>
        private static void CapturePrefix(out TvInterop.PlaybackUndoState __state)
            => __state = TvInterop.CapturePlaybackState();

        private static void PausePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote || TvInterop.Paused == __state.Paused)
            {
                return;
            }

            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Pause),
                () => TvInterop.ApplyPaused(!__state.Paused),
                () => TvInterop.ApplyPaused(__state.Paused));
        }

        private static void PowerPostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote
                || TvInterop.PoweredOff == __state.PoweredOff)
            {
                return;
            }

            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Power),
                () => TvInterop.ApplyPowered(!__state.PoweredOff),
                () => TvInterop.ApplyPowered(__state.PoweredOff));
        }

        private static void ShufflePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote || TvInterop.Shuffle == __state.Shuffle)
            {
                return;
            }

            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Shuffle),
                () => TvInterop.ApplyShuffle(!__state.Shuffle),
                () => TvInterop.ApplyShuffle(__state.Shuffle));
        }

        private static void NextPostfix(TvInterop.PlaybackUndoState __state)
            => ForwardMedia(Next, __state, null, null);

        private static void PreviousPostfix(TvInterop.PlaybackUndoState __state)
            => ForwardMedia(Previous, __state, null, null);

        /// <summary>Records one observed media change (next/previous/open) the game already applied
        /// and sends it; the apply/undo closures replay it through the game path on a rejection.</summary>
        private static void ForwardMedia(byte op, TvInterop.PlaybackUndoState prior, string url,
            string title)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return;
            }

            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, op, url, title),
                () => TvInterop.ApplyOperation(op, url, title, 0),
                () => TvInterop.RestorePlaybackState(prior));
        }

        /// <summary>Shuffle/power have no vanilla key binding: drive the game's own control for the
        /// observed hotkey, then register the change it made. The game call runs under
        /// <c>ApplyingRemote</c>, so the control postfix does not also forward it.</summary>
        private static void ForwardSelfApplied(byte op, TvInterop.PlaybackUndoState prior)
        {
            if (!TvInterop.ApplyOperation(op, null, null, 0))
            {
                return;
            }

            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, op),
                () => TvInterop.ApplyOperation(op, null, null, 0),
                () => TvInterop.RestorePlaybackState(prior));
        }

        private static void ForwardSeek(double fromPosition, double delta)
        {
            var target = Math.Max(0.0,
                Math.Min(TvInterop.MaxPositionSeconds, fromPosition + delta));
            CoopPredict.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Seek, value: delta),
                () => TvInterop.ApplySeek(target),
                () => TvInterop.ApplySeek(fromPosition));
        }
    }
}
