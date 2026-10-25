using System;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Runtime;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Renders the local co-op character into a <see cref="RenderTexture"/> for the native
    /// character editor. The stage lives far away from the shop on an otherwise unused layer so
    /// the preview is independent of the player, the main camera, and an active co-op session.
    ///
    /// <para>The body is built from the game's own <c>Resources</c> character prefab when the build
    /// packages one. On builds that only stream the character prefab with the shop scene, the
    /// preview falls back to a customer clone (in game) or the game's small CC_Dummy prefab (title
    /// screen before a world has ever loaded). A limited body never writes back to the saved model;
    /// see <see cref="Limited"/>.</para>
    /// </summary>
    internal sealed class CoopCharacterPreview
    {
        private const int PreviewLayer = 31;
        private const int TextureWidth = 512;
        private const int TextureHeight = 640;
        private const float BaseCameraDistance = 4.2f;
        private const float BaseCameraHeight = 1.3f;
        private const float BaseTargetHeight = 1.1f;

        private const string DummyPrefabName = "CC_Dummy";

        private GameObject _stage;
        private Camera _camera;
        private RenderTexture _texture;
        private GameObject _body;
        private CC.CharacterCustomization _customization;
        private bool _bodyFemale;
        private bool _bodyFromCustomer;
        private CC.CharacterCustomization _customerSourceCustom;
        private Transform _customerSourceRoot;
        private bool _limited;
        private bool _visible;
        private int _modelIndex;
        private PresenceModelEntry _model;
        private Vector2 _pan;
        private float _zoom = 1f;

        internal RenderTexture Texture => _texture;

        internal CC.CharacterCustomization Customization => _customization;

        internal bool Available => _customization != null;

        /// <summary>True when the current body does not have the game's real gender presets (the
        /// title-screen CC_Dummy fallback). A limited body can draw a preview, but it must never be
        /// captured back into the saved model, or opening the editor before a world has loaded
        /// would truncate the player's full wardrobe.</summary>
        internal bool Limited => _limited || _customization == null;

        internal PresenceModelEntry Model => _model;

        internal void Show(bool visible)
        {
            EnsureStage();
            if (_stage == null)
            {
                return;
            }

            _visible = visible;
            if (_stage.activeSelf != visible)
            {
                _stage.SetActive(visible);
            }
            if (_camera != null && _camera.enabled != visible)
            {
                _camera.enabled = visible;
            }
        }

        /// <summary>Moves the preview camera by a pointer delta in screen pixels.</summary>
        internal void Pan(Vector2 delta)
        {
            if (_camera == null)
            {
                return;
            }

            var sensitivity = 0.003f / Mathf.Max(0.55f, _zoom);
            _pan += new Vector2(-delta.x, delta.y) * sensitivity;
            _pan.x = Mathf.Clamp(_pan.x, -1.2f, 1.2f);
            _pan.y = Mathf.Clamp(_pan.y, -0.8f, 0.8f);
            ApplyView();
        }

        /// <summary>Applies an analog-stick pan using the current frame's unscaled time.</summary>
        internal void PanStick(Vector2 stick, float unscaledDeltaTime)
        {
            Pan(stick * (500f * unscaledDeltaTime));
        }

        /// <summary>Positive values zoom in; negative values zoom out.</summary>
        internal void Zoom(float delta)
        {
            if (_camera == null || Mathf.Approximately(delta, 0f))
            {
                return;
            }

            _zoom = Mathf.Clamp(_zoom * (1f + delta), 0.55f, 2.5f);
            ApplyView();
        }

        internal void Recenter()
        {
            _pan = Vector2.zero;
            _zoom = 1f;
            ApplyView();
        }

        private void ApplyView()
        {
            if (_camera == null)
            {
                return;
            }

            var distance = BaseCameraDistance / _zoom;
            var target = new Vector3(_pan.x, BaseTargetHeight + _pan.y, 0f);
            var cameraPosition = new Vector3(_pan.x, BaseCameraHeight + _pan.y * 0.7f, distance);
            _camera.transform.localPosition = cameraPosition;
            _camera.transform.localRotation = Quaternion.LookRotation(
                target - cameraPosition, Vector3.up);
        }

        /// <summary>Points the preview at a model. The body is rebuilt only when the gender (or
        /// the available prefab quality) changes; normal appearance edits re-dress in place.</summary>
        internal void SetModel(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            if (_body == null || _bodyFemale != model.Female
                || _limited && HasBetterSource(model.Female))
            {
                CreateBody(model.Female, model.ModelIndex, model);
            }

            _model = Clone(model);
            _modelIndex = CoopCharacterEditorModel.ClampModelIndex(_customization,
                model.Female, model.ModelIndex);
            ApplyModel();
            RefreshLayers();
        }

        /// <summary>Rebuilds the body for a gender change and initializes it with the base preset.
        /// The caller applies a concrete model afterwards.</summary>
        internal void Rebuild(bool female, int modelIndex)
        {
            var model = new PresenceModelEntry
            {
                Female = female,
                ModelIndex = Mathf.Max(0, modelIndex),
                CustomizationJson = null,
            };
            CreateBody(female, modelIndex, model);
            _model = Clone(model);
            _modelIndex = CoopCharacterEditorModel.ClampModelIndex(_customization, female,
                modelIndex);
            ApplyModel();
            RefreshLayers();
        }

        internal PresenceModelEntry CaptureModel()
        {
            if (_customization == null || _customization.StoredCharacterData == null)
            {
                return Clone(_model);
            }

            return new PresenceModelEntry
            {
                Id = _model?.Id ?? 0,
                Female = _bodyFemale,
                ModelIndex = _modelIndex,
                CustomizationJson = JsonConvert.SerializeObject(_customization.StoredCharacterData),
            };
        }

        internal void ApplyLiveBlendshape(string name, float value)
        {
            if (_customization != null && !string.IsNullOrEmpty(name))
            {
                _customization.setBlendshapeByName(name, Mathf.Clamp01(value), save: false);
            }
        }

        internal void ApplyLiveFloat(string name, float value)
        {
            if (_customization != null && !string.IsNullOrEmpty(name))
            {
                _customization.setFloatProperty(new CC.CC_Property
                {
                    propertyName = name,
                    floatValue = value,
                }, save: false);
            }
        }

        internal void ApplyLiveColor(string name, Color color)
        {
            if (_customization != null && !string.IsNullOrEmpty(name))
            {
                _customization.setColorProperty(new CC.CC_Property
                {
                    propertyName = name,
                }, color, save: false);
            }
        }

        internal void ApplyLiveTexture(string name, string value)
        {
            if (_customization != null && !string.IsNullOrEmpty(name))
            {
                _customization.setTextureProperty(new CC.CC_Property
                {
                    propertyName = name,
                    stringValue = value ?? "",
                }, save: false);
            }
        }

        internal void ApplyLiveHairColor(int slot, Color color)
        {
            if (_customization != null)
            {
                _customization.setHairColor(new CC.CC_Property
                {
                    propertyName = "_Hair_Color",
                }, color, slot, save: false);
            }
        }

        internal void ApplyLiveApparelTint(int slot, Color color)
        {
            if (_customization == null)
            {
                return;
            }

            PresenceLocalEditorApi.SetApparelTint(_customization, slot, color);
        }

        private void ConfigureAnimator()
        {
            if (_body == null)
            {
                return;
            }

            var animator = _body.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                return;
            }

            animator.applyRootMotion = false;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        /// <summary>The game parents newly generated hair/apparel on the "Customer" layer. The
        /// camera only draws the preview layer, so every generated object has to be moved back
        /// after a wardrobe change or it would silently disappear.</summary>
        internal void RefreshLayers()
        {
            if (_body == null)
            {
                return;
            }

            var transforms = _body.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    transforms[i].gameObject.layer = PreviewLayer;
                }
            }
        }

        internal void Destroy()
        {
            if (_camera != null)
            {
                _camera.enabled = false;
                _camera.targetTexture = null;
            }
            if (_stage != null)
            {
                _stage.SetActive(false);
                UnityEngine.Object.Destroy(_stage);
            }
            _stage = null;
            _camera = null;
            _body = null;
            _customization = null;
            _model = null;
            _visible = false;

            if (_texture != null)
            {
                _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }
            _texture = null;
        }

        private void EnsureStage()
        {
            if (_stage != null)
            {
                return;
            }

            _texture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "CoopCharacterEditorPreview",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 4,
            };
            _texture.Create();

            _stage = new GameObject("CoopCharacterEditorStage");
            _stage.layer = PreviewLayer;
            UnityEngine.Object.DontDestroyOnLoad(_stage);
            _stage.transform.position = new Vector3(9000f, -9000f, 9000f);
            _stage.SetActive(_visible);

            var cameraObject = new GameObject("PreviewCamera", typeof(Camera));
            cameraObject.layer = PreviewLayer;
            cameraObject.transform.SetParent(_stage.transform, false);
            _camera = cameraObject.GetComponent<Camera>();
            ApplyView();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.05f, 0.1f, 1f);
            _camera.cullingMask = 1 << PreviewLayer;
            _camera.fieldOfView = 38f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 10f;
            _camera.depth = -100f;
            _camera.allowHDR = false;
            _camera.allowMSAA = true;
            _camera.useOcclusionCulling = false;
            _camera.targetTexture = _texture;

            AddLight("KeyLight", new Vector3(20f, 155f, 0f), new Color(1f, 0.96f, 0.9f), 1.15f);
            AddLight("FillLight", new Vector3(8f, -35f, 0f), new Color(0.72f, 0.82f, 1f), 0.55f);
            AddLight("RimLight", new Vector3(5f, 55f, 0f), new Color(1f, 0.86f, 0.95f), 0.7f);

            _camera.enabled = _visible;
        }

        private void AddLight(string name, Vector3 euler, Color color, float intensity)
        {
            var lightObject = new GameObject(name, typeof(Light));
            lightObject.layer = PreviewLayer;
            lightObject.transform.SetParent(_stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(euler);
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << PreviewLayer;
        }

        private void CreateBody(bool female, int modelIndex, PresenceModelEntry model)
        {
            DestroyBody();
            CoopCharacterPrefabPreloader.EnsureStarted();

            if (CoopCharacterPrefabPreloader.TryGet(female, out var preloaded)
                && CreateFromPrefab(preloaded, "preloaded", isDummy: false, female, modelIndex))
            {
                return;
            }

            if (CreateFromCustomer(female, modelIndex))
            {
                return;
            }

            var dummy = Resources.Load<GameObject>(DummyPrefabName);
            if (dummy != null
                && CreateFromPrefab(dummy, DummyPrefabName, isDummy: true, female, modelIndex))
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: using the limited CC_Dummy "
                    + "preview body; the full character prefab is not available on this screen.");
                return;
            }

            CoopPlugin.Log?.LogError("co-op character editor: no usable character prefab was found "
                + "for the " + (female ? "female" : "male") + " preview.");
        }

        private bool CreateFromPrefab(GameObject prefab, string label, bool isDummy, bool female,
            int modelIndex)
        {
            if (prefab == null)
            {
                return false;
            }

            GameObject holder = null;
            GameObject clone = null;
            try
            {
                holder = new GameObject("CoopCharacterEditorHolder_tmp");
                holder.SetActive(false);
                clone = UnityEngine.Object.Instantiate(prefab, holder.transform);
                clone.AddComponent<CoopOwnedVisual>();

                var customization = clone.GetComponentInChildren<CC.CharacterCustomization>(true);
                if (customization == null || customization.Presets == null
                    || customization.Presets.Presets == null
                    || customization.Presets.Presets.Count == 0)
                {
                    CoopPlugin.Log?.LogWarning("co-op character editor: prefab '" + label
                        + "' has no usable CharacterCustomization.");
                    return false;
                }

                customization.Autoload = false;
                customization.CharacterName = CharacterName(female,
                    CoopCharacterEditorModel.ClampModelIndex(customization, female, modelIndex));

                clone.transform.SetParent(_stage.transform, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                clone.name = "CoopCharacterEditorBody";
                clone.SetActive(true);
                UnityEngine.Object.Destroy(holder);
                holder = null;

                _body = clone;
                _customization = customization;
                _bodyFemale = female;
                _bodyFromCustomer = false;
                _customerSourceCustom = null;
                _customerSourceRoot = null;
                _limited = isDummy || !HasGenderPresets(customization, female);
                ConfigureAnimator();
                RefreshLayers();
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: preview body from '" + label
                    + "' failed: " + error);
                if (clone != null)
                {
                    clone.SetActive(false);
                    UnityEngine.Object.Destroy(clone);
                }
                _body = null;
                _customization = null;
                return false;
            }
            finally
            {
                if (holder != null)
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }
        }

        private bool CreateFromCustomer(bool female, int modelIndex)
        {
            var customers = SceneRef<CustomerManager>.Get();
            if (customers == null)
            {
                return false;
            }

            var source = CharacterClone.FindCustomerSource(customers, female);
            if (source == null)
            {
                return false;
            }

            GameObject holder = null;
            GameObject clone = null;
            try
            {
                var sourceCustom = source.m_CharacterCustom;
                holder = new GameObject("CoopCharacterEditorHolder_tmp");
                holder.SetActive(false);
                clone = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
                clone.AddComponent<CoopOwnedVisual>();
                clone.transform.SetParent(_stage.transform, false);
                clone.SetActive(true);
                UnityEngine.Object.Destroy(holder);
                holder = null;

                var customer = clone.GetComponent<Customer>();
                HideCustomerProps(customer);
                var customization = customer != null
                    ? customer.m_CharacterCustom
                    : clone.GetComponentInChildren<CC.CharacterCustomization>(true);
                if (customization == null || customization.Presets == null
                    || customization.Presets.Presets == null
                    || customization.Presets.Presets.Count == 0)
                {
                    clone.SetActive(false);
                    UnityEngine.Object.Destroy(clone);
                    return false;
                }

                CharacterClone.PrepareForDressing(customization, sourceCustom, source.transform,
                    clone.transform, CharacterName(female,
                        CoopCharacterEditorModel.ClampModelIndex(customization, female,
                            modelIndex)), "co-op character editor preview");

                foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null)
                    {
                        continue;
                    }

                    var typeName = behaviour.GetType().Name;
                    if (typeName == "CopyPose" || typeName == "BlendshapeManager"
                        || typeName == "ScaleCharacter" || typeName == "CharacterCustomization"
                        || typeName == "TransformBone" || typeName == "MipBiasAdjust")
                    {
                        continue;
                    }

                    UnityEngine.Object.DestroyImmediate(behaviour);
                }
                foreach (var collider in clone.GetComponentsInChildren<Collider>(true))
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }
                foreach (var body in clone.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(body);
                }

                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                clone.name = "CoopCharacterEditorBody";
                clone.SetActive(true);

                _body = clone;
                _customization = customization;
                _bodyFemale = female;
                _bodyFromCustomer = true;
                _customerSourceCustom = sourceCustom;
                _customerSourceRoot = source.transform;
                _limited = !HasGenderPresets(customization, female);
                ConfigureAnimator();
                RefreshLayers();
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: customer preview body failed: "
                    + error);
                if (clone != null)
                {
                    clone.SetActive(false);
                    UnityEngine.Object.Destroy(clone);
                }
                _body = null;
                _customization = null;
                return false;
            }
            finally
            {
                if (holder != null)
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }
        }

        private void ApplyModel()
        {
            if (_customization == null)
            {
                return;
            }

            var json = _model?.CustomizationJson;
            if (!CoopPlugin.AllowNsfw.Value && PresenceLocalEditorApi.IsNude(json))
            {
                // NSFW off: keep the game's initialized, clothed preset instead of stripping the
                // body. This is the same guard the old world-space preview used.
                json = null;
            }

            try
            {
                if (_bodyFromCustomer)
                {
                    // Reset the clone to its base preset first. Unlike a Resources prefab, a
                    // customer clone does not reinitialize itself when the JSON is empty, so a
                    // preview that previously showed a different (or nude) look must be dressed
                    // back to the preset before the new data is applied.
                    CharacterClone.PrepareForDressing(_customization, _customerSourceCustom,
                        _customerSourceRoot, _body != null ? _body.transform : null,
                        CharacterName(_bodyFemale, _modelIndex), "co-op character editor preview");

                    if (!string.IsNullOrEmpty(json))
                    {
                        var data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(json);
                        if (data != null)
                        {
                            _customization.StoredCharacterData = data;
                            PresenceLocalEditorApi.ApplyCharacterData(_customization, data);
                            PresenceLocalEditorApi.ClearEmptyWardrobeSlots(_customization, data);
                        }
                    }
                }
                else
                {
                    PresenceLocalEditorApi.ApplyModel(_customization, new PresenceModelEntry
                    {
                        Female = _bodyFemale,
                        ModelIndex = _modelIndex,
                        CustomizationJson = json,
                    });
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: applying the preview model failed: "
                    + error);
            }
        }

        private static bool HasGenderPresets(CC.CharacterCustomization customization, bool female)
        {
            var presets = customization?.Presets?.Presets;
            if (presets == null)
            {
                return false;
            }

            var prefix = female ? "Female" : "Male";
            for (var i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                    && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CharacterName(bool female, int modelIndex)
        {
            return (female ? "Female" : "Male") + Mathf.Max(0, modelIndex);
        }

        private static bool HasBetterSource(bool female)
        {
            return CoopCharacterPrefabPreloader.TryGet(female, out _)
                || SceneRef<CustomerManager>.Get() != null;
        }

        private void DestroyBody()
        {
            if (_body != null)
            {
                _body.SetActive(false);
                UnityEngine.Object.Destroy(_body);
            }
            _body = null;
            _customization = null;
            _bodyFromCustomer = false;
            _customerSourceCustom = null;
            _customerSourceRoot = null;
            _limited = false;
        }

        private static void HideCustomerProps(Customer customer)
        {
            if (customer == null)
            {
                return;
            }

            try
            {
                HideProp(customer.m_ShoppingBagTransform?.gameObject, customer);
                HideProp(customer.m_CustomerCash?.gameObject, customer);
                HideProp(customer.m_GameCardFanOut, customer);
                HideProp(customer.m_GameCardSingle, customer);
                HideProp(customer.m_CleanFX, customer);
                HideProp(customer.m_ExclaimationMesh, customer);
                HideProp(customer.m_InteractCollider, customer);
                HideProp(customer.m_SmellyFX, customer);
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: hiding customer props failed: "
                    + error.Message);
            }
        }

        private static void HideProp(GameObject prop, Customer customer)
        {
            if (prop == null || prop.transform == customer.transform)
            {
                return;
            }
            prop.SetActive(false);
        }

        private static PresenceModelEntry Clone(PresenceModelEntry model)
        {
            return model == null
                ? null
                : new PresenceModelEntry
                {
                    Id = model.Id,
                    Female = model.Female,
                    ModelIndex = model.ModelIndex,
                    CustomizationJson = model.CustomizationJson,
                };
        }
    }
}
