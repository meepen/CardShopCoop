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
        private CoopRuntimeContext _context;
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

            _context = RuntimeContext;
            var registered = false;
            try
            {
                ResetSessionState();
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.tv.client.runtime");
                ApplyPatches(_harmony);
                OnControllerLifecycle();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("TV client initialization failed: " + exception);
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
        private void HandleBaseline(MessageContext context, TvBaselineMessage message)
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
        private void HandleMediaDelta(MessageContext context, TvMediaDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            if (message.Generation < _clientGeneration
                || _pendingMedia != null && _pendingMedia.Generation > message.Generation)
            {
                PredictionApi.ConfirmSuperseded(message.PredictionId);
                return;
            }

            _pendingMedia = message;
            if (_pendingError != null && _pendingError.Generation < message.Generation)
            {
                PredictionApi.ConfirmSuperseded(_pendingError.PredictionId);
                _pendingError = null;
            }
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPauseDeltaMessage))]
        private void HandlePauseDelta(MessageContext context, TvPauseDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            _pendingPause = message;
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPowerDeltaMessage))]
        private void HandlePowerDelta(MessageContext context, TvPowerDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            _pendingPower = message;
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvShuffleDeltaMessage))]
        private void HandleShuffleDelta(MessageContext context, TvShuffleDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            _pendingShuffle = message;
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvSeekDeltaMessage))]
        private void HandleSeekDelta(MessageContext context, TvSeekDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            _pendingSeek = message;
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvBarrierDeltaMessage))]
        private void HandleBarrierDelta(MessageContext context, TvBarrierDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            _pendingBarrier = message;
            ReconcileWithoutController(message.PredictionId);
            ApplyLatestState();
        }

        [MessageHandler(typeof(TvPlaybackErrorDeltaMessage))]
        private void HandlePlaybackError(MessageContext context, TvPlaybackErrorDeltaMessage message)
        {
            if (_shutdown || !TvInterop.Present)
            {
                return;
            }

            if (message.Generation < _clientGeneration
                || _pendingMedia != null && _pendingMedia.Generation > message.Generation)
            {
                PredictionApi.ConfirmSuperseded(message.PredictionId);
                return;
            }

            _pendingError = message;
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
                PredictionApi.ApplyAuthoritative(predictionId, () => { });
            }
        }

        private void ApplyLatestState()
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame()
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
                PredictionApi.ConfirmSuperseded(_pendingMedia.PredictionId);
                _pendingMedia = null;
            }

            if (_pendingError != null && _pendingError.Generation < _clientGeneration)
            {
                PredictionApi.ConfirmSuperseded(_pendingError.PredictionId);
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
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
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
            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
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
                PredictionApi.ConfirmSuperseded(message.PredictionId);
                _pendingError = null;
                return;
            }

            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
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
            TryPatch(harmony, "HandleGlobalInput",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientGlobalInputPrefix)), null);
            TryPatch(harmony, "ChangeToUrl",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientChangeToUrlPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(harmony, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(StreamYouTubePostfix)));
            TryPatch(harmony, "OnQualityChoice",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientQualityChoicePrefix)), null);
            TryPatch(harmony, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPostfix)));
            TryPatch(harmony, "RemotePlayPause",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientPausePrefix)), null);
            TryPatch(harmony, "RemoteNext",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientNextPrefix)), null);
            TryPatch(harmony, "RemotePrev",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientPreviousPrefix)), null);
            TryPatch(harmony, "RemoteShuffle",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientShufflePrefix)), null);
            TryPatch(harmony, "RemotePower",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ClientPowerPrefix)), null);
        }

        private static void TryPatch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            try
            {
                var original = AccessTools.Method(TvInterop.OptionalControllerType, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("TV client patch target missing: " + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("TV client patch failed " + method + ": " + exception.Message);
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

        private static bool ClientChangeToUrlPrefix(string url, string title)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return true;
            }

            PredictMedia(Open, url, title);
            return false;
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
                && _active._context != null && _active._context.InGame() && TvInterop.Present;
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

        private static void PredictMedia(byte op, string url = null, string title = null)
        {
            var prior = TvInterop.CapturePlaybackState();
            PredictionApi.Predict(
                PredictionScope,
                predictionId => SendIntent(predictionId, op, url, title),
                () => TvInterop.ApplyOperation(op, url, title, 0),
                () => TvInterop.RestorePlaybackState(prior));
        }

        private static bool ClientControl(byte op)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return true;
            }

            switch (op)
            {
                case Pause:
                    PredictPause();
                    break;
                case Power:
                    PredictPower();
                    break;
                case Shuffle:
                    PredictShuffle();
                    break;
                case Next:
                case Previous:
                    PredictMedia(op);
                    break;
                default:
                    throw new InvalidOperationException("Unknown client TV operation " + op + ".");
            }

            return false;
        }

        private static void PredictPause()
        {
            var prior = TvInterop.Paused;
            var predicted = !prior;
            PredictionApi.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Pause),
                () => TvInterop.ApplyPaused(predicted),
                () => TvInterop.ApplyPaused(prior));
        }

        private static void PredictPower()
        {
            var prior = TvInterop.PoweredOff;
            var predicted = !prior;
            PredictionApi.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Power),
                () => TvInterop.ApplyPowered(predicted),
                () => TvInterop.ApplyPowered(prior));
        }

        private static void PredictShuffle()
        {
            var prior = TvInterop.Shuffle;
            var predicted = !prior;
            PredictionApi.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Shuffle),
                () => TvInterop.ApplyShuffle(predicted),
                () => TvInterop.ApplyShuffle(prior));
        }

        private static void PredictSeek(double amount)
        {
            var prior = TvInterop.Position;
            var predicted = Math.Max(0.0, Math.Min(TvInterop.MaxPositionSeconds, prior + amount));
            PredictionApi.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, Seek, value: amount),
                () => TvInterop.ApplySeek(predicted),
                () => TvInterop.ApplySeek(prior));
        }

        private static bool ClientPausePrefix() => ClientControl(Pause);
        private static bool ClientNextPrefix() => ClientControl(Next);
        private static bool ClientPreviousPrefix() => ClientControl(Previous);
        private static bool ClientShufflePrefix() => ClientControl(Shuffle);
        private static bool ClientPowerPrefix() => ClientControl(Power);

        private static bool ClientQualityChoicePrefix(bool download)
        {
            if (!CanPredict() || TvInterop.ApplyingRemote)
            {
                return true;
            }

            var url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url) || !TvInterop.FinishQualitySelection())
            {
                return true;
            }

            PredictMedia(Open, url, "Shared stream");
            return false;
        }

        private static bool ClientGlobalInputPrefix()
        {
            if (!CanPredict())
            {
                return true;
            }

            var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt)
            {
                return true;
            }

            if (Input.GetKeyDown(KeyCode.Y))
            {
                return true;
            }

            if (Input.GetKeyDown(KeyCode.P))
            {
                PredictPause();
            }
            else if (Input.GetKeyDown(KeyCode.Equals))
            {
                PredictMedia(Next);
            }
            else if (Input.GetKeyDown(KeyCode.Minus))
            {
                PredictMedia(Previous);
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                PredictSeek(5);
            }
            else if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                PredictSeek(-5);
            }
            else if (Input.GetKeyDown(KeyCode.S))
            {
                PredictShuffle();
            }
            else if (Input.GetKeyDown(KeyCode.O))
            {
                PredictPower();
            }
            else
            {
                return false;
            }

            return false;
        }
    }
}
