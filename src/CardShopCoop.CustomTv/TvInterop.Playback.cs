using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.CustomTv
{
    internal static partial class TvInterop
    {
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
        internal static void ApplyMediaState(TvState state)
        {
            if (!Present || state == null)
            {
                return;
            }

            var sourceUrl = state.SourceUrl;
            var title = state.StreamTitle;
            var playlistUrl = state.PlaylistUrl;
            var live = state.IsLive;
            var segmented = state.IsSegmentedVod;
            var playlistIndex = state.PlaylistIndex;
            var position = state.Position;
            var paused = state.Paused;
            var powered = state.PoweredOff;
            var shuffle = state.Shuffle;
            var barrier = state.Barrier;
            var resume = state.Resume;
            var generation = state.Generation;

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

        internal static PlaybackUndoState CapturePlaybackState()
        {
            var playback = PlaybackFor(Master());
            return new PlaybackUndoState(SourceUrl, StreamUrl, StreamTitle, PlaylistUrlValue,
                IsLive, IsSegmentedVod, PlaylistIndexValue, Position, Paused, PoweredOff, Shuffle,
                playback?.Barrier ?? false, playback?.PlaybackGeneration ?? 0);
        }

        internal static void RestorePlaybackState(PlaybackUndoState state)
        {
            ApplyMediaState(new TvState
            {
                SourceUrl = state.SourceUrl,
                StreamUrl = state.StreamUrl,
                StreamTitle = state.StreamTitle,
                PlaylistUrl = state.PlaylistUrl,
                IsLive = state.IsLive,
                IsSegmentedVod = state.IsSegmentedVod,
                PlaylistIndex = state.PlaylistIndex,
                Position = state.Position,
                Paused = state.Paused,
                PoweredOff = state.PoweredOff,
                Shuffle = state.Shuffle,
                Barrier = state.Barrier,
                Resume = false,
                Generation = state.Generation,
            });
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
    }
}
