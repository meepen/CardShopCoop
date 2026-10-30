using System;
using CardShopCoop.Api;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Host-owned shop-light state. State, not a command: the host owns every toggle and
    /// publishes the resulting flag to the other peers.</summary>
    [NetworkMessage]
    public sealed class LightSwitchStateMessage : INetMessage
    {
        public bool IsActive;
    }

    /// <summary>Client command: set the shared shop-light flag. The game already toggled the
    /// light locally, so <see cref="OnUndo"/> restores the captured prior flag and
    /// <see cref="OnMerge"/> applies the host's absolute answer.</summary>
    [NetworkMessage]
    public sealed class LightSwitchIntentMessage : ActionMessage
    {
        public bool IsActive;

        /// <summary>The shop-light flag before the local toggle. Local undo state; not
        /// serialized.</summary>
        [NonSerialized]
        public bool Previous;

        public override void OnUndo(ActionResponseMessage response)
            => LightSwitchState.Apply(Previous);

        public override void OnMerge(ActionResponseMessage response)
        {
            if (response is LightSwitchResponse answer)
            {
                LightSwitchState.Apply(answer.IsActive);
            }
        }
    }

    /// <summary>The host's absolute shop-light flag after one toggle. The framework sets
    /// <see cref="ActionResponseMessage.Rejected"/>; the flag is authoritative either way.</summary>
    [NetworkMessage]
    public sealed class LightSwitchResponse : ActionResponseMessage
    {
        public bool IsActive;
    }

    /// <summary>Applies the one shop-light flag through the game's live lighting surface.
    /// Updating the group alone is insufficient: LightManager caches the flag that its public
    /// readers use, and each physical switch owns separate visual models.</summary>
    internal static class LightSwitchState
    {
        internal static bool TryGet(out bool isActive)
        {
            isActive = false;
            var manager = LightInterop.FindSceneManager();
            if (!LightInterop.IsSceneReady(manager) || manager.m_ShoplightGrp == null)
            {
                return false;
            }

            isActive = manager.m_ShoplightGrp.activeSelf;
            return true;
        }

        internal static void Apply(bool isActive)
        {
            var manager = LightInterop.FindSceneManager();
            if (manager.m_ShoplightGrp.activeSelf != isActive)
            {
                manager.m_ShoplightGrp.SetActive(isActive);
                LightInterop.EvaluateWorldUIBrightness.Invoke(manager, null);
            }

            ApplySwitchModels(isActive);
        }

        private static readonly int LightStateProperty = Shader.PropertyToID("_LightState");

        private static void ApplySwitchModels(bool isActive)
        {
            // The two live builds render the switch differently:
            //   1.03 Steam  - toggle the on/off model GameObjects (vanilla OnMouseButtonUp).
            //   1.04 Xbox   - rotate the on-model and drive _LightState on the off-model.
            // GamePassBuild is the same discriminator PlatformProbe already uses for the save
            // backend, and the material-property check keeps this safe if the visuals converge.
            var usesMaterialState = Net.PlatformProbe.GamePassBuild;
            var switches = Resources.FindObjectsOfTypeAll<InteractableLightSwitch>();
            for (var i = 0; i < switches.Length; i++)
            {
                var lightSwitch = switches[i];
                if (lightSwitch == null || !lightSwitch.gameObject.scene.IsValid())
                {
                    continue;
                }

                var offRenderer = lightSwitch.m_SwitchOffModel != null
                    ? lightSwitch.m_SwitchOffModel.GetComponentInChildren<MeshRenderer>(true)
                    : null;
                if (usesMaterialState && offRenderer != null
                    && offRenderer.sharedMaterial != null
                    && offRenderer.sharedMaterial.HasProperty(LightStateProperty))
                {
                    if (lightSwitch.m_SwitchOnModel != null)
                    {
                        lightSwitch.m_SwitchOnModel.transform.rotation =
                            Quaternion.Euler(isActive ? -9f : 9f, 0f, 0f);
                    }

                    offRenderer.material.SetFloat(LightStateProperty, isActive ? 1f : 0f);
                    continue;
                }

                lightSwitch.m_SwitchOnModel?.SetActive(isActive);

                lightSwitch.m_SwitchOffModel?.SetActive(!isActive);
            }
        }
    }
}
