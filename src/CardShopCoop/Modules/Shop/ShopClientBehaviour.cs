using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using System;

namespace CardShopCoop.Modules.Shop
{
    [ClientBehaviour]
    public sealed class ShopClientBehaviour : CoopBehaviour, IShopRenameEndpoint
    {
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _localEditing;
        private bool _confirming;
        private bool _pendingTutorialOpen;
        private ShopRenamer _pendingRenamer;
        private bool _pendingShowIntro;
        private string _pendingName;
        private bool _pendingInitial;
        private bool _pendingUnnamed;
        private bool _remoteInitialNamingApplied;
        private const string PredictionScope = "shop.rename";
        private const string LockPredictionScope = "shop.rename.lock";

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }
            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                ShopRenameSession.Reset();
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                ShopRenamePatches.SetEndpoint(this);
                _harmony = new Harmony("dev.meepen.cardshopcoop.shop.client");
                ShopRenamePatches.Install(_harmony);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Shop client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                ShopRenamePatches.ClearEndpoint(this);
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                _pendingName = null;
                _pendingInitial = false;
                _pendingUnnamed = false;
                _remoteInitialNamingApplied = false;
                _localEditing = false;
                _confirming = false;
                _pendingTutorialOpen = false;
                _pendingRenamer = null;
                _pendingShowIntro = false;
                ShopState.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(ShopRenameDeltaMessage))]
        private void Handle(MessageContext _, ShopRenameDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            // Confirm, not AckOrApply: the host normalizes the name (trim/length/control), so the
            // actor must retire its prediction AND apply the host's canonical value. AckOrApply
            // retires the actor's own echo and skips the apply, keeping the raw input.
            PredictionApi.Confirm(message.PredictionId,
                () =>
                {
                    _pendingName = message.Name;
                    _pendingInitial = message.Initial;
                    _pendingUnnamed = message.Unnamed;
                    ApplyPendingNameWhenReady();
                });
        }

        [MessageHandler(typeof(ShopRenameLockMessage))]
        private void Handle(MessageContext _, ShopRenameLockMessage message)
        {
            if (message == null)
            {
                return;
            }

            PredictionApi.Confirm(message.PredictionId, () => ApplyLockState(message));
        }

        [MessageHandler(typeof(ShopRenameDraftMessage))]
        private void Handle(MessageContext _, ShopRenameDraftMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (message.OwnerConn == CoopCore.LocalConnectionId)
            {
                // The local vanilla input already painted the newest text. Applying any echo of
                // our own draft - especially one that was already in flight when we confirmed -
                // would make the sign lag behind or regress after the commit.
                return;
            }

            ShopRenameSession.ApplyDraft(message.Text);
        }

        private void ApplyLockState(ShopRenameLockMessage message)
        {
            var wasLocalOwner = ShopRenameSession.IsLocalOwner;
            ShopState.SetUnnamed(message.Unnamed);
            ShopRenameSession.ApplyLock(message.OwnerConn, message.OwnerName,
                repaintCanonical: !_localEditing);

            if (message.OwnerConn == CoopCore.LocalConnectionId)
            {
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
                    // The UI surface disappeared between the request and the grant; do not
                    // leave the host holding a lock this player is not using.
                    SendRelease();
                }
                return;
            }

            if (message.OwnerConn == ShopRenameSession.NoOwner)
            {
                if (_localEditing)
                {
                    if (wasLocalOwner)
                    {
                        // We still have the UI open but the host no longer has us as the
                        // owner (for example a forced release). Claim the free lock again
                        // rather than typing into a dead edit session.
                        SendAcquire();
                    }
                    else
                    {
                        AbandonLocalEdit();
                    }
                    return;
                }

                if (_pendingRenamer == null)
                {
                    TryResumePendingTutorialOpen();
                }
                return;
            }

            if (_pendingRenamer != null)
            {
                // Someone else won while our open was deferred. Nothing was opened, so there
                // is no UI to close; drop the pending open and let the denial rollback show
                // the notice.
                CancelPendingRenameUi();
                return;
            }

            if (!_localEditing)
            {
                return;
            }

            // An already-open UI lost the lock. Close it now; the generic rollback is right
            // behind this authoritative state.
            _localEditing = false;
            CloseRenameUiIfOpen();
            ShopState.RepaintCanonical();
            ShowBusyNotice();
        }

        private void SendCurrentDraft()
        {
            var renamer = ShopInterop.FindRenamer();
            var text = renamer?.m_SetNameInput != null
                ? renamer.m_SetNameInput.text
                : CPlayerData.GetPlayerName();
            var draft = ShopState.NormalizeDraft(text);
            ShopRenameSession.ApplyDraft(draft);
            _context.Send(1, new ShopRenameDraftIntentMessage { Text = draft });
        }

        private void SendRelease()
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            // Release is a session fact. It must still go out while the game is between scenes,
            // otherwise a confirm/open that crosses a load strands the host's lock forever.
            _context.Send(1, new ShopRenameLockRequestMessage
            {
                PredictionId = Guid.Empty,
                Op = ShopRenameLockOp.Release,
            });
        }

        private void ApplyPendingNameWhenReady()
        {
            if (_shutdown || _context == null || !_context.InGame())
            {
                return;
            }

            if (_pendingName != null)
            {
                // On a new save, ShopRenamer.SetIsTutorial blanks the sign while the shop is unnamed
                // and only sets the "My Card Shop" placeholder as the name; the join baseline carries
                // that placeholder, so keep it off the sign until the shop is named. The host decides
                // whether the shop is unnamed; the guest's own tutorial index may be a stale snapshot.
                var unnamedBaseline = _pendingInitial && _pendingUnnamed;
                if (!ShopState.Apply(_pendingName, paintSign: !unnamedBaseline))
                {
                    return;
                }

                // The join baseline carries the current canonical name, not a rename. Replaying the
                // naming side effects here would advance this guest's tutorial (and grant the shop
                // naming bonus) even though the host is still in the intro, so a guest of an unnamed
                // host would sit one tutorial step ahead forever. Only a real rename - the host's own
                // rename or this guest's accepted request - replays those host-owned side effects.
                if (!_pendingInitial && !_remoteInitialNamingApplied)
                {
                    if (!ShopInterop.ApplyRemoteInitialNamingSideEffects())
                    {
                        return;
                    }

                    _remoteInitialNamingApplied = true;
                }

                _pendingName = null;
                _pendingInitial = false;
                _pendingUnnamed = false;
            }

            // A draft can arrive before this scene's renamer surface exists; paint it once the
            // canonical baseline has landed.
            ShopRenameSession.FlushPresentation();
        }

        /// <summary>Records the client's rename proposal the game already performed. Vanilla's
        /// confirm runs in every state, so the prediction only supplies the replay/undo closures and
        /// the canonical cache/sign are synced directly.</summary>
        private void PredictRenameObserved()
        {
            var (name, previous) = CaptureRename();
            PredictionApi.Predict(
                PredictionScope,
                RenameSend(name),
                () => ShopState.Apply(name),
                () =>
                {
                    // The host may have accepted another rename before this rejection landed.
                    // Only undo while our own optimistic name is still the canonical one; the
                    // host's canonical state message is sent immediately before the rollback.
                    if (previous != null && ShopState.IsCanonical(name))
                    {
                        ShopState.Apply(previous);
                    }
                });

            // Vanilla already performed the rename locally; keep the canonical cache and the
            // sign in step without double-applying through the prediction.
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

        private Action<Guid> RenameSend(string name)
            => predictionId => _context.Send(1, new ShopRenameRequestMessage
            {
                PredictionId = predictionId,
                Name = name,
            });

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
            _context?.SetStatusLine?.Invoke(ShopRenameSession.BusyEditorName()
                + " is renaming the shop.", 4f);
        }

        bool IShopRenameEndpoint.TryOpenRenameUi(ShopRenamer renamer, bool showIntro)
        {
            if (_shutdown || _context == null || !_context.InGame())
            {
                return true;
            }

            if (ShopRenameSession.HasOwner && !ShopRenameSession.IsLocalOwner)
            {
                if (showIntro)
                {
                    _pendingTutorialOpen = true;
                }
                ShowBusyNotice();
                return false;
            }

            if (ShopRenameSession.IsLocalOwner)
            {
                return true;
            }

            // Do not run vanilla's open yet. It enters UI mode, locks movement, forces the
            // camera and teleports the player to the sign; those side effects must not happen
            // for a player who is still waiting for the lock. The prediction's apply runs the
            // open when the host grants it, and its undo drops the pending open on rejection.
            ScheduleOpen(renamer, showIntro);
            return false;
        }

        /// <summary>Defers the vanilla open until the host grants this first-come request. The
        /// prediction is the pending open: its apply performs the open, and the one generic
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
            PredictionApi.Predict(
                LockPredictionScope,
                predictionId => _context.Send(1, new ShopRenameLockRequestMessage
                {
                    PredictionId = predictionId,
                    Op = ShopRenameLockOp.Acquire,
                }),
                OpenPendingRenameUi,
                CancelPendingRenameUi,
                ShowBusyNotice);
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
            PredictionApi.Predict(
                LockPredictionScope,
                predictionId => _context.Send(1, new ShopRenameLockRequestMessage
                {
                    PredictionId = predictionId,
                    Op = ShopRenameLockOp.Acquire,
                }),
                () => { },
                AbandonLocalEdit,
                ShowBusyNotice);
        }

        private void AbandonLocalEdit()
        {
            _localEditing = false;
            CloseRenameUiIfOpen();
            ShopState.RepaintCanonical();
        }

        private static bool IsLocalTutorialNamingPending()
            => ShopInterop.FindTutorialRenamer() != null && CPlayerData.m_TutorialIndex == 0;

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
                || !ShopRenameSession.IsLocalOwner)
            {
                return;
            }

            var draft = ShopState.NormalizeDraft(text);
            ShopRenameSession.ApplyDraft(draft);
            _context.Send(1, new ShopRenameDraftIntentMessage { Text = draft });
        }

        void IShopRenameEndpoint.ConfirmBegin() => _confirming = true;

        void IShopRenameEndpoint.ConfirmEnd()
        {
            _confirming = false;
            _pendingTutorialOpen = false;
            if (_shutdown || _context == null)
            {
                _localEditing = false;
                ShopRenameSession.ClearOwner();
                return;
            }

            _localEditing = false;
            if (!_context.InGame())
            {
                // Vanilla already closed the UI. Release the lock even across a loading frame;
                // there is no rename to send.
                SendRelease();
                ShopRenameSession.ApplyLock(ShopRenameSession.NoOwner, null);
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
                // The proposal never reached the host (or the prediction could not be recorded).
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
            ShopRenameSession.ApplyLock(ShopRenameSession.NoOwner, null);
        }

        void IShopRenameEndpoint.PresentationReady()
        {
            ApplyPendingNameWhenReady();
            TryResumePendingTutorialOpen();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _pendingName = null;
            _pendingInitial = false;
            _pendingUnnamed = false;
            _localEditing = false;
            _confirming = false;
            _pendingTutorialOpen = false;
            _pendingRenamer = null;
            _pendingShowIntro = false;
            _context?.Messages.UnregisterAttributedHandlers(this);
            ShopRenamePatches.ClearEndpoint(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            _context = null;
            ShopState.Reset();
            _remoteInitialNamingApplied = false;
        }
        private void OnDestroy() => Shutdown();
    }
}
