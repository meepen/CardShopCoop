using CardShopCoop.Api;

namespace CardShopCoop.Modules.Shop
{
    /// <summary>
    /// Presentation of the host-authoritative shop name. The authority is the session-wide
    /// <see cref="ShopRenameState"/>; this class keeps no copy of it. It only projects that value
    /// onto the game's own surfaces (<c>CPlayerData.PlayerName</c>, the shop sign and the rename
    /// inputs), using the game's field as the pre-session fallback.
    /// </summary>
    internal static class ShopState
    {
        internal const int MaxNameLength = 64;

        /// <summary>Host-authoritative "the shop has no name yet". A host publishes it with the
        /// session value; without one there is nothing authoritative to consult, so callers that
        /// paint a sign before a session simply treat the shop as named.</summary>
        internal static bool Unnamed => Global<ShopRenameState>.Value?.Unnamed == true;

        internal static void Reset()
        {
            ShopRenameSession.Reset();
            ShopInterop.Reset();
        }

        /// <summary>Clamps live editor text to the same ceiling the committed name uses. Drafts
        /// are display-only, so empty and partially typed values are valid; only the length is
        /// bounded here to keep a peer from painting an unbounded sign.</summary>
        internal static string NormalizeDraft(string text)
        {
            text ??= "";
            return text.Length > MaxNameLength ? text.Substring(0, MaxNameLength) : text;
        }

        /// <summary>Puts the last committed name back on every sign surface without consulting
        /// <c>CPlayerData.PlayerName</c> as an authority, which may still hold the local editor's
        /// draft. An unnamed shop keeps its blank tutorial sign instead of showing the
        /// placeholder.</summary>
        internal static void RepaintCanonical()
        {
            if (Unnamed)
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

            if (TryGetCanonical(out var name))
            {
                Apply(name);
            }
        }

        /// <summary>The committed name: the host-authoritative session value, or - before one
        /// exists - the game's own field. While a rename UI is open that field holds the live draft
        /// (vanilla ShopRenamer.OnInputChanged writes it), so it is not a name then.</summary>
        internal static bool TryGetCanonical(out string name)
        {
            if (TryNormalize(Global<ShopRenameState>.Value?.Name, out name))
            {
                return true;
            }

            return !ShopInterop.HasAnyOpenRenameScreen()
                && TryNormalize(CPlayerData.GetPlayerName(), out name);
        }

        /// <summary>True when the name this peer currently paints is exactly this name. A rename
        /// rollback uses it to avoid restoring an old snapshot over a newer rename that already
        /// reached the local sign.</summary>
        internal static bool IsPainted(string name)
            => TryNormalize(CPlayerData.GetPlayerName(), out var current) && current == name;

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

        /// <summary>Applies the canonical name. <paramref name="paintSign"/> false leaves the shop
        /// sign itself untouched, as vanilla does for an unnamed shop.</summary>
        internal static bool Apply(string name, bool paintSign = true)
        {
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
