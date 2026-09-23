using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Cheap always-on frame-rate meter for the co-op window. Uses unscaled real time so it
    /// keeps reading while the game is paused (the co-op window sets timeScale 0). Ticked once
    /// per frame from <see cref="CoopCore.Update"/>; costs two field reads and an increment.
    /// </summary>
    internal static class FrameCounter
    {
        private const double WindowSeconds = 1.0;

        private static int _frames;
        private static double _windowStart;
        private static double _fps;

        internal static double Fps => _fps;

        internal static void Tick()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (_windowStart <= 0d)
            {
                _windowStart = now;
                _frames = 0;
                return;
            }

            _frames++;
            var elapsed = now - _windowStart;
            if (elapsed >= WindowSeconds)
            {
                _fps = _frames / elapsed;
                _frames = 0;
                _windowStart = now;
            }
        }

        internal static void Reset()
        {
            _frames = 0;
            _windowStart = 0d;
            _fps = 0d;
        }
    }
}
