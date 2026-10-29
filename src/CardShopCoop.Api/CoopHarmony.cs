using System;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Optional-target-tolerant patching for the Harmony instance the runtime hands to each
    /// behaviour. The caller never needs its own lookup/logging wrapper.
    /// </summary>
    public static class CoopHarmony
    {
        /// <summary>
        /// Patches one method when it exists. Returns false and logs once when the type or method
        /// is missing, or when Harmony rejects the patch, so a feature can disable itself instead
        /// of failing its behaviour's initialization.
        /// </summary>
        public static bool TryPatch(this Harmony harmony, Type target, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, Type[] args = null)
        {
            if (harmony == null || target == null || string.IsNullOrEmpty(method))
            {
                return false;
            }

            MethodInfo original;
            try
            {
                original = args == null || args.Length == 0
                    ? AccessTools.Method(target, method)
                    : AccessTools.Method(target, method, args);
            }
            catch (Exception error)
            {
                CoopLog.WarnOnce("patch:resolve:" + target.FullName + "." + method,
                    "coop api: could not resolve patch target " + target.FullName + "." + method
                    + ": " + error.Message);
                return false;
            }

            if (original == null)
            {
                CoopLog.WarnOnce("patch:missing:" + target.FullName + "." + method,
                    "coop api: patch target missing: " + target.FullName + "." + method);
                return false;
            }

            try
            {
                harmony.Patch(original, prefix, postfix);
                return true;
            }
            catch (Exception error)
            {
                CoopLog.WarnOnce("patch:failed:" + target.FullName + "." + method,
                    "coop api: patch failed " + target.FullName + "." + method + ": " + error.Message);
                return false;
            }
        }
    }
}
