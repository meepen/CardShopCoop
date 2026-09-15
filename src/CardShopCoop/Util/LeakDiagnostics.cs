using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Periodic leak counters for a support log (CoopPlugin "LeakDebug"). Reads a few
    /// game-internal pool lists and process memory so a session can be attributed between the
    /// base game, this plugin, and other mods. Every category is fail-soft: a missing or
    /// renamed member only drops that field from the line and it never throws into the
    /// co-op tick. The expensive whole-scene GameObject walk is only run when the caller asks
    /// for it (once a minute), never on every sample.
    /// </summary>
    internal static class LeakDiagnostics
    {
        private static bool _probed;
        private static Type _itemSpawnType;
        private static Type _cardSpawnType;
        private static FieldInfo _fiItemList;
        private static FieldInfo _fiItemParent;
        private static FieldInfo _fiCardList;
        private static FieldInfo _fiAllCardList;
        private static int _lastGoAll = -1;
        private static bool _goAllError;

        /// <summary>Forget cached readings (a world reload makes the object census stale).</summary>
        public static void Reset()
        {
            _lastGoAll = -1;
            _goAllError = false;
        }

        private static void Probe()
        {
            if (_probed)
                return;
            _probed = true;
            try
            {
                _itemSpawnType = AccessTools.TypeByName("ItemSpawnManager");
                _cardSpawnType = AccessTools.TypeByName("Card3dUISpawner");
                if (_itemSpawnType != null)
                {
                    _fiItemList = AccessTools.Field(_itemSpawnType, "m_ItemList");
                    _fiItemParent = AccessTools.Field(_itemSpawnType, "m_ItemParentGrp");
                }
                if (_cardSpawnType != null)
                {
                    _fiCardList = AccessTools.Field(_cardSpawnType, "m_Card3dUIList");
                    _fiAllCardList = AccessTools.Field(_cardSpawnType, "m_AllCard3dUIList");
                }
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        /// <summary>One compact, greppable line fragment. <paramref name="includeObjectScan"/>
        /// runs the expensive scene-wide GameObject count (call it at most once a minute).</summary>
        public static string Sample(bool includeObjectScan)
        {
            Probe();
            var sb = new StringBuilder(192);
            long gc = 0, alloc = 0, reserved = 0;
            try
            {
                gc = GC.GetTotalMemory(false);
            }
            catch (Exception e) { Swallow.Log(e); }
            try
            {
                alloc = Profiler.GetTotalAllocatedMemoryLong();
            }
            catch (Exception e) { Swallow.Log(e); }
            try
            {
                reserved = Profiler.GetTotalReservedMemoryLong();
            }
            catch (Exception e) { Swallow.Log(e); }
            sb.Append("gc=").Append(gc.ToString(CultureInfo.InvariantCulture));
            sb.Append(" alloc=").Append(alloc.ToString(CultureInfo.InvariantCulture));
            sb.Append(" reserved=").Append(reserved.ToString(CultureInfo.InvariantCulture));
            if (includeObjectScan)
            {
                try
                {
                    _lastGoAll = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                    _goAllError = false;
                }
                catch (Exception e) { _goAllError = true; Swallow.Log(e); }
            }
            sb.Append(" goAll=");
            if (_goAllError)
                sb.Append("error");
            else if (_lastGoAll < 0)
                sb.Append("n/a");
            else
                sb.Append(_lastGoAll.ToString(CultureInfo.InvariantCulture));
            AppendItemPool(sb);
            AppendCardPool(sb);
            return sb.ToString();
        }

        private static void AppendItemPool(StringBuilder sb)
        {
            sb.Append(" itemPool=");
            var list = GetManagerList(_itemSpawnType, _fiItemList);
            if (list == null)
            {
                sb.Append("n/a itemAway=n/a");
                return;
            }
            sb.Append(list.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(" itemAway=");
            var parent = GetManagerField(_itemSpawnType, _fiItemParent) as Transform;
            if (parent == null)
            {
                // A missing pool parent would make every item look "away"; report unknown
                // rather than a false leak signal.
                sb.Append("n/a");
                return;
            }
            int away = 0;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i] as Component;
                    if (item == null)
                        continue;
                    if (item.transform.parent != parent)
                        away++;
                }
            }
            catch (Exception e) { Swallow.Log(e); }
            sb.Append(away.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendCardPool(StringBuilder sb)
        {
            sb.Append(" cardPool=");
            var list = GetManagerList(_cardSpawnType, _fiCardList);
            sb.Append(list != null ? list.Count.ToString(CultureInfo.InvariantCulture) : "n/a");
            sb.Append(" cardAll=");
            var all = GetManagerList(_cardSpawnType, _fiAllCardList);
            sb.Append(all != null ? all.Count.ToString(CultureInfo.InvariantCulture) : "n/a");
        }

        private static IList GetManagerList(Type managerType, FieldInfo field)
        {
            if (managerType == null || field == null)
                return null;
            try
            {
                var manager = FindManager(managerType);
                return manager == null ? null : field.GetValue(manager) as IList;
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }

        private static object GetManagerField(Type managerType, FieldInfo field)
        {
            if (managerType == null || field == null)
                return null;
            try
            {
                var manager = FindManager(managerType);
                return manager == null ? null : field.GetValue(manager);
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Find the live manager. Deliberately NOT CSingleton&lt;T&gt;.Instance: that
        /// can fabricate a persistent empty manager, which would poison the count.</summary>
        private static UnityEngine.Object FindManager(Type managerType)
        {
            try
            {
                return UnityEngine.Object.FindObjectOfType(managerType);
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }
    }
}
