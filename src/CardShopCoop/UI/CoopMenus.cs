using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using CardShopCoop.Api;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace CardShopCoop.UI
{
    internal static class CoopMenus
    {
        private struct Moved
        {
            internal RectTransform Rt;

            internal Vector2 Pos;

            internal Vector3 Scale;
        }

        private sealed class Slot
        {
            internal float Y;

            internal readonly List<RectTransform> Items = new List<RectTransform>();
        }

        private static TitleScreen _title;

        private static CoopButton _titleBtn;

        private static PauseScreen _pause;

        private static CoopButton _pauseBtn;

        private static readonly List<Moved> Restore = new List<Moved>();

        private static readonly List<KeyValuePair<ControllerScreenUIExtension, ControllerBtnList>> AddedRows = new List<KeyValuePair<ControllerScreenUIExtension, ControllerBtnList>>();

        private static readonly FieldInfo UiModeField = AccessTools.Field(typeof(InteractionPlayerController), "m_IsInUIMode");

        private static readonly FieldInfo PhoneModeField = AccessTools.Field(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");

        private static readonly FieldInfo DecoModeField = AccessTools.Field(typeof(InteractionPlayerController), "m_IsDecoScreenMode");

        private const float DefaultMenuBottom = -490f;

        private static Harmony _harmony;

        internal static bool TitleReady
        {
            get
            {
                if (_title != null && _titleBtn != null)
                {
                    return _titleBtn.Go != null;
                }
                return false;
            }
        }

        internal static PauseScreen Pause => _pause;

        internal static bool PauseOpen
        {
            get
            {
                PauseScreen val = _pause;
                if (val == null)
                {
                    val = Live<PauseScreen>.Get();
                }
                if (val != null && val.m_ScreenGrp != null)
                {
                    return val.m_ScreenGrp.activeSelf;
                }
                return false;
            }
        }

        internal static bool AtTitle
        {
            get
            {
                var instance = SceneRef<CGameManager>.Get();
                if (instance != null)
                {
                    return !instance.m_IsGameLevel;
                }
                return true;
            }
        }

        internal static bool InShop
        {
            get
            {
                var instance = SceneRef<CGameManager>.Get();
                if (instance != null)
                {
                    return instance.m_IsGameLevel;
                }
                return false;
            }
        }

        internal static void Init()
        {
            if (_harmony == null)
            {
                _harmony = new Harmony("dev.meepen.cardshopcoop.menus");
                Patch(_harmony, typeof(TitleScreen), "Start", "TitleStartPrefix",
                    "TitleStartPostfix");
                Patch(_harmony, typeof(ControllerScreenUIExtManager), "OnOpenScreen", null,
                    "ControllerScreenOpenPostfix");
                Patch(_harmony, typeof(PauseScreen), "OpenScreen", null, "PauseOpenPostfix");
                Patch(_harmony, typeof(PauseScreen), "CloseScreen", "PauseClosePrefix", null);
            }
        }

        private static void Patch(Harmony h, Type t, string name, string prefix, string postfix)
        {
            MethodInfo methodInfo = AccessTools.Method(t, name);
            if (methodInfo == null)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: method " + t.Name + "." + name + " not found");
                }
                return;
            }
            try
            {
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(CoopMenus), prefix);
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(CoopMenus), postfix);
                h.Patch(methodInfo, pre, post);
            }
            catch (Exception ex)
            {
                ManualLogSource log2 = CoopPlugin.Log;
                if (log2 != null)
                {
                    log2.LogWarning("coop: could not hook " + t.Name + "." + name + ": " + ex.Message);
                }
            }
        }

        private static void TitleStartPrefix(TitleScreen __instance) => _title = __instance;

        private static void TitleStartPostfix(TitleScreen __instance)
            => InjectTitleGuarded(__instance);

        /// <summary>The title screen opens its controller screen at the end of Start. That is the
        /// event that makes its buttons addressable, so this is where the co-op button is injected
        /// on builds where Start's postfix runs before the controller layout is ready.</summary>
        private static void ControllerScreenOpenPostfix(ControllerScreenUIExtension extension)
        {
            if (extension == null)
            {
                return;
            }

            var title = extension.GetComponentInParent<TitleScreen>() ?? _title;
            if (title == null)
            {
                return;
            }

            _title = title;
            InjectTitleGuarded(title);
        }

        private static void InjectTitleGuarded(TitleScreen title)
        {
            if (title == null)
            {
                return;
            }

            _title = title;
            try
            {
                InjectTitle(title);
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.menus.title-button",
                    "coop: could not add the title-screen co-op button: " + ex);
            }
        }

        private static void PauseOpenPostfix()
        {
            try
            {
                PauseScreen val = Live<PauseScreen>.Get();
                if (val != null)
                {
                    if (_pause != val || _pauseBtn == null || _pauseBtn.Go == null)
                    {
                        InjectPause(val);
                    }
                    if (_pauseBtn != null)
                    {
                        _pauseBtn.SetLabel(PauseLabel());
                    }
                }
            }
            catch (Exception ex)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: pause button: " + ex);
                }
            }
        }

        private static bool PauseClosePrefix()
        {
            if (CoopScreen.IsOpen)
            {
                CoopScreen.Close();
            }
            return true;
        }

        internal static void Shutdown()
        {
            foreach (KeyValuePair<ControllerScreenUIExtension, ControllerBtnList> kv in AddedRows)
            {
                if (kv.Key == null || kv.Key.m_ControllerBtnColumnList == null)
                {
                    continue;
                }
                int num = kv.Key.m_ControllerBtnColumnList.FindIndex(r => r.rowList == kv.Value.rowList);
                if (num >= 0)
                {
                    kv.Key.m_ControllerBtnColumnList.RemoveAt(num);
                    if (kv.Key.m_CurrentCtrlBtnYIndex >= num && kv.Key.m_CurrentCtrlBtnYIndex > 0)
                    {
                        ControllerScreenUIExtension key = kv.Key;
                        key.m_CurrentCtrlBtnYIndex--;
                    }
                }
            }
            AddedRows.Clear();
            foreach (Moved item in Restore)
            {
                if (item.Rt != null)
                {
                    item.Rt.anchoredPosition = item.Pos;
                    item.Rt.localScale = item.Scale;
                }
            }
            Restore.Clear();
            if (_titleBtn != null && _titleBtn.Go != null)
            {
                UnityEngine.Object.Destroy(_titleBtn.Go);
            }
            if (_pauseBtn != null && _pauseBtn.Go != null)
            {
                UnityEngine.Object.Destroy(_pauseBtn.Go);
            }
            _titleBtn = null;
            _pauseBtn = null;
            _title = null;
            _pause = null;
        }

        internal static void EnsurePause()
        {
            if (_pauseBtn == null || _pauseBtn.Go == null)
            {
                PauseOpenPostfix();
            }
        }

        internal static bool CanOpenPause()
        {
            if (Live<PauseScreen>.Get() == null)
            {
                return false;
            }
            InteractionPlayerController val = Live<InteractionPlayerController>.Get();
            if (val == null)
            {
                return false;
            }
            if (Flag(UiModeField, val) || Flag(PhoneModeField, val) || Flag(DecoModeField, val))
            {
                return false;
            }
            SettingScreen val2 = Live<SettingScreen>.Get();
            if (val2 != null && val2.m_ScreenGrp != null && val2.m_ScreenGrp.activeSelf)
            {
                return false;
            }
            return true;
        }

        private static bool Flag(FieldInfo f, object o)
        {
            try
            {
                return f != null && (bool)f.GetValue(o);
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.menus.flag." + (f?.Name ?? "unknown"),
                    "coop: could not read UI field " + (f?.Name ?? "<null>") + ": " + ex.Message);
                return false;
            }
        }

        internal static string PauseLabel()
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                return "Hosting Co-op";
            }
            if (CoopCore.Role == CoopRole.Client)
            {
                return "Co-op Session";
            }
            return "Host Co-op";
        }

        private static Transform ButtonRoot(Transform from, Transform stop)
        {
            Transform result = null;
            Transform val = from;
            while (val != null && val != stop)
            {
                if (val.GetComponent<ControllerButton>() != null)
                {
                    result = val;
                }
                val = val.parent;
            }
            return result;
        }

        private static void InjectTitle(TitleScreen ts)
        {
            if (ts == null || (_title == ts && _titleBtn != null && _titleBtn.Go != null))
            {
                return;
            }
            ControllerScreenUIExtension controllerScreenUIExtension = ts.m_ControllerScreenUIExtension;
            Transform anchor = null;
            if (ts.m_LoadGameButton != null)
            {
                anchor = ButtonRoot(ts.m_LoadGameButton.transform, ts.transform);
            }
            if (anchor == null && controllerScreenUIExtension != null && controllerScreenUIExtension.m_StartControllerButton != null)
            {
                anchor = controllerScreenUIExtension.m_StartControllerButton.transform;
            }
            if (anchor == null || anchor.parent == null)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: title screen buttons not found; use F10 to join.");
                }
                return;
            }
            NativeCoopUi.CaptureTemplate(anchor.gameObject);
            bool was = NativeCoopUi.MuteTmp();
            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(anchor.gameObject, anchor.parent, false);
            }
            finally
            {
                NativeCoopUi.UnmuteTmp(was);
            }
            clone.name = "NativeCoop_JoinBtn";
            clone.SetActive(true);
            CoopButton coopButton = NativeCoopUi.Restyle(clone, "Join Co-op", NativeCoopUi.Purple, OnTitleJoin);
            clone.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            Stack(anchor.parent, (RectTransform)anchor, (RectTransform)clone.transform, 0f);
            AddNavRow(controllerScreenUIExtension, anchor.GetComponent<ControllerButton>(), coopButton.Ctrl);
            _title = ts;
            _titleBtn = coopButton;
            ManualLogSource log2 = CoopPlugin.Log;
            if (log2 != null)
            {
                log2.LogInfo("coop: Join Co-op added to the title screen");
            }
        }

        private static void InjectPause(PauseScreen p)
        {
            if (p == null || p.m_ScreenGrp == null)
            {
                return;
            }
            ControllerScreenUIExtension controllerScreenUIExtension = p.m_ControllerScreenUIExtension;
            Transform anchor = (controllerScreenUIExtension != null && controllerScreenUIExtension.m_StartControllerButton != null)
                ? controllerScreenUIExtension.m_StartControllerButton.transform
                : null;
            if (anchor == null)
            {
                anchor = p.m_ScreenGrp.transform.Find("AnimGrp/ResumeBtn");
            }
            if (anchor == null || anchor.parent == null)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: pause menu buttons not found; use F10 for co-op.");
                }
                _pause = p;
                return;
            }
            NativeCoopUi.CaptureTemplate(anchor.gameObject);
            bool was = NativeCoopUi.MuteTmp();
            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(anchor.gameObject, anchor.parent, false);
            }
            finally
            {
                NativeCoopUi.UnmuteTmp(was);
            }
            clone.name = "NativeCoop_HostBtn";
            clone.SetActive(true);
            CoopButton coopButton = NativeCoopUi.Restyle(clone, PauseLabel(), NativeCoopUi.Purple, OnPauseHost);
            clone.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            Stack(anchor.parent, (RectTransform)anchor, (RectTransform)clone.transform, 40f);
            AddNavRow(controllerScreenUIExtension, anchor.GetComponent<ControllerButton>(), coopButton.Ctrl);
            _pause = p;
            _pauseBtn = coopButton;
            ManualLogSource log2 = CoopPlugin.Log;
            if (log2 != null)
            {
                log2.LogInfo("coop: Host Co-op added to the pause menu");
            }
        }

        private static void OnTitleJoin()
        {
            CoopScreen.Open(quiet: false);
        }

        private static void OnPauseHost()
        {
            CoopScreen.Open(quiet: false);
        }

        private static void AddNavRow(ControllerScreenUIExtension ext, ControllerButton anchor, ControllerButton added)
        {
            if (ext == null || ext.m_ControllerBtnColumnList == null || anchor == null || added == null)
            {
                return;
            }
            for (int i = 0; i < ext.m_ControllerBtnColumnList.Count; i++)
            {
                List<ControllerButton> rowList = ext.m_ControllerBtnColumnList[i].rowList;
                if (rowList == null)
                {
                    continue;
                }
                int num = rowList.IndexOf(anchor);
                if (num >= 0)
                {
                    List<ControllerButton> list = new List<ControllerButton>(rowList);
                    list[num] = added;
                    ControllerBtnList val = new ControllerBtnList
                    {
                        rowList = list
                    };
                    ext.m_ControllerBtnColumnList.Insert(i + 1, val);
                    AddedRows.Add(new KeyValuePair<ControllerScreenUIExtension, ControllerBtnList>(ext, val));
                    if (ext.m_CtrlBtnXChangeMethodList != null && ext.m_CtrlBtnXChangeMethodList.Count > i + 1)
                    {
                        ext.m_CtrlBtnXChangeMethodList.Insert(i + 1, ext.m_CtrlBtnXChangeMethodList[i]);
                    }
                    if (ext.m_CurrentCtrlBtnYIndex > i)
                    {
                        ext.m_CurrentCtrlBtnYIndex++;
                    }
                    if (ext.m_LastChangeMethodCtrlBtnXIndex < 0)
                    {
                        ext.m_LastChangeMethodCtrlBtnXIndex = 0;
                    }
                    return;
                }
            }
            ManualLogSource log = CoopPlugin.Log;
            if (log != null)
            {
                log.LogWarning("coop: controller row for " + anchor.name + " not found; the new button is mouse only");
            }
        }

        private static void Stack(Transform parent, RectTransform anchor, RectTransform added, float upAllowance)
        {
            Restore.RemoveAll(m => m.Rt == null);
            AddedRows.RemoveAll(r => r.Key == null);
            float x = anchor.anchoredPosition.x;
            Vector2 center = new Vector2(0.5f, 0.5f);
            List<Slot> list = new List<Slot>();
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                RectTransform rt = child as RectTransform;
                if (rt == null || rt == added || rt.anchorMin != center || rt.anchorMax != center || Mathf.Abs(rt.anchoredPosition.x - x) > 1f || rt.GetComponent<ControllerButton>() == null)
                {
                    continue;
                }
                Slot slot = null;
                foreach (Slot item in list)
                {
                    if (Mathf.Abs(item.Y - rt.anchoredPosition.y) < 1f)
                    {
                        slot = item;
                        break;
                    }
                }
                if (slot == null)
                {
                    slot = new Slot
                    {
                        Y = rt.anchoredPosition.y
                    };
                    list.Add(slot);
                }
                slot.Items.Add(rt);
            }
            list.Sort((a, b) => b.Y.CompareTo(a.Y));
            int num = -1;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].Items.Contains(anchor))
                {
                    num = j;
                    break;
                }
            }
            if (num < 0)
            {
                return;
            }
            float num2 = list.Count > 1 ? (list[0].Y - list[list.Count - 1].Y) / (float)(list.Count - 1) : 125f;
            if (num2 < 10f)
            {
                num2 = 125f;
            }
            Slot slot2 = new Slot
            {
                Y = 0f
            };
            slot2.Items.Add(added);
            list.Insert(num + 1, slot2);
            int count = list.Count;
            float num3 = list[0].Y;
            float y = anchor.localScale.y;
            float num4 = VisualHalfHeight(anchor) * y;
            float num5 = BottomOf(parent) + num4 + 12f;
            float num6 = (float)(count - 1) * num2;
            float num7 = 1f;
            if (num3 - num6 < num5)
            {
                num3 = Mathf.Min(num3 + upAllowance, num5 + num6);
                if (num3 - num6 < num5)
                {
                    num7 = (num3 - num5) / num6;
                }
            }
            foreach (Slot item2 in list)
            {
                foreach (RectTransform item3 in item2.Items)
                {
                    if (item3 != added)
                    {
                        Restore.Add(new Moved
                        {
                            Rt = item3,
                            Pos = item3.anchoredPosition,
                            Scale = item3.localScale
                        });
                    }
                }
            }
            for (int k = 0; k < count; k++)
            {
                float num8 = num3 - (float)k * num2 * num7;
                foreach (RectTransform item4 in list[k].Items)
                {
                    if (item4 == added)
                    {
                        item4.localScale = anchor.localScale;
                    }
                    item4.anchoredPosition = new Vector2(item4.anchoredPosition.x, num8);
                }
            }
            if (num7 < 0.999f)
            {
                foreach (Slot item5 in list)
                {
                    foreach (RectTransform item6 in item5.Items)
                    {
                        item6.localScale = new Vector3(item6.localScale.x * num7, item6.localScale.y * num7, item6.localScale.z);
                    }
                }
            }
            ManualLogSource log = CoopPlugin.Log;
            if (log != null)
            {
                log.LogInfo("coop: menu stack of " + count + " laid out (step " + (num2 * num7).ToString("0.0") + ", size " + (num7 * 100f).ToString("0") + "%)");
            }
        }

        private static float VisualHalfHeight(RectTransform button)
        {
            Transform bgBar = button.Find("AnimGrp/BGBarGrp");
            if (bgBar != null)
            {
                Rect rect = ((RectTransform)bgBar).rect;
                return Mathf.Max(20f, (rect.height - 136f) * 0.5f);
            }
            Rect rect2 = button.rect;
            return rect2.height * 0.4f;
        }

        private static float BottomOf(Transform parent)
        {
            try
            {
                Canvas componentInParent = parent.GetComponentInParent<Canvas>();
                if (componentInParent != null)
                {
                    RectTransform rt = (RectTransform)componentInParent.rootCanvas.transform;
                    Vector3[] array = new Vector3[4];
                    rt.GetWorldCorners(array);
                    float y = parent.InverseTransformPoint(array[0]).y;
                    if (!float.IsNaN(y) && !float.IsInfinity(y) && y < -100f)
                    {
                        return y;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.menus.bottom",
                    "coop: could not measure the menu bottom: " + ex.Message);
            }
            return DefaultMenuBottom;
        }
    }
}
