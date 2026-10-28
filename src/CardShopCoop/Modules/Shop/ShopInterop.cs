using System;
using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Scene lookup boundary for both live Unity 6 builds (Steam and Game Pass).</summary>
    internal static class ShopInterop
    {
        // The renamer deactivates its own GameObject after the shop is named (and on load once
        // the tutorial is done), so it is invisible to an active-only scene search. The sign
        // repaint therefore needs its own lookup that can see the inactive object; the naming
        // side-effect path keeps the active-only lookup so its tutorial/entitlement timing is
        // unchanged.
        private static ShopRenamer _signRenamer;

        private static readonly FieldInfo FiIsTutorial = ReflectionSurface.RequiredField(
            typeof(ShopRenamer), "m_IsTutorial");
        private static readonly FieldInfo FiTutorialIndex = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_TutorialIndex");
        private static readonly FieldInfo FiTutorialIndicator = ReflectionSurface.RequiredField(
            typeof(TutorialManager), "m_TutorialTargetIndicator");
        private static readonly MethodInfo MiEvaluateTaskVisibility = ReflectionSurface.RequiredMethod(
            typeof(TutorialManager), "EvaluateTaskVisibility");
        private static Type _grantBonusType;
        private static ConstructorInfo _grantBonusConstructor;
        private static bool _grantBonusResolved;

        static ShopInterop()
        {
            SceneRef.Register(
                () => _signRenamer = null,
                scene => InvalidateForScene(scene),
                scene => InvalidateForScene(scene));
        }

        /// <summary>Drop the cached sign renamer. Called when the session ends or its scene goes
        /// away; a destroyed Unity object also compares equal to null, so a missed reset cannot
        /// pin a stale reference.</summary>
        internal static void Reset()
        {
            _signRenamer = null;
        }

        private static void InvalidateForScene(Scene scene)
        {
            if (_signRenamer == null || !_signRenamer.gameObject.scene.IsValid()
                || _signRenamer.gameObject.scene.handle == scene.handle)
            {
                _signRenamer = null;
            }
        }

        /// <summary>Active-only lookup used by the naming side-effect replay.</summary>
        internal static ShopRenamer[] FindRenamers()
        {
            var renamer = SceneRef<ShopRenamer>.Get();
            return renamer == null ? new ShopRenamer[0] : new[] { renamer };
        }

        /// <summary>Repaint lookup for the shop sign. The renamer controller is routinely
        /// inactive while its sign stays visible, so this search includes inactive objects.</summary>
        internal static ShopRenamer[] FindSignRenamers()
        {
            if (_signRenamer == null)
            {
                _signRenamer = FindSceneObject<ShopRenamer>();
                if (_signRenamer == null)
                {
                    CoopPlugin.Log.LogDebug("Shop renamer is not present in the scene.");
                }
            }

            return _signRenamer == null ? new ShopRenamer[0] : new[] { _signRenamer };
        }

        /// <summary>Find a scene component even while its GameObject is inactive. Prefab assets
        /// returned by the resource search are rejected by the valid-scene check.</summary>
        private static T FindSceneObject<T>() where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.scene.IsValid())
                {
                    return all[i];
                }
            }

            return null;
        }

        internal static ShopRenamer FindRenamer()
        {
            var renamers = FindRenamers();
            return renamers.Length == 0 ? null : renamers[0];
        }

        internal static bool ApplyRenamerText(ShopRenamer renamer, string name, bool paintSign = true)
        {
            var applied = false;
            if (paintSign && renamer.m_ShopName != null)
            {
                renamer.m_ShopName.text = name;
                applied = true;
            }
            if (renamer.m_SetName != null)
            {
                renamer.m_SetName.text = name;
                applied = true;
            }
            if (renamer.m_SetNameInputDisplay != null)
            {
                renamer.m_SetNameInputDisplay.text = name;
                applied = true;
            }
            if (renamer.m_SetNameInput != null)
            {
                // TMP_InputField.text invokes onValueChanged; the rename UI wires that to
                // ShopRenamer.OnInputChanged, which paints m_ShopName. A baseline that must not
                // paint the sign therefore sets the field without notifying.
                if (paintSign)
                {
                    renamer.m_SetNameInput.text = name;
                }
                else
                {
                    renamer.m_SetNameInput.SetTextWithoutNotify(name);
                }
                applied = true;
            }
            return applied;
        }

        /// <summary>True while this renamer is still the tutorial's first-shop naming step. A
        /// blocked tutorial auto-open uses this to decide whether it still needs to retry.</summary>
        internal static bool IsTutorialRenamer(ShopRenamer renamer)
            => renamer != null && Convert.ToBoolean(FiIsTutorial.GetValue(renamer));

        /// <summary>The screens are separate objects from the controller, which is routinely
        /// inactive during normal play. Test the screens themselves so callers can tell whether
        /// the rename UI is actually open.</summary>
        internal static bool HasOpenRenameScreen(ShopRenamer renamer)
        {
            if (renamer == null)
            {
                return false;
            }

            return (renamer.m_IntroScreen != null && renamer.m_IntroScreen.activeSelf)
                || (renamer.m_NameScreen != null && renamer.m_NameScreen.activeSelf)
                || (renamer.m_ConfirmNameScreen != null && renamer.m_ConfirmNameScreen.activeSelf);
        }

        /// <summary>True when any scene renamer has one of its screens open, including a UI that
        /// was already open before this session's behaviour started.</summary>
        internal static bool HasAnyOpenRenameScreen()
        {
            var renamers = FindSignRenamers();
            for (var i = 0; i < renamers.Length; i++)
            {
                if (HasOpenRenameScreen(renamers[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The scene renamer still waiting for the tutorial's first shop name, including
        /// the routinely inactive controller whose screen objects are separate.</summary>
        internal static ShopRenamer FindTutorialRenamer()
        {
            var renamers = FindSignRenamers();
            for (var i = 0; i < renamers.Length; i++)
            {
                if (IsTutorialRenamer(renamers[i]))
                {
                    return renamers[i];
                }
            }

            return null;
        }

        /// <summary>Paints an unconfirmed editor draft. The input field is deliberately untouched:
        /// only the editor's local vanilla path writes it, so remote echoes cannot move the
        /// editor's caret.</summary>
        internal static bool ApplyPreviewText(ShopRenamer renamer, string name)
        {
            var applied = false;
            if (renamer.m_ShopName != null)
            {
                renamer.m_ShopName.text = name;
                applied = true;
            }
            if (renamer.m_SetName != null)
            {
                renamer.m_SetName.text = name;
                applied = true;
            }
            if (renamer.m_SetNameInputDisplay != null)
            {
                renamer.m_SetNameInputDisplay.text = name;
                applied = true;
            }
            return applied;
        }

        /// <summary>Close the client UI without running host-owned tutorial and entitlement code.
        /// Only touches UI modes when one of this renamer's screens was actually open, so a
        /// rename broadcast cannot yank a player out of an unrelated screen.</summary>
        internal static void CloseRenameUi(ShopRenamer renamer)
        {
            if (renamer == null)
            {
                return;
            }

            var hadIntro = renamer.m_IntroScreen != null && renamer.m_IntroScreen.activeSelf;
            var hadName = renamer.m_NameScreen != null && renamer.m_NameScreen.activeSelf;
            var hadConfirm = renamer.m_ConfirmNameScreen != null
                && renamer.m_ConfirmNameScreen.activeSelf;

            renamer.m_IntroScreen?.SetActive(false);
            renamer.m_NameScreen?.SetActive(false);
            renamer.m_ConfirmNameScreen?.SetActive(false);
            renamer.gameObject.SetActive(false);
            if (!hadIntro && !hadName && !hadConfirm)
            {
                return;
            }

            if (hadConfirm)
            {
                ControllerScreenUIExtManager.OnCloseScreen(
                    renamer.m_ControllerScreenUIExtension_ConfirmNameScreen);
            }
            if (hadName)
            {
                ControllerScreenUIExtManager.OnCloseScreen(
                    renamer.m_ControllerScreenUIExtension_NameScreen);
            }
            if (hadIntro)
            {
                ControllerScreenUIExtManager.OnCloseScreen(
                    renamer.m_ControllerScreenUIExtension_IntroScreen);
            }

            SceneRef<InteractionPlayerController>.Get()?.ExitUIMode();
            SceneRef<InteractionPlayerController>.Get()?.ExitLockMoveMode();
            GameUIScreen.SetGameUIVisible(true);
        }

        /// <summary>Replays the side effects that vanilla performs when the tutorial shop is
        /// named. The event is resolved and constructed without a hard reference so a build that
        /// moves or omits it still loads and falls back cleanly.</summary>
        internal static bool ApplyRemoteInitialNamingSideEffects()
        {
            var renamers = FindRenamers();
            var tutorialManager = SceneRef<TutorialManager>.Get();
            if (renamers.Length == 0 || tutorialManager == null)
            {
                return false;
            }

            var wasTutorial = Convert.ToInt32(FiTutorialIndex.GetValue(null)) == 0;
            for (var i = 0; i < renamers.Length; i++)
            {
                var renamer = renamers[i];
                if (renamer != null && Convert.ToBoolean(FiIsTutorial.GetValue(renamer)))
                {
                    wasTutorial = true;
                }
            }

            for (var i = 0; i < renamers.Length; i++)
            {
                CloseRenameUi(renamers[i]);
            }

            if (!wasTutorial)
            {
                return true;
            }

            for (var i = 0; i < renamers.Length; i++)
            {
                if (renamers[i] != null)
                {
                    FiIsTutorial.SetValue(renamers[i], false);
                }
            }
            // SetIsTutorial adds movement hints that vanilla only removes in OnTriggerEnter, which
            // runs for the player who walks in to name the shop. The Tutorial module's intro
            // cleanup keys off the tutorial index leaving 0, which is set here first. Rebuild the
            // tooltip bar from the player's current state, as vanilla does after the card album or
            // pack opening, so the hints go without naming them here.
            InteractionPlayerController.RestoreHiddenToolTip();
            FiTutorialIndex.SetValue(null, 1);
            MiEvaluateTaskVisibility.Invoke(tutorialManager, null);
            var indicator = FiTutorialIndicator.GetValue(tutorialManager) as GameObject;
            indicator?.SetActive(false);
            QueueGrantEntitlementsBonus();
            return true;
        }

        private static void QueueGrantEntitlementsBonus()
        {
            if (!_grantBonusResolved)
            {
                _grantBonusType = ReflectionSurface.OptionalType(
                    "CEventPlayer_GrantEntitlementsBonus", "Assembly-CSharp");
                _grantBonusConstructor = _grantBonusType?.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(bool) }, null);
                _grantBonusResolved = true;
            }

            if (_grantBonusConstructor == null)
            {
                return;
            }

            var evt = _grantBonusConstructor.Invoke(new object[] { true }) as CEvent;
            if (evt != null)
            {
                CEventManager.QueueEvent(evt);
            }
        }
    }
}
