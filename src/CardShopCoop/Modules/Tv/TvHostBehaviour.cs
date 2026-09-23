using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Tv
{
    /// <summary>Host-authoritative TV controls and event-driven playback deltas.</summary>
    [ServerBehaviour]
    public sealed class TvHostBehaviour : CoopBehaviour
    {
        private const byte Next = 1;
        private const byte Previous = 2;
        private const byte Pause = 3;
        private const byte Power = 4;
        private const byte Shuffle = 5;
        private const byte Seek = 6;
        private const byte Open = 7;

        private static TvHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private float _lastSeekInputTime;
        private bool _hasHostState;
        private string _lastUrl;
        private string _lastSource;
        private string _lastTitle;
        private string _lastPlaylist;
        private bool _lastLive;
        private bool _lastSegmented;
        private bool _lastPaused;
        private bool _lastPowered;
        private bool _lastShuffle;
        private int _lastPlaylistIndex;
        private double _lastPosition;
        private bool _barrier;
        private bool _resumePulse;
        private int _generation;
        private bool _hasPendingMediaOperation;
        private byte _pendingMediaOperation;
        private Guid _pendingMediaPredictionId;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            var registered = false;
            try
            {
                ResetSessionState();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tv.host.runtime");
                ApplyPatches(_harmony);
                OnControllerLifecycle();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("TV host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
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
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null
                || (connection.State != ConnectionState.Transferring
                    && connection.State != ConnectionState.FullyJoined))
            {
                return;
            }

            if (TvInterop.Present)
            {
                var activePlayback = TvInterop.IsPlayingStream || TvInterop.IsFetching
                    || !string.IsNullOrWhiteSpace(TvInterop.PendingSourceUrl);
                if (activePlayback && string.IsNullOrWhiteSpace(TvInterop.SourceUrl))
                {
                    return;
                }

                CaptureHostState(false);
            }

            _context.Send(connection.Id, BuildBaseline());
        }

        [MessageHandler(typeof(TvOpMessage))]
        private void HandleOperation(MessageContext context, TvOpMessage message)
        {
            if (_shutdown || message == null || !IsJoinedClient(context) || !TvInterop.Present
                || message.Op < Next || message.Op > Open)
            {
                return;
            }

            var mediaOperation = message.Op == Next || message.Op == Previous || message.Op == Open;
            if (mediaOperation)
            {
                if (!TryBeginPendingMediaOperation(message.Op, message.PredictionId))
                {
                    PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                    return;
                }
            }

            var pendingOperation = mediaOperation ? _pendingMediaOperation : (byte)0;
            var pendingPredictionId = mediaOperation ? _pendingMediaPredictionId : Guid.Empty;
            var applied = TvInterop.ApplyOperation(message.Op, message.Url, message.Title, message.Value);
            if (!applied)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, message.PredictionId);
                if (mediaOperation)
                {
                    ClearPendingMediaOperation(pendingOperation, pendingPredictionId);
                }
                return;
            }

            if (mediaOperation)
            {
                return;
            }

            PublishScalarDelta(message.Op, message.PredictionId);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
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
            _lastSeekInputTime = -0.25f;
            _hasHostState = false;
            _lastUrl = null;
            _lastSource = null;
            _lastTitle = null;
            _lastPlaylist = null;
            _lastLive = false;
            _lastSegmented = false;
            _lastPaused = false;
            _lastPowered = false;
            _lastShuffle = false;
            _lastPlaylistIndex = 0;
            _lastPosition = 0.0;
            _barrier = false;
            _resumePulse = false;
            _generation = 0;
            ClearPendingMediaOperation();
        }

        private static bool IsJoinedClient(MessageContext context)
        {
            return context?.Connection != null && context.Connection.Id != 1
                && (context.Connection.State == ConnectionState.Transferring
                    || context.Connection.State == ConnectionState.FullyJoined);
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TvInterop.NotifySceneLoaded();
            _hasHostState = false;
            _lastSource = null;
            _lastUrl = null;
            _lastTitle = null;
            _lastPlaylist = null;
            _barrier = false;
            _resumePulse = false;
            _generation++;
            ClearPendingMediaOperation();
            OnControllerLifecycle();
        }

        private void OnControllerLifecycle()
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TvInterop.AttachVideoErrorHandler(OnHostVideoError);
            if (CaptureHostState(false) && !string.IsNullOrEmpty(_lastSource))
            {
                PublishMediaDelta(TvMediaDeltaMessage.Open, Guid.Empty, true);
            }
        }

        private bool CaptureHostState(bool startBarrier)
        {
            var playing = TvInterop.IsPlayingStream;
            var fetching = TvInterop.IsFetching;
            var powered = TvInterop.PoweredOff;
            var streaming = playing || fetching || !string.IsNullOrWhiteSpace(TvInterop.PendingSourceUrl);
            if (playing)
            {
                TvInterop.ClearPendingSourceUrl();
            }

            var source = !string.IsNullOrWhiteSpace(TvInterop.SourceUrl)
                ? TvInterop.SourceUrl : (streaming ? null : _lastSource);
            if (streaming && string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            var url = streaming ? TvInterop.StreamUrl : _lastUrl;
            var title = streaming ? TvInterop.StreamTitle : _lastTitle;
            var playlist = streaming ? TvInterop.PlaylistUrlValue : _lastPlaylist;
            var live = streaming && TvInterop.IsLive;
            var segmented = streaming && TvInterop.IsSegmentedVod;
            var paused = streaming ? TvInterop.Paused : _lastPaused;
            var shuffle = streaming ? TvInterop.Shuffle : _lastShuffle;
            var playlistIndex = streaming ? TvInterop.PlaylistIndexValue : _lastPlaylistIndex;
            var position = TvInterop.Position;
            var mediaReplaced = !string.Equals(source, _lastSource, StringComparison.Ordinal)
                || !string.Equals(playlist, _lastPlaylist, StringComparison.Ordinal)
                || (streaming && playlistIndex != _lastPlaylistIndex);
            var changed = !_hasHostState || mediaReplaced || url != _lastUrl || title != _lastTitle
                || live != _lastLive || segmented != _lastSegmented || paused != _lastPaused
                || powered != _lastPowered || shuffle != _lastShuffle
                || playlistIndex != _lastPlaylistIndex || position != _lastPosition;

            if (mediaReplaced)
            {
                _generation++;
                if (startBarrier && !string.IsNullOrEmpty(source))
                {
                    _barrier = true;
                    TvInterop.SetSharedPaused(true);
                }
            }

            _lastSource = source;
            _lastUrl = url;
            _lastTitle = title;
            _lastPlaylist = playlist;
            _lastLive = live;
            _lastSegmented = segmented;
            _lastPaused = paused;
            _lastPowered = powered;
            _lastShuffle = shuffle;
            _lastPlaylistIndex = playlistIndex;
            _lastPosition = position;
            _hasHostState = streaming || _barrier || powered || !string.IsNullOrEmpty(source);
            return changed;
        }

        private void PublishMediaDelta(byte operation, Guid predictionId, bool force)
        {
            if (_shutdown || _context == null || !_context.InGame() || !TvInterop.Present)
            {
                return;
            }

            var changed = CaptureHostState(operation != TvMediaDeltaMessage.Playlist);
            if (!changed && !force)
            {
                return;
            }

            if (!force && string.IsNullOrEmpty(_lastSource))
            {
                return;
            }

            _context.Broadcast(new TvMediaDeltaMessage
            {
                PredictionId = predictionId,
                Operation = operation,
                SourceUrl = _lastSource,
                StreamUrl = _lastUrl,
                StreamTitle = _lastTitle,
                PlaylistUrl = _lastPlaylist,
                IsLive = _lastLive,
                IsSegmentedVod = _lastSegmented,
                PlaylistIndex = _lastPlaylistIndex,
                Position = _lastPosition,
                Paused = _lastPaused,
                PoweredOff = _lastPowered,
                Shuffle = _lastShuffle,
                Barrier = _barrier,
                Resume = _resumePulse,
                Generation = _generation,
            });
            _resumePulse = false;
            ClearPendingMediaOperation(operation, predictionId);
        }

        private void PublishScalarDelta(byte operation, Guid predictionId)
        {
            if (_shutdown || _context == null || !_context.InGame() || !TvInterop.Present)
            {
                return;
            }

            CaptureHostState(false);
            switch (operation)
            {
                case Pause:
                    _context.Broadcast(new TvPauseDeltaMessage
                    {
                        PredictionId = predictionId,
                        Paused = _lastPaused,
                    });
                    break;
                case Power:
                    _context.Broadcast(new TvPowerDeltaMessage
                    {
                        PredictionId = predictionId,
                        PoweredOff = _lastPowered,
                    });
                    break;
                case Shuffle:
                    _context.Broadcast(new TvShuffleDeltaMessage
                    {
                        PredictionId = predictionId,
                        Shuffle = _lastShuffle,
                    });
                    break;
                case Seek:
                    _context.Broadcast(new TvSeekDeltaMessage
                    {
                        PredictionId = predictionId,
                        Position = _lastPosition,
                    });
                    break;
                default:
                    throw new InvalidOperationException("Unknown scalar TV operation " + operation + ".");
            }
        }

        private void PublishBarrierDelta(Guid predictionId)
        {
            if (_shutdown || _context == null || !_context.InGame() || !TvInterop.Present)
            {
                return;
            }

            _context.Broadcast(new TvBarrierDeltaMessage
            {
                PredictionId = predictionId,
                Barrier = _barrier,
                Resume = _resumePulse,
            });
            _resumePulse = false;
        }

        private TvBaselineMessage BuildBaseline()
        {
            return new TvBaselineMessage
            {
                SourceUrl = _lastSource,
                StreamUrl = _lastUrl,
                StreamTitle = _lastTitle,
                PlaylistUrl = _lastPlaylist,
                IsLive = _lastLive,
                IsSegmentedVod = _lastSegmented,
                PlaylistIndex = _lastPlaylistIndex,
                Position = _lastPosition,
                Paused = _lastPaused,
                PoweredOff = _lastPowered,
                Shuffle = _lastShuffle,
                Barrier = _barrier,
                Resume = _resumePulse,
                Generation = _generation,
            };
        }

        private bool TryBeginPendingMediaOperation(byte operation, Guid predictionId)
        {
            if (_hasPendingMediaOperation)
            {
                return false;
            }

            _hasPendingMediaOperation = true;
            _pendingMediaOperation = operation == Next ? TvMediaDeltaMessage.Next
                : operation == Previous ? TvMediaDeltaMessage.Previous : TvMediaDeltaMessage.Open;
            _pendingMediaPredictionId = predictionId;
            return true;
        }

        private void ClearPendingMediaOperation(byte operation, Guid predictionId)
        {
            if (!_hasPendingMediaOperation || _pendingMediaOperation != operation
                || _pendingMediaPredictionId != predictionId)
            {
                return;
            }

            _hasPendingMediaOperation = false;
            _pendingMediaOperation = 0;
            _pendingMediaPredictionId = Guid.Empty;
        }

        private void ClearPendingMediaOperation()
        {
            _hasPendingMediaOperation = false;
            _pendingMediaOperation = 0;
            _pendingMediaPredictionId = Guid.Empty;
        }

        private static void ApplyPatches(Harmony harmony)
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TryPatch(harmony, "OnEnable", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "Start", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeVideo", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "HandleGlobalInput", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(HostGlobalInputPostfix)));
            TryPatch(harmony, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(StreamYouTubePostfix)));
            TryPatch(harmony, "ChangeToUrl", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(harmony, "PlayPlaylistItem", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PlaylistItemPostfix)));
            TryPatch(harmony, "ToggleGlobalPause", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PausePostfix)));
            TryPatch(harmony, "RemotePower", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PowerPostfix)));
            TryPatch(harmony, "RemoteShuffle", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ShufflePostfix)));
            TryPatch(harmony, "OnDisable", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerDisablePostfix)));
            TryPatch(harmony, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(LocalPreparedPostfix)));
        }

        private static void TryPatch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            try
            {
                var original = AccessTools.Method(TvInterop.OptionalControllerType, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("TV host patch target missing: " + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("TV host patch failed " + method + ": " + exception.Message);
            }
        }

        private static void StreamYouTubePostfix(string url)
        {
            TvInterop.StreamYouTubePostfix(url);
            _active?.OnMediaSourceChanged();
        }

        private static void ChangeToUrlPostfix(string url, string title)
        {
            TvInterop.ChangeToUrlPostfix(url);
            _active?.OnMediaSourceChanged();
        }

        private static void PlaylistItemPostfix()
        {
            if (_active == null || TvInterop.ApplyingRemote)
            {
                return;
            }

            var operation = _active._hasPendingMediaOperation
                ? _active._pendingMediaOperation : TvMediaDeltaMessage.Playlist;
            var predictionId = _active._hasPendingMediaOperation
                ? _active._pendingMediaPredictionId : Guid.Empty;
            _active.PublishMediaDelta(operation, predictionId, true);
        }

        private static void PausePostfix() => _active?.OnScalarChanged(Pause);
        private static void PowerPostfix() => _active?.OnScalarChanged(Power);
        private static void ShufflePostfix() => _active?.OnScalarChanged(Shuffle);

        private void OnMediaSourceChanged()
        {
            var operation = _hasPendingMediaOperation ? _pendingMediaOperation : TvMediaDeltaMessage.Open;
            var predictionId = _hasPendingMediaOperation ? _pendingMediaPredictionId : Guid.Empty;
            PublishMediaDelta(operation, predictionId, true);
        }

        private void OnScalarChanged(byte operation)
        {
            if (TvInterop.ApplyingRemote)
            {
                return;
            }

            PublishScalarDelta(operation, Guid.Empty);
        }

        private static void ControllerLifecyclePostfix(object __instance)
        {
            TvInterop.RegisterController(__instance as Component);
            _active?.OnControllerLifecycle();
        }

        private static void ControllerDisablePostfix()
        {
            if (_active != null && _active._barrier && !_active._shutdown)
            {
                _active.AbortBarrier("host TV controller playback was canceled");
                _active.PublishBarrierDelta(Guid.Empty);
            }
        }

        private static void HostGlobalInputPostfix()
        {
            var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt || (!Input.GetKey(KeyCode.RightBracket) && !Input.GetKey(KeyCode.LeftBracket)
                && Mathf.Abs(Input.mouseScrollDelta.y) <= 0.0001f))
            {
                return;
            }

            if (!TvInterop.IsPlayingStream && !TvInterop.IsFetching)
            {
                return;
            }

            if (_active == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            if (!float.IsNaN(now) && !float.IsInfinity(now)
                && now - _active._lastSeekInputTime >= 0.25f)
            {
                _active._lastSeekInputTime = now;
                _active.PublishScalarDelta(Seek, Guid.Empty);
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

        private void BeforeLocalPrepared(object instance, object source)
        {
            if (_barrier && TvInterop.IsMainPreparedPlayer(instance, source))
            {
                TvInterop.SetSharedPausedForPrepare(false);
            }
        }

        private void AfterLocalPrepared(object instance, object source)
        {
            if (!_barrier || !TvInterop.IsMainPreparedPlayer(instance, source))
            {
                return;
            }

            TvInterop.PausePreparedStream(instance);
            CompleteBarrier();
            PublishBarrierDelta(Guid.Empty);
        }

        private void OnHostVideoError(string message)
        {
            if (_shutdown)
            {
                return;
            }

            var pendingOperation = _pendingMediaOperation;
            var pendingPredictionId = _pendingMediaPredictionId;
            CoopPlugin.Log.LogError("Host TV VideoPlayer failed: " + (message ?? "unknown error"));
            TvInterop.AbortSharedPlayback();
            AbortBarrier("host TV playback failed");
            _lastSource = null;
            _lastUrl = null;
            _lastTitle = null;
            _lastPlaylist = null;
            _lastLive = false;
            _lastSegmented = false;
            _lastPaused = false;
            _lastPowered = TvInterop.PoweredOff;
            _lastShuffle = TvInterop.Shuffle;
            _lastPlaylistIndex = 0;
            _lastPosition = 0.0;
            _hasHostState = _lastPowered;
            if (_context != null && _context.InGame() && TvInterop.Present)
            {
                _context.Broadcast(new TvPlaybackErrorDeltaMessage
                {
                    PredictionId = _hasPendingMediaOperation ? pendingPredictionId : Guid.Empty,
                    Generation = _generation,
                });
                ClearPendingMediaOperation(pendingOperation, pendingPredictionId);
            }
        }

        private void CompleteBarrier()
        {
            _barrier = false;
            _resumePulse = true;
            TvInterop.ResumePlayback();
        }

        private void AbortBarrier(string reason)
        {
            _barrier = false;
            _resumePulse = true;
            TvInterop.ResumePlayback();
            CoopPlugin.Log.LogWarning("TV load barrier aborted: " + reason);
        }
    }
}
