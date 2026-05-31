using UnityEngine;
using UnityEngine.EventSystems;

namespace slash_DTP
{
    /// <summary>
    /// Demo digger that bridges <see cref="InputManager"/> input to
    /// <see cref="DestructibleTerrain.EraseCircle"/>. Attach to any GameObject
    /// in the scene.
    ///
    /// Behaviour:
    ///   - Only digs while the terrain's <see cref="DestructibleTerrain.canDig"/>
    ///     is enabled.
    ///   - While the pointer is held (touch or left mouse), erases a circle at
    ///     the pointer's world-space position using the terrain's configured
    ///     <see cref="DestructibleTerrain.eraseRadius"/>.
    ///   - Skips digs while the pointer hasn't moved more than
    ///     <see cref="moveThreshold"/> world units since the last successful
    ///     dig — avoids wasted EraseCircle calls when the user is holding still.
    ///   - Suppresses digs while the pointer is over a raycast-blocking UI
    ///     element so HUD buttons / panels remain interactable.
    ///
    /// Hot-swap friendly: assign different terrains / cameras at runtime by
    /// setting the public fields. Both auto-resolve at Start if left blank.
    /// </summary>
    public sealed class Digger : MonoBehaviour
    {
        [Tooltip("Terrain to dig. Auto-resolves to the first DestructibleTerrain in the scene if null.")]
        public DestructibleTerrain terrain;

        [Tooltip("Camera used for screen-to-world projection. Auto-resolves to Camera.main if null.")]
        public Camera cam;

        [Tooltip("If true, digs continuously while the pointer is held. " +
                 "If false, digs once per press (tap-to-dig).")]
        public bool continuous = true;

        [Tooltip("Min world-space distance the pointer must move between successive " +
                 "digs in continuous mode. Prevents thrash when holding still.")]
        [Min(0f)]
        public float moveThreshold = 0.1f;

        [Tooltip("If true, suppresses digs when the pointer is over a UI element.")]
        public bool ignoreOverUI = true;

        static readonly Vector2 NullPos = new Vector2(-9999f, -9999f);
        Vector2 _lastDigPos = NullPos;
        // Terrain latched for the duration of a held stroke. Prevents a momentary
        // gap (pointer over a hole/edge) from flipping the dig to another terrain,
        // which would starve this terrain's detection debounce and re-introduce
        // mid-stroke "seam" bands.
        DestructibleTerrain _strokeTarget;

        void Start()
        {
            if (cam == null) cam = Camera.main;
            if (terrain == null) terrain = FindAnyObjectByType<DestructibleTerrain>();
        }

        void Update()
        {
            if (cam == null || InputManager.Instance == null) return;

            bool active = continuous
                ? InputManager.Instance.PointerHeld
                : InputManager.Instance.PointerPressed;

            if (!active)
            {
                _lastDigPos = NullPos;
                _strokeTarget = null;
                return;
            }

            if (ignoreOverUI && IsPointerOverUI())
            {
                _lastDigPos = NullPos;
                return;
            }

            Vector2 world2D = InputManager.Instance.PointerWorldPosition(cam);

            // Move-threshold gate: skip the EraseCircle call entirely if the
            // pointer hasn't moved enough since the last successful dig. Cheap
            // squared-distance check avoids the per-frame redundant work that
            // would otherwise hit the chunk rebuild pipeline.
            if (continuous &&
                Vector2.SqrMagnitude(world2D - _lastDigPos) < (moveThreshold * moveThreshold))
            {
                return;
            }

            // Dig the terrain under the pointer; over a gap/edge (no terrain) keep
            // digging the last real one so its detection debounce stays warm —
            // kills mid-stroke "seam" bands without ever stranding another terrain.
            // Falls back to the assigned terrain only until the first real hit.
            DestructibleTerrain hit = InputManager.TerrainUnderPointer(world2D);
            if (hit != null) _strokeTarget = hit;
            DestructibleTerrain target = _strokeTarget != null ? _strokeTarget : terrain;

            if (target != null && target.canDig)
            {
                target.EraseCircle(world2D, target.eraseRadius);
                _lastDigPos = world2D;
            }
        }

        static bool IsPointerOverUI()
        {
            if (EventSystem.current == null) return false;
            return EventSystem.current.IsPointerOverGameObject();
        }
    }
}
