using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
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
    /// <c>Card3dUISpawner.Update</c> then threw every frame on <c>m_Cam.transform</c>. <see
    /// cref="ScreenSingletonGuard"/> repairs six screens, but only from four game entry points that
    /// run after scene Awake, so it cannot save the second load.
    ///
    /// The fix is the legacy semantics, applied to every concrete <c>CSingleton&lt;T&gt;</c> in the
    /// process: before the getter body runs, clear a latch whose cached instance is dead. The body
    /// then resolves the live scene object exactly as the public build does. The guard is inert on
    /// the public build, which has no <c>isSet</c> field.
    /// </summary>
    internal static class SingletonLatchGuard
    {
        private static readonly BindingFlags StaticPrivate =
            BindingFlags.Static | BindingFlags.NonPublic;

        private static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            var closedTypes = CollectClosedSingletonTypes();
            var isSetField = closedTypes.Count == 0
                ? null
                : closedTypes[0].GetField("isSet", StaticPrivate);
            if (isSetField == null)
            {
                CoopPlugin.Log.LogInfo(
                    "singleton latch guard: inactive (this build's CSingleton has no isSet latch)");
                return;
            }

            var harmony = new Harmony("dev.meepen.cardshopcoop.singleton-latch");
            var patched = 0;
            var failed = 0;
            for (var i = 0; i < closedTypes.Count; i++)
            {
                try
                {
                    if (PatchSingleton(harmony, closedTypes[i]))
                    {
                        patched++;
                    }
                }
                catch (Exception error)
                {
                    failed++;
                    CoopPlugin.Log.LogWarning("singleton latch guard: could not patch "
                        + closedTypes[i].Name + ": " + error.Message);
                }
            }

            CoopPlugin.Log.LogInfo("singleton latch guard: patched " + patched
                + " CSingleton<T> getter(s)"
                + (failed == 0 ? "" : ", " + failed + " failed")
                + " so a destroyed latch re-resolves instead of being returned");
        }

        private static bool PatchSingleton(Harmony harmony, Type singletonType)
        {
            var isSetField = singletonType.GetField("isSet", StaticPrivate);
            var instanceField = singletonType.GetField("instance", StaticPrivate);
            var getter = singletonType.GetProperty("Instance",
                BindingFlags.Static | BindingFlags.Public)?.GetGetMethod(true);
            if (isSetField == null || instanceField == null || getter == null)
            {
                return false;
            }

            var componentType = singletonType.GetGenericArguments()[0];
            var helperType = typeof(SingletonLatchRepair<>).MakeGenericType(componentType);
            helperType.GetMethod("Configure",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Invoke(null, new object[]
                {
                    CompileBoolGetter(isSetField),
                    CompileObjectGetter(instanceField),
                    CompileBoolSetter(isSetField)
                });

            var prefix = helperType.GetMethod("Prefix", StaticPrivate);
            harmony.Patch(getter, prefix: new HarmonyMethod(prefix));
            return true;
        }

        /// <summary>Every distinct <c>CSingleton&lt;T&gt;</c> closed type used by a loaded assembly.
        /// Walking the base chain (rather than matching <c>CSingleton&lt;Foo&gt;</c> exactly) also
        /// covers a type that inherits a game singleton subclass.</summary>
        private static List<Type> CollectClosedSingletonTypes()
        {
            var result = new List<Type>();
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
                    // Harmony/EPL-patched assemblies routinely contain a few types that cannot
                    // load on one build. The loadable remainder still needs the guard.
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
                            result.Add(b);
                        }
                        break;
                    }
                }
            }
            return result;
        }

        // Expressions, not AccessTools.StaticFieldRefAccess: Harmony's static ref accessor throws
        // inside its DynamicMethod for a field reached through CSingleton<T> (verified against the
        // shipped 1.00 Game Pass assembly). Expression.Compile reads the same closed static field
        // directly and is exercised by the build-time check.
        private static Func<bool> CompileBoolGetter(FieldInfo field)
            => Expression.Lambda<Func<bool>>(Expression.Field(null, field)).Compile();

        /// <summary>The instance field's static type is the concrete component; read it as
        /// <see cref="UnityEngine.Object"/> so the caller gets Unity's destroyed-object null check.</summary>
        private static Func<UnityEngine.Object> CompileObjectGetter(FieldInfo field)
            => Expression.Lambda<Func<UnityEngine.Object>>(
                Expression.Convert(Expression.Field(null, field), typeof(UnityEngine.Object)))
                .Compile();

        private static Action<bool> CompileBoolSetter(FieldInfo field)
        {
            var value = Expression.Parameter(typeof(bool), "value");
            return Expression.Lambda<Action<bool>>(
                Expression.Assign(Expression.Field(null, field), value), value).Compile();
        }
    }

    /// <summary>
    /// One closed <c>CSingleton&lt;T&gt;</c> latch: compiled accessors plus the Harmony prefix.
    /// The accessors live on this closed generic type so the hot getter path does no dictionary
    /// lookup or reflection - only two delegate calls after the cheap <c>isSet</c> test.
    /// </summary>
    internal static class SingletonLatchRepair<T> where T : Component
    {
        private static Func<bool> _isSet;
        private static Func<UnityEngine.Object> _readInstance;
        private static Action<bool> _writeIsSet;

        internal static void Configure(Func<bool> isSet,
            Func<UnityEngine.Object> readInstance, Action<bool> writeIsSet)
        {
            _isSet = isSet;
            _readInstance = readInstance;
            _writeIsSet = writeIsSet;
        }

        /// <summary>Harmony prefix on <c>CSingleton&lt;T&gt;.Instance</c>. Harmony injects nothing
        /// here: the original getter still runs afterwards and performs the actual re-resolve.</summary>
        private static void Prefix()
        {
            if (!_isSet())
            {
                return;
            }

            // Unity's == is the authority for "the native object is gone", which is exactly the
            // state a latch holds after its scene unloads.
            if (_readInstance() != null)
            {
                return;
            }

            _writeIsSet(false);
            CoopLog.WarnOnce("singleton:repair:" + typeof(T).Name,
                "[singleton] " + typeof(T).Name + " was latched to a destroyed object and never "
                + "released it; cleared isSet so Instance resolves the live scene object instead "
                + "of handing the game a dead one.");
        }
    }
}
