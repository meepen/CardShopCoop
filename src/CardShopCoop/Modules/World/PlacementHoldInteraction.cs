using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Tracks who is holding which placed object for placement. Observers do not receive an object
    /// pose or a validity flag: they put the real object into the same vanilla move state a local
    /// player would, infer the target from the holder's camera, and let the object's own Update do
    /// the lerp, snapping, and red/green evaluation. A per-hold copy of the game's preview model
    /// renders the ghost, because the stock preview is a single global object.
    /// </summary>
    internal sealed class PlacementHoldInteraction
    {
        private const float PreviewDistance = 3f;
        private const float PreviewOffsetUp = -0.1f;
        private const float DefaultRayDistance = 5f;
        private const float MoveRayReach = 1.5f;
        private static readonly MethodInfo OnPlacedMovedObjectMethod =
            AccessTools.Method(typeof(InteractableObject), "OnPlacedMovedObject");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static int _remotePreviewUpdates;

        private sealed class RemoteHold
        {
            internal InteractableObject Obj;
            internal GameObject Ghost;
            internal Material Material;
            internal PlayerRef Mover;
            internal int OriginalLayer;
            internal bool OriginalValidAreaActive;
            internal bool OriginalBoxColliderEnabled;
            internal bool[] OriginalBoxColliderList;
            internal GameObject Package;
            internal bool Valid;
            internal bool StopSnap;
            internal bool IsFlipped;
            internal float RayDistance;
            internal Vector3 CameraPosition;
            internal Quaternion CameraRotation;
            internal bool HasCamera;
            internal bool AimUnavailableLogged;
        }

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<ICoopPeer, INetMessage> _send;
        private readonly Dictionary<InteractableObject, PlayerRef> _holders =
            new(PlacementObjectComparer.Instance);
        private readonly Dictionary<InteractableObject, RemoteHold> _remote =
            new(PlacementObjectComparer.Instance);

        private InteractableObject _localObject;
        private bool _localBeginSent;
        private Vector3 _localBeforePosition;
        private Quaternion _localBeforeRotation;
        private Quaternion _localSentRotation;
        private bool _localStopSnap;
        private bool _localIsFlipped;
        private int _remotePreviewCount;
        private bool _savedStopSnap;
        private bool _remoteUpdateActive;
        private static int _suppressedLocalStarts;

        internal static bool SuppressGlobalPreview => _remotePreviewUpdates > 0;

        internal static void BeginRemotePreviewUpdate() => _remotePreviewUpdates++;

        internal static void EndRemotePreviewUpdate()
        {
            if (_remotePreviewUpdates > 0)
            {
                _remotePreviewUpdates--;
            }
        }

        internal bool IsRemoteHold(InteractableObject obj)
            => obj != null && _remote.ContainsKey(obj);

        internal PlacementHoldInteraction(bool host, Action<INetMessage> broadcast,
            Action<ICoopPeer, INetMessage> send)
        {
            _host = host;
            _broadcast = broadcast;
            _send = send;
        }

        /// <summary>A game-path apply drives StartMoveObject/PlaceMovedObject itself; the resulting
        /// move lifecycle is not a player hold. Callers wrap their apply with this scope so applies
        /// never emit hold traffic or leave a hold behind.</summary>
        internal static void BeginSuppressLocalStarts() => _suppressedLocalStarts++;

        internal static void EndSuppressLocalStarts()
        {
            if (_suppressedLocalStarts > 0)
            {
                _suppressedLocalStarts--;
            }
        }

        /// <summary>False when another player already holds the object, so the local move must be
        /// blocked. Untracked objects keep vanilla behavior.</summary>
        internal bool IsAllowed(InteractableObject obj)
        {
            if (obj == null)
            {
                return true;
            }

            if (_holders.TryGetValue(obj, out var mover) && !IsLocalMover(mover))
            {
                CoopPlugin.Log.LogInfo("[placement-hold] blocked entity="
                    + PlacementInterop.Describe(obj) + " held by mover=" + mover + ".");
                return false;
            }

            return true;
        }

        /// <summary>Local player started moving <paramref name="obj"/>, observed after the game's
        /// own StartMoveObject ran. Returns false when another mover already owns the hold; in
        /// that case the just-entered vanilla preview is unwound through the game's own exit path
        /// so this peer does not track (or later predict) a move it does not own.</summary>
        internal bool LocalStarted(InteractableObject obj)
        {
            if (_suppressedLocalStarts > 0 || obj == null || _localObject != null)
            {
                return false;
            }

            _localObject = obj;
            _localBeforePosition = obj.transform.position;
            _localBeforeRotation = obj.transform.rotation;

            if (_holders.TryGetValue(obj, out var mover) && !IsLocalMover(mover))
            {
                // Another player owns this object's hold. The game already entered move mode (this
                // runs as a postfix), so unwind it through the game's own exit path rather than
                // suppressing StartMoveObject; the single-writer hold table stays authoritative.
                CoopPlugin.Log.LogInfo("[placement-hold] local start refused entity="
                    + PlacementInterop.Describe(obj) + " held by mover=" + mover + ".");
                CancelLocalMove();
                return false;
            }

            // The wire can only name an object the identity module has bound. An in-flight
            // decoration has no placed object yet, so its hold is kept local and dropped from the
            // network rather than inventing an identifier.
            _localBeginSent = PlacementInterop.HasSharedIdentity(obj);
            var message = BuildBeginMessage(obj);
            _localStopSnap = message.StopSnap;
            _localIsFlipped = message.IsFlipped;
            CoopPlugin.Log.LogInfo("[placement-hold] local begin entity="
                + PlacementInterop.Describe(obj) + " shared=" + _localBeginSent + " host="
                + _host + " stopSnap=" + _localStopSnap + " flipped=" + _localIsFlipped + ".");

            if (_localBeginSent)
            {
                if (_host)
                {
                    message.Mover = LocalPlayer;
                    _holders[obj] = message.Mover;
                    _broadcast(message);
                }
                else
                {
                    // The host stamps the holder from the authenticated sender.
                    SendToHost(CreateBeginRequest(message));
                }
            }
            else
            {
                CoopPlugin.Log.LogWarning("[placement-hold] object has no shared identity yet; "
                    + "dropping hold message for entity=" + PlacementInterop.Describe(obj) + ".");
            }

            // The mover may already have aimed the object (StartMoveObject orients some objects),
            // so publish the starting rotation; observers then follow rotate events from there.
            _localSentRotation = obj.transform.rotation;
            PublishRotation(_localSentRotation);
            return true;
        }

        internal void LocalEnded(InteractableObject obj)
        {
            if (_localObject == null || !ReferenceEquals(obj, _localObject))
            {
                return;
            }

            var ended = _localObject;
            var shared = _localBeginSent;
            _localObject = null;
            _localBeginSent = false;
            _localStopSnap = false;
            _localIsFlipped = false;
            CoopPlugin.Log.LogInfo("[placement-hold] local end entity="
                + PlacementInterop.Describe(ended) + " shared=" + shared + " host=" + _host + ".");
            if (!shared)
            {
                return;
            }

            if (_host)
            {
                if (_holders.TryGetValue(ended, out var mover) && IsLocalMover(mover))
                {
                    _holders.Remove(ended);
                }

                _broadcast(new PlacementHoldEndMessage { Entity = ended });
            }
            else
            {
                SendToHost(new PlacementHoldEndRequestMessage { Entity = ended });
            }

            if (_remote.Count > 0)
            {
                SetDashedLines(true);
            }
        }

        /// <summary>Event-driven rotation driver: called after the local mover's rotate input
        /// actually changed the held object, and only then forwarded to observers.</summary>
        internal void LocalRotated(InteractableObject obj)
        {
            if (_localObject == null || !ReferenceEquals(obj, _localObject))
            {
                return;
            }

            var rotation = obj.transform.rotation;
            if (Quaternion.Angle(rotation, _localSentRotation) < 0.01f)
            {
                return;
            }

            _localSentRotation = rotation;
            CoopPlugin.Log.LogInfo("[placement-hold] local rotate entity="
                + PlacementInterop.Describe(_localObject) + " yaw="
                + rotation.eulerAngles.y.ToString("F1") + ".");
            PublishRotation(rotation);
        }

        /// <summary>Flip is not a transform rotation for vertical decorations; publish the flip
        /// state itself as well as any transform change.</summary>
        internal void LocalFlipped(InteractableObject obj)
        {
            if (_localObject == null || !ReferenceEquals(obj, _localObject))
            {
                return;
            }

            _localIsFlipped = PlacementInterop.ReadIsFlipped(obj);
            PublishState();
            LocalRotated(obj);
        }

        /// <summary>Called after the local controller's Update. Sends a state change only when the
        /// DisableSnap input actually changed while this peer owns a hold.</summary>
        internal void LocalInputChanged()
        {
            if (_localObject == null || !_localBeginSent)
            {
                return;
            }

            var stopSnap = PlacementInterop.ReadStopSnap();
            if (stopSnap == _localStopSnap)
            {
                return;
            }

            _localStopSnap = stopSnap;
            CoopPlugin.Log.LogInfo("[placement-hold] local state entity="
                + PlacementInterop.Describe(_localObject) + " stopSnap=" + stopSnap + ".");
            PublishState();
        }

        private void PublishRotation(Quaternion rotation)
        {
            if (_localObject == null || !_localBeginSent)
            {
                return;
            }

            if (_host)
            {
                _broadcast(new PlacementHoldRotationMessage
                {
                    Entity = _localObject,
                    Rotation = rotation,
                });
            }
            else
            {
                SendToHost(new PlacementHoldRotationRequestMessage
                {
                    Entity = _localObject,
                    Rotation = rotation,
                });
            }
        }

        private void PublishState()
        {
            if (_localObject == null || !_localBeginSent)
            {
                return;
            }

            if (_host)
            {
                _broadcast(new PlacementHoldStateMessage
                {
                    Entity = _localObject,
                    StopSnap = _localStopSnap,
                    IsFlipped = _localIsFlipped,
                });
            }
            else
            {
                SendToHost(new PlacementHoldStateRequestMessage
                {
                    Entity = _localObject,
                    StopSnap = _localStopSnap,
                    IsFlipped = _localIsFlipped,
                });
            }
        }

        /// <summary>Applies a mover's rotation to its rendered ghost on an observer.</summary>
        internal void ApplyRotation(PlacementHoldRotationMessage message)
        {
            if (message == null || message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped rotation with no live entity.");
                return;
            }

            if (!_remote.TryGetValue(message.Entity, out var hold))
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped rotation for an object that is "
                    + "not held remotely: entity=" + PlacementInterop.Describe(message.Entity) + ".");
                return;
            }

            if (hold.Obj != null)
            {
                hold.Obj.transform.rotation = message.Rotation;
            }
            else if (hold.Ghost != null)
            {
                hold.Ghost.transform.rotation = message.Rotation;
            }
        }

        /// <summary>Applies the mover's DisableSnap/flip inputs to the remote move state.</summary>
        internal void ApplyState(PlacementHoldStateMessage message)
        {
            if (message == null || message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped state with no live entity.");
                return;
            }

            if (!_remote.TryGetValue(message.Entity, out var hold))
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped state for an object that is "
                    + "not held remotely: entity=" + PlacementInterop.Describe(message.Entity) + ".");
                return;
            }

            hold.StopSnap = message.StopSnap;
            hold.IsFlipped = message.IsFlipped;
            if (hold.Obj != null)
            {
                PlacementInterop.SetIsFlipped(hold.Obj, message.IsFlipped);
            }
        }

        /// <summary>True when <paramref name="peer"/> owns the hold for <paramref name="obj"/>,
        /// used to validate a rotate/state request.</summary>
        internal bool IsHeldBy(InteractableObject obj, ICoopPeer peer)
            => obj != null && _holders.TryGetValue(obj, out var mover)
                && IsSamePlayer(mover, peer);

        /// <summary>Host authority: grants the hold to <paramref name="peer"/> when the object is
        /// free, returning the broadcast to send. The granted message carries the holder's
        /// authenticated roster identity, never a connection id.</summary>
        internal bool HostGrant(InteractableObject obj, ICoopPeer peer,
            PlacementHoldBeginMessage request, out PlacementHoldBeginMessage granted)
        {
            granted = null;
            var mover = peer?.Player;
            if (obj == null || mover == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] cannot grant a hold entity="
                    + PlacementInterop.Describe(obj) + " peer=" + (peer?.Name ?? "<none>")
                    + ": the peer has no roster identity.");
                return false;
            }

            if (_holders.TryGetValue(obj, out var existing) && !existing.Equals(mover))
            {
                return false;
            }

            _holders[obj] = mover;
            granted = new PlacementHoldBeginMessage
            {
                Entity = obj,
                Mover = mover,
            };
            CopyBeginState(request, granted);
            return true;
        }

        internal bool HostRelease(InteractableObject obj, ICoopPeer peer)
        {
            if (obj == null || peer == null || !_holders.TryGetValue(obj, out var existing)
                || !IsSamePlayer(existing, peer))
            {
                return false;
            }

            _holders.Remove(obj);
            return true;
        }

        /// <summary>Ends the hold for <paramref name="obj"/> when <paramref name="peer"/> owns it,
        /// and tells the peers. Used when another action (boxing up) supersedes the hold.</summary>
        internal bool EndIfHeldBy(InteractableObject obj, ICoopPeer peer)
        {
            if (!HostRelease(obj, peer))
            {
                return false;
            }

            ApplyEnd(obj);
            _broadcast(new PlacementHoldEndMessage { Entity = obj });
            return true;
        }

        /// <summary>Releases everything a disconnected peer held, appending each object to
        /// <paramref name="released"/> so the caller can broadcast the end.</summary>
        internal void ReleaseMover(ICoopPeer peer, List<InteractableObject> released)
        {
            released.Clear();
            if (peer == null)
            {
                return;
            }

            foreach (var pair in _holders)
            {
                if (IsSamePlayer(pair.Value, peer))
                {
                    released.Add(pair.Key);
                }
            }

            for (var i = 0; i < released.Count; i++)
            {
                // Stop our local render of the disconnected peer's hold as well as telling the
                // other peers; otherwise the ghost and the object's move state stick.
                ApplyEnd(released[i]);
                _broadcast(new PlacementHoldEndMessage { Entity = released[i] });
            }
        }

        internal void ApplyBegin(PlacementHoldBeginMessage message)
        {
            if (message == null || message.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped begin with no live entity.");
                return;
            }

            var obj = message.Entity;
            var mover = message.Mover;
            if (mover == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped begin for entity="
                    + PlacementInterop.Describe(obj) + " with no holder identity.");
                return;
            }

            var local = IsLocalMover(mover);
            _holders[obj] = mover;
            CoopPlugin.Log.LogInfo("[placement-hold] begin entity="
                + PlacementInterop.Describe(obj) + " mover=" + mover
                + " local=" + local + " stopSnap=" + message.StopSnap
                + " flipped=" + message.IsFlipped + ".");

            if (local)
            {
                // Our own hold is already being moved by vanilla; nothing to render.
                return;
            }

            if (ReferenceEquals(_localObject, obj))
            {
                // We optimistically grabbed the same object; the host gave it to someone else.
                CoopPlugin.Log.LogInfo("[placement-hold] losing race for entity="
                    + PlacementInterop.Describe(obj) + ".");
                CancelLocalMove();
            }

            if (_remote.ContainsKey(obj))
            {
                return;
            }

            if (!obj.GetIsMovingObject())
            {
                StartRemote(obj, message);
            }
            else
            {
                CoopPlugin.Log.LogWarning("[placement-hold] could not start remote hold entity="
                    + PlacementInterop.Describe(obj) + " because it is already moving locally.");
            }
        }

        internal void ApplyEnd(InteractableObject obj)
        {
            if (obj == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] dropped end with no live entity.");
                return;
            }

            CoopPlugin.Log.LogInfo("[placement-hold] end entity="
                + PlacementInterop.Describe(obj) + ".");
            _holders.Remove(obj);
            if (_remote.TryGetValue(obj, out var hold))
            {
                StopRemote(hold);
                _remote.Remove(obj);
            }
        }

        /// <summary>Maintenance only: the target and validity evaluation are driven by the game's
        /// own Update through <see cref="BeginRemoteUpdate"/>.</summary>
        internal void Tick()
        {
            if (_remote.Count == 0)
            {
                return;
            }

            var stale = new List<InteractableObject>();
            foreach (var pair in _remote)
            {
                var hold = pair.Value;
                if (hold.Obj == null)
                {
                    // The identity module destroyed the object while its hold was still mirrored.
                    // Drop the ghost; the authoritative end message (if any) is now a no-op.
                    CoopPlugin.Log.LogWarning("[placement-hold] remote hold lost its live entity; "
                        + "releasing the ghost.");
                    StopRemote(hold);
                    stale.Add(pair.Key);
                    continue;
                }

                // Keep the empty packaging box hidden for as long as the piece is being moved,
                // even if the box channel rebinds the object to a fresh package mid-hold.
                HidePackage(hold);
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _remote.Remove(stale[i]);
            }
        }

        /// <summary>Runs in the prefix of the remote object's Update. This is the same point where
        /// the local player's controller would have set the target from its camera, so vanilla's
        /// lerp, snapping, and validity all run on the fresh target in the same frame.</summary>
        internal bool BeginRemoteUpdate(InteractableObject obj)
        {
            if (obj == null || !_remote.TryGetValue(obj, out var hold) || hold.Obj == null)
            {
                return false;
            }

            _savedStopSnap = PlacementInterop.ReadStopSnap();
            _remoteUpdateActive = true;
            PlacementInterop.SetStopSnap(hold.StopSnap);
            BeginRemotePreviewUpdate();
            UpdateRemoteAim(hold);
            return true;
        }

        internal void EndRemoteUpdate()
        {
            if (!_remoteUpdateActive)
            {
                return;
            }

            _remoteUpdateActive = false;
            PlacementInterop.SetStopSnap(_savedStopSnap);
            EndRemotePreviewUpdate();
        }

        /// <summary>Runs after the remote object's own game Update. The game has now computed the
        /// exact snapped pose and m_IsMovingObjectValidState; the preview is parented to that
        /// object, so only the color needs the game result.</summary>
        internal void ApplyRemoteUpdate(InteractableObject obj)
        {
            if (obj == null || !_remote.TryGetValue(obj, out var hold) || hold.Material == null)
            {
                return;
            }

            var valid = PlacementInterop.ReadMoveValidity(obj);
            if (valid == hold.Valid)
            {
                return;
            }

            hold.Valid = valid;
            CoopPlugin.Log.LogInfo("[placement-hold] remote valid entity="
                + PlacementInterop.Describe(obj) + " valid=" + valid + ".");
            SetPreviewColor(hold.Material, PlacementInterop.FindShelfManager(), valid);
        }

        private void UpdateRemoteAim(RemoteHold hold)
        {
            if (PresenceApi.TryGetLatestPeerCameraForHolder(hold.Mover.Id,
                out var cameraPosition, out var cameraRotation))
            {
                hold.CameraPosition = cameraPosition;
                hold.CameraRotation = cameraRotation;
                hold.HasCamera = true;
            }

            if (!hold.HasCamera)
            {
                if (!hold.AimUnavailableLogged)
                {
                    hold.AimUnavailableLogged = true;
                    CoopPlugin.Log.LogWarning("[placement-hold] no camera sample for mover="
                        + hold.Mover + " entity="
                        + PlacementInterop.Describe(hold.Obj)
                        + "; keeping the last inferred target.");
                }
                return;
            }

            ResolveMoveTarget(hold.Obj, hold.CameraPosition, hold.CameraRotation,
                hold.RayDistance, out var target, out var normal);
            if (target.y < 0f)
            {
                target.y = 0f;
            }

            hold.Obj.SetTargetMovePosition(target, normal, null);
        }

        private static void ResolveMoveTarget(InteractableObject obj, Vector3 cameraPosition,
            Quaternion cameraRotation, float rayDistance, out Vector3 target, out Vector3 normal)
        {
            var forward = cameraRotation * Vector3.forward;
            var up = cameraRotation * Vector3.up;
            var mask = obj != null && obj.m_IsDecorationVertical
                ? LayerMask.GetMask("DecorationBlocker", "DecorationBlockerRaycast")
                : LayerMask.GetMask("ShopModel", "Ground", "Glass", "Obstacles");
            var reach = (float.IsNaN(rayDistance) || float.IsInfinity(rayDistance)
                || rayDistance <= 0f ? DefaultRayDistance : rayDistance) * MoveRayReach;
            if (mask != 0 && Physics.Raycast(new Ray(cameraPosition, forward), out var hit,
                reach, mask))
            {
                target = hit.point;
                normal = hit.normal;
                return;
            }

            // Vanilla SetMovePositionToCamera only updates the target. Keeping the previous
            // m_HitNormal matters for vertical pieces, whose Update derives their rotation from it.
            target = cameraPosition + forward * PreviewDistance + up * PreviewOffsetUp;
            normal = PlacementInterop.ReadHitNormal(obj);
        }

        private static void SetPreviewColor(Material material, ShelfManager manager, bool valid)
        {
            if (material == null || manager == null || !material.HasProperty(ColorId))
            {
                return;
            }

            material.SetColor(ColorId, valid
                ? manager.m_PreviewMeshValidColor : manager.m_PreviewMeshInvalidColor);
        }

        private static PlacementHoldBeginMessage BuildBeginMessage(InteractableObject obj)
        {
            var message = new PlacementHoldBeginMessage
            {
                Entity = obj,
                StopSnap = PlacementInterop.ReadStopSnap(),
                IsFlipped = PlacementInterop.ReadIsFlipped(obj),
                RayDistance = PlacementInterop.ReadLocalRayDistance(),
            };
            if (PlacementInterop.TryGetLocalCamera(out var position, out var rotation))
            {
                message.HasCamera = true;
                message.CameraPosition = position;
                message.CameraRotation = rotation;
            }

            return message;
        }

        private static PlacementHoldBeginRequestMessage CreateBeginRequest(
            PlacementHoldBeginMessage source)
        {
            // The request carries only what the mover observed about its own hold. The host stamps
            // the holder from the authenticated sender, so a sender cannot name someone else.
            var request = new PlacementHoldBeginRequestMessage { Entity = source.Entity };
            CopyBeginState(source, request);
            return request;
        }

        private static void CopyBeginState(PlacementHoldBeginMessage source,
            PlacementHoldBeginMessage destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            destination.HasCamera = source.HasCamera;
            destination.CameraPosition = source.CameraPosition;
            destination.CameraRotation = source.CameraRotation;
            destination.RayDistance = source.RayDistance;
            destination.StopSnap = source.StopSnap;
            destination.IsFlipped = source.IsFlipped;
        }

        internal void Reset()
        {
            foreach (var pair in _remote)
            {
                StopRemote(pair.Value);
            }

            _remote.Clear();
            _holders.Clear();
            _localObject = null;
            _localBeginSent = false;
            _localSentRotation = Quaternion.identity;
            _localStopSnap = false;
            _localIsFlipped = false;
            _remotePreviewCount = 0;
            SetDashedLines(false);
        }

        /// <summary>This machine's roster identity, used as the host-side holder of a local hold.</summary>
        private static PlayerRef LocalPlayer => PeerHandle.Self().Player;

        private void SendToHost(INetMessage message) => _send(PeerHandle.Host(), message);

        private static bool IsLocalMover(PlayerRef mover) => mover != null && mover.IsSelf;

        private static bool IsSamePlayer(PlayerRef owner, ICoopPeer peer)
            => owner != null && peer != null && owner.Equals(peer.Player);

        private void StartRemote(InteractableObject obj, PlacementHoldBeginMessage message)
        {
            var originalLayer = obj.gameObject.layer;
            var validArea = obj.m_MoveStateValidArea;
            var originalValidAreaActive = validArea != null && validArea.gameObject.activeSelf;
            var originalBoxEnabled = obj.m_BoxCollider != null && obj.m_BoxCollider.enabled;
            var originalList = CaptureColliderStates(obj);

            // Mirror StartMoveObject's move-state transitions, minus local input/UI/sound.
            PlacementInterop.WakeSettledBoxesAbove(obj);
            PlacementInterop.SetMovingObject(obj, true);
            PlacementInterop.SetMoveValidity(obj, false);
            PlacementInterop.SetSnappingPos(obj, false);
            PlacementInterop.ClearLastHitTransform(obj);
            PlacementInterop.ClearVerticalPlacementState(obj);
            PlacementInterop.SetOriginalLayer(obj, originalLayer);
            obj.SetTargetMovePosition(obj.transform.position, PlacementInterop.ReadHitNormal(obj),
                null);
            var ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycast >= 0)
            {
                obj.gameObject.layer = ignoreRaycast;
            }

            SetColliders(obj, false);
            PlacementInterop.SetStoredBoxShelfMoving(obj, true);
            if (validArea != null)
            {
                validArea.gameObject.SetActive(false);
            }

            // An unboxed object is still inactive (boxed) on observers until the placement delta
            // arrives; activate it so the preview parented to it is actually drawn.
            obj.gameObject.SetActive(true);

            // The piece was unboxed to move it, so the empty packaging box must not linger in the
            // world next to the ghost. Hide the specific package we unboxed from and restore it on
            // release only if the piece is boxed again.
            var package = PlacementInterop.FindPackagingBoxFor(obj);
            if (package != null)
            {
                package.gameObject.SetActive(false);
            }

            var manager = PlacementInterop.FindShelfManager();
            var ghost = CreateGhost(obj, manager, out var material);
            var hold = new RemoteHold
            {
                Obj = obj,
                Ghost = ghost,
                Material = material,
                Mover = message.Mover,
                OriginalLayer = originalLayer,
                OriginalValidAreaActive = originalValidAreaActive,
                OriginalBoxColliderEnabled = originalBoxEnabled,
                OriginalBoxColliderList = originalList,
                Package = package != null ? package.gameObject : null,
                StopSnap = message.StopSnap,
                IsFlipped = message.IsFlipped,
                RayDistance = message.RayDistance > 0f && !float.IsNaN(message.RayDistance)
                    && !float.IsInfinity(message.RayDistance)
                    ? message.RayDistance : DefaultRayDistance,
                CameraPosition = message.CameraPosition,
                CameraRotation = message.CameraRotation,
                HasCamera = message.HasCamera,
            };
            PlacementInterop.SetIsFlipped(obj, message.IsFlipped);
            _remote[obj] = hold;
            _remotePreviewCount++;
            if (_remotePreviewCount == 1)
            {
                SetDashedLines(true);
            }

            CoopPlugin.Log.LogInfo("[placement-hold] remote begin entity="
                + PlacementInterop.Describe(obj) + " mover=" + message.Mover
                + " preview=" + (ghost != null) + " material=" + (material != null)
                + " pickupMesh=" + (obj.m_PickupObjectMesh != null)
                + " package=" + (package != null) + " camera=" + message.HasCamera
                + " stopSnap=" + message.StopSnap + " flipped=" + message.IsFlipped + ".");
        }

        private void StopRemote(RemoteHold hold)
        {
            if (hold.Obj != null)
            {
                PlacementInterop.SetMovingObject(hold.Obj, false);
                PlacementInterop.SetSnappingPos(hold.Obj, false);
                PlacementInterop.SetStoredBoxShelfMoving(hold.Obj, false);
                hold.Obj.gameObject.layer = hold.OriginalLayer;
                RestoreColliders(hold.Obj, hold.OriginalBoxColliderEnabled,
                    hold.OriginalBoxColliderList);
                if (hold.Obj.m_MoveStateValidArea != null)
                {
                    hold.Obj.m_MoveStateValidArea.gameObject.SetActive(
                        hold.OriginalValidAreaActive);
                }

                if (!hold.Obj.m_IsDecorationVertical)
                {
                    var position = hold.Obj.transform.position;
                    position.y = 0f;
                    hold.Obj.transform.position = position;
                }

                // Restore visibility from the object's *current* boxed state. Restoring the state
                // captured when the hold began re-showed a piece that was boxed (or placed) during
                // the hold, which left the furniture visible in the world while also boxed.
                hold.Obj.gameObject.SetActive(!hold.Obj.GetIsBoxedUp());

                var boxed = hold.Obj.GetIsBoxedUp();
                var current = hold.Obj.GetPackagingBoxShelf();
                if (current != null)
                {
                    current.gameObject.SetActive(boxed);
                }
                else if (hold.Package != null)
                {
                    hold.Package.SetActive(boxed);
                }
            }

            if (hold.Ghost != null)
            {
                UnityEngine.Object.Destroy(hold.Ghost);
            }

            // The preview material is cloned per hold; destroy it with the ghost so repeated
            // holds do not accumulate native materials.
            if (hold.Material != null)
            {
                UnityEngine.Object.Destroy(hold.Material);
                hold.Material = null;
            }

            if (_remotePreviewCount > 0)
            {
                _remotePreviewCount--;
            }

            if (_remotePreviewCount == 0 && _localObject == null)
            {
                SetDashedLines(false);
            }
        }

        private static void HidePackage(RemoteHold hold)
        {
            if (hold.Obj == null)
            {
                return;
            }

            var package = PlacementInterop.FindPackagingBoxFor(hold.Obj);
            if (package == null)
            {
                return;
            }

            hold.Package = package.gameObject;
            if (package.gameObject.activeSelf)
            {
                package.gameObject.SetActive(false);
            }
        }

        private static bool[] CaptureColliderStates(InteractableObject obj)
        {
            if (obj.m_BoxColliderList == null)
            {
                return Array.Empty<bool>();
            }

            var states = new bool[obj.m_BoxColliderList.Count];
            for (var i = 0; i < states.Length; i++)
            {
                states[i] = obj.m_BoxColliderList[i] != null
                    && obj.m_BoxColliderList[i].enabled;
            }

            return states;
        }

        private static void SetColliders(InteractableObject obj, bool enabled)
        {
            if (obj.m_BoxCollider != null)
            {
                obj.m_BoxCollider.enabled = enabled;
            }

            for (var i = 0; obj.m_BoxColliderList != null && i < obj.m_BoxColliderList.Count; i++)
            {
                if (obj.m_BoxColliderList[i] != null)
                {
                    obj.m_BoxColliderList[i].enabled = enabled;
                }
            }
        }

        private static void RestoreColliders(InteractableObject obj, bool boxEnabled,
            bool[] listStates)
        {
            if (obj.m_BoxCollider != null)
            {
                obj.m_BoxCollider.enabled = boxEnabled;
            }

            for (var i = 0; obj.m_BoxColliderList != null && i < obj.m_BoxColliderList.Count; i++)
            {
                if (obj.m_BoxColliderList[i] != null)
                {
                    obj.m_BoxColliderList[i].enabled =
                        listStates != null && i < listStates.Length && listStates[i];
                }
            }
        }

        private void CancelLocalMove()
        {
            var obj = _localObject;
            var position = _localBeforePosition;
            var rotation = _localBeforeRotation;
            _localObject = null;
            _localBeginSent = false;
            _localStopSnap = false;
            _localIsFlipped = false;
            if (obj == null)
            {
                return;
            }

            try
            {
                OnPlacedMovedObjectMethod?.Invoke(obj, null);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("placement hold cancel failed: " + error.Message);
            }

            if (obj == null)
            {
                return;
            }

            obj.transform.SetPositionAndRotation(position, rotation);
            var body = obj.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = position;
                body.rotation = rotation;
            }

            if (_remote.Count > 0)
            {
                SetDashedLines(true);
            }
        }

        private static GameObject CreateGhost(InteractableObject obj, ShelfManager manager,
            out Material material)
        {
            material = null;
            if (obj == null || manager == null || manager.m_MoveObjectPreviewModel == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] cannot create preview: "
                    + (manager == null ? "no ShelfManager" : "no preview model") + " entity="
                    + PlacementInterop.Describe(obj) + ".");
                return null;
            }

            var sourceFilter = obj.m_PickupObjectMesh;
            if (sourceFilter == null)
            {
                sourceFilter = obj.GetComponentInChildren<MeshFilter>(true);
            }

            var mesh = sourceFilter != null ? sourceFilter.mesh : null;
            if (mesh == null)
            {
                mesh = sourceFilter?.sharedMesh;
            }

            if (mesh == null)
            {
                CoopPlugin.Log.LogWarning("[placement-hold] cannot create preview: no pickup mesh "
                    + "on entity=" + PlacementInterop.Describe(obj) + ".");
                return null;
            }

            var ghost = UnityEngine.Object.Instantiate(manager.m_MoveObjectPreviewModel.gameObject);
            ghost.name = "CardShopCoop.PlacementPreview";
            var filter = ghost.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = ghost.AddComponent<MeshFilter>();
            }

            var renderer = ghost.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = ghost.AddComponent<MeshRenderer>();
            }

            filter.mesh = mesh;
            var stock = renderer.sharedMaterial
                ?? manager.m_MoveObjectPreviewRenderer?.sharedMaterial;
            if (stock != null)
            {
                material = new Material(stock);
                var slotCount = renderer.sharedMaterials != null
                    && renderer.sharedMaterials.Length > 0
                        ? renderer.sharedMaterials.Length : 1;
                var materials = new Material[slotCount];
                for (var i = 0; i < slotCount; i++)
                {
                    materials[i] = material;
                }

                renderer.sharedMaterials = materials;
                SetPreviewColor(material, manager, false);
            }
            else
            {
                CoopPlugin.Log.LogWarning("[placement-hold] preview renderer has no stock material "
                    + "for entity=" + PlacementInterop.Describe(obj) + ".");
            }

            renderer.enabled = true;
            var anchor = sourceFilter != null ? sourceFilter.transform : obj.transform;
            ghost.transform.SetParent(null, true);
            ghost.transform.position = anchor.position;
            ghost.transform.rotation = anchor.rotation;
            ghost.transform.localScale = anchor.lossyScale + Vector3.one * 0.001f;
            ghost.transform.SetParent(obj.transform, true);
            ghost.SetActive(true);
            return ghost;
        }

        private static void SetDashedLines(bool visible)
        {
            // The game's static helper dereferences CSingleton<ShelfManager>.Instance, and that
            // getter returns null for every singleton once the application is closing
            // (CSingleton.isQuitting). Our cached scene manager is still alive at that point, so
            // FindShelfManager alone does not catch it and the call throws inside the game method.
            // The dashed lines belong to the scene being torn down, so there is nothing to restore
            // during quit and the correct action is to skip the call.
            if (CSingleton<ShelfManager>.IsQuitting || PlacementInterop.FindShelfManager() == null)
            {
                return;
            }

            ShelfManager.SetAllDashedLineVisibility(visible);
        }
    }
}
