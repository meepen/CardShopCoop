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
    /// the public build does. Inert on the public build, which has no <c>isSet</c> field.
    /// </summary>
    internal static class SingletonLatchGuard
    {
        private static readonly BindingFlags StaticAll =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly List<Latch> Latches = new();
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
            if (Latches.Count == 0 || Latches[0].IsSet == null)
            {
                CoopPlugin.Log.LogInfo(
                    "singleton latch guard: inactive (this build's CSingleton has no isSet latch)");
                return;
            }

            var harmony = new Harmony("dev.meepen.cardshopcoop.singleton-latch");
            var patchedMethods = 0;
            var failed = 0;
            for (var i = 0; i < Latches.Count; i++)
            {
                patchedMethods += PatchDeclared(harmony, Latches[i].ComponentType, "Awake", ref failed);
                patchedMethods += PatchDeclared(harmony, Latches[i].ComponentType, "OnEnable",
                    ref failed);
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
            CoopPlugin.Log.LogInfo("singleton latch guard: sweeping " + Latches.Count
                + " CSingleton<T> latch(es) from " + patchedMethods + " lifecycle hook(s)"
                + (failed == 0 ? "" : ", " + failed + " patch(es) failed")
                + "; a destroyed latch is cleared before the game reads it");
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

        private static void OnSceneUnloaded(Scene _) => Sweep("scene unloaded");
        private static void OnSceneLoaded(Scene _, LoadSceneMode __) => Sweep("scene loaded");
        private static void SweepPrefix() => Sweep("object init");

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
                        if (!(bool)latch.IsSet.GetValue(null))
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
                            Latches.Add(new Latch(b));
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
