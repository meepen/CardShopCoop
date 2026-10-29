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
    }
}
