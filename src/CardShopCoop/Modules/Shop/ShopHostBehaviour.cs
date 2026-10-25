using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Shop
{
    [ServerBehaviour]
    public sealed class ShopHostBehaviour : CoopBehaviour, IShopRenameEndpoint
    {
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _localEditing;
        private bool _confirming;
        private bool _pendingTutorialOpen;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            ShopRenameSession.Reset();
            // A rename UI already open when the session starts has the live draft in
            // CPlayerData; the canonical read below deliberately ignores it then.
            if (!ShopState.TryGetCanonical(out _))
            {
                CoopPlugin.Log.LogWarning("Shop host started before a valid canonical shop name was loaded.");
            }

            ShopRenamePatches.SetEndpoint(this);
            _harmony = new Harmony("dev.meepen.cardshopcoop.shop.host");
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

        /// <summary>The store owns the join baseline; seed it once after every behaviour's
        /// OnEnable and before the transport starts. There is deliberately no join callback:
        /// a late joiner receives this value in the generic extended-information baseline.</summary>
        [OnSessionStarted]
        private void SeedState()
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            ExtendedInformationModule.SeedGlobal(typeof(ShopRenameState),
                BuildRenameState(Guid.Empty, rejected: false, isRename: false,
                    refreshUnnamed: true));
            var draft = BuildDraftState();
            if (draft != null)
            {
                ExtendedInformationModule.SeedGlobal(typeof(ShopRenameDraftState), draft);
            }
        }

        [OnClientDisconnected]
        private void ForgetEditor(PeerConnection connection, DisconnectInfo _)
        {
            if (_shutdown || connection == null || !ShopRenameSession.IsOwner(connection.Id))
            {
                return;
            }

            CoopPlugin.Log.LogInfo("Shop rename lock released because peer " + connection.Id
                + " disconnected.");
            ReleaseLock(connection.Id);
        }

        [IntentValidator]
        private IntentValidation ValidateLock(CoopMessageContext context,
            ShopRenameLockIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            if (!context.IsAuthenticated)
            {
                return IntentValidation.Deny("player is not authenticated");
            }

            if (message.Op != ShopRenameLockOp.Acquire && message.Op != ShopRenameLockOp.Release)
            {
                return IntentValidation.Deny("unknown lock operation");
            }

            // Release is a session fact and must land across a loading scene. Acquire is gameplay
            // traffic and waits for the peer's fully-joined acknowledgement.
            if (message.Op == ShopRenameLockOp.Acquire
                && context.Connection.State != ConnectionState.FullyJoined)
            {
                return IntentValidation.Deny("player is still joining");
            }

            return IntentValidation.Allow();
        }

        [IntentRejected]
        private void LockRejected(CoopMessageContext context, ShopRenameLockIntent message,
            string reason)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("Shop rename lock intent rejected: " + reason);
            PublishRenameState(message.ActionId, rejected: true, isRename: false);
        }

        [MessageHandler]
        private void Handle(CoopMessageContext context, ShopRenameLockIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            var sender = context.ConnectionId;
            if (message.Op == ShopRenameLockOp.Release)
            {
                if (ShopRenameSession.IsOwner(sender))
                {
                    CoopPlugin.Log.LogDebug("Shop rename lock released by peer " + sender + ".");
                    ReleaseLock(sender);
                }
                return;
            }

            if (message.Op != ShopRenameLockOp.Acquire)
            {
                return;
            }

            if (!_context.InGame())
            {
                // Do not hand out a game-scene UI lock while the host is loading. The release
                // branch above still runs, so a lock opened before the load cannot stick.
                PublishRenameState(message.ActionId, rejected: true, isRename: false);
                Reject(context, message.ActionId);
                return;
            }

            if (!ShopRenameSession.HasOwner)
            {
                var ownerName = PeerDisplayName(sender);
                ShopRenameSession.ApplyLock(sender, ownerName);
                CoopPlugin.Log.LogInfo("Shop rename lock acquired by peer " + sender
                    + " (" + ownerName + ").");
                PublishRenameState(message.ActionId, rejected: false, isRename: false);
                return;
            }

            if (ShopRenameSession.IsOwner(sender))
            {
                // A repeated acquire (the UI was reopened before the release crossed) is a grant,
                // not a denial: retire the new action with the current state.
                PublishRenameState(message.ActionId, rejected: false, isRename: false);
                return;
            }

            CoopPlugin.Log.LogDebug("Shop rename lock denied to peer " + sender
                + "; held by peer " + ShopRenameSession.Owner + ".");
            PublishRenameState(message.ActionId, rejected: true, isRename: false);
            Reject(context, message.ActionId);
        }

        [IntentValidator]
        private IntentValidation ValidateRename(CoopMessageContext context,
            ShopRenameRequestIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            return context.IsAuthenticated
                ? IntentValidation.Allow()
                : IntentValidation.Deny("player is not authenticated");
        }

        [IntentRejected]
        private void RenameRejected(CoopMessageContext context, ShopRenameRequestIntent message,
            string reason)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("Shop rename intent rejected: " + reason);
            PublishRenameState(message.ActionId, rejected: true, isRename: false);
        }

        [MessageHandler]
        private void Handle(CoopMessageContext context, ShopRenameRequestIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            var sender = context.ConnectionId;
            if (!_context.InGame())
            {
                // The host cannot apply a scene change while it is loading. Release the sender's
                // lock and reject the proposal so the client reconciles instead of waiting.
                RejectRename(context, message, releaseOwner: true);
                return;
            }

            if (!ShopRenameSession.IsOwner(sender))
            {
                CoopPlugin.Log.LogDebug("Shop rename rejected from peer " + sender
                    + " because it does not hold the edit lock.");
                RejectRename(context, message, releaseOwner: false);
                return;
            }

            if (!ShopState.TryNormalize(message.Name, out var name))
            {
                CoopPlugin.Log.LogDebug("Shop rename rejected from peer " + sender
                    + ": invalid name.");
                RejectRename(context, message, releaseOwner: true);
                return;
            }

            var applied = false;
            var published = false;
            try
            {
                if (!ShopState.Apply(name))
                {
                    // CPlayerData is still authoritative if the sign UI is between scenes. The
                    // state broadcast remains valid and the next baseline will repaint the UI.
                    CoopPlugin.Log.LogDebug("Shop rename accepted while the rename UI was unavailable.");
                }
                applied = true;

                // The naming side effects are presentation/tutorial work. A failure in them must
                // not strand the lock or swallow the accepted name.
                try
                {
                    if (!ShopInterop.ApplyRemoteInitialNamingSideEffects())
                    {
                        CoopPlugin.Log.LogDebug("Shop rename accepted without completing the local "
                            + "rename UI/tutorial side effects.");
                    }
                }
                catch (Exception sideEffectError)
                {
                    CoopPlugin.Log.LogError("Shop rename side effects failed after applying the "
                        + "name; continuing with the accepted rename: " + sideEffectError);
                }

                PublishRenameCleared(message.ActionId, name);
                published = true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Shop rename failed while applying peer " + sender
                    + "'s name: " + error);
                if (!published)
                {
                    // If the canonical name was already applied, do not roll the requester back
                    // to an older name; just give it authority. Only an apply failure is a
                    // genuine rejection.
                    PublishRenameState(message.ActionId, rejected: !applied, isRename: false);
                    if (!applied)
                    {
                        Reject(context, message.ActionId);
                    }
                }
            }
            finally
            {
                // The lock must never survive a thrown handler: the router keeps the session
                // and there is no timeout to recover it.
                if (!published)
                {
                    ReleaseLock(sender);
                }
            }
        }

        [MessageHandler]
        private void Handle(CoopMessageContext context, ShopRenameDraftIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null
                || !context.IsAuthenticated)
            {
                return;
            }

            var sender = context.ConnectionId;
            if (!ShopRenameSession.IsOwner(sender))
            {
                return;
            }

            var draft = ShopState.NormalizeDraft(message.Text);
            ShopRenameSession.ApplyDraft(draft);
            PublishDraft();
        }

        private void Reject(CoopMessageContext context, Guid actionId)
        {
            if (actionId != Guid.Empty && context?.Connection != null)
            {
                ActionLedger.Rollback(_context, context.ConnectionId, actionId);
            }
        }

        private void RejectRename(CoopMessageContext context, ShopRenameRequestIntent message,
            bool releaseOwner)
        {
            if (releaseOwner && ShopRenameSession.IsOwner(context.ConnectionId))
            {
                PublishLockCleared(message.ActionId, rejected: true);
            }
            else
            {
                PublishRenameState(message.ActionId, rejected: true, isRename: false);
            }

            Reject(context, message.ActionId);
        }

        /// <summary>Publishes the accepted rename and releases the lock in one state, so the
        /// requester retires its rename action and applies the canonical name and owner together.
        /// The name is passed in because the session value still holds the previous one until this
        /// publish lands.</summary>
        private void PublishRenameCleared(Guid actionId, string name)
        {
            ShopRenameSession.ClearOwner();
            PublishRenameState(actionId, rejected: false, isRename: true, name: name,
                unnamed: false);
            PublishDraft();
            ShopState.RepaintCanonical();
            TryResumePendingTutorialOpen();
        }

        /// <summary>Releases the lock and publishes the canonical state. The rejected flag is used
        /// when the release is the re-anchor for a refused rename or lock action.</summary>
        private void PublishLockCleared(Guid actionId, bool rejected)
        {
            ShopRenameSession.ClearOwner();
            PublishRenameState(actionId, rejected: rejected, isRename: false);
            PublishDraft();
            ShopState.RepaintCanonical();
            TryResumePendingTutorialOpen();
        }

        private void ReleaseLock(int owner)
        {
            if (!ShopRenameSession.IsOwner(owner))
            {
                return;
            }

            // Clear first: the lock must not survive a failed sign repaint or broadcast.
            PublishLockCleared(Guid.Empty, rejected: false);
        }

        /// <summary>Builds the published state. <paramref name="name"/> is only supplied when the
        /// caller already holds the new canonical name (an accepted rename); every other publish
        /// reads it from the current session value, which keeps that value the only authority.
        /// <paramref name="unnamed"/> defaults to the current state, with the host's tutorial index
        /// as the pre-session answer; only a login/UI-open refresh or a committed rename changes
        /// it.</summary>
        private ShopRenameState BuildRenameState(Guid actionId, bool rejected, bool isRename,
            string name = null, bool? unnamed = null, bool refreshUnnamed = false)
        {
            string canonical = null;
            var hasName = name != null
                ? ShopState.TryNormalize(name, out canonical)
                : ShopState.TryGetCanonical(out canonical);
            var unresolved = unnamed
                ?? (refreshUnnamed
                    ? CPlayerData.m_TutorialIndex == 0
                    : Global<ShopRenameState>.Value?.Unnamed
                        ?? (CPlayerData.m_TutorialIndex == 0));
            return new ShopRenameState
            {
                Name = hasName ? canonical : null,
                Unnamed = unresolved,
                Owner = OwnerRef(ShopRenameSession.Owner),
                IsRename = isRename,
                ActionId = actionId,
                Rejected = rejected,
            };
        }

        private void PublishRenameState(Guid actionId, bool rejected, bool isRename,
            string name = null, bool? unnamed = null, bool refreshUnnamed = false)
        {
            if (_shutdown || _context == null || CoopCore.Instance?.Net == null)
            {
                return;
            }

            Global<ShopRenameState>.Value = BuildRenameState(actionId, rejected, isRename, name,
                unnamed, refreshUnnamed);
        }

        private ShopRenameDraftState BuildDraftState()
        {
            if (!ShopRenameSession.HasOwner || ShopRenameSession.Draft == null)
            {
                return null;
            }

            return new ShopRenameDraftState
            {
                Owner = OwnerRef(ShopRenameSession.Owner),
                Text = ShopRenameSession.Draft,
            };
        }

        private void PublishDraft()
        {
            if (_shutdown || _context == null || CoopCore.Instance?.Net == null)
            {
                return;
            }

            var draft = BuildDraftState();
            if (draft == null)
            {
                if (Global<ShopRenameDraftState>.Value != null)
                {
                    Global<ShopRenameDraftState>.Value = null;
                }
                return;
            }

            Global<ShopRenameDraftState>.Value = draft;
        }

        private PlayerRef OwnerRef(int owner)
        {
            if (owner == ShopRenameSession.NoOwner)
            {
                return null;
            }

            return PresenceApi.ResolvePlayerRef(owner)
                ?? new PlayerRef(owner, PeerDisplayName(owner), owner == 0, false);
        }

        private void ShowBusyNotice()
        {
            _context?.SetStatusLine?.Invoke(ShopRenameSession.BusyEditorName()
                + " is renaming the shop.", 4f);
        }

        private string PeerDisplayName(int connectionId)
        {
            if (connectionId == CoopCore.LocalConnectionId)
            {
                var local = PresenceApi.LocalPlayerName;
                return string.IsNullOrWhiteSpace(local) ? "The host" : local;
            }

            var name = _context?.PeerName?.Invoke(connectionId);
            return string.IsNullOrWhiteSpace(name) ? "Player " + connectionId : name;
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

            if (!ShopRenameSession.HasOwner)
            {
                ShopRenameSession.ApplyLock(CoopCore.LocalConnectionId,
                    PeerDisplayName(CoopCore.LocalConnectionId));
                CoopPlugin.Log.LogInfo("Shop rename lock acquired by the host.");
                PublishRenameState(Guid.Empty, rejected: false, isRename: false,
                    refreshUnnamed: true);
            }
            return true;
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
            PublishDraft();
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
                // Vanilla already closed the UI. Do not leave the lock held across a loading
                // transition; release is a session fact, not a game-chunk message.
                ReleaseLock(CoopCore.LocalConnectionId);
                return;
            }

            if (ShopRenameSession.HasOwner && !ShopRenameSession.IsLocalOwner)
            {
                // The rename UI was opened while another player owned the lock (for example it
                // was already open when the session started). Vanilla still renamed locally;
                // put the canonical name back instead of letting this leak into the session.
                ShopState.RepaintCanonical();
                return;
            }

            if (!ShopRenameSession.HasOwner)
            {
                // Nobody owns the lock (for example the UI was already open when the session
                // started). Acquire it now so the rename is still first-come-first-serve for
                // the peer that confirms next.
                ShopRenameSession.ApplyLock(CoopCore.LocalConnectionId,
                    PeerDisplayName(CoopCore.LocalConnectionId));
                PublishRenameState(Guid.Empty, rejected: false, isRename: false);
            }

            try
            {
                if (ShopState.TryNormalize(CPlayerData.GetPlayerName(), out var name))
                {
                    ShopState.Apply(name);
                    PublishRenameCleared(Guid.Empty, name);
                }
                else
                {
                    CoopPlugin.Log.LogDebug("Host confirmed an invalid shop name; keeping the "
                        + "canonical name.");
                    ShopState.RepaintCanonical();
                    ReleaseLock(CoopCore.LocalConnectionId);
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Host shop rename failed locally: " + error);
                try
                {
                    ShopState.RepaintCanonical();
                }
                catch (Exception repaintError)
                {
                    CoopPlugin.Log.LogError("Host could not restore the shop sign: " + repaintError);
                }

                ReleaseLock(CoopCore.LocalConnectionId);
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
            ReleaseLock(CoopCore.LocalConnectionId);
        }

        /// <summary>The tutorial's trigger fires once and never resets its in-area flag. If that
        /// auto-open was refused because another player held the lock, retry it when the lock is
        /// released so the host's tutorial can still be named.</summary>
        private void TryResumePendingTutorialOpen()
        {
            if (!_pendingTutorialOpen || _shutdown || _context == null || !_context.InGame())
            {
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
            try
            {
                renamer.ShowRenameShopScreen(true);
            }
            catch (Exception error)
            {
                // The retry may already have acquired the lock in its prefix. Do not let a
                // vanilla open failure leave the host holding it.
                CoopPlugin.Log.LogError("Failed to reopen the tutorial shop-name screen: " + error);
                ReleaseLock(CoopCore.LocalConnectionId);
                throw;
            }
        }

        void IShopRenameEndpoint.PresentationReady()
        {
            ShopRenameSession.FlushPresentation();
            TryResumePendingTutorialOpen();
        }

        private void ClearSessionState()
        {
            _localEditing = false;
            _confirming = false;
            _pendingTutorialOpen = false;
            ShopState.Reset();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ShopRenamePatches.ClearEndpoint(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            ClearSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
