namespace CardShopCoop.Modules.Shop
{
    internal static class ShopState
    {
        internal const int MaxNameLength = 64;
        private static string _canonicalName;
        private static bool _unnamed;

        internal static bool Unnamed => _unnamed;

        internal static void Reset()
        {
            _canonicalName = null;
            _unnamed = false;
            ShopRenameSession.Reset();
            ShopInterop.Reset();
        }

        /// <summary>Host-authoritative "the shop has no name yet". While true, repainting the
        /// canonical name must not put the "My Card Shop" placeholder on the blank tutorial
        /// sign.</summary>
        internal static void SetUnnamed(bool unnamed) => _unnamed = unnamed;

        /// <summary>Clamps live editor text to the same ceiling the committed name uses. Drafts
        /// are display-only, so empty and partially typed values are valid; only the length is
        /// bounded here to keep a peer from painting an unbounded sign.</summary>
        internal static string NormalizeDraft(string text)
        {
            text ??= "";
            return text.Length > MaxNameLength ? text.Substring(0, MaxNameLength) : text;
        }

        /// <summary>Puts the last committed name back on every sign surface without consulting
        /// <c>CPlayerData.PlayerName</c>, which may still hold the local editor's draft. An
        /// unnamed shop keeps its blank tutorial sign instead of showing the placeholder.</summary>
        internal static void RepaintCanonical()
        {
            if (_unnamed)
            {
                // The tutorial sign is blank, but a just-cancelled draft may still be painted
                // on it. Clear the presentation without writing the placeholder to the sign.
                var renamers = ShopInterop.FindSignRenamers();
                for (var i = 0; i < renamers.Length; i++)
                {
                    if (renamers[i] != null)
                    {
                        ShopInterop.ApplyPreviewText(renamers[i], "");
                    }
                }
                return;
            }

            if (TryNormalize(_canonicalName, out var name))
            {
                Apply(name);
            }
        }

        /// <summary>Reads only the committed cache - never <c>CPlayerData.PlayerName</c>, which is
        /// the live draft while the local player is editing.</summary>
        internal static bool TryGetCanonical(out string name)
            => TryNormalize(_canonicalName, out name);

        /// <summary>True when the committed cache still holds this exact name. A rename rollback
        /// uses it to avoid restoring an old snapshot over a newer authoritative rename.</summary>
        internal static bool IsCanonical(string name)
            => TryNormalize(_canonicalName, out var current) && current == name;

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

        /// <summary>Applies the canonical name. <paramref name="paintSign"/> false leaves the shop
        /// sign itself untouched, as vanilla does for an unnamed shop.</summary>
        internal static bool Apply(string name, bool paintSign = true)
        {
            _canonicalName = name;
            _unnamed = !paintSign;
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

                appliedUi |= ShopInterop.ApplyRenamerText(renamer, name, paintSign);
                if (!paintSign)
                {
                    // An unnamed baseline must also clear a draft painted by an optimistic
                    // rename, not merely avoid painting the placeholder over it.
                    ShopInterop.ApplyPreviewText(renamer, "");
                }
            }

            // CPlayerData is authoritative even while the scene is rebuilding. The caller uses
            // the UI result to retain the newest state until the sign/input surface exists.
            return appliedUi;
        }
    }
}
