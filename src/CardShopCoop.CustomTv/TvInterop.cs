using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.CustomTv
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
        private static readonly Type ChannelType =
            CoopReflection.OptionalType("Scripts.TvChannel", AssemblyName);
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;

        // Custom TV keeps all shared playback state on one TvChannel object per channel;
        // VideoPlayerController.Ch selects it. The controller only keeps the device-local
        // VideoPlayer, the quality dialog and the channel list.
        private static readonly FieldInfo ChannelField = Field("Ch");
        private static readonly FieldInfo AllTvs = Field("AllTVs");
        private static readonly FieldInfo Enrolled = ChannelState("enrolledTVs");
        private static readonly FieldInfo SharedVideoIndex = ChannelState("sharedVideoIndex");
        private static readonly FieldInfo StreamUrlField = ChannelState("activeStreamUrl");
        private static readonly FieldInfo StreamTitleField = ChannelState("activeStreamTitle");
        private static readonly FieldInfo PlaylistUrl = ChannelState("activePlaylistUrl");
        private static readonly FieldInfo PlaylistIndex = ChannelState("activePlaylistIndex");
        private static readonly FieldInfo Live = ChannelState("isLiveStream");
        private static readonly FieldInfo Segmented = ChannelState("isSegmentedVod");
        private static readonly FieldInfo Playing = ChannelState("isPlayingStream");
        private static readonly FieldInfo Fetching = ChannelState("isFetchingUrl");
        private static readonly FieldInfo PausedField = ChannelState("isSharedPaused");
        private static readonly FieldInfo Powered = ChannelState("isPoweredOff");
        private static readonly FieldInfo ShuffleField = ChannelState("isShuffleMode");
        private static readonly FieldInfo LastTime = ChannelState("lastKnownMasterTime");
        private static readonly FieldInfo PendingUrlField = ChannelState("pendingYouTubeUrl");
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
        private static readonly MethodInfo RemotePlayPause = Method("RemotePlayPause");
        private static readonly MethodInfo RemoteNext = Method("RemoteNext");
        private static readonly MethodInfo RemotePrev = Method("RemotePrev");
        private static readonly MethodInfo RemoteShuffle = Method("RemoteShuffle");
        private static readonly MethodInfo SoundTv = SilentMethod("SoundTV");
        private static readonly FeatureAvailability AvailabilityState = ValidateSurface();
        private static bool _logged;
        private static bool _availabilityLogged;
        private static Component _master;
        private sealed class ChannelPlayback
        {
            internal string PendingSourceUrl;
            internal string RequestedSourceUrl;
            internal string LaunchedSource;
            internal int LaunchedPlaylistIndex;
            internal bool StreamLaunchPending;
            internal int PlaybackGeneration;
            internal bool Barrier;
        }

        private sealed class PlayerPosition
        {
            internal double Pending;
            internal bool HasPending;
            internal int Generation;
        }

        private sealed class VideoErrorSink
        {
            internal Component Controller;
            internal Action<Component, string> Handler;
            internal void OnError(object _, string message) => Handler?.Invoke(Controller, message);
        }

        private sealed class ErrorBinding
        {
            internal object Player;
            internal Delegate Handler;
        }

        private static Component _focus;
        private static int _focusDepth;
        private static readonly Dictionary<object, ChannelPlayback> Playbacks = new();
        private static readonly Dictionary<Component, PlayerPosition> Positions = new();
        private static readonly Dictionary<Component, ErrorBinding> ErrorBindings = new();

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
        private static FieldInfo ChannelState(string name)
            => ChannelType == null ? null : CoopReflection.OptionalField(ChannelType, name);
        private static MethodInfo Method(string name, params Type[] arguments)
            => CoopReflection.OptionalMethod(ControllerType, name, arguments);
        private static MethodInfo SilentMethod(string name)
            => ControllerType?.GetMethod(name, Flags);

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

            if (ChannelType == null || ChannelField == null
                || !ChannelType.IsAssignableFrom(ChannelField.FieldType))
            {
                return FeatureAvailability.Drifted;
            }

            var fields = new[]
            {
                Enrolled, SharedVideoIndex, StreamUrlField, StreamTitleField, PlaylistUrl,
                PlaylistIndex, Live, Segmented, Playing, Fetching, PausedField, Powered,
                ShuffleField, LastTime, PendingUrlField,
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
                BroadcastChange, TogglePause, RemotePower,
                RemotePlayPause, RemoteNext, RemotePrev, RemoteShuffle,
            };
            for (var i = 0; i < methods.Length; i++)
            {
                if (methods[i] == null)
                {
                    return FeatureAvailability.Drifted;
                }
            }

            if (!typeof(IList).IsAssignableFrom(Enrolled.FieldType))
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
            if (field == null)
            {
                return null;
            }

            if (field.IsStatic)
            {
                return field.GetValue(null);
            }

            var channel = ChannelOf(Master());
            return channel == null ? null : field.GetValue(channel);
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

        /// <summary>Narrows every read/write to one TV for the duration of a handler. The EIM
        /// handler and the action hooks know the exact TV; this keeps a multi-TV shop from
        /// resolving state through whichever controller happened to enable last.</summary>
        internal static void BeginFocus(Component controller)
        {
            if (_focusDepth++ == 0)
            {
                _focus = controller;
            }
        }

        internal static void EndFocus()
        {
            if (_focusDepth == 0)
            {
                return;
            }

            if (--_focusDepth == 0)
            {
                _focus = null;
            }
        }

        internal static Component CurrentController => Master();

        /// <summary>The controller that performed the current local action. The hooks register it
        /// before they ask for an action target, so an action carries the TV it belongs to instead
        /// of the channel master selected for game calls.</summary>
        internal static Component ActionController => _master ?? Master();

        internal static InteractableObject OwnerOf(Component controller)
            => controller == null || ControllerType == null
                ? null : controller.GetComponentInParent<InteractableObject>();

        internal static Component ControllerFor(InteractableObject owner)
        {
            if (owner == null || ControllerType == null)
            {
                return null;
            }

            var controller = owner.GetComponentInChildren(ControllerType, true);
            return controller != null ? controller : owner.GetComponentInParent(ControllerType, true);
        }

        private static Component Master()
        {
            try
            {
                var focus = _focus;
                if (focus != null)
                {
                    return EnrolledMaster(focus) ?? focus;
                }

                // A TV that is enrolled is a real active channel, so it outranks the idle TV that
                // happened to run Start last. This is the one-TV fallback for code paths without
                // an explicit focus (for example the global seek input).
                var registered = _master;
                if (registered != null)
                {
                    var master = EnrolledMaster(registered);
                    if (master != null)
                    {
                        return master;
                    }
                }

                var sound = SoundTvController();
                if (sound != null)
                {
                    return EnrolledMaster(sound) ?? sound;
                }

                if (registered != null)
                {
                    return registered;
                }

                var all = AllTvs?.GetValue(null) as IList;
                if (all != null)
                {
                    for (var i = 0; i < all.Count; i++)
                    {
                        var enrolled = EnrolledMaster(all[i] as Component);
                        if (enrolled != null)
                        {
                            return enrolled;
                        }
                    }
                }

                return null;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return null;
            }
        }

        private static Component SoundTvController()
        {
            if (SoundTv == null)
            {
                return null;
            }

            try
            {
                return SoundTv.Invoke(null, null) as Component;
            }
            catch (Exception exception)
            {
                CoopLog.Swallow(exception);
                return null;
            }
        }

        internal static Component ChannelMaster(Component controller)
            => controller == null ? null : EnrolledMaster(controller) ?? controller;

        internal static IEnumerable<Component> Controllers()
        {
            var all = AllTvs?.GetValue(null) as IList;
            if (all == null)
            {
                yield break;
            }

            for (var i = 0; i < all.Count; i++)
            {
                if (all[i] is Component controller && controller != null)
                {
                    yield return controller;
                }
            }
        }

        /// <summary>True for the controller the game's <c>IsSoundChannelMaster</c> gate would
        /// accept: the channel master of the sound TV. Global hotkeys belong to that TV, not to
        /// whichever controller happened to run Start last.</summary>
        internal static bool IsSoundController(Component controller)
        {
            if (controller == null)
            {
                return false;
            }

            var sound = SoundTvController();
            return ReferenceEquals(ChannelMaster(sound ?? controller), controller);
        }

        private static Component EnrolledMaster(Component controller)
        {
            if (controller == null)
            {
                return null;
            }

            var channel = ChannelOf(controller);
            var enrolled = channel == null ? null : Enrolled?.GetValue(channel) as IList;
            if (enrolled == null)
            {
                return null;
            }

            for (var i = 0; i < enrolled.Count; i++)
            {
                if (enrolled[i] is Component master && master != null)
                {
                    return master;
                }
            }

            return null;
        }

        internal static object ChannelOf(Component controller)
            => controller == null || ChannelField == null ? null : ChannelField.GetValue(controller);

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
        internal static string SourceUrl => PlaybackFor(Master())?.RequestedSourceUrl;
        internal static string PendingSourceUrl => PlaybackFor(Master())?.PendingSourceUrl;
        internal static int EnrolledCount
        {
            get
            {
                var channel = ChannelOf(Master());
                return channel == null ? 0 : (Enrolled?.GetValue(channel) as IList)?.Count ?? 0;
            }
        }
        internal static bool EnrollmentReady => EnrolledCount > 0;

        internal static void RegisterController(Component controller)
        {
            if (controller != null && ControllerType.IsInstanceOfType(controller))
            {
                _master = controller;
            }
        }

        /// <summary>Drops a TV that is being disabled so the next lookup can find another
        /// enrolled TV instead of reading state from a controller that no longer drives a
        /// channel.</summary>
        internal static void ForgetController(Component controller)
        {
            if (ReferenceEquals(_master, controller))
            {
                _master = null;
            }
        }

        private static ChannelPlayback PlaybackFor(Component controller)
        {
            if (controller == null)
            {
                controller = Master();
            }

            var channel = ChannelOf(controller);
            if (channel == null)
            {
                return null;
            }

            if (!Playbacks.TryGetValue(channel, out var playback))
            {
                playback = new ChannelPlayback();
                Playbacks.Add(channel, playback);
            }

            return playback;
        }

        private static PlayerPosition PositionFor(Component controller)
        {
            if (controller == null)
            {
                return null;
            }

            if (!Positions.TryGetValue(controller, out var position))
            {
                position = new PlayerPosition();
                Positions.Add(controller, position);
            }

            return position;
        }

        internal static void RecordPendingSourceUrl(Component controller, string url)
        {
            var playback = PlaybackFor(controller);
            if (playback != null && !string.IsNullOrWhiteSpace(url))
            {
                playback.RequestedSourceUrl = url;
                playback.PendingSourceUrl = url;
            }
        }

        internal static void RecordActiveSourceUrl(Component controller, string url)
        {
            var playback = PlaybackFor(controller);
            if (playback != null && !string.IsNullOrWhiteSpace(url)
                && string.IsNullOrWhiteSpace(playback.RequestedSourceUrl))
            {
                playback.RequestedSourceUrl = url;
            }
        }

        internal static void ClearPendingSourceUrl()
        {
            var playback = PlaybackFor(Master());
            if (playback != null)
            {
                playback.PendingSourceUrl = null;
            }
        }

        internal static void ClearSourceIdentity()
        {
            var playback = PlaybackFor(Master());
            if (playback != null)
            {
                playback.PendingSourceUrl = null;
                playback.RequestedSourceUrl = null;
            }
        }

        /// <summary>Drops every cache that keys live scene objects: the current controller, the
        /// focused controller, channel playback state, player positions and VideoPlayer error
        /// bindings. A session boundary and a full world load both make them stale.</summary>
        internal static void ResetState()
        {
            DetachVideoErrorHandlers();
            _master = null;
            _focus = null;
            _focusDepth = 0;
            Playbacks.Clear();
            Positions.Clear();
            ApplyingRemote = false;
        }

        internal static bool AttachVideoErrorHandler(Component controller,
            Action<Component, string> handler)
        {
            if (controller == null || VideoErrorReceived == null)
            {
                return false;
            }

            var player = VideoPlayer(controller);
            if (player == null)
            {
                return false;
            }

            if (ErrorBindings.TryGetValue(controller, out var existing)
                && ReferenceEquals(existing.Player, player))
            {
                return true;
            }

            DetachVideoErrorHandler(controller);
            var sink = new VideoErrorSink { Controller = controller, Handler = handler };
            try
            {
                var callback = typeof(VideoErrorSink).GetMethod(nameof(VideoErrorSink.OnError),
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var binding = Delegate.CreateDelegate(VideoErrorReceived.EventHandlerType, sink,
                    callback);
                VideoErrorReceived.AddEventHandler(player, binding);
                ErrorBindings[controller] = new ErrorBinding
                {
                    Player = player,
                    Handler = binding,
                };
                return true;
            }
            catch (Exception exception)
            {
                CoopLog.Error("TV VideoPlayer error lifecycle hook failed: " + exception);
                return false;
            }
        }

        private static void DetachVideoErrorHandler(Component controller)
        {
            if (controller == null || !ErrorBindings.TryGetValue(controller, out var binding))
            {
                return;
            }

            if (VideoErrorReceived != null && binding.Player != null)
            {
                VideoErrorReceived.RemoveEventHandler(binding.Player, binding.Handler);
            }

            ErrorBindings.Remove(controller);
        }

        private static void DetachVideoErrorHandlers()
        {
            if (VideoErrorReceived != null)
            {
                foreach (var binding in ErrorBindings.Values)
                {
                    if (binding.Player != null)
                    {
                        VideoErrorReceived.RemoveEventHandler(binding.Player, binding.Handler);
                    }
                }
            }

            ErrorBindings.Clear();
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
            var playback = PlaybackFor(master);
            if (master == null || playback == null)
            {
                return;
            }

            var positionState = PositionFor(master);
            ApplyingRemote = true;
            try
            {
                var oldSource = playback.LaunchedSource;
                var replaced = !string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                    || playback.LaunchedPlaylistIndex != playlistIndex;
                if (replaced)
                {
                    playback.PlaybackGeneration = generation;
                }

                playback.Barrier = barrier;
                SetField(Live, live, master);
                SetField(Segmented, segmented, master);
                SetField(StreamTitleField, title, master);
                SetField(PlaylistUrl, playlistUrl, master);
                SetField(PlaylistIndex, playlistIndex, master);
                SetField(Powered, powered, master);
                SetField(ShuffleField, shuffle, master);

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    if (positionState != null)
                    {
                        positionState.HasPending = false;
                    }

                    ClearSourceIdentity();
                }
                else if (positionState != null)
                {
                    positionState.Pending = position;
                    positionState.HasPending = true;
                    positionState.Generation = generation;
                }

                var newPlaylistItem = !string.IsNullOrEmpty(playlistUrl)
                    && string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                    && playback.LaunchedPlaylistIndex != playlistIndex;
                var needsLaunch = !string.IsNullOrEmpty(sourceUrl)
                    && (!string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                        || newPlaylistItem || playback.StreamLaunchPending);
                if (needsLaunch)
                {
                    playback.LaunchedSource = sourceUrl;
                    playback.LaunchedPlaylistIndex = playlistIndex;
                    playback.StreamLaunchPending = true;
                    CoopLog.Info("TV client injecting shared stream: " + sourceUrl);
                    SetField(PendingUrlField, sourceUrl, master);
                    QualityChoice.Invoke(master, new object[] { false });
                    playback.StreamLaunchPending = false;
                }
                else if (string.IsNullOrEmpty(sourceUrl) && string.IsNullOrEmpty(playlistUrl))
                {
                    var wasStreaming = !string.IsNullOrEmpty(playback.LaunchedSource)
                        || playback.LaunchedPlaylistIndex != 0;
                    playback.LaunchedSource = null;
                    playback.LaunchedPlaylistIndex = 0;
                    playback.StreamLaunchPending = false;
                    if (wasStreaming)
                    {
                        BroadcastChange?.Invoke(master, new object[] { Get(SharedVideoIndex, 0) });
                    }
                }

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    if (positionState != null)
                    {
                        positionState.HasPending = false;
                    }
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

            var playback = PlaybackFor(Master());
            if (playback != null)
            {
                playback.Barrier = barrier;
            }

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

            var playback = PlaybackFor(Master());
            if (playback != null)
            {
                playback.Barrier = false;
            }

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
        {
            var playback = PlaybackFor(Master());
            return new PlaybackUndoState(SourceUrl, StreamUrl, StreamTitle, PlaylistUrlValue,
                IsLive, IsSegmentedVod, PlaylistIndexValue, Position, Paused, PoweredOff, Shuffle,
                playback?.Barrier ?? false, playback?.PlaybackGeneration ?? 0);
        }

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
                var playback = PlaybackFor(master);
                if (playback != null)
                {
                    playback.LaunchedSource = null;
                    playback.LaunchedPlaylistIndex = 0;
                    playback.StreamLaunchPending = false;
                }

                var position = PositionFor(master);
                if (position != null)
                {
                    position.HasPending = false;
                }
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
            var controller = instance as Component;
            var playback = PlaybackFor(controller);
            var position = PositionFor(controller);
            if (position == null || !position.HasPending || controller == null
                || !ReferenceEquals(Master(), controller)
                || position.Generation != (playback?.PlaybackGeneration ?? 0))
            {
                return false;
            }

            var player = VideoPlayer(controller);
            if (player == null || !GetProperty(player, "isPrepared", false))
            {
                return false;
            }

            ApplyingRemote = true;
            try
            {
                SetPlayerPosition(player, position.Pending);
                position.HasPending = false;
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

        internal static void StreamYouTubePostfix(Component controller, string url)
            => RecordPendingSourceUrl(controller, url);

        internal static void ChangeToUrlPostfix(Component controller, string url)
            => RecordActiveSourceUrl(controller, url);

        /// <summary>Applies one ActionMessage on the peer that owns the command (the host for a
        /// client intent, or the client when a newer authoritative value replays a local follower).
        /// Throws on refusal so the action framework rolls the sender back.</summary>
        internal static void ApplyOp(InteractableObject tv, TvOperation operation, string url,
            string title, double value)
        {
            var controller = ControllerFor(tv);
            if (!Present || controller == null)
            {
                throw new InvalidOperationException("The target TV is not available.");
            }

            BeginFocus(controller);
            try
            {
                if (!ApplyOperation(operation, url, title, value))
                {
                    throw new InvalidOperationException("The TV rejected the operation.");
                }
            }
            finally
            {
                EndFocus();
            }
        }

        internal static void UndoOp(InteractableObject tv, PlaybackUndoState previous)
        {
            var controller = ControllerFor(tv);
            if (controller == null)
            {
                return;
            }

            BeginFocus(controller);
            try
            {
                RestorePlaybackState(previous);
            }
            finally
            {
                EndFocus();
            }
        }

        internal static void UndoSeek(InteractableObject tv, double position)
        {
            var controller = ControllerFor(tv);
            if (controller == null)
            {
                return;
            }

            BeginFocus(controller);
            try
            {
                ApplySeek(position);
            }
            finally
            {
                EndFocus();
            }
        }

        internal static bool ApplyOperation(TvOperation operation, string url, string title,
            double value)
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
                switch (operation)
                {
                    case TvOperation.Next:
                        if (!IsPlaylist || PlayPlaylistItem == null)
                        {
                            return false;
                        }
                        PlayPlaylistItem.Invoke(master, new object[] { PlaylistIndexValue + 1 });
                        return true;
                    case TvOperation.Previous:
                        if (!IsPlaylist || PlayPlaylistItem == null)
                        {
                            return false;
                        }
                        PlayPlaylistItem.Invoke(master, new object[] { Math.Max(1, PlaylistIndexValue - 1) });
                        return true;
                    case TvOperation.Pause:
                        if (TogglePause == null)
                        {
                            return false;
                        }

                        TogglePause?.Invoke(master, null);
                        return true;
                    case TvOperation.Power:
                        if (RemotePower != null)
                        {
                            RemotePower.Invoke(master, null);
                            return true;
                        }
                        if (Powered == null)
                        {
                            return false;
                        }

                        SetField(Powered, !PoweredOff, master);
                        return true;
                    case TvOperation.Shuffle:
                        if (ShuffleField == null)
                        {
                            return false;
                        }

                        if (ApplyRemoteShuffle())
                        {
                            return true;
                        }

                        SetField(ShuffleField, !Shuffle, master);
                        return true;
                    case TvOperation.Seek:
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
                    case TvOperation.Open:
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

        internal static string PendingUrl => Get<string>(PendingUrlField);

        private static object VideoPlayer(Component master)
        {
            return master == null || VideoPlayerField == null ? null
                : VideoPlayerField.GetValue(master);
        }

        private static void RequestPosition(Component master, double position)
        {
            var playback = PlaybackFor(master);
            var pending = PositionFor(master);
            if (playback == null || pending == null)
            {
                return;
            }

            pending.Pending = position;
            pending.HasPending = true;
            pending.Generation = playback.PlaybackGeneration;
            if (ApplyPosition(master, position))
            {
                pending.HasPending = false;
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

            var target = LastTime.IsStatic ? null : ChannelOf(Master());
            if (!LastTime.IsStatic && target == null)
            {
                return;
            }

            var value = Convert.ChangeType(position, LastTime.FieldType);
            LastTime.SetValue(target, value);
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

        private static void SetField(FieldInfo field, object value, object instance)
        {
            if (field == null)
            {
                return;
            }

            var target = field.IsStatic ? null : ChannelOf(instance as Component);
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
