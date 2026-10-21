using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

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
            var handlersRegistered = false;
            try
            {
                ShopRenameSession.Reset();
                ShopState.SetUnnamed(CPlayerData.m_TutorialIndex == 0);
                // A rename UI already open when the session starts has the live draft in
                // CPlayerData; do not cache that as the canonical baseline.
                if (!ShopState.TryGetCanonical(out _) && !ShopInterop.HasAnyOpenRenameScreen()
                    && !ShopState.TryGet(out _))
                {
                    CoopPlugin.Log.LogWarning("Shop host started before a valid canonical shop name was loaded.");
                }

                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                ShopRenamePatches.SetEndpoint(this);
                _harmony = new Harmony("dev.meepen.cardshopcoop.shop.host");
                ShopRenamePatches.Install(_harmony);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Shop host initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                ShopRenamePatches.ClearEndpoint(this);
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                ClearSessionState();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null)
            {
                return;
            }

            var unnamed = CPlayerData.m_TutorialIndex == 0;
            ShopState.SetUnnamed(unnamed);
            var editing = _localEditing || ShopRenameSession.IsLocalOwner
                || ShopInterop.HasAnyOpenRenameScreen();
            var hasName = ShopState.TryGetCanonical(out var name)
                || (!editing && ShopState.TryGet(out name));
            if (hasName)
            {
                _context.Send(connection.Id, new ShopRenameDeltaMessage
                {
                    PredictionId = Guid.Empty,
                    Name = name,
                    Initial = true,
                    Unnamed = unnamed,
                });
            }

            _context.Send(connection.Id, BuildLockMessage(Guid.Empty));
            if (ShopRenameSession.Draft != null)
            {
                _context.Send(connection.Id, new ShopRenameDraftMessage
                {
                    OwnerConn = ShopRenameSession.Owner,
                    Text = ShopRenameSession.Draft,
                });
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

        [MessageHandler(typeof(ShopRenameLockRequestMessage))]
        private void Handle(MessageContext context, ShopRenameLockRequestMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            var sender = context.Connection.Id;
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
                CoopPlugin.Log.LogWarning("Shop rename lock intent had unknown op " + message.Op
                    + " from peer " + sender + "; ignoring.");
                return;
            }

            if (!_context.InGame())
            {
                // Do not hand out a game-scene UI lock while the host is loading. The release
                // branch above still runs, so a lock opened before the load cannot stick.
                SendLockState(sender, Guid.Empty);
                if (message.PredictionId != Guid.Empty)
                {
                    PredictionApi.Rollback(_context, sender, message.PredictionId);
                }
                return;
            }

            if (!ShopRenameSession.HasOwner)
            {
                var ownerName = PeerDisplayName(sender);
                ShopRenameSession.ApplyLock(sender, ownerName);
                CoopPlugin.Log.LogInfo("Shop rename lock acquired by peer " + sender
                    + " (" + ownerName + ").");
                _context.Broadcast(BuildLockMessage(message.PredictionId));
                return;
            }

            if (ShopRenameSession.IsOwner(sender))
            {
                // A repeated acquire (the UI was reopened before the release crossed) is a grant,
                // not a denial: retire the new prediction with the current state.
                _context.Send(sender, BuildLockMessage(message.PredictionId));
                return;
            }

            CoopPlugin.Log.LogDebug("Shop rename lock denied to peer " + sender
                + "; held by peer " + ShopRenameSession.Owner + ".");
            SendLockState(sender, Guid.Empty);
            if (message.PredictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, sender, message.PredictionId);
            }
        }

        [MessageHandler(typeof(ShopRenameDraftIntentMessage))]
        private void Handle(MessageContext context, ShopRenameDraftIntentMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            var sender = context.Connection.Id;
            if (!ShopRenameSession.IsOwner(sender))
            {
                SendLockState(sender, Guid.Empty);
                return;
            }

            var draft = ShopState.NormalizeDraft(message.Text);
            ShopRenameSession.ApplyDraft(draft);
            _context.Broadcast(new ShopRenameDraftMessage
            {
                OwnerConn = sender,
                Text = draft,
            });
        }

        [MessageHandler(typeof(ShopRenameRequestMessage))]
        private void Handle(MessageContext context, ShopRenameRequestMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            var sender = context.Connection.Id;
            if (!_context.InGame())
            {
                // The host cannot apply a scene change while it is loading. Release the sender's
                // lock and reject the proposal so the client rolls back instead of waiting.
                try
                {
                    SendCanonicalState(sender);
                }
                catch (Exception stateError)
                {
                    CoopPlugin.Log.LogError("Could not send the canonical shop name during a "
                        + "load: " + stateError);
                }
                finally
                {
                    ReleaseLock(sender);
                }
                Reject(context, message.PredictionId);
                return;
            }

            if (!ShopRenameSession.IsOwner(sender))
            {
                CoopPlugin.Log.LogDebug("Shop rename rejected from peer " + sender
                    + " because it does not hold the edit lock.");
                try
                {
                    SendLockState(sender, Guid.Empty);
                    // Put the requester on the host's canonical name before the rollback, so a
                    // stale optimistic snapshot cannot be re-applied over a newer accepted rename.
                    SendCanonicalState(sender);
                }
                catch (Exception stateError)
                {
                    CoopPlugin.Log.LogError("Could not resync peer " + sender
                        + " before rejecting its rename: " + stateError);
                }
                Reject(context, message.PredictionId);
                return;
            }

            if (!ShopState.TryNormalize(message.Name, out var name))
            {
                CoopPlugin.Log.LogDebug("Shop rename rejected from peer " + sender
                    + ": invalid name.");
                // The editor closed its UI on confirm. Put the canonical name back and release
                // the lock so another player can try; the requester reconciles to the canonical
                // state before the generic rollback.
                try
                {
                    SendCanonicalState(sender);
                }
                catch (Exception stateError)
                {
                    CoopPlugin.Log.LogError("Could not send the canonical shop name before "
                        + "rejecting peer " + sender + "'s rename: " + stateError);
                }
                finally
                {
                    ReleaseLock(sender);
                }
                Reject(context, message.PredictionId);
                return;
            }

            var applied = false;
            var broadcast = false;
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

                BroadcastRename(name, message.PredictionId);
                broadcast = true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Shop rename failed while applying peer " + sender
                    + "'s name: " + error);
                if (!broadcast)
                {
                    // If the canonical name was already applied, do not roll the requester back
                    // to an older name; just give it authority. Only an apply failure is a
                    // genuine rejection.
                    SendCanonicalState(sender);
                    if (!applied)
                    {
                        Reject(context, message.PredictionId);
                    }
                }
            }
            finally
            {
                // The lock must never survive a thrown handler: the router keeps the session
                // and there is no timeout to recover it.
                ReleaseLock(sender);
            }
        }

        /// <summary>A live authenticated peer. Game-scene availability is checked by each handler
        /// that actually needs the shop scene, so a lock release still lands during a load.</summary>
        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && _context != null && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
            }
        }

        private void BroadcastRename(string name, Guid predictionId)
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            _context.Broadcast(new ShopRenameDeltaMessage
            {
                PredictionId = predictionId,
                Name = name,
            });
        }

        private void ReleaseLock(int owner)
        {
            if (!ShopRenameSession.IsOwner(owner))
            {
                return;
            }

            // Clear first: the lock must not survive a failed sign repaint or broadcast.
            ShopRenameSession.ClearOwner();
            BroadcastLock(Guid.Empty);
            try
            {
                ShopState.RepaintCanonical();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Shop rename lock released but the sign repaint failed: "
                    + error);
            }
            TryResumePendingTutorialOpen();
        }

        private ShopRenameLockMessage BuildLockMessage(Guid predictionId)
            => new ShopRenameLockMessage
            {
                PredictionId = predictionId,
                OwnerConn = ShopRenameSession.Owner,
                OwnerName = ShopRenameSession.OwnerName,
                Unnamed = ShopState.Unnamed,
            };

        private void SendLockState(int peer, Guid predictionId)
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            _context.Send(peer, BuildLockMessage(predictionId));
        }

        private void BroadcastLock(Guid predictionId)
        {
            if (_shutdown || _context == null)
            {
                return;
            }

            _context.Broadcast(BuildLockMessage(predictionId));
        }

        /// <summary>The canonical name as a baseline (Initial suppresses tutorial side effects),
        /// sent before a rejection rollback so the requester reconciles to authority. Only the
        /// committed cache is sent - never <c>CPlayerData.PlayerName</c>, which is the live
        /// draft while the host is editing.</summary>
        private void SendCanonicalState(int peer)
        {
            if (_shutdown || _context == null || !ShopState.TryGetCanonical(out var name))
            {
                return;
            }

            _context.Send(peer, new ShopRenameDeltaMessage
            {
                PredictionId = Guid.Empty,
                Name = name,
                Initial = true,
                Unnamed = ShopState.Unnamed,
            });
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

            // The tutorial's "still unnamed" state is current at the moment the host opens the
            // rename UI; carry it into the lock state so a release cannot paint the placeholder.
            ShopState.SetUnnamed(CPlayerData.m_TutorialIndex == 0);

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
                BroadcastLock(Guid.Empty);
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
            _context.Broadcast(new ShopRenameDraftMessage
            {
                OwnerConn = CoopCore.LocalConnectionId,
                Text = draft,
            });
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
                BroadcastLock(Guid.Empty);
            }

            try
            {
                if (ShopState.TryNormalize(CPlayerData.GetPlayerName(), out var name))
                {
                    ShopState.Apply(name);
                    BroadcastRename(name, Guid.Empty);
                }
                else
                {
                    CoopPlugin.Log.LogDebug("Host confirmed an invalid shop name; keeping the "
                        + "canonical name.");
                    ShopState.RepaintCanonical();
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
            }
            finally
            {
                // The lock must not survive a thrown UI/game call.
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
            _context?.Messages.UnregisterAttributedHandlers(this);
            ShopRenamePatches.ClearEndpoint(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            ClearSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
