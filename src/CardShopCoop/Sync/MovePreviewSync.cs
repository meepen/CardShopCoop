using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Streams only the visual furniture move preview. The real object remains under
    /// ObjMoveSync's settled-pose authority; remote previews are detached meshes and therefore
    /// cannot participate in movement, collision, or index reconciliation.</summary>
    public sealed class MovePreviewSync
    {
        private const byte Start = 0;
        private const byte Update = 1;
        private const byte Stop = 2;
        private const float SendInterval = 1f / 12f;
        private const float ExpireAfter = 1.5f;

        private sealed class RemotePreview
        {
            public int SourceId;
            public int ObjectKey;
            public Component SourceObject;
            public GameObject Ghost;
            public MeshRenderer Renderer;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Valid;
            public float LastReceived;
        }

        private static readonly FieldInfo FiValid = AccessTools.Field(
            typeof(InteractableObject), "m_IsMovingObjectValidState");
        private readonly Dictionary<int, RemotePreview> _remote =
            new Dictionary<int, RemotePreview>();
        private InteractableObject _localObject;
        private int _localKey;
        private int _localSourceId;
        private float _sendTimer;
        private bool _localValid;
        private static readonly FieldInfo FiBoxedObject = AccessTools.Field(
            typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");

        public void Reset()
        {
            _localObject = null;
            _localKey = 0;
            _sendTimer = 0f;
            foreach (var preview in _remote.Values)
                DestroyPreview(preview);
            _remote.Clear();
        }

        public void RemoveSource(int sourceId)
        {
            var remove = new List<int>();
            foreach (var pair in _remote)
                if (pair.Value.SourceId == sourceId)
                    remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++)
                RemoveRemote(remove[i]);
        }

        public void Tick(float dt)
        {
            if (CoopCore.Role == CoopRole.None)
            {
                if (_localObject != null)
                    Reset();
                return;
            }

            if (_localObject != null)
            {
                if (!_localObject || !_localObject.GetIsMovingObject())
                {
                    StopLocal();
                }
                else
                {
                    _sendTimer -= dt;
                    if (_sendTimer <= 0f)
                    {
                        _sendTimer = SendInterval;
                        SendLocal(Update, ReadValid());
                    }
                }
            }

            float now = Time.realtimeSinceStartup;
            var expired = new List<int>();
            foreach (var pair in _remote)
                if (now - pair.Value.LastReceived > ExpireAfter)
                    expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++)
                RemoveRemote(expired[i]);
        }

        public void BeginLocal(InteractableObject obj)
        {
            if (CoopCore.Role == CoopRole.None || obj == null || !obj.GetIsMovingObject())
                return;
            if (!TryFindKey(obj, out int key, out int kind))
                return;
            if (_localObject != null)
                StopLocal();
            _localObject = obj;
            _localKey = key;
            _localSourceId = CoopCore.Role == CoopRole.Host ? 0 : 1;
            _localValid = ReadValid();
            _sendTimer = SendInterval;
            SendLocal(Start, _localValid);
        }

        public void UpdateLocalValidity(bool valid)
        {
            if (_localObject == null)
                return;
            _localValid = valid;
            SendLocal(Update, valid);
        }

        public void EndLocal()
        {
            if (_localObject != null)
                StopLocal();
        }

        public void ApplyRemote(MovePreviewMessage message, int sourceId)
        {
            if (message == null || CoopCore.Role == CoopRole.None)
                return;
            if (sourceId == (CoopCore.Role == CoopRole.Host ? 0 : 1))
                return;
            int key = message.ObjectKey;
            if (key == 0)
                return;

            if (message.Phase == Stop)
            {
                RemoveRemote(keyFor(sourceId, key));
                return;
            }

            int id = keyFor(sourceId, key);
            if (!_remote.TryGetValue(id, out var preview))
            {
                preview = new RemotePreview { SourceId = sourceId, ObjectKey = key };
                _remote[id] = preview;
            }
            preview.SourceId = sourceId;
            preview.ObjectKey = key;
            preview.Position = message.Pos;
            preview.Rotation = message.Rot;
            preview.Valid = message.Valid;
            preview.LastReceived = Time.realtimeSinceStartup;
            EnsureGhost(preview);
            ApplyGhost(preview);
        }

        private static int keyFor(int sourceId, int objectKey)
        {
            unchecked
            {
                return (sourceId * 397) ^ objectKey;
            }
        }

        private void EnsureGhost(RemotePreview preview)
        {
            if (preview.Ghost != null)
                return;
            Component source = ResolvePreviewSource(preview.ObjectKey);
            var obj = source as InteractableObject;
            if (obj == null || obj.m_PickupObjectMesh == null || obj.m_PickupObjectMesh.sharedMesh == null)
                return;
            var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (sm == null || sm.m_MoveObjectPreviewRenderer == null)
                return;

            var ghost = new GameObject("CoopRemoteMovePreview");
            var filter = ghost.AddComponent<MeshFilter>();
            var renderer = ghost.AddComponent<MeshRenderer>();
            filter.sharedMesh = obj.m_PickupObjectMesh.sharedMesh;
            renderer.material = new Material(sm.m_MoveObjectPreviewRenderer.material);
            preview.SourceObject = obj;
            preview.Ghost = ghost;
            preview.Renderer = renderer;
        }

        private static void ApplyGhost(RemotePreview preview)
        {
            if (preview.Ghost == null || preview.Renderer == null)
                return;
            preview.Ghost.transform.SetPositionAndRotation(preview.Position, preview.Rotation);
            if (preview.SourceObject is InteractableObject obj && obj.m_PickupObjectMesh != null)
                preview.Ghost.transform.localScale = obj.m_PickupObjectMesh.transform.lossyScale + Vector3.one * 0.001f;
            var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (sm != null)
                preview.Renderer.material.SetColor("_Color",
                    preview.Valid ? sm.m_PreviewMeshValidColor : sm.m_PreviewMeshInvalidColor);
            preview.Ghost.SetActive(true);
        }

        private void RemoveRemote(int id)
        {
            if (_remote.TryGetValue(id, out var preview))
            {
                DestroyPreview(preview);
                _remote.Remove(id);
            }
        }

        private static void DestroyPreview(RemotePreview preview)
        {
            if (preview.Ghost != null)
                UnityEngine.Object.Destroy(preview.Ghost);
            preview.Ghost = null;
            preview.Renderer = null;
        }

        private void StopLocal()
        {
            SendLocal(Stop, _localValid);
            _localObject = null;
            _localKey = 0;
        }

        private bool ReadValid()
        {
            return _localObject != null && FiValid != null &&
                (bool)FiValid.GetValue(_localObject);
        }

        private void SendLocal(byte phase, bool valid)
        {
            if (_localObject == null || _localKey == 0)
                return;
            var message = new MovePreviewMessage
            {
                Phase = phase,
                ObjectKey = _localKey,
                SourceId = _localSourceId,
                Pos = _localObject.transform.position,
                Rot = _localObject.transform.rotation,
                Valid = valid,
            };
            CoopCore.Instance?.SendMovePreview(message);
        }

        private static bool TryFindKey(InteractableObject obj, out int key, out int kind)
        {
            key = 0;
            kind = -1;
            InteractableObject identityObject = obj;
            if (obj is InteractablePackagingBox_Shelf box)
            {
                identityObject = FiBoxedObject?.GetValue(box) as InteractableObject;
                if (identityObject == null)
                    return false;
            }
            var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (sm == null)
                return false;
            for (int i = 0; i < PopulationSync.KindCount; i++)
            {
                IList list = PopulationSync.GetList(sm, i);
                if (list == null)
                    continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (!ReferenceEquals(list[j], identityObject))
                        continue;
                    if (!PlacedObjectIdentity.TryMakeObjectKey(i, identityObject, out key))
                        return false;
                    kind = i;
                    return true;
                }
            }
            return false;
        }

        private static Component ResolvePreviewSource(int objectKey)
        {
            var identity = ObjMoveSync.ResolveObjectByKey(objectKey) as InteractableObject;
            if (identity == null)
                return null;
            var boxes = UnityEngine.Object.FindObjectsOfType<InteractablePackagingBox_Shelf>();
            for (int i = 0; i < boxes.Length; i++)
            {
                var boxed = FiBoxedObject?.GetValue(boxes[i]) as InteractableObject;
                if (ReferenceEquals(boxed, identity))
                    return boxes[i];
            }
            return identity;
        }
    }
}
