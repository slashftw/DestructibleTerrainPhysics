using UnityEngine;
using UnityEngine.InputSystem;

namespace slash_DTP
{
    /// <summary>
    /// Minimal pointer-input singleton for the destructible terrain demo.
    ///
    /// Reads <see cref="UnityEngine.InputSystem.Pointer.current"/>, which unifies
    /// mouse (editor Game view / desktop), touch (device / Device Simulator) and
    /// pen — so the same code digs in all of them.
    ///
    /// Auto-bootstraps: if no InputManager exists when play starts, one is created
    /// automatically, so callers never get a null <see cref="Instance"/>.
    /// </summary>
    public sealed class InputManager : MonoBehaviour
    {
        public static InputManager Instance { get; private set; }

        /// <summary>Screen-space pixel position of the active pointer.</summary>
        public Vector2 PointerPosition { get; private set; }

        /// <summary>True for one frame when the pointer is pressed (tap / left-click).</summary>
        public bool PointerPressed { get; private set; }

        /// <summary>True while the pointer is held down.</summary>
        public bool PointerHeld { get; private set; }

        /// <summary>
        /// Pointer position projected into world space on the 2D plane via
        /// <paramref name="cam"/>. Feed the result straight to
        /// <see cref="DestructibleTerrain.EraseCircle"/>.
        /// </summary>
        public Vector2 PointerWorldPosition(Camera cam)
        {
            Vector3 screen = PointerPosition;
            screen.z = -cam.transform.position.z; // distance to the z=0 plane (orthographic 2D)
            return cam.ScreenToWorldPoint(screen);
        }

        /// <summary>
        /// The <see cref="DestructibleTerrain"/> whose collider sits under
        /// <paramref name="world2D"/> — mainland or falling chunk — or
        /// <paramref name="fallback"/> when the pointer is over no terrain.
        /// </summary>
        public static DestructibleTerrain TerrainUnderPointer(Vector2 world2D, DestructibleTerrain fallback = null)
        {
            Collider2D hit = Physics2D.OverlapPoint(world2D);
            if (hit != null && hit.TryGetComponent(out ChunkRef cr) && cr.owner != null) return cr.owner;
            return fallback;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("InputManager (auto)");
            DontDestroyOnLoad(go);
            go.AddComponent<InputManager>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void Update()
        {
            // Prefer an active touch (device / Device Simulator). Otherwise read
            // the mouse (editor Game view / desktop). Reading the devices directly
            // avoids Pointer.current going stale on a Touchscreen the Simulator
            // added, which would otherwise ignore the mouse in the Game view.
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed)
            {
                PointerPosition = touch.primaryTouch.position.ReadValue();
                PointerPressed = touch.primaryTouch.press.wasPressedThisFrame;
                PointerHeld = true;
                return;
            }

            var mouse = Mouse.current;
            if (mouse == null) return;
            PointerPosition = mouse.position.ReadValue();
            PointerPressed = mouse.leftButton.wasPressedThisFrame;
            PointerHeld = mouse.leftButton.isPressed;
        }
    }
}
