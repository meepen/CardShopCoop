using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Pointer and gamepad controls for the in-panel character preview. Mouse drag pans,
    /// mouse wheel zooms, the right stick pans, the triggers zoom, and R3 recenters. The
    /// component lives on the preview image, so it is only active while the Character tab is
    /// visible; the game's controller navigation keeps the left stick and triggers' face-button
    /// paths untouched.
    /// </summary>
    internal sealed class CoopCharacterPreviewInput : MonoBehaviour, IBeginDragHandler,
        IDragHandler, IEndDragHandler, IScrollHandler
    {
        internal CoopCharacterPreview Preview;
        internal Action Recenter;
        private bool _panning;

        public void OnBeginDrag(PointerEventData eventData)
        {
            // Latch the mode for the whole drag. The event's button can still describe the
            // previous press when a left drag follows a right drag, so use the live button state.
            var mouse = Mouse.current;
            _panning = mouse != null
                ? mouse.rightButton.isPressed
                : eventData.button == PointerEventData.InputButton.Right;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Preview == null)
            {
                return;
            }

            if (_panning)
            {
                Preview.Pan(eventData.delta);
            }
            else
            {
                Preview.Orbit(eventData.delta);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _panning = false;
        }

        public void OnScroll(PointerEventData eventData)
        {
            Preview?.Zoom(eventData.scrollDelta.y * 0.12f);
        }

        private void Update()
        {
            var gamepad = Gamepad.current;
            if (gamepad == null || Preview == null)
            {
                return;
            }

            // Analog stick/trigger input is continuous, so it is sampled while this preview is
            // active. Everything else in the editor remains event-driven.
            var stick = gamepad.rightStick.ReadValue();
            if (stick.sqrMagnitude > 0.01f)
            {
                Preview.OrbitStick(stick, Time.unscaledDeltaTime);
            }

            var zoom = gamepad.rightTrigger.ReadValue() - gamepad.leftTrigger.ReadValue();
            if (Mathf.Abs(zoom) > 0.08f)
            {
                Preview.Zoom(zoom * Time.unscaledDeltaTime * 1.8f);
            }

            if (gamepad.rightStickButton.wasPressedThisFrame)
            {
                Preview.Recenter();
                Recenter?.Invoke();
            }
        }
    }
}
