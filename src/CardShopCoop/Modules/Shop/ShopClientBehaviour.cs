using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Shop
{
    [ClientBehaviour]
    public sealed class ShopClientBehaviour : CoopBehaviour, IShopRenameEndpoint
    {
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

        // Local UI lifecycle only. The authoritative name, owner, unnamed flag and live draft
        // are read from Global<ShopRenameState> / Global<ShopRenameDraftState>.
        private bool _localEditing;
        private bool _confirming;
        private bool _pendingTutorialOpen;
        private ShopRenamer _pendingRenamer;
        private bool _pendingShowIntro;
        private bool _tutorialNamingPending;
        private const string RenameScope = "shop.rename";
        private const string RenameLockScope = "shop.rename.lock";

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            ResetLocalState();
            ShopRenamePatches.SetEndpoint(this);
            _harmony = new Harmony("dev.meepen.cardshopcoop.shop.client");
            try
            {
                ShopRenamePatches.Install(_harmony);
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        /// <summary>The host's authoritative rename/lock state. The store owns the baseline and
        /// the wire; this handler projects the changed fields onto the local game state. Because
        /// the value implements <see cref="IActionValue"/>, the framework wraps this handler in
        /// <see cref="ActionLedger"/> so the pending rename or lock action is retired on an
        /// accepted echo or undone before a rejection re-anchor is applied.</summary>
        [GlobalInformationHandler]
        private void HandleState(InformationChange<ShopRenameState> change)
        {
            if (_shutdown)
            {
                return;
            }

            var state = change.Current;
            if (state == null)
            {
                return;
            }

            var previous = change.Previous;
            var nameRelevant = state.Name != null
                && (previous == null || state.IsRename
                    || !string.Equals(previous.Name, state.Name, StringComparison.Ordinal));
            var ownerRelevant = previous == null || state.ActionId != Guid.Empty
                || !Equals(previous.Owner, state.Owner);

            if (nameRelevant)
            {
                ApplyName(state, baseline: previous == null);
            }

            if (ownerRelevant)
            {
                ApplyOwner(state, previous?.Owner?.IsSelf == true);
            }
        }

        /// <summary>The editor's live preview. It is deliberately a separate value: a keystroke
        /// must not apply the canonical name/lock or resolve a rename action. The local editor
        /// already painted its own input, so its echo is ignored by owner identity.</summary>
        [GlobalInformationHandler]
        private void HandleDraft(ShopRenameDraftState draft)
        {
            if (_shutdown)
            {
                return;
            }

            if (draft == null)
            {
                // The canonical lock state also repaints; this clears a preview that outlived
                // its draft value during a release/confirm. Never repaint the input field out
                // from under a local editor; the owner state will close that UI.
                if (!_localEditing)
                {
                    ShopState.RepaintCanonical();
                }
                return;
            }

            if (draft.Owner == null || draft.Owner.IsSelf)
            {
                return;
            }

            PaintDraft(draft.Text);
        }

        private void ApplyName(ShopRenameState state, bool baseline)
        {
            if (state.Name == null)
            {
                return;
            }

            // A baseline, lock update or rejection carries the canonical name with the host's
            // "unnamed" flag; only a committed live rename paints through the unnamed baseline.
            // The store retains the last state, so a late joiner can receive a value whose
            // IsRename flag is set by an earlier rename: the baseline path must still suppress
            // the tutorial side effects.
            var unnamedBaseline = state.Unnamed && (!state.IsRename || baseline);
            var applied = ShopState.Apply(state.Name, paintSign: !unnamedBaseline);
            if (baseline || !state.IsRename || state.Rejected)
            {
                return;
            }

            // The side-effect replay is only the local tutorial naming step. A non-tutorial
            // rename has no side effects to run, and its renamer object is inactive by design.
            if (!NeedsNamingSideEffects())
            {
                _tutorialNamingPending = false;
                return;
            }

            if (!applied || !ShopInterop.ApplyRemoteInitialNamingSideEffects())
            {
                // The sign/renamer surface is not ready yet; retry from the stored state when
                // it is, and do not lose the one-shot tutorial naming side effect.
                _tutorialNamingPending = true;
                return;
            }

            _tutorialNamingPending = false;
        }

        private void ApplyOwner(ShopRenameState state, bool wasLocalOwner)
        {
            var owner = state.Owner;
            if (owner != null && !owner.IsSelf)
            {
                if (_pendingRenamer != null)
                {
                    // Someone else won while our open was deferred. Nothing was opened, so there
                    // is no UI to close; drop the pending open and tell this player why.
                    CancelPendingRenameUi();
                    ShowBusyNotice();
                    return;
                }

                if (!_localEditing)
                {
                    PaintOwnerSurface(owner);
                    return;
                }

                // An already-open UI lost the lock. Close it now; the generic rollback is right
                // behind this authoritative state.
                _localEditing = false;
                CloseRenameUiIfOpen();
                PaintOwnerSurface(owner);
                ShowBusyNotice();
                return;
            }

            if (owner == null)
            {
                if (_localEditing)
                {
                    if (wasLocalOwner)
                    {
                        // We still have the UI open but the host no longer has us as the owner
                        // (for example a forced release). Claim the free lock again rather than
                        // typing into a dead edit session.
                        SendAcquire();
                    }
                    else
                    {
                        AbandonLocalEdit();
                    }
                    return;
                }

                ShopState.RepaintCanonical();
                if (_pendingRenamer == null)
                {
                    TryResumePendingTutorialOpen();
                }
                return;
            }

            // This player owns the lock.
            if (!_localEditing)
            {
                ShopState.RepaintCanonical();
            }

            if (_pendingRenamer != null)
            {
                // The host granted the lock. Now, and only now, run vanilla's open so its
                // ForceLookAt/SetPlayerPos/UI-mode side effects happen exactly once for the
                // player who actually owns the edit.
                OpenPendingRenameUi();
            }

            if (_localEditing)
            {
                SendCurrentDraft();
            }
            else
            {
                // The UI surface disappeared between the request and the grant; do not leave
                // the host holding a lock this player is not using.
                SendRelease();
            }
        }

        private void SendCurrentDraft()
        {
            var renamer = ShopInterop.FindRenamer();
            var text = renamer?.m_SetNameInput != null
                ? renamer.m_SetNameInput.text
                : CPlayerData.GetPlayerName();
            var draft = ShopState.NormalizeDraft(text);
            SendToHost(new ShopRenameDraftIntent { Text = draft });
        }

        private void SendRelease()
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            // Release is a session fact. It must still go out while the game is between scenes,
            // otherwise a confirm/open that crosses a load strands the host's lock forever.
            SendToHost(new ShopRenameLockIntent
            {
                ActionId = Guid.Empty,
                Op = ShopRenameLockOp.Release,
            });
        }

        /// <summary>Sends a non-action message to the host. A plain message has no action ledger to
        /// fall back on, so a missing connection is reported instead of silently dropping the
        /// shop's lock or draft traffic.</summary>
        private void SendToHost(INetMessage message)
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            var host = ((ICoopContext)_context).HostConnection;
            if (host == null)
            {
                CoopPlugin.Log.LogWarning("[shop] dropped " + message.GetType().Name
                    + " because the host connection is missing.");
                return;
            }

            _context.Send(host.Id, message);
        }

        /// <summary>Repaints the name and draft from the store once the scene's renamer surface
        /// exists. This is the only deferred presentation path; it reads no local name copy.</summary>
        private void ApplyStoredState()
        {
            var state = Global<ShopRenameState>.Value;
            if (state?.Name != null)
            {
                // This is a deferred paint of a value that may be the last live rename or the
                // join baseline. Never start the tutorial side effects from the baseline itself;
                // only a pending live rename (recorded in ApplyName) may do that.
                var unnamedBaseline = state.Unnamed && !state.IsRename;
                if (ShopState.Apply(state.Name, paintSign: !unnamedBaseline)
                    && _tutorialNamingPending)
                {
                    if (ShopInterop.ApplyRemoteInitialNamingSideEffects())
                    {
                        _tutorialNamingPending = false;
                    }
                }
            }

            var draft = Global<ShopRenameDraftState>.Value;
            if (draft?.Owner != null && !draft.Owner.IsSelf)
            {
                PaintDraft(draft.Text);
            }
        }

        private static void PaintDraft(string text)
        {
            var renamers = ShopInterop.FindSignRenamers();
            for (var i = 0; i < renamers.Length; i++)
            {
                var renamer = renamers[i];
                if (renamer != null)
                {
                    ShopInterop.ApplyPreviewText(renamer, text ?? "");
                }
            }
        }

        /// <summary>Paints a remote owner's current draft when one exists, otherwise the canonical
        /// name. A join baseline can deliver the lock entry before the draft entry; either order
        /// must leave the sign on the active editor's preview.</summary>
        private static void PaintOwnerSurface(PlayerRef owner)
        {
            var draft = Global<ShopRenameDraftState>.Value;
            if (draft?.Owner != null && owner != null && draft.Owner.Equals(owner))
            {
                PaintDraft(draft.Text);
                return;
            }

            ShopState.RepaintCanonical();
        }

        /// <summary>Records the client's rename proposal the game already performed. Vanilla's
        /// confirm runs in every state, so the action only supplies the replay/undo closures and
        /// the sign is synced directly.</summary>
        private void PredictRenameObserved()
        {
            var (name, previous) = CaptureRename();
            var actionId = ActionLedger.Submit(
                RenameScope,
                new ShopRenameRequestIntent { Name = name },
                () => ShopState.Apply(name),
                () =>
                {
                    // The host may have accepted another rename before this rejection landed.
                    // Only undo while our own optimistic name is still painted; the host's
                    // canonical state message is sent under the rejected action id so it is
                    // applied as authority.
                    if (previous != null && ShopState.IsPainted(name))
                    {
                        ShopState.Apply(previous);
                    }
                });
            if (actionId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "the shop rename intent could not be sent: there is no live host connection.");
            }

            // Vanilla already performed the rename locally; keep the sign in step without
            // double-applying through the action replay.
            ShopState.Apply(name);
        }

        private static (string Name, string Previous) CaptureRename()
        {
            var name = CPlayerData.GetPlayerName();
            // Never use the live player name as the undo snapshot: during an edit it is the
            // draft, so a rejection would re-apply the rejected name. If the baseline has not
            // landed yet, the host's canonical state before the rollback repairs it.
            ShopState.TryGetCanonical(out var previous);
            CoopPlugin.Log.LogDebug("Shop rename proposal sent to host: " + name);
            return (name, previous);
        }

        private void CloseRenameUiIfOpen()
        {
            var renamers = ShopInterop.FindSignRenamers();
            for (var i = 0; i < renamers.Length; i++)
            {
                var renamer = renamers[i];
                if (!ShopInterop.HasOpenRenameScreen(renamer))
                {
                    continue;
                }

                ShopInterop.CloseRenameUi(renamer);
            }
        }

        private void ShowBusyNotice()
        {
            var owner = Global<ShopRenameState>.Value?.Owner;
            var name = string.IsNullOrWhiteSpace(owner?.Name)
                ? (owner == null ? "Another player"
                    : owner.IsHost ? "The host" : "Player " + owner.Id)
                : owner.Name;
            _context?.SetStatusLine?.Invoke(name + " is renaming the shop.", 4f);
        }

        bool IShopRenameEndpoint.TryOpenRenameUi(ShopRenamer renamer, bool showIntro)
        {
            if (_shutdown || _context == null || !_context.InGame())
            {
                return true;
            }

            var owner = Global<ShopRenameState>.Value?.Owner;
            if (owner != null && !owner.IsSelf)
            {
                if (showIntro)
                {
                    _pendingTutorialOpen = true;
                }
                ShowBusyNotice();
                return false;
            }

            if (owner?.IsSelf == true)
            {
                return true;
            }

            // Do not run vanilla's open yet. It enters UI mode, locks movement, forces the
            // camera and teleports the player to the sign; those side effects must not happen
            // for a player who is still waiting for the lock. The action's apply runs the
            // open when the host grants it, and its undo drops the pending open on rejection.
            ScheduleOpen(renamer, showIntro);
            return false;
        }

        /// <summary>Defers the vanilla open until the host grants this first-come request. The
        /// action is the pending open: its apply performs the open, and the one generic
        /// rejection cancels it before any UI-mode or player-position side effect ran.</summary>
        private void ScheduleOpen(ShopRenamer renamer, bool showIntro)
        {
            if (renamer == null)
            {
                return;
            }

            if (_pendingRenamer != null)
            {
                // A second request while one is already pending (for example the tutorial
                // trigger plus a billboard press) is satisfied by the same grant.
                _pendingShowIntro |= showIntro;
                return;
            }

            _pendingRenamer = renamer;
            _pendingShowIntro = showIntro;
            var actionId = ActionLedger.Submit(
                RenameLockScope,
                new ShopRenameLockIntent { Op = ShopRenameLockOp.Acquire },
                OpenPendingRenameUi,
                CancelPendingRenameUi,
                ShowBusyNotice);
            if (actionId == Guid.Empty)
            {
                // The request never left this peer; do not hold a pending open that only a
                // grant could ever run.
                CoopPlugin.Log.LogError("Shop rename lock request was not sent: there is no live "
                    + "host connection.");
                CancelPendingRenameUi();
            }
        }

        private void OpenPendingRenameUi()
        {
            var renamer = _pendingRenamer;
            var showIntro = _pendingShowIntro;
            _pendingRenamer = null;
            _pendingShowIntro = false;
            if (_shutdown || _context == null || !_context.InGame() || renamer == null)
            {
                return;
            }

            try
            {
                renamer.ShowRenameShopScreen(showIntro);
            }
            catch (Exception error)
            {
                // The host already granted this player the lock. If vanilla's open throws, give
                // it back instead of stranding every other player behind a dead editor.
                CoopPlugin.Log.LogError("Failed to open the shop-name UI after the lock grant: "
                    + error);
                SendRelease();
                throw;
            }
        }

        private void CancelPendingRenameUi()
        {
            var wasTutorial = _pendingShowIntro;
            _pendingRenamer = null;
            _pendingShowIntro = false;
            if (wasTutorial && IsLocalTutorialNamingPending())
            {
                // The trigger already fired, so it cannot ask again. Retry when the other
                // editor releases the lock.
                _pendingTutorialOpen = true;
            }
        }

        private void SendAcquire()
        {
            var actionId = ActionLedger.Submit(
                RenameLockScope,
                new ShopRenameLockIntent { Op = ShopRenameLockOp.Acquire },
                () => { },
                AbandonLocalEdit,
                ShowBusyNotice);
            if (actionId == Guid.Empty)
            {
                // We lost the lock and cannot ask for it back; close the dead editor instead of
                // leaving this player typing into a UI the host will refuse.
                CoopPlugin.Log.LogError("Shop rename lock re-request was not sent: there is no live "
                    + "host connection.");
                AbandonLocalEdit();
            }
        }

        private void AbandonLocalEdit()
        {
            _localEditing = false;
            CloseRenameUiIfOpen();
            ShopState.RepaintCanonical();
        }

        private static bool IsLocalTutorialNamingPending()
            => ShopInterop.FindTutorialRenamer() != null && CPlayerData.m_TutorialIndex == 0;

        /// <summary>The tutorial's first naming side effects (closing the intro UI, advancing the
        /// local index, entitlement bonus) are owed once per peer. The renamer's own tutorial flag
        /// is the gate, not the local tutorial index: the authoritative tutorial state can reach
        /// this peer first and advance the index before this replay runs. A non-tutorial rename
        /// leaves the renamer marked as a normal renamer, so the replay stays a no-op there.</summary>
        private static bool NeedsNamingSideEffects()
            => ShopInterop.FindTutorialRenamer() != null;

        /// <summary>The tutorial's trigger fires once and never resets its in-area flag. If the
        /// auto-open was refused because another player held the lock, retry it when the lock is
        /// released so the tutorial can still be named. The retry goes through the same deferred
        /// open, so no position/UI side effects run before a grant.</summary>
        private void TryResumePendingTutorialOpen()
        {
            if (!_pendingTutorialOpen || _shutdown || _context == null || !_context.InGame())
            {
                return;
            }

            if (_pendingRenamer != null)
            {
                _pendingTutorialOpen = false;
                return;
            }

            var renamer = ShopInterop.FindTutorialRenamer();
            if (renamer == null || CPlayerData.m_TutorialIndex != 0)
            {
                _pendingTutorialOpen = false;
                return;
            }

            _pendingTutorialOpen = false;
            CoopPlugin.Log.LogInfo("Retrying the tutorial shop-name screen after the rename lock released.");
            renamer.ShowRenameShopScreen(true);
        }

        void IShopRenameEndpoint.RenameUiOpened()
        {
            _pendingTutorialOpen = false;
            _localEditing = true;
        }

        void IShopRenameEndpoint.RenameInputChanged(string text)
        {
            if (_shutdown || !_localEditing || _context == null || !_context.InGame()
                || Global<ShopRenameState>.Value?.Owner?.IsSelf != true)
            {
                return;
            }

            var draft = ShopState.NormalizeDraft(text);
            SendToHost(new ShopRenameDraftIntent { Text = draft });
        }

        void IShopRenameEndpoint.ConfirmBegin() => _confirming = true;

        void IShopRenameEndpoint.ConfirmEnd()
        {
            _confirming = false;
            _pendingTutorialOpen = false;
            if (_shutdown || _context == null)
            {
                _localEditing = false;
                return;
            }

            _localEditing = false;
            if (!_context.InGame())
            {
                // Vanilla already closed the UI. Release the lock even across a loading frame;
                // there is no rename to send.
                SendRelease();
                ShopState.RepaintCanonical();
                return;
            }

            // Vanilla already applied the name locally. The host validates both the edit lock
            // and the name; a rejection rolls this proposal back to the previous canonical name.
            try
            {
                PredictRenameObserved();
            }
            catch
            {
                // The proposal never reached the host (or the action could not be recorded).
                // Do not leave the host holding the lock for a screen that is already closed.
                SendRelease();
                throw;
            }
        }

        void IShopRenameEndpoint.RenameUiClosed()
        {
            _pendingTutorialOpen = false;
            if (_confirming || _shutdown || !_localEditing)
            {
                return;
            }

            _localEditing = false;
            SendRelease();
            ShopState.RepaintCanonical();
        }

        void IShopRenameEndpoint.PresentationReady()
        {
            ApplyStoredState();
            TryResumePendingTutorialOpen();
        }

        private void ResetLocalState()
        {
            _localEditing = false;
            _confirming = false;
            _pendingTutorialOpen = false;
            _pendingRenamer = null;
            _pendingShowIntro = false;
            _tutorialNamingPending = false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ResetLocalState();
            ShopRenamePatches.ClearEndpoint(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            _context = null;
            ShopState.Reset();
        }

        private void OnDestroy() => Shutdown();
    }
}
