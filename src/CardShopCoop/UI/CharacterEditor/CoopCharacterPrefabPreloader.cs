using System;
using System.Collections;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Loads the game's real character model for the co-op character tab and keeps only the
    /// model, never the scene that owns it.
    ///
    /// <para>The character prefab lives in the shop scene's serialized assets, not in Resources
    /// or an Addressables catalog. The only supported way to reach it is a temporary additive
    /// load of that scene. This class loads the scene, immediately deactivates every scene root
    /// before any <c>Start</c>/<c>Update</c> can run (which is what previously captured the
    /// cursor), clones the character prefab into a DontDestroyOnLoad template, copies the scene's
    /// <see cref="SharedMaterials"/> onto that clone, and then unloads the scene. The template is
    /// destroyed when the Character tab is left. No scene and no game object is kept for the
    /// process lifetime.</para>
    /// </summary>
    internal sealed class CoopCharacterPrefabPreloader : MonoBehaviour
    {
        private const float ActivationTimeoutSeconds = 60f;
        private const string DontDestroySceneName = "DontDestroyOnLoad";

        private static readonly string[] FemalePrefabNames =
        {
            "Female_Combined_HP Variant",
            "Female_Combined_HP",
            "Female_Combined_HPOptimized",
        };

        private static readonly string[] MalePrefabNames =
        {
            "Male_Combined_HP Variant",
            "Male_Combined_HP VariantA",
            "Male_Combined_HP",
            "Male_Combined_HPOptimized",
        };

        private static GameObject _femaleTemplate;
        private static GameObject _maleTemplate;
        private static GameObject _templateHolder;
        private static bool _preloadInFlight;
        private static bool _preloadAttemptedThisScene;
        private static int _preloadAttemptedScene = int.MinValue;
        private static bool _loggedMissingScene;
        private static bool _notified;
        private static bool _releaseRequested;
        private static bool _activationOwned;
        private static bool _sceneLoadedSubscribed;
        private static AsyncOperation _operation;
        private static string _preloadSceneName;
        private static GameObject _preloadHost;

        /// <summary>Raised on the main thread when the model templates are ready.</summary>
        internal static event Action Completed;

        internal static bool HasAny => _femaleTemplate != null || _maleTemplate != null;

        /// <summary>
        /// Starts the on-demand model load. Called only while the Character tab is enabled (or a
        /// preview needs a body). A missing prefab is logged and leaves the editor in its loading
        /// state; nothing is substituted.
        /// </summary>
        internal static void EnsureStarted()
        {
            CoopPlugin.Log?.LogInfo("co-op character editor: EnsureStarted hasAny=" + HasAny
                + " inFlight=" + _preloadInFlight);
            if (HasAny)
            {
                NotifyIfReady();
                return;
            }

            if (_preloadInFlight)
            {
                return;
            }

            CoopPlugin.Log?.LogInfo("co-op character editor: model requested (scene="
                + SceneManager.GetActiveScene().name + ", atTitle=" + CoopMenus.AtTitle
                + ", role=" + CoopCore.Role + ", loading=" + GameLoadInProgress() + ").");

            // In a live shop the prefab already exists in the loaded scene. Clone it directly
            // and skip the additive load entirely.
            if (TryCreateTemplatesFromLoadedScene())
            {
                NotifyIfReady();
                return;
            }

            if (!IsAtTitleForPreload())
            {
                // Never add a second copy of the shop over a live world. In gameplay the source
                // scan above is the only safe path; if it missed, the tab stays in loading state.
                return;
            }

            var sceneHandle = SceneManager.GetActiveScene().handle;
            if (_preloadAttemptedThisScene && _preloadAttemptedScene == sceneHandle)
            {
                return;
            }
            _preloadAttemptedThisScene = true;
            _preloadAttemptedScene = sceneHandle;

            var sceneName = ResolveShopSceneName();
            if (sceneName == null)
            {
                if (!_loggedMissingScene)
                {
                    _loggedMissingScene = true;
                    CoopPlugin.Log?.LogWarning("co-op character editor: neither StartOptimized nor "
                        + "Start is streamable; the character prefab cannot be resolved.");
                }
                return;
            }

            _preloadInFlight = true;
            _preloadHost = new GameObject("CoopCharacterPrefabPreloader");
            UnityEngine.Object.DontDestroyOnLoad(_preloadHost);
            _preloadHost.AddComponent<CoopCharacterPrefabPreloader>().StartCoroutine(
                PreloadSceneAndBuildTemplates(sceneName));
        }

        private static string ResolveShopSceneName()
        {
            if (Application.CanStreamedLevelBeLoaded(CGameManager.k_StartSceneName))
            {
                return CGameManager.k_StartSceneName;
            }
            return Application.CanStreamedLevelBeLoaded("Start") ? "Start" : null;
        }

        private static IEnumerator PreloadSceneAndBuildTemplates(string sceneName)
        {
            CoopPlugin.Log?.LogInfo("co-op character editor: temporarily loading " + sceneName
                + " to extract the character model.");

            AsyncOperation operation;
            try
            {
                operation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: could not load " + sceneName
                    + " for the character model: " + error);
                FinishPreload();
                yield break;
            }

            if (operation == null)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: could not load " + sceneName
                    + " for the character model (no operation).");
                FinishPreload();
                yield break;
            }

            _operation = operation;
            _preloadSceneName = sceneName;
            operation.allowSceneActivation = false;

            var deadline = Time.realtimeSinceStartup + ActivationTimeoutSeconds;
            while (!operation.isDone && operation.progress < 0.9f
                && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (operation.isDone || operation.progress < 0.9f)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: " + sceneName
                    + " did not reach the activation boundary (progress=" + operation.progress
                    + "); the character model could not be extracted.");
                yield return FinishAndUnload(operation, sceneName);
                yield break;
            }

            // Activate with a sceneLoaded handler that deactivates every root and extracts the
            // model synchronously, before any Start/Update can run. The scene stays loaded but
            // inactive as the temporary asset lease; it is unloaded when the Character tab is
            // left. Keeping it loaded is what keeps the wardrobe prefabs/materials alive.
            SubscribeSceneLoaded();
            _activationOwned = true;
            operation.allowSceneActivation = true;
            while (!operation.isDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!operation.isDone)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: " + sceneName
                    + " did not finish activating within " + ActivationTimeoutSeconds + "s.");
                yield return FinishAndUnload(operation, sceneName);
                yield break;
            }

            // The roots are inactive, so this extraction can take as long as it needs without
            // any scene Start/Update running. Keep the scene loaded but inactive as the temporary
            // asset lease (hair/apparel prefabs and materials live in it) until the tab closes.
            if (!TryCreateTemplatesFromLoadedScene())
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: no usable character prefab "
                    + "was found in " + sceneName + "; the editor has no model to show.");
            }

            _operation = null;
            _activationOwned = false;
            UnsubscribeSceneLoaded();
            _preloadSceneName = sceneName;
            FinishPreload(keepScene: true);
        }

        private static IEnumerator FinishAndUnload(AsyncOperation operation, string sceneName)
        {
            if (!operation.isDone)
            {
                _activationOwned = true;
                try
                {
                    operation.allowSceneActivation = true;
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogWarning("co-op character editor: could not finish the "
                        + "preload activation: " + error.Message);
                }
            }

            // The sceneLoaded handler has already made the roots inactive. Give Unity one frame
            // to process the activation before unloading; by then no Start/Update ran.
            yield return null;
            _operation = null;
            _preloadSceneName = null;
            _activationOwned = false;
            UnsubscribeSceneLoaded();

            var scene = SceneManager.GetSceneByName(sceneName);
            if (scene.IsValid() && scene.isLoaded && scene != SceneManager.GetActiveScene())
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null)
                {
                    while (!unload.isDone)
                    {
                        yield return null;
                    }
                }
                CoopPlugin.Log?.LogInfo("co-op character editor: unloaded " + sceneName
                    + "; only the extracted character model remains.");
            }

            FinishPreload();
        }

        /// <summary>
        /// Runs synchronously when the additive scene becomes active. Deactivating the roots here
        /// is the cursor fix: the scene's player/input components never receive Start/Update.
        /// </summary>
        private static void OnPreloadSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive || !_activationOwned)
            {
                return;
            }
            if (_preloadSceneName != null
                && !string.Equals(scene.name, _preloadSceneName, StringComparison.Ordinal))
            {
                return;
            }

            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null)
                {
                    continue;
                }

                if (roots[i].activeSelf)
                {
                    roots[i].SetActive(false);
                }

                // The temporary scene must never own the cursor or latch a gameplay manager
                // before it is unloaded. This does not touch cursor state.
                DestroyInScene<InteractionPlayerController>(roots[i]);
                DestroyInScene<CMF.MouseCursorLock>(roots[i]);
            }

            CoopPlugin.Log?.LogInfo("co-op character editor: deactivated the temporary "
                + scene.name + " scene before any gameplay component could start.");
        }

        /// <summary>
        /// The temporary scene is only a container for serialized prefab assets. Destroying its
        /// MonoBehaviours before activation means no CGameManager/InputManager/PhoneManager/
        /// InteractionPlayerController/Seeker can run, capture the cursor, or latch a singleton
        /// to an object that is about to be unloaded.
        /// </summary>
        private static void DestroyInScene<T>(GameObject root) where T : Component
        {
            var components = root.GetComponentsInChildren<T>(true);
            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(components[i]);
                }
            }
        }

        private static void SubscribeSceneLoaded()
        {
            if (_sceneLoadedSubscribed)
            {
                return;
            }
            _sceneLoadedSubscribed = true;
            SceneManager.sceneLoaded += OnPreloadSceneLoaded;
        }

        private static void UnsubscribeSceneLoaded()
        {
            if (!_sceneLoadedSubscribed)
            {
                return;
            }
            _sceneLoadedSubscribed = false;
            SceneManager.sceneLoaded -= OnPreloadSceneLoaded;
        }

        private static void FinishPreload(bool keepScene = false)
        {
            _preloadInFlight = false;
            _operation = null;
            _activationOwned = false;
            UnsubscribeSceneLoaded();
            if (!keepScene)
            {
                _preloadSceneName = null;
            }

            var host = _preloadHost;
            _preloadHost = null;
            if (host != null)
            {
                UnityEngine.Object.Destroy(host);
            }

            if (_releaseRequested)
            {
                _releaseRequested = false;
                ReleaseEditorLease();
                return;
            }

            NotifyIfReady();
        }

        /// <summary>
        /// Finds the real prefab instances in a loaded shop scene and clones only their visual
        /// hierarchy into temporary templates. The shop scene itself is never modified or kept.
        /// </summary>
        private static bool TryCreateTemplatesFromLoadedScene()
        {
            if (_femaleTemplate != null && _maleTemplate != null)
            {
                return true;
            }

            var sources = FindLoadedPrefabs();
            if (sources.female == null && sources.male == null)
            {
                return false;
            }

            var shared = FindSharedMaterials(sources.female, sources.male);
            if (shared == null)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: the shop scene has no "
                    + "usable SharedMaterials; the character model would render untextured.");
                return false;
            }

            if (_templateHolder == null)
            {
                _templateHolder = new GameObject("CoopCharacterTemplates");
                UnityEngine.Object.DontDestroyOnLoad(_templateHolder);
                _templateHolder.SetActive(false);
            }

            if (_femaleTemplate == null && sources.female != null)
            {
                _femaleTemplate = CreateTemplate(sources.female, shared, female: true);
            }
            if (_maleTemplate == null && sources.male != null)
            {
                _maleTemplate = CreateTemplate(sources.male, shared, female: false);
            }

            CoopPlugin.Log?.LogInfo("co-op character editor: extracted model templates female="
                + (_femaleTemplate != null) + " male=" + (_maleTemplate != null) + ".");
            return HasAny;
        }

        private static GameObject CreateTemplate(GameObject source, SharedMaterials shared,
            bool female)
        {
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source, _templateHolder.transform);
                clone.name = female ? "CoopFemaleCharacterTemplate" : "CoopMaleCharacterTemplate";
                clone.SetActive(false);

                StripToVisualHierarchy(clone);

                var customization = clone.GetComponentInChildren<CC.CharacterCustomization>(true);
                if (customization == null)
                {
                    CoopPlugin.Log?.LogWarning("co-op character editor: the "
                        + (female ? "female" : "male") + " prefab has no CharacterCustomization.");
                    UnityEngine.Object.Destroy(clone);
                    return null;
                }

                customization.Autoload = false;
                customization.UI = null;
                // A live customer may already be dressed; strip its generated wardrobe and clear
                // the runtime lists so every preview clone initializes from the tables.
                CharacterClone.ResetForTemplate(customization);

                var storage = customization.gameObject.GetComponent<SharedMaterials>();
                if (storage == null)
                {
                    storage = customization.gameObject.AddComponent<SharedMaterials>();
                }
                CopyMaterialArrays(shared, storage);
                customization.MaterialsStorage = storage;

                var renderers = customization.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var first = renderers.Length > 0 ? renderers[0] : null;
                var material = first != null ? first.sharedMaterial : null;
                CoopPlugin.Log?.LogInfo("co-op character editor: extracted "
                    + (female ? "female" : "male") + " template renderers=" + renderers.Length
                    + " meshes=" + (customization.CharacterMeshes != null
                        ? customization.CharacterMeshes.Count : 0)
                    + " presets=" + (customization.Presets != null
                        && customization.Presets.Presets != null
                        ? customization.Presets.Presets.Count : 0)
                    + " hairTables=" + (customization.HairTables != null
                        ? customization.HairTables.Count : 0)
                    + " apparelTables=" + (customization.ApparelTables != null
                        ? customization.ApparelTables.Count : 0)
                    + " mesh=" + (first != null && first.sharedMesh != null
                        ? first.sharedMesh.name : "<null>")
                    + " material=" + (material != null ? material.name : "<null>")
                    + " skinTexture=" + (material != null
                        && material.HasProperty("_Skin_Texture")
                        && material.GetTexture("_Skin_Texture") != null
                        ? material.GetTexture("_Skin_Texture").name : "<none>")
                    + " sharedUpper=" + storage.MaleUpper.Length + "/"
                    + storage.FemaleUpper.Length);
                return clone;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: could not extract the "
                    + (female ? "female" : "male") + " character model: " + error);
                if (clone != null)
                {
                    UnityEngine.Object.Destroy(clone);
                }
                return null;
            }
        }

        /// <summary>
        /// Destroys every game/AI/input component on the cloned prefab, leaving only what the
        /// wardrobe pipeline and renderer need. This is what makes the clone a model instead of a
        /// live customer: no Customer, Seeker, colliders, cameras, audio or cursor components.
        /// </summary>
        private static void StripToVisualHierarchy(GameObject root)
        {
            var components = root.GetComponentsInChildren<Component>(true);
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null || component is Transform)
                {
                    continue;
                }
                if (IsVisualComponent(component))
                {
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static bool IsVisualComponent(Component component)
        {
            return component is Animator
                || component is Animation
                || component is SkinnedMeshRenderer
                || component is MeshRenderer
                || component is MeshFilter
                || component is LODGroup
                || component is CC.CharacterCustomization
                || component is SharedMaterials
                || component is CC.CopyPose
                || component is CC.ScaleCharacter
                || component is CoopOwnedVisual;
        }

        private static void CopyMaterialArrays(SharedMaterials source, SharedMaterials target)
        {
            target.MaleUpper = CloneArray(source.MaleUpper);
            target.MaleLower = CloneArray(source.MaleLower);
            target.FemaleUpper = CloneArray(source.FemaleUpper);
            target.FemaleLower = CloneArray(source.FemaleLower);
            target.Shoes = CloneArray(source.Shoes);
        }

        private static Material[] CloneArray(Material[] source)
        {
            return source == null ? Array.Empty<Material>() : (Material[])source.Clone();
        }

        private static (GameObject female, GameObject male) FindLoadedPrefabs()
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>();
            GameObject female = null;
            GameObject male = null;
            for (var i = 0; i < objects.Length; i++)
            {
                var candidate = objects[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.name))
                {
                    continue;
                }
                if (!IsLoadedSource(candidate))
                {
                    continue;
                }

                if (female == null && Matches(candidate.name, FemalePrefabNames)
                    && IsUsable(candidate))
                {
                    female = candidate;
                }
                if (male == null && Matches(candidate.name, MalePrefabNames)
                    && IsUsable(candidate))
                {
                    male = candidate;
                }
                if (female != null && male != null)
                {
                    break;
                }
            }
            return (female, male);
        }

        private static bool IsLoadedSource(GameObject candidate)
        {
            var scene = candidate.scene;
            if (!scene.IsValid())
            {
                // A prefab asset in the scene's shared assets is a valid source; it is kept
                // alive by the extracted template after the scene is unloaded.
                return true;
            }
            if (!scene.isLoaded && _preloadSceneName != null
                && string.Equals(scene.name, _preloadSceneName, StringComparison.Ordinal))
            {
                // At Unity's 0.9 activation boundary the scene is addressable but not marked
                // loaded yet; its serialized objects are exactly what we are extracting.
                return true;
            }
            return scene.isLoaded
                && !string.Equals(scene.name, DontDestroySceneName, StringComparison.Ordinal);
        }

        private static SharedMaterials FindSharedMaterials(params GameObject[] sources)
        {
            for (var i = 0; i < sources.Length; i++)
            {
                var customization = sources[i] != null
                    ? sources[i].GetComponentInChildren<CC.CharacterCustomization>(true)
                    : null;
                if (customization != null && customization.MaterialsStorage != null
                    && HasMaterials(customization.MaterialsStorage))
                {
                    return customization.MaterialsStorage;
                }
            }

            var all = Resources.FindObjectsOfTypeAll<SharedMaterials>();
            for (var i = 0; i < all.Length; i++)
            {
                var candidate = all[i];
                if (candidate != null && IsLoadedSource(candidate.gameObject)
                    && HasMaterials(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool HasMaterials(SharedMaterials shared)
        {
            return shared != null
                && (shared.MaleUpper != null && shared.MaleUpper.Length > 0
                    || shared.MaleLower != null && shared.MaleLower.Length > 0
                    || shared.FemaleUpper != null && shared.FemaleUpper.Length > 0
                    || shared.FemaleLower != null && shared.FemaleLower.Length > 0
                    || shared.Shoes != null && shared.Shoes.Length > 0);
        }

        private static bool Matches(string name, string[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (string.Equals(name, names[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUsable(GameObject prefab)
        {
            if (prefab == null)
            {
                return false;
            }

            var customization = prefab.GetComponentInChildren<CC.CharacterCustomization>(true);
            return customization != null && customization.Presets != null
                && customization.Presets.Presets != null
                && customization.Presets.Presets.Count > 0;
        }

        private static void NotifyIfReady()
        {
            if (!HasAny || _notified)
            {
                return;
            }

            _notified = true;
            try
            {
                Completed?.Invoke();
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor preload completion failed: "
                    + error);
            }
        }

        internal static bool TryGet(bool female, out GameObject prefab)
        {
            prefab = female ? _femaleTemplate : _maleTemplate;
            return prefab != null;
        }

        /// <summary>
        /// Ends the temporary model lease. Called when the Character tab is left, the co-op window
        /// closes, or the player commits to a load. The next Character-tab open loads it again.
        /// </summary>
        internal static void ReleaseEditorLease()
        {
            if (_operation != null && !_operation.isDone)
            {
                // Let the preload coroutine activate/deactivate/unload the scene, then destroy
                // the templates there. Do not leave a pending scene load behind.
                _releaseRequested = true;
                try
                {
                    _operation.allowSceneActivation = true;
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogWarning("co-op character editor: could not release the "
                        + "pending preload: " + error.Message);
                }
                return;
            }

            DestroyTemplates();
            _preloadAttemptedThisScene = false;
            _preloadAttemptedScene = int.MinValue;
            _loggedMissingScene = false;
            _notified = false;
            _releaseRequested = false;
        }

        private static void DestroyTemplates()
        {
            _femaleTemplate = null;
            _maleTemplate = null;
            if (_templateHolder != null)
            {
                UnityEngine.Object.Destroy(_templateHolder);
                _templateHolder = null;
            }
        }

        /// <summary>The player committed to a load. Drop the model lease before the real scene
        /// load starts so it cannot be blocked by the temporary additive scene.</summary>
        internal static void OnGameLoadCommitted()
        {
            _releaseRequested = true;
            if (_operation != null && !_operation.isDone)
            {
                try
                {
                    _operation.allowSceneActivation = true;
                }
                catch (Exception error)
                {
                    CoopPlugin.Log?.LogWarning("co-op character editor: could not release the "
                        + "preload before a load: " + error.Message);
                }
            }

            DestroyTemplates();
            _notified = false;
            _preloadAttemptedThisScene = false;
            _preloadAttemptedScene = int.MinValue;
            // The real load owns the screen now; release the temporary inactive scene lease.
            ReleaseEditorLease();
        }

        /// <summary>
        /// Harmony prefix on <c>CGameManager.OnLevelFinishedLoading</c>. The temporary additive
        /// activation is not the player entering a shop; letting the game handle it would set
        /// <c>m_IsGameLevel</c>, run <c>CreateDefaultData</c> and mark <c>m_InitLoaded</c>. Only
        /// our exact additive preload activation is skipped.
        /// </summary>
        internal static bool SuppressPreloadLevelLoad(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive || !_activationOwned)
            {
                return true;
            }

            if (_preloadSceneName != null
                && !string.Equals(scene.name, _preloadSceneName, StringComparison.Ordinal))
            {
                return true;
            }

            CoopPlugin.Log?.LogInfo("co-op character editor: suppressed CGameManager's level "
                + "handler for the temporary model scene " + scene.name);
            return false;
        }

        private static bool IsAtTitleForPreload()
            => CoopMenus.AtTitle
                && (CoopCore.Instance == null || CoopCore.Role == CoopRole.None);

        private static bool GameLoadInProgress()
        {
            var loading = LoadingScreen.m_Instance;
            return loading != null && loading.m_ScreenGrp != null
                && loading.m_ScreenGrp.activeInHierarchy;
        }
    }
}
