using UnityEngine;

namespace CardShopCoop.UI
{
    internal struct Tint
    {
        internal Color Border;
        internal Color Highlight;
        internal Color Mid;

        internal Tint(Color border, Color highlight, Color mid)
        {
            Border = border;
            Highlight = highlight;
            Mid = mid;
        }
    }
}
