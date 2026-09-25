using System;
using System.Collections;
using System.Reflection;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.Modules.Tv
{
    /// <summary>
    /// Optional reflection bridge to RTCGO Custom TV. The TV assembly is supplied by an optional
    /// prefab pack, so this file deliberately contains no compile-time TV type reference.
    /// </summary>
    internal static class TvInterop
    {
        internal enum FeatureAvailability
        {
            Absent,
            Ready,
            Drifted,
        }

        private const string AssemblyName = "RTCGOCustomScripts";
        internal const double MaxPositionSeconds = 86400.0;
        internal const double MaxSeekDeltaSeconds = 300.0;
        private static readonly Type ControllerType =
            CoopReflection.OptionalType("Scripts.VideoPlayerController", AssemblyName);
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo Enrolled = Field("enrolledTVs");
        private static readonly FieldInfo SharedVideoIndex = Field("sharedVideoIndex");
        private static readonly FieldInfo StreamUrlField = Field("activeStreamUrl");
        private static readonly FieldInfo StreamTitleField = Field("activeStreamTitle");
        private static readonly FieldInfo PlaylistUrl = Field("activePlaylistUrl");
        private static readonly FieldInfo PlaylistIndex = Field("activePlaylistIndex");
        private static readonly FieldInfo Live = Field("isLiveStream");
        private static readonly FieldInfo Segmented = Field("isSegmentedVod");
        private static readonly FieldInfo Playing = Field("isPlayingStream");
        private static readonly FieldInfo Fetching = Field("isFetchingUrl");
        private static readonly FieldInfo PausedField = Field("isSharedPaused");
        private static readonly FieldInfo Powered = Field("isPoweredOff");
        private static readonly FieldInfo ShuffleField = Field("isShuffleMode");
        private static readonly FieldInfo LastTime = Field("lastKnownMasterTime");
        private static readonly FieldInfo PendingUrlField = Field("pendingYouTubeUrl");
        private static readonly FieldInfo QualityOpen = Field("isQualityChoiceOpen");
        private static readonly FieldInfo QualityRoot = Field("qualityChoiceRoot");
        private static readonly FieldInfo VideoPlayerField = Field("m_VideoPlayer");
        private static readonly EventInfo VideoErrorReceived = VideoPlayerField?.FieldType.GetEvent(
            "errorReceived", Flags);
        private static readonly MethodInfo ChangeUrl = Method("ChangeToUrl", typeof(string), typeof(string));
        private static readonly MethodInfo PlayPlaylistItem = Method("PlayPlaylistItem", typeof(int));
        private static readonly MethodInfo Stream = Method("StreamYouTube", typeof(string));
        private static readonly MethodInfo QualityChoice = Method("OnQualityChoice", typeof(bool));
        private static readonly MethodInfo ChangeVideo = Method("ChangeVideo", typeof(int));
        private static readonly MethodInfo BroadcastChange = Method("BroadcastChange", typeof(int));
        private static readonly MethodInfo TogglePause = Method("ToggleGlobalPause");
        private static readonly MethodInfo RemotePower = Method("RemotePower");
        private static readonly MethodInfo Unfreeze = Method("UnfreezeGame");
        private static readonly MethodInfo LifecycleEnable = Method("OnEnable");
        private static readonly MethodInfo LifecycleStart = Method("Start");
        private static readonly MethodInfo LifecycleInput = Method("HandleGlobalInput");
        private static readonly MethodInfo LifecycleQuality = Method("OnQualityChoice");
        private static readonly MethodInfo LifecyclePrepared = Method("OnLocalPrepared");
        private static readonly MethodInfo RemotePlayPause = Method("RemotePlayPause");
        private static readonly MethodInfo RemoteNext = Method("RemoteNext");
        private static readonly MethodInfo RemotePrev = Method("RemotePrev");
        private static readonly MethodInfo RemoteShuffle = Method("RemoteShuffle");
        private static readonly FeatureAvailability AvailabilityState = ValidateSurface();
        private static bool _logged;
        private static bool _availabilityLogged;
        private static Component _master;
        private static string _pendingSourceUrl;
        private static string _requestedSourceUrl;
        private static string _launchedSource;
        private static int _launchedPlaylistIndex;
        private static bool _streamLaunchPending;
        private static double _pendingPosition;
        private static bool _hasPendingPosition;
        private static int _pendingPositionGeneration;
        private static int _playbackGeneration;
        private static bool _barrierState;
        private static object _attachedVideoPlayer;
        private static Delegate _videoErrorDelegate;
        private static Action<string> _videoErrorHandler;

        internal static Type OptionalControllerType => ControllerType;
        internal static bool ApplyingRemote;

        internal static FeatureAvailability Availability
        {
            get
            {
                LogAvailability();
                return AvailabilityState;
            }
        }

        internal static string UnavailableReason
        {
            get
            {
                if (AvailabilityState == FeatureAvailability.Absent)
                {
                    return "RTCGO Custom TV is not installed";
                }

                return AvailabilityState == FeatureAvailability.Drifted
                    ? "RTCGO Custom TV reflection surface changed"
                    : null;
            }
        }

        internal static bool Present
        {
            get
            {
                var present = Availability == FeatureAvailability.Ready;
                if (present && !_logged)
                {
                    _logged = true;
                    CoopLog.Info("RTCGO Custom TV detected - shared playback is available");
                }
                return present;
            }
        }

        private static FieldInfo Field(string name) => CoopReflection.OptionalField(ControllerType, name);
        private static MethodInfo Method(string name, params Type[] arguments)
            => CoopReflection.OptionalMethod(ControllerType, name, arguments);

        private static FeatureAvailability ValidateSurface()
        {
            if (ControllerType == null)
            {
                return FeatureAvailability.Absent;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(ControllerType))
            {
                return FeatureAvailability.Drifted;
            }

            var fields = new[]
            {
                Enrolled, SharedVideoIndex, StreamUrlField, StreamTitleField, PlaylistUrl,
                PlaylistIndex, Live, Segmented, Playing, Fetching, PausedField, Powered,
                ShuffleField, LastTime, PendingUrlField, QualityOpen, QualityRoot,
            };
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i] == null)
                {
                    return FeatureAvailability.Drifted;
                }
            }

            var methods = new[]
            {
                ChangeUrl, PlayPlaylistItem, Stream, QualityChoice, ChangeVideo,
                BroadcastChange, TogglePause, RemotePower, Unfreeze, LifecycleEnable,
                LifecycleStart, LifecycleInput, LifecycleQuality, LifecyclePrepared,
                RemotePlayPause, RemoteNext, RemotePrev, RemoteShuffle,
            };
            for (var i = 0; i < methods.Length; i++)
            {
                if (methods[i] == null)
                {
                    return FeatureAvailability.Drifted;
                }
            }

            if (!Enrolled.IsStatic)
            {
                return FeatureAvailability.Drifted;
            }

            if (!typeof(IEnumerator).IsAssignableFrom(Stream.ReturnType))
            {
                return FeatureAvailability.Drifted;
            }

            if (VideoPlayerField == null || VideoErrorReceived == null)
            {
                return FeatureAvailability.Drifted;
            }

            var videoPlayerType = VideoPlayerField.FieldType;
            if (CoopReflection.OptionalProperty(videoPlayerType, "isPrepared") == null
                || CoopReflection.OptionalProperty(videoPlayerType, "time") == null
                || CoopReflection.OptionalProperty(videoPlayerType, "length") == null)
            {
                return FeatureAvailability.Drifted;
            }

            return FeatureAvailability.Ready;
        }

        private static void LogAvailability()
        {
            if (_availabilityLogged)
            {
                return;
            }

            _availabilityLogged = true;
            if (AvailabilityState == FeatureAvailability.Drifted)
            {
                CoopLog.Error("TV integration disabled: " + UnavailableReason);
            }
            else if (AvailabilityState == FeatureAvailability.Absent)
            {
                CoopLog.Info("RTCGO Custom TV is not installed - shared TV disabled");
            }
        }

        private static object Get(FieldInfo field)
        {
            return field == null ? null : field.GetValue(field.IsStatic ? null : Master());
        }

        private static T Get<T>(FieldInfo field, T fallback = default)
        {
            try
            {
                var value = Get(field);
                return value == null ? fallback : (T)value;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return fallback;
            }
        }

        private static Component Master()
        {
            try
            {
                var list = Enrolled?.GetValue(null) as IList;
                if (list != null && list.Count > 0)
                {
                    var enrolled = list[0] as Component;
                    if (enrolled != null)
                    {
                        _master = enrolled;
                    }
                }

                return _master;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return null;
            }
        }

        internal static string StreamUrl => Get<string>(StreamUrlField);
        internal static string StreamTitle => Get<string>(StreamTitleField);
        internal static string PlaylistUrlValue => Get<string>(PlaylistUrl);
        internal static bool IsPlayingStream => Get(Playing, false);
        internal static bool IsFetching => Get(Fetching, false);
        internal static bool IsLive => Get(Live, false);
        internal static bool IsSegmentedVod => Get(Segmented, false);
        internal static bool Paused => Get(PausedField, false);
        internal static bool PoweredOff => Get(Powered, false);
        internal static bool Shuffle => Get(ShuffleField, false);
        internal static int PlaylistIndexValue => Get(PlaylistIndex, 1);
        internal static double Position => GetDouble(LastTime, 0.0);
        internal static bool IsPlaylist => !string.IsNullOrEmpty(PlaylistUrlValue);
        internal static string SourceUrl
        {
            get
            {
                return _requestedSourceUrl;
            }
        }
        internal static string PendingSourceUrl => _pendingSourceUrl;
        internal static int EnrolledCount => (Enrolled?.GetValue(null) as IList)?.Count ?? 0;
        internal static bool ControllerReady => Master() != null;
        internal static bool EnrollmentReady => EnrolledCount > 0;

        internal static void RegisterController(Component controller)
        {
            if (controller != null && ControllerType.IsInstanceOfType(controller))
            {
                _master = controller;
            }
        }

        internal static void RecordPendingSourceUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                _requestedSourceUrl = url;
                _pendingSourceUrl = url;
            }
        }

        internal static void RecordActiveSourceUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (string.IsNullOrWhiteSpace(_requestedSourceUrl))
                {
                    _requestedSourceUrl = url;
                }
            }
        }

        internal static void ClearPendingSourceUrl()
        {
            _pendingSourceUrl = null;
        }

        internal static void ClearSourceIdentity()
        {
            _pendingSourceUrl = null;
            _requestedSourceUrl = null;
        }

        internal static void ResetSession()
        {
            _pendingSourceUrl = null;
            _requestedSourceUrl = null;
            ResetControllerState();
            ApplyingRemote = false;
        }

        internal static void NotifySceneLoaded()
        {
            _pendingSourceUrl = null;
            _requestedSourceUrl = null;
            ResetControllerState();
            ApplyingRemote = false;
        }

        private static void ResetControllerState()
        {
            if (_attachedVideoPlayer != null && _videoErrorDelegate != null
                && VideoErrorReceived != null)
            {
                VideoErrorReceived.RemoveEventHandler(_attachedVideoPlayer, _videoErrorDelegate);
            }

            _master = null;
            _launchedSource = null;
            _launchedPlaylistIndex = 0;
            _streamLaunchPending = false;
            _pendingPosition = 0.0;
            _hasPendingPosition = false;
            _pendingPositionGeneration = 0;
            _playbackGeneration = 0;
            _barrierState = false;
            _attachedVideoPlayer = null;
            _videoErrorDelegate = null;
            _videoErrorHandler = null;
        }

        internal static bool AttachVideoErrorHandler(Action<string> handler)
        {
            _videoErrorHandler = handler;
            var player = VideoPlayer(Master());
            if (player == null || VideoErrorReceived == null)
            {
                return false;
            }

            if (ReferenceEquals(player, _attachedVideoPlayer))
            {
                return true;
            }

            if (_attachedVideoPlayer != null && _videoErrorDelegate != null)
            {
                VideoErrorReceived.RemoveEventHandler(_attachedVideoPlayer, _videoErrorDelegate);
            }

            var callback = typeof(TvInterop).GetMethod(nameof(OnVideoError),
                BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                _videoErrorDelegate = Delegate.CreateDelegate(VideoErrorReceived.EventHandlerType,
                    callback);
                VideoErrorReceived.AddEventHandler(player, _videoErrorDelegate);
                _attachedVideoPlayer = player;
                return true;
            }
            catch (Exception exception)
            {
                _videoErrorDelegate = null;
                _attachedVideoPlayer = null;
                CoopLog.Error("TV VideoPlayer error lifecycle hook failed: " + exception);
                return false;
            }
        }

        private static void OnVideoError(object _, string message)
        {
            _videoErrorHandler?.Invoke(message);
        }

        internal static void ApplyMediaState(string sourceUrl, string url, string title, string playlistUrl,
            bool live, bool segmented, int playlistIndex, double position, bool paused, bool powered,
            bool shuffle, bool barrier, bool resume, int generation)
        {
            if (!Present)
            {
                return;
            }

            var master = Master();
            if (master == null)
            {
                return;
            }

            ApplyingRemote = true;
            try
            {
                var oldSource = _launchedSource;
                var replaced = !string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                    || _launchedPlaylistIndex != playlistIndex;
                if (replaced)
                {
                    _playbackGeneration = generation;
                }

                _barrierState = barrier;
                SetField(Live, live, master);
                SetField(Segmented, segmented, master);
                SetField(StreamTitleField, title, master);
                SetField(PlaylistUrl, playlistUrl, master);
                SetField(PlaylistIndex, playlistIndex, master);
                SetField(Powered, powered, master);
                SetField(ShuffleField, shuffle, master);

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    _hasPendingPosition = false;
                    ClearSourceIdentity();
                }
                else
                {
                    _pendingPosition = position;
                    _hasPendingPosition = true;
                    _pendingPositionGeneration = generation;
                }

                var newPlaylistItem = !string.IsNullOrEmpty(playlistUrl)
                    && string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                    && _launchedPlaylistIndex != playlistIndex;
                var needsLaunch = !string.IsNullOrEmpty(sourceUrl)
                    && (!string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                        || newPlaylistItem || _streamLaunchPending);
                if (needsLaunch)
                {
                    _launchedSource = sourceUrl;
                    _launchedPlaylistIndex = playlistIndex;
                    _streamLaunchPending = true;
                    CoopLog.Info("TV client injecting shared stream: " + sourceUrl);
                    SetField(PendingUrlField, sourceUrl, master);
                    QualityChoice.Invoke(master, new object[] { false });
                    _streamLaunchPending = false;
                }
                else if (string.IsNullOrEmpty(sourceUrl) && string.IsNullOrEmpty(playlistUrl))
                {
                    var wasStreaming = !string.IsNullOrEmpty(_launchedSource) || _launchedPlaylistIndex != 0;
                    _launchedSource = null;
                    _launchedPlaylistIndex = 0;
                    _streamLaunchPending = false;
                    if (wasStreaming)
                    {
                        BroadcastChange?.Invoke(master, new object[] { Get(SharedVideoIndex, 0) });
                    }
                }

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    _hasPendingPosition = false;
                }
                else
                {
                    RequestPosition(master, position);
                }

                SetSharedPaused(barrier || paused || powered);
                if (resume && !powered)
                {
                    ResumePlayback();
                }
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        internal static void ApplyPaused(bool paused)
        {
            if (!Present || Master() == null)
            {
                return;
            }

            SetSharedPaused(paused);
        }

        internal static void ApplyPowered(bool poweredOff)
        {
            if (!Present)
            {
                return;
            }

            var master = Master();
            if (master == null || PoweredOff == poweredOff)
            {
                return;
            }

            if (RemotePower != null)
            {
                var wasApplyingRemote = ApplyingRemote;
                ApplyingRemote = true;
                try
                {
                    RemotePower.Invoke(master, null);
                }
                finally
                {
                    ApplyingRemote = wasApplyingRemote;
                }
            }

            SetField(Powered, poweredOff, master);
        }

        internal static void ApplyShuffle(bool shuffle)
        {
            if (!Present)
            {
                return;
            }

            var master = Master();
            if (master == null || Shuffle == shuffle)
            {
                return;
            }

            if (!ApplyRemoteShuffle())
            {
                SetField(ShuffleField, shuffle, master);
            }
            else if (Shuffle != shuffle)
            {
                SetField(ShuffleField, shuffle, master);
            }
        }

        internal static void ApplySeek(double position)
        {
            if (!Present || !IsValidPosition(position))
            {
                return;
            }

            var master = Master();
            if (master == null)
            {
                return;
            }

            RequestPosition(master, Math.Min(position, MaxPositionSeconds));
        }

        internal static void ApplyBarrier(bool barrier, bool resume)
        {
            if (!Present || Master() == null)
            {
                return;
            }

            _barrierState = barrier;
            SetSharedPaused(barrier);
            if (resume && !PoweredOff)
            {
                ResumePlayback();
            }
        }

        internal static void ApplyPlaybackError()
        {
            if (!Present)
            {
                return;
            }

            _barrierState = false;
            AbortSharedPlayback();
        }

        internal readonly struct PlaybackUndoState
        {
            internal PlaybackUndoState(string sourceUrl, string url, string title, string playlistUrl,
                bool live, bool segmented, int playlistIndex, double position, bool paused,
                bool powered, bool shuffle, bool barrier, int generation)
            {
                SourceUrl = sourceUrl;
                StreamUrl = url;
                StreamTitle = title;
                PlaylistUrl = playlistUrl;
                IsLive = live;
                IsSegmentedVod = segmented;
                PlaylistIndex = playlistIndex;
                Position = position;
                Paused = paused;
                PoweredOff = powered;
                Shuffle = shuffle;
                Barrier = barrier;
                Generation = generation;
            }

            internal string SourceUrl
            {
                get;
            }
            internal string StreamUrl
            {
                get;
            }
            internal string StreamTitle
            {
                get;
            }
            internal string PlaylistUrl
            {
                get;
            }
            internal bool IsLive
            {
                get;
            }
            internal bool IsSegmentedVod
            {
                get;
            }
            internal int PlaylistIndex
            {
                get;
            }
            internal double Position
            {
                get;
            }
            internal bool Paused
            {
                get;
            }
            internal bool PoweredOff
            {
                get;
            }
            internal bool Shuffle
            {
                get;
            }
            internal bool Barrier
            {
                get;
            }
            internal int Generation
            {
                get;
            }
        }

        internal static PlaybackUndoState CapturePlaybackState()
            => new PlaybackUndoState(SourceUrl, StreamUrl, StreamTitle, PlaylistUrlValue, IsLive,
                IsSegmentedVod, PlaylistIndexValue, Position, Paused, PoweredOff, Shuffle,
                _barrierState, _playbackGeneration);

        internal static void RestorePlaybackState(PlaybackUndoState state)
        {
            ApplyMediaState(state.SourceUrl, state.StreamUrl, state.StreamTitle, state.PlaylistUrl,
                state.IsLive, state.IsSegmentedVod, state.PlaylistIndex, state.Position,
                state.Paused, state.PoweredOff, state.Shuffle, state.Barrier, false,
                state.Generation);
        }

        internal static void SetSharedPaused(bool paused)
        {
            var changed = Paused != paused;
            if (changed && TogglePause != null)
            {
                var wasApplyingRemote = ApplyingRemote;
                ApplyingRemote = true;
                try
                {
                    TogglePause.Invoke(Master(), null);
                }
                finally
                {
                    ApplyingRemote = wasApplyingRemote;
                }
            }

            if (Paused != paused)
            {
                SetField(PausedField, paused, Master());
            }
        }

        internal static void SetSharedPausedForPrepare(bool paused)
            => SetField(PausedField, paused, Master());
        internal static void ResumePlayback() => SetSharedPaused(false);

        internal static void AbortSharedPlayback()
        {
            var master = Master();
            try
            {
                SetSharedPaused(false);
                if (master != null && !PoweredOff && RemotePower != null)
                {
                    var wasApplyingRemote = ApplyingRemote;
                    ApplyingRemote = true;
                    try
                    {
                        RemotePower.Invoke(master, null);
                    }
                    finally
                    {
                        ApplyingRemote = wasApplyingRemote;
                    }
                }
            }
            finally
            {
                ClearSourceIdentity();
                _launchedSource = null;
                _launchedPlaylistIndex = 0;
                _streamLaunchPending = false;
                _hasPendingPosition = false;
            }
        }

        internal static bool ApplyRemoteShuffle()
        {
            var master = Master();
            if (master == null || RemoteShuffle == null)
            {
                return false;
            }

            var wasApplyingRemote = ApplyingRemote;
            ApplyingRemote = true;
            try
            {
                RemoteShuffle.Invoke(master, null);
                return true;
            }
            finally
            {
                ApplyingRemote = wasApplyingRemote;
            }
        }

        internal static bool IsMainPreparedPlayer(object instance, object source)
        {
            if (instance == null || !IsPlayingStream || !ReferenceEquals(Master(), instance))
            {
                return false;
            }

            return ReferenceEquals(VideoPlayer(instance as Component), source);
        }

        internal static void PausePreparedStream(object instance)
        {
            if (instance != null && ReferenceEquals(Master(), instance))
            {
                SetSharedPaused(true);
            }
        }

        internal static bool ApplyPreparedPosition(object instance)
        {
            if (!_hasPendingPosition || instance == null || !ReferenceEquals(Master(), instance)
                || _pendingPositionGeneration != _playbackGeneration)
            {
                return false;
            }

            var player = VideoPlayer(instance as Component);
            if (player == null || !GetProperty(player, "isPrepared", false))
            {
                return false;
            }

            ApplyingRemote = true;
            try
            {
                SetPlayerPosition(player, _pendingPosition);
                _hasPendingPosition = false;
                return true;
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        internal static bool RequestClientEnrollment()
        {
            if (!Present)
            {
                return false;
            }

            if (EnrolledCount > 0)
            {
                return false;
            }

            var master = Master();
            if (master == null || ChangeVideo == null)
            {
                return false;
            }

            ChangeVideo.Invoke(master, new object[] { Get(SharedVideoIndex, 0) });
            return true;
        }

        internal static void StreamYouTubePostfix(string url) => RecordPendingSourceUrl(url);

        internal static void ChangeToUrlPostfix(string url) => RecordActiveSourceUrl(url);

        internal static bool ApplyOperation(byte op, string url, string title, double value)
        {
            if (!Present)
            {
                return false;
            }

            var master = Master();
            if (master == null)
            {
                return false;
            }

            var wasApplyingRemote = ApplyingRemote;
            ApplyingRemote = true;
            try
            {
                switch (op)
                {
                    case 1:
                        if (!IsPlaylist || PlayPlaylistItem == null)
                        {
                            return false;
                        }
                        PlayPlaylistItem.Invoke(master, new object[] { PlaylistIndexValue + 1 });
                        return true;
                    case 2:
                        if (!IsPlaylist || PlayPlaylistItem == null)
                        {
                            return false;
                        }
                        PlayPlaylistItem.Invoke(master, new object[] { Math.Max(1, PlaylistIndexValue - 1) });
                        return true;
                    case 3:
                        if (TogglePause == null)
                        {
                            return false;
                        }

                        TogglePause?.Invoke(master, null);
                        return true;
                    case 4:
                        if (RemotePower != null)
                        {
                            RemotePower.Invoke(master, null);
                            return true;
                        }
                        if (Powered == null)
                        {
                            return false;
                        }

                        Powered.SetValue(Powered.IsStatic ? null : master, !PoweredOff);
                        return true;
                    case 5:
                        if (ShuffleField == null)
                        {
                            return false;
                        }

                        if (ApplyRemoteShuffle())
                        {
                            return true;
                        }

                        ShuffleField.SetValue(ShuffleField.IsStatic ? null : master, !Shuffle);
                        return true;
                    case 6:
                        if (double.IsNaN(value) || double.IsInfinity(value)
                            || Math.Abs(value) > MaxSeekDeltaSeconds)
                        {
                            return false;
                        }

                        var player = VideoPlayer(master);
                        if (player == null || !GetProperty(player, "isPrepared", false))
                        {
                            return false;
                        }

                        var currentPosition = GetPropertyDouble(player, "time", Position);
                        if (!IsValidPosition(currentPosition))
                        {
                            currentPosition = 0.0;
                        }

                        var requestedPosition = currentPosition + value;
                        if (!IsValidPosition(requestedPosition))
                        {
                            return false;
                        }

                        var position = ClampPosition(player, requestedPosition);
                        if (Math.Abs(position - currentPosition) <= 0.0001)
                        {
                            return false;
                        }

                        SetPlayerPosition(player, position);
                        return true;
                    case 7:
                        if (string.IsNullOrWhiteSpace(url))
                        {
                            return false;
                        }

                        SetField(PlaylistUrl, url.Contains("list=") ? url : null, master);
                        if (Stream != null)
                        {
                            ((MonoBehaviour)master).StartCoroutine((IEnumerator)Stream.Invoke(master,
                                new object[] { url }));
                        }
                        else if (ChangeUrl != null)
                        {
                            ChangeUrl.Invoke(master, new object[] { url, title ?? "Shared stream" });
                        }
                        else
                        {
                            return false;
                        }

                        return true;
                    default:
                        return false;
                }
            }
            finally
            {
                ApplyingRemote = wasApplyingRemote;
            }
        }

        internal static bool FinishQualitySelection()
        {
            if (!Present)
            {
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(Get<string>(PendingUrlField)))
                {
                    return false;
                }

                Unfreeze?.Invoke(Master(), null);
                SetField(QualityOpen, false, Master());
                (GetField(QualityRoot) as GameObject)?.SetActive(false);
                return true;
            }
            catch (Exception exception)
            {
                CoopLog.Warn("TV quality selection failed: " + exception);
                return false;
            }
        }

        internal static string PendingUrl => Get<string>(PendingUrlField);

        private static object VideoPlayer(Component master)
        {
            return master == null || VideoPlayerField == null ? null
                : VideoPlayerField.GetValue(master);
        }

        private static void RequestPosition(Component master, double position)
        {
            var player = VideoPlayer(master);
            _pendingPosition = position;
            _hasPendingPosition = true;
            _pendingPositionGeneration = _playbackGeneration;
            if (ApplyPosition(master, _pendingPosition))
            {
                _hasPendingPosition = false;
            }
        }

        private static bool ApplyPosition(Component master, double position)
        {
            var player = VideoPlayer(master);
            if (player == null || !GetProperty(player, "isPrepared", false))
            {
                return false;
            }

            SetPlayerPosition(player, position);
            return true;
        }

        private static void SetPlayerPosition(object player, double position)
        {
            SetProperty(player, "time", position);
            SetLastTime(position);
        }

        private static void SetLastTime(double position)
        {
            if (LastTime == null)
            {
                return;
            }

            var value = Convert.ChangeType(position, LastTime.FieldType);
            LastTime.SetValue(LastTime.IsStatic ? null : Master(), value);
        }

        private static bool IsValidPosition(double position)
        {
            return !double.IsNaN(position) && !double.IsInfinity(position) && position >= 0.0;
        }

        private static double ClampPosition(object player, double position)
        {
            if (!IsValidPosition(position))
            {
                return 0.0;
            }

            var duration = GetPropertyDouble(player, "length", double.NaN);
            var maximum = IsValidPosition(duration) && duration > 0.0
                ? Math.Min(duration, MaxPositionSeconds) : MaxPositionSeconds;
            return Math.Min(position, maximum);
        }

        private static double GetDouble(FieldInfo field, double fallback)
        {
            try
            {
                var value = Get(field);
                var result = value == null ? fallback : Convert.ToDouble(value);
                return IsValidPosition(result) ? Math.Min(result, MaxPositionSeconds) : fallback;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return fallback;
            }
        }

        private static double GetPropertyDouble(object target, string name, double fallback)
        {
            try
            {
                var property = CoopReflection.OptionalProperty(target.GetType(), name);
                var value = property?.GetValue(target, null);
                var result = value == null ? fallback : Convert.ToDouble(value);
                return IsValidPosition(result) ? Math.Min(result, MaxPositionSeconds) : fallback;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return fallback;
            }
        }

        private static T GetProperty<T>(object target, string name, T fallback)
        {
            try
            {
                var property = CoopReflection.OptionalProperty(target.GetType(), name);
                var value = property?.GetValue(target, null);
                return value == null ? fallback : (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return fallback;
            }
        }

        private static void SetProperty(object target, string name, object value)
        {
            var property = target?.GetType().GetProperty(name, Flags);
            if (property == null || !property.CanWrite)
            {
                return;
            }

            var converted = value;
            if (value != null && property.PropertyType != value.GetType())
            {
                converted = Convert.ChangeType(value, property.PropertyType);
            }

            property.SetValue(target, converted, null);
        }

        private static object GetField(FieldInfo field)
        {
            return field?.GetValue(field.IsStatic ? null : Master());
        }

        private static void SetField(FieldInfo field, object value, object instance)
        {
            if (field == null)
            {
                return;
            }

            var target = field.IsStatic ? null : instance;
            if (!field.IsStatic && target == null)
            {
                return;
            }

            var converted = value;
            if (value != null && field.FieldType != value.GetType())
            {
                converted = Convert.ChangeType(value, field.FieldType);
            }

            field.SetValue(target, converted);
        }
    }
}
