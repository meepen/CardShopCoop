using System;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Util
{
    /// <summary>
    /// One-line memory snapshot for the expensive, rare boundaries around a join: the host's
    /// forced world save, the save transfer, the identity/world baselines, and the character
    /// avatar/template construction. The counters separate a managed/native allocation from
    /// asset paging, which is what a heavily modded process actually does on a join. Keeping the
    /// numbers in the normal log makes the next report diagnosable without a profiler attach.
    ///
    /// <para>Unity's profiler counters are main-thread only, and some are unavailable in a
    /// retail player; unreadable values are reported as "?" rather than failing the join.
    /// <see cref="GC.GetTotalMemory(bool)"/> is always available and is enough to separate a
    /// managed allocation from native/asset paging.</para>
    /// </summary>
    internal static class MemoryProbe
    {
        internal static void Log(string stage)
        {
            CoopPlugin.Log?.LogInfo("[mem] " + DateTime.UtcNow.ToString("HH:mm:ss.fff")
                + " " + stage + " " + Describe());
        }

        internal static string Describe()
        {
            var text = new StringBuilder(192);
            text.Append("gc=").Append(Megabytes(GC.GetTotalMemory(false)));
            text.Append(" monoUsed=").Append(Megabytes(TryRead(Profiler.GetMonoUsedSizeLong)));
            text.Append(" monoHeap=").Append(Megabytes(TryRead(Profiler.GetMonoHeapSizeLong)));
            text.Append(" totalAlloc=").Append(Megabytes(TryRead(Profiler.GetTotalAllocatedMemoryLong)));
            text.Append(" totalReserved=")
                .Append(Megabytes(TryRead(Profiler.GetTotalReservedMemoryLong)));
            // Process working set is deliberately not read here: Mono reports 0 for both
            // Process.WorkingSet64 and Environment.WorkingSet in this player. The OS working set
            // is an external measurement; these counters are the ones Unity itself accounts for.
            var scenes = SceneManager.sceneCount;
            var shown = Math.Min(scenes, 4);
            text.Append(" scenes=").Append(scenes).Append('[');
            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(SceneManager.GetSceneAt(i).name);
            }

            if (scenes > shown)
            {
                text.Append(",+").Append(scenes - shown);
            }

            text.Append(']');
            return text.ToString();
        }

        private static long TryRead(Func<long> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string Megabytes(long bytes)
            => bytes < 0 ? "?" : (bytes / (1024d * 1024d)).ToString("F1") + "MB";
    }
}
