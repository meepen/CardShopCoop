using System;
using UnityEngine;

namespace CardShopCoop.Modules.DebugTools
{
    /// <summary>
    /// Reflection-only boundary for CheatManager. The legacy game does not expose that type,
    /// so this module must not put a CheatManager token in the plugin's type references.
    /// </summary>
    internal static class DebugToolsRuntime
    {
        private static readonly Type _cheatManagerType =
            typeof(CGameManager).Assembly.GetType("CheatManager", false);

        private static DebugToolsBehaviour _active;
        private static Component _ownedManager;
        private static bool _cheatMenuOpen;

        internal static Type CheatManagerType => _cheatManagerType;

        internal static bool CheatsEnabledForHost
        {
            get
            {
                return _active != null && CoopCore.Role != CoopRole.Client
                    && CoopPlugin.ShowHiddenCategory != null && CoopPlugin.ShowHiddenCategory.Value
                    && CoopPlugin.EnableGameCheatMenu != null && CoopPlugin.EnableGameCheatMenu.Value;
            }
        }

        internal static void Attach(DebugToolsBehaviour behaviour)
        {
            if (_active != null && !ReferenceEquals(_active, behaviour))
            {
                throw new InvalidOperationException("DebugTools is already active for another session.");
            }

            _active = behaviour;
            _ownedManager = null;
            _cheatMenuOpen = false;
        }

        internal static void Detach(DebugToolsBehaviour behaviour)
        {
            if (!ReferenceEquals(_active, behaviour))
            {
                return;
            }

            if (_cheatMenuOpen)
            {
                // The owned menu can be destroyed without another SetMenuOpen transition. Tell
                // SessionInput first so its walker/timeScale ownership is released coherently.
                _cheatMenuOpen = false;
                CardShopCoop.Modules.SessionInput.SessionInputRuntime.CheatMenuChanged(false);
            }

            if (_ownedManager != null)
            {
                UnityEngine.Object.Destroy(_ownedManager.gameObject);
            }

            _ownedManager = null;
            _cheatMenuOpen = false;
            _active = null;
        }

        internal static void CheatMenuChanged(bool open)
        {
            if (_active == null)
            {
                return;
            }

            _cheatMenuOpen = open;
            CardShopCoop.Modules.SessionInput.SessionInputRuntime.CheatMenuChanged(open);
        }

        internal static void CloseForSessionStop()
        {
            if (_active == null || !_cheatMenuOpen || _cheatManagerType == null)
            {
                return;
            }

            try
            {
                var manager = _ownedManager != null ? _ownedManager
                    : FindSceneComponent(_cheatManagerType);
                var close = _cheatManagerType.GetMethod("SetMenuOpen",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                close?.Invoke(manager, new object[] { false });
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("DebugTools could not close cheat menu at session end: "
                    + error.Message);
            }
            finally
            {
                _cheatMenuOpen = false;
                CardShopCoop.Modules.SessionInput.SessionInputRuntime.CheatMenuChanged(false);
            }
        }

        internal static void BootstrapCheatManager()
        {
            if (_active == null || _cheatManagerType == null)
            {
                return;
            }

            try
            {
                if (!typeof(Component).IsAssignableFrom(_cheatManagerType))
                {
                    CoopPlugin.Log.LogWarning("DebugTools: reflected CheatManager is not a Component");
                    return;
                }

                var existing = FindSceneComponent(_cheatManagerType);
                if (existing != null)
                {
                    return;
                }

                var go = new GameObject("CardShopCoopGameCheatManager");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var manager = go.AddComponent(_cheatManagerType) as Component;
                if (manager == null)
                {
                    UnityEngine.Object.Destroy(go);
                    CoopPlugin.Log.LogWarning("DebugTools: reflected CheatManager could not be added");
                    return;
                }

                HarmonyLib.AccessTools.Field(_cheatManagerType, "m_CheatCanvasPrefab")?.SetValue(
                    manager, Resources.Load<GameObject>("CheatUI_Root"));
                _ownedManager = manager;
                CoopPlugin.Log.LogInfo(
                    "DebugTools: game cheat manager bootstrapped for gated host/testing use");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("DebugTools cheat manager bootstrap failed: " + e.Message);
            }
        }

        private static Component FindSceneComponent(Type componentType)
        {
            var all = Resources.FindObjectsOfTypeAll(componentType);
            for (var i = 0; i < all.Length; i++)
            {
                var component = all[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    return component;
                }
            }

            return null;
        }
    }
}
