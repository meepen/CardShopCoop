using System;
using System.Threading;

namespace CardShopCoop.Runtime
{
    /// <summary>Captures the Unity main thread once and fails loudly if entity-bearing DTOs are
    /// serialized or deserialized anywhere else. Entity references resolve through scene objects,
    /// so the wire codec may only touch them on the Unity frame.</summary>
    internal static class MainThreadGuard
    {
        private static int _mainThreadId;

        internal static void Capture()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal static bool IsMainThread
            => _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        internal static void RequireMainThread(string operation)
        {
            if (!IsMainThread)
            {
                throw new InvalidOperationException(
                    operation + " must run on the Unity main thread.");
            }
        }
    }
}
