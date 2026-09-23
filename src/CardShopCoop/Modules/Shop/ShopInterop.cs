using System;
using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>Scene lookup boundary for both the Unity 2021 and Unity 6 runtimes.</summary>
    internal static class ShopInterop
    {
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

        internal static ShopRenamer[] FindRenamers()
        {
            var renamer = SceneRef<ShopRenamer>.Get();
            return renamer == null ? new ShopRenamer[0] : new[] { renamer };
        }

        internal static ShopRenamer FindRenamer()
        {
            var renamers = FindRenamers();
            return renamers.Length == 0 ? null : renamers[0];
        }

        internal static bool ApplyRenamerText(ShopRenamer renamer, string name)
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
            if (renamer.m_SetNameInput != null)
            {
                renamer.m_SetNameInput.text = name;
                applied = true;
            }
            return applied;
        }

        /// <summary>Close the client UI without running host-owned tutorial and entitlement code.</summary>
        internal static void CloseRenameUi(ShopRenamer renamer)
        {
            if (renamer == null)
            {
                return;
            }

            renamer.m_IntroScreen?.SetActive(false);
            renamer.m_NameScreen?.SetActive(false);
            renamer.m_ConfirmNameScreen?.SetActive(false);
            renamer.gameObject.SetActive(false);
            SceneRef<InteractionPlayerController>.Get()?.ExitUIMode();
            SceneRef<InteractionPlayerController>.Get()?.ExitLockMoveMode();
            GameUIScreen.SetGameUIVisible(true);
            ControllerScreenUIExtManager.OnCloseScreen(renamer.m_ControllerScreenUIExtension_ConfirmNameScreen);
        }

        /// <summary>Replays the side effects that vanilla performs when the tutorial shop is
        /// named. The event type was added only by the Unity 6 build, so it is deliberately
        /// resolved and constructed without a hard reference to keep the legacy DLL loadable.</summary>
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
