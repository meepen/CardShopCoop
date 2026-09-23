using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Runtime
{
    /// <summary>Central lifecycle and invalidation for every lazily resolved scene reference.</summary>
    internal static class SceneRef
    {
        private static readonly List<Action> Clearers = new();
        private static readonly List<Action<Scene>> SceneClearers = new();
        private static readonly List<Action<Scene>> SceneLoaders = new();
        private static bool _installed;

        internal static void Register(Action clear)
        {
            Clearers.Add(clear);
        }

        internal static void Register(Action clear, Action<Scene> clearScene,
            Action<Scene> sceneLoaded)
        {
            Register(clear);
            SceneClearers.Add(clearScene);
            SceneLoaders.Add(sceneLoaded);
        }

        /// <summary>
        /// Installs the small set of lifecycle hooks used by the modules. This runs before the
        /// first world scene is loaded, so normal manager/UI construction publishes its instance
        /// without making a later caller search the whole scene.
        /// </summary>
        internal static void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            Ensure<CGameManager>();
            Ensure<CEventManager>();
            Ensure<GameUIScreen>();
            Ensure<InteractionPlayerController>();
            Ensure<CustomerManager>();
            Ensure<RestockManager>();
            Ensure<InventoryBase>();
            Ensure<ShelfManager>();
            Ensure<UnlockRoomManager>();
            Ensure<LightManager>();
            Ensure<TutorialManager>();
            Ensure<PlayCardGameManager>();
            Ensure<Card3dUISpawner>();
            Ensure<PricePopupSpawner>();
            Ensure<PauseScreen>();
            Ensure<GradeCardWebsiteUIScreen>();
            Ensure<WorkerManager>();

            RegisterComponentType<CC.CharacterCustomization>();
            RegisterComponentType<CollectionBinderFlipAnimCtrl>();
            RegisterComponentType<TMPro.TMP_Text>();
            RegisterComponentType<RestockItemPanelUI>();
            RegisterComponentType<ScannerRestockScreen>();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void Ensure<T>() where T : Component
        {
            RuntimeHelpers.RunClassConstructor(typeof(SceneRef<T>).TypeHandle);
        }

        private static void RegisterComponentType<T>() where T : Component
        {
            SceneLifecycleHooks.Register(typeof(T),
                instance => SceneComponentRegistry<T>.Register((T)instance),
                instance => SceneComponentRegistry<T>.Unregister((T)instance));
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
        {
            NotifySceneLoaded(scene);
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            ClearScene(scene);
        }

        internal static void NotifySceneLoaded(Scene scene)
        {
            for (var i = 0; i < SceneLoaders.Count; i++)
            {
                SceneLoaders[i](scene);
            }
        }

        internal static void ClearScene(Scene scene)
        {
            for (var i = 0; i < SceneClearers.Count; i++)
            {
                SceneClearers[i](scene);
            }
        }

        internal static void ClearAll()
        {
            for (var i = 0; i < Clearers.Count; i++)
            {
                Clearers[i]();
            }
        }
    }

    /// <summary>
    /// Non-fabricating lookup for scene-lifetime managers.
    ///
    /// NEVER use <c>CSingleton&lt;T&gt;.Instance</c> for these. The game's getter auto-creates an
    /// empty DontDestroyOnLoad <c>(singleton)T</c> object the instant it is touched while no real
    /// instance exists - boot, the title screen, a guest's world reload, a host's mid-session save
    /// load - and that empty copy then shadows the real manager for the rest of the run. Every
    /// serialized reference on the copy is null (e.g. <c>CGameManager.m_TextSO</c>, sprite and UI
    /// refs), which silently disables whole systems: the HUD tooltip list then falls back to its
    /// prefab placeholder ("Action Name" + the default key image) for the whole session.
    ///
    /// The lifecycle bridge publishes instances from Awake/Start/Init/open hooks. A single
    /// fallback lookup is retained for sessions that attach after the game scene already exists;
    /// its result, including null, is cached until the scene changes or a lifecycle hook publishes
    /// an instance. It therefore cannot become a per-frame retry loop.
    /// </summary>
    internal static class SceneRef<T> where T : Component
    {
        private static T _cached;
        private static int _lastLookupScene = int.MinValue;

        static SceneRef()
        {
            SceneRef.Register(Clear, ClearScene, SceneLoaded);
            SceneLifecycleHooks.Register(typeof(T),
                instance => Set((T)instance),
                instance => Unset((T)instance));
        }

        internal static T Get()
        {
            if (_cached != null)
            {
                return _cached;
            }

            var scene = SceneManager.GetActiveScene();
            if (_lastLookupScene == scene.handle)
            {
                return null;
            }

            _lastLookupScene = scene.handle;
            // Late session startup can happen after Awake/Init has already run. This is the only
            // compatibility fallback; a missing object is negatively cached for this scene.
            _cached = UnityEngine.Object.FindObjectOfType<T>();
            return _cached;
        }

        private static void Set(T instance)
        {
            if (instance == null)
            {
                return;
            }

            _cached = instance;
            _lastLookupScene = int.MinValue;
        }

        private static void Unset(T instance)
        {
            if (!ReferenceEquals(_cached, instance))
            {
                return;
            }

            _cached = null;
            // Key the negative cache to the scene the component actually belonged to. Using the
            // active scene here poisoned the cache during a scene change: Unity can report the
            // incoming scene as active while the outgoing scene's components are still being
            // destroyed, so the new scene inherited a "lookup already failed" mark and every
            // later Get() there returned null (observed as manager=<null> after loading a shop).
            var scene = instance != null && instance.gameObject != null
                ? instance.gameObject.scene
                : default;
            _lastLookupScene = scene.IsValid() ? scene.handle : int.MinValue;
        }

        private static void ClearScene(Scene scene)
        {
            if (_cached == null || !_cached.gameObject.scene.IsValid()
                || _cached.gameObject.scene.handle == scene.handle)
            {
                _cached = null;
            }

            if (_lastLookupScene == scene.handle)
            {
                _lastLookupScene = int.MinValue;
            }
        }

        private static void SceneLoaded(Scene scene)
        {
            if (_cached == null)
            {
                _lastLookupScene = int.MinValue;
            }
            else if (_cached.gameObject.scene.IsValid()
                && _cached.gameObject.scene.handle == scene.handle)
            {
                _lastLookupScene = int.MinValue;
            }
        }

        private static void Clear()
        {
            _cached = null;
            _lastLookupScene = int.MinValue;
        }
    }

    /// <summary>Lifecycle-maintained component set used by dynamic UI and presence lookups.</summary>
    internal static class SceneComponentRegistry<T> where T : Component
    {
        private static readonly Dictionary<int, HashSet<T>> ByScene = new();
        private static readonly HashSet<T> All = new();

        internal static void Register(T instance)
        {
            if (instance == null || !instance.gameObject.scene.IsValid())
            {
                return;
            }

            var sceneHandle = instance.gameObject.scene.handle;
            if (!ByScene.TryGetValue(sceneHandle, out var values))
            {
                values = new HashSet<T>();
                ByScene.Add(sceneHandle, values);
            }

            values.Add(instance);
            All.Add(instance);
        }

        internal static void Unregister(T instance)
        {
            if (ReferenceEquals(instance, null))
            {
                return;
            }

            All.Remove(instance);
            foreach (var values in ByScene.Values)
            {
                values.Remove(instance);
            }
        }

        internal static List<T> Snapshot(Scene scene, bool activeOnly)
        {
            var result = new List<T>();
            if (!ByScene.TryGetValue(scene.handle, out var values))
            {
                return result;
            }

            var stale = new List<T>();
            foreach (var instance in values)
            {
                if (instance == null || !instance.gameObject.scene.IsValid()
                    || instance.gameObject.scene.handle != scene.handle)
                {
                    stale.Add(instance);
                    continue;
                }

                if (!activeOnly || instance.gameObject.activeInHierarchy)
                {
                    result.Add(instance);
                }
            }

            RemoveStale(values, stale);
            return result;
        }

        internal static List<T> SnapshotAll(bool activeOnly)
        {
            var result = new List<T>();
            var stale = new List<T>();
            foreach (var instance in All)
            {
                if (instance == null || !instance.gameObject.scene.IsValid())
                {
                    stale.Add(instance);
                    continue;
                }

                if (!activeOnly || instance.gameObject.activeInHierarchy)
                {
                    result.Add(instance);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                Unregister(stale[i]);
            }
            return result;
        }

        private static void RemoveStale(HashSet<T> values, List<T> stale)
        {
            for (var i = 0; i < stale.Count; i++)
            {
                values.Remove(stale[i]);
                All.Remove(stale[i]);
            }
        }
    }

    internal static class SceneLifecycleHooks
    {
        private static readonly Harmony Harmony = new("com.zwhit.cardshopcoop.scene-lifecycle");
        private static readonly Dictionary<Type, List<Action<Component>>> ReadyHandlers = new();
        private static readonly Dictionary<Type, List<Action<Component>>> GoneHandlers = new();
        private static readonly HashSet<Type> Patched = new();

        internal static void Register(Type type, Action<Component> ready,
            Action<Component> gone)
        {
            if (!ReadyHandlers.TryGetValue(type, out var readyHandlers))
            {
                readyHandlers = new List<Action<Component>>();
                ReadyHandlers.Add(type, readyHandlers);
            }
            readyHandlers.Add(ready);

            if (!GoneHandlers.TryGetValue(type, out var goneHandlers))
            {
                goneHandlers = new List<Action<Component>>();
                GoneHandlers.Add(type, goneHandlers);
            }
            goneHandlers.Add(gone);

            if (!Patched.Add(type))
            {
                return;
            }

            Patch(type, "Awake", nameof(ReadyPostfix));
            Patch(type, "Start", nameof(ReadyPostfix));
            Patch(type, "Init", nameof(ReadyPostfix));
            Patch(type, "OnEnable", nameof(ReadyPostfix));
            Patch(type, "OnOpenScreen", nameof(ReadyPostfix));
            Patch(type, "OnDestroy", nameof(GonePostfix));
        }

        private static void Patch(Type type, string methodName, string postfixName)
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null || method.IsStatic)
            {
                return;
            }

            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(SceneLifecycleHooks), postfixName));
        }

        private static void ReadyPostfix(Component __instance)
        {
            Notify(ReadyHandlers, __instance);
        }

        private static void GonePostfix(Component __instance)
        {
            Notify(GoneHandlers, __instance);
        }

        private static void Notify(Dictionary<Type, List<Action<Component>>> handlers,
            Component instance)
        {
            if (instance == null)
            {
                return;
            }

            foreach (var pair in handlers)
            {
                if (!pair.Key.IsInstanceOfType(instance))
                {
                    continue;
                }

                var callbacks = pair.Value;
                for (var i = 0; i < callbacks.Count; i++)
                {
                    callbacks[i](instance);
                }
            }
        }
    }
}


