using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Decoration;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardShopCoop.Modules.World
{
    /// <summary>Stable identity for a placement hold. Placement objects use their placement key;
    /// decorations use the host-assigned decoration id behind a high flag bit.</summary>
    internal static class PlacementHoldKey
    {
        private const long DecorationFlag = 1L << 62;

        internal static bool TryGet(InteractableObject obj, out long key)
        {
            key = 0;
            if (obj == null)
            {
                return false;
            }

            if (DecorationInterop.IsDecoration(obj))
            {
                if (!DecorationInterop.TryGetHoldId(obj, out var decorationId) || decorationId <= 0)
                {
                    return false;
                }

                key = DecorationFlag | decorationId;
                return true;
            }

            if (!PlacementApi.TryMakeObjectKey(PlacementInterop.FindKind(obj), obj,
                out var placementKey))
            {
                return false;
            }

            key = placementKey;
            return true;
        }

        internal static InteractableObject Resolve(long key)
        {
            if ((key & DecorationFlag) != 0)
            {
                return DecorationInterop.TryResolveHoldId(key & ~DecorationFlag, out var decoration)
                    ? decoration : null;
            }

            return PlacementApi.ResolveObjectByKey((int)key) as InteractableObject;
        }
    }

    /// <summary>
    /// Tracks who is holding which placed object for placement. Only the hold edge is networked:
    /// observers rebuild the vanilla move preview from the holder's already-streamed camera, so no
    /// object pose is ever sent. The host grants the first hold for an object; a competing hold is
    /// blocked at start or rolled back when the winning grant is observed.
    /// </summary>
    internal sealed class PlacementHoldInteraction
    {
        private const float PreviewDistance = 3f;
        private const float PreviewOffsetUp = -0.1f;

        private static readonly FieldInfo MovingObjectField =
            AccessTools.Field(typeof(InteractableObject), "m_IsMovingObject");
        private static readonly FieldInfo SnappingPosField =
            AccessTools.Field(typeof(InteractableObject), "m_IsSnappingPos");
        private static readonly MethodInfo OnPlacedMovedObjectMethod =
            AccessTools.Method(typeof(InteractableObject), "OnPlacedMovedObject");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private sealed class RemoteHold
        {
            internal InteractableObject Obj;
            internal GameObject Ghost;
            internal Material Material;
            internal int MoverConnectionId;
            internal int OriginalLayer;
            internal ShelfMoveStateValidArea OriginalShelfMove;
            /// <summary>The empty packaging box the object was unboxed from, hidden while the
            /// ghost preview stands in for the piece.</summary>
            internal GameObject Package;
            internal readonly List<Renderer> HiddenRenderers = new();
        }

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<int, INetMessage> _send;
        private readonly Dictionary<long, int> _holders = new();
        private readonly Dictionary<long, RemoteHold> _remote = new();

        private long _localKey;
        private InteractableObject _localObject;
        private Vector3 _localBeforePosition;
        private Quaternion _localBeforeRotation;
        private Quaternion _localSentRotation;

        internal PlacementHoldInteraction(bool host, Action<INetMessage> broadcast,
            Action<int, INetMessage> send)
        {
            _host = host;
            _broadcast = broadcast;
            _send = send;
        }

        /// <summary>False when another player already holds the object, so the local move must be
        /// blocked. Untracked objects keep vanilla behavior.</summary>
        internal bool IsAllowed(InteractableObject obj)
        {
            if (obj == null || !PlacementHoldKey.TryGet(obj, out var key))
            {
                return true;
            }

            if (_holders.TryGetValue(key, out var mover) && !IsLocalMover(mover))
            {
                CoopPlugin.Log.LogInfo("[placement-hold] blocked key=" + key + " held by mover="
                    + mover + ".");
                return false;
            }

            return true;
        }

        internal void LocalStarted(InteractableObject obj)
        {
            if (obj == null || _localKey != 0 || !PlacementHoldKey.TryGet(obj, out var key))
            {
                return;
            }

            if (_holders.TryGetValue(key, out var mover) && !IsLocalMover(mover))
            {
                return;
            }

            _localKey = key;
            _localObject = obj;
            _localBeforePosition = obj.transform.position;
            _localBeforeRotation = obj.transform.rotation;
            CoopPlugin.Log.LogInfo("[placement-hold] local begin key=" + key + " host=" + _host + ".");

            if (_host)
            {
                _holders[key] = 0;
                _broadcast(new PlacementHoldBeginMessage { HoldKey = key, MoverConnectionId = 0 });
            }
            else
            {
                _send(1, new PlacementHoldBeginRequestMessage { HoldKey = key });
            }

            // The mover may already have aimed the object (StartMoveObject orients some objects),
            // so publish the starting rotation; observers then follow rotate events from there.
            _localSentRotation = obj.transform.rotation;
            PublishRotation(_localSentRotation);
        }

        internal void LocalEnded(InteractableObject obj)
        {
            if (_localKey == 0 || !ReferenceEquals(obj, _localObject))
            {
                return;
            }

            var key = _localKey;
            _localKey = 0;
            _localObject = null;
            CoopPlugin.Log.LogInfo("[placement-hold] local end key=" + key + " host=" + _host + ".");
            if (_host)
            {
                if (_holders.TryGetValue(key, out var mover) && mover == 0)
                {
                    _holders.Remove(key);
                }

                _broadcast(new PlacementHoldEndMessage { HoldKey = key });
            }
            else
            {
                _send(1, new PlacementHoldEndRequestMessage { HoldKey = key });
            }
        }

        /// <summary>Event-driven rotation driver: called after the local mover's rotate input
        /// actually changed the held object, and only then forwarded to observers.</summary>
        internal void LocalRotated(InteractableObject obj)
        {
            if (_localKey == 0 || !ReferenceEquals(obj, _localObject))
            {
                return;
            }

            var rotation = obj.transform.rotation;
            if (Quaternion.Angle(rotation, _localSentRotation) < 0.01f)
            {
                return;
            }

            _localSentRotation = rotation;
            CoopPlugin.Log.LogInfo("[placement-hold] local rotate key=" + _localKey + " yaw="
                + rotation.eulerAngles.y.ToString("F1") + ".");
            PublishRotation(rotation);
        }

        private void PublishRotation(Quaternion rotation)
        {
            if (_localKey == 0)
            {
                return;
            }

            if (_host)
            {
                _broadcast(new PlacementHoldRotationMessage
                {
                    HoldKey = _localKey,
                    Rotation = rotation,
                });
            }
            else
            {
                _send(1, new PlacementHoldRotationRequestMessage
                {
                    HoldKey = _localKey,
                    Rotation = rotation,
                });
            }
        }

        /// <summary>Applies a mover's rotation to its rendered ghost on an observer.</summary>
        internal void ApplyRotation(PlacementHoldRotationMessage message)
        {
            if (message == null
                || !_remote.TryGetValue(message.HoldKey, out var hold) || hold.Obj == null)
            {
                return;
            }

            hold.Obj.transform.rotation = message.Rotation;
        }

        /// <summary>True when <paramref name="connectionId"/> owns the hold for
        /// <paramref name="key"/> (0 = the host), used to validate a rotate request.</summary>
        internal bool IsHeldBy(long key, int connectionId)
            => _holders.TryGetValue(key, out var mover) && mover == connectionId;

        /// <summary>Host authority: grants the hold to <paramref name="connectionId"/> when the
        /// object is free, returning the broadcast to send.</summary>
        internal bool HostGrant(long key, int connectionId, out PlacementHoldBeginMessage granted)
        {
            granted = null;
            if (_holders.TryGetValue(key, out var existing) && existing != connectionId)
            {
                return false;
            }

            _holders[key] = connectionId;
            granted = new PlacementHoldBeginMessage
            {
                HoldKey = key,
                MoverConnectionId = connectionId,
            };
            return true;
        }

        internal bool HostRelease(long key, int connectionId)
        {
            if (!_holders.TryGetValue(key, out var existing) || existing != connectionId)
            {
                return false;
            }

            _holders.Remove(key);
            return true;
        }

        /// <summary>Ends the hold for <paramref name="key"/> when <paramref name="connectionId"/>
        /// owns it, and tells the peers. Used when another action (boxing up) supersedes the hold.</summary>
        internal bool EndIfHeldBy(long key, int connectionId)
        {
            if (!_holders.TryGetValue(key, out var mover) || mover != connectionId)
            {
                return false;
            }

            ApplyEnd(key);
            _broadcast(new PlacementHoldEndMessage { HoldKey = key });
            return true;
        }

        /// <summary>Releases everything a disconnected peer held, appending each key to
        /// <paramref name="released"/> so the caller can broadcast the end.</summary>
        internal void ReleaseMover(int connectionId, List<long> released)
        {
            released.Clear();
            foreach (var pair in _holders)
            {
                if (pair.Value == connectionId)
                {
                    released.Add(pair.Key);
                }
            }

            for (var i = 0; i < released.Count; i++)
            {
                // Stop our local render of the disconnected peer's hold as well as telling the
                // other peers; otherwise the ghost and the object's move state stick.
                ApplyEnd(released[i]);
                _broadcast(new PlacementHoldEndMessage { HoldKey = released[i] });
            }
        }

        internal void ApplyBegin(PlacementHoldBeginMessage message)
        {
            if (message == null)
            {
                return;
            }

            var key = message.HoldKey;
            var local = IsLocalMover(message.MoverConnectionId);
            _holders[key] = message.MoverConnectionId;
            CoopPlugin.Log.LogInfo("[placement-hold] begin key=" + key + " mover="
                + message.MoverConnectionId + " local=" + local + ".");

            if (local)
            {
                // Our own hold is already being moved by vanilla; nothing to render.
                return;
            }

            if (_localKey == key)
            {
                // We optimistically grabbed the same object; the host gave it to someone else.
                CoopPlugin.Log.LogInfo("[placement-hold] losing race for key=" + key + ".");
                CancelLocalMove();
            }

            if (_remote.ContainsKey(key))
            {
                return;
            }

            var obj = PlacementHoldKey.Resolve(key);
            if (obj != null && !obj.GetIsMovingObject())
            {
                StartRemote(obj, key, message.MoverConnectionId);
            }
            else
            {
                CoopPlugin.Log.LogWarning("[placement-hold] could not start remote hold key=" + key
                    + " object=" + (obj != null) + ".");
            }
        }

        internal void ApplyEnd(long key)
        {
            CoopPlugin.Log.LogInfo("[placement-hold] end key=" + key + ".");
            _holders.Remove(key);
            if (_remote.TryGetValue(key, out var hold))
            {
                StopRemote(hold);
                _remote.Remove(key);
            }
        }

        internal void Tick()
        {
            if (_remote.Count == 0)
            {
                return;
            }

            var manager = PlacementInterop.FindShelfManager();
            foreach (var pair in _remote)
            {
                var hold = pair.Value;
                if (hold.Obj == null)
                {
                    continue;
                }

                // Keep the empty packaging box hidden for as long as the piece is being moved,
                // even if the box channel rebinds the object to a fresh package mid-hold.
                HidePackage(hold);

                if (!PresenceApi.TryGetPeerCameraForHolder(hold.MoverConnectionId, out var cameraPosition,
                    out var cameraRotation))
                {
                    continue;
                }

                var target = cameraPosition + cameraRotation * Vector3.forward * PreviewDistance
                    + cameraRotation * Vector3.up * PreviewOffsetUp;
                if (target.y < 0f)
                {
                    target.y = 0f;
                }

                // The game's own move update snaps a valid placement to nearby snap points, which
                // reads as lag on an observer that is not the one aiming. Keep it in free-lerp mode
                // so it tracks the holder's camera every frame.
                SnappingPosField?.SetValue(hold.Obj, false);
                hold.Obj.SetTargetMovePosition(target, cameraRotation * Vector3.up, null);
                if (hold.Material != null && manager != null)
                {
                    hold.Material.SetColor(ColorId, PlacementInterop.ReadMoveValidity(hold.Obj)
                        ? manager.m_PreviewMeshValidColor : manager.m_PreviewMeshInvalidColor);
                }
            }
        }

        internal void Reset()
        {
            foreach (var pair in _remote)
            {
                StopRemote(pair.Value);
            }

            _remote.Clear();
            _holders.Clear();
            _localKey = 0;
            _localObject = null;
            _localSentRotation = Quaternion.identity;
        }

        private static bool IsLocalMover(int connectionId)
            => connectionId == CoopCore.LocalConnectionId;

        private void StartRemote(InteractableObject obj, long key, int moverConnectionId)
        {
            MovingObjectField?.SetValue(obj, true);
            var originalLayer = obj.gameObject.layer;
            SetColliders(obj, false);
            var ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycast >= 0)
            {
                obj.gameObject.layer = ignoreRaycast;
            }

            // Hide the object's own meshes so the see-through ghost is the only representation,
            // matching the mover's translucent preview instead of a solid object under a ghost.
            var hidden = new List<Renderer>();
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled)
                {
                    renderers[i].enabled = false;
                    hidden.Add(renderers[i]);
                }
            }

            // An unboxed object is still inactive (boxed) on observers until the placement delta
            // arrives; activate it so the ghost parented to it is actually drawn.
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
            CoopPlugin.Log.LogInfo("[placement-hold] remote begin key=" + key + " mover="
                + moverConnectionId + " ghost=" + (ghost != null) + " pickupMesh="
                + (obj.m_PickupObjectMesh != null) + " material=" + (material != null)
                + " package=" + (package != null) + ".");
            var hold = new RemoteHold
            {
                Obj = obj,
                Ghost = ghost,
                Material = material,
                MoverConnectionId = moverConnectionId,
                OriginalLayer = originalLayer,
                OriginalShelfMove = obj.m_ShelfMoveStateValidArea,
                Package = package != null ? package.gameObject : null,
            };
            // Free-lerp on observers: the game snaps a valid handheld placement to nearby shelf
            // snap points, which makes the remote copy sit still until the aim moves far enough.
            obj.m_ShelfMoveStateValidArea = null;
            hold.HiddenRenderers.AddRange(hidden);
            _remote[key] = hold;
        }

        private static void StopRemote(RemoteHold hold)
        {
            if (hold.Obj != null)
            {
                MovingObjectField?.SetValue(hold.Obj, false);
                SnappingPosField?.SetValue(hold.Obj, false);
                hold.Obj.m_ShelfMoveStateValidArea = hold.OriginalShelfMove;
                hold.Obj.gameObject.layer = hold.OriginalLayer;
                SetColliders(hold.Obj, true);
                for (var i = 0; i < hold.HiddenRenderers.Count; i++)
                {
                    if (hold.HiddenRenderers[i] != null)
                    {
                        hold.HiddenRenderers[i].enabled = true;
                    }
                }

                hold.HiddenRenderers.Clear();
                // Restore visibility from the object's *current* boxed state. Restoring the state
                // captured when the hold began re-showed a piece that was boxed (or placed) during
                // the hold, which left the furniture visible in the world while also boxed.
                hold.Obj.gameObject.SetActive(!hold.Obj.GetIsBoxedUp());
            }

            // Bring the packaging box back only when the piece is boxed again; a placed piece has
            // already retired its package through the placement/box channel. Prefer the object's
            // current package in case it was re-boxed during the hold.
            if (hold.Obj != null)
            {
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

        private void CancelLocalMove()
        {
            var obj = _localObject;
            var position = _localBeforePosition;
            var rotation = _localBeforeRotation;
            _localKey = 0;
            _localObject = null;
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
        }

        private static GameObject CreateGhost(InteractableObject obj, ShelfManager manager,
            out Material material)
        {
            material = null;
            // The pickup mesh may sit under rotated/scaled children; fall back to the object's own
            // MeshFilter when it is missing.
            var sourceFilter = obj.m_PickupObjectMesh;
            if ((sourceFilter == null || sourceFilter.sharedMesh == null)
                && obj.TryGetComponent<MeshFilter>(out var own))
            {
                sourceFilter = own;
            }

            var mesh = sourceFilter != null ? sourceFilter.sharedMesh : null;
            if (mesh == null)
            {
                return null;
            }

            var ghost = new GameObject("CardShopCoop.PlacementGhost");
            ghost.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ghost.AddComponent<MeshRenderer>();
            var source = manager != null ? manager.m_MoveObjectPreviewRenderer : null;
            if (source != null && source.sharedMaterial != null)
            {
                material = new Material(source.sharedMaterial);
                renderer.sharedMaterial = material;

                // The see-through preview must draw over translucent shop geometry (glass walls,
                // windows, doors) instead of being half-hidden behind it. The stock preview
                // material shares the transparent queue with the glass, so force it to the overlay
                // queue and ignore depth like a highlight.
                if (material.HasProperty("_ZWrite"))
                {
                    material.SetFloat("_ZWrite", 0f);
                }

                if (material.HasProperty("_ZTest"))
                {
                    material.SetFloat("_ZTest", (float)CompareFunction.Always);
                }

                material.renderQueue = (int)RenderQueue.Overlay;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = short.MaxValue;
            // Match the game's own move preview: place the ghost at the world pose of the pickup
            // mesh (which can be nested under rotated/scaled children), then reparent to the
            // object preserving world pose. Reading the mesh's local transform instead put the
            // ghost in the wrong place, rotation and scale whenever it was not a direct child.
            var anchor = sourceFilter != null ? sourceFilter.transform : obj.transform;
            ghost.transform.position = anchor.position;
            ghost.transform.rotation = anchor.rotation;
            ghost.transform.localScale = anchor.lossyScale + Vector3.one * 0.001f;
            ghost.transform.SetParent(obj.transform, true);
            return ghost;
        }
    }
}
