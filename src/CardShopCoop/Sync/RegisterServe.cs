using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Shared helpers for the register.</summary>
    public static class RegisterServe
    {
        // FindObjectOfType walks every loaded object; callers here fire a few times a second,
        // so the manager is cached. Unity's destroyed-object == null overload makes the lazy
        // re-resolve self-healing across scene loads.
        private static ShelfManager _sm;

        private static ShelfManager Shelf()
        {
            if (_sm == null) _sm = Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        /// <summary>Nearest cashier counter index within reach, or -1. Used by the trade prompt path.</summary>
        public static int FindNearestCounter(Vector3 playerPos, float maxDist = 3.5f, bool quiet = false)
        {
            var sm = Shelf();
            if (sm == null) { if (!quiet) CoopPlugin.Log.LogInfo("serve: no ShelfManager"); return -1; }
            int best = -1;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < sm.m_CashierCounterList.Count; i++)
            {
                var counter = sm.m_CashierCounterList[i];
                if (counter == null) continue;
                float sq = (counter.transform.position - playerPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            if (!quiet)
                CoopPlugin.Log.LogInfo($"serve: {sm.m_CashierCounterList.Count} counters, nearest={best} ({Mathf.Sqrt(bestSq):F1}m)");
            return best;
        }
    }
}
