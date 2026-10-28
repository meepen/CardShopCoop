using CardShopCoop.Util;
using System;
using System.Reflection;
using CardShopCoop.Api;

namespace CardShopCoop.Net
{
    /// <summary>
    /// THE STEAM ISOLATION BOUNDARY.
    ///
    /// The Game Pass build of Card Shop Simulator ships a STUB assembly that is merely NAMED
    /// com.rlabrecque.steamworks.net (4 KB, two Unity-generated types, none of the Steamworks
    /// API), not the real wrapper. Assembly presence therefore proves nothing - the real test is
    /// whether the required types bind. The CLR's failure mode when they do not is brutal and
    /// non-obvious: a value-type (struct) FIELD whose type lives in a missing assembly
    /// kills the load of the whole CONTAINING TYPE, and a method body that merely
    /// mentions such a type (including generic instantiations like Action&lt;CSteamID&gt;
    /// or List&lt;TSteamType&gt;) throws at JIT the first time it is called.
    /// A try/catch in the SAME method never catches that - the throw happens before the
    /// body runs, so the catch must live in a CALLER whose own body is clean.
    ///
    /// So: nothing in this file may EVER name a Steamworks type. No `using Steamworks;`,
    /// no CSteamID, not even in a generic argument. Deleting that using statement from
    /// CoopCore.cs is what turns "did I miss a reference?" into a compiler error, and this file
    /// is what lets that assembly compile without it.
    ///
    /// Everything Steam-typed lives behind a bridge interface implemented in the Steam
    /// transport assembly, which is allowed to fail to load. This file never names it - the
    /// only path that reaches it goes through the Steam plugin's own factory.
    /// </summary>
    /// <remarks>
    /// The same hazard applies to any Steam-typed generic that crosses this boundary: a
    /// List&lt;TSteamType&gt; or Action&lt;TSteamType&gt; whose type argument lives in the
    /// Steam transport assembly takes the whole member with it when that assembly cannot
    /// load, even if the value is never dereferenced. SteamBridgeImpl converts between the
    /// Steam-typed and Steam-free worlds before anything crosses.
    /// </remarks>
    /// <summary>
    /// Reflection-only usability test for the Steamworks wrapper (<c>com.rlabrecque.steamworks.net</c>).
    /// Cached once, never throws, and never names a Steamworks type in metadata - the assembly
    /// name and the type names are STRINGS, which is the whole trick. It tests the REQUIRED
    /// TYPES, not the assembly name, because the Game Pass build ships a same-named stub.
    /// </summary>
    public static class PlatformProbe
    {
        /// <summary>The Steamworks surface the Steam bridge actually binds. Presence of
        /// an assembly NAMED com.rlabrecque.steamworks.net is not enough: the Xbox / Game Pass
        /// build ships a 4 KB stub under exactly that name (types: &lt;Module&gt; and Unity's
        /// generated script list) that has none of these. Binding every one of them is the only
        /// test that separates the real package from the stub.</summary>
        private static readonly string[] RequiredSteamworksTypes =
        {
            "Steamworks.SteamAPI",     // the wrapper entry point
            "Steamworks.CallResult`1", // async result handle (SteamLobby._createLobby)
            "Steamworks.Callback`1",   // persistent callback handle (lobby events)
        };

        private const string SteamworksAssemblyName = "com.rlabrecque.steamworks.net";

        private static int _state; // 0 = unknown, 1 = usable, 2 = not

        /// <summary>True when the REAL Steamworks wrapper is usable in this process - the
        /// assembly resolves AND exposes the required types. Evaluated at most once per session.
        ///
        /// This answers "can this process bind the Steamworks wrapper", NOT "which save backend
        /// does the game use" (see <see cref="GamePassBuild"/>/save-counter for that) and NOT
        /// "does an assembly with that name exist".</summary>
        public static bool SteamworksPresent
        {
            get
            {
                if (_state == 0)
                {
                    _state = Detect() ? 1 : 2;
                }

                return _state == 1;
            }
        }

        private static bool Detect()
        {
            // (a) Already loaded? Verify the LOADED assembly exposes the required types. The Game
            //     Pass stub is loaded by the game's own script-type scan, so a simple-name match
            //     alone would answer "present" - the exact false positive that let SteamLobby
            //     fail to load. There is one assembly per identity, so a loaded stub is final.
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(a.GetName().Name, SteamworksAssemblyName,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        return HasRequiredTypes(a);
                    }
                }
            }
            catch (Exception e) { CoopLog.Swallow(e); }

            // (b) Not loaded YET? Force a partial bind of each required type. DO NOT "SIMPLIFY"
            //     THIS AWAY: our Awake can easily run before the game first touches Steamworks,
            //     so (a) alone would return a false negative on a real Steam install and hide the
            //     Steam UI permanently. This is the branch that actually fires on Steam.
            //     throwOnError:false - a stub (or absent assembly) resolves to null, never throws.
            foreach (var typeName in RequiredSteamworksTypes)
            {
                try
                {
                    if (Type.GetType(typeName + ", " + SteamworksAssemblyName, false) == null)
                    {
                        return false;
                    }
                }
                catch (Exception e) { CoopLog.Swallow(e); return false; }
            }

            return true;
        }

        /// <summary>Do the required Steamworks types resolve inside THIS assembly? For the
        /// already-loaded branch, where the assembly object is in hand and a same-named stub must
        /// be rejected.</summary>
        private static bool HasRequiredTypes(Assembly assembly)
        {
            foreach (var typeName in RequiredSteamworksTypes)
            {
                try
                {
                    if (assembly.GetType(typeName, false) == null)
                    {
                        return false;
                    }
                }
                catch (Exception e) { CoopLog.Swallow(e); return false; }
            }

            return true;
        }

        // =====================================================================
        // SAVE-BACKEND ORACLE (added 2026-08-17 for 1.0.39).
        //
        // NEVER USE SteamworksPresent TO DECIDE ANYTHING ABOUT SAVES. It answers a
        // question about OUR dll-resolution environment ("can this process bind the
        // Steamworks wrapper?"), not about which backend the GAME writes saves with, and
        // the field proved the difference: a Game Pass install carrying a stray
        // com.rlabrecque.steamworks.net.dll (bundled by another mod, or left over from a
        // fork) answers TRUE, so both of SaveTransfer's gates took the Steam branch -
        // hosting threw FileNotFoundException demanding a savedGames_Release6.json the
        // wgs backend never writes, and joining skipped the m_SavedGame injection and
        // loaded the guest's own world instead of the host's. Assembly presence is the
        // right oracle for "can Steam UI exist here", the wrong one for "where do saves
        // live". Two different questions; keep them apart.
        //
        // The oracle that IS right is the game's own save-completion counter -
        // CPlayerData.m_SaveIndex/m_SaveCycle, bumped inside CGameData.SaveGameData
        // UPSTREAM of the backend call, so it advances identically on the Steam json
        // writer and on the Game Pass Gamecore/wgs writer. Sample it either side of
        // CGameManager.SaveGameData: a bump is positive proof the save ran all the way
        // through its four guards, no bump is positive proof it bailed. That is the same
        // completion proof the slot-file freshness clock buys on Steam, obtained from the
        // game instead of from the filesystem, and it needs no platform knowledge at all.
        //
        // <see cref="GamePassBuild"/> below is the LAST-RESORT tiebreak, used only when
        // the counter fields cannot be found (an unknown future build) - never as the
        // primary answer.
        //
        // Everything here is string-keyed reflection: this file names no game type in its
        // metadata either, so nothing in it can fault at JIT on a reshaped build.
        // =====================================================================

        private static Assembly _gameAsm;
        private static bool _gameAsmSearched;

        /// <summary>The game's Assembly-CSharp, by SIMPLE NAME, or null. Cached.</summary>
        private static Assembly GameAssembly()
        {
            if (!_gameAsmSearched)
            {
                _gameAsmSearched = true;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(a.GetName().Name, "Assembly-CSharp",
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            _gameAsm = a;
                            break;
                        }
                    }
                }
                catch (Exception e) { CoopLog.Swallow(e); }
            }
            return _gameAsm;
        }

        private static FieldInfo _saveIndexField, _saveCycleField;
        private static bool _counterSearched;

        /// <summary>
        /// PRIMARY SAVE ORACLE. Reads the game's save-completion counter pair
        /// (CPlayerData.m_SaveIndex, CPlayerData.m_SaveCycle). Returns false - meaning
        /// "unknown build, this oracle cannot answer" - when either field is missing;
        /// callers must fall through to <see cref="GamePassBuild"/> in that case, never
        /// treat false as "the save did not run".
        ///
        /// Sample it BEFORE and AFTER CGameManager.SaveGameData and compare the PAIR: the
        /// index wraps to 0 and bumps the cycle at 1e9, so index alone is not enough.
        ///
        /// Read CPlayerData's statics, NOT CSaveLoad.m_SavedGame.m_SaveIndex: on Game Pass
        /// the Gamecore writer may not re-point m_SavedGame the way CSaveLoad.Save does,
        /// but CPlayerData's counter is bumped before the backends split.
        /// </summary>
        public static bool TrySampleSaveCounter(out int index, out int cycle)
        {
            index = 0;
            cycle = 0;
            try
            {
                if (!_counterSearched)
                {
                    _counterSearched = true;
                    var t = GameAssembly()?.GetType("CPlayerData");
                    if (t != null)
                    {
                        _saveIndexField = ReflectionSurface.OptionalField(t, "m_SaveIndex");
                        _saveCycleField = ReflectionSurface.OptionalField(t, "m_SaveCycle");
                    }
                }
                if (_saveIndexField == null || _saveCycleField == null)
                {
                    return false;
                }

                index = (int)_saveIndexField.GetValue(null);
                cycle = (int)_saveCycleField.GetValue(null);
                return true;
            }
            catch { index = 0; cycle = 0; return false; }
        }

        private static int _gpState; // 0 = unknown, 1 = Game Pass, 2 = not

        /// <summary>
        /// LAST-RESORT save-backend tiebreak: does this build write saves through the native
        /// platform store? The game answers this itself with
        /// <c>GA.PlatformManager.UseNativeSaves()</c> (Steam -> false, Game Pass/consoles ->
        /// true); that is always preferred. Before <c>GA.PlatformManager.Instance</c> exists the
        /// exact <c>GA.GamecorePlatformManager</c> type is the discriminator - the same type the
        /// Xbox identity chain already trusts.
        ///
        /// Never match on the substring "Gamecore": the Steam 1.03 build ships an empty
        /// <c>GA.GameCoreNativePlugin</c> whose name matches case-insensitively, and that false
        /// positive re-enables the stale in-memory-world fallback in SaveTransfer. Cached;
        /// never throws.
        ///
        /// Only consult this when <see cref="TrySampleSaveCounter"/> returns false.
        /// </summary>
        public static bool GamePassBuild
        {
            get
            {
                if (_gpState == 0)
                {
                    _gpState = DetectGamecore() ? 1 : 2;
                }

                return _gpState == 1;
            }
        }

        private static bool DetectGamecore()
        {
            var asm = GameAssembly();
            if (asm == null)
            {
                return false;
            }

            // The game's own answer. It is authoritative whenever the manager is alive.
            try
            {
                var managerType = asm.GetType("GA.PlatformManager", false);
                if (managerType != null)
                {
                    var instanceProperty = managerType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var useNativeSaves = managerType.GetMethod("UseNativeSaves",
                        BindingFlags.Public | BindingFlags.Instance);
                    var instance = instanceProperty?.GetValue(null);
                    if (instance != null && useNativeSaves != null)
                    {
                        return (bool)useNativeSaves.Invoke(instance, null);
                    }

                    // Before Instance exists (startup), only the Game Pass implementation
                    // discriminates. Steam carries GA.PlatformManager with no Gamecore manager.
                    return asm.GetType("GA.GamecorePlatformManager", false) != null;
                }
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }

            return false;
        }

        // =====================================================================
        // XBOX IDENTITY.
        //
        // On the Game Pass build the player's Xbox gamertag is the natural default display
        // name. It lives on GA.PlatformManager.Instance.m_platformImpl (a
        // GamecorePlatformManager) -> m_userManager -> CurrentUserData.UserGamertag.
        //
        // GA.PlatformManager EXISTS ON THE STEAM-SIDE UNITY 6 BUILDS TOO (1.00 and
        // 1.0newrender): there CreatePlatformManager() returns a nested DefaultPlatformManager
        // and GetPlatform() is EPlatform.Unknown, with no user manager at all. So the
        // discriminator is GA.GamecoreUserManager - present ONLY on the Game Pass build - never
        // GA.PlatformManager by itself. Gating on the manager type would hand a Steam beta the
        // gamertag of whatever Xbox account happened to be signed in on the PC.
        //
        // The gamertag arrives ASYNCHRONOUSLY (GamecoreUserManager.GetBasicInfo after sign-in),
        // so only the resolvability of the surface is cached; the VALUE is read fresh on every
        // call. Caching the value would freeze the empty string read before sign-in; reading it is
        // a handful of reflection field reads, at session start and on co-op window draw.
        //
        // String-keyed reflection throughout: this file still names no game type in metadata.
        // =====================================================================

        private static int _xgState; // 0 = unknown, 1 = resolvable, 2 = not
        private static PropertyInfo _xgManagerInstance;
        private static FieldInfo _xgImplField;
        private static FieldInfo _xgUserManagerField;
        private static PropertyInfo _xgCurrentUserData;
        private static FieldInfo _xgGamertagField;

        /// <summary>The local Xbox gamertag, or an empty string on every non-Game-Pass build and
        /// before sign-in completes. Resolved by reflection; never throws.</summary>
        public static string XboxGamertag
        {
            get
            {
                if (_xgState == 0)
                {
                    _xgState = ResolveXboxIdentity() ? 1 : 2;
                }

                if (_xgState != 1)
                {
                    return "";
                }

                try
                {
                    var manager = _xgManagerInstance.GetValue(null);
                    var impl = manager == null ? null : _xgImplField.GetValue(manager);
                    var userManager = impl == null ? null : _xgUserManagerField.GetValue(impl);
                    var userData = userManager == null ? null : _xgCurrentUserData.GetValue(userManager);
                    return userData == null ? "" : (_xgGamertagField.GetValue(userData) as string ?? "");
                }
                catch (Exception e)
                {
                    CoopLog.Swallow(e);
                    return "";
                }
            }
        }

        /// <summary>Bind the gamertag chain once. False means "not the Game Pass build", which is
        /// the ordinary case on Steam and on the DRM-free build. A Game Pass build whose surface
        /// moved logs one warning and is then treated the same way - fail closed, never fault.</summary>
        private static bool ResolveXboxIdentity()
        {
            var asm = GameAssembly();
            if (asm == null)
            {
                return false;
            }

            var managerType = asm.GetType("GA.PlatformManager", false);
            var implType = asm.GetType("GA.GamecorePlatformManager", false);
            var userManagerType = asm.GetType("GA.GamecoreUserManager", false);
            var userDataType = asm.GetType("GA.UserData", false);
            if (managerType == null || implType == null
                || userManagerType == null || userDataType == null)
            {
                return false;
            }

            _xgManagerInstance = managerType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            _xgImplField = managerType.GetField("m_platformImpl",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _xgUserManagerField = implType.GetField("m_userManager",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _xgCurrentUserData = userManagerType.GetProperty("CurrentUserData",
                BindingFlags.Public | BindingFlags.Instance);
            _xgGamertagField = userDataType.GetField("UserGamertag",
                BindingFlags.Public | BindingFlags.Instance);

            var complete = _xgManagerInstance != null && _xgImplField != null
                && _xgUserManagerField != null && _xgCurrentUserData != null
                && _xgGamertagField != null;
            if (!complete)
            {
                // The Game Pass types are all present, yet one link of the chain is gone: a game
                // update reshaped the surface. Say so once so the missing name is explainable.
                CoopLog.WarnOnce("platform.xbox-identity-shape",
                    "xbox identity: the Game Pass user-manager surface changed; the Xbox gamertag "
                    + "will not be used as the default name.");
            }

            return complete;
        }

        /// <summary>One-line save-backend verdict for the startup log. Deliberately
        /// separate from the Steamworks line: they answer different questions, and 1.0.38
        /// shipped a field report where the Steam line was true and the save backend was
        /// not.</summary>
        public static string SaveBackendDescription
        {
            get
            {
                int i, c;
                var counter = TrySampleSaveCounter(out i, out c);
                return (GamePassBuild ? "native platform store (Game Pass/console)" : "local files")
                     + (counter
                        ? " [save-completion counter available]"
                        : " [save-completion counter UNAVAILABLE - unknown build, using the game's UseNativeSaves answer]");
            }
        }
    }
}




