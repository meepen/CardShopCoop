using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>All game-facing state owned by the presence module. Network behaviours only
    /// provide authority and transport; this service keeps model editing and rendering usable
    /// by UI code without exposing either behaviour's internals.</summary>
    internal sealed class PresenceService
    {
        private const int ModelHistoryLimit = 30;
        private static readonly FieldInfo HoldItemsField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");
        private static readonly FieldInfo HoldCardsField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingCard3dList");
        private static readonly FieldInfo ViewAlbumField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsViewCardAlbumMode");
        private static readonly FieldInfo ActiveSprayField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldSprayItem");
        private static readonly FieldInfo HoldingBoxField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBox");
        private static readonly FieldInfo HoldingItemBoxField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingItemBox");
        private static readonly FieldInfo ItemBoxBigField =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_IsBigBox");
        private static readonly FieldInfo ItemBoxTypeField =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_ItemType");

        private readonly CoopRuntimeContext _context;
        private readonly List<int> _holdTypes = new();
        private readonly List<CardData> _holdCards = new();
        private readonly List<PresenceModelEntry> _undo = new();
        private readonly List<PresenceModelEntry> _redo = new();
        private readonly PresenceAvatarRenderer _renderer = new();
        private PresenceModelEntry _localModel;
        private PresenceModelEntry _lastCommittedModel;
        private bool _localModelReady;
        private bool _autoRepair;
        private Transform _appliedModelRoot;
        private bool _savePending;
        private float _saveIn;
        private bool _previewVisible;
        private InteractionPlayerController _ipc;
        private Transform _playerTransform;
        private Transform _cameraTransform;
        private Vector3 _lastStatePosition;
        private bool _hasLastStatePosition;
        private float _lastStateTime;
        private bool _shutdown;
        private int _modelGeneration;

        internal static PresenceService Active
        {
            get; private set;
        }

        internal PresenceService(CoopRuntimeContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Active = this;
        }

        internal Action<PresenceModelEntry> ModelChanged;
        internal int ModelGeneration => _modelGeneration;

        internal void Tick(float deltaTime)
        {
            if (_shutdown)
            {
                return;
            }

            if (_context.InGame())
            {
                ResolvePlayer();
                if (_context.PreloadHold?.Invoke() != true)
                {
                    PresenceAvatarRenderer.ViewCamera = _cameraTransform;
                    _renderer.Tick(deltaTime);
                    _renderer.UpdatePreview(_previewVisible ? EnsureLocalModel() : null,
                        _playerTransform, _cameraTransform, _previewVisible);
                }
                else
                {
                    _renderer.DestroyPreview();
                }
            }
            else
            {
                _renderer.DestroyPreview();
            }

            FlushModelSave(deltaTime);
        }

        internal bool TryGetLocalState(out PresenceStateMessage state)
        {
            ResolvePlayer();
            if (_playerTransform == null)
            {
                state = null;
                return false;
            }

            var position = _playerTransform.position;
            var camera = _cameraTransform != null ? _cameraTransform : Camera.main?.transform;
            var yaw = camera != null ? camera.eulerAngles.y : _playerTransform.eulerAngles.y;
            var cameraPosition = camera != null ? camera.position : position;
            var cameraRotation = camera != null ? camera.rotation : Quaternion.Euler(0f, yaw, 0f);
            var hold = ComputeHoldState();
            var now = Time.unscaledTime;
            var speed = 0f;
            if (_hasLastStatePosition)
            {
                var elapsed = Mathf.Max(0.0001f, now - _lastStateTime);
                var delta = position - _lastStatePosition;
                delta.y = 0f;
                speed = Mathf.Clamp(delta.magnitude / elapsed, 0f, 6f);
            }

            _lastStatePosition = position;
            _lastStateTime = now;
            _hasLastStatePosition = true;
            state = new PresenceStateMessage
            {
                Position = position,
                Yaw = yaw,
                CameraPosition = cameraPosition,
                CameraRotation = cameraRotation,
                Speed = speed,
                Hold = hold,
                HoldTypes = hold == 3 ? null : new List<int>(_holdTypes),
                HoldCards = hold == 3 ? new List<CardData>(_holdCards) : null,
            };
            return IsFinite(state);
        }

        internal bool TryGetLocalPosition(out Vector3 position)
        {
            position = ResolvePlayer() != null ? _playerTransform.position : default(Vector3);
            return _playerTransform != null && IsFinite(position);
        }

        internal void SetName(int id, string name) => _renderer.SetName(id, name);

        internal void SetModel(int id, PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            _renderer.SetModel(id, model.Female, model.ModelIndex, model.CustomizationJson);
        }

        internal void ApplyAuthoritativeLocalModel(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            _localModel = Clone(model);
            _localModelReady = true;
            _autoRepair = false;
            _lastCommittedModel = Clone(model);
            _appliedModelRoot = null;
        }

        internal void ApplyPredictionLocalModel(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            _localModel = Clone(model);
            _localModelReady = true;
            _autoRepair = false;
            _lastCommittedModel = Clone(model);
            _appliedModelRoot = null;

            var custom = _renderer.GetEditorCustomization(_localModel.Female);
            if (custom != null)
            {
                _renderer.ApplyLocalModel(custom, _localModel);
                _appliedModelRoot = custom.transform;
            }

            QueueModelSave();
        }

        internal void ApplyState(int avatarId, PresenceStateMessage state)
        {
            _renderer.UpdateState(avatarId, state.Position, state.Yaw, state.Hold,
                state.CameraPosition, state.CameraRotation, state.HoldTypes, state.HoldCards);
        }

        /// <summary>Chest-bone anchor of the avatar holding a box, so the world module can ride the
        /// real box on the animated skeleton. <paramref name="holderConnectionId"/> is the holder's
        /// connection id as the sender saw it (0 = the host itself).</summary>
        internal bool TryGetRemoteCarryAnchor(int holderConnectionId, out Transform anchor)
        {
            anchor = null;
            var avatarId = MapHolderToAvatarId(holderConnectionId);
            return avatarId != 0 && _renderer.TryGetCarryAnchor(avatarId, out anchor);
        }

        private static int MapHolderToAvatarId(int holderConnectionId)
        {
            // Host: avatars are keyed by the peer's connection id (0 is the host itself - no remote
            // avatar to attach to). Client: the host is avatar 1 and other peers are relayed as
            // 1000 + the host-space sender id.
            if (PresenceHostBehaviour.Active != null)
            {
                return holderConnectionId;
            }

            return holderConnectionId == 0 ? 1 : 1000 + holderConnectionId;
        }

        internal void ShowEmote(int id) => _renderer.ShowEmote(id);

        internal void ShowActivity(int id, int pack)
        {
            _renderer.ShowTag(id, "opening a pack!", 3f);
            _renderer.ShowPackOpen(id, pack);
        }

        internal void Remove(int id) => _renderer.Remove(id);

        internal bool TryGetPeerCamera(int id, out Vector3 position, out Quaternion rotation)
            => _renderer.TryGetPlacementCamera(id, out position, out rotation);

        /// <summary>Camera pose of the peer holding something, keyed by the holder's connection id
        /// (0 = the host) so callers do not have to know the role-specific avatar id.</summary>
        internal bool TryGetPeerCameraForHolder(int holderConnectionId, out Vector3 position,
            out Quaternion rotation)
        {
            var avatarId = MapHolderToAvatarId(holderConnectionId);
            if (avatarId != 0)
            {
                return _renderer.TryGetPlacementCamera(avatarId, out position, out rotation);
            }

            position = default(Vector3);
            rotation = Quaternion.identity;
            return false;
        }

        internal PresenceModelEntry EnsureLocalModel()
        {
            if (!_localModelReady)
            {
                var loaded = PresenceModelStore.TryLoad(out _localModel);
                _localModelReady = true;
                if (!loaded)
                {
                    ResolvePlayer();
                    _localModel = _playerTransform != null
                        ? _renderer.CaptureLocalModel(_playerTransform)
                        : null;
                    if (_localModel == null)
                    {
                        _localModel = _renderer.CreateDefaultModel();
                    }
                }

                _autoRepair = NeedsAutoRepair(_localModel);
            }

            if (_autoRepair)
            {
                TryUpgradeAutoModel();
            }

            var editor = _renderer.GetEditorCustomization(_localModel.Female);
            if (editor != null && _appliedModelRoot != editor.transform)
            {
                var before = _localModel.CustomizationJson;
                _renderer.ApplyLocalModel(editor, _localModel);
                _appliedModelRoot = editor.transform;
                if (before != _localModel.CustomizationJson)
                {
                    _lastCommittedModel = Clone(_localModel);
                    QueueModelSave();
                }
            }

            _lastCommittedModel ??= Clone(_localModel);
            return _localModel;
        }

        internal PresenceModelEntry GetLocalModel() => Clone(EnsureLocalModel());

        internal bool CanUndo => _undo.Count > 0;
        internal bool CanRedo => _redo.Count > 0;

        internal CC.CharacterCustomization GetLocalCustomization()
        {
            var model = EnsureLocalModel();
            var custom = _renderer.GetEditorCustomization(model.Female);
            if (custom != null && _appliedModelRoot != custom.transform)
            {
                _renderer.ApplyLocalModel(custom, model);
                _appliedModelRoot = custom.transform;
            }

            return custom;
        }

        internal List<CC.CC_Property> GetLocalSliders()
            => _renderer.GetBlendshapes(GetLocalCustomization());

        internal void SetPreview(bool active) => _previewVisible = active;

        internal void CommitCustomization()
        {
            var model = EnsureLocalModel();
            RecordChange();
            var custom = GetLocalCustomization();
            if (custom == null)
            {
                return;
            }

            _autoRepair = false;
            _localModel = _renderer.CaptureLocalModel(custom, model.Female, model.ModelIndex);
            _lastCommittedModel = Clone(_localModel);
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            _modelGeneration++;
        }

        internal bool Undo()
        {
            EnsureLocalModel();
            if (_undo.Count == 0)
            {
                return false;
            }

            Push(_redo, _lastCommittedModel ?? _localModel);
            _localModel = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _lastCommittedModel = Clone(_localModel);
            _appliedModelRoot = null;
            GetLocalCustomization();
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            _modelGeneration++;
            return true;
        }

        internal bool Redo()
        {
            EnsureLocalModel();
            if (_redo.Count == 0)
            {
                return false;
            }

            Push(_undo, _lastCommittedModel ?? _localModel);
            _localModel = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _lastCommittedModel = Clone(_localModel);
            _appliedModelRoot = null;
            GetLocalCustomization();
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            _modelGeneration++;
            return true;
        }

        internal List<string> GetPresetNames()
        {
            var result = new List<string>();
            var custom = GetLocalCustomization();
            var presets = custom?.Presets?.Presets;
            if (presets == null)
            {
                return result;
            }

            var prefix = EnsureLocalModel().Female ? "Female" : "Male";
            foreach (var preset in presets)
            {
                if (preset != null && (preset.CharacterName ?? "").StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(preset.CharacterName);
                }
            }

            return result;
        }

        internal void ApplyPreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName))
            {
                return;
            }

            var custom = GetLocalCustomization();
            var presets = custom?.Presets?.Presets;
            if (presets == null)
            {
                return;
            }

            CC.CC_CharacterData selected = null;
            foreach (var preset in presets)
            {
                if (preset != null && preset.CharacterName == presetName)
                {
                    selected = preset;
                    break;
                }
            }

            var model = PresenceAvatarRenderer.ModelFromPreset(selected);
            if (model == null)
            {
                return;
            }

            RecordChange();
            _autoRepair = false;
            _localModel = model;
            _appliedModelRoot = null;
            GetLocalCustomization();
            _lastCommittedModel = Clone(_localModel);
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            _modelGeneration++;
        }

        internal void SetModel(bool female, int modelIndex)
        {
            EnsureLocalModel();
            RecordChange();
            var genderChanged = _localModel.Female != female;
            _localModel.Female = female;
            _localModel.ModelIndex = Mathf.Max(0, modelIndex);
            if (genderChanged)
            {
                var preset = _renderer.TryCreateRandomPresetModel(female);
                _localModel.CustomizationJson = preset?.CustomizationJson;
                if (preset != null)
                {
                    _localModel.ModelIndex = preset.ModelIndex;
                }
            }

            _autoRepair = false;
            _appliedModelRoot = null;
            GetLocalCustomization();
            _lastCommittedModel = Clone(_localModel);
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            _modelGeneration++;
        }

        internal bool SetSlider(string propertyName, float value)
        {
            EnsureLocalModel();
            RecordChange();
            var custom = GetLocalCustomization();
            if (!_renderer.SetBlendshape(custom, propertyName, value))
            {
                return false;
            }

            _autoRepair = false;
            _localModel = _renderer.CaptureLocalModel(custom, _localModel.Female, _localModel.ModelIndex);
            _lastCommittedModel = Clone(_localModel);
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
            return true;
        }

        internal bool ClearHair(int slot)
        {
            if (!_renderer.ClearHair(GetLocalCustomization(), slot))
            {
                return false;
            }

            CommitCustomization();
            return true;
        }

        internal bool ClearApparel(int slot)
        {
            if (!_renderer.ClearApparel(GetLocalCustomization(), slot))
            {
                return false;
            }

            CommitCustomization();
            return true;
        }

        internal bool SetHairColor(int slot, Color color)
        {
            if (!_renderer.SetHairColor(GetLocalCustomization(), slot, color))
            {
                return false;
            }

            CommitCustomization();
            return true;
        }

        internal bool SetApparelTint(int slot, Color color)
        {
            if (!_renderer.SetApparelTint(GetLocalCustomization(), slot, color))
            {
                return false;
            }

            CommitCustomization();
            return true;
        }

        internal void SetNsfwAllowed(bool allowed)
        {
            if (CoopPlugin.AllowNsfw.Value == allowed)
            {
                return;
            }

            CoopPlugin.AllowNsfw.Value = allowed;
            EnsureLocalModel();
            if (!allowed && PresenceAvatarRenderer.IsNude(_localModel))
            {
                var preset = _renderer.TryCreateRandomPresetModel(_localModel.Female);
                if (preset != null)
                {
                    RecordChange();
                    _localModel = preset;
                    _autoRepair = false;
                    _appliedModelRoot = null;
                    _lastCommittedModel = Clone(_localModel);
                    QueueModelSave();
                    ModelChanged?.Invoke(Clone(_localModel));
                    _modelGeneration++;
                }
                else
                {
                    _autoRepair = true;
                }
            }

            _renderer.RefreshRemoteAvatars();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }

            _renderer.Clear();
            _undo.Clear();
            _redo.Clear();
            _localModel = null;
            _lastCommittedModel = null;
            _ipc = null;
            _playerTransform = null;
            _cameraTransform = null;
            ModelChanged = null;
        }

        private Transform ResolvePlayer()
        {
            if (_playerTransform != null)
            {
                return _playerTransform;
            }

            var ipc = SceneRef<InteractionPlayerController>.Get();

            if (ipc == null)
            {
                return null;
            }

            _ipc = ipc;
            _playerTransform = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
            _cameraTransform = ipc.m_Cam != null ? ipc.m_Cam.transform : null;
            return _playerTransform;
        }

        private void TryUpgradeAutoModel()
        {
            var preset = _renderer.TryCreateRandomPresetModel(_localModel.Female);
            if (preset == null)
            {
                return;
            }

            _localModel = preset;
            _autoRepair = false;
            _appliedModelRoot = null;
            _lastCommittedModel = Clone(_localModel);
            QueueModelSave();
            ModelChanged?.Invoke(Clone(_localModel));
        }

        private void RecordChange()
        {
            EnsureLocalModel();
            Push(_undo, _lastCommittedModel ?? _localModel);
            _redo.Clear();
        }

        private static void Push(List<PresenceModelEntry> history, PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            history.Add(Clone(model));
            if (history.Count > ModelHistoryLimit)
            {
                history.RemoveAt(0);
            }
        }

        private void QueueModelSave()
        {
            _savePending = true;
            _saveIn = 0.4f;
        }

        private void FlushModelSave(float deltaTime)
        {
            if (!_savePending || _localModel == null)
            {
                return;
            }

            _saveIn -= deltaTime;
            if (_saveIn > 0f)
            {
                return;
            }

            _savePending = false;
            PresenceModelStore.Save(_localModel);
        }

        private static bool NeedsAutoRepair(PresenceModelEntry model)
        {
            return model == null || string.IsNullOrEmpty(model.CustomizationJson)
                || !CoopPlugin.AllowNsfw.Value && PresenceAvatarRenderer.IsNude(model);
        }

        private static PresenceModelEntry Clone(PresenceModelEntry model)
        {
            return model == null
                ? new PresenceModelEntry()
                : new PresenceModelEntry
                {
                    Id = model.Id,
                    Female = model.Female,
                    ModelIndex = model.ModelIndex,
                    CustomizationJson = model.CustomizationJson,
                };
        }

        private byte ComputeHoldState()
        {
            _holdTypes.Clear();
            _holdCards.Clear();
            if (_ipc == null)
            {
                return 0;
            }

            try
            {
                var active = AppendActiveHeldItem();
                if (HoldItemsField?.GetValue(_ipc) is List<Item> items && items.Count > 0)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i] != null)
                        {
                            _holdTypes.Add((int)items[i].GetItemType());
                        }
                    }

                    if (!active)
                    {
                        AppendActiveHeldItem();
                    }

                    return 2;
                }

                if (AppendActiveHeldItem())
                {
                    return 2;
                }

                if (HoldingBoxField?.GetValue(_ipc) != null || HoldingItemBoxField?.GetValue(_ipc) != null)
                {
                    var box = HoldingItemBoxField?.GetValue(_ipc);
                    if (box != null && ItemBoxTypeField?.GetValue(box) is EItemType itemType)
                    {
                        _holdTypes.Add(ItemBoxBigField?.GetValue(box) is bool big && big ? 1 : 0);
                        _holdTypes.Add((int)itemType);
                    }

                    return 1;
                }

                if (HoldCardsField?.GetValue(_ipc) is List<InteractableCard3d> cards && cards.Count > 0)
                {
                    for (var i = 0; i < cards.Count; i++)
                    {
                        var card = cards[i];
                        if (card != null && card.m_Card3dUI != null && card.m_Card3dUI.m_CardUI != null)
                        {
                            _holdCards.Add(card.m_Card3dUI.m_CardUI.GetCardData());
                        }
                    }

                    if (_holdCards.Count > 0)
                    {
                        return 3;
                    }
                }

                if (ViewAlbumField?.GetValue(_ipc) is bool album && album)
                {
                    return 4;
                }
            }
            catch (Exception error)
            {
                Swallow.Log(error);
            }

            return 0;
        }

        private bool AppendActiveHeldItem()
        {
            var active = ActiveSprayField?.GetValue(_ipc) as Item;
            if (active == null)
            {
                return false;
            }

            var type = (int)active.GetItemType();
            if (_holdTypes.Contains(type))
            {
                return true;
            }

            _holdTypes.Add(type);

            return true;
        }

        private static bool IsFinite(PresenceStateMessage state)
        {
            return state != null && IsFinite(state.Position) && IsFinite(state.CameraPosition)
                && IsFinite(state.Yaw) && IsFinite(state.Speed) && IsFinite(state.CameraRotation);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
