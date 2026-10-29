using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CardShopCoop.Util
{
    /// <summary>
    /// A joining client loading the world can move a heavily modded host's working set by several
    /// GB even though the host's own allocation is flat: the pages are already committed mod
    /// asset data, and Windows does not return them on its own. That leaves the host looking like
    /// it leaked a multi-GB buffer (the client-at-title and ordinary-save runs both stay flat, so
    /// neither the save nor the host's own rendering is the trigger). A one-time
    /// empty-working-set request after the join burst lets the OS drop the unused pages; the game
    /// pages its active shop/avatar assets back in as it renders them.
    ///
    /// <para>This is Windows-only. The call is best-effort and can never fail a join; the config
    /// switch exists for players who prefer to avoid the one-time disk read that follows it.</para>
    /// </summary>
    internal static class WorkingSetTrim
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        internal static void AfterJoin(string stage)
        {
            if (CoopPlugin.TrimWorkingSetAfterJoin?.Value != true)
            {
                return;
            }

            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    if (EmptyWorkingSet(process.Handle))
                    {
                        CoopPlugin.Log?.LogInfo("[mem] working-set trim after " + stage
                            + ": released unused join pages.");
                        return;
                    }

                    CoopPlugin.Log?.LogWarning("[mem] working-set trim after " + stage
                        + " failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
                }
            }
            catch (DllNotFoundException)
            {
                // Non-Windows runtime (for example a Proton wrapper without psapi); the join is
                // unaffected, the pages simply stay resident.
                CoopPlugin.Log?.LogDebug("[mem] working-set trim unavailable on this platform.");
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogDebug("[mem] working-set trim skipped: " + error.Message);
            }
        }
    }
}
