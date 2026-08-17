using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace CardShopCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.zwhit.cardshopcoop";
        public const string Name = "CardShopCoop";
        public const string Version = "1.0.39";

        public static ManualLogSource Log;

        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastJoinIP;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<float> SendRateHz;
        public static ConfigEntry<bool> AvatarsEnabled;
        public static ConfigEntry<KeyCode> UiToggleKey;
        public static ConfigEntry<KeyCode> EmoteKey;
        public static ConfigEntry<KeyCode> ServeKey;
        public static ConfigEntry<int> ClientWorldSlot;
        public static ConfigEntry<bool> AutoSyncCardDatabase;
        public static ConfigEntry<float> ServeReach;
        public static ConfigEntry<bool> HostServeKey;
        public static ConfigEntry<bool> AllowCrossBuildJoin;
        public static ConfigEntry<bool> AutoPortForward;
        public static ConfigEntry<bool> AutoLanPassword;

        private void Awake()
        {
            Log = Logger;
            Util.FileLog.Init(Paths.GameRootPath);
            Logger.LogEvent += (_, e) => Util.FileLog.Write($"{e.Level,-7} {e.Data}");

            Port = Config.Bind("Network", "Port", 27886,
                "TCP port used for hosting. Both PCs' firewalls must allow the game on this port.");
            LastJoinIP = Config.Bind("Network", "LastJoinIP", "192.168.1.100",
                "IP address of the host PC (remembered after a successful join).");
            PlayerName = Config.Bind("Player", "Name", System.Environment.UserName,
                "Name shown above your head on the other player's screen.");
            SendRateHz = Config.Bind("Network", "SendRateHz", 15f,
                "How many position updates per second to send (8-20 is sensible).");
            if (Mathf.Approximately(SendRateHz.Value, 12f))
                SendRateHz.Value = 15f; // migrate configs saved by earlier builds
            AvatarsEnabled = Config.Bind("Player", "AvatarsEnabled", true,
                "Show the other player as a walking character in your shop.");
            UiToggleKey = Config.Bind("Keys", "UiToggleKey", KeyCode.F2,
                "Toggles the co-op window. (F3 is reserved for future co-op options.)");
            if (UiToggleKey.Value == KeyCode.F11)
                UiToggleKey.Value = KeyCode.F2; // migrate configs saved by early builds
            EmoteKey = Config.Bind("Keys", "EmoteKey", KeyCode.G,
                "Sends a wave emote that pops above your avatar.");
            ServeKey = Config.Bind("Keys", "ServeKey", KeyCode.V,
                "When JOINING: stand at the register and press this to serve the customer (scan items, take payment, give change).");
            ClientWorldSlot = Config.Bind("Network", "ClientWorldSlot", 7,
                "Save slot the co-op world uses when JOINING someone (your own slots 0-3 are never touched). On a PC dedicated to co-op you can set 0 for maximum mod-data fidelity.");
            AutoSyncCardDatabase = Config.Bind("Network", "AutoSyncCardDatabase", true,
                "When your modded-card ID registry (EPL enum_values.json) differs from the host's, automatically install the host's copy (yours is backed up beside it) so you only need to restart and rejoin. Set false to handle the file yourself.");
            ServeReach = Config.Bind("Player", "ServeReach", 1.6f,
                "How close (meters, to the counter's center) a JOINER must stand to serve the register or a trade customer. The counter itself is ~1m wide, so values below ~1.2 make it unreachable.");
            HostServeKey = Config.Bind("Keys", "HostServeKey", false,
                "Let the HOST also use the serve key to run the register (quick-serve, bypassing the minigame) - the same shortcut joiners get. ADDITIVE to the game's normal mouse serving; off by default.");
            AllowCrossBuildJoin = Config.Bind("Network", "AllowCrossBuildJoin", false,
                "Let players join even when the GAME build fingerprint (game version / Unity version) differs from the host's. Dangerous: two different game builds can corrupt each other's saves. Only enable for supervised testing of Steam <-> Game Pass cross-play.");
            AutoPortForward = Config.Bind("Network", "AutoPortForward", true,
                "While you are LAN-hosting, ask your router over UPnP to open the co-op port so a friend outside your house can join with an invite code - and remove that opening again when you stop hosting. Turn it off if you forward the port yourself, or if you would rather nothing touched the router. Either way the invite code still works inside your own house.");
            AutoLanPassword = Config.Bind("Network", "AutoLanPassword", true,
                "Hosting via LAN generates a random session password. It is baked into the invite code (so a friend using the code notices nothing), shown in the host panel for a friend typing your IP by hand, and checked exactly like the Steam lobby password. Leave this on: the port your router opens for you is a door into your game, and this is the lock on it.");

            // PLATFORM LINE FIRST, ABOVE EVERYTHING THAT CAN FAIL. This is the line a Game
            // Pass player (or a support thread) is told to look for, and it is most useful
            // precisely in the session where CoopCore fails to load - so it must not sit
            // downstream of the very failure it explains.
            //
            // IT REPORTS ONLY WHAT IT ACTUALLY KNOWS (1.0.39). PlatformProbe answers exactly
            // one question - "can this process bind the Steamworks wrapper?" - and the field
            // proved that is NOT the same question as "will Steam work here": a Game Pass
            // install carrying a stray com.rlabrecque.steamworks.net.dll answers TRUE, and the
            // old wording then PROMISED that player "Steam lobbies, invites and P2P available"
            // on a build where Steam can never run. So this line is now a neutral statement of
            // fact about the assembly, and the promise moved to CoopCore.Awake - the first
            // point that knows whether the bridge actually came up. Do not put it back here:
            // nothing at this point in startup is entitled to make it.
            if (!Net.PlatformProbe.SteamworksPresent)
                Log.LogInfo("Steamworks assembly not detected - LAN and direct IP only (a HarmonyX ReflectionTypeLoadException warning naming Steamworks types may appear when any mod - including this one - enumerates loaded types; it is EXPECTED on this build and harmless)");
            else
                Log.LogInfo("Steamworks assembly detected.");

            // SECOND LINE, ON PURPOSE. The line above answers "can Steam UI exist here"; this
            // one answers "where do this game's saves live", and 1.0.38 shipped a field report
            // where the first was true and the second was not (a stray
            // com.rlabrecque.steamworks.net.dll in a Game Pass install). SaveTransfer no longer
            // infers one from the other, and neither should the log.
            Log.LogInfo("save backend: " + Net.PlatformProbe.SaveBackendDescription +
                        " - decided from the game's own save-completion counter at join time, never from Steamworks presence.");

            // ORDER MATTERS, AND NOT THE WAY YOU'D EXPECT. CoopCore is added FIRST and the
            // Harmony patches only go in if that succeeded.
            //
            // Every patch body reads CoopCore statics (Role, GuestBorrowedWorld), so the
            // patches inherit CoopCore's type-load fate. With the old order - patch first,
            // AddComponent second - a CoopCore that failed to load left the patches LIVE
            // and unbacked: BepInEx catches the AddComponent failure and logs "Error loading
            // [CardShopCoop]", but every prefix then throws at first JIT inside a patched
            // GAME method (CGameManager.SaveGameData, CustomerManager.Update,
            // CPlayerData.AddCard...). A dead mod is a nuisance; a dead mod that breaks
            // saving and customers is a broken GAME. This way the failure degrades to
            // clean vanilla.
            var go = new GameObject("CardShopCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            bool coreLoaded;
            try
            {
                AttachCore(go, HostServeKey.Value);
                coreLoaded = true;
            }
            catch (System.Exception e)
            {
                // TypeLoadException lands here when a dependency of CoopCore is missing on
                // this platform. Say so loudly - this is the line that explains an otherwise
                // silent "co-op window won't open".
                coreLoaded = false;
                Log.LogError($"{Name} {Version}: CoopCore FAILED TO LOAD - co-op is disabled this session and NO game patches were installed (the game runs exactly as vanilla). {e.GetType().Name}: {e.Message}");
                try { Destroy(go); } catch { }
            }

            if (coreLoaded)
            {
                var harmony = new Harmony(Guid);
                Patches.GamePatches.ApplyAll(harmony);
            }

            // Only claim the window works if the component that draws it actually loaded -
            // the LogError above is the whole story otherwise.
            if (coreLoaded)
                Log.LogInfo($"{Name} {Version} loaded. Press {UiToggleKey.Value} in-game to open the co-op window.");
        }

        /// <summary>
        /// THE ONLY METHOD IN THIS FILE THAT NAMES CoopCore, and deliberately NoInlining -
        /// the same two-method shape as <see cref="Net.SteamBridge.TryCreate"/> /
        /// <see cref="Net.SteamBridge.Create"/>, for the same reason. A method body that
        /// merely MENTIONS a type that cannot load throws at JIT, before the body (and any
        /// try/catch inside it) ever runs, so the handler that saves us has to live in a
        /// CALLER whose own body is clean. Inline this into Awake and the AddComponent
        /// token - and CoopCore's type-load fault with it - migrates up past the catch,
        /// which takes the whole plugin down on a build where CoopCore can't load.
        ///
        /// The HostServeKeyEnabled write lives HERE, not in Awake, and after the
        /// AddComponent on purpose: a stsfld is a type-init trigger exactly like the
        /// AddComponent is, so writing it in Awake's body would force CoopCore's load
        /// outside the catch and defeat the whole arrangement.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AttachCore(GameObject go, bool hostServeKey)
        {
            go.AddComponent<CoopCore>();
            CoopCore.HostServeKeyEnabled = hostServeKey;
        }
    }
}
