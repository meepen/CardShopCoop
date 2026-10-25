using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    internal sealed class CoopButton
    {
        internal GameObject Go;
        internal RectTransform Rt;
        internal Button Btn;
        internal TextMeshProUGUI Label;
        internal ControllerButton Ctrl;
        private string _text;

        internal void SetLabel(string text)
        {
            if (Label != null && _text != text)
            {
                _text = text;
                Label.text = text;
            }
        }

        internal void Show(bool on)
        {
            if (Go != null && Go.activeSelf != on)
            {
                Go.SetActive(on);
            }
        }

        internal void SetStyle(Tint tint)
        {
            if (Rt != null)
            {
                NativeCoopUi.Paint(Rt, tint);
            }
        }

        internal void SetInteractable(bool on)
        {
            if (Btn != null)
            {
                Btn.interactable = on;
            }
        }
    }
}
