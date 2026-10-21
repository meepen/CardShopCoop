using System;
using HarmonyLib;

namespace CardShopCoop.Modules.DebugTools
{
    internal static class DebugToolsPatches
    {
        internal static void Apply(Harmony harmony)
        {
            // BEFORE the cheat-type check below, on purpose: the forced-entitlement hook belongs to a
            // DIFFERENT game type (the platform manager), which exists even on a build with no
            // CheatManager at all - and that build is exactly the one that option is for.
            //
            // IsFurnitureAvailable consults HasEntitlement for the deluxe/pre-order furniture, and
            // FurnitureShopUIScreen is its only caller. So this ONE method is the whole gate: no other
            // code in the game asks whether those objects may be used.
            var platformType = AccessTools.TypeByName("GA.PlatformManager");
            if (platformType == null)
            {
                CoopPlugin.Log.LogInfo("DebugTools: no platform manager on this build, so the forced-"
                    + "entitlement option has nothing to hook (this build has no deluxe/pre-order gate)");
            }
            else
            {
                TryPatch(harmony, platformType, "HasEntitlement",
                    prefix: new HarmonyMethod(typeof(DebugToolsPatches), nameof(ForcedEntitlementPrefix)));
                // The narrow, deluxe-only sibling of the above: it forces the deluxe furniture in the
                // shop WITHOUT touching HasEntitlement, so it never trips the coin/pack grant path.
                TryPatch(harmony, platformType, "IsFurnitureAvailable",
                    postfix: new HarmonyMethod(typeof(DebugToolsPatches),
                        nameof(ForcedDeluxeFurniturePostfix)));
            }

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

        /// <summary>Forced entitlements for testing. Read from the config on EVERY call rather than
        /// captured when the patch went in, so the option can be flipped in the .cfg while the game
        /// runs without a restart.
        ///
        /// PARAMETERLESS ON PURPOSE: the original takes a game enum (EntitlementKey), and this assembly
        /// must never name a game type in a patch signature. A Harmony prefix may omit parameters it
        /// does not need; returning false with __result set skips the game's own check entirely.</summary>
        internal static bool ForcedEntitlementPrefix(ref bool __result)
        {
            if (!CoopPlugin.ForceEntitlements.Value)
            {
                return true;
            }

            __result = true;
            return false;
        }

        /// <summary>Forced deluxe furniture for testing. Runs as a POSTFIX on
        /// <c>PlatformManager.IsFurnitureAvailable(EObjectType)</c>: it flips a false result to true
        /// only for the deluxe furniture, and never touches <c>HasEntitlement</c>, so it cannot reach
        /// the entitlement-grant path (the deluxe coins and packs). Read from the config on every call
        /// so it can be flipped in the .cfg while the game runs.
        ///
        /// The comparison is by NAME, never a direct member reference: DeluxeInfiniteScentMachine
        /// exists only on the Game Pass build, and this assembly must still load on the Steam build.
        /// EObjectType itself is safe to name - both builds carry it.</summary>
        internal static void ForcedDeluxeFurniturePostfix(EObjectType type, ref bool __result)
        {
            if (__result
                || CoopPlugin.ForceDeluxeFurniture == null
                || !CoopPlugin.ForceDeluxeFurniture.Value)
            {
                return;
            }

            if (string.Equals(type.ToString(), "DeluxeInfiniteScentMachine", StringComparison.Ordinal))
            {
                __result = true;
            }
        }

        /// <summary>Clients may close the menu, but may never open the shared-economy cheat UI.</summary>
        internal static bool CheatMenuOpenPrefix(bool open)
        {
            var allowed = !open || DebugToolsRuntime.CheatsEnabledForHost;
            if (open && allowed)
            {
                // CheatManager snapshots cursor/time state before opening. Release the co-op
                // window first so its vanilla restore remains authoritative on close.
                SessionInput.SessionInputRuntime.PrepareVanillaModalOpen();
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
