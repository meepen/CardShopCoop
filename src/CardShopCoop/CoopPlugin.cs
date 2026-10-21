using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop
{
    /// <summary>How loudly a graded-album divergence is announced ON SCREEN.
    /// The LOG is never gated by this - a support log has to be complete whatever the player
    /// chose, and the whole point of the setting is to quiet the screen without going blind.</summary>
    public enum GradedAlertMode
    {
        Always, OncePerSession, Never
    }

    [BepInPlugin(Guid, Name, Version)]
    // SOFT dependency on Grading Overhaul: it changes NOTHING when GO is absent, and when GO is
    // present it guarantees GO is loaded before our Awake. That ordering is load-bearing now that
    // Sync/WorldGradingInteraction patches GO's OWN day-start prefix and resolves GO's submit validator at
    // patch time (see TryPatchGoDayStart) - both are one-shot reflective lookups, so a guest that
    // happened to sort ahead of GO in the chainloader would silently lose the guard that stops it
    // minting certificate numbers. Guid copied from GO's own BepInPlugin (decompiled-grading
    // :16912), which is also the Harmony owner id used in the `before` array over there.
    [BepInDependency("munch.gradingoverhaul", BepInDependency.DependencyFlags.SoftDependency)]
    // SOFT dependency on EnhancedPrefabLoader: no effect when EPL is absent, and when it is
    // present it guarantees EPL's plugin initializes before ours. CatalogInterop probes the EPL
    // surface exactly once at start (ProbeEplIfNeeded) and caches the result; this ordering is
    // what makes "check on start only" safe instead of a chainloader-order race. Guid copied
    // from EPL's own BepInPlugin.
    [BepInDependency("EnhancedPrefabLoader", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.meepen.cardshopcoop";
        public const string Name = "CardShopCoopCommunity";
        public static ManualLogSource Log;

        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastJoinIP;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<bool> NameOverride;
        public static ConfigEntry<float> SendRateHz;
        public static ConfigEntry<bool> AvatarsEnabled;
        public static ConfigEntry<bool> AllowNsfw;
        public static ConfigEntry<KeyCode> UiToggleKey;
        public static ConfigEntry<KeyCode> EmoteKey;
        public static ConfigEntry<int> ClientWorldSlot;
        public static ConfigEntry<float> ServeReach;
        public static ConfigEntry<bool> AllowCrossBuildJoin;
        public static ConfigEntry<bool> AutoPortForward;
        public static ConfigEntry<GradedAlertMode> GradedDriftAlert;
        public static ConfigEntry<bool> PerfDebug;
        public static ConfigEntry<int> ArtificialLagMs;
        public static ConfigEntry<int> ArtificialJitterMs;
        public static ConfigEntry<bool> EnableGameCheatMenu;
        public static ConfigEntry<bool> ShowHiddenCategory;

        /// <summary>TESTING ONLY, and deliberately not surfaced in the window: it lives in the Hidden
        /// config section, which players only reach by editing the .cfg by hand. It makes the game
        /// answer "yes" to every store entitlement, so the deluxe/pre-order object types can be
        /// exercised on a build or an account that does not own them.</summary>
        public static ConfigEntry<bool> ForceEntitlements;

        /// <summary>TESTING ONLY, Game Pass ONLY: bound only when this build has the entitlement gate
        /// (GA.PlatformManager), so it is null - and the hidden-menu toggle is absent - on the Steam
        /// build. It unlocks ONLY the deluxe furniture (the infinite scent machine), for testing with
        /// deluxe owners; unlike <see cref="ForceEntitlements"/> it never unlocks pre-order furniture
        /// and never grants the deluxe coins or packs.</summary>
        public static ConfigEntry<bool> ForceDeluxeFurniture;

        private void Awake()
        {
            Log = Logger;
            // Install the public integration contract before anything else can read it. External
            // mods may touch CoopApi from their own Awake, which runs after ours.
            CoopApi.Binding = Runtime.ExternalCoopMods.Instance;
            CoopApi.Log = Logger;
            ProtectPluginHost();
            Runtime.SceneRef.Install();
            // The Unity 6 builds latch CSingleton<T> forever (see SingletonLatchGuard); repair the
            // latch before any game scene can hand a destroyed object to a second world's Awake.
            Util.SingletonLatchGuard.Install();
            Util.ScreenSingletonGuard.Install();
            try
            {
                Assembly.Load("Newtonsoft.Json");
                Logger.LogInfo("network payload serializer: Newtonsoft.Json");
            }
            catch (Exception e)
            {
                Logger.LogError("network payload serializer unavailable: Newtonsoft.Json.dll is required (" + e.Message + ")");
            }
            Util.FileLog.Init(Paths.GameRootPath);
            Logger.LogEvent += (_, e) => Util.FileLog.Write($"{e.Level,-7} {e.Data}");

            Port = Config.Bind("Network", "Port", 27886,
                "UDP port used for LAN/KCP hosting. Both PCs' firewalls must allow the game on this port.");
            LastJoinIP = Config.Bind("Network", "LastJoinIP", "192.168.1.100",
                "IP address of the host PC (remembered after a successful join).");
            PlayerName = Config.Bind("Player", "Name", Environment.UserName,
                "Fallback name shown above your head on the other player's screen. By default the mod uses your platform name instead (your Xbox gamertag on Game Pass, your Steam name on Steam) and this is only the last resort.");
            NameOverride = Config.Bind("Player", "NameOverride", false,
                "Set automatically by the co-op window: true once you type your own name into the \"Your name\" box, false again the moment you clear it (the box refills with your platform name once you click away). Leave it alone to keep using your platform name whenever one is available.");
            SendRateHz = Config.Bind("Network", "SendRateHz", 15f,
                "How many position updates per second to send (8-20 is sensible).");
            if (Mathf.Approximately(SendRateHz.Value, 12f))
            {
                SendRateHz.Value = 15f; // migrate configs saved by earlier builds
            }

            AvatarsEnabled = Config.Bind("Player", "AvatarsEnabled", true,
                "Show the other player as a walking character in your shop.");
            AllowNsfw = Config.Bind("Player", "AllowNsfw", false,
                "Allow nude/NSFW character appearances. When off, new characters get a random clothed preset, the Nude wardrobe option is hidden, and fully nude players are shown in the game's random clothed customer look. When on, you may choose Nude and you will see other players who chose it.");
            UiToggleKey = Config.Bind("Keys", "UiToggleKey", KeyCode.F2,
                "Toggles the co-op window. (F3 is reserved for future co-op options.)");
            if (UiToggleKey.Value == KeyCode.F11)
            {
                UiToggleKey.Value = KeyCode.F2; // migrate configs saved by early builds
            }

            EmoteKey = Config.Bind("Keys", "EmoteKey", KeyCode.G,
                "Sends a wave emote that pops above your avatar.");
            ClientWorldSlot = Config.Bind("Network", "ClientWorldSlot", -100,
                "Save slot the co-op world uses when JOINING someone. The default -100 is a reserved negative slot the save UI never shows, so your own local saves (0-3) are never touched. Only change this if you need the borrowed world to live at a specific slot.");
            ServeReach = Config.Bind("Player", "ServeReach", 1.6f,
                "How close (meters, to the counter's center) a JOINER must stand to answer a counter trade customer. The counter itself is ~1m wide, so values below ~1.2 make it unreachable.");
            AllowCrossBuildJoin = Config.Bind("Network", "AllowCrossBuildJoin", false,
                "Let players join even when the GAME build fingerprint (game version / Unity version) differs from the host's. Dangerous: two different game builds can corrupt each other's saves. Only enable for supervised testing of Steam <-> Game Pass cross-play.");
            AutoPortForward = Config.Bind("Network", "AutoPortForward", true,
                "While you are hosting, ask your router to open the co-op port so a friend outside your house can join - and remove that opening again when you stop hosting. Turn this off if you forward the port yourself: with it off the mod never touches the router.");
            GradedDriftAlert = Config.Bind("Graded", "DriftAlert", GradedAlertMode.Always,
                "How loudly to announce that your graded albums have drifted apart. THIS CONTROLS THE HOST'S SCREEN. While you are HOSTING it decides both your own on-screen line and the heads-up sent to the joiner. While you are JOINING it does nothing at all: the host's setting alone decides whether you get that heads-up, because the drift is only ever announced from the host's side. Always: announce every check that finds a difference. OncePerSession: say it once per player per session and then stay quiet - including for a later, bigger difference. Never: never put it on screen at all. The log records every check whichever you pick and on both PCs, so a support log stays complete whatever you choose; this only controls the screen, and it never changes what the co-op panel's adopt button offers.");
            PerfDebug = Config.Bind("Diagnostics", "PerfDebug", false,
                "Enable Unity hitch profiling. Logs frames slower than 33 ms with available main-thread, GC, rendering, physics, and co-op stage timings; individual stages above 5 ms are also rate-limited. Also logs a [perf-update] breakdown of every mod Update/LateUpdate and Harmony patch body each window. Enable temporarily to find lag spikes; it does not change gameplay.");
            ArtificialLagMs = Config.Bind("Diagnostics", "ArtificialLagMs", 0,
                "TESTING ONLY. Adds this many milliseconds of latency to every network message you RECEIVE. Set the same value on both PCs for symmetric lag (each hop adds one delay, so a round trip is roughly twice the value). 0 disables it.");
            ArtificialJitterMs = Config.Bind("Diagnostics", "ArtificialJitterMs", 0,
                "TESTING ONLY. Randomly varies each received message's artificial latency by up to this many milliseconds, plus or minus, so the connection feels unstable. 0 is a steady delay. Only meaningful while ArtificialLagMs is above 0.");
            EnableGameCheatMenu = Config.Bind("Hidden", "EnableGameCheatMenu", false,
                "TESTING ONLY. Allows the game's 1.00 cheat menu to open in solo mode or for the HOST. Clients are always blocked. Requires ShowHiddenCategory=true as an additional safety gate.");
            ShowHiddenCategory = Config.Bind("Hidden", "ShowHiddenCategory", false,
                "TESTING ONLY. Shows the hidden category in the CardShopCoopCommunity window. The game cheat menu cannot open unless this and EnableGameCheatMenu are both enabled.");
            ForceEntitlements = Config.Bind("Hidden", "ForceEntitlements", false,
                "TESTING ONLY - NOT FOR PLAYERS. Makes the game answer \"yes\" to every store entitlement (the Game Pass deluxe and pre-order bonuses: their furniture and their coins), as if this account owned them. It exists so the deluxe-only object types can be tested on a build that does not have them. It changes only what the furniture shop offers; it grants nothing else. Off by default, and hidden from the window on purpose.");

            // Bind this option wherever the deluxe/pre-order gate it forces actually exists, i.e.
            // wherever GA.PlatformManager is present. That is the Unity 6 / 1.00 builds - the Steam
            // 1.00 beta AND the Xbox/Game Pass build - NOT the legacy public build, which has no
            // PlatformManager at all. GA.PlatformManager alone is NOT the Game Pass discriminator
            // (GA.GamecoreUserManager is); see PlatformProbe. The hidden menu draws the option only
            // when it is bound, so it stays off builds with no such gate.
            if (Type.GetType("GA.PlatformManager, Assembly-CSharp") != null)
            {
                ForceDeluxeFurniture = Config.Bind("Hidden", "ForceDeluxeFurniture", false,
                    "TESTING ONLY. Makes the game treat the deluxe-edition furniture (the infinite scent machine) as owned, so you can buy and place it while testing with deluxe owners. It does NOT unlock pre-order furniture and does NOT grant the deluxe coins or packs - use ForceEntitlements for that blunt version. Available only on builds that carry the deluxe/pre-order gate (the 1.00 / Unity 6 builds).");
            }

            // Bind one enable/disable checkbox per feature module and say exactly what will run.
            // Kept beside the config binds so the [modules] line appears before anything it can
            // disable.
            Runtime.ModuleCatalog.Bind(Config);
            Runtime.ModuleCatalog.LogConfiguration();
            // Install the generic mod Update profiler up front when PerfDebug is already on,
            // so the wrappers exist before the first Update; PerfProbe re-installs lazily if the
            // setting is turned on later.
            Util.UpdateProfiler.Install();

            // There USED to be a "platform line" here (Steamworks assembly present or not). It is
            // gone: Steam availability is now reported by the Steam transport plugin when it
            // registers (see CardShopCoop.Steam), and this assembly no longer probes for
            // Steamworks itself. Do not re-add a second source for that answer.

            // THE LINE BELOW ANSWERS A DIFFERENT QUESTION: where this game's saves live. 1.0.38
            // shipped a field report where "can Steam UI exist here" was true and the save backend
            // was not (a stray com.rlabrecque.steamworks.net.dll in a Game Pass install), so
            // SaveTransfer no longer infers one from the other - and neither should the log.
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
                AttachCore(go);
                coreLoaded = true;
            }
            catch (Exception e)
            {
                // TypeLoadException lands here when a dependency of CoopCore is missing on
                // this platform. Say so loudly - this is the line that explains an otherwise
                // silent "co-op window won't open".
                coreLoaded = false;
                Log.LogError($"{Name} {Version}: CoopCore FAILED TO LOAD - co-op is disabled this session and NO game patches were installed (the game runs exactly as vanilla). {e.GetType().Name}: {e.Message}");
                try
                {
                    Destroy(go);
                }
                catch (Exception ex) { CoopLog.Swallow(ex); }
            }

            // Only claim the window works if the component that draws it actually loaded -
            // the LogError above is the whole story otherwise.
            if (coreLoaded)
            {
                Log.LogInfo($"{Name} {Version} loaded. Press {UiToggleKey.Value} in-game to open the co-op window.");
            }
        }

        /// <summary>
        /// THE ONLY METHOD IN THIS FILE THAT NAMES CoopCore, and deliberately NoInlining, for
        /// the same reason the Steam transport keeps bridge construction behind a type that is
        /// allowed to fail to load. A method body that
        /// merely MENTIONS a type that cannot load throws at JIT, before the body (and any
        /// try/catch inside it) ever runs, so the handler that saves us has to live in a
        /// CALLER whose own body is clean. Inline this into Awake and the AddComponent
        /// token - and CoopCore's type-load fault with it - migrates up past the catch,
        /// which takes the whole plugin down on a build where CoopCore can't load.
        ///
        /// The AddComponent lives HERE, not in Awake, on purpose: it is a type-init trigger,
        /// so putting it in Awake's body would force CoopCore's load outside the catch and
        /// defeat the whole arrangement.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AttachCore(GameObject go)
        {
            go.AddComponent<CoopCore>();
        }

        /// <summary>
        /// Keeps BepInEx's plugin host - and with it EVERY plugin component, ours and third
        /// parties' - alive past the game's first scene load.
        ///
        /// BepInEx 5 adds every plugin as a component of one shared "BepInEx_Manager"
        /// GameObject that it protects with DontDestroyOnLoad alone
        /// (BepInEx.Bootstrap.Chainloader.Start). That protection does not hold here: the
        /// manager is created during engine startup (the Chainloader patch runs inside
        /// UnityEngine.InputSystem's static constructor), before any scene exists, and this game
        /// tears it down when the first scene loads. Reproduced on this machine with BepInEx's
        /// defaults: right after "Chainloader startup complete" every plugin's OnDestroy runs -
        /// other mods log their own "Unloaded!", and the Steam transport unregistered its
        /// networking backend, which removed the Steam host option for the rest of the run
        /// (reported as "after updating to 2.1.0 the option to host via Steam is missing").
        ///
        /// This mod's core survives that teardown only because its real work lives on the
        /// DontDestroyOnLoad + HideAndDontSave object below - the same combination BepInEx
        /// applies when its own HideManagerGameObject option is on, and the only difference
        /// between a surviving install and a broken one. Marking the manager DontSave here
        /// applies exactly that protection while leaving the object visible, so no user setting
        /// is overridden and no plugin has to survive its own host being destroyed. This runs
        /// from this mod's Awake, which the chainloader reaches before the first scene loads.
        /// </summary>
        private static void ProtectPluginHost()
        {
            var manager = Chainloader.ManagerObject;
            if (manager == null)
            {
                return;
            }

            manager.hideFlags |= HideFlags.DontSave;
            Log.LogInfo("[host] BepInEx plugin manager marked DontSave so the first scene load "
                + "cannot destroy it (and every plugin on it).");
        }
    }
}




