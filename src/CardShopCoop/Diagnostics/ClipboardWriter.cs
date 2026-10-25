using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CardShopCoop.Diagnostics
{
    /// <summary>
    /// Puts a text payload and one or more files on the Windows clipboard in one owner session, so
    /// a single paste can carry the report text and the log/report files together. Unity's
    /// GUIUtility.systemCopyBuffer is text-only - assigning it clears every other format - which is
    /// why this uses the Win32 clipboard directly.
    /// </summary>
    internal static class ClipboardWriter
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_HDROP = 15;
        private const uint GMEM_MOVEABLE = 0x0002;

        // Another process can hold the clipboard briefly (a browser, Explorer, Discord itself).
        // Retry rather than failing the button press on the first busy response.
        private const int OpenAttempts = 5;
        private const int OpenRetryMilliseconds = 10;

        [StructLayout(LayoutKind.Sequential)]
        private struct DropFiles
        {
            public uint pFiles;
            public int x;
            public int y;
            public int fNC;
            public int fWide;
        }

        /// <summary>Sets CF_UNICODETEXT when <paramref name="text"/> is not null and CF_HDROP when
        /// <paramref name="filePaths"/> has entries. Every file path must already exist. Throws when
        /// Windows refuses the clipboard; the caller falls back to Unity's text-only buffer.</summary>
        public static void SetTextAndFiles(string text, IReadOnlyList<string> filePaths)
        {
            if (!OpenClipboardWithRetry())
            {
                throw new InvalidOperationException(
                    "the Windows clipboard is busy (last error " + Marshal.GetLastWin32Error() + ")");
            }

            var textHandle = IntPtr.Zero;
            var fileHandle = IntPtr.Zero;
            var textOwnedByClipboard = false;
            var fileOwnedByClipboard = false;
            try
            {
                if (!EmptyClipboard())
                {
                    throw new InvalidOperationException(
                        "EmptyClipboard failed (last error " + Marshal.GetLastWin32Error() + ")");
                }

                if (text != null)
                {
                    textHandle = AllocateText(text);
                    if (SetClipboardData(CF_UNICODETEXT, textHandle) == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "CF_UNICODETEXT was rejected (last error " + Marshal.GetLastWin32Error() + ")");
                    }

                    textOwnedByClipboard = true;
                }

                if (filePaths != null && filePaths.Count > 0)
                {
                    fileHandle = AllocateFileDrop(filePaths);
                    if (SetClipboardData(CF_HDROP, fileHandle) == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "CF_HDROP was rejected (last error " + Marshal.GetLastWin32Error() + ")");
                    }

                    fileOwnedByClipboard = true;
                }
            }
            finally
            {
                if (!textOwnedByClipboard && textHandle != IntPtr.Zero)
                {
                    GlobalFree(textHandle);
                }

                if (!fileOwnedByClipboard && fileHandle != IntPtr.Zero)
                {
                    GlobalFree(fileHandle);
                }

                CloseClipboard();
            }
        }

        private static bool OpenClipboardWithRetry()
        {
            // EmptyClipboard associates the clipboard with this owner. A NULL owner makes
            // SetClipboardData fail by design, so always hand Windows a real window handle.
            var owner = ClipboardOwner();
            for (var attempt = 0; attempt < OpenAttempts; attempt++)
            {
                if (OpenClipboard(owner))
                {
                    return true;
                }

                Thread.Sleep(OpenRetryMilliseconds);
            }

            return false;
        }

        private static IntPtr ClipboardOwner()
        {
            var owner = GetActiveWindow();
            if (owner == IntPtr.Zero)
            {
                owner = GetForegroundWindow();
            }

            return owner == IntPtr.Zero ? GetDesktopWindow() : owner;
        }

        private static IntPtr AllocateText(string text)
        {
            var bytes = Encoding.Unicode.GetBytes((text ?? "") + "\0");
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("could not allocate clipboard text");
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("could not lock clipboard text");
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            return handle;
        }

        private static IntPtr AllocateFileDrop(IReadOnlyList<string> filePaths)
        {
            var headerSize = Marshal.SizeOf(typeof(DropFiles));
            var list = new StringBuilder();
            for (var i = 0; i < filePaths.Count; i++)
            {
                var path = filePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                list.Append(path).Append('\0');
            }

            // A double null terminates the file list; the second null is added here because the
            // loop already appended the last path's single terminator.
            list.Append('\0');
            var pathBytes = Encoding.Unicode.GetBytes(list.ToString());
            var size = headerSize + pathBytes.Length;
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)size);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("could not allocate the clipboard file list");
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("could not lock the clipboard file list");
            }

            try
            {
                var header = new DropFiles
                {
                    pFiles = (uint)headerSize,
                    fWide = 1,
                };
                Marshal.StructureToPtr(header, target, false);
                Marshal.Copy(pathBytes, 0, new IntPtr(target.ToInt64() + headerSize),
                    pathBytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            return handle;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
