using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Runtime;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The character tab edits either the active presence service (inside a co-op
    /// session) or the same persisted appearance directly (title screen / no session).</summary>
    internal interface ICharacterEditorBackend
    {
        bool Available
        {
            get;
        }

        bool Editable
        {
            get;
        }

        PresenceModelEntry Model
        {
            get;
        }

        CC.CharacterCustomization Customization
        {
            get;
        }

        bool CanUndo
        {
            get;
        }

        bool CanRedo
        {
            get;
        }

        List<string> PresetNames
        {
            get;
        }

        void RefreshModel();

        void SetGender(bool female);

        void ApplyPreset(string name);

        bool Undo();

        bool Redo();

        void SetBlendshape(string name, float value);

        void SetFloat(string name, float value);

        void SetColor(string name, Color color);

        void SetTexture(string name, string value);

        void SetHair(int slot, string name);

        void SetHairColor(int slot, Color color);

        void SetApparel(int slot, string name, int material);

        void SetApparelTint(int slot, Color color);

        void SetNsfw(bool allowed);

        void Save();
    }

    /// <summary>Adapter over <see cref="PresenceApi"/>. The presence service owns the model,
    /// history, network broadcast, and the debounced disk save while a session exists.</summary>
    internal sealed class ServiceCharacterEditorBackend : ICharacterEditorBackend
    {
        private CC.CharacterCustomization _customization;

        public bool Available
        {
            get
            {
                if (!PresenceLocalEditorApi.HasActiveService)
                {
                    return false;
                }

                return EnsureCustomization() != null;
            }
        }

        public bool Editable
        {
            get
            {
                if (!Available)
                {
                    return false;
                }

                var model = Model;
                var customization = EnsureCustomization();
                return model != null && CoopCharacterEditorModel.HasGenderPresets(customization,
                    model.Female);
            }
        }

        public PresenceModelEntry Model => PresenceApi.GetLocalPlayerModel();

        public CC.CharacterCustomization Customization => EnsureCustomization();

        public bool CanUndo => PresenceApi.CanUndoPlayerModel;

        public bool CanRedo => PresenceApi.CanRedoPlayerModel;

        public List<string> PresetNames => PresenceApi.GetLocalPresetNames();

        public void RefreshModel()
        {
            _customization = PresenceApi.GetLocalCustomization();
        }

        public void SetGender(bool female) => PresenceApi.SetLocalPlayerModel(female, 0);

        public void ApplyPreset(string name) => PresenceApi.ApplyLocalPreset(name);

        public bool Undo() => PresenceApi.UndoPlayerModel();

        public bool Redo() => PresenceApi.RedoPlayerModel();

        public void SetBlendshape(string name, float value)
            => PresenceApi.SetLocalModelSlider(name, value);

        public void SetFloat(string name, float value)
        {
            var customization = EnsureCustomization();
            if (customization == null)
            {
                return;
            }

            CoopCharacterEditorModel.SetFloat(customization, name, value);
            PresenceApi.CommitLocalCustomization();
        }

        public void SetColor(string name, Color color)
        {
            var customization = EnsureCustomization();
            if (customization == null)
            {
                return;
            }

            CoopCharacterEditorModel.SetColor(customization, name, color);
            PresenceApi.CommitLocalCustomization();
        }

        public void SetTexture(string name, string value)
        {
            var customization = EnsureCustomization();
            if (customization == null)
            {
                return;
            }

            CoopCharacterEditorModel.SetTexture(customization, name, value);
            PresenceApi.CommitLocalCustomization();
        }

        public void SetHair(int slot, string name)
        {
            var customization = EnsureCustomization();
            if (customization == null)
            {
                return;
            }

            if (CoopCharacterEditorModel.IsNoneName(name))
            {
                PresenceApi.ClearLocalHair(slot);
                return;
            }

            customization.setHairByName(name, slot);
            PresenceApi.CommitLocalCustomization();
        }

        public void SetHairColor(int slot, Color color)
            => PresenceApi.SetLocalHairColor(slot, color);

        public void SetApparel(int slot, string name, int material)
        {
            var customization = EnsureCustomization();
            if (customization == null)
            {
                return;
            }

            if (CoopCharacterEditorModel.IsNudeApparelName(name))
            {
                PresenceApi.ClearLocalApparel(slot);
                return;
            }

            var materialCount = CoopCharacterEditorModel.MaterialCount(customization, slot, name);
            var safeMaterial = materialCount > 0 ? Mathf.Clamp(material, 0, materialCount - 1) : 0;
            customization.setApparelByName(name, slot, safeMaterial);
            PresenceApi.CommitLocalCustomization();
        }

        public void SetApparelTint(int slot, Color color)
            => PresenceApi.SetLocalApparelTint(slot, color);

        public void SetNsfw(bool allowed) => PresenceApi.SetNsfwAllowed(allowed);

        public void Save()
        {
            // The presence service already debounces its own model save.
        }

        private CC.CharacterCustomization EnsureCustomization()
        {
            if (_customization == null || _customization.gameObject == null)
            {
                _customization = PresenceApi.GetLocalCustomization();
            }
            return _customization;
        }
    }

    /// <summary>Editor state for the title screen and for playing without a co-op session.
    /// It reuses the same persisted model file as the service, so the appearance edited here is
    /// exactly what the next session sends.</summary>
    internal sealed class LocalCharacterEditorBackend : ICharacterEditorBackend
    {
        private const int HistoryLimit = 30;

        private readonly CoopCharacterPreview _preview;
        private readonly List<PresenceModelEntry> _undo = new();
        private readonly List<PresenceModelEntry> _redo = new();
        private PresenceModelEntry _model;
        private bool _ready;

        internal LocalCharacterEditorBackend(CoopCharacterPreview preview)
        {
            _preview = preview;
        }

        public bool Available => _preview.Customization != null;

        public bool Editable
        {
            get
            {
                Ensure();
                return Available && _model != null
                    && CoopCharacterEditorModel.IsEditable(_preview.Customization, _model.Female);
            }
        }

        public PresenceModelEntry Model
        {
            get
            {
                Ensure();
                return _model;
            }
        }

        public CC.CharacterCustomization Customization
        {
            get
            {
                Ensure();
                return _preview.Customization;
            }
        }

        public bool CanUndo
        {
            get
            {
                Ensure();
                return _undo.Count > 0;
            }
        }

        public bool CanRedo
        {
            get
            {
                Ensure();
                return _redo.Count > 0;
            }
        }

        public List<string> PresetNames
        {
            get
            {
                Ensure();
                return _model == null
                    ? new List<string>()
                    : CoopCharacterEditorModel.PresetNames(_preview.Customization, _model.Female);
            }
        }

        public void RefreshModel()
        {
            _ready = false;
            _model = null;
            _undo.Clear();
            _redo.Clear();
            Ensure();
        }

        public void SetGender(bool female)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            _preview.Rebuild(female, 0);
            if (!ApplyRandomPreset(female, record: false))
            {
                _model = _preview.CaptureModel();
            }
            Save();
        }

        public void ApplyPreset(string name)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            var preset = CoopCharacterEditorModel.FindPreset(_preview.Customization, name);
            if (preset == null)
            {
                return;
            }

            var model = PresenceLocalEditorApi.ModelFromPreset(preset);
            if (model == null)
            {
                return;
            }

            RecordChange();
            _preview.SetModel(model);
            _model = _preview.CaptureModel() ?? model;
            Save();
        }

        public bool Undo()
        {
            Ensure();
            if (!Editable || _undo.Count == 0)
            {
                return false;
            }

            Push(_redo, _model);
            _model = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _preview.SetModel(_model);
            _model = _preview.CaptureModel() ?? _model;
            Save();
            return true;
        }

        public bool Redo()
        {
            Ensure();
            if (!Editable || _redo.Count == 0)
            {
                return false;
            }

            Push(_undo, _model);
            _model = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _preview.SetModel(_model);
            _model = _preview.CaptureModel() ?? _model;
            Save();
            return true;
        }

        public void SetBlendshape(string name, float value)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            PresenceLocalEditorApi.SetBlendshape(_preview.Customization, name, value);
            Commit();
        }

        public void SetFloat(string name, float value)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            CoopCharacterEditorModel.SetFloat(_preview.Customization, name, value);
            Commit();
        }

        public void SetColor(string name, Color color)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            CoopCharacterEditorModel.SetColor(_preview.Customization, name, color);
            Commit();
        }

        public void SetTexture(string name, string value)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            CoopCharacterEditorModel.SetTexture(_preview.Customization, name, value);
            Commit();
        }

        public void SetHair(int slot, string name)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            if (CoopCharacterEditorModel.IsNoneName(name))
            {
                PresenceLocalEditorApi.ClearHair(_preview.Customization, slot);
            }
            else
            {
                _preview.Customization.setHairByName(name, slot);
            }
            _preview.RefreshLayers();
            Commit();
        }

        public void SetHairColor(int slot, Color color)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            PresenceLocalEditorApi.SetHairColor(_preview.Customization, slot, color);
            Commit();
        }

        public void SetApparel(int slot, string name, int material)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            if (CoopCharacterEditorModel.IsNudeApparelName(name))
            {
                PresenceLocalEditorApi.ClearApparel(_preview.Customization, slot);
            }
            else
            {
                var materialCount = CoopCharacterEditorModel.MaterialCount(
                    _preview.Customization, slot, name);
                var safeMaterial = materialCount > 0
                    ? Mathf.Clamp(material, 0, materialCount - 1) : 0;
                _preview.Customization.setApparelByName(name, slot, safeMaterial);
            }
            _preview.RefreshLayers();
            Commit();
        }

        public void SetApparelTint(int slot, Color color)
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            RecordChange();
            PresenceLocalEditorApi.SetApparelTint(_preview.Customization, slot, color);
            Commit();
        }

        public void SetNsfw(bool allowed)
        {
            Ensure();
            if (CoopPlugin.AllowNsfw.Value == allowed)
            {
                return;
            }

            CoopPlugin.AllowNsfw.Value = allowed;
            if (!Editable)
            {
                return;
            }

            if (!allowed && CoopCharacterEditorModel.IsNude(_model))
            {
                RecordChange();
                if (!ApplyRandomPreset(_model.Female, record: false))
                {
                    _model = _preview.CaptureModel() ?? _model;
                }
                Save();
            }
        }

        public void Save()
        {
            Ensure();
            if (!Editable)
            {
                return;
            }

            Commit();
            PresenceLocalEditorApi.SaveModel(_model);
        }

        private void Ensure()
        {
            if (_ready)
            {
                return;
            }
            _ready = true;

            var loaded = PresenceLocalEditorApi.TryLoadSavedModel(out _model);
            if (!loaded)
            {
                loaded = TryCaptureGamePlayer(out _model);
            }
            if (_model == null)
            {
                _model = new PresenceModelEntry
                {
                    Female = UnityEngine.Random.value < 0.5f,
                    ModelIndex = 0,
                    CustomizationJson = null,
                };
                loaded = false;
            }
            else if (!string.IsNullOrEmpty(_model.CustomizationJson)
                && !CoopCharacterEditorModel.IsUsableJson(_model.CustomizationJson))
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: the saved co-op appearance "
                    + "could not be decoded; rebuilding it from the game preset.");
                _model.CustomizationJson = null;
                loaded = false;
            }

            _preview.SetModel(_model);
            if (!Editable)
            {
                // A limited title-screen body (CC_Dummy) only draws the preview. Never capture
                // from it: it has fewer wardrobe slots and would truncate the saved model.
                return;
            }

            if (string.IsNullOrEmpty(_model.CustomizationJson))
            {
                _model = _preview.CaptureModel() ?? _model;
                loaded = false;
            }

            if (!CoopPlugin.AllowNsfw.Value && CoopCharacterEditorModel.IsNude(_model))
            {
                if (ApplyRandomPreset(_model.Female, record: false))
                {
                    loaded = false;
                }
            }

            if (!loaded)
            {
                _model = _preview.CaptureModel() ?? _model;
                PresenceLocalEditorApi.SaveModel(_model);
            }
        }

        private bool ApplyRandomPreset(bool female, bool record)
        {
            var names = CoopCharacterEditorModel.PresetNames(_preview.Customization, female);
            if (names.Count == 0)
            {
                return false;
            }

            var name = names[UnityEngine.Random.Range(0, names.Count)];
            var preset = CoopCharacterEditorModel.FindPreset(_preview.Customization, name);
            var model = PresenceLocalEditorApi.ModelFromPreset(preset);
            if (model == null)
            {
                return false;
            }

            if (record)
            {
                RecordChange();
            }
            _preview.SetModel(model);
            _model = _preview.CaptureModel() ?? model;
            return true;
        }

        private void RecordChange()
        {
            Push(_undo, _model);
            _redo.Clear();
        }

        private void Commit()
        {
            if (!Editable)
            {
                return;
            }
            _model = _preview.CaptureModel() ?? _model;
        }

        private void Push(List<PresenceModelEntry> history, PresenceModelEntry model)
        {
            var copy = CoopCharacterEditorModel.Clone(model);
            if (copy == null)
            {
                return;
            }

            history.Add(copy);
            if (history.Count > HistoryLimit)
            {
                history.RemoveAt(0);
            }
        }

        private static bool TryCaptureGamePlayer(out PresenceModelEntry model)
        {
            model = null;
            var player = CGameManager.Player;
            if (player == null)
            {
                var controller = SceneRef<InteractionPlayerController>.Get();
                if (controller != null && controller.m_WalkerCtrl != null)
                {
                    player = controller.m_WalkerCtrl.transform;
                }
            }
            if (player == null)
            {
                return false;
            }

            CC.CharacterCustomization customization = null;
            try
            {
                customization = player.GetComponentInChildren<CC.CharacterCustomization>(true);
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("co-op character editor: reading the game player body "
                    + "failed: " + error.Message);
                return false;
            }

            if (customization?.StoredCharacterData == null)
            {
                return false;
            }

            model = new PresenceModelEntry
            {
                Female = CoopCharacterEditorModel.IsFemaleName(customization.CharacterName),
                ModelIndex = CoopCharacterEditorModel.ParseModelIndex(customization.CharacterName),
                CustomizationJson = JsonConvert.SerializeObject(customization.StoredCharacterData),
            };
            return true;
        }
    }
}
