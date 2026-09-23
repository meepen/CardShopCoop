using System;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Modules.DebugTools
{
    internal static class DebugToolsPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var cheatType = DebugToolsRuntime.CheatManagerType;
            if (cheatType == null)
            {
                CoopPlugin.Log.LogInfo(
                    "DebugTools: game CheatManager is absent; cheat testing is disabled on this build");
                return;
            }

            TryPatch(harmony, cheatType, "SetMenuOpen",
                prefix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(CheatMenuOpenPrefix)),
                postfix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(CheatMenuChangedPostfix)));
            TryPatch(harmony, cheatType, "Start",
                prefix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(CheatManagerStartPrefix)));
            TryPatch(harmony, cheatType, "ApplyCheat",
                prefix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(CheatApplyPrefix)));
            TryPatch(harmony, cheatType, "GiveAllCards",
                prefix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(CheatGiveCardsPrefix)));

            CoopPlugin.Log.LogInfo("DebugTools: reflected CheatManager hooks installed");
        }

        /// <summary>Clients may close the menu, but may never open the shared-economy cheat UI.</summary>
        internal static bool CheatMenuOpenPrefix(bool open)
        {
            var allowed = !open || DebugToolsRuntime.CheatsEnabledForHost;
            if (open && allowed)
            {
                // CheatManager snapshots cursor/time state before opening. Release the co-op
                // window first so its vanilla restore remains authoritative on close.
                CardShopCoop.Modules.SessionInput.SessionInputRuntime.PrepareVanillaModalOpen();
            }

            return allowed;
        }

        internal static void CheatMenuChangedPostfix(bool open)
        {
            // Harmony postfixes run even when the prefix skipped the original. Re-evaluate the
            // gate so a blocked client open cannot make SessionInput hold the player still.
            DebugToolsRuntime.CheatMenuChanged(open && DebugToolsRuntime.CheatsEnabledForHost);
        }

        /// <summary>1.00's production CheatManager destroys itself in Start; retain it for F1 and
        /// the controller sequence, while the menu/action prefixes provide the explicit safety
        /// gates.</summary>
        internal static bool CheatManagerStartPrefix()
        {
            return false;
        }

        internal static bool CheatApplyPrefix()
        {
            return DebugToolsRuntime.CheatsEnabledForHost;
        }

        internal static bool CheatGiveCardsPrefix()
        {
            return DebugToolsRuntime.CheatsEnabledForHost;
        }

        private static void TryPatch(Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("DebugTools reflected patch target missing: "
                        + type.Name + "." + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("DebugTools reflected patch failed for " + type.Name
                    + "." + method + ": " + e.Message);
            }
        }
    }
}
