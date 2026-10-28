using System;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Runtime;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Renders the local co-op character into a <see cref="RenderTexture"/> for the native
    /// character editor. The stage lives far away from the shop on an otherwise unused layer so
    /// the preview is independent of the player, the main camera, and an active co-op session.
    ///
    /// <para>The body is built from the real character prefab the preloader keeps alive in a
    /// DontDestroyOnLoad template. There is no fallback body and no limited mode: until that
    /// template exists the editor shows a loading state, and a malformed template is logged and
    /// refused rather than substituted.</para>
    /// </summary>
    internal sealed class CoopCharacterPreview
    {
        private const int PreviewLayer = 31;
        private const int TextureWidth = 512;
        private const int TextureHeight = 640;
        private const float BaseCameraDistance = 4.2f;
        private const float BaseCameraHeight = 1.3f;
        private const float BaseTargetHeight = 1.1f;

        private GameObject _stage;
        private Camera _camera;
        private RenderTexture _texture;
        private GameObject _body;
        private CC.CharacterCustomization _customization;
        private bool _bodyFemale;
        private bool _visible;
        private int _modelIndex;
        private PresenceModelEntry _model;
        private Vector3 _pan;
        private float _yaw;
        private float _pitch;
        private float _zoom = 1f;
        private bool _renderSettingsCaptured;
        private AmbientMode _renderAmbientMode;
        private Color _renderAmbientLight;
        private Color _renderAmbientSky;
        private Color _renderAmbientEquator;
        private Color _renderAmbientGround;
        private float _renderAmbientIntensity;
        private SphericalHarmonicsL2 _renderAmbientProbe;
        private Light _renderSun;
        private bool _lightingLogged;
        private Light _keyLight;
        private SphericalHarmonicsL2 _previewAmbient;

        internal RenderTexture Texture => _texture;

        internal CC.CharacterCustomization Customization => _customization;

        internal bool Available => _customization != null;

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
            // Pan in the camera's current screen plane, not in world X/Y, so right-dragging
            // keeps following the view after the preview has been orbited or pitched.
            _pan += (-_camera.transform.right * delta.x
                - _camera.transform.up * delta.y) * sensitivity;
            _pan = Vector3.ClampMagnitude(_pan, 1.6f);
            ApplyView();
        }

        /// <summary>Orbits the preview around the character using a pointer delta.</summary>
        internal void Orbit(Vector2 delta)
        {
            if (_camera == null)
            {
                return;
            }

            _yaw = Mathf.Repeat(_yaw + delta.x * 0.35f, 360f);
            _pitch = Mathf.Clamp(_pitch + delta.y * 0.2f, -35f, 35f);
            ApplyView();
        }

        /// <summary>Applies an analog-stick orbit using the current frame's unscaled time.</summary>
        internal void OrbitStick(Vector2 stick, float unscaledDeltaTime)
        {
            Orbit(stick * (140f * unscaledDeltaTime));
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
            _pan = Vector3.zero;
            _yaw = 0f;
            _pitch = 0f;
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
            var target = new Vector3(0f, BaseTargetHeight, 0f) + _pan;
            var offset = Quaternion.Euler(_pitch, _yaw, 0f)
                * new Vector3(0f, BaseCameraHeight - BaseTargetHeight, distance);
            var cameraPosition = target + offset;
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

            if (_body == null || _bodyFemale != model.Female)
            {
                CreateBody(model.Female, model.ModelIndex, model);
            }

            _model = Clone(model);
            _modelIndex = CoopCharacterEditorModel.ClampModelIndex(_customization,
                model.Female, model.ModelIndex);
            ApplyModel();
            RefreshLayers();
            var applied = _body != null
                ? _body.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (_body != null && _stage != null)
            {
                var renderers = _body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var summary = "";
                for (var i = 0; i < renderers.Length && i < 8; i++)
                {
                    var renderer = renderers[i];
                    var material = renderer != null ? renderer.sharedMaterial : null;
                    summary += " [" + (renderer != null ? renderer.name : "<null>")
                        + " on=" + (renderer != null && renderer.enabled)
                        + " shadow=" + (renderer != null ? renderer.shadowCastingMode.ToString() : "-")
                        + " probe=" + (renderer != null ? renderer.lightProbeUsage.ToString() : "-")
                        + " mat=" + (material != null ? material.name : "<null>") + "]";
                }
                CoopPlugin.Log?.LogInfo("co-op character editor: body renderers="
                    + renderers.Length + summary);
            }
            var appliedMaterial = applied != null ? applied.sharedMaterial : null;
            CoopPlugin.Log?.LogInfo("co-op character editor: model applied material="
                + (appliedMaterial != null ? appliedMaterial.name : "<null>")
                + " skinTexture=" + (appliedMaterial != null
                    && appliedMaterial.HasProperty("_Skin_Texture")
                    && appliedMaterial.GetTexture("_Skin_Texture") != null
                    ? appliedMaterial.GetTexture("_Skin_Texture").name : "<none>")
                + " enabled=" + (applied != null && applied.enabled)
                + " layer=" + (applied != null ? applied.gameObject.layer : -1)
                + " json=" + (string.IsNullOrEmpty(model.CustomizationJson) ? 0
                    : model.CustomizationJson.Length));
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

            // The stage is deliberately far away from the world, so there are no baked light
            // probes or reflection probes anywhere near it. The game's character shaders are
            // SH/probe lit; leaving probe usage on makes them sample an empty probe set and
            // render black. Use the preview's own studio ambient instead.
            var renderers = _body.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                }

                // CharacterCustomization keeps shadow-only duplicates of every body part. In the
                // preview they would draw as a black silhouette over the real lit body, because
                // the preview camera has no shadow pass. Draw the body, never its shadow proxy.
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                    || renderer.name.IndexOf("_Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    renderer.enabled = false;
                }
            }
        }

        /// <summary>Drops the current body but keeps the stage/texture. Used when the Character
        /// tab is left and the temporary model lease is released.</summary>
        internal void ReleaseModel()
        {
            DestroyBody();
            _model = null;
            _modelIndex = 0;
        }

        internal void Destroy()
        {
            Camera.onPreRender -= OnPreviewCameraPreRender;
            Camera.onPostRender -= OnPreviewCameraPostRender;
            RestoreRenderSettings();
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
            _camera.allowHDR = true;
            _camera.allowMSAA = true;
            _camera.useOcclusionCulling = false;
            // The character shader is a forward Shader Graph material. Do not let the player's
            // deferred quality tier force a deferred pass it cannot draw.
            _camera.renderingPath = RenderingPath.Forward;
            _camera.targetTexture = _texture;

            _keyLight = AddLight("KeyLight", new Vector3(20f, 155f, 0f),
                new Color(1f, 0.95f, 0.88f), 1.55f, LightRenderMode.ForcePixel);
            AddLight("FillLight", new Vector3(10f, 215f, 0f),
                new Color(0.72f, 0.82f, 1f), 0.55f, LightRenderMode.ForceVertex);
            AddLight("RimLight", new Vector3(5f, 55f, 0f),
                new Color(1f, 0.86f, 0.95f), 0.85f, LightRenderMode.ForceVertex);
            AddPointLight("PreviewFill", new Vector3(0f, 1.4f, -2.4f), Color.white, 1.8f, 9f);

            CoopPlugin.Log?.LogInfo("co-op character editor: preview camera path="
                + _camera.renderingPath + " actual=" + _camera.actualRenderingPath
                + " pipeline=" + (GraphicsSettings.currentRenderPipeline != null
                    ? GraphicsSettings.currentRenderPipeline.GetType().FullName : "builtin"));
            _previewAmbient = BuildPreviewAmbient();
            Camera.onPreRender += OnPreviewCameraPreRender;
            Camera.onPostRender += OnPreviewCameraPostRender;
            _camera.enabled = _visible;
        }

        /// <summary>
        /// The preview stage is isolated 9000 units from the world, so the active scene's baked
        /// light probes never reach it and the game's character shaders (which are SH/probe lit)
        /// sample an empty probe set. Apply the preview's own studio probe only for the preview
        /// camera's render, then restore the scene's lighting in the post-render callback so the
        /// world behind the editor is never relit.
        /// </summary>
        private void OnPreviewCameraPreRender(Camera camera)
        {
            if (_camera == null || !ReferenceEquals(camera, _camera))
            {
                return;
            }
            if (_renderSettingsCaptured)
            {
                return;
            }

            _renderAmbientMode = RenderSettings.ambientMode;
            _renderAmbientLight = RenderSettings.ambientLight;
            _renderAmbientSky = RenderSettings.ambientSkyColor;
            _renderAmbientEquator = RenderSettings.ambientEquatorColor;
            _renderAmbientGround = RenderSettings.ambientGroundColor;
            _renderAmbientIntensity = RenderSettings.ambientIntensity;
            _renderAmbientProbe = RenderSettings.ambientProbe;
            _renderSun = RenderSettings.sun;
            _renderSettingsCaptured = true;

            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = BuildPreviewAmbient();
            RenderSettings.ambientIntensity = 1f;
            // The built-in forward main light is only guaranteed to be the scene sun. The preview
            // stage is isolated from the game's lighting, so hand it the preview key light.
            RenderSettings.sun = _keyLight != null ? _keyLight : _renderSun;
            if (!_lightingLogged)
            {
                _lightingLogged = true;
                LogPreviewLighting();
            }
        }

        private void OnPreviewCameraPostRender(Camera camera)
        {
            if (_camera == null || !ReferenceEquals(camera, _camera))
            {
                return;
            }
            RestoreRenderSettings();
        }

        /// <summary>
        /// The character shaders are SH-lit. A flat ambient colour gives no shape, so build a small
        /// studio probe: cool fill from everywhere, a warm key from the front-left/above and a
        /// cool rim from behind. This is the preview's own baked lighting, independent of the
        /// active scene's light probes (which are 9000 units away and therefore useless).
        /// </summary>
        private static SphericalHarmonicsL2 BuildPreviewAmbient()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.17f, 0.19f, 0.24f));
            probe.AddDirectionalLight(new Vector3(-0.4f, 0.6f, 0.8f).normalized,
                new Color(1f, 0.94f, 0.88f), 0.95f);
            probe.AddDirectionalLight(new Vector3(0.5f, 0.3f, -0.9f).normalized,
                new Color(0.72f, 0.82f, 1f), 0.4f);
            return probe;
        }

        private void RestoreRenderSettings()
        {
            if (!_renderSettingsCaptured)
            {
                return;
            }

            RenderSettings.ambientMode = _renderAmbientMode;
            RenderSettings.ambientLight = _renderAmbientLight;
            RenderSettings.ambientSkyColor = _renderAmbientSky;
            RenderSettings.ambientEquatorColor = _renderAmbientEquator;
            RenderSettings.ambientGroundColor = _renderAmbientGround;
            RenderSettings.ambientIntensity = _renderAmbientIntensity;
            RenderSettings.ambientProbe = _renderAmbientProbe;
            RenderSettings.sun = _renderSun;
            _renderSettingsCaptured = false;
        }

        private void LogPreviewLighting()
        {
            var lights = _stage != null
                ? _stage.GetComponentsInChildren<Light>(true) : Array.Empty<Light>();
            var summary = "";
            for (var i = 0; i < lights.Length; i++)
            {
                var light = lights[i];
                if (light == null)
                {
                    continue;
                }
                summary += " [" + light.name + " " + light.type + " mode=" + light.renderMode
                    + " intensity=" + light.intensity + " mask=" + light.cullingMask
                    + " enabled=" + light.enabled + "/" + light.gameObject.activeInHierarchy + "]";
            }
            CoopPlugin.Log?.LogInfo("co-op character editor: preview lighting ambientMode="
                + RenderSettings.ambientMode + " ambient=" + RenderSettings.ambientLight
                + " intensity=" + RenderSettings.ambientIntensity
                + " sun=" + (RenderSettings.sun != null ? RenderSettings.sun.name : "<none>")
                + summary);
        }

        private Light AddLight(string name, Vector3 euler, Color color, float intensity,
            LightRenderMode renderMode)
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
            light.renderMode = renderMode;
            light.cullingMask = 1 << PreviewLayer;
            return light;
        }

        private void AddPointLight(string name, Vector3 position, Color color, float intensity,
            float range)
        {
            var lightObject = new GameObject(name, typeof(Light));
            lightObject.layer = PreviewLayer;
            lightObject.transform.SetParent(_stage.transform, false);
            lightObject.transform.localPosition = position;
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForceVertex;
            light.cullingMask = 1 << PreviewLayer;
        }

        private void CreateBody(bool female, int modelIndex, PresenceModelEntry model)
        {
            CoopPlugin.Log?.LogInfo("co-op character editor: CreateBody female=" + female
                + " hasAny=" + CoopCharacterPrefabPreloader.HasAny);
            DestroyBody();
            CoopCharacterPrefabPreloader.EnsureStarted();

            if (!CoopCharacterPrefabPreloader.TryGet(female, out var preloaded))
            {
                // The preload is still running (or did not resolve). Keep the preview empty and
                // let the Completed event rebuild it; never substitute a dummy or a customer.
                CoopPlugin.Log?.LogInfo("co-op character editor: waiting for the real character "
                    + "prefab (" + (female ? "female" : "male") + ").");
                return;
            }

            if (!CreateFromPrefab(preloaded, "preloaded", female, modelIndex))
            {
                CoopPlugin.Log?.LogError("co-op character editor: the real character prefab could "
                    + "not be built for the " + (female ? "female" : "male") + " preview.");
            }
        }

        private bool CreateFromPrefab(GameObject prefab, string label, bool female,
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

                if (customization.MaterialsStorage == null)
                {
                    // Without the game's SharedMaterials the wardrobe cannot assign textures;
                    // refuse to show a black body rather than pretending it is a successful load.
                    CoopPlugin.Log?.LogError("co-op character editor: prefab '" + label
                        + "' has no SharedMaterials; refusing to render an untextured body.");
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
                ConfigureAnimator();
                RefreshLayers();
                var diagnostic = _body.GetComponentInChildren<SkinnedMeshRenderer>(true);
                var diagnosticMaterial = diagnostic != null ? diagnostic.sharedMaterial : null;
                CoopPlugin.Log?.LogInfo("co-op character editor: preview body built from '"
                    + label + "' (female=" + female + ") material="
                    + (diagnosticMaterial != null ? diagnosticMaterial.name : "<null>")
                    + " shader=" + (diagnosticMaterial != null && diagnosticMaterial.shader != null
                        ? diagnosticMaterial.shader.name : "<null>")
                    + " skinTexture=" + (diagnosticMaterial != null
                        && diagnosticMaterial.HasProperty("_Skin_Texture")
                        && diagnosticMaterial.GetTexture("_Skin_Texture") != null
                        ? diagnosticMaterial.GetTexture("_Skin_Texture").name : "<none>")
                    + " lights=" + (_stage != null
                        ? _stage.GetComponentsInChildren<Light>(true).Length : 0));
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

        private void ApplyModel()
        {
            if (_customization == null)
            {
                return;
            }

            try
            {
                PresenceLocalEditorApi.ApplyModel(_customization, new PresenceModelEntry
                {
                    Female = _bodyFemale,
                    ModelIndex = _modelIndex,
                    CustomizationJson = _model?.CustomizationJson,
                });
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor: applying the preview model failed: "
                    + error);
            }
        }

        private static string CharacterName(bool female, int modelIndex)
        {
            return (female ? "Female" : "Male") + Mathf.Max(0, modelIndex);
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
