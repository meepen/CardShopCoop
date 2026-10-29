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
    internal static partial class TvInterop
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
    }
}
