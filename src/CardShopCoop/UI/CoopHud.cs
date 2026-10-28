using CardShopCoop.Modules.Hud;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Native (UGUI/TMP) HUD pills. They show the session-wide notices that belong outside the
    /// co-op screen: the error line, the guest's host clock, toasts, and the end-of-day
    /// wait/countdown. The full co-op screen covers them while open, so the HUD is deliberately
    /// hidden then.
    /// </summary>
    internal static class CoopHud
    {
        private const int SortingOrder = 30005;
        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;

        private sealed class Pill
        {
            internal GameObject Go;
            internal TextMeshProUGUI Text;

            internal void Show(string value, bool visible)
            {
                if (Go == null)
                {
                    return;
                }

                var on = visible && !string.IsNullOrEmpty(value);
                if (Go.activeSelf != on)
                {
                    Go.SetActive(on);
                }
                if (on)
                {
                    NativeCoopUi.Set(Text, value);
                }
            }
        }

        private static GameObject _root;
        private static Pill _error;
        private static Pill _hostTime;
        private static Pill _toast;
        private static Pill _wait;
        private static Sprite _white;
        private static bool _failed;

        internal static void Tick()
        {
            if (_failed || CoopScreen.IsOpen)
            {
                Hide();
                return;
            }

            var core = CoopCore.Instance;
            if (core == null)
            {
                Hide();
                return;
            }

            var error = core.ErrorLine ?? "";
            var hostTime = CoopCore.Role == CoopRole.Client ? HudApi.HostTimeText : "";
            var toast = HudApi.ToastText ?? "";
            var wait = "";
            if (HudApi.NextDayWaitActive && HudApi.NextDayWaitText.Length > 0)
            {
                wait = HudApi.NextDayWaitText;
                if (HudApi.EndOfDayPendingActive && HudApi.EndOfDayPendingText.Length > 0)
                {
                    wait += "  " + HudApi.EndOfDayPendingText;
                }
            }
            else if (HudApi.EndOfDayPendingActive && HudApi.EndOfDayPendingText.Length > 0)
            {
                wait = HudApi.EndOfDayPendingText;
            }

            if (error.Length == 0 && hostTime.Length == 0 && toast.Length == 0
                && wait.Length == 0)
            {
                Hide();
                return;
            }

            if (!EnsureBuilt())
            {
                return;
            }

            _root.SetActive(true);
            _error.Show(error, true);
            _hostTime.Show(hostTime, true);
            _toast.Show(toast, true);
            _wait.Show(wait, true);
        }

        internal static void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            _root = null;
            _error = null;
            _hostTime = null;
            _toast = null;
            _wait = null;
            if (_white != null)
            {
                Object.Destroy(_white);
            }
            _white = null;
            _failed = false;
        }

        private static void Hide()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        private static bool EnsureBuilt()
        {
            if (_root != null)
            {
                return true;
            }

            try
            {
                _root = new GameObject("CardShopCoop_Hud", typeof(RectTransform), typeof(Canvas),
                    typeof(CanvasScaler));
                Object.DontDestroyOnLoad(_root);
                var canvas = _root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
                var scaler = _root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 1f;

                _error = BuildPill("Error", NativeCoopUi.Bad, TextAlignmentOptions.Left,
                    new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(8f, 8f),
                    new Vector2(900f, 68f), 20f, 8f);
                _hostTime = BuildPill("HostTime", NativeCoopUi.Gold, TextAlignmentOptions.Center,
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -4f),
                    new Vector2(520f, 36f), 20f, 4f);
                _toast = BuildPill("Toast", NativeCoopUi.Good, TextAlignmentOptions.Center,
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -680f),
                    new Vector2(1100f, 46f), 20f, 4f);
                _wait = BuildPill("Wait", NativeCoopUi.Gold, TextAlignmentOptions.Center,
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -752f),
                    new Vector2(1400f, 58f), 20f, 4f);
                _root.SetActive(false);
                return true;
            }
            catch (System.Exception error)
            {
                _failed = true;
                CoopPlugin.Log?.LogError("coop: native HUD could not be built: " + error);
                if (_root != null)
                {
                    Object.Destroy(_root);
                }
                _root = null;
                _error = null;
                _hostTime = null;
                _toast = null;
                _wait = null;
                return false;
            }
        }

        private static Pill BuildPill(string name, Color textColor, TextAlignmentOptions align,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size,
            float fontSize, float padding)
        {
            var go = new GameObject("CardShopCoop_Hud" + name, typeof(RectTransform),
                typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_root.transform, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = anchorMin;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            var background = go.GetComponent<Image>();
            background.sprite = WhiteSprite();
            background.type = Image.Type.Simple;
            background.color = new Color(0.08f, 0.06f, 0.1f, 0.82f);
            background.raycastTarget = false;

            var text = NativeCoopUi.Text(go.transform, "", fontSize, padding, padding,
                Mathf.Max(1f, size.x - padding * 2f), Mathf.Max(1f, size.y - padding * 2f),
                textColor, align, wrap: true, title: false);
            return new Pill { Go = go, Text = text };
        }

        private static Sprite WhiteSprite()
        {
            if (_white != null)
            {
                return _white;
            }

            var texture = Texture2D.whiteTexture;
            _white = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
            _white.name = "CardShopCoop_HudWhite";
            return _white;
        }
    }
}
