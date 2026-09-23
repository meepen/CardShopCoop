using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Host-owned shop-light state.</summary>
    [NetworkMessage]
    public sealed class LightSwitchStateMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool IsActive;
    }

    [NetworkMessage]
    public sealed class LightSwitchIntentMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
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

        private static void ApplySwitchModels(bool isActive)
        {
            var switches = Resources.FindObjectsOfTypeAll<InteractableLightSwitch>();
            for (var i = 0; i < switches.Length; i++)
            {
                var lightSwitch = switches[i];
                if (lightSwitch == null || !lightSwitch.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (lightSwitch.m_SwitchOnModel != null)
                {
                    lightSwitch.m_SwitchOnModel.SetActive(isActive);
                }

                if (lightSwitch.m_SwitchOffModel != null)
                {
                    lightSwitch.m_SwitchOffModel.SetActive(!isActive);
                }
            }
        }
    }
}
