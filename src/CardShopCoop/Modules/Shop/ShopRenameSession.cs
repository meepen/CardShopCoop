namespace CardShopCoop.Modules.Shop
{
    /// <summary>
    /// The single player allowed to edit the shop name, plus their live draft text. The host owns
    /// this state and broadcasts it; clients mirror it. Only the owner's drafts and confirm are
    /// accepted, which makes the rename UI first-come-first-serve without making the host special.
    ///
    /// A draft is deliberately separate from the canonical name in <see cref="ShopState"/>: it is
    /// painted on every shop sign and input display, but it never updates <c>CPlayerData.PlayerName</c>
    /// on other machines and never survives the lock being released.
    /// </summary>
    internal static class ShopRenameSession
    {
        internal const int NoOwner = -1;

        private static int _owner = NoOwner;
        private static string _ownerName;
        private static string _draft;

        internal static int Owner => _owner;
        internal static string OwnerName => _ownerName;
        internal static string Draft => _draft;
        internal static bool HasOwner => _owner != NoOwner;

        internal static bool IsOwner(int connectionId)
            => connectionId != NoOwner && _owner == connectionId;

        internal static bool IsLocalOwner => IsOwner(CoopCore.LocalConnectionId);

        internal static void Reset() => ClearOwner();

        /// <summary>Clears the lock and preview without touching the sign. Release paths clear
        /// this first, then broadcast and repaint, so a failed repaint cannot leave the lock
        /// held.</summary>
        internal static void ClearOwner()
        {
            _owner = NoOwner;
            _ownerName = null;
            _draft = null;
        }

        /// <summary>Mirrors host-authoritative lock state. Releasing the lock clears the live
        /// preview and puts the canonical name back on every sign.
        ///
        /// <paramref name="repaintCanonical"/> is false for a client whose own rename UI is
        /// open and mid-edit: repainting the input field would wipe the text this player is
        /// still typing. The caller closes or replaces that UI deliberately.</summary>
        internal static void ApplyLock(int owner, string ownerName, bool repaintCanonical = true)
        {
            var previousOwner = _owner;
            _owner = owner;
            _ownerName = ownerName;
            if (owner == NoOwner)
            {
                ClearOwner();
                if (repaintCanonical)
                {
                    ShopState.RepaintCanonical();
                }
                return;
            }

            // A new editor must not inherit the previous editor's live text. A grant to this
            // machine arrives before it has stored a draft, so this does not wipe local typing.
            if (previousOwner != owner && _draft != null)
            {
                _draft = null;
                if (repaintCanonical)
                {
                    ShopState.RepaintCanonical();
                }
            }
        }

        /// <summary>Stores and paints the editor's live text. The shop input field itself is
        /// never written here: the editor owns its caret, and vanilla already painted its own
        /// input for local typing.</summary>
        internal static void ApplyDraft(string text)
        {
            _draft = text ?? "";
            var renamers = ShopInterop.FindSignRenamers();
            for (var i = 0; i < renamers.Length; i++)
            {
                var renamer = renamers[i];
                if (renamer != null)
                {
                    ShopInterop.ApplyPreviewText(renamer, _draft);
                }
            }
        }

        /// <summary>Paints a draft retained while the scene's renamer surface was not ready.</summary>
        internal static void FlushPresentation()
        {
            if (_draft != null)
            {
                ApplyDraft(_draft);
            }
        }

        internal static string BusyEditorName()
        {
            if (!string.IsNullOrWhiteSpace(_ownerName))
            {
                return _ownerName;
            }

            if (_owner == 0)
            {
                return "The host";
            }

            return _owner > 0 ? "Player " + _owner : "Another player";
        }
    }
}
