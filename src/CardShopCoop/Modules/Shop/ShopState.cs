namespace CardShopCoop.Modules.Shop
{
    internal static class ShopState
    {
        internal const int MaxNameLength = 64;
        private static string _canonicalName;

        internal static void Reset()
        {
            _canonicalName = null;
            ShopInterop.Reset();
        }

        internal static bool TryNormalize(string proposed, out string canonical)
        {
            canonical = proposed?.Trim();
            if (string.IsNullOrWhiteSpace(canonical) || canonical.Length > MaxNameLength)
            {
                canonical = null;
                return false;
            }

            for (var i = 0; i < canonical.Length; i++)
            {
                if (char.IsControl(canonical[i]))
                {
                    canonical = null;
                    return false;
                }
            }

            return true;
        }

        internal static bool TryGet(out string name)
        {
            if (TryNormalize(_canonicalName, out name))
            {
                return true;
            }

            if (!TryNormalize(CPlayerData.GetPlayerName(), out name))
            {
                name = null;
                return false;
            }

            _canonicalName = name;
            return true;
        }

        internal static bool Apply(string name)
        {
            _canonicalName = name;
            CPlayerData.PlayerName = name;
            // The renamer controller is inactive during normal play, but its text fields point at
            // the visible shop sign (and the rename inputs). Repaint through the scene instance so
            // a remote rename lands on the sign without waiting for the billboard to be opened.
            var renamers = ShopInterop.FindSignRenamers();
            var appliedUi = false;
            for (var i = 0; i < renamers.Length; i++)
            {
                var renamer = renamers[i];
                if (renamer == null)
                {
                    continue;
                }

                appliedUi |= ShopInterop.ApplyRenamerText(renamer, name);
            }

            // CPlayerData is authoritative even while the scene is rebuilding. The caller uses
            // the UI result to retain the newest state until the sign/input surface exists.
            return appliedUi;
        }
    }
}
