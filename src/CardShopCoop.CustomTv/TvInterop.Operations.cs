using System;
using System.Collections;
using System.Reflection;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.CustomTv
{
    internal static partial class TvInterop
    {
        /// <summary>Applies one TV command on the host, which owns the command. The client already
        /// ran the same game path as its prediction. Throws on refusal so the host can answer the
        /// action as rejected.</summary>
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
