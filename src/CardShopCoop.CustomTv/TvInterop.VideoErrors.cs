using System;
using System.Reflection;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.CustomTv
{
    internal static partial class TvInterop
    {
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
    }
}
