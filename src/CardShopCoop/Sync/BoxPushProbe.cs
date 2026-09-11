using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Detects the boxes the LOCAL player is physically pushing. It rides on the player's
    /// walking body, so Unity's collision callbacks fire only while a real contact exists -
    /// the work is O(contacts), never O(all boxes). Remote avatars run with their physics and
    /// colliders disabled, so each machine only ever sees its own player's pushes.
    ///
    /// This component only answers "which loose boxes is my body touching right now". The
    /// engine turns the resulting <see cref="Pushed"/> set into a transient motion stream
    /// (<see cref="BoxEngine.PushTick"/>), which is what actually moves the box on the peers.
    /// </summary>
    public sealed class BoxPushProbe : MonoBehaviour
    {
        // A body can only meaningfully drive a handful of boxes at once; past the cap a new
        // box is ignored until an existing one drops out (see NoteContact).
        private const int MaxPushed = 8;

        private Rigidbody _body;
        private readonly Dictionary<InteractablePackagingBox, float> _lastContact
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly HashSet<InteractablePackagingBox> _pushed
            = new HashSet<InteractablePackagingBox>();
        // Reused per-frame prune list so Tick allocates nothing.
        private readonly List<InteractablePackagingBox> _scratch
            = new List<InteractablePackagingBox>();

        /// <summary>The boxes the local player is currently pushing.</summary>
        public IReadOnlyCollection<InteractablePackagingBox> Pushed
        {
            get
            {
                return _pushed;
            }
        }

        /// <summary>Bind the probe to the player's walking body. The body is the collision
        /// source: contacts with loose boxes are what this tracks.</summary>
        public void Init(Rigidbody body)
        {
            if (body == null)
                throw new ArgumentNullException("body");
            _body = body;
        }

        private void OnCollisionEnter(Collision c)
        {
            NoteContact(c, true);
        }

        private void OnCollisionStay(Collision c)
        {
            NoteContact(c, true);
        }

        private void OnCollisionExit(Collision c)
        {
            NoteContact(c, false);
        }

        private void NoteContact(Collision c, bool contacting)
        {
            if (c == null || c.collider == null)
                return;
            var box = c.collider.GetComponentInParent<InteractablePackagingBox>();
            if (box == null)
                return;
            if (contacting)
            {
                var engine = CoopCore.Instance?.Boxes;
                if (engine == null || !engine.CanLocallyPush(box))
                    return;
                _lastContact[box] = Time.time;
                if (!_pushed.Contains(box) && _pushed.Count >= MaxPushed)
                    return;
                _pushed.Add(box);
            }
            else
            {
                _pushed.Remove(box);
                _lastContact.Remove(box);
            }
        }

        /// <summary>Per-frame maintenance: drop boxes that are destroyed, or that lost contact
        /// and are now at rest. A box that lost contact but is still sliding stays in the set
        /// (its slide-to-rest is carried by the motion stream), so only a genuinely resting box
        /// is released. Runs over a reused scratch list - no per-frame allocation.</summary>
        public void Tick(float dt)
        {
            if (_pushed.Count == 0)
                return;
            // The player's body went away (scene transition): release everything we hold.
            if (_body == null)
            {
                _pushed.Clear();
                _lastContact.Clear();
                return;
            }
            float settleSq = BoxEngine.MotionSettleSpeed * BoxEngine.MotionSettleSpeed;
            _scratch.Clear();
            foreach (var box in _pushed)
            {
                if (box == null) // Unity fake-null: the box was destroyed
                {
                    _scratch.Add(box);
                    continue;
                }
                // Still in contact (fresh) - keep it regardless of speed.
                if (_lastContact.TryGetValue(box, out var last)
                    && Time.time - last <= BoxEngine.PushHysteresis)
                    continue;
                var rb = box.m_Rigidbody;
                bool resting = rb == null || rb.isKinematic || rb.IsSleeping()
                    || rb.velocity.sqrMagnitude < settleSq;
                if (resting)
                    _scratch.Add(box);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                var box = _scratch[i];
                _pushed.Remove(box);
                _lastContact.Remove(box);
            }
        }
    }
}
