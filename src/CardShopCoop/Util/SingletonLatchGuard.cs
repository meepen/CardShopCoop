using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CardShopCoop.Api;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Restores the legacy <c>CSingleton&lt;T&gt;</c> contract on the 1.00 / Unity 6 builds.
    ///
    /// The public build's singleton getter is null-checked on every access:
    /// <c>if (instance == null) instance = FindObjectOfType&lt;T&gt;(); return instance;</c>.
    /// Because Unity's overloaded <c>==</c> treats a destroyed component as null, that re-resolves
    /// the moment the scene that owned it goes away.
    ///
    /// The 1.00 and Game Pass builds added an <c>isSet</c> latch ahead of that check:
    /// <c>if (!isSet) { ...; isSet = true; } return instance;</c>, and reset it only from
    /// <c>private void OnDestroy()</c> on the generic base. Unity does not dispatch magic methods
    /// declared on a generic MonoBehaviour, so <c>isSet</c> is never cleared. From the first scene
    /// change on, <c>Instance</c> keeps returning a destroyed object from the previous world.
    ///
    /// This is not theoretical. On the Game Pass log a guest joined once (fine - the Title screen had
    /// never touched those singletons, so isSet was still false), was kicked, and rejoined. The
    /// second world's <c>Awake</c>/<c>Start</c> then read the previous world's corpses:
    /// <c>TutorialManager.Awake -&gt; TutorialSubGroup.CloseScreen</c> threw on a destroyed gameObject,
    /// <c>AutoCleanserStatusUI.Start</c> threw on a destroyed m_ScreenGrp,
    /// <c>InteractionPlayerController.Start -&gt; CenterDot.SetVisibility</c> threw on a destroyed
    /// CenterDot, <c>PauseScreen.CloseScreen</c> threw on a destroyed m_CameraController, and
    /// <c>Card3dUISpawner.Update</c> then threw every frame on <c>m_Cam.transform</c>.
    ///
    /// The first attempt patched <c>CSingleton&lt;T&gt;.Instance</c> itself (one prefix per closed
    /// generic type). That hard-crashes Unity's Mono the first time a patched getter runs - the
    /// shipped Game Pass build died inside <c>CEventManager.Instance</c> during
    /// <c>CoopPlugin.Awake</c>, with the native stack under
    /// <c>GameObject.AddComponent&lt;CoopCore&gt;</c> and <c>mono_unity_class_has_failure</c>.
    /// Generic-method patching is not safe there, so the latch is now repaired from the outside:
    /// this guard never patches a generic method. It sweeps every latch at the game's own scene
    /// lifecycle boundaries - singleton <c>Awake</c>/<c>OnEnable</c>, the game's scene-loaded
    /// handlers, and Unity's scene events - and clears only a latch whose cached instance is already
    /// dead. The game's next <c>Instance</c> access then resolves the live scene object exactly as
    /// the public build does. The dead-latch half is necessarily inert on the public build, which
    /// has no <c>isSet</c> field; the shadow repair below runs on both.
    ///
    /// The same guard also repairs the hazard every build's getter has:
    /// <c>Instance</c> fabricates <c>(singleton)T</c> (a bare <c>AddComponent</c> object marked
    /// DontDestroyOnLoad) whenever it is touched while no scene instance is resolvable, and that
    /// empty copy shadows the real manager for the rest of the run - every serialized field on it is
    /// null. A mod path that reads a game manager during a scene-load window does exactly that; the
    /// shadow <c>RestockManager</c> has no prefabs or spawn locations, so delivery spawns throw and
    /// no restock box ever appears. <see cref="RepairShadowedCache"/> swaps the game's cache back to
    /// the scene manager as soon as that manager publishes itself through <c>SceneRef</c>, on every
    /// build.
    ///
    /// The repair is deliberately narrow. A fabricated object is recognised by the name the game's
    /// own getter gives it, but it is only replaced when the loaded scene owns a real instance of
    /// the same manager. Some fabricated objects are the game's intended singleton - there is no
    /// scene <c>CEventManager</c> in either build; the getter is how it is created - so a copy with
    /// no scene-owned counterpart is left alone rather than "repaired" into a swap that would
    /// discard live listeners.
    /// </summary>
    internal static class SingletonLatchGuard
    {
        private static readonly BindingFlags StaticAll =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly List<Latch> Latches = new();
        private static readonly Dictionary<Type, Latch> LatchBySingletonType = new();
        private static readonly HashSet<MethodBase> Patched = new();
        private static bool _installed;
        private static bool _sweeping;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            CollectLatches();
            if (Latches.Count == 0)
            {
                CoopPlugin.Log.LogInfo(
                    "singleton latch guard: inactive (no CSingleton<T> types were found)");
                return;
            }

            var latchBuild = false;
            for (var i = 0; i < Latches.Count; i++)
            {
                latchBuild |= Latches[i].IsSet != null;
            }

            var harmony = new Harmony("dev.meepen.cardshopcoop.singleton-latch");
            var patchedMethods = 0;
            var failed = 0;
            if (latchBuild)
            {
                for (var i = 0; i < Latches.Count; i++)
                {
                    patchedMethods += PatchDeclared(harmony, Latches[i].ComponentType, "Awake",
                        ref failed);
                    patchedMethods += PatchDeclared(harmony, Latches[i].ComponentType, "OnEnable",
                        ref failed);
                }
            }

            // The game's own scene-loaded handlers read latched singletons from their bodies. These
            // are the two entry points that run before the rest of the load, and both are plain
            // (non-generic) methods, so patching them is safe. The Game Pass-only platform hook is
            // resolved by name because it does not exist on the public build.
            patchedMethods += PatchDeclared(harmony, typeof(CGameManager), "OnLevelFinishedLoading",
                ref failed);
            var platform = typeof(CGameManager).Assembly.GetType("GA.PlatformManager", false);
            if (platform != null)
            {
                var onSceneLoaded = platform.GetMethod("OnSceneLoaded",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                if (onSceneLoaded != null)
                {
                    patchedMethods += PatchMethod(harmony, onSceneLoaded, ref failed);
                }
            }

            // Belt and braces: if a scene unload finishes before the new scene's Awake hooks run,
            // this clears the latches at the earliest possible moment. sceneLoaded covers Start and
            // Update for any type whose own Awake/OnEnable did not touch a singleton.
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            Sweep("install");
            RepairAll("install");
            CoopPlugin.Log.LogInfo("singleton guard: " + Latches.Count
                + " CSingleton<T> latch(es)"
                + (latchBuild
                    ? " with the isSet latch; repairing destroyed latches"
                    : " on a build without the isSet latch")
                + " and repairing empty (singleton) shadows from " + patchedMethods
                + " lifecycle hook(s)"
                + (failed == 0 ? "" : ", " + failed + " patch(es) failed")
                + ".");
        }

        private static int PatchDeclared(Harmony harmony, Type type, string methodName, ref int failed)
        {
            // DeclaredOnly: never patch a shared base implementation (UIScreenBase.Start and
            // friends) once per derived type, and never patch a generic method.
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (method == null || method.IsGenericMethodDefinition || method.ContainsGenericParameters)
            {
                return 0;
            }
            return PatchMethod(harmony, method, ref failed);
        }

        private static int PatchMethod(Harmony harmony, MethodBase method, ref int failed)
        {
            if (!Patched.Add(method))
            {
                return 0;
            }

            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(SingletonLatchGuard), nameof(SweepPrefix)));
                return 1;
            }
            catch (Exception error)
            {
                failed++;
                CoopPlugin.Log.LogWarning("singleton latch guard: could not patch "
                    + method.DeclaringType?.Name + "." + method.Name + ": " + error.Message);
                return 0;
            }
        }

        private static void OnSceneUnloaded(Scene _)
        {
            Sweep("scene unloaded");
            RepairAll("scene unloaded");
        }

        private static void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            Sweep("scene loaded");
            RepairAll("scene loaded");
        }

        private static void SweepPrefix() => Sweep("object init");

        /// <summary>
        /// A real scene manager of type <paramref name="live"/>'s own <c>CSingleton&lt;T&gt;</c>
        /// reported itself through <see cref="SceneRef"/>. If the game's static cache currently
        /// holds a fabricated <c>(singleton)T</c> copy instead, put the real manager back. This is
        /// the repair for the empty-copy shadowing: the fabricated copy has no serialized
        /// references (a <c>RestockManager</c> copy has no prefabs or spawn locations), so leaving
        /// it cached breaks the game system for the rest of the run.
        /// </summary>
        internal static void RepairShadowedCache(Component live)
        {
            if (live == null)
            {
                return;
            }

            var singletonType = SingletonTypeOf(live.GetType());
            if (singletonType == null || !LatchBySingletonType.TryGetValue(singletonType, out var latch))
            {
                return;
            }

            Repair(latch, live, "manager ready");
        }

        /// <summary>
        /// Swap every cache that holds a fabricated copy for the live scene instance of the same
        /// type. Only a scene-owned manager qualifies: a fabricated copy whose type the scene never
        /// provides (CEventManager, TouchManager) is the game's intended singleton, not a shadow.
        /// </summary>
        private static void RepairAll(string site)
        {
            if (_sweeping)
            {
                return;
            }

            _sweeping = true;
            try
            {
                for (var i = 0; i < Latches.Count; i++)
                {
                    var latch = Latches[i];
                    try
                    {
                        if (latch.Instance == null
                            || !IsFabricated(latch,
                                latch.Instance.GetValue(null) as UnityEngine.Object))
                        {
                            continue;
                        }

                        var scene = FindSceneInstance(latch.ComponentType);
                        if (scene != null)
                        {
                            Repair(latch, scene, site);
                        }
                    }
                    catch (Exception error)
                    {
                        CoopLog.Swallow(error);
                    }
                }
            }
            finally
            {
                _sweeping = false;
            }
        }

        private static void Repair(Latch latch, Component live, string site)
        {
            try
            {
                // Only ever replace a fabricated copy with a real (non-fabricated) manager, and
                // only one the loaded scene owns: an app-level getter-created singleton such as
                // CEventManager is live, not damage, and must never be swapped out.
                if (live == null || live.gameObject == null || !live.gameObject.scene.IsValid()
                    || live.gameObject.scene.name == "DontDestroyOnLoad"
                    || IsFabricatedSingleton(live))
                {
                    return;
                }

                var cached = latch.Instance.GetValue(null) as UnityEngine.Object;
                if (ReferenceEquals(cached, live) || !IsFabricated(latch, cached))
                {
                    return;
                }

                latch.Instance.SetValue(null, live);
                if (latch.IsSet != null)
                {
                    // The 1.00 getter returns early while isSet is true; it must point at the real
                    // manager now that the cache does.
                    latch.IsSet.SetValue(null, true);
                }

                CoopLog.WarnOnce("singleton:shadow:" + latch.Name,
                    "[singleton] the game had cached an empty (singleton)" + latch.Name
                    + " created by CSingleton.Instance while no scene manager existed; replaced it "
                    + "with the live scene manager during " + site + " and destroyed the empty copy. "
                    + "That copy has no prefabs, spawn locations or UI references, so any system "
                    + "reading it is broken until this repair.");

                if (cached is Component stale && stale.gameObject != null)
                {
                    UnityEngine.Object.Destroy(stale.gameObject);
                }
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
            }
        }

        /// <summary>True when <paramref name="value"/> is the object the game's own
        /// <c>CSingleton&lt;T&gt;.Instance</c> fallback creates, identified by the name that getter
        /// gives it (<c>(singleton)Full.Type.Name</c>). A fabricated object is not necessarily
        /// broken: <c>CEventManager</c> is created that way on purpose and is the live dispatcher.
        /// The repair therefore only replaces one when the loaded scene owns another instance of the
        /// same manager, which is the case the game's getter itself would have preferred.</summary>
        private static bool IsFabricated(Latch latch, UnityEngine.Object value)
            => value is Component component && IsFabricatedSingleton(component);

        /// <summary>
        /// True when this component is the game's own <c>(singleton)T</c> fallback object. Works
        /// even before the getter's <c>DontDestroyOnLoad</c> call moves it (an Awake/OnEnable hook
        /// fires between <c>AddComponent</c> and that move).
        /// </summary>
        internal static bool IsFabricatedSingleton(Component instance)
        {
            if (instance == null || instance.gameObject == null)
            {
                return false;
            }

            var singletonType = SingletonTypeOf(instance.GetType());
            return singletonType != null
                && singletonType.GetGenericArguments().Length == 1
                && string.Equals(instance.gameObject.name,
                    "(singleton)" + singletonType.GetGenericArguments()[0],
                    StringComparison.Ordinal);
        }

        /// <summary>Read the game's own cached instance for a <c>CSingleton&lt;T&gt;</c> component
        /// type without calling its getter (which would fabricate one). Used by diagnostics to name
        /// the manager the game actually resolved.</summary>
        internal static bool TryGetCachedInstance(Type componentType, out Component instance)
        {
            instance = null;
            var singletonType = SingletonTypeOf(componentType);
            if (singletonType == null || !LatchBySingletonType.TryGetValue(singletonType, out var latch))
            {
                return false;
            }

            instance = latch.Instance?.GetValue(null) as Component;
            return instance != null;
        }

        private static Component FindSceneInstance(Type componentType)
        {
            var found = UnityEngine.Object.FindObjectsOfType(componentType);
            for (var i = 0; i < found.Length; i++)
            {
                if (found[i] is Component candidate && candidate.gameObject != null
                    && !IsFabricatedSingleton(candidate))
                {
                    var scene = candidate.gameObject.scene;
                    if (scene.IsValid() && scene.name != "DontDestroyOnLoad")
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>The closed <c>CSingleton&lt;T&gt;</c> in <paramref name="type"/>'s base chain.</summary>
        private static Type SingletonTypeOf(Type type)
        {
            for (var baseType = type?.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.IsGenericType
                    && baseType.GetGenericTypeDefinition() == typeof(CSingleton<>))
                {
                    return baseType;
                }
            }

            return null;
        }

        /// <summary>Clear every latch whose cached instance is dead. A live instance is left alone,
        /// including a DontDestroyOnLoad singleton such as Card3dUISpawner.</summary>
        private static void Sweep(string site)
        {
            if (_sweeping)
            {
                return;
            }

            _sweeping = true;
            try
            {
                for (var i = 0; i < Latches.Count; i++)
                {
                    var latch = Latches[i];
                    try
                    {
                        if (latch.IsSet == null || !(bool)latch.IsSet.GetValue(null))
                        {
                            continue;
                        }

                        if (latch.Instance.GetValue(null) is UnityEngine.Object live && live != null)
                        {
                            continue;
                        }

                        latch.IsSet.SetValue(null, false);
                        CoopLog.WarnOnce("singleton:repair:" + latch.Name,
                            "[singleton] " + latch.Name + " was latched to a destroyed object and "
                            + "never released it; cleared isSet during " + site
                            + " so Instance resolves the live scene object instead of handing the "
                            + "game a dead one.");
                    }
                    catch (Exception error)
                    {
                        CoopLog.Swallow(error);
                    }
                }
            }
            finally
            {
                _sweeping = false;
            }
        }

        /// <summary>Every distinct <c>CSingleton&lt;T&gt;</c> closed type used by a loaded assembly.
        /// Walking the base chain (rather than matching <c>CSingleton&lt;Foo&gt;</c> exactly) also
        /// covers a type that inherits a game singleton subclass.</summary>
        private static void CollectLatches()
        {
            var seen = new HashSet<Type>();
            var singletonDefinition = typeof(CSingleton<>);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException error)
                {
                    // EPL-patched assemblies routinely contain a few types that cannot load on one
                    // build. The loadable remainder still needs the guard.
                    types = error.Types;
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogWarning("singleton latch guard: could not list types in "
                        + assemblies[a].GetName().Name + ": " + error.Message);
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (var t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type == null || !type.IsClass || type.IsAbstract
                        || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    for (var b = type.BaseType; b != null; b = b.BaseType)
                    {
                        if (!b.IsGenericType || b.GetGenericTypeDefinition() != singletonDefinition)
                        {
                            continue;
                        }

                        if (seen.Add(b))
                        {
                            var latch = new Latch(b);
                            Latches.Add(latch);
                            LatchBySingletonType[b] = latch;
                        }
                        break;
                    }
                }
            }
        }

        private sealed class Latch
        {
            internal readonly Type ComponentType;
            internal readonly string Name;
            internal readonly FieldInfo IsSet;
            internal readonly FieldInfo Instance;

            internal Latch(Type singletonType)
            {
                ComponentType = singletonType.GetGenericArguments()[0];
                Name = ComponentType.Name;
                IsSet = singletonType.GetField("isSet", StaticAll);
                Instance = singletonType.GetField("instance", StaticAll);
            }
        }
    }
}
