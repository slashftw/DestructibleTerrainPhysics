using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
// Disambiguate: Unity.Mathematics also defines a Random type.
using Random = UnityEngine.Random;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

namespace slash_DTP
{
    [RequireComponent(typeof(SpriteRenderer))]
    [RequireComponent(typeof(Rigidbody2D))]
    public class DestructibleTerrain : MonoBehaviour
    {
        // Texture Settings — controls how the source sprite's texture is cloned
        // and prepared for runtime editing. Runtime-mutable settings (filterMode,
        // wrapMode, anisoLevel, pixelsPerUnit) are applied inside CloneTexture().
        // Import-time settings (Read/Write, format, sprite mode, mipmaps,
        // alpha-is-transparency, max size) live on the TextureImporter and are
        // configured via the custom editor's "Fix Texture Settings" button.
        [System.Serializable]
        public class TextureSettings
        {
            [Tooltip("Pixels-per-unit applied to the cloned sprite. Affects world-space size of the terrain.")]
            public float pixelsPerUnit = 100f;

            [Tooltip("Texture filter mode. Use Point for crisp pixel art; Bilinear for smooth.")]
            public FilterMode filterMode = FilterMode.Point;

            [Tooltip("Wrap mode for the cloned texture.")]
            public TextureWrapMode wrapMode = TextureWrapMode.Clamp;

            [Tooltip("Anisotropic filtering level. Use 0 for 2D pixel art.")]
            [Range(0, 16)] public int anisoLevel = 0;

            [Tooltip("Maximum allowed texture dimension. Larger source textures will be downsampled by the importer's Max Size. Unity's hard ceiling is 16384.")]
            [Range(64, 16384)] public int maxResolution = 4096;
        }

        [Header("Preset")]
        [Tooltip("Optional. If assigned, its settings are applied in Awake and auto-scaled to this terrain's texture resolution (see TerrainPreset). Overrides the fields below.")]
        public TerrainPreset preset;

        [Header("Texture Settings")]
        public TextureSettings textureSettings = new TextureSettings();

        [Header("Chunk Settings")]
        public int chunkSize = 128;
        public float alphaThreshold = 0.1f;
        public float simplifyTolerance = 1f;

        [Header("Destruction Settings")]
        public float eraseRadius = 0.5f;
        public bool canDig = true;
        public int maxPhysicsPixels = 5000000;
        public bool anchorToSideBorders = false;
        [Tooltip("If on, the terrain's whole remaining body is always anchored — it never falls as a unit even when its art doesn't touch the texture's bottom/side edges (e.g. PNGs with transparent margins). Pieces you slice off still fall. Off by default: the whole image is physics-enabled, so slicing anywhere drops it.")]
        public bool anchorWholeBody = false;

        [Header("Visual Effects")]
        public Color32 aftermathColor = new Color32(0, 0, 0, 255);
        public int aftermathThickness = 3;
        public ParticleSystem debrisParticles;
        [Range(0f, 1f)] public float particleSpawnChance = 0.05f;
        public bool enableDebrisParticles = true;

        [Header("Physics Settings")]
        public int groundAnchorY = 0;
        public float massPerPixel = 1e-06f;
        public float gravityPerPixel = 0.1f;
        public float minChunkGravity = 1f;
        public float maxChunkGravity = 5f;
        public bool enableIslandFalling = true;
        public bool enableImpactWelding = true;

        [Tooltip("If true, falling chunks that leave the camera view are destroyed (freeing memory + physics/render cost) instead of just sleeping off-screen. Destroyed chunks do NOT reappear if the camera pans back — best for mobile cleanup/FPS in games where chunks fall away and aren't revisited.")]
        public bool destroyOffscreenChunks = false;

        [Tooltip("Minimum island pixel count to spawn a falling chunk. Smaller detached islands are cleared inline instead. Lower = more debris fidelity; higher = fewer spawn spikes.")]
        public int minimumPhysicsPixels = 750;

        [Header("Impact & Welding Physics")]
        public float minMergeVelocity = 2f;
        public float minEmbedVelocity = 5f;
        public float embedMultiplier = 0.05f;
        public int maxEmbedPixels = 20;
        public float mergeDuration = 0.25f;
        public float maxMergeAngle = 45f;
        public float smashVelocity = 15f;

        private Vector2 spriteMin;
        private float ppu = 32f;
        private NativeArray<Color32> rawPixels;
        private int texWidth;
        private int texHeight;

        [Header("Visual Chunk Settings")]
        [Tooltip("Slices the 4K texture into smaller pieces so the GPU doesn't stall on uploads.")]
        public int visualChunkSize = 256;

        [Header("Performance")]
        [Tooltip("Max mainland chunks rebuilt per LateUpdate. Overflow defers to next frame. Lower = smoother frame time, slightly delayed collider updates after huge edits.")]
        public int maxRebuildsPerFrame = 16;

        [Tooltip("Max wall-clock time (ms) per LateUpdate spent on RebuildChunk calls. Whichever fires first (this or maxRebuildsPerFrame) stops the loop. Clips spikes when several chunks need expensive multi-path SetPath uploads in the same frame. 0 disables the time check.")]
        public float rebuildBudgetMs = 4.0f;

        [Tooltip("Max wall-clock time (ms) per LateUpdate spent on ApplyVisuals texture uploads. Whichever fires first (this or maxVisualsPerFrame) stops the loop. Clips ApplyVisuals spikes when many sub-chunks need uploading on the same frame (e.g. merge frames or wide brush strokes). 0 disables the time check.")]
        public float visualsBudgetMs = 3.0f;

        [Tooltip("RDP simplify tolerance for mainland chunk contours (pixels). Lower = more accurate but costlier colliders; higher = faster rebuilds. Drop to 0.3 for collision misses on thin features; raise to 0.7+ if rebuilds spike.")]
        public float mainlandSimplifyTolerance = 0.5f;

        [Tooltip("Run GhostSweepJob after each extraction to clean faint pixels (0 < alpha < alphaThreshold) adjacent to the extracted region. Required for sprites with continuous alpha gradients to avoid visible ghost artifacts at extraction boundaries. Skip for hard-edged terrain (pixel art / Worms-style with binary alpha) where the sweep is pure cost. Saves ~1–5ms per spawn.")]
        public bool enableGhostSweep = true;

        [Tooltip("After this many settled seconds with no merge target, a falling chunk's Rigidbody2D.simulated is turned off (Box2D ignores it) — big perf win when failed-merge chunks pile up. Any dig within sleepWakeRadius wakes it. 0 disables.")]
        public float sleepIdleSeconds = 3.0f;

        [Tooltip("Radius (world units) within which slept chunks wake up when a dig event fires. Should be at least the largest dig radius. Larger = chunks wake more readily (more responsive but more wake events). Smaller = fewer wakes (more performance but possible 'stuck' look if dig is just outside).")]
        public float sleepWakeRadius = 5.0f;

        [Tooltip("Max visual chunks updated per frame in ApplyVisuals. Spreads cost across " +
                 "frames to avoid spikes when many chunks dirty at once; visual lag 1-3 frames.")]
        public int maxVisualsPerFrame = 8;

        #region Internal Data

        static readonly Color32 clearColor = new Color32(0, 0, 0, 0);

        private static int[] nextNode;
        private static bool[] vertexVisited;
        private static int[] usedNodes;
        private static int usedNodesCount = 0;

        // Converted to static! All chunks share the Mainland's massive buffer.
        // NativeArray<int> for Burst/Job compatibility — needed for off-thread BFS.
        private static NativeArray<int> pixelVisitId;

        // Frozen copy of solidMap for the async flood. A flood-fill needs a CONSISTENT
        // connectivity graph for the whole traversal; reading the live solidMap while
        // the dig mutates it lets a still-attached sliver read as disconnected mid-flood
        // (the search "breaks mid-way"), so it's flagged floating and evaporated — a 1px
        // gap. Snapshotting at dispatch gives the flood a stable graph. Per-byte atomicity
        // does NOT provide this: it stops torn bytes, not torn traversal.
        private static NativeArray<byte> _bfsSolidSnapshot;

        private static Vector2[][] caseToLines = new Vector2[16][];
        private static bool caseLookupInitialized = false;

        // prewarm flag — set true after first successful prewarm.
        // Burst JIT-compiles each job on first Schedule/Run; running RebuildChunkPipelineJob
        // once with arrayLength=0 forces the compile during scene Init (a one-time cost
        // moves to load time) instead of on the first heavy gameplay frame. Editor-only
        // benefit: shipping/AOT builds compile Burst at build time, no runtime JIT.
        private static bool _burstJobsPrewarmed = false;

        // NATIVE EQUIVALENTS OF MARCHING SQUARES BUFFERS
        // Allocated alongside the managed arrays so each Burst-migration step can
        // switch its function to the native version without breaking the others.
        // Managed copies are removed once everything reads from native.
        // padBufferNative: per-chunk solidness with a 1-cell border, mirrors padBuffer.
        // caseLineCount[c] / caseLineData[c*4 + i]: flattened caseToLines for Burst.
        //   Each marching-squares case has 0, 2, or 4 line endpoints; we reserve 4
        //   slots per case (16 cases × 4 = 64 entries).
        // edgesNative: Burst-compatible mirror of the managed edges list.
        // nextNodeNative / vertexVisitedNative / usedNodesNative: TraceContours scratch.
        // rdpKeepFlagsNative: per-vertex keep flags for RDP simplification.
        private static NativeArray<byte> padBufferNative;
        private static NativeArray<int> caseLineCount;
        private static NativeArray<float2> caseLineData;
        private static NativeList<Edge> edgesNative;
        private static NativeArray<int> nextNodeNative;
        private static NativeArray<byte> vertexVisitedNative;
        private static NativeArray<int> usedNodesNative;
        private static NativeArray<byte> rdpKeepFlagsNative;

        // pooled List<Vector2> reused for pc.SetPath calls. We need a
        // managed list because PolygonCollider2D.SetPath has no NativeArray overload.
        private static List<Vector2> _setPathList = new List<Vector2>(1024);

        // filter tiny/excessive contour paths in RebuildChunk before
        // pc.SetPath. With heavy fragmentation (chunk carved from within or with
        // many small disconnected pieces), marching squares can emit 100+ contours
        // per logical chunk. Each pc.SetPath rebuilds a Box2D fixture, so 100+
        // paths is prohibitively expensive per chunk rebuild. These tiny pieces
        // will either become real
        // falling-chunk extractions on the next BFS pass (if disconnected) or are
        // visual-only debris that doesn't need accurate collision.
        // MIN_CONTOUR_AREA_PX_SQR: paths with signed-area magnitude below this
        // threshold (in squared pixels) are skipped. 64 = ~8×8 piece — anything
        // smaller is visual-only debris. Pixel-based so it scales correctly with
        // user's ppu setting.
        // MAX_PATHS_PER_CHUNK: hard cap on path count per logical chunk. Largest
        // by area kept; rest dropped. Bounds the worst-case rebuild cost on a
        // heavily fragmented chunk (each dropped path saves a pc.SetPath call).
        [Tooltip("Minimum signed-area magnitude (squared pixels) for a contour path to make it into a PolygonCollider2D. Smaller paths are dropped — the player can't walk on a 4×4 fragment of terrain anyway, but each path still costs ~1ms in pc.SetPath. Raise to 256 (16×16 floor) or 1024 (32×32 floor) to skip more clutter. 64 default keeps medium fragments; higher values reduce per-rebuild cost on heavily fragmented chunks.")]
        public float minContourAreaPxSqr = 256f;
        private const int MAX_PATHS_PER_CHUNK = 8;

        // PARALLEL CHUNK REBUILD PIPELINE
        // Replaces sequential per-chunk RebuildChunk calls with an IJobParallelFor
        // that runs the full marching-squares + RDP pipeline in parallel across
        // REBUILD_BATCH_SIZE slots. Main-thread sequential pass after the parallel
        // job handles path filter + pc.SetPath (Box2D fixture API isn't thread-safe).
        // Memory: each slot gets its own scratch (pad, edges, nextNode, etc.) so
        // workers don't contend. Sized for typical chunk worst case; chunks that
        // overflow `REBUILD_EDGE_CAP` edges fall back to the sequential RebuildChunk
        // path (rare — only fragmentation worst cases).
        // Per-slot persistent state: nextNodeBuffersB / vertexVisitedBuffersB are
        // dirty after each frame at positions tracked by slotPrevUsedCount[slot].
        // Next frame's Execute(slot) cleans those positions before reuse.
        private const int REBUILD_BATCH_SIZE = 8;

        // Per-slot capacities. Sized for chunkSize=256 worst-case practical loads.
        // edges: theoretical max for 256² is ~270k after dedup, but realistic chunks
        // produce <16k. Overflow → fallback to sequential RebuildChunk for that chunk.
        private const int REBUILD_EDGE_CAP = 16384;
        // Per-slot vertex/span CSR capacity. ~16k verts and 256 contours covers
        // typical chunks; overflow handled via the same fallback path as edges.
        private const int REBUILD_VERT_CAP = 16384;
        private const int REBUILD_SPAN_CAP = 256;
        // Per-slot RDP scratch. RDPSimplifyJob's closed-loop trick needs n+1 verts.
        private const int REBUILD_RDP_SCRATCH_CAP = 16384;
        private const int REBUILD_RDP_STACK_CAP = 2048;
        private const int REBUILD_RDP_KEEP_CAP = 16384;

        // Per-slot scratch (sized BATCH_SIZE × per-slot-cap, sliced by index).
        // Allocated in InitMarchingSquaresBuffers, disposed in DisposeStaticNativeArrays.
        private static NativeArray<byte> padBuffersB;
        private static NativeArray<Edge> edgeBuffersB;
        private static NativeArray<int> nextNodeBuffersB;
        private static NativeArray<byte> vertexVisitedBuffersB;
        private static NativeArray<int> usedNodesBuffersB;
        private static NativeArray<Vector2> contourVertsBuffersB;
        private static NativeArray<int2> contourSpansBuffersB;
        private static NativeArray<Vector2> simplVertsBuffersB;
        private static NativeArray<int2> simplSpansBuffersB;
        private static NativeArray<int> rdpStackBuffersB;
        private static NativeArray<Vector2> rdpScratchBuffersB;
        private static NativeArray<byte> rdpKeepFlagsBuffersB;

        // Per-slot counts and persistent state.
        // edgeCountsB[slot]:        edges produced this iteration (slot-local).
        // usedNodeCountsB[slot]:    contour vertices touched this iteration.
        // contourSpanCountsB[slot]: contour count this iteration.
        // simplSpanCountsB[slot]:   simplified contour count this iteration.
        // slotPrevUsedCountB[slot]: PERSISTENT — touched-vertex count from the slot's
        //                          previous use (different chunk, possibly different
        //                          frame). Read at the START of each Execute to
        //                          clean nextNodeBuffersB/vertexVisitedBuffersB.
        // outNewSignaturesB[slot]:  new chunk signature hash, written back to
        //                          chunkSignatures by the main-thread post-pass.
        // outSignatureChangedB[slot]: 1 = signature differs from chunkSignatures[i],
        //                          rebuild proceeded; 0 = unchanged, skip post-pass.
        // outOverflowedB[slot]:     1 = edge/vert capacity exceeded, fall back to
        //                          sequential RebuildChunk for this chunk.
        private static NativeArray<int> edgeCountsB;
        private static NativeArray<int> usedNodeCountsB;
        private static NativeArray<int> contourSpanCountsB;
        private static NativeArray<int> simplSpanCountsB;
        private static NativeArray<int> slotPrevUsedCountB;
        private static NativeArray<int> outNewSignaturesB;
        private static NativeArray<byte> outSignatureChangedB;
        private static NativeArray<byte> outOverflowedB;

        // Per-slot capacity values (depend on packStride / maxNodes which depend on
        // chunkSize and physicsStep). Recomputed in InitMarchingSquaresBuffers.
        // Used by the main-thread post-pass to slice the per-slot output buffers.
        private static int _slotPadLen;
        private static int _slotMaxNodes;
        private static int _slotUsedCap;

        // Inputs to the parallel job, populated each frame from dirtyChunksList.
        // chunkIndicesB[slot]: which chunk this slot is rebuilding.
        // prevSignaturesB[slot]: chunkSignatures[idx] before the call (-1 = sentinel-invalidated).
        private static NativeArray<int> chunkIndicesB;
        private static NativeArray<int> prevSignaturesB;

        // PROFILER MARKERS
        // Sub-method markers exposed via Unity's Profile Analyzer. Only active in
        // editor and development builds — completely stripped from release builds
        // by the #if guard so they have zero runtime cost on shipping mobile builds.
        // Naming convention: "DT.<Method>.<Section>" so they group cleanly in the
        // analyzer (sortable by parent).
        // Usage pattern: marker.Begin() at section start, marker.End() at section
        // end. Begin/End is preferred over `using (marker.Auto())` to avoid
        // re-indenting the existing code blocks.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Throttled rebuild loop (the new parallel pipeline).
        private static readonly ProfilerMarker s_pmRebuild_Fill = new ProfilerMarker("DT.Rebuild.FillBatch");
        private static readonly ProfilerMarker s_pmRebuild_Schedule = new ProfilerMarker("DT.Rebuild.Schedule");
        private static readonly ProfilerMarker s_pmRebuild_Complete = new ProfilerMarker("DT.Rebuild.Complete");
        private static readonly ProfilerMarker s_pmRebuild_PostPass = new ProfilerMarker("DT.Rebuild.PostPass");
        private static readonly ProfilerMarker s_pmRebuild_Fallback = new ProfilerMarker("DT.Rebuild.Fallback");

        // ApplyParallelRebuildResult sub-sections (a spike target).
        private static readonly ProfilerMarker s_pmApply_AreaFilter = new ProfilerMarker("DT.Apply.AreaFilter");
        private static readonly ProfilerMarker s_pmApply_Sort = new ProfilerMarker("DT.Apply.Sort");
        private static readonly ProfilerMarker s_pmApply_SetPath = new ProfilerMarker("DT.Apply.SetPath");
        private static readonly ProfilerMarker s_pmApply_Deactivate = new ProfilerMarker("DT.Apply.Deactivate");

        // ExtractAndSpawnIsland sub-sections (a spike target).
        private static readonly ProfilerMarker s_pmExtract_AllocSrc = new ProfilerMarker("DT.Extract.AllocSrc");
        private static readonly ProfilerMarker s_pmExtract_PixelTransform = new ProfilerMarker("DT.Extract.PixelTransform");
        private static readonly ProfilerMarker s_pmExtract_GhostSweep = new ProfilerMarker("DT.Extract.GhostSweep");
        private static readonly ProfilerMarker s_pmExtract_SetPixelData = new ProfilerMarker("DT.Extract.SetPixelData");
        private static readonly ProfilerMarker s_pmExtract_SpawnInstance = new ProfilerMarker("DT.Extract.SpawnInstance");

        // StampAndDestroy sub-sections (a merge spike target).
        private static readonly ProfilerMarker s_pmStamp_MergeStamp = new ProfilerMarker("DT.Stamp.MergeStampJob");
        private static readonly ProfilerMarker s_pmStamp_DestroyVisuals = new ProfilerMarker("DT.Stamp.DestroyVisuals");
        private static readonly ProfilerMarker s_pmStamp_FlagDirty = new ProfilerMarker("DT.Stamp.FlagDirty");
        private static readonly ProfilerMarker s_pmStamp_Particles = new ProfilerMarker("DT.Stamp.Particles");
#endif

        // cap particle emission per merge to prevent storm spikes.
        // 256 saturates a typical visual effect; excess is imperceptible.
        private const int MAX_PARTICLES_PER_MERGE = 256;

        // cap merges per frame to spread merge work across frames. Each
        // StampAndDestroy is expensive (Texture2D destroy + visual sub-chunk destroy
        // + AABB pixel sync + chunk dirty flagging + forceDrainVisuals payload).
        // When 5 chunks settle simultaneously and try to merge in the same frame,
        // a large spike occurs. Capping to MAX_MERGES_PER_FRAME spreads them.
        // The user perceives a slightly delayed merge animation, much
        // better than a single big stutter.
        // Set to 2 (not 1) because a 1-merge-per-frame cap was a visible delay
        // when many small chunks settled at once. 2/frame is a compromise that
        // still allows 6 merges over 3 frames vs 6 in 1.
        private const int MAX_MERGES_PER_FRAME = 2;
        private static int _mergesThisFrame;
        private static int _lastMergeFrame;

        // per-instance scratch arrays for the path filter+sort. Sized
        // lazily up to rawSpanCount on first use; reused across rebuilds.
        private static float[] _pathFilterArea;
        private static int[] _pathFilterIdx;

        // TraceContours output stored in CSR-style flat layout.
        // contoursVertsNative concatenates every contour's vertices end-to-end;
        // contourSpansNative records (startIdx, length) for each contour. This
        // uses Burst-friendly
        // containers. The job's Execute() rebuilds them from edgesNative.
        private static NativeList<Vector2> contoursVertsNative;
        private static NativeList<int2> contourSpansNative;
        // Single-int scratch buffer: TraceContoursJob writes the new usedNodesCount
        // here so managed code can read it back after .Run() completes.
        private static NativeArray<int> _traceResultNative;

        // simplified contour output (post-RDP). Same CSR layout —
        // simplifiedVertsNative + simplifiedSpansNative. Caller copies each span
        // into a managed Vector2 buffer for pc.SetPath.
        private static NativeList<Vector2> simplifiedVertsNative;
        private static NativeList<int2> simplifiedSpansNative;
        // RDP iterative needs a stack (was managed rdpStack int[2048]) and a
        // scratch buffer for the closed-loop trick (copy contour + duplicate first
        // vertex at end). Scratch sized large enough for the longest expected
        // contour at chunkSize=128 (≤16k verts).
        private static NativeArray<int> rdpStackNative;
        private static NativeArray<Vector2> rdpScratchNative;

        // DYNAMIC STRIDE
        private int packStride;

        // Set by RebuildChunk before TraceContours so that contour vertices are emitted
        // directly in world (parent-local) coordinates: (cur%packStride) * traceWorldScale
        // + traceWorldOffset, a single multiply-add (no multi-step coordinate chain).
        private float traceWorldScale = 0.5f;
        private float traceWorldOffset = 0f;

        // STATIC: used transiently inside RebuildChunk on main thread only.
        // Per-instance copies wasted memory on 310 inactive map components in scene.
        private static List<List<Vector2>> validPathsBuffer = new List<List<Vector2>>(64);

        // ZERO-GC ARRAY TRACKERS
        private bool[] isChunkDirty;
        private List<int> dirtyChunksList = new List<int>(512);

        private bool[] isVisualDirty;

        // When true, the next ApplyVisuals call drains the entire dirtyVisualsList
        // regardless of maxVisualsPerFrame. Set by StampAndDestroy on merge so the
        // visual reload is instant rather than smeared across 4-6 frames.
        private bool forceDrainVisuals;

        // DEFERRED PHYSICS ACTIVATION
        // Avoids a synchronous spawn-site RebuildChunk loop. New falling chunks
        // spawn with rb.simulated=false
        // (Box2D ignores them entirely — no overlap resolution, no "fly off" bug).
        // The throttled rebuild loop processes spawn-area dirty chunks at 16/frame.
        // Each LateUpdate, the pending list is scanned: when all chunks in a falling
        // chunk's AABB are clean, rb.simulated = true (chunk starts falling).
        private struct PendingPhysicsActivation
        {
            public DestructibleTerrain dt;
            public int rcx0, rcx1, rcy0, rcy1;
            public int safetyFrames; // Force-activate after this many frames as a fallback.
        }
        private readonly List<PendingPhysicsActivation> pendingPhysicsActivations = new List<PendingPhysicsActivation>();
        private List<int> dirtyVisualsList = new List<int>(512);

        // Pre-allocate the edges list so it NEVER resizes during massive explosions!
        // STATIC: RebuildChunk runs on main thread only and sequentially, so a single
        // shared list is safe and saves significant memory across component instances
        // (active + disabled map prefabs in scene hierarchy).
        static List<Edge> edges = new List<Edge>(131072);

        private static readonly List<Collider2D> sCollScratch = new List<Collider2D>(16);

        // COMPONENT CACHING
        [HideInInspector] public SpriteRenderer spriteRend;
        [HideInInspector] public Rigidbody2D rb;

        // To this:
        [HideInInspector] public bool isMerging = false;
        [HideInInspector] public int currentSolidPixels = -1;

        // Auto-merge: settled chunks merge back into mainland after resting for this duration
        private float _restTimer = 0f;
        private Vector3 _lastRestPos;
        // Frames spent stranded at rb.simulated=false; drives the orphan self-activation safety net.
        private int _activationGrace;

        // per-instance cached mainland reference resolved by
        // FindNearbyMainland's first successful OverlapBoxNonAlloc query. Used
        // by OnCollisionStay2D's mergeTarget search; saves time in pile
        // scenarios where many chunks simultaneously fire OverlapBox each fixed
        // step. Cleared by Unity's implicit Object null operator if the cached
        // mainland gets destroyed.
        private DestructibleTerrain _cachedMergeTarget;
        private const float REST_DURATION_TO_MERGE = 0.1f;
        private const float REST_VELOCITY_THRESHOLD = 0.5f;
        private const float REST_ANGULAR_THRESHOLD = 30f; // degrees/sec
        // A chunk only counts as "at rest" while its per-step position drift stays
        // under this (world units). Lets chunks that jitter in place (noisy contact
        // velocity) or creep slowly still settle and weld in. Tune to taste.
        private const float REST_POS_EPSILON = 0.03f;
        private static readonly Collider2D[] sOverlapResults = new Collider2D[16];
        private static readonly ContactPoint2D[] sSleepContacts = new ContactPoint2D[16];

        // settled-chunk sleep registry. Falling chunks that fail to
        // merge and stay idle for sleepIdleSeconds set Rigidbody2D.simulated=false
        // and add themselves here. Wake API (WakeNearbyChunks) walks this list
        // when a dig fires within sleepWakeRadius. Static so any DestructibleTerrain
        // instance can wake nearby slept chunks (e.g., mainland's ProcessEraseCircle
        // wakes piled debris when player digs near it).
        private static readonly List<DestructibleTerrain> _sleptChunks = new List<DestructibleTerrain>();

        private float _cachedCosMaxAngle;
        private float _cachedMaxMergeAngle = -1f;

        private static readonly Vector3[] sLocalCorners = new Vector3[4];

        // Target physics resolution for falling debris
        // Keeps marching squares grid manageable regardless of debris size.
        private const int FALLING_CHUNK_TARGET_CELLS = 128;

        // pixel size of each logical/physics chunk for falling chunks.
        // Was effectively the whole texture (1×1 grid) — every dig rebuilt the
        // ENTIRE chunk's collider, causing a pc.SetPath spike per dig on a
        // huge contour. Mainland uses chunkSize=128 (small chunks). Falling chunks
        // subdivide into 512px logical chunks (8×8 grid for 4096²) with
        // physicsStep=4 — same effective marching-squares grid (128 cells) per
        // chunk as mainland, so per-chunk rebuild cost is mainland-equivalent
        // per chunk. Per-dig touches 1-4 chunks.
        // 512 keeps the Box2D fixture count manageable on mobile; a smaller grid
        // (256) would mean ~4x more dynamic fixtures per huge chunk for no real
        // rebuild-cost win (RebuildChunk is already mainland-equivalent at 512).
        private const int FALLING_CHUNK_PIXEL_DIM = 512;

        // Falling chunks above this dimension switch to visual sub-chunks.
        // Below threshold: single texture + tex.Apply on the parent spriteRend
        // (cheap for small chunks). Above threshold: subdivide into visualChunkSize-
        // sized sub-textures so digs only re-upload the dirty 256x256 area instead
        // of the full huge texture (expensive per dig frame on mobile for
        // near-5M-pixel chunks).
        private const int FALLING_VISUAL_CHUNK_MIN_DIM = 512;

        // Per-instance step used by RebuildChunk. 
        // Mainland keeps this at 1; falling chunks set it to their downsampling stride.
        private int physicsStepForRebuild = 1;

        private int islandPixelCount = 0;

        private int islandMinX, islandMinY, islandMaxX, islandMaxY;

        private Texture2D tex;

        // Texture2D pooling for falling chunks. Each pool slot's
        // DestructibleTerrain instance keeps its parent Texture2D across reuses
        // via Texture2D.Reinitialize, avoiding the cost of `new Texture2D`
        // + Destroy on every spawn/merge cycle. The texture's GPU buffer is
        // re-sized in place; pixels are immediately re-filled by ExtractTransform-
        // ReleaseJob, so Reinitialize's "uninitialized contents" guarantee doesn't
        // matter (we overwrite everything anyway).
        private Texture2D _pooledTexture;

        // deferred Init for falling chunks. ExtractAndSpawnIsland
        // splits its work — texture allocation, extraction job, GhostSweep, Apply,
        // GameObject activation, and Sprite assignment all run in frame 0
        // (synchronously). The heavy InitializeAsFallingChunkSingle / SetupAsBox-
        // Debris call (which runs ClearByteArrayJob + PopulateFallingSolidMapJob
        // + CreateChunks + dirty-flagging) is staged into the dt's next
        // LateUpdate via these fields. ProcessPendingInit() at the top of
        // LateUpdate consumes them.
        // Snapshot is required because islandPixelBuffer is a `static` field
        // shared across all instances; next frame's BFS would overwrite it
        // before the deferred Init runs. Allocator.Persistent + dispose on
        // consume / OnDestroy.
        private bool _hasPendingInit;
        private NativeArray<int> _pendingInitSnapshot;
        private int _pendingInitPixelCount;
        private int _pendingInitWidth;
        private int _pendingInitHeight;
        private bool _pendingInitIsBoxDebris;

        // per-instance "single texture dirty" flag for falling chunks
        // (visualChunks==null path). ApplyVisuals' else branch fires `tex.Apply(false)`
        // only when this is true; cleared after upload. Set by ProcessEraseCircle
        // when pixels change. ExtractAndSpawnIsland already calls fallingTex.Apply
        // explicitly so doesn't need to set the flag (texture is fresh on GPU).
        // No-op for mainland (visualChunks != null path is gated separately).
        private bool _singleTextureDirty;

        private int texW, texH, chunksX, chunksY;

        // PHYSICS MEMORY CACHES
        private static int currentSearchId = 0;
        private static int startSearchId = 0;
        private static int mainlandId = 1;

        // Change padBuffer from int[] to byte[]
        private static byte[] padBuffer;

        private static NativeArray<int> floodStack;
        private static NativeArray<int> islandPixelBuffer;

        private static List<List<Vector2>> contourPool = new List<List<Vector2>>();
        private static int contourPoolIndex = 0;
        private static List<List<Vector2>> reusableContours = new List<List<Vector2>>();

        // solidMap stays non-static because every chunk needs its own unique shape!
        // NativeArray<byte> for Burst/Job compatibility.
        private NativeArray<byte> solidMap;

        // CHUNKS AND VISUALS
        private GameObject[] chunkObjects;
        // parallel cache of each chunk's PolygonCollider2D. RebuildChunk
        // is called thousands of times per session; the GetComponent lookup it did
        // each call (4-5µs × 5500 calls) added up. Filled in CreateChunks alongside
        // chunkObjects. Same lifetime, same recycle rules.
        private PolygonCollider2D[] chunkColliders;
        private int[] chunkSignatures;

        // per-chunk per-path signature hash. SetPath is the dominant
        // cost in RebuildChunk now (5-10 paths). Most rebuilds change
        // only ONE path (the one near the dig); the others are unchanged but
        // would still be re-uploaded to Box2D. By hashing each path's vertex
        // sequence and comparing to last upload, we skip SetPath for unchanged
        // paths.
        // Layout: chunkPathSignatures[chunkIdx * MAX_PATHS_PER_CHUNK + pathIdx].
        // Sentinel value 0 means "path was never set" (rare hash collision is
        // benign — worst case re-uploads the path unnecessarily once).
        private int[] chunkPathSignatures;
        private int[] chunkPathCounts; // currently active path count per chunk

        private static readonly Vector2 ONE_VEC = new Vector2(1f, 1f);

        private struct VisualChunk
        {
            public Texture2D tex;
            public NativeArray<Color32> pixels;
            public int startX, startY, width, height;

            // DIRTY RECT TRACKING
            public int minDirtyX, minDirtyY, maxDirtyX, maxDirtyY;
            public bool isDirty;

            // ref to the child SpriteRenderer so ApplyVisuals can flip
            // SetActive(true) on the sub-chunk once its first upload completes.
            // For deferred-uploaded sub-chunks (falling chunks above the size
            // threshold), we keep the child GameObject inactive so its
            // uninitialized GPU texture data doesn't render as gray on top of
            // the parent SpriteRenderer during catchup.
            public SpriteRenderer renderer;

            public void ResetDirty()
            {
                minDirtyX = width; minDirtyY = height;
                maxDirtyX = 0; maxDirtyY = 0;
                isDirty = false;
            }
        }
        private VisualChunk[] visualChunks;
        private int visualChunksX, visualChunksY;

        // pool of persistent (GameObject, SpriteRenderer, Texture2D)
        // tuples. CreateVisualChunks reuses these across spawn/merge cycles instead
        // of allocating fresh GameObjects + AddComponent + new Texture2D every time.
        // Per-spawn savings from reusing visual sub-chunks across spawns.
        // Sprite is still recreated when size changes (e.g., edge sub-chunks); inner
        // sub-chunks at the standard visualChunkSize keep their sprite across reuses.
        private struct PooledVisualChunk
        {
            public GameObject vgo;
            public SpriteRenderer vsr;
            public Texture2D tex;
            public int curWidth, curHeight;
        }
        private List<PooledVisualChunk> _vcPool = new List<PooledVisualChunk>();

        // was `=> visualChunks != null`, but added visual
        // sub-chunks for big falling chunks too — that broke this property and
        // caused the merge gate (`targetTerrain.visualChunks != null` originally,
        // and other IsMainland call sites) to incorrectly tag big falling chunks
        // as mainland → falling-into-falling merge attempts → NRE in the target's
        // mainland-only code paths. Backing field is set true only in Init() (the
        // mainland-only init path), false everywhere else.
        private bool _isMainlandTerrain;
        public bool IsMainland => _isMainlandTerrain;

        // Parent for spawned chunks: the mainland itself, or (for a falling chunk that
        // spawns a sub-chunk) the mainland it already lives under. Never a moving chunk —
        // the mainland is a stationary Kinematic body, so child rigidbodies simulate correctly.
        private Transform ChunkParent => (_isMainlandTerrain || transform.parent == null) ? transform : transform.parent;

        private struct ScanRequest
        {
            public int pixCX;
            public int pixCY;
            public int radiusPx;
        }
        private List<ScanRequest> pendingScans = new List<ScanRequest>(64);
        // Floating-island detection is debounced to the cut PAUSING: scans accumulate
        // while digging and only dispatch once no dig has happened for
        // DETECT_DEBOUNCE_FRAMES, so a region peeled over many frames detaches as ONE
        // seam-free chunk. Scans are retained until a batch finds nothing floating, so
        // several detached pieces drain one per frame once the cut stops.
        private int _lastDigFrame = -1000;
        private const int DETECT_DEBOUNCE_FRAMES = 3;

        struct Edge
        {
            public Vector2 a, b;
            public int packA, packB;
            public Edge(Vector2 a, Vector2 b, int packA, int packB)
            {
                this.a = a; this.b = b;
                this.packA = packA; this.packB = packB;
            }
        }

        private List<GameObject> reusableNewIslands = new List<GameObject>();

        private struct DamageEvent
        {
            public Vector2 pos;
            public float radius;
        }
        private List<DamageEvent> pendingDamage = new List<DamageEvent>();

        [HideInInspector] public TerrainConfig config;
        public struct TerrainConfig
        {
            public int chunkSize, maxPhysicsPixels, minimumPhysicsPixels;
            public float alphaThreshold, simplifyTolerance, eraseRadius, massPerPixel, gravityPerPixel, minChunkGravity, maxChunkGravity;
            public float minMergeVelocity, minEmbedVelocity, embedMultiplier, mergeDuration, maxMergeAngle, smashVelocity;
            public int maxEmbedPixels, aftermathThickness;
            public Color32 aftermathColor;
            public bool canDig, anchorToSideBorders, anchorWholeBody, enableDebrisParticles, enableIslandFalling, enableImpactWelding, destroyOffscreenChunks;
        }

        private Queue<GameObject> islandPool = new Queue<GameObject>();
        //private List<List<Vector2>> contourPool = new List<List<Vector2>>();
        //private int contourPoolIndex = 0;
        //private List<List<Vector2>> reusableContours = new List<List<Vector2>>();

        // PARTICLE BATCHING
        private ParticleSystem.EmitParams sharedEmitParams = new ParticleSystem.EmitParams();

        List<Vector2> GetPooledContour()
        {
            if (contourPoolIndex < contourPool.Count)
            {
                var list = contourPool[contourPoolIndex++];
                list.Clear();
                return list;
            }
            var newList = new List<Vector2>(1024); // Boosted from 64
            contourPool.Add(newList);
            contourPoolIndex++;
            return newList;
        }

        void Init()
        {
            // explicit mainland flag. Init() runs only on the persistent
            // mainland terrain (falling chunks use InitializeAsFallingChunkSingle /
            // SetupAsBoxDebris paths instead). Setting this here makes IsMainland
            // robust to visual-chunk ownership changes.
            _isMainlandTerrain = true;
            texWidth = tex.width;
            texHeight = tex.height;
            rawPixels = tex.GetRawTextureData<Color32>();

            int totalPixels = texWidth * texHeight;

            if (!solidMap.IsCreated || solidMap.Length != totalPixels)
            {
                if (solidMap.IsCreated) solidMap.Dispose();
                solidMap = new NativeArray<byte>(totalPixels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!islandPixelBuffer.IsCreated || islandPixelBuffer.Length < totalPixels)
            {
                if (islandPixelBuffer.IsCreated) islandPixelBuffer.Dispose();
                islandPixelBuffer = new NativeArray<int>(totalPixels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            byte alphaCutoff = (byte)Mathf.Clamp(alphaThreshold * 255f, 1f, 255f);
            bool needCount = (currentSolidPixels == -1);

            // Burst-compiled: ~5ms for 16M pixels vs ~1-2 seconds without (NativeArray
            // safety overhead per write × 16M iterations).
            var countResult = new NativeArray<int>(1, Allocator.TempJob);
            new InitSolidMapJob
            {
                rawPixels = rawPixels,
                solidMap = solidMap,
                resultCount = countResult,
                alphaCutoff = alphaCutoff,
                totalPixels = totalPixels,
                needCount = needCount ? (byte)1 : (byte)0,
            }.Run();
            if (needCount) currentSolidPixels = countResult[0];
            countResult.Dispose();
        }

        void InitMarchingSquaresBuffers()
        {
            InitMarchingSquaresBuffers(chunkSize);
        }

        void InitMarchingSquaresBuffers(int effectiveCells)
        {
            packStride = (effectiveCells + 3) * 2;
            int maxNodes = packStride * packStride;
            int maxEdges = (effectiveCells + 2) * (effectiveCells + 2) * 4;

            int padLen = (effectiveCells + 2) * (effectiveCells + 2);
            int safeUsedNodesSize = maxEdges * 2;

            // NATIVE-ONLY MARCHING SQUARES BUFFERS
            // Every consumer uses NativeArrays. The old managed mirrors
            // (padBuffer, nextNode, vertexVisited, usedNodes) are no longer
            // allocated, freeing dead managed memory at startup.
            if (!padBufferNative.IsCreated || padBufferNative.Length < padLen)
            {
                if (padBufferNative.IsCreated) padBufferNative.Dispose();
                padBufferNative = new NativeArray<byte>(padLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!nextNodeNative.IsCreated || nextNodeNative.Length < maxNodes)
            {
                if (nextNodeNative.IsCreated) nextNodeNative.Dispose();
                if (vertexVisitedNative.IsCreated) vertexVisitedNative.Dispose();
                nextNodeNative = new NativeArray<int>(maxNodes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                // Critical: nextNode uses -1 as the "no successor" sentinel. The
                // managed counterpart initializes once at allocation; mirror that
                // here so the chain-walk in TraceContoursJob terminates correctly
                // on never-touched slots. Per-call cleanup only resets the slots
                // touched in the previous call, not the rest.
                for (int i = 0; i < maxNodes; i++) nextNodeNative[i] = -1;
                // bool not blittable for Burst NativeArray; use byte (0/1) instead.
                // ClearMemory gives zero-initialized = "not visited".
                vertexVisitedNative = new NativeArray<byte>(maxNodes, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            if (!usedNodesNative.IsCreated || usedNodesNative.Length < safeUsedNodesSize)
            {
                if (usedNodesNative.IsCreated) usedNodesNative.Dispose();
                usedNodesNative = new NativeArray<int>(safeUsedNodesSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            // edgesNative grows as edges are appended — initial capacity matches the
            // managed list's pre-allocation. NativeList grows automatically beyond.
            if (!edgesNative.IsCreated)
            {
                edgesNative = new NativeList<Edge>(131072, Allocator.Persistent);
            }
            // rdpKeepFlags: per-vertex retention bits during RDP simplification.
            // Sized large enough to handle the longest possible contour.
            if (!rdpKeepFlagsNative.IsCreated || rdpKeepFlagsNative.Length < 16384)
            {
                if (rdpKeepFlagsNative.IsCreated) rdpKeepFlagsNative.Dispose();
                rdpKeepFlagsNative = new NativeArray<byte>(16384, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            // Contour output containers. Capacities sized to typical worst case
            // (16k vertices total, 256 contours). Both NativeLists auto-grow.
            if (!contoursVertsNative.IsCreated)
            {
                contoursVertsNative = new NativeList<Vector2>(16384, Allocator.Persistent);
            }
            if (!contourSpansNative.IsCreated)
            {
                contourSpansNative = new NativeList<int2>(256, Allocator.Persistent);
            }
            if (!_traceResultNative.IsCreated)
            {
                _traceResultNative = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            // RDP outputs and scratch.
            if (!simplifiedVertsNative.IsCreated)
            {
                simplifiedVertsNative = new NativeList<Vector2>(16384, Allocator.Persistent);
            }
            if (!simplifiedSpansNative.IsCreated)
            {
                simplifiedSpansNative = new NativeList<int2>(256, Allocator.Persistent);
            }
            if (!rdpStackNative.IsCreated)
            {
                rdpStackNative = new NativeArray<int>(2048, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!rdpScratchNative.IsCreated)
            {
                rdpScratchNative = new NativeArray<Vector2>(16384, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // PER-SLOT PARALLEL REBUILD BUFFERS
            // Allocate REBUILD_BATCH_SIZE × per-slot scratch flat-sliced. Sized for
            // the current chunkSize/physicsStep — re-allocated if the slot capacity
            // requirement changes (e.g., mainland vs falling-chunk Init).
            int slotPadLen = padLen;
            int slotMaxNodes = maxNodes;
            // Cap usedNodes to REBUILD_EDGE_CAP * 2; chunks that exceed this take
            // the sequential fallback path. Worst-case maxEdges*2 would be huge
            // (270k×2×8slots = 17MB just for usedNodes) and the overflow case is rare.
            int slotUsedCap = REBUILD_EDGE_CAP * 2;
            if (slotUsedCap > safeUsedNodesSize) slotUsedCap = safeUsedNodesSize;

            // If the per-slot capacities changed (chunkSize / physicsStep changed
            // and the new requirement is LARGER than what's allocated), reallocate
            // AND invalidate slotPrevUsedCountB so the per-Execute cleanup doesn't
            // try to clear stale positions in resized buffers.
            // Buffers only GROW — once allocated for a large mainland chunkSize, we
            // keep that stride for all subsequent (potentially smaller) instances.
            // The slot-stride values (_slotPadLen / _slotMaxNodes / _slotUsedCap)
            // track the actual allocated capacity, NOT the current instance's needs.
            // This keeps slot indexing consistent across instances of different sizes.
            bool slotCapsChanged =
                !padBuffersB.IsCreated ||
                padBuffersB.Length < slotPadLen * REBUILD_BATCH_SIZE ||
                !nextNodeBuffersB.IsCreated ||
                nextNodeBuffersB.Length < slotMaxNodes * REBUILD_BATCH_SIZE ||
                !usedNodesBuffersB.IsCreated ||
                usedNodesBuffersB.Length < slotUsedCap * REBUILD_BATCH_SIZE;

            if (slotCapsChanged)
            {
                // Dispose old.
                if (padBuffersB.IsCreated) padBuffersB.Dispose();
                if (edgeBuffersB.IsCreated) edgeBuffersB.Dispose();
                if (nextNodeBuffersB.IsCreated) nextNodeBuffersB.Dispose();
                if (vertexVisitedBuffersB.IsCreated) vertexVisitedBuffersB.Dispose();
                if (usedNodesBuffersB.IsCreated) usedNodesBuffersB.Dispose();
                if (contourVertsBuffersB.IsCreated) contourVertsBuffersB.Dispose();
                if (contourSpansBuffersB.IsCreated) contourSpansBuffersB.Dispose();
                if (simplVertsBuffersB.IsCreated) simplVertsBuffersB.Dispose();
                if (simplSpansBuffersB.IsCreated) simplSpansBuffersB.Dispose();
                if (rdpStackBuffersB.IsCreated) rdpStackBuffersB.Dispose();
                if (rdpScratchBuffersB.IsCreated) rdpScratchBuffersB.Dispose();
                if (rdpKeepFlagsBuffersB.IsCreated) rdpKeepFlagsBuffersB.Dispose();

                // Allocate new.
                padBuffersB = new NativeArray<byte>(slotPadLen * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                edgeBuffersB = new NativeArray<Edge>(REBUILD_EDGE_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                nextNodeBuffersB = new NativeArray<int>(slotMaxNodes * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                // Init nextNodeBuffersB to -1 (sentinel for "no successor"). Same as nextNodeNative.
                for (int i = 0; i < nextNodeBuffersB.Length; i++) nextNodeBuffersB[i] = -1;
                vertexVisitedBuffersB = new NativeArray<byte>(slotMaxNodes * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                usedNodesBuffersB = new NativeArray<int>(slotUsedCap * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                contourVertsBuffersB = new NativeArray<Vector2>(REBUILD_VERT_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                contourSpansBuffersB = new NativeArray<int2>(REBUILD_SPAN_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                simplVertsBuffersB = new NativeArray<Vector2>(REBUILD_VERT_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                simplSpansBuffersB = new NativeArray<int2>(REBUILD_SPAN_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                rdpStackBuffersB = new NativeArray<int>(REBUILD_RDP_STACK_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                rdpScratchBuffersB = new NativeArray<Vector2>(REBUILD_RDP_SCRATCH_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                rdpKeepFlagsBuffersB = new NativeArray<byte>(REBUILD_RDP_KEEP_CAP * REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                _slotPadLen = slotPadLen;
                _slotMaxNodes = slotMaxNodes;
                _slotUsedCap = slotUsedCap;
            }

            // Per-slot count and state arrays. These don't depend on chunkSize so
            // they only need to exist; reuse across chunkSize changes.
            if (!edgeCountsB.IsCreated)
            {
                edgeCountsB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                usedNodeCountsB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                contourSpanCountsB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                simplSpanCountsB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                slotPrevUsedCountB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                outNewSignaturesB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                outSignatureChangedB = new NativeArray<byte>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                outOverflowedB = new NativeArray<byte>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                chunkIndicesB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                prevSignaturesB = new NativeArray<int>(REBUILD_BATCH_SIZE, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            // If we just reallocated nextNode/usedNodes (slotCapsChanged), invalidate
            // the persistent prev-used counts. Without this, next frame's Execute(slot)
            // would attempt to clean positions in the new nextNode that don't correspond
            // to anything (left by the previous tenant of that slot).
            if (slotCapsChanged && slotPrevUsedCountB.IsCreated)
            {
                for (int i = 0; i < REBUILD_BATCH_SIZE; i++) slotPrevUsedCountB[i] = 0;
            }

            // BURST JIT PREWARM
            // Schedule RebuildChunkPipelineJob once with arrayLength=0 to trigger Burst
            // synchronous compilation NOW (during scene Init / loading) instead of on
            // the first heavy gameplay frame. Eliminates the first-use stutter
            // (a chained Burst compile on the first dig event).
            // Editor only: shipping builds use Burst AOT (compiled at build time), no
            // runtime JIT, so prewarm is purely an editor profiling improvement.
            // Runs once per session — guarded by _burstJobsPrewarmed flag. Requires
            // solidMap + caseLineCount + caseLineData all allocated, which they are
            // by the time InitMarchingSquaresBuffers returns.
#if UNITY_EDITOR
            if (!_burstJobsPrewarmed && solidMap.IsCreated && caseLineCount.IsCreated && caseLineData.IsCreated)
            {
                _burstJobsPrewarmed = true;
                int prewarmStep = physicsStepForRebuild;
                int prewarmScaledChunkSize = chunkSize / prewarmStep;
                float prewarmPixToWorld = prewarmStep / (float)ppu;

                var prewarmJob = new RebuildChunkPipelineJob
                {
                    chunkIndices = chunkIndicesB,
                    prevSignatures = prevSignaturesB,
                    solidMap = solidMap,
                    caseLineCount = caseLineCount,
                    caseLineData = caseLineData,
                    padBuffers = padBuffersB,
                    edgeBuffers = edgeBuffersB,
                    nextNodeBuffers = nextNodeBuffersB,
                    vertexVisitedBuffers = vertexVisitedBuffersB,
                    usedNodesBuffers = usedNodesBuffersB,
                    contourVertsBuffers = contourVertsBuffersB,
                    contourSpansBuffers = contourSpansBuffersB,
                    simplVertsBuffers = simplVertsBuffersB,
                    simplSpansBuffers = simplSpansBuffersB,
                    rdpStackBuffers = rdpStackBuffersB,
                    rdpScratchBuffers = rdpScratchBuffersB,
                    rdpKeepFlagsBuffers = rdpKeepFlagsBuffersB,
                    edgeCounts = edgeCountsB,
                    usedNodeCounts = usedNodeCountsB,
                    contourSpanCounts = contourSpanCountsB,
                    simplSpanCounts = simplSpanCountsB,
                    slotPrevUsedCount = slotPrevUsedCountB,
                    outNewSignatures = outNewSignaturesB,
                    outSignatureChanged = outSignatureChangedB,
                    outOverflowed = outOverflowedB,
                    padCap = _slotPadLen,
                    edgeCap = REBUILD_EDGE_CAP,
                    maxNodes = _slotMaxNodes,
                    usedCap = _slotUsedCap,
                    vertCap = REBUILD_VERT_CAP,
                    spanCap = REBUILD_SPAN_CAP,
                    rdpStackCap = REBUILD_RDP_STACK_CAP,
                    rdpScratchCap = REBUILD_RDP_SCRATCH_CAP,
                    rdpKeepCap = REBUILD_RDP_KEEP_CAP,
                    chunkSize = chunkSize,
                    physicsStep = prewarmStep,
                    chunksX = chunksX,
                    texWidth = texWidth,
                    texHeight = texHeight,
                    packStride = packStride,
                    scaledChunkSize = prewarmScaledChunkSize,
                    padStride = prewarmScaledChunkSize + 2,
                    traceWorldScale = 0.5f * prewarmPixToWorld,
                    traceWorldOffset = -prewarmPixToWorld,
                    worldTol = 0.5f * prewarmPixToWorld,
                };
                // arrayLength=0 → Burst still compiles, but Execute is never called,
                // so no out-of-bounds reads on dummy data.
                prewarmJob.Schedule(0, 1).Complete();
            }
#endif
        }

        #endregion

        // Burst-compiled clear jobs. Replace System.Array.Clear for NativeArrays.
        // Called via .Run() (synchronous, on main thread) — Burst SIMD-vectorizes
        // the loop, getting near-memset speed without `unsafe` keyword in user code.
        [BurstCompile]
        private struct ClearByteArrayJob : IJob
        {
            public NativeArray<byte> arr;
            public int length;
            public void Execute()
            {
                for (int i = 0; i < length; i++) arr[i] = 0;
            }
        }

        [BurstCompile]
        private struct ClearIntArrayJob : IJob
        {
            public NativeArray<int> arr;
            public int length;
            public void Execute()
            {
                for (int i = 0; i < length; i++) arr[i] = 0;
            }
        }

        // Initializes solidMap from rawPixels alpha — runs at scene start for the
        // mainland's full 4K texture (16M pixels). Without Burst, NativeArray safety
        // overhead made this take ~1-2 seconds; Burst-compiled it drops to ~5ms.
        [BurstCompile(CompileSynchronously = true)]
        private struct InitSolidMapJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> rawPixels;
            [WriteOnly] public NativeArray<byte> solidMap;
            public NativeArray<int> resultCount; // [0] = count if needCount
            public byte alphaCutoff;
            public int totalPixels;
            public byte needCount; // 0 or 1; bool would also work but byte is explicit

            public void Execute()
            {
                int count = 0;
                for (int i = 0; i < totalPixels; i++)
                {
                    byte solid = rawPixels[i].a >= alphaCutoff ? (byte)1 : (byte)0;
                    solidMap[i] = solid;
                    count += solid;
                }
                if (needCount != 0) resultCount[0] = count;
            }
        }

        // Burst-compile StampAndDestroy + ComputeChunkSignature
        // Burst-compiled IJobs eliminate the safety overhead entirely. Particle
        // emission can't run inside Burst (calls Unity APIs), so the job buffers
        // events into a NativeList drained on the main thread post-job.

        // Buffered particle emission request. Main thread drains these post-job and
        // calls EmitDebrisParticle(worldPos, existing, fallVel) for each.
        private struct MergeParticleEvent
        {
            public int sx, sy;
            public Color32 existing;
        }

        // Burst-compiled merge stamp: a single path with no Burst-safety overhead.
        [BurstCompile]
        private struct MergeStampJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> fPixels;
            public NativeArray<Color32> rawPixels;
            public NativeArray<byte> solidMap;
            public NativeList<MergeParticleEvent> particleEvents;
            public NativeArray<int> result; // [minX, maxX, minY, maxY, mapChanged]

            public byte alphaCutoff;
            public int texWidth, fWidth, fHeight;
            public int startX, endX, startY, endY;
            public float origin_fx, origin_fy;
            public float stepX_fx, stepX_fy;
            public float stepY_fx, stepY_fy;
            public byte enableDebrisParticles; // bool→byte for blittable
            public float particleSpawnChance;
            public Unity.Mathematics.Random rng;

            public void Execute()
            {
                int dirtyMinX = int.MaxValue, dirtyMaxX = int.MinValue;
                int dirtyMinY = int.MaxValue, dirtyMaxY = int.MinValue;
                int mapChanged = 0;

                int rawLen = rawPixels.Length;
                int solidLen = solidMap.Length;
                int fLen = fPixels.Length;

                for (int sy = startY; sy <= endY; sy++)
                {
                    float rowFx = origin_fx + stepX_fx * startX + stepY_fx * sy;
                    float rowFy = origin_fy + stepX_fy * startX + stepY_fy * sy;
                    int sRowBase = sy * texWidth;

                    for (int sx = startX; sx <= endX; sx++)
                    {
                        int sIdx = sRowBase + sx;
                        int fx = (int)rowFx;
                        int fy = (int)rowFy;

                        if (fx >= 0 && fx < fWidth && fy >= 0 && fy < fHeight)
                        {
                            int fIdx = fy * fWidth + fx;
                            if ((uint)fIdx < (uint)fLen && (uint)sIdx < (uint)rawLen)
                            {
                                Color32 fPixel = fPixels[fIdx];
                                if (fPixel.a >= alphaCutoff)
                                {
                                    Color32 writeColor = fPixel;
                                    Color32 existing = rawPixels[sIdx];
                                    if (existing.a >= alphaCutoff)
                                    {
                                        writeColor = new Color32(
                                            (byte)(writeColor.r * 0.4f),
                                            (byte)(writeColor.g * 0.4f),
                                            (byte)(writeColor.b * 0.4f),
                                            255);

                                        if (enableDebrisParticles != 0 && rng.NextFloat() < particleSpawnChance)
                                        {
                                            particleEvents.Add(new MergeParticleEvent
                                            {
                                                sx = sx,
                                                sy = sy,
                                                existing = existing,
                                            });
                                        }
                                    }

                                    rawPixels[sIdx] = writeColor;
                                    if ((uint)sIdx < (uint)solidLen) solidMap[sIdx] = 1;

                                    if (sx < dirtyMinX) dirtyMinX = sx;
                                    if (sx > dirtyMaxX) dirtyMaxX = sx;
                                    if (sy < dirtyMinY) dirtyMinY = sy;
                                    if (sy > dirtyMaxY) dirtyMaxY = sy;
                                    mapChanged = 1;
                                }
                            }
                        }
                        rowFx += stepX_fx;
                        rowFy += stepX_fy;
                    }
                }

                result[0] = dirtyMinX;
                result[1] = dirtyMaxX;
                result[2] = dirtyMinY;
                result[3] = dirtyMaxY;
                result[4] = mapChanged;
            }
        }

        // Burst-compile the dig path itself.
        // Strategy: replace the per-chunk loop with ONE Burst job over the whole
        // brush AABB. Job writes rawPixels + solidMap (no safety in Burst). The
        // managedRawPixels mirror is post-synced from rawPixels for the brush
        // AABB (per-row Copy = memcpy). Visual chunk dirty rects already use
        // brush AABB so no per-chunk granularity is lost.
        [BurstCompile]
        private struct EraseBrushJob : IJob
        {
            public NativeArray<Color32> rawPixels;
            public NativeArray<byte> solidMap;
            public NativeArray<int> result; // [0..3] dirty rect, [4] changed, [5] solidPixelsDelta

            public Color32 aftermath;
            public int texWidth, texHeight;
            public int pixCX, pixCY;
            public int sqrR, sqrTotalR, totalRadiusPx;
            public byte trackSolidDelta; // 1 = decrement currentSolidPixels (falling chunk path)

            public void Execute()
            {
                int yLo = pixCY - totalRadiusPx;
                int yHi = pixCY + totalRadiusPx;
                if (yLo < 0) yLo = 0;
                if (yHi > texHeight - 1) yHi = texHeight - 1;

                int dirtyMinX = int.MaxValue, dirtyMaxX = int.MinValue;
                int dirtyMinY = int.MaxValue, dirtyMaxY = int.MinValue;
                int changed = 0;
                int solidDelta = 0;
                int rawLen = rawPixels.Length;
                int solidLen = solidMap.Length;

                for (int gy = yLo; gy <= yHi; gy++)
                {
                    int dy = gy - pixCY;
                    int dy2 = dy * dy;

                    int outerSpan2 = sqrTotalR - dy2;
                    if (outerSpan2 < 0) continue;
                    int dxMaxOuter = (int)math.sqrt((float)outerSpan2);
                    int x0 = pixCX - dxMaxOuter;
                    int x1 = pixCX + dxMaxOuter;
                    if (x0 < 0) x0 = 0;
                    if (x1 > texWidth - 1) x1 = texWidth - 1;
                    if (x1 < x0) continue;

                    int dxMaxInner = -1;
                    if (dy2 <= sqrR) dxMaxInner = (int)math.sqrt((float)(sqrR - dy2));
                    int innerX0 = pixCX - dxMaxInner;
                    int innerX1 = pixCX + dxMaxInner;

                    int rowIndex = gy * texWidth;

                    for (int gx = x0; gx <= x1; gx++)
                    {
                        int index = rowIndex + gx;
                        if ((uint)index >= (uint)solidLen) continue;
                        if (solidMap[index] == 0) continue;

                        if (dxMaxInner >= 0 && gx >= innerX0 && gx <= innerX1)
                        {
                            // Inner: full erase.
                            if ((uint)index < (uint)rawLen)
                            {
                                Color32 c = rawPixels[index];
                                c.a = 0;
                                rawPixels[index] = c;
                            }
                            solidMap[index] = 0;
                            if (trackSolidDelta != 0) solidDelta--;

                            if (gx < dirtyMinX) dirtyMinX = gx;
                            if (gx > dirtyMaxX) dirtyMaxX = gx;
                            if (gy < dirtyMinY) dirtyMinY = gy;
                            if (gy > dirtyMaxY) dirtyMaxY = gy;
                            changed = 1;
                        }
                        else
                        {
                            // Outer ring: aftermath color, only if not already applied.
                            if ((uint)index < (uint)rawLen)
                            {
                                Color32 existing = rawPixels[index];
                                if (existing.r != aftermath.r || existing.g != aftermath.g)
                                {
                                    rawPixels[index] = aftermath;

                                    if (gx < dirtyMinX) dirtyMinX = gx;
                                    if (gx > dirtyMaxX) dirtyMaxX = gx;
                                    if (gy < dirtyMinY) dirtyMinY = gy;
                                    if (gy > dirtyMaxY) dirtyMaxY = gy;
                                    changed = 1;
                                }
                            }
                        }
                    }
                }

                result[0] = dirtyMinX;
                result[1] = dirtyMaxX;
                result[2] = dirtyMinY;
                result[3] = dirtyMaxY;
                result[4] = changed;
                result[5] = solidDelta;
            }
        }

        // Burst-compiled chunk signature. Hash 31-mul over a strided sample of
        // solidMap bits. With safety checks: 64ms across 17 chunks. Without: <1ms.
        [BurstCompile]
        private struct ComputeChunkSignatureJob : IJob
        {
            [ReadOnly] public NativeArray<byte> solidMap;
            public NativeArray<int> result; // [0] = hash
            public int baseX, baseY;
            public int chunkSize, texWidth, texHeight;

            public void Execute()
            {
                int hash = 17;
                int startY = baseY;
                int endY = baseY + chunkSize;
                if (endY > texHeight) endY = texHeight;
                int startX = baseX;
                int endX = baseX + chunkSize;
                if (endX > texWidth) endX = texWidth;

                for (int gy = startY; gy < endY; gy += 2)
                {
                    int rowBase = gy * texWidth;
                    for (int gx = startX; gx < endX; gx += 2)
                    {
                        hash = hash * 31 + (solidMap[rowBase + gx] != 0 ? 1 : 0);
                    }
                }
                result[0] = hash;
            }
        }

        // Burst-compiled population of a falling chunk's solidMap from a list of local
        // pixel indices. For huge detachments (1M-5M pixels) the managed loop costs
        // 30-150ms via NativeArray safety overhead per write; Burst-compiled drops to
        // sub-millisecond.
        [BurstCompile(CompileSynchronously = true)]
        private struct PopulateFallingSolidMapJob : IJob
        {
            [ReadOnly] public NativeArray<int> activeLocalPixels;
            [WriteOnly] public NativeArray<byte> solidMap;
            public int pixelCount;

            public void Execute()
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    solidMap[activeLocalPixels[i]] = 1;
                }
            }
        }

        // Extraction transform job (release/build branch). Same as the editor variant
        // plus the rawPixels/newPixels work since release uses NativeArrays directly
        // (no managed mirror). Single Burst pass handles the entire per-pixel loop.
        [BurstCompile(CompileSynchronously = true)]
        private struct ExtractTransformReleaseJob : IJob
        {
            public NativeArray<int> islandPixelBuffer;
            public NativeArray<byte> solidMap;
            public NativeArray<byte> dirtyChunkBitset;
            public NativeArray<Color32> rawPixels;
            [WriteOnly] public NativeArray<Color32> newPixels;
            public Color32 clearColor;
            public int islandPixelCount;
            public int texWidth;
            public int islandMinX;
            public int islandMinY;
            public int width;
            public int chunkSize;
            public int chunksX;

            public void Execute()
            {
                // Clear ALL bbox pixels first. Without this, non-island positions
                // in newPixels carry uninitialized Texture2D data (often grey on
                // Android), producing a grey square around the extracted shape.
                // The dev branch hides this via System.Array.Clear on the managed
                // mirror; release has no mirror so we must clear newPixels directly.
                int totalBboxPixels = newPixels.Length;
                for (int j = 0; j < totalBboxPixels; j++)
                {
                    newPixels[j] = clearColor;
                }

                for (int i = 0; i < islandPixelCount; i++)
                {
                    int idx = islandPixelBuffer[i];
                    int py = idx / texWidth;
                    int px = idx - py * texWidth;
                    int localIdx = (py - islandMinY) * width + (px - islandMinX);

                    newPixels[localIdx] = rawPixels[idx];
                    rawPixels[idx] = clearColor;
                    solidMap[idx] = 0;
                    islandPixelBuffer[i] = localIdx;

                    int chunkIdx = (py / chunkSize) * chunksX + (px / chunkSize);
                    dirtyChunkBitset[chunkIdx] = 1;
                }
            }
        }

        // Ghost outline sweep job. Iterates the island AABB; for each empty cell with
        // a faded-but-not-zero alpha and an empty 4-neighbor, clears the pixel. This
        // is the visual residue cleanup after extraction. For huge detachments it
        // dominated extraction cost (5M iterations × multiple NativeArray reads
        // ≈ 200ms). Burst-compiled drops to ~5-15ms.
        // NOTE: operates on a NativeArray<Color32> (rawPixels). For editor mainland
        // path, callers must sync managedRawPixels (AABB+1 border) -> rawPixels before
        // the job, and rawPixels -> managedRawPixels after. The +1 border is required
        // because the job reads adjacent pixels at the AABB boundary.
        [BurstCompile(CompileSynchronously = true)]
        private struct GhostSweepJob : IJob
        {
            [ReadOnly] public NativeArray<byte> solidMap;
            public NativeArray<Color32> pixels;
            public int islandMinX;
            public int islandMaxX;
            public int islandMinY;
            public int islandMaxY;
            public int texWidth;
            public int texHeight;
            public byte alphaCutoff;
            public Color32 clearColor;

            public void Execute()
            {
                for (int gy = islandMinY; gy <= islandMaxY; gy++)
                {
                    int rowBase = gy * texWidth;
                    for (int gx = islandMinX; gx <= islandMaxX; gx++)
                    {
                        int idx = rowBase + gx;
                        if (solidMap[idx] == 0 && pixels[idx].a > 0 && pixels[idx].a < alphaCutoff)
                        {
                            bool adjacent =
                                (gx > 0 && pixels[idx - 1].a == 0 && solidMap[idx - 1] == 0) ||
                                (gx < texWidth - 1 && pixels[idx + 1].a == 0 && solidMap[idx + 1] == 0) ||
                                (gy > 0 && pixels[idx - texWidth].a == 0 && solidMap[idx - texWidth] == 0) ||
                                (gy < texHeight - 1 && pixels[idx + texWidth].a == 0 && solidMap[idx + texWidth] == 0);
                            if (adjacent) pixels[idx] = clearColor;
                        }
                    }
                }
            }
        }

        // BURST POPULATE ISOLATED PAD BUFFER
        // Reads solidMap and writes a single chunk's worth of solidness data into
        // padBufferNative with a 1-cell border. Mirrors the original managed body
        // verbatim (fast path for step==1, slow path for downsampled physics).
        // Runs synchronously via .Run() — Burst native code, ~10x faster than the
        // managed equivalent for large pad sizes.
        [BurstCompile(CompileSynchronously = true)]
        private struct PopulateIsolatedPadBufferJob : IJob
        {
            [ReadOnly] public NativeArray<byte> solidMap;
            public NativeArray<byte> padBuffer;

            public int padLen;
            public int scaledChunkSize;
            public int stride;
            public int baseX;
            public int baseY;
            public int step;
            public int texWidth;
            public int texHeight;

            public void Execute()
            {
                // Clear pad to zeros.
                for (int i = 0; i < padLen; i++) padBuffer[i] = 0;

                // Fast path: step==1 and the chunk is fully in-bounds. Per-row copy.
                if (step == 1 && baseX >= 0 && baseY >= 0 &&
                    baseX + scaledChunkSize <= texWidth && baseY + scaledChunkSize <= texHeight)
                {
                    for (int y = 0; y < scaledChunkSize; y++)
                    {
                        int srcRow = (baseY + y) * texWidth + baseX;
                        int dstRow = (y + 1) * stride + 1;
                        for (int i = 0; i < scaledChunkSize; i++)
                        {
                            padBuffer[dstRow + i] = solidMap[srcRow + i];
                        }
                    }
                    return;
                }

                // Slow path: downsampled and/or partially out-of-bounds chunk.
                int yLimit = scaledChunkSize;
                int yClamp = (texHeight - baseY + step - 1) / step;
                if (yClamp < yLimit) yLimit = yClamp;
                int xLimit = scaledChunkSize;
                int xClamp = (texWidth - baseX + step - 1) / step;
                if (xClamp < xLimit) xLimit = xClamp;
                for (int y = 0; y < yLimit; y++)
                {
                    int gy = baseY + y * step;
                    if (gy < 0) continue;
                    int rowOffset = (y + 1) * stride;
                    int srcRowBase = gy * texWidth;
                    for (int x = 0; x < xLimit; x++)
                    {
                        int gx = baseX + x * step;
                        if (gx < 0) continue;
                        padBuffer[rowOffset + x + 1] = solidMap[srcRowBase + gx];
                    }
                }
            }
        }

        // BURST BUILD EDGES
        // Marching squares cell-by-cell traversal. For each non-trivial case (not
        // 0/15), emits one or two line segments into edgesNative. Saddle cases
        // (5 and 10) read their endpoints from the flat caseLineData lookup so the
        // switch can stay branch-only without managed allocations.
        // PackVertex inlined as PackVertexLocal — same int-cast trick the managed
        // version used (10x faster than Mathf.RoundToInt).
        [BurstCompile(CompileSynchronously = true)]
        private struct BuildEdgesJob : IJob
        {
            [ReadOnly] public NativeArray<byte> padBuffer;
            [ReadOnly] public NativeArray<int> caseLineCount;
            [ReadOnly] public NativeArray<float2> caseLineData;
            public NativeList<Edge> edges;

            public int stride;
            public int packStride;

            public void Execute()
            {
                int R = stride, C = stride;
                edges.Clear();

                bool prevRowEmpty = IsRowEmpty(0, C);
                for (int y = 0; y < R - 1; y++)
                {
                    int row0 = y * stride;
                    int row1 = (y + 1) * stride;
                    bool currRowEmpty = IsRowEmpty(row1, C);

                    if (!(prevRowEmpty && currRowEmpty))
                    {
                        for (int x = 0; x < C - 1; x++)
                        {
                            int idx = padBuffer[row0 + x] | (padBuffer[row0 + x + 1] << 1)
                                    | (padBuffer[row1 + x + 1] << 2) | (padBuffer[row1 + x] << 3);

                            Vector2 a, b;
                            switch (idx)
                            {
                                case 0:
                                case 15: continue;
                                case 1: a = new Vector2(x + 0.5f, y); b = new Vector2(x, y + 0.5f); break;
                                case 2: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x + 0.5f, y); break;
                                case 4: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x + 1, y + 0.5f); break;
                                case 8: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 0.5f, y + 1); break;
                                case 3: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x, y + 0.5f); break;
                                case 12: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 1, y + 0.5f); break;
                                case 6: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x + 0.5f, y); break;
                                case 9: a = new Vector2(x + 0.5f, y); b = new Vector2(x + 0.5f, y + 1); break;
                                case 7: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x, y + 0.5f); break;
                                case 11: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x + 0.5f, y + 1); break;
                                case 13: a = new Vector2(x + 0.5f, y); b = new Vector2(x + 1, y + 0.5f); break;
                                case 14: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 0.5f, y); break;
                                case 5:
                                case 10:
                                    {
                                        // Ambiguous saddle: read endpoints from flat lookup.
                                        int count = caseLineCount[idx];
                                        int basePtr = idx * 4;
                                        for (int i = 0; i < count; i += 2)
                                        {
                                            float2 lookA = caseLineData[basePtr + i];
                                            float2 lookB = caseLineData[basePtr + i + 1];
                                            Vector2 ptA = new Vector2(x + lookA.x, y + lookA.y);
                                            Vector2 ptB = new Vector2(x + lookB.x, y + lookB.y);
                                            edges.Add(new Edge(ptA, ptB, PackVertexLocal(ptA), PackVertexLocal(ptB)));
                                        }
                                        continue;
                                    }
                                default: continue;
                            }
                            edges.Add(new Edge(a, b, PackVertexLocal(a), PackVertexLocal(b)));
                        }
                    }
                    prevRowEmpty = currRowEmpty;
                }
            }

            // Inline IsRowAllZero — Burst can't call managed methods.
            private bool IsRowEmpty(int rowStart, int len)
            {
                for (int i = 0; i < len; i++) if (padBuffer[rowStart + i] != 0) return false;
                return true;
            }

            // Inline PackVertex — same algorithm, just local to keep Burst happy.
            private int PackVertexLocal(Vector2 v)
            {
                int x = (int)(v.x * 2f + 0.5f);
                int y = (int)(v.y * 2f + 0.5f);
                return y * packStride + x;
            }
        }

        // BURST TRACE CONTOURS
        // Walks the chain built from edges (nextNode[packA] = packB), traces each
        // unvisited start vertex, and emits world-space vertices into a flat
        // CSR-style layout: contoursVerts (concatenated vertices) + contourSpans
        // (start, length per contour).
        // Why CSR: NativeList<NativeList<T>> isn't supported in Burst. Flat layout
        // gives constant-time append + zero allocation per contour.
        // The `result[0]` output carries the new usedNodesCount back to managed
        // code so the next call can clean up the right slice of nextNode.
        [BurstCompile(CompileSynchronously = true)]
        private struct TraceContoursJob : IJob
        {
            [ReadOnly] public NativeArray<Edge> edges;
            public int edgeCount;

            public NativeArray<int> nextNode;
            public NativeArray<byte> vertexVisited;
            public NativeArray<int> usedNodes;

            public NativeList<Vector2> contoursVerts;
            public NativeList<int2> contourSpans;
            public NativeArray<int> result;       // [0] = new usedNodesCount

            public int prevUsedNodesCount;
            public int packStride;
            public float traceWorldScale;
            public float traceWorldOffset;

            public void Execute()
            {
                // Clean up state from the prior call's used vertex slots.
                int nnLen = nextNode.Length;
                int vvLen = vertexVisited.Length;
                for (int i = 0; i < prevUsedNodesCount; i++)
                {
                    int n = usedNodes[i];
                    if ((uint)n < (uint)nnLen) nextNode[n] = -1;
                    if ((uint)n < (uint)vvLen) vertexVisited[n] = 0;
                }

                contoursVerts.Clear();
                contourSpans.Clear();

                int usedCount = 0;
                int usedCap = usedNodes.Length - 1;

                // Pass 1: build chain (nextNode[packA] = packB) and record packA in usedNodes.
                // (uint) cast guards against negative pack values flowing in from
                // some upstream mismatch — a bare `va < nnLen` lets -1680 through.
                for (int i = 0; i < edgeCount; i++)
                {
                    Edge e = edges[i];
                    int va = e.packA;
                    int vb = e.packB;
                    if ((uint)va < (uint)nnLen) nextNode[va] = vb;
                    if (usedCount < usedCap)
                    {
                        usedNodes[usedCount++] = va;
                    }
                }

                // Pass 2: walk contours from each unvisited start vertex.
                for (int i = 0; i < edgeCount; i++)
                {
                    int start = edges[i].packA;
                    if ((uint)start >= (uint)vvLen || vertexVisited[start] != 0) continue;

                    int contourStart = contoursVerts.Length;
                    int cur = start;

                    while (true)
                    {
                        if ((uint)cur < (uint)vvLen) vertexVisited[cur] = 1;

                        Vector2 v = new Vector2(
                            (cur % packStride) * traceWorldScale + traceWorldOffset,
                            (cur / packStride) * traceWorldScale + traceWorldOffset);
                        contoursVerts.Add(v);

                        if ((uint)cur >= (uint)nnLen) break;
                        int nxt = nextNode[cur];
                        if (nxt < 0 || nxt >= vvLen || vertexVisited[nxt] != 0) break;
                        cur = nxt;
                    }

                    int contourLen = contoursVerts.Length - contourStart;
                    if (contourLen >= 3)
                    {
                        contourSpans.Add(new int2(contourStart, contourLen));
                    }
                    else
                    {
                        contoursVerts.Length = contourStart;
                    }
                }

                result[0] = usedCount;
            }
        }

        // BURST RDP SIMPLIFICATION
        // Runs Ramer-Douglas-Peucker on every contour from contoursVertsNative.
        // Output goes to simplifiedVertsNative + simplifiedSpansNative (CSR layout).
        // For closed loops we copy the contour into rdpScratch with a duplicate
        // first-vertex appended (matches the original SimplifyRDPClosedLoop's
        // c.Add(c[0]) trick). RDPIterativeBurst then walks src[s..e] linearly,
        // marking keepFlags. Filtering pass writes survivors to dstVerts.
        [BurstCompile(CompileSynchronously = true)]
        private struct RDPSimplifyJob : IJob
        {
            [ReadOnly] public NativeArray<Vector2> srcVerts;
            [ReadOnly] public NativeArray<int2> srcSpans;
            public NativeList<Vector2> dstVerts;
            public NativeList<int2> dstSpans;
            public NativeArray<byte> keepFlags;
            public NativeArray<int> rdpStack;
            public NativeArray<Vector2> rdpScratch;
            public float worldTol;

            public void Execute()
            {
                dstVerts.Clear();
                dstSpans.Clear();

                float tolSqr = worldTol * worldTol;
                int spanCount = srcSpans.Length;

                for (int s = 0; s < spanCount; s++)
                {
                    int2 span = srcSpans[s];
                    int srcStart = span.x;
                    int n = span.y;
                    if (n < 3) continue;

                    int dstStart = dstVerts.Length;

                    if (n < 8)
                    {
                        // Below threshold — skip RDP, copy verbatim.
                        for (int i = 0; i < n; i++) dstVerts.Add(srcVerts[srcStart + i]);
                    }
                    else
                    {
                        // Closed-loop trick: copy contour + duplicate first vertex
                        // at end into rdpScratch, then RDP on n+1 entries.
                        int loopLen = n + 1;
                        if (loopLen > rdpScratch.Length || loopLen > keepFlags.Length)
                        {
                            // Scratch too small — fall back to copy-verbatim. Won't
                            // happen at chunkSize=128 (max ~16k), but defensive.
                            for (int i = 0; i < n; i++) dstVerts.Add(srcVerts[srcStart + i]);
                        }
                        else
                        {
                            for (int i = 0; i < n; i++) rdpScratch[i] = srcVerts[srcStart + i];
                            rdpScratch[n] = rdpScratch[0]; // close

                            for (int i = 0; i < loopLen; i++) keepFlags[i] = 0;
                            int mid = loopLen / 2;
                            keepFlags[0] = 1;
                            keepFlags[mid] = 1;
                            keepFlags[loopLen - 1] = 1;

                            RDPRange(0, mid, tolSqr, loopLen);
                            RDPRange(mid, loopLen - 1, tolSqr, loopLen);

                            // Filter kept verts (skip the closing duplicate at loopLen-1).
                            for (int i = 0; i < loopLen - 1; i++)
                            {
                                if (keepFlags[i] != 0) dstVerts.Add(rdpScratch[i]);
                            }
                        }
                    }

                    int dstLen = dstVerts.Length - dstStart;
                    if (dstLen >= 3)
                    {
                        dstSpans.Add(new int2(dstStart, dstLen));
                    }
                    else
                    {
                        dstVerts.Length = dstStart;
                    }
                }
            }

            // Iterative RDP using a pre-sized stack. Marks keepFlags for retained
            // verts. Reads from rdpScratch which contains the closed-loop copy.
            private void RDPRange(int s0, int e0, float tolSqr, int loopLen)
            {
                int sp = 0;
                int stackCap = rdpStack.Length;
                rdpStack[sp++] = s0;
                rdpStack[sp++] = e0;
                while (sp > 0)
                {
                    int e = rdpStack[--sp];
                    int s = rdpStack[--sp];
                    if (e - s < 2) continue;

                    Vector2 a = rdpScratch[s];
                    Vector2 b = rdpScratch[e];
                    float dx = b.x - a.x, dy = b.y - a.y;
                    float lineLengthSqr = dx * dx + dy * dy;

                    float maxSqr = 0f;
                    int idx = s;
                    if (lineLengthSqr == 0f)
                    {
                        for (int i = s + 1; i < e; i++)
                        {
                            Vector2 p = rdpScratch[i];
                            float ddx = p.x - a.x, ddy = p.y - a.y;
                            float distSqr = ddx * ddx + ddy * ddy;
                            if (distSqr > maxSqr) { maxSqr = distSqr; idx = i; }
                        }
                    }
                    else
                    {
                        float invLenSqr = 1f / lineLengthSqr;
                        for (int i = s + 1; i < e; i++)
                        {
                            Vector2 p = rdpScratch[i];
                            float crossProduct = dx * (a.y - p.y) - (a.x - p.x) * dy;
                            float dSqr = (crossProduct * crossProduct) * invLenSqr;
                            if (dSqr > maxSqr) { maxSqr = dSqr; idx = i; }
                        }
                    }

                    if (maxSqr > tolSqr)
                    {
                        keepFlags[idx] = 1;
                        // Stack overflow guard: skip further subdivision rather
                        // than crash. The fallback gives a slightly less simplified
                        // contour but never breaks the rebuild.
                        if (sp + 4 <= stackCap)
                        {
                            rdpStack[sp++] = s; rdpStack[sp++] = idx;
                            rdpStack[sp++] = idx; rdpStack[sp++] = e;
                        }
                    }
                }
            }
        }

        // REBUILD CHUNK PIPELINE (PARALLEL)
        // Inlines ComputeChunkSignature + PopulateIsolatedPadBuffer + BuildEdges +
        // TraceContours + RDPSimplify into a single IJobParallelFor. Each Execute(slot)
        // processes one chunk using its own slice of per-slot scratch arrays.
        // Why one big Execute instead of separate jobs: every additional Schedule()
        // costs dispatch overhead. With many dirty chunks each needing several
        // jobs, that dispatch cost adds up. One unified job amortizes dispatch over
        // the entire batch.
        // Per-slot scratch slicing: each slot K reads/writes scratch[K*cap..(K+1)*cap].
        // [NativeDisableParallelForRestriction] tells the safety system this is safe
        // (workers never touch each other's slices).
        // Persistent state across frames: nextNodeBuffers and vertexVisitedBuffers
        // retain dirty entries from the slot's previous use. slotPrevUsedCount[slot]
        // remembers how many positions to clean before reuse.
        // Overflow: if a chunk's edge count exceeds REBUILD_EDGE_CAP, we flag
        // outOverflowed[slot]=1 and the main thread falls back to the sequential
        // RebuildChunk path for that chunk only. Rare in practice (>16k edges
        // requires extreme fragmentation).
        [BurstCompile(CompileSynchronously = true)]
        private struct RebuildChunkPipelineJob : IJobParallelFor
        {
            // Inputs
            [ReadOnly] public NativeArray<int> chunkIndices;
            [ReadOnly] public NativeArray<int> prevSignatures;
            [ReadOnly] public NativeArray<byte> solidMap;
            [ReadOnly] public NativeArray<int> caseLineCount;
            [ReadOnly] public NativeArray<float2> caseLineData;

            // Per-slot scratch (sliced by index)
            [NativeDisableParallelForRestriction] public NativeArray<byte> padBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<Edge> edgeBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<int> nextNodeBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<byte> vertexVisitedBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<int> usedNodesBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> contourVertsBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<int2> contourSpansBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> simplVertsBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<int2> simplSpansBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<int> rdpStackBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> rdpScratchBuffers;
            [NativeDisableParallelForRestriction] public NativeArray<byte> rdpKeepFlagsBuffers;

            // Per-slot counts / outputs
            [NativeDisableParallelForRestriction] public NativeArray<int> edgeCounts;
            [NativeDisableParallelForRestriction] public NativeArray<int> usedNodeCounts;
            [NativeDisableParallelForRestriction] public NativeArray<int> contourSpanCounts;
            [NativeDisableParallelForRestriction] public NativeArray<int> simplSpanCounts;
            [NativeDisableParallelForRestriction] public NativeArray<int> slotPrevUsedCount;
            [NativeDisableParallelForRestriction] public NativeArray<int> outNewSignatures;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outSignatureChanged;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outOverflowed;

            // Per-slot capacities
            public int padCap;
            public int edgeCap;
            public int maxNodes;
            public int usedCap;
            public int vertCap;
            public int spanCap;
            public int rdpStackCap;
            public int rdpScratchCap;
            public int rdpKeepCap;

            // Chunk parameters (shared — same DT instance, same physicsStep)
            public int chunkSize;
            public int physicsStep;
            public int chunksX;
            public int texWidth;
            public int texHeight;
            public int packStride;
            public int scaledChunkSize;
            public int padStride;
            public float traceWorldScale;
            public float traceWorldOffset;
            public float worldTol;

            public void Execute(int slot)
            {
                int chunkIdx = chunkIndices[slot];
                int cx = chunkIdx % chunksX;
                int cy = chunkIdx / chunksX;
                int baseX = cx * chunkSize;
                int baseY = cy * chunkSize;

                // STEP 1: SIGNATURE CHECK (early exit if unchanged).
                // Mirrors ComputeChunkSignatureJob: stride-2 hash over solidMap.
                int sigHash = 17;
                int sigEndY = baseY + chunkSize; if (sigEndY > texHeight) sigEndY = texHeight;
                int sigEndX = baseX + chunkSize; if (sigEndX > texWidth) sigEndX = texWidth;
                for (int gy = baseY; gy < sigEndY; gy += 2)
                {
                    int rowBase = gy * texWidth;
                    for (int gx = baseX; gx < sigEndX; gx += 2)
                    {
                        sigHash = sigHash * 31 + (solidMap[rowBase + gx] != 0 ? 1 : 0);
                    }
                }
                outNewSignatures[slot] = sigHash;

                int prevSig = prevSignatures[slot];
                // prevSig == -1 is the "force rebuild" sentinel. Otherwise, equal
                // signatures mean nothing changed — skip the rest of the pipeline.
                if (prevSig != -1 && prevSig == sigHash)
                {
                    outSignatureChanged[slot] = 0;
                    outOverflowed[slot] = 0;
                    return;
                }
                outSignatureChanged[slot] = 1;
                outOverflowed[slot] = 0;

                // STEP 2: POPULATE ISOLATED PAD BUFFER.
                // Mirrors PopulateIsolatedPadBufferJob.
                int padBase = slot * padCap;
                // Clear pad to zeros.
                for (int i = 0; i < padCap; i++) padBuffers[padBase + i] = 0;

                if (physicsStep == 1 && baseX >= 0 && baseY >= 0 &&
                    baseX + scaledChunkSize <= texWidth && baseY + scaledChunkSize <= texHeight)
                {
                    // Fast path: per-row in-bounds copy.
                    for (int y = 0; y < scaledChunkSize; y++)
                    {
                        int srcRow = (baseY + y) * texWidth + baseX;
                        int dstRow = (y + 1) * padStride + 1;
                        for (int i = 0; i < scaledChunkSize; i++)
                        {
                            padBuffers[padBase + dstRow + i] = solidMap[srcRow + i];
                        }
                    }
                }
                else
                {
                    // Slow path: downsampled and/or partially out-of-bounds.
                    int yLimit = scaledChunkSize;
                    int yClamp = (texHeight - baseY + physicsStep - 1) / physicsStep;
                    if (yClamp < yLimit) yLimit = yClamp;
                    int xLimit = scaledChunkSize;
                    int xClamp = (texWidth - baseX + physicsStep - 1) / physicsStep;
                    if (xClamp < xLimit) xLimit = xClamp;
                    for (int y = 0; y < yLimit; y++)
                    {
                        int gy = baseY + y * physicsStep;
                        if (gy < 0) continue;
                        int rowOffset = (y + 1) * padStride;
                        int srcRowBase = gy * texWidth;
                        for (int x = 0; x < xLimit; x++)
                        {
                            int gx = baseX + x * physicsStep;
                            if (gx < 0) continue;
                            padBuffers[padBase + rowOffset + x + 1] = solidMap[srcRowBase + gx];
                        }
                    }
                }

                // STEP 3: BUILD EDGES (marching squares).
                // Mirrors BuildEdgesJob, with overflow detection.
                int edgeBase = slot * edgeCap;
                int edgeCount = 0;
                int R = padStride, C = padStride;

                // IsRowEmpty inline (unrolled).
                bool prevRowEmpty = true;
                for (int i = 0; i < C; i++)
                {
                    if (padBuffers[padBase + i] != 0) { prevRowEmpty = false; break; }
                }

                for (int y = 0; y < R - 1; y++)
                {
                    int row0 = y * padStride;
                    int row1 = (y + 1) * padStride;
                    bool currRowEmpty = true;
                    for (int i = 0; i < C; i++)
                    {
                        if (padBuffers[padBase + row1 + i] != 0) { currRowEmpty = false; break; }
                    }

                    if (!(prevRowEmpty && currRowEmpty))
                    {
                        for (int x = 0; x < C - 1; x++)
                        {
                            int caseIdx = padBuffers[padBase + row0 + x]
                                        | (padBuffers[padBase + row0 + x + 1] << 1)
                                        | (padBuffers[padBase + row1 + x + 1] << 2)
                                        | (padBuffers[padBase + row1 + x] << 3);

                            Vector2 a, b;
                            switch (caseIdx)
                            {
                                case 0:
                                case 15: continue;
                                case 1: a = new Vector2(x + 0.5f, y); b = new Vector2(x, y + 0.5f); break;
                                case 2: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x + 0.5f, y); break;
                                case 4: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x + 1, y + 0.5f); break;
                                case 8: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 0.5f, y + 1); break;
                                case 3: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x, y + 0.5f); break;
                                case 12: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 1, y + 0.5f); break;
                                case 6: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x + 0.5f, y); break;
                                case 9: a = new Vector2(x + 0.5f, y); b = new Vector2(x + 0.5f, y + 1); break;
                                case 7: a = new Vector2(x + 0.5f, y + 1); b = new Vector2(x, y + 0.5f); break;
                                case 11: a = new Vector2(x + 1, y + 0.5f); b = new Vector2(x + 0.5f, y + 1); break;
                                case 13: a = new Vector2(x + 0.5f, y); b = new Vector2(x + 1, y + 0.5f); break;
                                case 14: a = new Vector2(x, y + 0.5f); b = new Vector2(x + 0.5f, y); break;
                                case 5:
                                case 10:
                                    {
                                        // Saddle: read endpoints from flat lookup.
                                        int count = caseLineCount[caseIdx];
                                        int basePtr = caseIdx * 4;
                                        for (int i = 0; i < count; i += 2)
                                        {
                                            if (edgeCount >= edgeCap) goto OVERFLOW;
                                            float2 lookA = caseLineData[basePtr + i];
                                            float2 lookB = caseLineData[basePtr + i + 1];
                                            Vector2 ptA = new Vector2(x + lookA.x, y + lookA.y);
                                            Vector2 ptB = new Vector2(x + lookB.x, y + lookB.y);
                                            int paA = ((int)(ptA.y * 2f + 0.5f)) * packStride + (int)(ptA.x * 2f + 0.5f);
                                            int paB = ((int)(ptB.y * 2f + 0.5f)) * packStride + (int)(ptB.x * 2f + 0.5f);
                                            edgeBuffers[edgeBase + edgeCount] = new Edge(ptA, ptB, paA, paB);
                                            edgeCount++;
                                        }
                                        continue;
                                    }
                                default: continue;
                            }
                            if (edgeCount >= edgeCap) goto OVERFLOW;
                            int packA = ((int)(a.y * 2f + 0.5f)) * packStride + (int)(a.x * 2f + 0.5f);
                            int packB = ((int)(b.y * 2f + 0.5f)) * packStride + (int)(b.x * 2f + 0.5f);
                            edgeBuffers[edgeBase + edgeCount] = new Edge(a, b, packA, packB);
                            edgeCount++;
                        }
                    }
                    prevRowEmpty = currRowEmpty;
                }

                edgeCounts[slot] = edgeCount;

                // STEP 4: TRACE CONTOURS.
                // Mirrors TraceContoursJob, but writes to per-slot CSR buffers
                // instead of NativeLists (NativeList isn't safe across parallel
                // workers, and per-slot capacity is bounded by REBUILD_VERT_CAP).
                int nnBase = slot * maxNodes;
                int vvBase = slot * maxNodes;
                int unBase = slot * usedCap;
                int cvBase = slot * vertCap;
                int csBase = slot * spanCap;

                // Cleanup of this slot's previous-iteration dirty entries.
                int prevUsedCount = slotPrevUsedCount[slot];
                for (int i = 0; i < prevUsedCount; i++)
                {
                    int n = usedNodesBuffers[unBase + i];
                    if ((uint)n < (uint)maxNodes)
                    {
                        nextNodeBuffers[nnBase + n] = -1;
                        vertexVisitedBuffers[vvBase + n] = 0;
                    }
                }

                int contourVertCount = 0;
                int contourSpanCount = 0;
                int usedCount = 0;
                int usedCapMinus1 = usedCap - 1;

                // Pass 1: chain edges (nextNode[packA] = packB).
                for (int i = 0; i < edgeCount; i++)
                {
                    Edge e = edgeBuffers[edgeBase + i];
                    int va = e.packA;
                    int vb = e.packB;
                    if ((uint)va < (uint)maxNodes) nextNodeBuffers[nnBase + va] = vb;
                    if (usedCount < usedCapMinus1)
                    {
                        usedNodesBuffers[unBase + usedCount++] = va;
                    }
                }

                // Pass 2: walk contours from each unvisited start vertex.
                for (int i = 0; i < edgeCount; i++)
                {
                    int start = edgeBuffers[edgeBase + i].packA;
                    if ((uint)start >= (uint)maxNodes || vertexVisitedBuffers[vvBase + start] != 0) continue;

                    int contourStart = contourVertCount;
                    int cur = start;
                    while (true)
                    {
                        if ((uint)cur < (uint)maxNodes) vertexVisitedBuffers[vvBase + cur] = 1;
                        // Capacity guard for output buffer.
                        if (contourVertCount >= vertCap) goto OVERFLOW;
                        contourVertsBuffers[cvBase + contourVertCount] = new Vector2(
                            (cur % packStride) * traceWorldScale + traceWorldOffset,
                            (cur / packStride) * traceWorldScale + traceWorldOffset);
                        contourVertCount++;

                        if ((uint)cur >= (uint)maxNodes) break;
                        int nxt = nextNodeBuffers[nnBase + cur];
                        if (nxt < 0 || nxt >= maxNodes || vertexVisitedBuffers[vvBase + nxt] != 0) break;
                        cur = nxt;
                    }

                    int contourLen = contourVertCount - contourStart;
                    if (contourLen >= 3)
                    {
                        if (contourSpanCount >= spanCap) goto OVERFLOW;
                        contourSpansBuffers[csBase + contourSpanCount] = new int2(contourStart, contourLen);
                        contourSpanCount++;
                    }
                    else
                    {
                        // Discard degenerate contour.
                        contourVertCount = contourStart;
                    }
                }

                slotPrevUsedCount[slot] = usedCount;
                usedNodeCounts[slot] = usedCount;
                contourSpanCounts[slot] = contourSpanCount;

                // STEP 5: RDP SIMPLIFY each contour.
                // Mirrors RDPSimplifyJob with per-slot scratch.
                int svBase = slot * vertCap;
                int ssBase = slot * spanCap;
                int rsBase = slot * rdpScratchCap;
                int rkBase = slot * rdpKeepCap;
                int rstkBase = slot * rdpStackCap;

                int simplVertCount = 0;
                int simplSpanCount = 0;
                float tolSqr = worldTol * worldTol;

                for (int s = 0; s < contourSpanCount; s++)
                {
                    int2 span = contourSpansBuffers[csBase + s];
                    int srcStart = span.x;
                    int n = span.y;
                    if (n < 3) continue;

                    int dstStart = simplVertCount;

                    if (n < 8)
                    {
                        // Below threshold — copy verbatim.
                        if (simplVertCount + n > vertCap) goto OVERFLOW;
                        for (int i = 0; i < n; i++)
                        {
                            simplVertsBuffers[svBase + simplVertCount++] = contourVertsBuffers[cvBase + srcStart + i];
                        }
                    }
                    else
                    {
                        int loopLen = n + 1;
                        if (loopLen > rdpScratchCap || loopLen > rdpKeepCap)
                        {
                            // Scratch too small — copy verbatim.
                            if (simplVertCount + n > vertCap) goto OVERFLOW;
                            for (int i = 0; i < n; i++)
                            {
                                simplVertsBuffers[svBase + simplVertCount++] = contourVertsBuffers[cvBase + srcStart + i];
                            }
                        }
                        else
                        {
                            // Closed-loop trick: copy contour + duplicate first vertex.
                            for (int i = 0; i < n; i++)
                            {
                                rdpScratchBuffers[rsBase + i] = contourVertsBuffers[cvBase + srcStart + i];
                            }
                            rdpScratchBuffers[rsBase + n] = rdpScratchBuffers[rsBase + 0];
                            for (int i = 0; i < loopLen; i++) rdpKeepFlagsBuffers[rkBase + i] = 0;
                            int mid = loopLen / 2;
                            rdpKeepFlagsBuffers[rkBase + 0] = 1;
                            rdpKeepFlagsBuffers[rkBase + mid] = 1;
                            rdpKeepFlagsBuffers[rkBase + loopLen - 1] = 1;

                            RDPRangeInline(rsBase, rkBase, rstkBase, 0, mid, tolSqr);
                            RDPRangeInline(rsBase, rkBase, rstkBase, mid, loopLen - 1, tolSqr);

                            // Filter kept verts (skip the closing duplicate).
                            for (int i = 0; i < loopLen - 1; i++)
                            {
                                if (rdpKeepFlagsBuffers[rkBase + i] != 0)
                                {
                                    if (simplVertCount >= vertCap) goto OVERFLOW;
                                    simplVertsBuffers[svBase + simplVertCount++] = rdpScratchBuffers[rsBase + i];
                                }
                            }
                        }
                    }

                    int dstLen = simplVertCount - dstStart;
                    if (dstLen >= 3)
                    {
                        if (simplSpanCount >= spanCap) goto OVERFLOW;
                        simplSpansBuffers[ssBase + simplSpanCount] = new int2(dstStart, dstLen);
                        simplSpanCount++;
                    }
                    else
                    {
                        // Discard degenerate.
                        simplVertCount = dstStart;
                    }
                }

                simplSpanCounts[slot] = simplSpanCount;
                return;

            // OVERFLOW: any per-slot capacity exhausted. Flag the slot for
            // sequential fallback and bail. Persistent state (slotPrevUsedCount)
            // already updated above for any partial work, so the slot remains
            // consistent.
            OVERFLOW:
                outOverflowed[slot] = 1;
                outSignatureChanged[slot] = 0; // main thread will rebuild via fallback, which writes signature
                edgeCounts[slot] = 0;
                contourSpanCounts[slot] = 0;
                simplSpanCounts[slot] = 0;
            }

            // Iterative RDP using per-slot scratch slices.
            private void RDPRangeInline(int rsBase, int rkBase, int rstkBase, int s0, int e0, float tolSqr)
            {
                int sp = 0;
                rdpStackBuffers[rstkBase + sp++] = s0;
                rdpStackBuffers[rstkBase + sp++] = e0;
                while (sp > 0)
                {
                    int e = rdpStackBuffers[rstkBase + (--sp)];
                    int s = rdpStackBuffers[rstkBase + (--sp)];
                    if (e - s < 2) continue;

                    Vector2 a = rdpScratchBuffers[rsBase + s];
                    Vector2 b = rdpScratchBuffers[rsBase + e];
                    float dx = b.x - a.x, dy = b.y - a.y;
                    float lineLengthSqr = dx * dx + dy * dy;

                    float maxSqr = 0f;
                    int idx = s;
                    if (lineLengthSqr == 0f)
                    {
                        for (int i = s + 1; i < e; i++)
                        {
                            Vector2 p = rdpScratchBuffers[rsBase + i];
                            float ddx = p.x - a.x, ddy = p.y - a.y;
                            float distSqr = ddx * ddx + ddy * ddy;
                            if (distSqr > maxSqr) { maxSqr = distSqr; idx = i; }
                        }
                    }
                    else
                    {
                        float invLenSqr = 1f / lineLengthSqr;
                        for (int i = s + 1; i < e; i++)
                        {
                            Vector2 p = rdpScratchBuffers[rsBase + i];
                            float crossProduct = dx * (a.y - p.y) - (a.x - p.x) * dy;
                            float dSqr = (crossProduct * crossProduct) * invLenSqr;
                            if (dSqr > maxSqr) { maxSqr = dSqr; idx = i; }
                        }
                    }

                    if (maxSqr > tolSqr)
                    {
                        rdpKeepFlagsBuffers[rkBase + idx] = 1;
                        if (sp + 4 <= rdpStackCap)
                        {
                            rdpStackBuffers[rstkBase + sp++] = s; rdpStackBuffers[rstkBase + sp++] = idx;
                            rdpStackBuffers[rstkBase + sp++] = idx; rdpStackBuffers[rstkBase + sp++] = e;
                        }
                    }
                }
            }
        }

        // Dirty-chunk bitset used by the extraction jobs. Sized to chunksX*chunksY
        // (max ~1024 for 4096^2 textures with chunkSize=128). Populated by Burst,
        // scanned by managed code post-job to call FlagChunkDirty + chunkSignatures.
        private static NativeArray<byte> _dirtyChunkBitset;

        // Off-thread BFS via Schedule()/Complete() across frames.
        // The synchronous BFS pipeline (ScanBoundaryForIslands -> CheckBoundaryPixel ->
        // DetectFloatingIsland) ran on the main thread during
        // continuous digging. The boundary scan + BFS work is moved off-thread for
        // mainland: the job is scheduled at end of LateUpdate damage processing and
        // completed at the start of the NEXT frame's LateUpdate. Detachment spawning
        // gets a 1-frame latency — imperceptible.
        // Falling chunks keep the synchronous DetectFloatingIsland wrapper because
        // they rarely run BFS and the wrapper waits for any in-flight async via
        // _globalBfsHandle.Complete() to avoid races on shared NativeArrays.
        private struct PendingScanRequest
        {
            public int pixCX;
            public int pixCY;
            public int radiusPx;
        }

        // BatchedScanAndBfsJob runs Bresenham boundary scans + BFS for all queued
        // damage events in a single Burst job. Stops on the first floating region
        // found (matches prior synchronous behavior). Multiple detachments per frame
        // would take multiple frames to spawn — acceptable for typical play.
        [BurstCompile(CompileSynchronously = true)]
        private struct BatchedScanAndBfsJob : IJob
        {
            // solidMap here is _bfsSolidSnapshot (a frozen copy taken at dispatch), NOT
            // the live map. Reading the live map raced the dig: per-byte atomicity stops
            // torn bytes but NOT torn traversal, so the flood could see a still-attached
            // sliver as disconnected mid-walk → false floating → evaporated 1px gap. The
            // snapshot gives a consistent graph. Attribute kept because the snapshot is a
            // shared static array; dispatch is serialized (_globalBfsInFlight) so the copy
            // never overwrites it while a prior flood still reads it.
            [Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction]
            [ReadOnly] public NativeArray<byte> solidMap;
            public NativeArray<int> pixelVisitId;
            public NativeArray<int> floodStack;
            public NativeArray<int> islandPixelBuffer;
            [ReadOnly] public NativeArray<PendingScanRequest> pendingScans;
            public int pendingScanCount;
            public NativeArray<int> outputBuffer; // per-instance snapshot of islandPixelBuffer
            public NativeArray<int> result;       // per-instance 7-int result

            public int texWidth, texHeight;
            public bool isMainland;
            public int groundAnchorY;
            public bool anchorToSideBorders;
            public bool anchorWholeBody;
            public int maxPhysicsPixels;
            public int mainlandId;
            public int startSearchId;
            public int currentSearchIdIn;
            public int currentSolidPixels;

            public void Execute()
            {
                int currentSearchId = currentSearchIdIn;
                bool foundFloating = false;
                int finalIslandPixelCount = 0;
                int finalIslandMinX = texWidth, finalIslandMaxX = 0;
                int finalIslandMinY = texHeight, finalIslandMaxY = 0;

                for (int s = 0; s < pendingScanCount && !foundFloating; s++)
                {
                    int pixCX = pendingScans[s].pixCX;
                    int pixCY = pendingScans[s].pixCY;
                    int radiusPx = pendingScans[s].radiusPx;

                    int r = radiusPx + 1;
                    int xx = r, yy = 0;
                    int err = 1 - r;

                    while (xx >= yy && !foundFloating)
                    {
                        if (TryDetectFloating(pixCX + xx, pixCY + yy, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX - xx, pixCY + yy, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX + xx, pixCY - yy, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX - xx, pixCY - yy, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX + yy, pixCY + xx, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX - yy, pixCY + xx, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX + yy, pixCY - xx, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }
                        if (TryDetectFloating(pixCX - yy, pixCY - xx, ref currentSearchId,
                            ref finalIslandPixelCount, ref finalIslandMinX, ref finalIslandMaxX,
                            ref finalIslandMinY, ref finalIslandMaxY)) { foundFloating = true; break; }

                        yy++;
                        if (err < 0) err += 2 * yy + 1;
                        else { xx--; err += 2 * (yy - xx) + 1; }
                    }
                }

                result[0] = currentSearchId;
                result[1] = foundFloating ? 1 : 0;
                result[2] = finalIslandPixelCount;
                result[3] = finalIslandMinX;
                result[4] = finalIslandMaxX;
                result[5] = finalIslandMinY;
                result[6] = finalIslandMaxY;

                if (foundFloating)
                {
                    for (int i = 0; i < finalIslandPixelCount; i++)
                    {
                        outputBuffer[i] = islandPixelBuffer[i];
                    }
                }
            }

            private bool TryDetectFloating(int gx, int gy, ref int currentSearchId,
                ref int outIslandPixelCount, ref int outIslandMinX, ref int outIslandMaxX,
                ref int outIslandMinY, ref int outIslandMaxY)
            {
                if (gx < 0 || gx >= texWidth || gy < 0 || gy >= texHeight) return false;
                int index = gy * texWidth + gx;
                if (solidMap[index] == 0) return false;
                int v = pixelVisitId[index];
                if ((v >= startSearchId && v <= currentSearchId) || v == mainlandId) return false;

                currentSearchId++;
                int totalPixels = texWidth * texHeight;
                int islandPixelCount = 0;
                int islandMinX = texWidth, islandMaxX = 0;
                int islandMinY = texHeight, islandMaxY = 0;

                int startIdx = gy * texWidth + gx;
                if (startIdx < 0 || startIdx >= totalPixels || solidMap[startIdx] == 0) return false;

                int head = 0, tail = 0;
                int stackLen = floodStack.Length;
                floodStack[tail++] = startIdx;

                bool isAnchored = false;

                while (head < tail)
                {
                    int seedIdx = floodStack[head++];
                    if (pixelVisitId[seedIdx] == currentSearchId) continue;

                    int cy = seedIdx / texWidth;
                    int seedX = seedIdx - cy * texWidth;
                    int rowBase = cy * texWidth;

                    int leftX = seedX;
                    while (leftX > 0 && solidMap[rowBase + leftX - 1] != 0 && pixelVisitId[rowBase + leftX - 1] != currentSearchId)
                    {
                        leftX--;
                    }

                    int rightX = seedX;
                    while (rightX < texWidth - 1 && solidMap[rowBase + rightX + 1] != 0 && pixelVisitId[rowBase + rightX + 1] != currentSearchId)
                    {
                        rightX++;
                    }

                    if (isMainland)
                    {
                        if (cy <= groundAnchorY || (anchorToSideBorders && (leftX <= 0 || rightX >= texWidth - 1)))
                        {
                            isAnchored = true; break;
                        }
                    }

                    if (leftX < islandMinX) islandMinX = leftX;
                    if (rightX > islandMaxX) islandMaxX = rightX;
                    if (cy < islandMinY) islandMinY = cy;
                    if (cy > islandMaxY) islandMaxY = cy;

                    for (int x = leftX; x <= rightX; x++)
                    {
                        int idx = rowBase + x;
                        int vv = pixelVisitId[idx];

                        if (vv == mainlandId || (vv >= startSearchId && vv < currentSearchId))
                        {
                            isAnchored = true; break;
                        }

                        if (isMainland && islandPixelCount >= maxPhysicsPixels)
                        {
                            isAnchored = true; break;
                        }

                        if (islandPixelCount >= islandPixelBuffer.Length)
                        {
                            isAnchored = true; break;
                        }

                        pixelVisitId[idx] = currentSearchId;
                        islandPixelBuffer[islandPixelCount++] = idx;
                    }
                    if (isAnchored) break;

                    // 8-connected: scan one px past the span so a diagonal pinch doesn't split a region.
                    int sx = leftX > 0 ? leftX - 1 : 0;
                    int ex = rightX < texWidth - 1 ? rightX + 1 : texWidth - 1;

                    if (cy < texHeight - 1)
                    {
                        int upBase = rowBase + texWidth;
                        bool inSpan = false;
                        for (int x = sx; x <= ex; x++)
                        {
                            int uIdx = upBase + x;
                            if (solidMap[uIdx] != 0 && pixelVisitId[uIdx] != currentSearchId)
                            {
                                if (!inSpan)
                                {
                                    if (tail >= stackLen) { isAnchored = true; break; }
                                    floodStack[tail++] = uIdx;
                                    inSpan = true;
                                }
                            }
                            else inSpan = false;
                        }
                    }

                    if (cy > 0)
                    {
                        int dnBase = rowBase - texWidth;
                        bool inSpan = false;
                        for (int x = sx; x <= ex; x++)
                        {
                            int dIdx = dnBase + x;
                            if (solidMap[dIdx] != 0 && pixelVisitId[dIdx] != currentSearchId)
                            {
                                if (!inSpan)
                                {
                                    if (tail >= stackLen) { isAnchored = true; break; }
                                    floodStack[tail++] = dIdx;
                                    inSpan = true;
                                }
                            }
                            else inSpan = false;
                        }
                    }
                }

                if ((!isMainland || anchorWholeBody) && !isAnchored)
                {
                    if (islandPixelCount >= currentSolidPixels - 5)
                    {
                        isAnchored = true;
                    }
                }

                if (isAnchored)
                {
                    for (int i = 0; i < islandPixelCount; i++)
                    {
                        pixelVisitId[islandPixelBuffer[i]] = mainlandId;
                    }
                    return false;
                }
                else
                {
                    outIslandPixelCount = islandPixelCount;
                    outIslandMinX = islandMinX;
                    outIslandMaxX = islandMaxX;
                    outIslandMinY = islandMinY;
                    outIslandMaxY = islandMaxY;
                    return true;
                }
            }
        }

        // Per-instance fields. Only mainland actively uses them — falling
        // chunks fall back to the synchronous DetectFloatingIsland wrapper which
        // waits for _globalBfsHandle to drain before running.
        private NativeList<PendingScanRequest> _pendingScansNative;
        private NativeArray<int> _myBfsResult;       // 7 ints (mirrors _bfsResult layout)
        private NativeArray<int> _bfsOutputBuffer;   // snapshot of islandPixelBuffer if floating
        private JobHandle _myBfsHandle;
        private bool _hasPendingDetection;

        // Burst MergeStampJob support
        private NativeList<MergeParticleEvent> _mergeParticles;
        private NativeArray<int> _mergeResult;       // 5 ints: minX, maxX, minY, maxY, mapChanged
        private NativeArray<int> _sigResult;         // 1 int for ComputeChunkSignatureJob
                                                     // Burst EraseBrushJob support
        private NativeArray<int> _eraseResult;       // 6 ints: minX, maxX, minY, maxY, changed, solidDelta

        // Deferred-disable of parent SpriteRenderer for falling chunks
        // that use deferred-upload visual sub-chunks. Set true at spawn; cleared
        // by ApplyVisuals once dirtyVisualsList drains (= all sub-chunks have had
        // their first GPU upload).
        private bool _deferredParentDisable;
        // Global serialization: ALL BFS jobs (sync wrapper or async) chain through
        // this handle so they don't race on the shared static NativeArrays.
        private static JobHandle _globalBfsHandle;
        // Global gate: only ONE instance may have a BFS dispatched at a time. The BFS
        // scratch buffers (pixelVisitId/floodStack/islandPixelBuffer) and the search-id
        // state are static/shared; two same-frame dispatches capture the same id range
        // and corrupt each other on the shared buffer (each indexes with its own
        // texWidth). Serializing dispatch keeps the ranges disjoint. Released wherever
        // _hasPendingDetection is cleared (completion / OnDisable / OnDestroy / reset).
        private static bool _globalBfsInFlight;

        // Burst-compiled DetectFloatingIsland BFS. The BFS is the single most expensive
        // operation in the system — visits up to maxPhysicsPixels (5M) per call.
        // Without Burst, NativeArray safety checks add 30-50ns per access; the BFS
        // becomes 5-10x SLOWER than managed arrays. With Burst, those checks compile
        // out and we get raw native speed.
        // Result layout (7 ints):
        //   [0] new currentSearchId (we increment inside the job)
        //   [1] isFloating: 1 if floating, 0 if anchored
        //   [2] islandPixelCount
        //   [3..6] AABB: minX, maxX, minY, maxY
        private static NativeArray<int> _bfsResult;

        [BurstCompile(CompileSynchronously = true)]
        private struct DetectFloatingIslandJob : IJob
        {
            [ReadOnly] public NativeArray<byte> solidMap;
            public NativeArray<int> pixelVisitId;
            public NativeArray<int> islandPixelBuffer;
            public NativeArray<int> floodStack;
            public NativeArray<int> result;

            public int texWidth, texHeight;
            public bool isMainland;
            public int groundAnchorY;
            public bool anchorToSideBorders;
            public bool anchorWholeBody;
            public int maxPhysicsPixels;
            public int mainlandId;
            public int startSearchId;
            public int currentSearchIdIn;
            public int currentSolidPixels;
            public int startX, startY;

            public void Execute()
            {
                int currentSearchId = currentSearchIdIn + 1;
                result[0] = currentSearchId;

                int totalPixels = texWidth * texHeight;
                int islandPixelCount = 0;
                int islandMinX = texWidth, islandMaxX = 0;
                int islandMinY = texHeight, islandMaxY = 0;

                int startIdx = startY * texWidth + startX;
                if (startIdx < 0 || startIdx >= totalPixels || solidMap[startIdx] == 0)
                {
                    result[1] = 0; result[2] = 0;
                    result[3] = islandMinX; result[4] = islandMaxX;
                    result[5] = islandMinY; result[6] = islandMaxY;
                    return;
                }

                int vStart = pixelVisitId[startIdx];
                if ((vStart >= startSearchId && vStart <= currentSearchId) || vStart == mainlandId)
                {
                    result[1] = 0; result[2] = 0;
                    result[3] = islandMinX; result[4] = islandMaxX;
                    result[5] = islandMinY; result[6] = islandMaxY;
                    return;
                }

                int head = 0, tail = 0;
                int stackLen = floodStack.Length;
                floodStack[tail++] = startIdx;

                bool isAnchored = false;

                while (head < tail)
                {
                    int seedIdx = floodStack[head++];
                    if (pixelVisitId[seedIdx] == currentSearchId) continue;

                    int cy = seedIdx / texWidth;
                    int seedX = seedIdx - cy * texWidth;
                    int rowBase = cy * texWidth;

                    int leftX = seedX;
                    while (leftX > 0 && solidMap[rowBase + leftX - 1] != 0 && pixelVisitId[rowBase + leftX - 1] != currentSearchId)
                    {
                        leftX--;
                    }

                    int rightX = seedX;
                    while (rightX < texWidth - 1 && solidMap[rowBase + rightX + 1] != 0 && pixelVisitId[rowBase + rightX + 1] != currentSearchId)
                    {
                        rightX++;
                    }

                    if (isMainland)
                    {
                        if (cy <= groundAnchorY || (anchorToSideBorders && (leftX <= 0 || rightX >= texWidth - 1)))
                        {
                            isAnchored = true; break;
                        }
                    }

                    if (leftX < islandMinX) islandMinX = leftX;
                    if (rightX > islandMaxX) islandMaxX = rightX;
                    if (cy < islandMinY) islandMinY = cy;
                    if (cy > islandMaxY) islandMaxY = cy;

                    for (int x = leftX; x <= rightX; x++)
                    {
                        int idx = rowBase + x;
                        int v = pixelVisitId[idx];

                        if (v == mainlandId || (v >= startSearchId && v < currentSearchId))
                        {
                            isAnchored = true; break;
                        }

                        if (isMainland && islandPixelCount >= maxPhysicsPixels)
                        {
                            isAnchored = true; break;
                        }

                        if (islandPixelCount >= islandPixelBuffer.Length)
                        {
                            isAnchored = true; break;
                        }

                        pixelVisitId[idx] = currentSearchId;
                        islandPixelBuffer[islandPixelCount++] = idx;
                    }
                    if (isAnchored) break;

                    // 8-connected: scan one px past the span so a diagonal pinch doesn't split a region.
                    int sx = leftX > 0 ? leftX - 1 : 0;
                    int ex = rightX < texWidth - 1 ? rightX + 1 : texWidth - 1;

                    if (cy < texHeight - 1)
                    {
                        int upBase = rowBase + texWidth;
                        bool inSpan = false;
                        for (int x = sx; x <= ex; x++)
                        {
                            int uIdx = upBase + x;
                            if (solidMap[uIdx] != 0 && pixelVisitId[uIdx] != currentSearchId)
                            {
                                if (!inSpan)
                                {
                                    if (tail >= stackLen) { isAnchored = true; break; }
                                    floodStack[tail++] = uIdx;
                                    inSpan = true;
                                }
                            }
                            else inSpan = false;
                        }
                    }

                    if (cy > 0)
                    {
                        int dnBase = rowBase - texWidth;
                        bool inSpan = false;
                        for (int x = sx; x <= ex; x++)
                        {
                            int dIdx = dnBase + x;
                            if (solidMap[dIdx] != 0 && pixelVisitId[dIdx] != currentSearchId)
                            {
                                if (!inSpan)
                                {
                                    if (tail >= stackLen) { isAnchored = true; break; }
                                    floodStack[tail++] = dIdx;
                                    inSpan = true;
                                }
                            }
                            else inSpan = false;
                        }
                    }
                }

                if ((!isMainland || anchorWholeBody) && !isAnchored)
                {
                    if (islandPixelCount >= currentSolidPixels - 5)
                    {
                        isAnchored = true;
                    }
                }

                if (isAnchored)
                {
                    for (int i = 0; i < islandPixelCount; i++)
                    {
                        pixelVisitId[islandPixelBuffer[i]] = mainlandId;
                    }
                    islandPixelCount = 0;
                }

                result[1] = isAnchored ? 0 : 1;
                result[2] = islandPixelCount;
                result[3] = islandMinX;
                result[4] = islandMaxX;
                result[5] = islandMinY;
                result[6] = islandMaxY;
            }
        }

        /// <summary>
        /// Disposes static NativeArrays at the start of each play session.
        /// Without this, in-editor with Domain Reload disabled, NativeArrays from
        /// the previous play session persist and Unity warns "X allocations leaked".
        /// Runs before any Awake/Start.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticNativeArrays()
        {
            if (pixelVisitId.IsCreated) pixelVisitId.Dispose();
            if (_bfsSolidSnapshot.IsCreated) _bfsSolidSnapshot.Dispose();
            if (floodStack.IsCreated) floodStack.Dispose();
            if (islandPixelBuffer.IsCreated) islandPixelBuffer.Dispose();
            if (_bfsResult.IsCreated) _bfsResult.Dispose();
            if (_dirtyChunkBitset.IsCreated) _dirtyChunkBitset.Dispose();
            // native marching squares buffers.
            if (padBufferNative.IsCreated) padBufferNative.Dispose();
            if (caseLineCount.IsCreated) caseLineCount.Dispose();
            if (caseLineData.IsCreated) caseLineData.Dispose();
            if (edgesNative.IsCreated) edgesNative.Dispose();
            if (nextNodeNative.IsCreated) nextNodeNative.Dispose();
            if (vertexVisitedNative.IsCreated) vertexVisitedNative.Dispose();
            if (usedNodesNative.IsCreated) usedNodesNative.Dispose();
            if (rdpKeepFlagsNative.IsCreated) rdpKeepFlagsNative.Dispose();
            pixelVisitId = default;
            floodStack = default;
            islandPixelBuffer = default;
            _globalBfsInFlight = false;
            _bfsResult = default;
            _dirtyChunkBitset = default;
            padBufferNative = default;
            caseLineCount = default;
            caseLineData = default;
            edgesNative = default;
            nextNodeNative = default;
            vertexVisitedNative = default;
            usedNodesNative = default;
            rdpKeepFlagsNative = default;
            // Contour output containers.
            if (contoursVertsNative.IsCreated) contoursVertsNative.Dispose();
            if (contourSpansNative.IsCreated) contourSpansNative.Dispose();
            if (_traceResultNative.IsCreated) _traceResultNative.Dispose();
            contoursVertsNative = default;
            contourSpansNative = default;
            _traceResultNative = default;
            // Simplified contour outputs and RDP scratch.
            if (simplifiedVertsNative.IsCreated) simplifiedVertsNative.Dispose();
            if (simplifiedSpansNative.IsCreated) simplifiedSpansNative.Dispose();
            if (rdpStackNative.IsCreated) rdpStackNative.Dispose();
            if (rdpScratchNative.IsCreated) rdpScratchNative.Dispose();
            simplifiedVertsNative = default;
            simplifiedSpansNative = default;
            rdpStackNative = default;
            rdpScratchNative = default;

            // per-slot parallel rebuild buffers.
            if (padBuffersB.IsCreated) padBuffersB.Dispose();
            if (edgeBuffersB.IsCreated) edgeBuffersB.Dispose();
            if (nextNodeBuffersB.IsCreated) nextNodeBuffersB.Dispose();
            if (vertexVisitedBuffersB.IsCreated) vertexVisitedBuffersB.Dispose();
            if (usedNodesBuffersB.IsCreated) usedNodesBuffersB.Dispose();
            if (contourVertsBuffersB.IsCreated) contourVertsBuffersB.Dispose();
            if (contourSpansBuffersB.IsCreated) contourSpansBuffersB.Dispose();
            if (simplVertsBuffersB.IsCreated) simplVertsBuffersB.Dispose();
            if (simplSpansBuffersB.IsCreated) simplSpansBuffersB.Dispose();
            if (rdpStackBuffersB.IsCreated) rdpStackBuffersB.Dispose();
            if (rdpScratchBuffersB.IsCreated) rdpScratchBuffersB.Dispose();
            if (rdpKeepFlagsBuffersB.IsCreated) rdpKeepFlagsBuffersB.Dispose();
            if (edgeCountsB.IsCreated) edgeCountsB.Dispose();
            if (usedNodeCountsB.IsCreated) usedNodeCountsB.Dispose();
            if (contourSpanCountsB.IsCreated) contourSpanCountsB.Dispose();
            if (simplSpanCountsB.IsCreated) simplSpanCountsB.Dispose();
            if (slotPrevUsedCountB.IsCreated) slotPrevUsedCountB.Dispose();
            if (outNewSignaturesB.IsCreated) outNewSignaturesB.Dispose();
            if (outSignatureChangedB.IsCreated) outSignatureChangedB.Dispose();
            if (outOverflowedB.IsCreated) outOverflowedB.Dispose();
            if (chunkIndicesB.IsCreated) chunkIndicesB.Dispose();
            if (prevSignaturesB.IsCreated) prevSignaturesB.Dispose();
            padBuffersB = default; edgeBuffersB = default; nextNodeBuffersB = default;
            vertexVisitedBuffersB = default; usedNodesBuffersB = default;
            contourVertsBuffersB = default; contourSpansBuffersB = default;
            simplVertsBuffersB = default; simplSpansBuffersB = default;
            rdpStackBuffersB = default; rdpScratchBuffersB = default; rdpKeepFlagsBuffersB = default;
            edgeCountsB = default; usedNodeCountsB = default;
            contourSpanCountsB = default; simplSpanCountsB = default;
            slotPrevUsedCountB = default;
            outNewSignaturesB = default; outSignatureChangedB = default; outOverflowedB = default;
            chunkIndicesB = default; prevSignaturesB = default;
            // caseLookupInitialized must reset alongside caseLineData so that the
            // next play session re-populates the (now-disposed) native tables.
            caseLookupInitialized = false;
            // prewarm flag resets too — next session must re-trigger Burst JIT.
            _burstJobsPrewarmed = false;
            // clear the global BFS chain handle. Any per-instance handles
            // from a previous play session are stale; per-instance OnDestroy and
            // re-Awake will rebuild them. The global handle just needs to be reset
            // to default so the next Schedule() doesn't depend on a dead handle.
            _globalBfsHandle = default;

            // Subscribe once for cleanup on play mode exit / app quit. The
            // RuntimeInitializeOnLoadMethod above runs at PLAY MODE ENTRY, so
            // without this hook, native memory leaks every time the user stops
            // play mode in the editor (Unity reports "Persistent allocates N
            // individual allocations").
            // -= first to avoid double-subscribe across domain reloads.
            Application.quitting -= DisposeStaticNativeArrays;
            Application.quitting += DisposeStaticNativeArrays;
        }

        private static void DisposeStaticNativeArrays()
        {
            if (pixelVisitId.IsCreated) pixelVisitId.Dispose();
            if (_bfsSolidSnapshot.IsCreated) _bfsSolidSnapshot.Dispose();
            if (floodStack.IsCreated) floodStack.Dispose();
            if (islandPixelBuffer.IsCreated) islandPixelBuffer.Dispose();
            if (_bfsResult.IsCreated) _bfsResult.Dispose();
            if (_dirtyChunkBitset.IsCreated) _dirtyChunkBitset.Dispose();
            // native marching squares buffers.
            if (padBufferNative.IsCreated) padBufferNative.Dispose();
            if (caseLineCount.IsCreated) caseLineCount.Dispose();
            if (caseLineData.IsCreated) caseLineData.Dispose();
            if (edgesNative.IsCreated) edgesNative.Dispose();
            if (nextNodeNative.IsCreated) nextNodeNative.Dispose();
            if (vertexVisitedNative.IsCreated) vertexVisitedNative.Dispose();
            if (usedNodesNative.IsCreated) usedNodesNative.Dispose();
            if (rdpKeepFlagsNative.IsCreated) rdpKeepFlagsNative.Dispose();
            pixelVisitId = default;
            floodStack = default;
            islandPixelBuffer = default;
            _globalBfsInFlight = false;
            _bfsResult = default;
            _dirtyChunkBitset = default;
            padBufferNative = default;
            caseLineCount = default;
            caseLineData = default;
            edgesNative = default;
            nextNodeNative = default;
            vertexVisitedNative = default;
            usedNodesNative = default;
            rdpKeepFlagsNative = default;
            if (contoursVertsNative.IsCreated) contoursVertsNative.Dispose();
            if (contourSpansNative.IsCreated) contourSpansNative.Dispose();
            if (_traceResultNative.IsCreated) _traceResultNative.Dispose();
            contoursVertsNative = default;
            contourSpansNative = default;
            _traceResultNative = default;
            if (simplifiedVertsNative.IsCreated) simplifiedVertsNative.Dispose();
            if (simplifiedSpansNative.IsCreated) simplifiedSpansNative.Dispose();
            if (rdpStackNative.IsCreated) rdpStackNative.Dispose();
            if (rdpScratchNative.IsCreated) rdpScratchNative.Dispose();
            simplifiedVertsNative = default;
            simplifiedSpansNative = default;
            rdpStackNative = default;
            rdpScratchNative = default;

            // per-slot parallel rebuild buffers.
            if (padBuffersB.IsCreated) padBuffersB.Dispose();
            if (edgeBuffersB.IsCreated) edgeBuffersB.Dispose();
            if (nextNodeBuffersB.IsCreated) nextNodeBuffersB.Dispose();
            if (vertexVisitedBuffersB.IsCreated) vertexVisitedBuffersB.Dispose();
            if (usedNodesBuffersB.IsCreated) usedNodesBuffersB.Dispose();
            if (contourVertsBuffersB.IsCreated) contourVertsBuffersB.Dispose();
            if (contourSpansBuffersB.IsCreated) contourSpansBuffersB.Dispose();
            if (simplVertsBuffersB.IsCreated) simplVertsBuffersB.Dispose();
            if (simplSpansBuffersB.IsCreated) simplSpansBuffersB.Dispose();
            if (rdpStackBuffersB.IsCreated) rdpStackBuffersB.Dispose();
            if (rdpScratchBuffersB.IsCreated) rdpScratchBuffersB.Dispose();
            if (rdpKeepFlagsBuffersB.IsCreated) rdpKeepFlagsBuffersB.Dispose();
            if (edgeCountsB.IsCreated) edgeCountsB.Dispose();
            if (usedNodeCountsB.IsCreated) usedNodeCountsB.Dispose();
            if (contourSpanCountsB.IsCreated) contourSpanCountsB.Dispose();
            if (simplSpanCountsB.IsCreated) simplSpanCountsB.Dispose();
            if (slotPrevUsedCountB.IsCreated) slotPrevUsedCountB.Dispose();
            if (outNewSignaturesB.IsCreated) outNewSignaturesB.Dispose();
            if (outSignatureChangedB.IsCreated) outSignatureChangedB.Dispose();
            if (outOverflowedB.IsCreated) outOverflowedB.Dispose();
            if (chunkIndicesB.IsCreated) chunkIndicesB.Dispose();
            if (prevSignaturesB.IsCreated) prevSignaturesB.Dispose();
            padBuffersB = default; edgeBuffersB = default; nextNodeBuffersB = default;
            vertexVisitedBuffersB = default; usedNodesBuffersB = default;
            contourVertsBuffersB = default; contourSpansBuffersB = default;
            simplVertsBuffersB = default; simplSpansBuffersB = default;
            rdpStackBuffersB = default; rdpScratchBuffersB = default; rdpKeepFlagsBuffersB = default;
            edgeCountsB = default; usedNodeCountsB = default;
            contourSpanCountsB = default; simplSpanCountsB = default;
            slotPrevUsedCountB = default;
            outNewSignaturesB = default; outSignatureChangedB = default; outOverflowedB = default;
            chunkIndicesB = default; prevSignaturesB = default;
            caseLookupInitialized = false;
            _burstJobsPrewarmed = false;
        }

        void OnDisable()
        {
            // Pooling deactivates chunks via SetActive(false), which fires OnDisable
            // (not OnDestroy). If we were the BFS dispatcher, release the global lock
            // here so a pooled chunk can't deadlock detection for every instance.
            if (_hasPendingDetection)
            {
                _globalBfsHandle.Complete();
                _hasPendingDetection = false;
                _globalBfsInFlight = false;
            }
        }

        void OnDestroy()
        {
            // Drain any in-flight async BFS we own before disposing buffers.
            if (_hasPendingDetection)
            {
                _myBfsHandle.Complete();
                _hasPendingDetection = false;
                _globalBfsInFlight = false;
            }
            if (solidMap.IsCreated) solidMap.Dispose();
            if (_pendingScansNative.IsCreated) _pendingScansNative.Dispose();
            if (_myBfsResult.IsCreated) _myBfsResult.Dispose();
            if (_bfsOutputBuffer.IsCreated) _bfsOutputBuffer.Dispose();
            if (_mergeParticles.IsCreated) _mergeParticles.Dispose();
            if (_mergeResult.IsCreated) _mergeResult.Dispose();
            if (_sigResult.IsCreated) _sigResult.Dispose();
            if (_eraseResult.IsCreated) _eraseResult.Dispose();
            // release the pooled falling-chunk Texture2D if held.
            if (_pooledTexture != null) { Destroy(_pooledTexture); _pooledTexture = null; }
            // dispose pending init snapshot if still queued.
            if (_pendingInitSnapshot.IsCreated)
            {
                _pendingInitSnapshot.Dispose();
                _hasPendingInit = false;
            }
            // remove from slept-chunk registry if still listed. Static
            // list survives this instance's destruction otherwise → wake walks
            // would dereference a destroyed dt.
            _sleptChunks.Remove(this);
            // release pooled visual sub-chunks.
            if (_vcPool != null)
            {
                for (int i = 0; i < _vcPool.Count; i++)
                {
                    var pc = _vcPool[i];
                    if (pc.vsr != null && pc.vsr.sprite != null) Destroy(pc.vsr.sprite);
                    if (pc.tex != null) Destroy(pc.tex);
                    if (pc.vgo != null) Destroy(pc.vgo);
                }
                _vcPool.Clear();
            }
        }

        // Set true when Awake bailed early because gameObject was inactive in
        // hierarchy. Triggers deferred initialization in OnEnable when activated.
        private bool _awakeDeferred;

        // Called by the editor when the component is added (or via context-menu
        // Reset). Configures the auto-added Rigidbody2D so the terrain body is
        // correct in the inspector immediately, without waiting for runtime Init:
        // kinematic (terrain doesn't fall), rotation frozen, never sleeping (so
        // chunk-merge collision callbacks keep firing).
        void Reset()
        {
            var body = GetComponent<Rigidbody2D>();
            if (body == null) return;
            body.bodyType = RigidbodyType2D.Kinematic;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            body.sleepMode = RigidbodySleepMode2D.NeverSleep;
        }

        void OnEnable()
        {
            if (_awakeDeferred)
            {
                _awakeDeferred = false;
                // Run the originally-deferred Awake logic now that we're active.
                Awake();
            }
        }

        // Copies a preset onto this terrain, auto-scaling the resolution-dependent fields to
        // this texture's long side vs preset.referenceResolution. Same-world model: per-pixel
        // mass/gravity / area, pixel-count limits x area, chunk/visual sizes + rim x linear.
        // Call before texture-derived allocation (Awake does). Public so consumers can apply
        // a preset from code too.
        public void ApplyPreset(TerrainPreset p)
        {
            if (p == null) return;

            int longSide = Mathf.Max(texW, texH);
            if (longSide <= 0 && spriteRend != null && spriteRend.sprite != null)
                longSide = Mathf.Max(spriteRend.sprite.texture.width, spriteRend.sprite.texture.height);
            float r = (p.referenceResolution > 0 && longSide > 0) ? (float)longSide / p.referenceResolution : 1f;
            float area = r * r;

            // resolution-independent (copied verbatim)
            alphaThreshold = p.alphaThreshold;
            simplifyTolerance = p.simplifyTolerance;
            eraseRadius = p.eraseRadius;
            canDig = p.canDig;
            anchorToSideBorders = p.anchorToSideBorders;
            anchorWholeBody = p.anchorWholeBody;
            aftermathColor = p.aftermathColor;
            particleSpawnChance = p.particleSpawnChance;
            enableDebrisParticles = p.enableDebrisParticles;
            groundAnchorY = p.groundAnchorY;
            minChunkGravity = p.minChunkGravity;
            maxChunkGravity = p.maxChunkGravity;
            enableIslandFalling = p.enableIslandFalling;
            enableImpactWelding = p.enableImpactWelding;
            destroyOffscreenChunks = p.destroyOffscreenChunks;
            minMergeVelocity = p.minMergeVelocity;
            minEmbedVelocity = p.minEmbedVelocity;
            embedMultiplier = p.embedMultiplier;
            mergeDuration = p.mergeDuration;
            maxMergeAngle = p.maxMergeAngle;
            smashVelocity = p.smashVelocity;
            maxRebuildsPerFrame = p.maxRebuildsPerFrame;
            rebuildBudgetMs = p.rebuildBudgetMs;
            visualsBudgetMs = p.visualsBudgetMs;
            mainlandSimplifyTolerance = p.mainlandSimplifyTolerance;
            enableGhostSweep = p.enableGhostSweep;
            sleepIdleSeconds = p.sleepIdleSeconds;
            sleepWakeRadius = p.sleepWakeRadius;
            maxVisualsPerFrame = p.maxVisualsPerFrame;

            // scale x linear
            chunkSize = NearestPow2(p.chunkSize * r);
            visualChunkSize = NearestPow2(p.visualChunkSize * r);
            aftermathThickness = Mathf.Max(1, Mathf.RoundToInt(p.aftermathThickness * r));

            // scale x area
            maxPhysicsPixels = Mathf.Max(1, Mathf.RoundToInt(p.maxPhysicsPixels * area));
            minimumPhysicsPixels = Mathf.Max(1, Mathf.RoundToInt(p.minimumPhysicsPixels * area));
            maxEmbedPixels = Mathf.Max(1, Mathf.RoundToInt(p.maxEmbedPixels * area));
            minContourAreaPxSqr = p.minContourAreaPxSqr * area;

            // scale / area (per-pixel terms, so world mass/gravity stay constant)
            massPerPixel = p.massPerPixel / area;
            gravityPerPixel = p.gravityPerPixel / area;
        }

        private static int NearestPow2(float v)
        {
            if (v < 1f) return 1;
            return Mathf.Max(1, 1 << Mathf.RoundToInt(Mathf.Log(v, 2f)));
        }

        void Awake()
        {
            // Skip all initialization for GameObjects that are inactive in the
            // hierarchy at Awake-time. Unused map prefabs sitting disabled in the
            // scene shouldn't allocate their managed mirrors, solidMaps, etc.
            // If a disabled map is later activated at runtime, OnEnable will run
            // the deferred initialization (see lazyInitOnEnable below).
            if (!gameObject.activeInHierarchy)
            {
                _awakeDeferred = true;
                return;
            }

            // Populate caches once at startup!
            spriteRend = GetComponent<SpriteRenderer>();
            rb = GetComponent<Rigidbody2D>();

            // Only allocate the 2,000 lists ONCE for the entire game!
            if (contourPool.Count == 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    contourPool.Add(new List<Vector2>(256));
                }
            }

            if (spriteRend.sprite == null) return;

            tex = spriteRend.sprite.texture;
            ppu = spriteRend.sprite.pixelsPerUnit;
            texW = tex.width; texH = tex.height;

            // Apply + auto-scale the preset before any texture-derived sizing/allocation.
            if (preset != null) ApplyPreset(preset);

            Vector2 pivotNorm = spriteRend.sprite.pivot / spriteRend.sprite.rect.size;
            Vector2 spriteWorldSize = new Vector2(texW, texH) / ppu;
            spriteMin = -Vector2.Scale(spriteWorldSize, pivotNorm);

            chunksX = Mathf.CeilToInt((float)texW / chunkSize);
            chunksY = Mathf.CeilToInt((float)texH / chunkSize);

            chunkObjects = new GameObject[chunksX * chunksY];

            // Cap the memory arrays to the max physics limit instead of the total map size!
            int totalPixels = texW * texH;
            int maxFloodSize = maxPhysicsPixels;

            if (!floodStack.IsCreated || floodStack.Length < maxFloodSize)
            {
                if (floodStack.IsCreated) floodStack.Dispose();
                floodStack = new NativeArray<int>(maxFloodSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // pixelVisitId still needs to cover the whole map to track what has been checked
            if (!pixelVisitId.IsCreated || pixelVisitId.Length < totalPixels)
            {
                if (pixelVisitId.IsCreated) pixelVisitId.Dispose();
                pixelVisitId = new NativeArray<int>(totalPixels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            GetComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;

            config = new TerrainConfig
            {
                chunkSize = this.chunkSize,
                alphaThreshold = this.alphaThreshold,
                enableIslandFalling = this.enableIslandFalling,
                enableImpactWelding = this.enableImpactWelding,
                destroyOffscreenChunks = this.destroyOffscreenChunks,
                simplifyTolerance = this.simplifyTolerance,
                eraseRadius = this.eraseRadius,
                canDig = this.canDig,
                maxPhysicsPixels = this.maxPhysicsPixels,
                minimumPhysicsPixels = this.minimumPhysicsPixels,
                anchorToSideBorders = this.anchorToSideBorders,
                anchorWholeBody = this.anchorWholeBody,
                massPerPixel = this.massPerPixel,
                gravityPerPixel = this.gravityPerPixel,
                minChunkGravity = this.minChunkGravity,
                maxChunkGravity = this.maxChunkGravity,
                minMergeVelocity = this.minMergeVelocity,
                minEmbedVelocity = this.minEmbedVelocity,
                embedMultiplier = this.embedMultiplier,
                maxEmbedPixels = this.maxEmbedPixels,
                mergeDuration = this.mergeDuration,
                maxMergeAngle = this.maxMergeAngle,
                smashVelocity = this.smashVelocity,
                aftermathColor = this.aftermathColor,
                aftermathThickness = this.aftermathThickness,
            };

            // Pool size: user's typical case is 1-5 falling chunks active. 50 is
            // plenty of headroom; pool grows on demand if needed (see ExtractAndSpawnIsland's
            // overflow branch). Was 300 — that pre-allocated 1.5 GB of solidMaps and
            // added ~10s to play mode startup.
            for (int i = 0; i < 50; i++)
            {
                GameObject poolObj = new GameObject("PooledChunk");
                poolObj.transform.SetParent(transform); // nest under the mainland for a clean hierarchy
                poolObj.layer = gameObject.layer;

                poolObj.AddComponent<SpriteRenderer>();
                poolObj.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
                DestructibleTerrain dt = poolObj.AddComponent<DestructibleTerrain>();

                // Pre-add a disabled BoxCollider2D so the small-debris path in
                // ExtractAndSpawnIsland never has to call AddComponent at runtime.
                poolObj.AddComponent<BoxCollider2D>().enabled = false;

                // solidMap is now allocated lazily on first use in InitializeAsFallingChunkSingle
                // / SetupAsBoxDebris. Native (non-GC) allocation is cheap enough to defer.
                // Eager 5M-byte alloc per pool object across the whole pool was a heavy startup cost.

                poolObj.SetActive(false);
                islandPool.Enqueue(poolObj);
            }

            InitCaseLookup();
            InitMarchingSquaresBuffers();
            CreateChunks();
            if (!CloneTexture()) return;
            Init();

            // MUST create visual chunks FIRST so 'IsMainland' evaluates to true!
            CreateVisualChunks();
            RebuildAllChunks();
        }

        private void ApplyConfig(TerrainConfig newConfig)
        {
            this.config = newConfig;

            // Unpack the variables into the hot-path fields
            this.chunkSize = newConfig.chunkSize;
            this.alphaThreshold = newConfig.alphaThreshold;
            this.simplifyTolerance = newConfig.simplifyTolerance;
            this.eraseRadius = newConfig.eraseRadius;
            this.canDig = newConfig.canDig;
            this.maxPhysicsPixels = newConfig.maxPhysicsPixels;
            this.anchorToSideBorders = newConfig.anchorToSideBorders;
            this.anchorWholeBody = newConfig.anchorWholeBody;
            this.aftermathColor = newConfig.aftermathColor;
            this.aftermathThickness = newConfig.aftermathThickness;
            this.massPerPixel = newConfig.massPerPixel;
            this.gravityPerPixel = newConfig.gravityPerPixel;
            this.minChunkGravity = newConfig.minChunkGravity;
            this.maxChunkGravity = newConfig.maxChunkGravity;
            this.enableIslandFalling = newConfig.enableIslandFalling;
            this.enableImpactWelding = newConfig.enableImpactWelding;
            this.destroyOffscreenChunks = newConfig.destroyOffscreenChunks;
            this.minimumPhysicsPixels = newConfig.minimumPhysicsPixels;
            this.minMergeVelocity = newConfig.minMergeVelocity;
            this.minEmbedVelocity = newConfig.minEmbedVelocity;
            this.embedMultiplier = newConfig.embedMultiplier;
            this.maxEmbedPixels = newConfig.maxEmbedPixels;
            this.mergeDuration = newConfig.mergeDuration;
            this.maxMergeAngle = newConfig.maxMergeAngle;
            this.smashVelocity = newConfig.smashVelocity;
        }

        /// <summary>
        /// Single-chunk + downsampled physics for falling debris.
        /// Single rebuild instead of per-grid-cell, so RebuildChunk runs once (not 256 times).
        /// </summary>
        // drains the pending Init request set by ExtractAndSpawnIsland
        // on the SPAWNER's previous LateUpdate. Runs at the top of THIS instance's
        // LateUpdate. After this call, this instance is fully initialized
        // (solidMap populated, chunkObjects allocated, chunks marked dirty for
        // throttled rebuild) — equivalent to what a synchronous ExtractAndSpawn-
        // Island would have produced in the spawn frame.
        // Snapshot is disposed here. If something destroyed the dt before this
        // ran, OnDestroy disposes instead.
        void ProcessPendingInit()
        {
            if (!_hasPendingInit) return;
            _hasPendingInit = false;

            if (_pendingInitIsBoxDebris)
            {
                SetupAsBoxDebris(_pendingInitSnapshot, _pendingInitPixelCount, _pendingInitWidth, _pendingInitHeight);
            }
            else
            {
                InitializeAsFallingChunkSingle(_pendingInitSnapshot, _pendingInitPixelCount, _pendingInitWidth, _pendingInitHeight);
            }

            // Init sets rb.isKinematic = true as a guard during chunk-grid setup;
            // re-flip to false here so the spawned chunk actually falls.
            if (rb != null) rb.bodyType = RigidbodyType2D.Dynamic;

            if (_pendingInitSnapshot.IsCreated)
            {
                _pendingInitSnapshot.Dispose();
            }
        }

        private void InitializeAsFallingChunkSingle(NativeArray<int> activeLocalPixels, int pixelCount, int spriteW, int spriteH)
        {
            if (spriteRend == null) spriteRend = GetComponent<SpriteRenderer>();
            if (rb == null) rb = GetComponent<Rigidbody2D>();

            // reset rest detection + merge target cache from the
            // previous tenant of this pool slot. _restTimer is per-instance and
            // could carry over a stale value otherwise. _cachedMergeTarget should
            // re-resolve in case the player spawned a different mainland between
            // pool reuses (defensive — typically there's one mainland and the
            // cache stays valid).
            _restTimer = 0f;
            _lastRestPos = transform.position;
            _cachedMergeTarget = null;

            tex = spriteRend.sprite.texture;
            ppu = spriteRend.sprite.pixelsPerUnit;
            rawPixels = tex.GetRawTextureData<Color32>();

            texWidth = spriteW;
            texHeight = spriteH;
            texW = spriteW;
            texH = spriteH;

            Vector2 pivotNorm = spriteRend.sprite.pivot / spriteRend.sprite.rect.size;
            Vector2 spriteWorldSize = new Vector2(texWidth, texHeight) / ppu;
            spriteMin = -Vector2.Scale(spriteWorldSize, pivotNorm);

            int totalPixels = texWidth * texHeight;

            if (!solidMap.IsCreated || solidMap.Length < totalPixels)
            {
                if (solidMap.IsCreated) solidMap.Dispose();
                solidMap = new NativeArray<byte>(totalPixels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            new ClearByteArrayJob { arr = solidMap, length = totalPixels }.Run();

            currentSolidPixels = pixelCount;
            new PopulateFallingSolidMapJob
            {
                activeLocalPixels = activeLocalPixels,
                solidMap = solidMap,
                pixelCount = pixelCount,
            }.Run();

            // MULTI-CHUNK FALLING CHUNKS
            // Was a single logical chunk covering the whole texture; every dig
            // rebuilt that one giant contour (expensive pc.SetPath). Now
            // subdivide into FALLING_CHUNK_PIXEL_DIM-sized chunks (default 512px),
            // each with effective grid = FALLING_CHUNK_TARGET_CELLS (128), via
            // physicsStep = chunkPixelDim / TARGET_CELLS = 4. Per-dig only the
            // 1-4 chunks the brush touches get rebuilt (mainland-equivalent).
            int chunkPixelDim = FALLING_CHUNK_PIXEL_DIM;
            int physicsStep = Mathf.Max(1, chunkPixelDim / FALLING_CHUNK_TARGET_CELLS);
            int physicsCells = chunkPixelDim / physicsStep;

            chunkSize = chunkPixelDim;
            chunksX = Mathf.Max(1, Mathf.CeilToInt((float)spriteW / chunkPixelDim));
            chunksY = Mathf.Max(1, Mathf.CeilToInt((float)spriteH / chunkPixelDim));
            physicsStepForRebuild = physicsStep;

            rb.bodyType = RigidbodyType2D.Kinematic;
            // Auto-merge depends on OnCollisionStay2D firing every fixed step. Sleeping
            // rigidbodies stop receiving collision callbacks, which prevents settled
            // chunks from ever reaching REST_DURATION_TO_MERGE.
            rb.sleepMode = RigidbodySleepMode2D.NeverSleep;

            InitCaseLookup();
            InitMarchingSquaresBuffers(physicsCells);
            CreateChunks();

            // VISUAL SUB-CHUNKS FOR LARGE FALLING CHUNKS
            // When the chunk is big enough that a full tex.Apply costs >a few ms,
            // subdivide it into visualChunkSize-sized sub-textures (same scheme as
            // mainland). Per-dig only the dirty sub-chunk uploads (~256KB) instead
            // of the full chunk (~64MB for 4096^2).
            // Use deferred upload — skip the per-sub-chunk bulk pixel
            // copy + tex.Apply at creation (expensive per spawn).
            // Each sub-chunk is marked fully dirty; ApplyVisuals catches up over
            // multiple frames. The parent SpriteRenderer stays visible during
            // catchup so the chunk doesn't disappear, then is disabled by
            // ApplyVisuals once dirtyVisualsList drains.
            // Falling chunks ALWAYS use the parent
            // SpriteRenderer's single Texture2D — never visual sub-chunks. The
            // previous size-gate (>512px to sub-chunks) is removed because
            // CreateVisualChunks was costly per big-island spawn and dwarfed the
            // per-frame Apply savings sub-chunks were meant to provide. A single
            // full tex.Apply on a 1024-px texture is much cheaper than the
            // sub-chunk creation overhead.
            // Mainland still uses sub-chunks (CreateVisualChunks called once at
            // scene Init). Falling chunks rely on the pooled fallingTex and
            // ApplyVisuals's visualChunks==null branch (`tex.Apply(false)`).
            if (!spriteRend.enabled) spriteRend.enabled = true;
            if (visualChunks != null) DestroyFallingVisualChunks();
            _deferredParentDisable = false;

            // defer initial rebuild via the throttled mechanism. Was a
            // single chunkSignatures[0]=-1 + RebuildChunk(0,0) call. With multi-
            // chunk grids (up to 8x8=64 chunks for 4096²), a synchronous rebuild
            // would be expensive at spawn. Instead, mark all chunks dirty and
            // let LateUpdate's throttled rebuild process them at maxRebuildsPerFrame
            // over several frames. Physics activation (rb.simulated=true) waits
            // for the new chunk's own chunks to finish — see the pendingPhysics-
            // Activations check in LateUpdate.
            int totalLogicalChunks = chunksX * chunksY;
            for (int i = 0; i < totalLogicalChunks; i++)
            {
                chunkSignatures[i] = -1;  // sentinel: skip hash, rebuild on next pass
                FlagChunkDirty(i);        // adds to dirtyChunksList for throttled rebuild
            }
        }

        /// <summary>
        /// Lightweight init for micro-debris (≤100 pixels). 
        /// </summary>
        private void SetupAsBoxDebris(NativeArray<int> activeLocalPixels, int pixelCount, int spriteW, int spriteH)
        {
            if (spriteRend == null) spriteRend = GetComponent<SpriteRenderer>();
            if (rb == null) rb = GetComponent<Rigidbody2D>();

            // reset rest detection + merge target cache from the
            // previous tenant of this pool slot. _restTimer is per-instance and
            // could carry over a stale value otherwise. _cachedMergeTarget should
            // re-resolve in case the player spawned a different mainland between
            // pool reuses (defensive — typically there's one mainland and the
            // cache stays valid).
            _restTimer = 0f;
            _lastRestPos = transform.position;
            _cachedMergeTarget = null;

            tex = spriteRend.sprite.texture;
            ppu = spriteRend.sprite.pixelsPerUnit;
            rawPixels = tex.GetRawTextureData<Color32>();

            texWidth = spriteW;
            texHeight = spriteH;
            texW = spriteW;
            texH = spriteH;

            Vector2 pivotNorm = spriteRend.sprite.pivot / spriteRend.sprite.rect.size;
            Vector2 spriteWorldSize = new Vector2(texWidth, texHeight) / ppu;
            spriteMin = -Vector2.Scale(spriteWorldSize, pivotNorm);

            int totalPixels = texWidth * texHeight;
            if (!solidMap.IsCreated || solidMap.Length < totalPixels)
            {
                if (solidMap.IsCreated) solidMap.Dispose();
                solidMap = new NativeArray<byte>(totalPixels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            new ClearByteArrayJob { arr = solidMap, length = totalPixels }.Run();

            currentSolidPixels = pixelCount;
            new PopulateFallingSolidMapJob
            {
                activeLocalPixels = activeLocalPixels,
                solidMap = solidMap,
                pixelCount = pixelCount,
            }.Run();

            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.sleepMode = RigidbodySleepMode2D.NeverSleep;

            chunksX = 1;
            chunksY = 1;
            chunkSize = Mathf.Max(spriteW, spriteH);
            physicsStepForRebuild = 1;

            // Explicitly revoke digging rights! Micro-debris does not have the 
            // procedural arrays required to process mid-air destruction.
            canDig = false;
        }

        void LateUpdate()
        {
            // Drain Init queued by ExtractAndSpawnIsland last frame. Splits the
            // spawn pipeline so heavy CreateChunks work doesn't combine with
            // extraction in the same frame. Runs first so the new chunk has valid
            // solidMap / chunkObjects state before anything else touches it.
            ProcessPendingInit();

            // Self-activation safety net. A spawned chunk normally gets rb.simulated=true
            // from its SPAWNER's pendingPhysicsActivations loop. If that spawner was pooled
            // before activating us (e.g. a falling chunk that spawned this sub-chunk then
            // merged away), we'd be stranded out of the physics world — unable to fall,
            // collide, or be dug. The spawner's own safety fires within ~30 frames, so this
            // longer grace only ever rescues genuine orphans.
            if (!IsMainland && rb != null && !rb.simulated && !_hasPendingInit)
            {
                if (++_activationGrace > 90) rb.simulated = true;
            }
            else
            {
                _activationGrace = 0;
            }

            reusableNewIslands.Clear();

            // Complete last frame's async BFS and process any detected floating
            // island. Done before the early-out so a quiet frame can still spawn a
            // chunk detected last frame.
            if (_hasPendingDetection)
            {
                // Non-blocking: poll IsCompleted instead of calling Complete()
                // synchronously, which could stall the main thread while a falling
                // chunk's BFS scans millions of pixels. If still running, defer a
                // frame (detection latency +1-3 frames, imperceptible). Wait on the
                // GLOBAL handle, not _myBfsHandle: the job chain and islandPixelBuffer
                // are shared across instances, so we must avoid racing in-flight jobs.
                if (!_globalBfsHandle.IsCompleted)
                {
                    // Worker thread still busy. Skip processing this frame; recheck
                    // next LateUpdate. _hasPendingDetection stays true.
                }
                else
                {
                    _globalBfsHandle.Complete();
                    _hasPendingDetection = false;
                    _globalBfsInFlight = false;
                    currentSearchId = _myBfsResult[0];
                    if (_myBfsResult[1] == 1)
                    {
                        int detectedCount = _myBfsResult[2];
                        int minX = _myBfsResult[3];
                        int maxX = _myBfsResult[4];
                        int minY = _myBfsResult[5];
                        int maxY = _myBfsResult[6];
                        // Defensive: validate BFS output before consuming. Corruption
                        // here causes IndexOutOfRange in ExtractAndSpawnIsland, which
                        // can compound into native crashes from in-flight Burst jobs.
                        int outBufLen = _bfsOutputBuffer.IsCreated ? _bfsOutputBuffer.Length : 0;
                        int ipBufLen = islandPixelBuffer.IsCreated ? islandPixelBuffer.Length : 0;
                        bool valid =
                            detectedCount > 0 &&
                            detectedCount <= outBufLen &&
                            detectedCount <= ipBufLen &&
                            minX >= 0 && maxX < texWidth && minX <= maxX &&
                            minY >= 0 && maxY < texHeight && minY <= maxY;
                        if (!valid)
                        {
                            Debug.LogWarning($"[Phase5] Skipping invalid detection result: " +
                                $"count={detectedCount} bufOut={outBufLen} bufIsl={ipBufLen} " +
                                $"bbox=[{minX},{maxX}]x[{minY},{maxY}] tex={texWidth}x{texHeight} " +
                                $"isMainland={IsMainland} solidPx={currentSolidPixels}");
                            pendingScans.Clear();
                        }
                        else
                        {
                            islandMinX = minX; islandMaxX = maxX;
                            islandMinY = minY; islandMaxY = maxY;
                            NativeArray<int>.Copy(_bfsOutputBuffer, 0, islandPixelBuffer, 0, detectedCount);
                            islandPixelCount = detectedCount;
                            // Debounced: detection runs only after the cut has PAUSED for
                            // DETECT_DEBOUNCE_FRAMES, so this floating region is final — extract it.
                            // pendingScans are retained so the next frame re-scans and drains the
                            // next detached piece; cleared once a batch finds nothing floating.
                            try
                            {
                                GameObject newIsland = ExtractAndSpawnIsland();
                                if (newIsland != null) reusableNewIslands.Add(newIsland);
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"[Phase5] ExtractAndSpawnIsland threw: {ex.GetType().Name}: {ex.Message}\n" +
                                    $"  count={detectedCount} bbox=[{minX},{maxX}]x[{minY},{maxY}] " +
                                    $"isMainland={IsMainland} solidPx={currentSolidPixels}\n{ex.StackTrace}");
                            }
                        }
                    }
                    else
                    {
                        pendingScans.Clear(); // no floating island left — drain complete
                    }
                } // end of IsCompleted else
            }

            // We must update the engine if there is damage OR if a merge dirtied the arrays!
            // also continue if a detection just spawned an island this frame.
            if (pendingDamage.Count == 0 && dirtyChunksList.Count == 0 && dirtyVisualsList.Count == 0 && reusableNewIslands.Count == 0 && pendingScans.Count == 0) return;

            if (currentSearchId > int.MaxValue - 1000000)
            {
                currentSearchId = 0;
                new ClearIntArrayJob { arr = pixelVisitId, length = pixelVisitId.Length }.Run();
            }
            currentSearchId++;
            startSearchId = currentSearchId;

            // We MUST regenerate this every frame so the engine is forced to re-verify 
            // that chunks are actually still connected to the ground!
            mainlandId = startSearchId + 500000;

            // pendingScans is NOT cleared here. If a BFS is still in flight we must
            // retain queued scans (e.g. the dig that just severed a chunk) so the
            // next BFS still processes them — otherwise stopping the dig stroke would
            // strand a disconnected piece undetected. Cleared once consumed below.

            for (int i = 0; i < pendingDamage.Count; i++)
            {
                ProcessEraseCircle(pendingDamage[i].pos, pendingDamage[i].radius);
            }
            pendingDamage.Clear();

            // LOCALIZED BRESENHAM SCANNING
            if (enableIslandFalling)
            {
                // ASYNC BATCHED BFS (mainland AND falling chunks)
                // All instances use the async path. Falling chunks running sync
                // would force _globalBfsHandle.Complete() on the main thread,
                // negating the async benefit and causing massive lag whenever the
                // player digs into a carved-off chunk while mainland's BFS is in
                // flight. Async + JobHandle chaining serializes them safely.
                // Detection latency: 1 frame. Imperceptible.
                // _bfsOutputBuffer size: maxPhysicsPixels for mainland (the BFS
                // budget cap), currentSolidPixels for falling chunks (their own
                // size — smaller, saves memory).
                if (pendingScans.Count > 0 && Time.frameCount - _lastDigFrame >= DETECT_DEBOUNCE_FRAMES && !_hasPendingDetection && !_globalBfsInFlight)
                {
                    // gate scheduling on _hasPendingDetection. Only one BFS
                    // in flight at a time. Now: BFS reschedules immediately when the previous
                    // one finishes (1-3 frames later for huge falling chunks). New
                    // digs during in-flight BFS are dropped from THIS scan, but the
                    // NEXT BFS naturally picks up any disconnected pieces because
                    // its perimeter walk + flood fill detects all current fragments,
                    // not just ones from the most recent dig.
                    if (!_pendingScansNative.IsCreated)
                    {
                        _pendingScansNative = new NativeList<PendingScanRequest>(64, Allocator.Persistent);
                    }
                    if (!_myBfsResult.IsCreated)
                    {
                        _myBfsResult = new NativeArray<int>(7, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    }
                    int bufferSize = IsMainland ? maxPhysicsPixels : Mathf.Max(currentSolidPixels + 1024, 1024);
                    if (!_bfsOutputBuffer.IsCreated || _bfsOutputBuffer.Length < bufferSize)
                    {
                        if (_bfsOutputBuffer.IsCreated) _bfsOutputBuffer.Dispose();
                        _bfsOutputBuffer = new NativeArray<int>(bufferSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    }

                    _pendingScansNative.Clear();
                    for (int i = 0; i < pendingScans.Count; i++)
                    {
                        _pendingScansNative.Add(new PendingScanRequest
                        {
                            pixCX = pendingScans[i].pixCX,
                            pixCY = pendingScans[i].pixCY,
                            radiusPx = pendingScans[i].radiusPx,
                        });
                    }
                    // pendingScans retained — drained in the completion handler once a
                    // batch finds nothing floating (lets multiple pieces fall over frames).

                    // Freeze solidMap so the async flood traverses a stable graph. Safe to
                    // overwrite here: dispatch is gated on !_globalBfsInFlight, so the prior
                    // flood has finished reading it. The live solidMap keeps being dug.
                    if (!_bfsSolidSnapshot.IsCreated || _bfsSolidSnapshot.Length < solidMap.Length)
                    {
                        if (_bfsSolidSnapshot.IsCreated) _bfsSolidSnapshot.Dispose();
                        _bfsSolidSnapshot = new NativeArray<byte>(solidMap.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    }
                    NativeArray<byte>.Copy(solidMap, _bfsSolidSnapshot, solidMap.Length);

                    var job = new BatchedScanAndBfsJob
                    {
                        solidMap = _bfsSolidSnapshot,
                        pixelVisitId = pixelVisitId,
                        floodStack = floodStack,
                        islandPixelBuffer = islandPixelBuffer,
                        pendingScans = _pendingScansNative.AsArray(),
                        pendingScanCount = _pendingScansNative.Length,
                        outputBuffer = _bfsOutputBuffer,
                        result = _myBfsResult,
                        texWidth = texWidth,
                        texHeight = texHeight,
                        isMainland = IsMainland,
                        groundAnchorY = groundAnchorY,
                        anchorToSideBorders = anchorToSideBorders,
                anchorWholeBody = anchorWholeBody,
                        maxPhysicsPixels = maxPhysicsPixels,
                        mainlandId = mainlandId,
                        startSearchId = startSearchId,
                        currentSearchIdIn = currentSearchId,
                        currentSolidPixels = currentSolidPixels,
                    };
                    _myBfsHandle = job.Schedule(_globalBfsHandle);
                    _globalBfsHandle = _myBfsHandle;
                    _hasPendingDetection = true;
                    _globalBfsInFlight = true;
                }
            }
            else
            {
                pendingScans.Clear(); // feature off: don't accumulate
            }

            ApplyVisuals();

            // THROTTLED REBUILD
            // Cap rebuilds per frame and defer the rest to next LateUpdate.
            // Spawn-side rebuilds in InitializeAsFallingChunkSingle are NOT throttled
            // (they call RebuildChunk directly, not via dirtyChunksList).
            // time-budget alongside count-budget. Per-rebuild cost varies
            // 10-30× depending on how many polygon paths actually changed (hash-skip
            // eliminates unchanged ones, but paths near the brush genuinely
            // change every frame). When several chunks need expensive multi-path
            // SetPath uploads in the same frame, the count-based cap (16) lets through
            // too much work in one frame. The time budget clips that to
            // ~rebuildBudgetMs without
            // affecting the common steady-state case (3-4 cheap rebuilds/frame).
            // chunks are processed in PARALLEL batches of REBUILD_BATCH_SIZE
            // via RebuildChunkPipelineJob. The Burst-compiled marching squares + RDP
            // pipeline runs concurrently for up to 8 chunks per Schedule, then a
            // sequential main-thread post-pass applies the resulting contours to
            // PolygonCollider2D (Box2D fixture API isn't thread-safe). For
            // maxRebuildsPerFrame=16 with chunkSize=256, expect 2 batch dispatches
            // per frame, with a wall-clock speedup on the marching-squares portion
            // and elimination of per-rebuild dispatch overhead.
            int budget = maxRebuildsPerFrame;
            long timeBudgetTicks = rebuildBudgetMs > 0f
                ? (long)(rebuildBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0)
                : long.MaxValue;
            long rebuildStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            // Pipeline parameters are constant across this frame's rebuilds (same
            // DT instance, same physicsStep). Compute once outside the batch loop.
            int rebuildStep = physicsStepForRebuild;
            int rebuildScaledChunkSize = chunkSize / rebuildStep;
            int rebuildPadStride = rebuildScaledChunkSize + 2;
            float rebuildInvPpu = 1f / ppu;
            float rebuildPixToWorld = rebuildStep * rebuildInvPpu;
            float rebuildTraceWorldScale = 0.5f * rebuildPixToWorld;
            float rebuildTraceWorldOffset = -rebuildPixToWorld;
            float rebuildBaseTol = IsMainland ? mainlandSimplifyTolerance : simplifyTolerance;
            float rebuildWorldTol = rebuildBaseTol * rebuildPixToWorld;

            while (budget > 0 && dirtyChunksList.Count > 0)
            {
                // FILL THE BATCH
                // Drain up to REBUILD_BATCH_SIZE truly-dirty entries off the list.
                // Skip stale entries (already rebuilt synchronously elsewhere) and
                // continue scanning so a single stale doesn't waste a slot.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_Fill.Begin();
#endif
                int batchCount = 0;
                while (batchCount < REBUILD_BATCH_SIZE && budget > 0 && dirtyChunksList.Count > 0)
                {
                    int last = dirtyChunksList.Count - 1;
                    int idx = dirtyChunksList[last];
                    dirtyChunksList.RemoveAt(last);
                    if (!isChunkDirty[idx]) continue;
                    chunkIndicesB[batchCount] = idx;
                    prevSignaturesB[batchCount] = chunkSignatures[idx];
                    batchCount++;
                    budget--;
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_Fill.End();
#endif
                if (batchCount == 0) break;

                // SCHEDULE PARALLEL JOB
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_Schedule.Begin();
#endif
                var pipelineJob = new RebuildChunkPipelineJob
                {
                    chunkIndices = chunkIndicesB,
                    prevSignatures = prevSignaturesB,
                    solidMap = solidMap,
                    caseLineCount = caseLineCount,
                    caseLineData = caseLineData,
                    padBuffers = padBuffersB,
                    edgeBuffers = edgeBuffersB,
                    nextNodeBuffers = nextNodeBuffersB,
                    vertexVisitedBuffers = vertexVisitedBuffersB,
                    usedNodesBuffers = usedNodesBuffersB,
                    contourVertsBuffers = contourVertsBuffersB,
                    contourSpansBuffers = contourSpansBuffersB,
                    simplVertsBuffers = simplVertsBuffersB,
                    simplSpansBuffers = simplSpansBuffersB,
                    rdpStackBuffers = rdpStackBuffersB,
                    rdpScratchBuffers = rdpScratchBuffersB,
                    rdpKeepFlagsBuffers = rdpKeepFlagsBuffersB,
                    edgeCounts = edgeCountsB,
                    usedNodeCounts = usedNodeCountsB,
                    contourSpanCounts = contourSpanCountsB,
                    simplSpanCounts = simplSpanCountsB,
                    slotPrevUsedCount = slotPrevUsedCountB,
                    outNewSignatures = outNewSignaturesB,
                    outSignatureChanged = outSignatureChangedB,
                    outOverflowed = outOverflowedB,
                    padCap = _slotPadLen,
                    edgeCap = REBUILD_EDGE_CAP,
                    maxNodes = _slotMaxNodes,
                    usedCap = _slotUsedCap,
                    vertCap = REBUILD_VERT_CAP,
                    spanCap = REBUILD_SPAN_CAP,
                    rdpStackCap = REBUILD_RDP_STACK_CAP,
                    rdpScratchCap = REBUILD_RDP_SCRATCH_CAP,
                    rdpKeepCap = REBUILD_RDP_KEEP_CAP,
                    chunkSize = chunkSize,
                    physicsStep = rebuildStep,
                    chunksX = chunksX,
                    texWidth = texWidth,
                    texHeight = texHeight,
                    packStride = packStride,
                    scaledChunkSize = rebuildScaledChunkSize,
                    padStride = rebuildPadStride,
                    traceWorldScale = rebuildTraceWorldScale,
                    traceWorldOffset = rebuildTraceWorldOffset,
                    worldTol = rebuildWorldTol,
                };
                // innerloopBatchCount=1: each Execute(slot) is heavy (full pipeline
                // for one chunk), so workers should claim one at a time to keep
                // load-balanced across cores.
                var pipelineHandle = pipelineJob.Schedule(batchCount, 1);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_Schedule.End();
                s_pmRebuild_Complete.Begin();
#endif
                pipelineHandle.Complete();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_Complete.End();
#endif

                // SEQUENTIAL POST-PASS
                // For each slot: handle overflow/skip/apply paths. Box2D pc.SetPath
                // is main-thread only, so this stays serial.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_PostPass.Begin();
#endif
                for (int slot = 0; slot < batchCount; slot++)
                {
                    int idx = chunkIndicesB[slot];

                    if (outOverflowedB[slot] != 0)
                    {
                        // Per-slot capacity exhausted. Fall back to sequential
                        // RebuildChunk which uses the larger static buffers.
                        int cx = idx % chunksX, cy = idx / chunksX;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        s_pmRebuild_Fallback.Begin();
#endif
                        RebuildChunk(cx, cy);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        s_pmRebuild_Fallback.End();
#endif
                        isChunkDirty[idx] = false;
                        continue;
                    }

                    // Persist the new signature regardless of whether contents changed.
                    chunkSignatures[idx] = outNewSignaturesB[slot];

                    if (outSignatureChangedB[slot] == 0)
                    {
                        // Signature matched — chunk hasn't changed since last rebuild.
                        isChunkDirty[idx] = false;
                        continue;
                    }

                    // Apply simplified contours to the PolygonCollider2D for this chunk.
                    ApplyParallelRebuildResult(slot, idx);
                    isChunkDirty[idx] = false;
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmRebuild_PostPass.End();
#endif

                // Bail if we've burned the time budget. The remaining dirty chunks
                // stay flagged in dirtyChunksList and isChunkDirty[]; LateUpdate
                // will pick them up next frame.
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStartTicks) >= timeBudgetTicks)
                    break;
            }

            // PROCESS PENDING PHYSICS ACTIVATIONS
            // For each falling chunk spawned this/recent frame, check whether all its
            // spawn-area chunks have been rebuilt by the throttled loop. When clean,
            // re-enable Box2D simulation so the chunk starts falling.
            for (int i = pendingPhysicsActivations.Count - 1; i >= 0; i--)
            {
                var pp = pendingPhysicsActivations[i];

                // Defensive: if the chunk got destroyed/pooled before activation, drop it.
                if (pp.dt == null || pp.dt.rb == null || !pp.dt.gameObject.activeInHierarchy)
                {
                    pendingPhysicsActivations.RemoveAt(i);
                    continue;
                }

                bool allClean = true;
                for (int rcy = pp.rcy0; rcy <= pp.rcy1 && allClean; rcy++)
                {
                    for (int rcx = pp.rcx0; rcx <= pp.rcx1; rcx++)
                    {
                        if (isChunkDirty[rcx + rcy * chunksX]) { allClean = false; break; }
                    }
                }
                // also wait for the new falling chunk's OWN chunks to
                // finish their initial rebuild. With multi-chunk falling chunks
                // (chunkSize=512), the throttled rebuild on the new chunk's
                // instance processes its 1-64 logical chunks over several frames.
                // Activating physics before they're rebuilt would mean the chunk
                // has no collider and falls through mainland.
                // with deferred Init, isChunkDirty may be null in the
                // spawn frame (Init runs in the chunk's next LateUpdate via
                // ProcessPendingInit). Gate on isChunkDirty != null too — null
                // means Init hasn't run yet, must wait.
                if (allClean)
                {
                    if (pp.dt.isChunkDirty == null)
                    {
                        allClean = false;
                    }
                    else
                    {
                        bool[] dirty = pp.dt.isChunkDirty;
                        for (int j = 0; j < dirty.Length; j++)
                        {
                            if (dirty[j]) { allClean = false; break; }
                        }
                    }
                }

                pp.safetyFrames--;
                if (allClean || pp.safetyFrames <= 0)
                {
                    pp.dt.rb.simulated = true;
                    pendingPhysicsActivations.RemoveAt(i);
                }
                else
                {
                    pendingPhysicsActivations[i] = pp;
                }
            }

            if (enableIslandFalling)
            {
                foreach (GameObject island in reusableNewIslands)
                {
                    Rigidbody2D rb = island.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.bodyType = RigidbodyType2D.Dynamic;
                }
            }
        }

        #region Physics & Merging
        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!enableImpactWelding || isMerging) return;

            if (this.rb != null && this.rb.bodyType != RigidbodyType2D.Kinematic)
            {
                // collision.gameObject returns the root Rigidbody (which has no ChunkRef).
                // We MUST use collision.collider.gameObject to target the specific child chunk we hit!
                if (!collision.collider.gameObject.TryGetComponent(out ChunkRef cr)) return;
                DestructibleTerrain targetTerrain = cr.owner;

                // Only allow merging if the target is the MAINLAND. We use
                // IsMainland (backed by an explicit flag) instead of
                // `visualChunks != null` because visual sub-chunks were added
                // for big falling chunks too — that would let two falling chunks
                // try to merge into each other and NRE in the target's mainland-only
                // code paths in StampAndDestroy.
                if (targetTerrain != null && targetTerrain != this && targetTerrain.IsMainland)
                {

                    Rigidbody2D targetRb = targetTerrain.rb; // Use our cached variable!
                    if (targetRb == null || targetRb.bodyType == RigidbodyType2D.Kinematic || targetRb.bodyType == RigidbodyType2D.Static)
                    {

                        // SQUARED MAGNITUDES & DOT PRODUCTS
                        // .sqrMagnitude is practically free compared to .magnitude!
                        float impactSqrSpeed = collision.relativeVelocity.sqrMagnitude;
                        if (impactSqrSpeed < minMergeVelocity * minMergeVelocity) return;

                        ContactPoint2D contact = collision.GetContact(0);
                        Vector2 surfaceNormal = contact.normal;

                        // The Dot Product of normal and Vector2.up (0,1) is literally just normal.y!
                        // This bypasses the heavy Vector2.Angle() Acos operation entirely.
                        if (_cachedMaxMergeAngle != maxMergeAngle)
                        {
                            _cachedMaxMergeAngle = maxMergeAngle;
                            _cachedCosMaxAngle = Mathf.Cos(maxMergeAngle * Mathf.Deg2Rad);
                        }

                        if (surfaceNormal.y < _cachedCosMaxAngle && impactSqrSpeed < smashVelocity * smashVelocity) return;

                        isMerging = true;
                        Vector2 rawNormal = -surfaceNormal;
                        Vector2 impactDirection = new Vector2(rawNormal.x * 0.2f, rawNormal.y).normalized;

                        // Only do the heavy Sqrt here, where we absolute need the real speed
                        StartCoroutine(CrunchAndMerge(targetTerrain, Mathf.Sqrt(impactSqrSpeed), impactDirection));
                    }
                }
            }
        }

        void OnCollisionStay2D(Collision2D collision)
        {
            if (!enableImpactWelding || isMerging) return;
            if (this.rb == null || this.rb.bodyType == RigidbodyType2D.Kinematic) return;

            // Rest detection: a chunk is "at rest" only when its velocity is low AND
            // its position has stopped drifting. The position term lets chunks that
            // jitter in place (noisy contact velocity) or creep slowly still settle
            // and merge — the same stillness idea as the idle-sleep check below.
            Vector3 restPos = transform.position;
            bool moved = (restPos - _lastRestPos).sqrMagnitude > REST_POS_EPSILON * REST_POS_EPSILON;
            _lastRestPos = restPos;
            if (moved ||
                this.rb.linearVelocity.sqrMagnitude > REST_VELOCITY_THRESHOLD * REST_VELOCITY_THRESHOLD ||
                Mathf.Abs(this.rb.angularVelocity) > REST_ANGULAR_THRESHOLD)
            {
                _restTimer = 0f;
                return;
            }

            _restTimer += Time.fixedDeltaTime;
            if (_restTimer < REST_DURATION_TO_MERGE) return;

            // Find a mainland to merge into.
            // Preferred: the contact partner if it IS mainland (lets us snap to a clean embed).
            // Fallback: spatial query — handles "piled on another falling chunk" case.
            DestructibleTerrain mergeTarget = null;
            bool directMainlandContact = false;
            if (collision.collider.gameObject.TryGetComponent(out ChunkRef cr))
            {
                // was `cr.owner.visualChunks != null` — broken since big
                // falling chunks now also have visual sub-chunks. Use IsMainland.
                if (cr.owner != null && cr.owner != this && cr.owner.IsMainland)
                {
                    mergeTarget = cr.owner;
                    directMainlandContact = true;
                }
            }
            if (mergeTarget == null)
            {
                mergeTarget = FindNearbyMainland();
            }
            if (mergeTarget == null)
            {
                // no merge target found. If chunk has been at rest long
                // enough (sleepIdleSeconds), demote it to Static. Static bodies
                // are out of Box2D's dynamic broadphase (no contact resolution
                // between them), so a 50-chunk pile costs ~zero per frame. Critical:
                // we use bodyType=Static (NOT rb.simulated=false) because the
                // latter removes the body from the world entirely, breaking
                // player-on-debris collision and projectile hits. Static keeps
                // the collider live for player + projectile interaction.
                if (sleepIdleSeconds > 0f && _restTimer >= sleepIdleSeconds && rb != null && rb.bodyType == RigidbodyType2D.Dynamic)
                {
                    // Don't freeze while deeply interpenetrating a neighbour: two Static
                    // bodies never depenetrate (see above), so the overlap would lock in
                    // permanently. Stay Dynamic so Box2D keeps pushing us apart, and sleep
                    // next cycle once separation is back within a few pixels.
                    int contactCount = rb.GetContacts(sSleepContacts);
                    for (int i = 0; i < contactCount; i++)
                    {
                        if (sSleepContacts[i].separation < -5f / ppu) return;
                    }

                    rb.bodyType = RigidbodyType2D.Static;
                    if (!_sleptChunks.Contains(this)) _sleptChunks.Add(this);
                }
                return;
            }

            isMerging = true;

            if (directMainlandContact)
            {
                // Snap to a fixed 2-pixel embed using contact info.
                ContactPoint2D contact = collision.GetContact(0);
                Vector2 normal = contact.normal;
                float currentSep = contact.separation;
                float desiredSep = -2f / ppu;
                float adjustment = desiredSep - currentSep;
                transform.position += (Vector3)(normal * adjustment);
            }
            // Indirect merges (piled on another chunk) stamp at current position.
            // Mainland's texture gets a small bump where the pile was.

            StartCoroutine(CrunchAndMerge(mergeTarget, 0f, Vector2.zero));
        }

        // Spatial fallback for chunks not in direct contact with mainland.
        // Uses a NonAlloc overlap query so it's GC-free.
        DestructibleTerrain FindNearbyMainland()
        {
            // cache the resolved mainland. Unity's implicit Object null
            // check handles destroyed targets correctly, so a stale reference
            // gracefully falls back to the OverlapBox query. Without the cache,
            // every OnCollisionStay2D tick on a piled chunk fires a full
            // Physics2D.OverlapBoxNonAlloc, which is expensive when many
            // chunks pile and all simultaneously query. With one mainland per
            // scene (the common case), the cache hits forever after first resolve.
            if (_cachedMergeTarget != null && _cachedMergeTarget.IsMainland)
            {
                return _cachedMergeTarget;
            }
            if (spriteRend == null) return null;
            Bounds b = spriteRend.bounds;
            Vector2 size = b.size * 1.1f;
            int hits = Physics2D.OverlapBox(b.center, size, 0f, ContactFilter2D.noFilter, sOverlapResults);
            for (int i = 0; i < hits; i++)
            {
                var col = sOverlapResults[i];
                if (col == null) continue;
                if (col.TryGetComponent(out ChunkRef cr2))
                {
                    // use IsMainland; visualChunks is no longer mainland-only.
                    if (cr2.owner != null && cr2.owner != this && cr2.owner.IsMainland)
                    {
                        _cachedMergeTarget = cr2.owner;
                        return cr2.owner;
                    }
                }
            }
            return null;
        }

        void OnCollisionExit2D(Collision2D collision)
        {
            // No-op kept for symmetry. Velocity-based reset in OnCollisionStay2D
            // already handles bumped/stirred chunks correctly.
        }

        // camera-frustum culling for falling chunks. Unity fires
        // OnBecameInvisible/Visible based on renderer visibility from any active
        // camera (uses each renderer's bounds + camera frustum). Sleeping
        // off-camera chunks removes them from Box2D's broadphase entirely;
        // off-camera physics is invisible work that costs CPU + battery for no
        // visual benefit.
        // Only falling chunks participate (mainland stays simulated always —
        // it's static anyway). Mainland's IsMainland=true short-circuits both
        // hooks. Box-debris chunks (BoxCollider2D path) also sleep/wake; they
        // share the same SpriteRenderer machinery.
        // Trade-off: a falling chunk that spawns off-camera doesn't fall — it's
        // immediately put to sleep by OnBecameInvisible (which fires shortly
        // after the renderer realizes nothing sees it). When the camera pans
        // to it, OnBecameVisible re-enables physics and gravity resumes.
        void OnBecameInvisible()
        {
            if (IsMainland) return;
            if (isMerging) return;
            if (destroyOffscreenChunks)
            {
                // Free everything (OnDestroy disposes solidMap + native buffers +
                // texture). Won't reappear if the camera returns — that's the trade.
                _sleptChunks.Remove(this);
                Destroy(gameObject);
                return;
            }
            if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic) return;
            rb.bodyType = RigidbodyType2D.Static;
            if (!_sleptChunks.Contains(this)) _sleptChunks.Add(this);
        }

        void OnBecameVisible()
        {
            if (IsMainland) return;
            if (rb == null) return;
            if (rb.bodyType == RigidbodyType2D.Dynamic) return;
            rb.bodyType = RigidbodyType2D.Dynamic;
            _sleptChunks.Remove(this);
            _restTimer = 0f;
        }

        // wake any slept chunks within radius of worldPos.
        // ProcessEraseCircle calls this on every dig. External systems (rockets,
        // explosions) can call this directly. Cheap — typically <50 slept chunks
        // even on heavy dig scenarios; iterates in O(n) once per dig event, not
        // per frame. The rb.simulated=true brings the chunk back into Box2D's
        // broadphase; from there normal physics + OnCollisionStay2D rest-detect
        // resume.
        private static void WakeNearbyChunks(Vector2 worldPos, float radius)
        {
            if (radius <= 0f || _sleptChunks.Count == 0) return;
            float sqrR = radius * radius;
            for (int i = _sleptChunks.Count - 1; i >= 0; i--)
            {
                var dt = _sleptChunks[i];
                if (dt == null || dt.rb == null)
                {
                    _sleptChunks.RemoveAt(i);
                    continue;
                }
                Vector2 center = dt.spriteRend != null ? (Vector2)dt.spriteRend.bounds.center : (Vector2)dt.transform.position;
                if ((center - worldPos).sqrMagnitude <= sqrR)
                {
                    dt.WakeUp();
                    _sleptChunks.RemoveAt(i);
                }
            }
        }

        // re-enable physics on a previously-slept chunk. Caller is
        // responsible for removing from _sleptChunks (WakeNearbyChunks does this
        // during iteration; OnDestroy/StampAndDestroy do so explicitly).
        private void WakeUp()
        {
            if (rb != null) rb.bodyType = RigidbodyType2D.Dynamic;
            _restTimer = 0f;
        }

        private System.Collections.IEnumerator CrunchAndMerge(DestructibleTerrain targetTerrain, float impactSpeed, Vector2 impactDirection)
        {

            // Skip merging chunks that overhang the mainland texture: StampAndDestroy
            // clamps the stamp to [0, texWidth/Height-1] and then deletes the chunk, so
            // any out-of-perimeter pixels would vanish. Leave it as a normal Dynamic
            // body (still falls + diggable); isMerging stays true so it won't retry.
            // (Assumes an unrotated mainland — the standard terrain case.)
            {
                Vector3 mBL = targetTerrain.transform.TransformPoint(targetTerrain.spriteMin);
                Vector3 mTR = targetTerrain.transform.TransformPoint(targetTerrain.spriteMin
                    + new Vector2(targetTerrain.texWidth, targetTerrain.texHeight) / targetTerrain.ppu);
                Bounds cb = spriteRend.bounds;
                if (cb.min.x < mBL.x || cb.min.y < mBL.y || cb.max.x > mTR.x || cb.max.y > mTR.y)
                {
                    yield break;
                }
            }

            // Use cached this.rb
            if (this.rb != null)
            {
                this.rb.bodyType = RigidbodyType2D.Kinematic;
                this.rb.linearVelocity = Vector2.zero;
                this.rb.angularVelocity = 0f;
            }

            GetComponentsInChildren<Collider2D>(true, sCollScratch);
            for (int i = 0; i < sCollScratch.Count; i++)
            {
                var coll = sCollScratch[i];
                if (coll != null) coll.enabled = false;
            }
            sCollScratch.Clear();

            // SETTLED MERGE FAST-PATH
            // Auto-merge from OnCollisionStay2D passes impactSpeed = 0. For these
            // settled chunks, the chunk has already come to rest in contact with the
            // mainland — physics has placed it where it belongs. Adding the standard
            // baselineEmbed lerp on top of any pre-existing physics overlap is what
            // produced the "way deep" embeds. Stamp at the current rest position.
            if (impactSpeed == 0f)
            {
                yield return null; // let collider-disable propagate one frame
                                   // throttle to MAX_MERGES_PER_FRAME globally. Multiple
                                   // simultaneous settled merges cascade in one frame otherwise.
                while (true)
                {
                    if (Time.frameCount != _lastMergeFrame) { _lastMergeFrame = Time.frameCount; _mergesThisFrame = 0; }
                    if (_mergesThisFrame < MAX_MERGES_PER_FRAME) { _mergesThisFrame++; break; }
                    yield return null;
                }
                targetTerrain.MergeChunk(this);
                yield break;
            }

            float baselineEmbed = 1.5f / ppu;
            float kineticEmbed = 0f;
            if (impactSpeed > minEmbedVelocity)
            {
                kineticEmbed = (impactSpeed - minEmbedVelocity) * (embedMultiplier / ppu);
            }

            // Prevent small chunks from embedding completely underground and vanishing
            float chunkHeight = spriteRend.bounds.size.y;
            float chunkWidth = spriteRend.bounds.size.x;
            float minDimension = Mathf.Min(chunkWidth, chunkHeight);
            float maxSafeEmbed = minDimension * 0.5f;

            float requestedEmbed = Mathf.Min(baselineEmbed + kineticEmbed, maxEmbedPixels / ppu);
            float finalEmbed = Mathf.Min(requestedEmbed, maxSafeEmbed);

            Vector3 startPos = transform.position;
            Vector3 endPos = startPos + (Vector3)(impactDirection * finalEmbed);

            float elapsed = 0f;
            while (elapsed < mergeDuration)
            {
                transform.position = Vector3.Lerp(startPos, endPos, elapsed / mergeDuration);
                elapsed += Time.deltaTime;
                yield return null;
            }

            transform.position = endPos;
            // throttle to MAX_MERGES_PER_FRAME globally.
            while (true)
            {
                if (Time.frameCount != _lastMergeFrame) { _lastMergeFrame = Time.frameCount; _mergesThisFrame = 0; }
                if (_mergesThisFrame < MAX_MERGES_PER_FRAME) { _mergesThisFrame++; break; }
                yield return null;
            }
            targetTerrain.MergeChunk(this);
        }

        private void MergeChunk(DestructibleTerrain fallingChunk)
        {
            StampAndDestroy(fallingChunk);
        }

        private void StampAndDestroy(DestructibleTerrain fallingChunk)
        {
            if (!fallingChunk.gameObject.activeInHierarchy || !fallingChunk.rawPixels.IsCreated) return;

            // Safety: StampAndDestroy is invoked from the CrunchAndMerge
            // coroutine which can run BEFORE LateUpdate (where mainland's async BFS
            // result is consumed). If BFS is still in flight, our solidMap writes
            // below race against the job's reads. Drain any in-flight async first.
            // Also process any pending detection result so we don't lose it.
            if (_hasPendingDetection)
            {
                // Drain the entire chain — same reasoning as the LateUpdate block.
                _globalBfsHandle.Complete();
                _hasPendingDetection = false;
                _globalBfsInFlight = false;
                currentSearchId = _myBfsResult[0];
                if (_myBfsResult[1] == 1)
                {
                    int detectedCount = _myBfsResult[2];
                    int minX = _myBfsResult[3];
                    int maxX = _myBfsResult[4];
                    int minY = _myBfsResult[5];
                    int maxY = _myBfsResult[6];
                    int outBufLen = _bfsOutputBuffer.IsCreated ? _bfsOutputBuffer.Length : 0;
                    int ipBufLen = islandPixelBuffer.IsCreated ? islandPixelBuffer.Length : 0;
                    bool valid =
                        detectedCount > 0 &&
                        detectedCount <= outBufLen &&
                        detectedCount <= ipBufLen &&
                        minX >= 0 && maxX < texWidth && minX <= maxX &&
                        minY >= 0 && maxY < texHeight && minY <= maxY;
                    if (valid)
                    {
                        islandMinX = minX; islandMaxX = maxX;
                        islandMinY = minY; islandMaxY = maxY;
                        NativeArray<int>.Copy(_bfsOutputBuffer, 0, islandPixelBuffer, 0, detectedCount);
                        islandPixelCount = detectedCount;
                        try
                        {
                            GameObject newIsland = ExtractAndSpawnIsland();
                            if (newIsland != null) reusableNewIslands.Add(newIsland);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"[Phase5/Merge] ExtractAndSpawnIsland threw: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[Phase5/Merge] Skipping invalid detection: count={detectedCount} bbox=[{minX},{maxX}]x[{minY},{maxY}] tex={texWidth}x{texHeight}");
                    }
                }
            }
            // removed the redundant `_globalBfsHandle.Complete()` that
            // is unnecessary here. With the NativeDisable on
            // BatchedScanAndBfsJob.solidMap, MergeStampJob's solidMap writes can
            // proceed concurrently with any in-flight BFS reads (byte-atomic on
            // ARMv8, BFS detection tolerates 1-frame stale state). The drain was
            // costly per merge during sustained dig (BFS continuously in flight),
            // making each large-chunk merge a LateUpdate spike.

            bool mapChanged = false;
            var fPixels = fallingChunk.rawPixels;
            int fWidth = fallingChunk.texWidth;
            int fHeight = fallingChunk.texHeight;
            float fPpu = fallingChunk.ppu;

            // PIVOT-AGNOSTIC BOUNDS
            // Get the 4 true local corners of the falling sprite, completely ignoring pivot offset!
            Bounds bounds = fallingChunk.spriteRend.sprite.bounds;
            sLocalCorners[0] = new Vector3(bounds.min.x, bounds.min.y, 0);
            sLocalCorners[1] = new Vector3(bounds.max.x, bounds.min.y, 0);
            sLocalCorners[2] = new Vector3(bounds.min.x, bounds.max.y, 0);
            sLocalCorners[3] = new Vector3(bounds.max.x, bounds.max.y, 0);

            // Create a matrix that converts directly from the falling chunk to the mainland
            Matrix4x4 fallToMain = this.transform.worldToLocalMatrix * fallingChunk.transform.localToWorldMatrix;

            Vector2 minLocal = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 maxLocal = new Vector2(float.MinValue, float.MinValue);

            // Transform the 4 corners and find the tightest possible bounding box
            for (int i = 0; i < 4; i++)
            {
                Vector3 mainLocalPoint = fallToMain.MultiplyPoint3x4(sLocalCorners[i]) - (Vector3)this.spriteMin;

                if (mainLocalPoint.x < minLocal.x) minLocal.x = mainLocalPoint.x;
                if (mainLocalPoint.x > maxLocal.x) maxLocal.x = mainLocalPoint.x;
                if (mainLocalPoint.y < minLocal.y) minLocal.y = mainLocalPoint.y;
                if (mainLocalPoint.y > maxLocal.y) maxLocal.y = mainLocalPoint.y;
            }

            // We already cached fallVel in the previous optimization!
            Vector2 fallVel = fallingChunk.rb != null ? fallingChunk.rb.linearVelocity : Vector2.zero;

            // Prevents rotated bounds from calculating negative spans that silently abort the loop
            int x0 = Mathf.FloorToInt(minLocal.x * ppu) - 1;
            int x1 = Mathf.CeilToInt(maxLocal.x * ppu) + 1;
            int y0 = Mathf.FloorToInt(minLocal.y * ppu) - 1;
            int y1 = Mathf.CeilToInt(maxLocal.y * ppu) + 1;

            int startX = Mathf.Clamp(Mathf.Min(x0, x1), 0, texWidth - 1);
            int endX = Mathf.Clamp(Mathf.Max(x0, x1), 0, texWidth - 1);
            int startY = Mathf.Clamp(Mathf.Min(y0, y1), 0, texHeight - 1);
            int endY = Mathf.Clamp(Mathf.Max(y0, y1), 0, texHeight - 1);

            Matrix4x4 worldToFallLocal = fallingChunk.transform.worldToLocalMatrix;
            Matrix4x4 mainLocalToWorld = this.transform.localToWorldMatrix;
            Matrix4x4 mainToFall = worldToFallLocal * mainLocalToWorld;

            Vector3 pOrigin = mainToFall.MultiplyPoint3x4(new Vector3(spriteMin.x, spriteMin.y, 0));
            Vector3 stepX = mainToFall.MultiplyVector(new Vector3(1f / ppu, 0, 0));
            Vector3 stepY = mainToFall.MultiplyVector(new Vector3(0, 1f / ppu, 0));

            Vector3 pOriginAdj = pOrigin - (Vector3)fallingChunk.spriteMin;

            // The Vector3 += stepX in the per-pixel loop was expensive on big stamps
            // because Vector3.op_Addition wasn't being inlined.
            // Pre-multiply the steps by fPpu and accumulate two floats instead.
            float stepX_fx = stepX.x * fPpu;
            float stepX_fy = stepX.y * fPpu;
            float stepY_fx = stepY.x * fPpu;
            float stepY_fy = stepY.y * fPpu;
            float origin_fx = pOriginAdj.x * fPpu;
            float origin_fy = pOriginAdj.y * fPpu;

            int dirtyMinX = texWidth, dirtyMinY = texHeight;
            int dirtyMaxX = 0, dirtyMaxY = 0;

            // Calculate the cutoff ONCE before the massive loops
            byte alphaCutoff = (byte)Mathf.Clamp(alphaThreshold * 255f, 1f, 255f);

            // BURST MERGE STAMP
            // Single Burst path; Burst strips the NativeArray-safety overhead that
            // dominated the managed merge. Particle emission is buffered
            // (no Unity API access in Burst) and drained on the main thread post-job.

            if (!_mergeParticles.IsCreated)
            {
                _mergeParticles = new NativeList<MergeParticleEvent>(4096, Allocator.Persistent);
            }
            else
            {
                _mergeParticles.Clear();
            }
            if (!_mergeResult.IsCreated)
            {
                _mergeResult = new NativeArray<int>(5, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            var mergeJob = new MergeStampJob
            {
                fPixels = fPixels,
                rawPixels = this.rawPixels,
                solidMap = this.solidMap,
                particleEvents = _mergeParticles,
                result = _mergeResult,
                alphaCutoff = alphaCutoff,
                texWidth = texWidth,
                fWidth = fWidth,
                fHeight = fHeight,
                startX = startX,
                endX = endX,
                startY = startY,
                endY = endY,
                origin_fx = origin_fx,
                origin_fy = origin_fy,
                stepX_fx = stepX_fx,
                stepX_fy = stepX_fy,
                stepY_fx = stepY_fx,
                stepY_fy = stepY_fy,
                enableDebrisParticles = (byte)(enableDebrisParticles ? 1 : 0),
                particleSpawnChance = particleSpawnChance,
                rng = new Unity.Mathematics.Random((uint)((Time.frameCount + 1) * 9747u + 1u)),
            };
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmStamp_MergeStamp.Begin();
#endif
            mergeJob.Run();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmStamp_MergeStamp.End();
#endif

            dirtyMinX = _mergeResult[0];
            dirtyMaxX = _mergeResult[1];
            dirtyMinY = _mergeResult[2];
            dirtyMaxY = _mergeResult[3];
            mapChanged = _mergeResult[4] == 1;

            // Drain buffered particle events on the main thread.
            // cap per-merge emission. With particleSpawnChance ~0.05 and
            // a 600×600 merge overlap, the queue can hold thousands of events.
            // Each ParticleSystem.Emit is cheap but in aggregate (1000+) becomes
            // a spike. Cap to MAX_PARTICLES_PER_MERGE to keep merge frames
            // responsive. Excess particles are discarded — visually imperceptible
            // because 256 particles already saturates a typical merge effect.
            if (enableDebrisParticles && _mergeParticles.Length > 0)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmStamp_Particles.Begin();
#endif
                int evtCount = _mergeParticles.Length;
                int maxToEmit = evtCount < MAX_PARTICLES_PER_MERGE ? evtCount : MAX_PARTICLES_PER_MERGE;
                for (int i = 0; i < maxToEmit; i++)
                {
                    var ev = _mergeParticles[i];
                    Vector2 staticLocal = new Vector2(ev.sx, ev.sy) / this.ppu + this.spriteMin;
                    Vector3 worldPos = mainLocalToWorld.MultiplyPoint3x4(new Vector3(staticLocal.x, staticLocal.y, 0));
                    EmitDebrisParticle(worldPos, ev.existing, fallVel);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmStamp_Particles.End();
#endif
            }

            if (mapChanged)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmStamp_FlagDirty.Begin();
#endif
                int cx0 = Mathf.Clamp(dirtyMinX / chunkSize, 0, chunksX - 1);
                int cx1 = Mathf.Clamp(dirtyMaxX / chunkSize, 0, chunksX - 1);
                int cy0 = Mathf.Clamp(dirtyMinY / chunkSize, 0, chunksY - 1);
                int cy1 = Mathf.Clamp(dirtyMaxY / chunkSize, 0, chunksY - 1);

                for (int cy = cy0; cy <= cy1; cy++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        FlagChunkDirty(cx + cy * chunksX);
                    }
                }

                if (visualChunks != null)
                {
                    int vx0 = Mathf.Clamp(dirtyMinX / visualChunkSize, 0, visualChunksX - 1);
                    int vx1 = Mathf.Clamp(dirtyMaxX / visualChunkSize, 0, visualChunksX - 1);
                    int vy0 = Mathf.Clamp(dirtyMinY / visualChunkSize, 0, visualChunksY - 1);
                    int vy1 = Mathf.Clamp(dirtyMaxY / visualChunkSize, 0, visualChunksY - 1);

                    for (int vy = vy0; vy <= vy1; vy++)
                    {
                        for (int vx = vx0; vx <= vx1; vx++)
                        {
                            int vIdx = vx + vy * visualChunksX;
                            FlagVisualDirty(vIdx);
                            var vc = visualChunks[vIdx];
                            int lx0 = Mathf.Clamp(dirtyMinX - vc.startX, 0, vc.width - 1);
                            int lx1 = Mathf.Clamp(dirtyMaxX - vc.startX, 0, vc.width - 1);
                            int ly0 = Mathf.Clamp(dirtyMinY - vc.startY, 0, vc.height - 1);
                            int ly1 = Mathf.Clamp(dirtyMaxY - vc.startY, 0, vc.height - 1);
                            if (lx0 < visualChunks[vIdx].minDirtyX) visualChunks[vIdx].minDirtyX = lx0;
                            if (lx1 > visualChunks[vIdx].maxDirtyX) visualChunks[vIdx].maxDirtyX = lx1;
                            if (ly0 < visualChunks[vIdx].minDirtyY) visualChunks[vIdx].minDirtyY = ly0;
                            if (ly1 > visualChunks[vIdx].maxDirtyY) visualChunks[vIdx].maxDirtyY = ly1;
                        }
                    }
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmStamp_FlagDirty.End();
                s_pmStamp_DestroyVisuals.Begin();
#endif

                SpriteRenderer fallingSr = fallingChunk.GetComponent<SpriteRenderer>();
                if (fallingSr.sprite != null)
                {
                    // keep the Texture2D for reuse on the next spawn into
                    // this pool slot. Sprite still gets destroyed (it's a thin
                    // view, cheap to recreate). Skipping `new Texture2D` allocation
                    // saves time per spawn cycle.
                    Texture2D oldTex = fallingSr.sprite.texture;
                    if (oldTex != null && fallingChunk._pooledTexture == null)
                    {
                        fallingChunk._pooledTexture = oldTex;
                    }
                    else if (oldTex != null)
                    {
                        // Already have a pooled texture (shouldn't happen normally);
                        // destroy the redundant one to avoid leak.
                        Destroy(oldTex);
                    }
                    Destroy(fallingSr.sprite);
                    fallingSr.sprite = null;
                }

                fallingChunk.rawPixels = default;
                // dispose any visual sub-chunks created for big falling
                // chunks. Their Texture2Ds + child GameObjects must be released so
                // the next pool dequeue starts clean (and small-chunk reuse can fall
                // back to parent-spriteRend rendering). Re-enable the parent
                // spriteRend so the small-chunk single-texture path works.
                if (fallingChunk.visualChunks != null)
                {
                    fallingChunk.DestroyFallingVisualChunks();
                }
                if (fallingChunk.spriteRend != null && !fallingChunk.spriteRend.enabled)
                {
                    fallingChunk.spriteRend.enabled = true;
                }
                // dispose the falling chunk's BFS output buffer when it
                // returns to pool. The buffer is up to 20MB per instance — without
                // disposal here, all 50 pool slots accumulate the largest size
                // they've ever held (~1 GB worst case → mobile OOM).
                // Drain any in-flight BFS first so the dispose is safe.
                if (fallingChunk._hasPendingDetection)
                {
                    fallingChunk._myBfsHandle.Complete();
                    fallingChunk._hasPendingDetection = false;
                }
                if (fallingChunk._bfsOutputBuffer.IsCreated)
                {
                    fallingChunk._bfsOutputBuffer.Dispose();
                    fallingChunk._bfsOutputBuffer = default;
                }
                // CRITICAL: same leak applies to per-instance solidMap. Pool slots
                // that ever held a 4096² chunk keep 16MB allocated. With 50 slots
                // worst case = 800MB ghost memory → the 2.2GB consumption observed
                // on mobile. Dispose here; reallocated fresh in InitializeAs...
                if (fallingChunk.solidMap.IsCreated)
                {
                    fallingChunk.solidMap.Dispose();
                    fallingChunk.solidMap = default;
                }
                // defensive — remove the merged-out chunk from slept
                // registry so a re-spawn into this pool slot starts fresh.
                _sleptChunks.Remove(fallingChunk);
                fallingChunk.gameObject.SetActive(false);
                islandPool.Enqueue(fallingChunk.gameObject);

                // Merge is a discrete event — drain the visuals in one frame so the
                // user doesn't see the merged region populate bottom-to-top via the
                // ApplyVisuals throttle.
                forceDrainVisuals = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmStamp_DestroyVisuals.End();
#endif
            }
            else
            {
                fallingChunk.GetComponentsInChildren<Collider2D>(true, sCollScratch);
                for (int i = 0; i < sCollScratch.Count; i++)
                {
                    var coll = sCollScratch[i];
                    if (coll != null) coll.enabled = true;
                }
                sCollScratch.Clear();

                if (fallingChunk.rb != null) fallingChunk.rb.bodyType = RigidbodyType2D.Dynamic;

                fallingChunk.isMerging = false; // Reset so it can try merging again later
            }
        }

        #endregion

        #region Digging & Extraction
        // Returns false if the texture cannot be cloned (e.g. Read/Write disabled).
        // Caller MUST check the return value and abort further init — setting
        // `enabled = false` does not halt the current Awake() call, only future
        // Update/LateUpdate dispatches.
        private bool CloneTexture()
        {
            Texture2D texture = spriteRend.sprite.texture;

            // Hard-fail with a clear message if Read/Write isn't enabled. Without
            // this guard, GetRawTextureData<Color32>() throws a generic
            // UnityException with no hint about what to do.
            if (!texture.isReadable)
            {
                Debug.LogError(
                    $"[DestructibleTerrain] Texture '{texture.name}' is not readable. " +
                    "Open its Inspector \u2192 Advanced \u2192 enable 'Read/Write'.", this);
                enabled = false;
                return false;
            }

            tex = Instantiate(texture);
            // Apply runtime-mutable texture settings. Import-time settings (format,
            // mipmaps, sprite mode, alpha-is-transparency, max size) are configured
            // by the custom editor and are immutable here.
            tex.filterMode = textureSettings.filterMode;
            tex.wrapMode = textureSettings.wrapMode;
            tex.anisoLevel = textureSettings.anisoLevel;

            Vector2 pivotNorm = new Vector2(spriteRend.sprite.pivot.x / spriteRend.sprite.rect.width, spriteRend.sprite.pivot.y / spriteRend.sprite.rect.height);
            spriteRend.sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), pivotNorm, textureSettings.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            spriteRend.enabled = false;
            return true;
        }

        public void EraseCircle(Vector2 worldPos, float radius)
        {
            _lastDigFrame = Time.frameCount;
            pendingDamage.Add(new DamageEvent { pos = worldPos, radius = radius });
        }

        private void ProcessEraseCircle(Vector2 worldPos, float radius)
        {
            // wake any slept falling chunks near this dig. Use the
            // bigger of (dig radius + sleepWakeRadius) so chunks just outside the
            // dig still react to nearby disturbance. No-op if list is empty.
            WakeNearbyChunks(worldPos, radius + sleepWakeRadius);
            // Premature bounds rejection leaves indestructible 1-pixel bridges at the edges!

            Vector2 local = (Vector2)transform.InverseTransformPoint(worldPos) - spriteMin;
            Vector2 pixCenterF = local * ppu;

            int pixCX = Mathf.RoundToInt(pixCenterF.x);
            int pixCY = Mathf.RoundToInt(pixCenterF.y);

            int radiusPx = Mathf.CeilToInt(radius * ppu);
            int totalRadiusPx = radiusPx + aftermathThickness;

            int sqrR = radiusPx * radiusPx;
            int sqrTotalR = totalRadiusPx * totalRadiusPx;

            int cxMin = Mathf.FloorToInt((pixCX - totalRadiusPx) / (float)chunkSize);
            int cyMin = Mathf.FloorToInt((pixCY - totalRadiusPx) / (float)chunkSize);
            int cxMax = Mathf.FloorToInt((pixCX + totalRadiusPx) / (float)chunkSize);
            int cyMax = Mathf.FloorToInt((pixCY + totalRadiusPx) / (float)chunkSize);

            if (visualChunks != null)
            {
                int vXMin = Mathf.Clamp(Mathf.FloorToInt((pixCX - totalRadiusPx) / (float)visualChunkSize), 0, visualChunksX - 1);
                int vXMax = Mathf.Clamp(Mathf.FloorToInt((pixCX + totalRadiusPx) / (float)visualChunkSize), 0, visualChunksX - 1);
                int vYMin = Mathf.Clamp(Mathf.FloorToInt((pixCY - totalRadiusPx) / (float)visualChunkSize), 0, visualChunksY - 1);
                int vYMax = Mathf.Clamp(Mathf.FloorToInt((pixCY + totalRadiusPx) / (float)visualChunkSize), 0, visualChunksY - 1);

                // Compute exact pixel rect of the brush AABB
                int brushX0 = Mathf.Max(0, pixCX - totalRadiusPx);
                int brushX1 = Mathf.Min(texWidth - 1, pixCX + totalRadiusPx);
                int brushY0 = Mathf.Max(0, pixCY - totalRadiusPx);
                int brushY1 = Mathf.Min(texHeight - 1, pixCY + totalRadiusPx);

                for (int vy = vYMin; vy <= vYMax; vy++)
                {
                    for (int vx = vXMin; vx <= vXMax; vx++)
                    {
                        int vIdx = vx + vy * visualChunksX;
                        FlagVisualDirty(vIdx);

                        var vc = visualChunks[vIdx];
                        int lx0 = Mathf.Clamp(brushX0 - vc.startX, 0, vc.width - 1);
                        int lx1 = Mathf.Clamp(brushX1 - vc.startX, 0, vc.width - 1);
                        int ly0 = Mathf.Clamp(brushY0 - vc.startY, 0, vc.height - 1);
                        int ly1 = Mathf.Clamp(brushY1 - vc.startY, 0, vc.height - 1);

                        if (lx0 < visualChunks[vIdx].minDirtyX) visualChunks[vIdx].minDirtyX = lx0;
                        if (lx1 > visualChunks[vIdx].maxDirtyX) visualChunks[vIdx].maxDirtyX = lx1;
                        if (ly0 < visualChunks[vIdx].minDirtyY) visualChunks[vIdx].minDirtyY = ly0;
                        if (ly1 > visualChunks[vIdx].maxDirtyY) visualChunks[vIdx].maxDirtyY = ly1;
                    }
                }
            }

            bool anyChanged = false;
            bool isMainlandCached = IsMainland;

            // BURST DIG
            // Single Burst job over the whole brush AABB; Burst strips the
            // NativeArray-safety overhead on the solidMap reads/writes.
            if (!_eraseResult.IsCreated)
            {
                _eraseResult = new NativeArray<int>(6, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            var eraseJob = new EraseBrushJob
            {
                rawPixels = this.rawPixels,
                solidMap = this.solidMap,
                result = _eraseResult,
                aftermath = aftermathColor,
                texWidth = texWidth,
                texHeight = texHeight,
                pixCX = pixCX,
                pixCY = pixCY,
                sqrR = sqrR,
                sqrTotalR = sqrTotalR,
                totalRadiusPx = totalRadiusPx,
                trackSolidDelta = 1,
            };

            eraseJob.Run();

            anyChanged = _eraseResult[4] == 1;
            if (anyChanged)
            {
                // Apply the (negative) solid delta so currentSolidPixels stays accurate.
                // The mainland needs it too now, for the anchorWholeBody whole-body test.
                int newCount = currentSolidPixels + _eraseResult[5];
                currentSolidPixels = newCount > 0 ? newCount : 0;
                // Falling chunks (visualChunks==null) need this to upload their texture
                // next LateUpdate; the mainland uses the visual sub-chunk dirty list.
                if (!isMainlandCached) _singleTextureDirty = true;
            }

            if (anyChanged)
            {
                int dMinX = _eraseResult[0];
                int dMaxX = _eraseResult[1];
                int dMinY = _eraseResult[2];
                int dMaxY = _eraseResult[3];

                int actCxMin = Mathf.Clamp(dMinX / chunkSize, 0, chunksX - 1);
                int actCxMax = Mathf.Clamp(dMaxX / chunkSize, 0, chunksX - 1);
                int actCyMin = Mathf.Clamp(dMinY / chunkSize, 0, chunksY - 1);
                int actCyMax = Mathf.Clamp(dMaxY / chunkSize, 0, chunksY - 1);

                for (int cy = actCyMin; cy <= actCyMax; cy++)
                {
                    for (int cx = actCxMin; cx <= actCxMax; cx++)
                    {
                        int chunkIdx = cx + cy * chunksX;
                        FlagChunkDirty(chunkIdx);
                        chunkSignatures[chunkIdx] = -1;
                    }
                }
            }

            // Only schedule a boundary scan if pixels actually changed.
            // Prevents ghost digs (brush misses chunk entirely) from triggering O(W*H) scans.
            if (anyChanged)
            {
                pendingScans.Add(new ScanRequest { pixCX = pixCX, pixCY = pixCY, radiusPx = radiusPx });
            }
        }

        void ScanBoundaryForIslands(int pixCX, int pixCY, int radiusPx)
        {
            // We only need to check pixels immediately adjacent to the erased circle.
            // Walk the circle perimeter using Bresenham's midpoint algorithm — O(radius) not O(radius²).

            int r = radiusPx + 1; // One pixel outside the erased zone
            int x = r, y = 0;
            int err = 1 - r;

            while (x >= y)
            {
                // 8 octant points per step — check each for unvisited solid pixels
                CheckBoundaryPixel(pixCX + x, pixCY + y);
                CheckBoundaryPixel(pixCX - x, pixCY + y);
                CheckBoundaryPixel(pixCX + x, pixCY - y);
                CheckBoundaryPixel(pixCX - x, pixCY - y);
                CheckBoundaryPixel(pixCX + y, pixCY + x);
                CheckBoundaryPixel(pixCX - y, pixCY + x);
                CheckBoundaryPixel(pixCX + y, pixCY - x);
                CheckBoundaryPixel(pixCX - y, pixCY - x);

                y++;
                if (err < 0)
                {
                    err += 2 * y + 1;
                }
                else
                {
                    x--;
                    err += 2 * (y - x) + 1;
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void CheckBoundaryPixel(int gx, int gy)
        {
            if (gx < 0 || gx >= texWidth || gy < 0 || gy >= texHeight) return;

            int index = gy * texWidth + gx;
            if (solidMap[index] == 0) return;

            int v = pixelVisitId[index];
            if ((v >= startSearchId && v <= currentSearchId) || v == mainlandId) return;

            bool isFloating = DetectFloatingIsland(gx, gy);
            if (isFloating)
            {
                GameObject newIsland = ExtractAndSpawnIsland();
                if (newIsland != null) reusableNewIslands.Add(newIsland);
            }
        }

        void CreateVisualChunks()
        {
            CreateVisualChunks(deferInitialUpload: false);
        }

        // Deferred initial upload mode for falling chunks.
        // With deferInitialUpload=true, we skip the bulk pixel copy AND the per-
        // sub-chunk tex.Apply at creation time. Each sub-chunk is marked fully
        // dirty and queued to dirtyVisualsList. ApplyVisuals' existing throttle
        // (maxVisualsPerFrame) catches up over the next 5-30 frames depending on
        // chunk size, with each frame uploading at most maxVisualsPerFrame chunks.
        // During catchup the parent SpriteRenderer stays VISIBLE so the chunk
        // doesn't disappear — sub-chunks render on top with whatever GPU-init
        // state Unity gave them (typically zero/transparent on most GPUs). Once
        // dirtyVisualsList drains, the parent is hidden by ApplyVisuals.
        void CreateVisualChunks(bool deferInitialUpload)
        {
            visualChunksX = Mathf.CeilToInt((float)texWidth / visualChunkSize);
            visualChunksY = Mathf.CeilToInt((float)texHeight / visualChunkSize);
            int totalVisualChunks = visualChunksX * visualChunksY;
            visualChunks = new VisualChunk[totalVisualChunks];

            // INIT ARRAYS
            isVisualDirty = new bool[totalVisualChunks];
            dirtyVisualsList.Clear();

            Vector2 pivotNorm = spriteRend.sprite.pivot / spriteRend.sprite.rect.size;
            Vector2 spriteWorldSize = new Vector2(texWidth, texHeight) / ppu;
            Vector2 localBL = -Vector2.Scale(spriteWorldSize, pivotNorm);

            for (int vy = 0; vy < visualChunksY; vy++)
            {
                for (int vx = 0; vx < visualChunksX; vx++)
                {
                    int idx = vx + vy * visualChunksX;

                    int startX = vx * visualChunkSize;
                    int startY = vy * visualChunkSize;
                    int w = Mathf.Min(visualChunkSize, texWidth - startX);
                    int h = Mathf.Min(visualChunkSize, texHeight - startY);

                    // get pooled item or create new on first use of this slot.
                    PooledVisualChunk pc;
                    bool sizeChanged;
                    if (idx < _vcPool.Count)
                    {
                        pc = _vcPool[idx];
                        sizeChanged = (pc.curWidth != w || pc.curHeight != h);
                        if (sizeChanged)
                        {
                            pc.tex.Reinitialize(w, h, TextureFormat.RGBA32, false);
                            pc.curWidth = w;
                            pc.curHeight = h;
                        }
                    }
                    else
                    {
                        Texture2D vTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                        vTex.filterMode = tex.filterMode;
                        // was $"VisualChunk_{vx}_{vy}" — String.Format
                        // has measurable GC cost across cold-fill chunks.
                        // Constant name; debugging by index is just as easy via
                        // sibling order in the hierarchy.
                        GameObject vgo = new GameObject("VisualChunk");
                        vgo.transform.SetParent(transform, false);
                        SpriteRenderer vsr = vgo.AddComponent<SpriteRenderer>();
                        vsr.sortingLayerID = spriteRend.sortingLayerID;
                        pc = new PooledVisualChunk { vgo = vgo, vsr = vsr, tex = vTex, curWidth = w, curHeight = h };
                        _vcPool.Add(pc);
                        sizeChanged = true; // first-use forces sprite create
                    }

                    VisualChunk vc = new VisualChunk
                    {
                        tex = pc.tex,
                        pixels = pc.tex.GetRawTextureData<Color32>(),
                        startX = startX,
                        startY = startY,
                        width = w,
                        height = h,
                        renderer = pc.vsr,
                    };
                    vc.ResetDirty();

                    if (!deferInitialUpload)
                    {
                        // Mainland path: bulk-copy + Apply at creation. Done once at
                        // game start so the spike is amortized against load time.
                        for (int y = 0; y < h; y++)
                        {
                            NativeSlice<Color32> src = new NativeSlice<Color32>(rawPixels, (startY + y) * texWidth + startX, w);
                            NativeSlice<Color32> dst = new NativeSlice<Color32>(vc.pixels, y * w, w);
                            dst.CopyFrom(src);
                        }
                        pc.tex.Apply(false);
                    }
                    else
                    {
                        // Deferred path: skip bulk copy + Apply. Mark fully dirty so
                        // ApplyVisuals catches up over the next several frames.
                        vc.minDirtyX = 0;
                        vc.maxDirtyX = w - 1;
                        vc.minDirtyY = 0;
                        vc.maxDirtyY = h - 1;
                        vc.isDirty = true;
                        isVisualDirty[idx] = true;
                        dirtyVisualsList.Add(idx);
                    }

                    // Position transform (always — chunk's spatial layout per spawn).
                    Vector2 chunkOffset = new Vector2(startX, startY) / ppu;
                    Vector2 centerOffset = new Vector2(w / 2f, h / 2f) / ppu;
                    pc.vgo.transform.localPosition = localBL + chunkOffset + centerOffset;
                    pc.vgo.transform.localRotation = Quaternion.identity;
                    pc.vgo.transform.localScale = Vector2.one;

                    // only recreate sprite when size actually changes.
                    // Inner sub-chunks (always w=h=visualChunkSize) keep their
                    // sprite across reuses (avoids per-chunk sprite recreation).
                    if (sizeChanged || pc.vsr.sprite == null)
                    {
                        if (pc.vsr.sprite != null) Destroy(pc.vsr.sprite);
                        pc.vsr.sprite = Sprite.Create(pc.tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
                    }
                    pc.vsr.sortingOrder = spriteRend.sortingOrder + (deferInitialUpload ? 1 : 0);

                    visualChunks[idx] = vc;
                    _vcPool[idx] = pc; // store updated curWidth/curHeight

                    // Activate / deactivate per the deferred-upload rule.
                    pc.vsr.enabled = true; // re-enable in case prior tenant disabled
                    if (deferInitialUpload)
                    {
                        // hide deferred sub-chunks until first upload.
                        pc.vgo.SetActive(false);
                    }
                    else
                    {
                        if (!pc.vgo.activeSelf) pc.vgo.SetActive(true);
                    }
                }
            }

            // deactivate any pool entries beyond the current spawn's count.
            // Previous tenant of this slot may have used more visual chunks.
            for (int i = totalVisualChunks; i < _vcPool.Count; i++)
            {
                var pc = _vcPool[i];
                if (pc.vgo != null) pc.vgo.SetActive(false);
            }
        }

        // VISUAL CHUNK CLEANUP
        // Destroys all child VisualChunk_* GameObjects, their Sprites, and their
        // Texture2Ds. Called when a falling chunk returns to the pool so the next
        // dequeue starts clean (or downsizes to the small-chunk single-texture path).
        // Mainland never calls this — its sub-chunks live for the whole session.
        void DestroyFallingVisualChunks()
        {
            if (visualChunks == null) return;
            // pooled visual chunks. Deactivate instead of destroying so
            // the next spawn into this pool slot can reuse the GameObject +
            // SpriteRenderer + Texture2D, avoiding per-visual-chunk recreation
            // across the spawn. Sprite stays attached for potential reuse
            // (recreated on next spawn only if dimensions change).
            // The renderer reference in each VisualChunk struct points into the
            // pool entry — disabling the GameObject below disables rendering.
            for (int i = 0; i < _vcPool.Count; i++)
            {
                var pc = _vcPool[i];
                if (pc.vgo != null)
                {
                    if (pc.vsr != null) pc.vsr.enabled = false;
                    pc.vgo.SetActive(false);
                }
            }
            // Clear per-spawn state. The pool persists for next reuse.
            visualChunks = null;
            isVisualDirty = null;
            dirtyVisualsList.Clear();
            _deferredParentDisable = false;
        }

        void ApplyVisuals()
        {
            if (visualChunks != null)
            {
                // Throttled visual update — process from end of dirtyVisualsList using
                // O(1) RemoveAt(last). Mirrors the throttled rebuild pattern. Defers
                // overflow to next frame. Critical for chunk-separation spikes where
                // many visual chunks become fully dirty in one frame (expensive
                // in row-by-row NativeArray.Copy alone).
                // forceDrainVisuals: discrete events like merge (StampAndDestroy) need
                // an immediate full update — they don't repeat every frame, and a smear
                // over 4-6 frames is visually jarring (looks like the chunk reloads
                // bottom-to-top). When the flag is set, use a much larger budget than
                // normal but still bounded; an unlimited drain on a big merge would
                // defeat the whole throttle.
                int budget = forceDrainVisuals ? Mathf.Max(maxVisualsPerFrame, 32) : maxVisualsPerFrame;
                forceDrainVisuals = false;
                // time-budget mirror of the RebuildChunk loop.
                // ApplyVisuals spikes the same shape as the RebuildChunk
                // spike, where many 256×256 tex.Apply calls cluster on a merge or
                // brush-spanning-many-sub-chunks frame. Time-cap the loop so a
                // run of slow texture uploads spreads across frames instead of
                // burning a single frame.
                long visualsTimeBudgetTicks = visualsBudgetMs > 0f
                    ? (long)(visualsBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0)
                    : long.MaxValue;
                long visualsStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                while (budget > 0 && dirtyVisualsList.Count > 0)
                {
                    int last = dirtyVisualsList.Count - 1;
                    int vIdx = dirtyVisualsList[last];
                    dirtyVisualsList.RemoveAt(last);

                    VisualChunk vc = visualChunks[vIdx];
                    if (!vc.isDirty || !isVisualDirty[vIdx]) continue;

                    // THE DIRTY RECT FILL
                    // Only copy the rows and columns that actually changed!
                    int startY = vc.minDirtyY;
                    int endY = vc.maxDirtyY;
                    int copyWidth = (vc.maxDirtyX - vc.minDirtyX) + 1;

                    // Silently skip if the math inverted or pushed out of bounds
                    if (copyWidth <= 0 || startY > endY || startY >= vc.height)
                    {
                        vc.ResetDirty();
                        visualChunks[vIdx] = vc;
                        isVisualDirty[vIdx] = false;
                        continue;
                    }

                    for (int y = startY; y <= endY; y++)
                    {
                        // Calculate the exact source and destination offsets for this dirty row segment
                        int sourceIdx = (vc.startY + y) * texWidth + (vc.startX + vc.minDirtyX);
                        int destIdx = y * vc.width + vc.minDirtyX;

                        // rawPixels is the canonical source (managedRawPixels removed).
                        // NativeArray.Copy is internal-memcpy fast in both editor and release.
                        NativeArray<Color32>.Copy(rawPixels, sourceIdx, vc.pixels, destIdx, copyWidth);
                    }

                    vc.tex.Apply(false);

                    // deferred sub-chunks are SetActive(false) at creation
                    // so their uninitialized GPU texture doesn't render as gray. After
                    // the first tex.Apply, they have correct pixel data — activate them
                    // so they replace the parent SpriteRenderer's view of this region.
                    if (vc.renderer != null && !vc.renderer.gameObject.activeSelf)
                    {
                        vc.renderer.gameObject.SetActive(true);
                    }

                    vc.ResetDirty();

                    visualChunks[vIdx] = vc;
                    isVisualDirty[vIdx] = false; // Reset the flag
                    budget--;

                    // bail if the time budget burned. Remaining dirty
                    // visuals stay queued in dirtyVisualsList + isVisualDirty[]
                    // for next frame.
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - visualsStartTicks) >= visualsTimeBudgetTicks)
                        break;
                }
                // Note: do NOT call dirtyVisualsList.Clear() — overflow entries stay
                // queued for next frame's ApplyVisuals pass.

                // deferred-upload catchup complete — disable the parent
                // SpriteRenderer that was kept visible while sub-chunks populated
                // their first GPU upload. Once dirtyVisualsList is empty AND the
                // flag is set, all sub-chunks have rendered correct pixels at
                // least once, so the parent's stale image can safely be hidden.
                if (_deferredParentDisable && dirtyVisualsList.Count == 0)
                {
                    if (spriteRend != null) spriteRend.enabled = false;
                    _deferredParentDisable = false;
                }
            }
            else
            {
                // gate on _singleTextureDirty so we don't fire a full
                // tex.Apply every LateUpdate when nothing changed. ProcessEraseCircle
                // and ExtractAndSpawnIsland set the flag when they touch rawPixels;
                // ApplyVisuals consumes + clears it, avoiding redundant per-frame
                // work per active falling chunk (a bigger win in the editor).
                if (_singleTextureDirty)
                {
                    if (tex != null) tex.Apply(false);
                    _singleTextureDirty = false;
                }
                dirtyVisualsList.Clear();
            }
        }

        void FullyDirtyVisualChunk(int vIdx)
        {
            FlagVisualDirty(vIdx);
            visualChunks[vIdx].isDirty = true;
            visualChunks[vIdx].minDirtyX = 0;
            visualChunks[vIdx].minDirtyY = 0;
            visualChunks[vIdx].maxDirtyX = visualChunks[vIdx].width - 1;
            visualChunks[vIdx].maxDirtyY = visualChunks[vIdx].height - 1;
        }

        void MarkVisualDirty(int px, int py)
        {
            if (visualChunks == null) return;

            int vx = Mathf.Clamp(px / visualChunkSize, 0, visualChunksX - 1);
            int vy = Mathf.Clamp(py / visualChunkSize, 0, visualChunksY - 1);
            int vIdx = vx + vy * visualChunksX;

            // Calculate local coordinates within this specific chunk
            int localX = px - visualChunks[vIdx].startX;
            int localY = py - visualChunks[vIdx].startY;

            // Expand the dirty rectangle
            if (localX < visualChunks[vIdx].minDirtyX) visualChunks[vIdx].minDirtyX = localX;
            if (localX > visualChunks[vIdx].maxDirtyX) visualChunks[vIdx].maxDirtyX = localX;
            if (localY < visualChunks[vIdx].minDirtyY) visualChunks[vIdx].minDirtyY = localY;
            if (localY > visualChunks[vIdx].maxDirtyY) visualChunks[vIdx].maxDirtyY = localY;

            //visualChunks[vIdx].isDirty = true;
            FlagVisualDirty(vIdx);
        }

        bool DetectFloatingIsland(int startX, int startY)
        {
            // Lazy-allocate the 7-int result buffer. Static so all DT instances share it
            // (BFS calls don't overlap — single-threaded entry point).
            if (!_bfsResult.IsCreated)
            {
                _bfsResult = new NativeArray<int>(7, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // drain any in-flight async BFS so we don't race on shared
            // pixelVisitId / floodStack / islandPixelBuffer. In typical play this
            // wrapper is only called by falling chunks (rare path), so the wait
            // is essentially a no-op.
            _globalBfsHandle.Complete();

            new DetectFloatingIslandJob
            {
                solidMap = solidMap,
                pixelVisitId = pixelVisitId,
                islandPixelBuffer = islandPixelBuffer,
                floodStack = floodStack,
                result = _bfsResult,
                texWidth = texWidth,
                texHeight = texHeight,
                isMainland = IsMainland,
                groundAnchorY = groundAnchorY,
                anchorToSideBorders = anchorToSideBorders,
                anchorWholeBody = anchorWholeBody,
                maxPhysicsPixels = maxPhysicsPixels,
                mainlandId = mainlandId,
                startSearchId = startSearchId,
                currentSearchIdIn = currentSearchId,
                currentSolidPixels = currentSolidPixels,
                startX = startX,
                startY = startY,
            }.Run();

            currentSearchId = _bfsResult[0];
            bool isFloating = _bfsResult[1] == 1;
            islandPixelCount = _bfsResult[2];
            islandMinX = _bfsResult[3];
            islandMaxX = _bfsResult[4];
            islandMinY = _bfsResult[5];
            islandMaxY = _bfsResult[6];

            return isFloating;
        }

        GameObject ExtractAndSpawnIsland()
        {
            if (islandPixelCount == 0) return null;

            int evaporateThreshold = Mathf.Max(minimumPhysicsPixels, 36);
            if (islandPixelCount < evaporateThreshold)
            {
                for (int i = 0; i < islandPixelCount; i++)
                {
                    int idx = islandPixelBuffer[i];
                    rawPixels[idx] = clearColor;
                    solidMap[idx] = 0;
                }
                if (currentSolidPixels > 0) currentSolidPixels -= islandPixelCount;
                BatchDirtyChunks(islandMinX, islandMaxX, islandMinY, islandMaxY);
                return null;
            }

            int width = islandMaxX - islandMinX + 1;
            int height = islandMaxY - islandMinY + 1;
            int totalNewPixels = width * height;

            // dequeue / create the pool slot first so we can access its
            // _pooledTexture before allocating the falling chunk's Texture2D below.
            // Position/SetActive/sprite assignment still happens later (after the
            // ExtractTransformReleaseJob populates the pixel buffer) — the dequeue
            // is just a Queue.Dequeue() on an inactive GameObject, no side effects.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_AllocSrc.Begin();
#endif
            GameObject fallingObj;
            if (islandPool.Count > 0)
            {
                fallingObj = islandPool.Dequeue();
            }
            else
            {
                fallingObj = new GameObject("FallingChunk");
                fallingObj.transform.SetParent(ChunkParent); // nest under the mainland
                fallingObj.layer = gameObject.layer;
                fallingObj.AddComponent<SpriteRenderer>();
                fallingObj.AddComponent<Rigidbody2D>();
                fallingObj.AddComponent<DestructibleTerrain>();
                fallingObj.AddComponent<BoxCollider2D>().enabled = false;
            }
            DestructibleTerrain dt = fallingObj.GetComponent<DestructibleTerrain>();

            // reuse pooled Texture2D via Reinitialize. Skips the
            // cost of allocating a new Texture2D when this pool slot's previous
            // tenant left its texture behind in StampAndDestroy.
            Texture2D fallingTex;
            if (dt._pooledTexture != null)
            {
                fallingTex = dt._pooledTexture;
                dt._pooledTexture = null;
                // Reinitialize resizes the existing GPU buffer to width×height.
                // The pixel contents are uninitialized after this, but Extract-
                // TransformReleaseJob immediately overwrites every pixel via
                // GetRawTextureData below, so it doesn't matter.
                fallingTex.Reinitialize(width, height, TextureFormat.RGBA32, false);
            }
            else
            {
                fallingTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            fallingTex.filterMode = tex.filterMode;
            NativeArray<Color32> newPixels = fallingTex.GetRawTextureData<Color32>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_AllocSrc.End();
#endif

            // PREPARE FOR FAINT PIXEL SWEEP
            byte alphaCutoff = (byte)Mathf.Clamp(alphaThreshold * 255f, 1f, 255f);

            // UNIFIED BURST EXTRACTION
            // rawPixels is canonical in both editor and release (single Burst ExtractTransformReleaseJob).
            int totalChunksLogical = chunksX * chunksY;
            if (!_dirtyChunkBitset.IsCreated || _dirtyChunkBitset.Length < totalChunksLogical)
            {
                if (_dirtyChunkBitset.IsCreated) _dirtyChunkBitset.Dispose();
                _dirtyChunkBitset = new NativeArray<byte>(Mathf.Max(totalChunksLogical, 1024), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            else
            {
                new ClearByteArrayJob { arr = _dirtyChunkBitset, length = totalChunksLogical }.Run();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_PixelTransform.Begin();
#endif
            new ExtractTransformReleaseJob
            {
                islandPixelBuffer = islandPixelBuffer,
                solidMap = solidMap,
                dirtyChunkBitset = _dirtyChunkBitset,
                rawPixels = rawPixels,
                newPixels = newPixels,
                clearColor = clearColor,
                islandPixelCount = islandPixelCount,
                texWidth = texWidth,
                islandMinX = islandMinX,
                islandMinY = islandMinY,
                width = width,
                chunkSize = chunkSize,
                chunksX = chunksX,
            }.Run();

            for (int ci = 0; ci < totalChunksLogical; ci++)
            {
                if (_dirtyChunkBitset[ci] != 0)
                {
                    FlagChunkDirty(ci);
                    chunkSignatures[ci] = -1; // Sentinel: known dirty, skip hash recompute
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_PixelTransform.End();
#endif

            // GhostSweep cleans faint pixels (0 < alpha < alphaCutoff)
            // adjacent to the extracted region. Necessary for sprites with
            // continuous alpha gradients to avoid ghost artifacts at the
            // extraction boundary. Has a per-spawn cost (iterates the island
            // AABB). For sprites with hard alpha edges (pixels are fully opaque
            // or fully transparent — typical for pixel art / Worms-style
            // terrain), this sweep is a no-op and pure cost. Toggle off in
            // Inspector to skip.
            if (enableGhostSweep)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmExtract_GhostSweep.Begin();
#endif
                new GhostSweepJob
                {
                    solidMap = solidMap,
                    pixels = rawPixels,
                    islandMinX = islandMinX,
                    islandMaxX = islandMaxX,
                    islandMinY = islandMinY,
                    islandMaxY = islandMaxY,
                    texWidth = texWidth,
                    texHeight = texHeight,
                    alphaCutoff = alphaCutoff,
                    clearColor = clearColor,
                }.Run();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_pmExtract_GhostSweep.End();
#endif
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_SetPixelData.Begin();
#endif
            fallingTex.Apply(false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_SetPixelData.End();
            s_pmExtract_SpawnInstance.Begin();
#endif

            if (currentSolidPixels > 0) currentSolidPixels -= islandPixelCount;
            // Logical chunks were marked dirty per-pixel inside the extraction loop —
            // only chunks that actually contain island pixels are flagged. Just need
            // to dirty the visual chunks for the AABB (covers ghost-sweep changes too).
            BatchDirtyVisualChunksRect(islandMinX, islandMaxX, islandMinY, islandMaxY);

            // DEFERRED PHYSICS ACTIVATION
            // ALWAYS defer (was IsMainland-only). Falling-chunk-sourced
            // sub-island spawns also use multi-chunk grids and need the same
            // gating until their own chunks are rebuilt. The pp.check loop in
            // LateUpdate waits for both the spawner's spawn-area chunks AND the
            // new chunk's own chunks to be clean before activating physics.
            int spawnRcx0 = 0, spawnRcx1 = 0, spawnRcy0 = 0, spawnRcy1 = 0;
            spawnRcx0 = Mathf.Clamp(islandMinX / chunkSize, 0, chunksX - 1);
            spawnRcx1 = Mathf.Clamp(islandMaxX / chunkSize, 0, chunksX - 1);
            spawnRcy0 = Mathf.Clamp(islandMinY / chunkSize, 0, chunksY - 1);
            spawnRcy1 = Mathf.Clamp(islandMaxY / chunkSize, 0, chunksY - 1);

            Vector2 pivotNorm = spriteRend.sprite.pivot / new Vector2(texWidth, texHeight);
            Vector2 spriteWorldSize = new Vector2(texWidth, texHeight) / ppu;
            Vector2 localBL = -Vector2.Scale(spriteWorldSize, pivotNorm);
            Vector2 chunkLocalOffset = localBL + new Vector2(islandMinX, islandMinY) / ppu;

            fallingObj.transform.position = transform.TransformPoint(chunkLocalOffset);
            fallingObj.transform.rotation = transform.rotation;
            // localScale must yield WORLD scale == the spawner's lossyScale now that the chunk
            // is parented under the (possibly scaled) mainland — divide out the parent scale.
            Vector3 ws = transform.lossyScale, ps = fallingObj.transform.parent != null ? fallingObj.transform.parent.lossyScale : Vector3.one;
            fallingObj.transform.localScale = new Vector3(ws.x / ps.x, ws.y / ps.y, ws.z / ps.z);

            // Activate the object BEFORE assigning the sprite! 
            // This forces Awake() to run, hit the null sprite check, and safely abort 
            // so it doesn't accidentally run the giant Mainland initialization logic!
            fallingObj.SetActive(true);

            SpriteRenderer sr = fallingObj.GetComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(fallingTex, new Rect(0, 0, width, height), new Vector2(0f, 0f), ppu, 0, SpriteMeshType.FullRect);

            // dt was acquired earlier (above the texture alloc).

            // 1. copy the parent config onto the falling chunk
            dt.ApplyConfig(this.config);

            // 2. Override ONLY the 2 variables that MUST be different for falling debris
            dt.groundAnchorY = 0;
            dt.isMerging = false;

            // Now that Awake() is safely out of the way, we can manually initialize the chunk!
            // defer the heavy Init work (ClearByteArrayJob +
            // PopulateFallingSolidMapJob + CreateChunks + chunk-dirty-flagging)
            // to dt's next LateUpdate via ProcessPendingInit. Snapshot
            // islandPixelBuffer (now holding LOCAL indices post-extraction)
            // because the static buffer is overwritten by next frame's BFS.
            // BoxCollider2D state still set in frame 0 (cheap, doesn't depend on
            // Init). dt.tex set so dt's frame-0 LateUpdate ApplyVisuals has a
            // valid texture to call Apply on. dt.rb fields (mass, gravity,
            // kinematic, velocity) set below as before — independent of Init.
            if (islandPixelCount < 100)
            {
                BoxCollider2D box = fallingObj.GetComponent<BoxCollider2D>();
                box.size = new Vector2(width, height) / ppu;
                box.offset = box.size / 2f;
                box.enabled = true;
            }
            else
            {
                BoxCollider2D box = fallingObj.GetComponent<BoxCollider2D>();
                if (box != null) box.enabled = false;
            }

            // stage the Init work for next LateUpdate.
            if (dt._pendingInitSnapshot.IsCreated) dt._pendingInitSnapshot.Dispose();
            dt._pendingInitSnapshot = new NativeArray<int>(islandPixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeArray<int>.Copy(islandPixelBuffer, 0, dt._pendingInitSnapshot, 0, islandPixelCount);
            dt._pendingInitPixelCount = islandPixelCount;
            dt._pendingInitWidth = width;
            dt._pendingInitHeight = height;
            dt._pendingInitIsBoxDebris = (islandPixelCount < 100);
            dt._hasPendingInit = true;
            // Init normally sets dt.tex from spriteRend.sprite.texture; do it now
            // so any frame-0 ApplyVisuals call on dt has a non-null texture.
            dt.tex = fallingTex;

            dt.rb.mass = islandPixelCount * massPerPixel;
            dt.rb.gravityScale = Mathf.Clamp(islandPixelCount * gravityPerPixel, minChunkGravity, maxChunkGravity);

            // Ensure the falling chunk is immediately dynamic so it falls!
            dt.rb.bodyType = RigidbodyType2D.Dynamic;

            if (this.rb != null)
            {
                dt.rb.linearVelocity = this.rb.linearVelocity;
                dt.rb.angularVelocity = this.rb.angularVelocity;
            }

            // DEFERRED PHYSICS ACTIVATION
            // ALWAYS defer. Mainland-sourced spawns wait for mainland's
            // spawn-area chunks to be rebuilt; falling-chunk-sourced sub-islands
            // wait for the spawner's chunks. In both cases the new chunk's OWN
            // chunks (multi-chunk grid) are also gated by the pp.check extension.
            // safetyFrames=30 caps wait time at ~0.5s in case rebuilds are blocked.
            dt.rb.simulated = false;
            pendingPhysicsActivations.Add(new PendingPhysicsActivation
            {
                dt = dt,
                rcx0 = spawnRcx0,
                rcx1 = spawnRcx1,
                rcy0 = spawnRcy0,
                rcy1 = spawnRcy1,
                safetyFrames = 30
            });

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmExtract_SpawnInstance.End();
#endif
            return fallingObj;
        }

        #endregion

        #region Marching Squares & Visuals
        void CreateChunks()
        {
            float worldSize = chunkSize / ppu;
            Vector2 spriteWorldSize = new Vector2(texW, texH) / ppu;
            Vector2 pivotNorm = spriteRend.sprite.pivot / spriteRend.sprite.rect.size;
            Vector2 localBL = -Vector2.Scale(spriteWorldSize, pivotNorm);

            // CHUNK RECYCLING
            // ZERO-GC ARRAY RECYCLING
            int newSize = chunksX * chunksY;
            GameObject[] oldChunks = chunkObjects;

            // Only allocate new memory if the pooled arrays are too small!
            if (chunkObjects == null || chunkObjects.Length < newSize ||
                isChunkDirty == null || isChunkDirty.Length < newSize ||
                chunkSignatures == null || chunkSignatures.Length < newSize)
            { // <-- Added bounds check!

                chunkObjects = new GameObject[newSize];
                chunkColliders = new PolygonCollider2D[newSize];
                isChunkDirty = new bool[newSize];
                chunkSignatures = new int[newSize];
                                                    // per-path signatures. Sized by MAX_PATHS_PER_CHUNK.
                chunkPathSignatures = new int[newSize * MAX_PATHS_PER_CHUNK];
                chunkPathCounts = new int[newSize];
            }
            else
            {
                // If they are big enough, just wipe them clean!
                System.Array.Clear(isChunkDirty, 0, newSize);
                System.Array.Clear(chunkSignatures, 0, newSize);
                // ensure collider cache has room (parallel to chunkObjects).
                if (chunkColliders == null || chunkColliders.Length < newSize)
                {
                    chunkColliders = new PolygonCollider2D[newSize];
                }
                else
                {
                    System.Array.Clear(chunkColliders, 0, newSize);
                }
                // also wipe path signatures so a reused chunk re-uploads.
                if (chunkPathSignatures == null || chunkPathSignatures.Length < newSize * MAX_PATHS_PER_CHUNK)
                {
                    chunkPathSignatures = new int[newSize * MAX_PATHS_PER_CHUNK];
                }
                else
                {
                    System.Array.Clear(chunkPathSignatures, 0, newSize * MAX_PATHS_PER_CHUNK);
                }
                if (chunkPathCounts == null || chunkPathCounts.Length < newSize)
                {
                    chunkPathCounts = new int[newSize];
                }
                else
                {
                    System.Array.Clear(chunkPathCounts, 0, newSize);
                }
            }

            for (int cy = 0; cy < chunksY; cy++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    int idx = cx + cy * chunksX;

                    GameObject go = null;
                    PolygonCollider2D pc = null;

                    // RECYCLE existing chunks instead of calling AddComponent!
                    if (oldChunks != null && idx < oldChunks.Length && oldChunks[idx] != null)
                    {
                        go = oldChunks[idx];
                        pc = go.GetComponent<PolygonCollider2D>();

                        if (pc != null) pc.enabled = true;

                        // Safely update the owner reference for recycled chunks
                        if (go.TryGetComponent(out ChunkRef cr)) cr.owner = this;
                    }
                    else
                    {
                        // Only allocate if we actually need MORE chunks than before
                        go = new GameObject($"chunk_{cx}_{cy}");
                        go.transform.SetParent(transform, false);
                        // Inherit tag + layer from the parent terrain so consumers
                        // can filter chunks (e.g. collision masks, raycast layers,
                        // CompareTag) by configuring the root GameObject. Default
                        // is "Untagged" / Default layer \u2014 no scene setup required.
                        go.tag = gameObject.tag;
                        go.layer = gameObject.layer;

                        pc = go.AddComponent<PolygonCollider2D>();

                        // Attach the reference component safely AFTER the GameObject exists
                        go.AddComponent<ChunkRef>().owner = this;
                    }

                    Vector2 chunkOffset = new Vector2(cx * worldSize, cy * worldSize);
                    go.transform.localPosition = localBL + chunkOffset;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector2.one;

                    pc.pathCount = 0; // Wipe the old collision data clean
                    go.SetActive(true);

                    chunkObjects[idx] = go;
                    chunkColliders[idx] = pc; // cache for RebuildChunk.
                }
            }

            // Hide any leftover chunks from the old array that we didn't end up using
            // Hide any leftover chunks from the previous time this pool object was used
            if (oldChunks != null)
            {
                for (int i = 0; i < oldChunks.Length; i++)
                {
                    // If this old chunk isn't being actively used in the new grid, turn it off!
                    if (i >= newSize && oldChunks[i] != null)
                    {
                        oldChunks[i].SetActive(false);
                    }
                }
            }
        }

        void RebuildAllChunks()
        {
            for (int cy = 0; cy < chunksY; cy++)
                for (int cx = 0; cx < chunksX; cx++) RebuildChunk(cx, cy);
        }

        void BatchDirtyChunks(int minX, int maxX, int minY, int maxY)
        {
            int cx0 = Mathf.Clamp(minX / chunkSize, 0, chunksX - 1);
            int cx1 = Mathf.Clamp(maxX / chunkSize, 0, chunksX - 1);
            int cy0 = Mathf.Clamp(minY / chunkSize, 0, chunksY - 1);
            int cy1 = Mathf.Clamp(maxY / chunkSize, 0, chunksY - 1);

            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    FlagChunkDirty(cx + cy * chunksX);
                }
            }

            BatchDirtyVisualChunksRect(minX, maxX, minY, maxY);
        }

        // Visual-only batch dirty. Used by ExtractAndSpawnIsland which marks logical
        // chunks per-pixel (precise) and only needs AABB-based marking for visuals.
        void BatchDirtyVisualChunksRect(int minX, int maxX, int minY, int maxY)
        {
            if (visualChunks == null)
            {
                // falling chunk path (no sub-chunks). Signal
                // ApplyVisuals to fire a single tex.Apply next LateUpdate.
                // Covers ExtractAndSpawnIsland's modifications to a falling-chunk
                // source (sub-island extracted from a falling chunk) and any
                // future caller that batches a dirty rect on a non-mainland.
                _singleTextureDirty = true;
                return;
            }

            int vx0 = Mathf.Clamp(minX / visualChunkSize, 0, visualChunksX - 1);
            int vx1 = Mathf.Clamp(maxX / visualChunkSize, 0, visualChunksX - 1);
            int vy0 = Mathf.Clamp(minY / visualChunkSize, 0, visualChunksY - 1);
            int vy1 = Mathf.Clamp(maxY / visualChunkSize, 0, visualChunksY - 1);

            for (int vy = vy0; vy <= vy1; vy++)
            {
                for (int vx = vx0; vx <= vx1; vx++)
                {
                    int vIdx = vx + vy * visualChunksX;
                    FlagVisualDirty(vIdx);
                    var vc = visualChunks[vIdx];
                    int lx0 = Mathf.Clamp(minX - vc.startX, 0, vc.width - 1);
                    int lx1 = Mathf.Clamp(maxX - vc.startX, 0, vc.width - 1);
                    int ly0 = Mathf.Clamp(minY - vc.startY, 0, vc.height - 1);
                    int ly1 = Mathf.Clamp(maxY - vc.startY, 0, vc.height - 1);
                    if (lx0 < visualChunks[vIdx].minDirtyX) visualChunks[vIdx].minDirtyX = lx0;
                    if (lx1 > visualChunks[vIdx].maxDirtyX) visualChunks[vIdx].maxDirtyX = lx1;
                    if (ly0 < visualChunks[vIdx].minDirtyY) visualChunks[vIdx].minDirtyY = ly0;
                    if (ly1 > visualChunks[vIdx].maxDirtyY) visualChunks[vIdx].maxDirtyY = ly1;
                }
            }
        }

        void PopulateIsolatedPadBuffer(int cx, int cy, int step)
        {
            int scaledChunkSize = chunkSize / step;
            int stride = scaledChunkSize + 2;
            int baseX = cx * chunkSize;
            int baseY = cy * chunkSize;
            int padLen = stride * stride;

            // BURST PAD POPULATE
            // Burst-compiled job fills padBufferNative; downstream stages all read
            // padBufferNative directly. Managed padBuffer was eliminated.
            new PopulateIsolatedPadBufferJob
            {
                solidMap = solidMap,
                padBuffer = padBufferNative,
                padLen = padLen,
                scaledChunkSize = scaledChunkSize,
                stride = stride,
                baseX = baseX,
                baseY = baseY,
                step = step,
                texWidth = texWidth,
                texHeight = texHeight,
            }.Run();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        int ComputeChunkSignature(int cx, int cy)
        {
            // Burst-compiled. Was 64ms across 17 chunks because the
            // `solidMap[rowBase + gx]` read incurred AtomicSafetyHandle checks per
            // access. Burst strips them; runs in <1ms total.
            if (!_sigResult.IsCreated)
            {
                _sigResult = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            var sigJob = new ComputeChunkSignatureJob
            {
                solidMap = solidMap,
                result = _sigResult,
                baseX = cx * chunkSize,
                baseY = cy * chunkSize,
                chunkSize = chunkSize,
                texWidth = texWidth,
                texHeight = texHeight,
            };
            sigJob.Run();
            return _sigResult[0];
        }

        // Apply parallel rebuild result to PolygonCollider2D.
        // Reads per-slot simplified contours from the parallel job's output buffers
        // (simplVertsBuffersB / simplSpansBuffersB sliced by slot) and runs the same
        // path filter + sort + per-path hash skip + pc.SetPath logic that the
        // sequential RebuildChunk does. Box2D fixture API isn't thread-safe, so this
        // runs serially on the main thread after the parallel job completes.
        // Mirrors the bottom half of RebuildChunk verbatim — only the data sources
        // differ (per-slot buffers instead of the global NativeLists).
        void ApplyParallelRebuildResult(int slot, int idx)
        {
            var pc = chunkColliders[idx];

            int rawSpanCount = simplSpanCountsB[slot];
            int spanBase = slot * REBUILD_SPAN_CAP;
            int vertBase = slot * REBUILD_VERT_CAP;

            // Pass 1: compute |signed area| per span, collect valid path indices.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_AreaFilter.Begin();
#endif
            if (_pathFilterArea == null || _pathFilterArea.Length < rawSpanCount)
            {
                _pathFilterArea = new float[Mathf.Max(rawSpanCount, 64)];
                _pathFilterIdx = new int[Mathf.Max(rawSpanCount, 64)];
            }
            float minAreaWorld = minContourAreaPxSqr / (ppu * ppu);
            int validCount = 0;
            for (int i = 0; i < rawSpanCount; i++)
            {
                int2 span = simplSpansBuffersB[spanBase + i];
                int sStart = span.x;
                int sLen = span.y;
                if (sLen < 3) continue;
                float area2 = 0f;
                int last = sStart + sLen - 1;
                Vector2 prev = simplVertsBuffersB[vertBase + last];
                for (int v = sStart; v <= last; v++)
                {
                    Vector2 cur = simplVertsBuffersB[vertBase + v];
                    area2 += (prev.x * cur.y) - (cur.x * prev.y);
                    prev = cur;
                }
                float absArea = 0.5f * (area2 < 0f ? -area2 : area2);
                if (absArea < minAreaWorld) continue;
                _pathFilterArea[validCount] = absArea;
                _pathFilterIdx[validCount] = i;
                validCount++;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_AreaFilter.End();
#endif

            // Cap to MAX_PATHS_PER_CHUNK by partial selection sort (largest first).
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_Sort.Begin();
#endif
            int finalCount = validCount;
            if (validCount > MAX_PATHS_PER_CHUNK)
            {
                for (int i = 0; i < MAX_PATHS_PER_CHUNK; i++)
                {
                    int maxJ = i;
                    float maxA = _pathFilterArea[i];
                    for (int j = i + 1; j < validCount; j++)
                    {
                        if (_pathFilterArea[j] > maxA) { maxA = _pathFilterArea[j]; maxJ = j; }
                    }
                    if (maxJ != i)
                    {
                        float ta = _pathFilterArea[i]; _pathFilterArea[i] = _pathFilterArea[maxJ]; _pathFilterArea[maxJ] = ta;
                        int ti = _pathFilterIdx[i]; _pathFilterIdx[i] = _pathFilterIdx[maxJ]; _pathFilterIdx[maxJ] = ti;
                    }
                }
                finalCount = MAX_PATHS_PER_CHUNK;
            }

            // only INCREASE pc.pathCount, never decrease (decreasing
            // triggers a Box2D fixture array resize).
            int currentPC = pc.pathCount;
            if (finalCount > currentPC)
            {
                pc.pathCount = finalCount;
                currentPC = finalCount;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_Sort.End();
#endif

            // per-path FNV hash to skip unchanged uploads.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_SetPath.Begin();
#endif
            int pathSigBase = idx * MAX_PATHS_PER_CHUNK;
            int prevPathCount = chunkPathCounts[idx];

            for (int i = 0; i < finalCount; i++)
            {
                int srcSpanIdx = _pathFilterIdx[i];
                int2 span = simplSpansBuffersB[spanBase + srcSpanIdx];
                int sStart = span.x;
                int sLen = span.y;

                int hash = unchecked((int)2166136261u);
                int last = sStart + sLen - 1;
                for (int v = sStart; v <= last; v++)
                {
                    Vector2 p = simplVertsBuffersB[vertBase + v];
                    hash = (hash ^ Unity.Mathematics.math.asint(p.x)) * 16777619;
                    hash = (hash ^ Unity.Mathematics.math.asint(p.y)) * 16777619;
                }
                hash ^= sLen;
                if (hash == 0) hash = 1;

                if (i < prevPathCount && chunkPathSignatures[pathSigBase + i] == hash)
                {
                    continue;
                }

                _setPathList.Clear();
                if (_setPathList.Capacity < sLen) _setPathList.Capacity = sLen;
                for (int v = 0; v < sLen; v++) _setPathList.Add(simplVertsBuffersB[vertBase + sStart + v]);
                pc.SetPath(i, _setPathList);
                chunkPathSignatures[pathSigBase + i] = hash;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_SetPath.End();
#endif

            // deactivate trailing paths via SetPath(i, empty).
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_Deactivate.Begin();
#endif
            for (int i = finalCount; i < currentPC; i++)
            {
                if (chunkPathSignatures[pathSigBase + i] == 0) continue;
                _setPathList.Clear();
                pc.SetPath(i, _setPathList);
                chunkPathSignatures[pathSigBase + i] = 0;
            }

            chunkPathCounts[idx] = finalCount;

            bool wantEnabled = finalCount > 0;
            if (pc.enabled != wantEnabled) pc.enabled = wantEnabled;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_pmApply_Deactivate.End();
#endif
        }

        bool RebuildChunk(int cx, int cy)
        {
            return RebuildChunk(cx, cy, physicsStepForRebuild);
        }

        bool RebuildChunk(int cx, int cy, int step)
        {
            int idx = cx + cy * chunksX;

            // If the sentinel (-1) is present, we skip the hash completely.
            if (chunkSignatures[idx] != -1)
            {
                int sigHash = ComputeChunkSignature(cx, cy);
                if (chunkSignatures[idx] == sigHash) return false;
                chunkSignatures[idx] = sigHash;
            }
            else
            {
                chunkSignatures[idx] = ComputeChunkSignature(cx, cy);
            }

            PopulateIsolatedPadBuffer(cx, cy, step);
            BuildEdges(null, 1f / ppu, step);

            // Configure TraceContours to emit vertices in world (parent-local) space directly.
            // Was: emit padded scaled-pixel, then a 2nd loop unpads + multiplies by step,
            // then a 3rd loop multiplies by invPpu. All folded into the unpack here.
            float invPpu = 1f / ppu;
            float pixToWorld = step * invPpu;
            traceWorldScale = 0.5f * pixToWorld;
            traceWorldOffset = -pixToWorld;

            TraceContours();  // populates contoursVertsNative + contourSpansNative

            // RDP runs in world space — scale tolerance from pixels to world units.
            // mainland tolerance is now Inspector-tunable
            // (mainlandSimplifyTolerance) instead of a hardcoded 0.3f. Default
            // raised to 0.5 — Box2D concave decomposition inside pc.SetPath is
            // roughly O(vertCount²), so a ~30% vertex reduction cuts the dominant
            // RebuildChunk cost (was 0.88ms/path → expected ~0.4ms/path).
            float baseTol = IsMainland ? mainlandSimplifyTolerance : simplifyTolerance;
            float worldTol = baseTol * pixToWorld;

            // BURST RDP SIMPLIFICATION
            // Reads contoursVertsNative (raw contours), writes simplifiedVertsNative
            // (simplified). Filters short contours (<3 verts) inside the job.
            new RDPSimplifyJob
            {
                srcVerts = contoursVertsNative.AsArray(),
                srcSpans = contourSpansNative.AsArray(),
                dstVerts = simplifiedVertsNative,
                dstSpans = simplifiedSpansNative,
                keepFlags = rdpKeepFlagsNative,
                rdpStack = rdpStackNative,
                rdpScratch = rdpScratchNative,
                worldTol = worldTol,
            }.Run();

            var pc = chunkColliders[idx]; // cached, no GetComponent.

            // filter tiny contour paths. With heavy fragmentation a chunk
            // can produce 100+ contours, and each pc.SetPath is a Box2D fixture
            // rebuild. Filtering keeps only contours with signed-area magnitude
            // above MIN_CONTOUR_AREA_PX_SQR (in squared pixels), capped at
            // MAX_PATHS_PER_CHUNK by largest area first.
            // Pass 1: compute area per span, count valid paths, sort indices by
            // area descending (only when over the cap).
            int rawSpanCount = simplifiedSpansNative.Length;
            if (_pathFilterArea == null || _pathFilterArea.Length < rawSpanCount)
            {
                _pathFilterArea = new float[Mathf.Max(rawSpanCount, 64)];
                _pathFilterIdx = new int[Mathf.Max(rawSpanCount, 64)];
            }
            // Convert pixel-area threshold to world-area (verts are world-space).
            float minAreaWorld = minContourAreaPxSqr / (ppu * ppu);
            int validCount = 0;
            for (int i = 0; i < rawSpanCount; i++)
            {
                int2 span = simplifiedSpansNative[i];
                int sStart = span.x;
                int sLen = span.y;
                if (sLen < 3) continue; // degenerate
                                        // Compute |signed area| via shoelace.
                float area2 = 0f;
                int last = sStart + sLen - 1;
                Vector2 prev = simplifiedVertsNative[last];
                for (int v = sStart; v <= last; v++)
                {
                    Vector2 cur = simplifiedVertsNative[v];
                    area2 += (prev.x * cur.y) - (cur.x * prev.y);
                    prev = cur;
                }
                float absArea = 0.5f * (area2 < 0f ? -area2 : area2);
                if (absArea < minAreaWorld) continue;
                _pathFilterArea[validCount] = absArea;
                _pathFilterIdx[validCount] = i;
                validCount++;
            }

            // Cap: if over MAX_PATHS_PER_CHUNK, keep the N largest. Insertion-sort
            // selection is fine — typical case validCount is small (~1-10), and
            // cap activation is rare.
            int finalCount = validCount;
            if (validCount > MAX_PATHS_PER_CHUNK)
            {
                // Partial sort: bubble the N largest to the front.
                for (int i = 0; i < MAX_PATHS_PER_CHUNK; i++)
                {
                    int maxJ = i;
                    float maxA = _pathFilterArea[i];
                    for (int j = i + 1; j < validCount; j++)
                    {
                        if (_pathFilterArea[j] > maxA) { maxA = _pathFilterArea[j]; maxJ = j; }
                    }
                    if (maxJ != i)
                    {
                        float ta = _pathFilterArea[i]; _pathFilterArea[i] = _pathFilterArea[maxJ]; _pathFilterArea[maxJ] = ta;
                        int ti = _pathFilterIdx[i]; _pathFilterIdx[i] = _pathFilterIdx[maxJ]; _pathFilterIdx[maxJ] = ti;
                    }
                }
                finalCount = MAX_PATHS_PER_CHUNK;
            }

            // pc.pathCount setter is costly to change (Box2D fixture
            // array resize). It fires whenever finalCount differs from the
            // previous rebuild — a path crossing the area threshold or a
            // fragmentation/merge event flips this often. Strategy: only INCREASE
            // pc.pathCount, never decrease. For paths beyond finalCount that
            // were previously active, SetPath with an empty list to deactivate
            // them without resizing the underlying array. After steady state
            // a chunk's pc.pathCount stabilizes at the largest value it has
            // ever needed; subsequent rebuilds skip the setter entirely.
            int currentPC = pc.pathCount;
            if (finalCount > currentPC)
            {
                pc.pathCount = finalCount;
                currentPC = finalCount;
            }

            // per-path signature check. Most rebuilds only change ONE
            // path (the one near the dig). pc.SetPath is the costly part and
            // dominates RebuildChunk now. Skip SetPath for paths whose vertex
            // hash matches what we last uploaded.
            int pathSigBase = idx * MAX_PATHS_PER_CHUNK;
            int prevPathCount = chunkPathCounts[idx];

            for (int i = 0; i < finalCount; i++)
            {
                int srcSpanIdx = _pathFilterIdx[i];
                int2 span = simplifiedSpansNative[srcSpanIdx];
                int sStart = span.x;
                int sLen = span.y;

                // Compute hash of this path's vertex sequence. FNV-1a-style mix
                // on the int-bit pattern of each float coord.
                int hash = unchecked((int)2166136261u);
                int last = sStart + sLen - 1;
                for (int v = sStart; v <= last; v++)
                {
                    Vector2 p = simplifiedVertsNative[v];
                    hash = (hash ^ Unity.Mathematics.math.asint(p.x)) * 16777619;
                    hash = (hash ^ Unity.Mathematics.math.asint(p.y)) * 16777619;
                }
                // Mix sLen so paths of different length but same prefix differ.
                hash ^= sLen;
                // Avoid 0 sentinel collision.
                if (hash == 0) hash = 1;

                // Skip SetPath if this slot already holds an identical path.
                if (i < prevPathCount && chunkPathSignatures[pathSigBase + i] == hash)
                {
                    continue;
                }

                _setPathList.Clear();
                if (_setPathList.Capacity < sLen) _setPathList.Capacity = sLen;
                for (int v = 0; v < sLen; v++) _setPathList.Add(simplifiedVertsNative[sStart + v]);
                pc.SetPath(i, _setPathList);
                chunkPathSignatures[pathSigBase + i] = hash;
            }

            // deactivate unused trailing paths via SetPath(i, empty).
            // This is much cheaper than decreasing pc.pathCount.
            // skip the empty-set if this slot is already empty
            // (signature 0 = never-set or already-cleared).
            for (int i = finalCount; i < currentPC; i++)
            {
                if (chunkPathSignatures[pathSigBase + i] == 0) continue; // already empty
                _setPathList.Clear();
                pc.SetPath(i, _setPathList);
                chunkPathSignatures[pathSigBase + i] = 0;
            }

            chunkPathCounts[idx] = finalCount;

            // Match enabled state to whether we actually have geometry. Only flip if
            // it's currently wrong, so we avoid the destroy/recreate work in the steady case.
            bool wantEnabled = finalCount > 0;
            if (pc.enabled != wantEnabled) pc.enabled = wantEnabled;

            return true;
        }

        // FAST PACK VERTEX
        // Forces the compiler to inline the math, bypassing method overhead entirely!
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        int PackVertex(Vector2 v)
        {
            // Raw casting + 0.5f is functionally identical to RoundToInt but 10x faster
            int x = (int)(v.x * 2f + 0.5f);
            int y = (int)(v.y * 2f + 0.5f);

            return y * packStride + x;
        }
        private void FlagChunkDirty(int idx)
        {
            // Silently abort if the arrays aren't allocated or the index is completely out of bounds.
            if (isChunkDirty == null || idx < 0 || idx >= isChunkDirty.Length) return;

            if (!isChunkDirty[idx])
            {
                isChunkDirty[idx] = true;
                dirtyChunksList.Add(idx);
            }
        }
        private void FlagVisualDirty(int vIdx)
        {
            if (!isVisualDirty[vIdx])
            {
                isVisualDirty[vIdx] = true;
                visualChunks[vIdx].isDirty = true;
                dirtyVisualsList.Add(vIdx);
            }
        }

        void BuildEdges(byte[] grid, float cellSize, int step)
        { // grid param retained for caller compat
            int scaledChunkSize = chunkSize / step;
            int stride = scaledChunkSize + 2;

            // BURST EDGES
            // Burst job populates edgesNative from padBufferNative. After the job,
            // sync edgesNative -> managed edges list because TraceContours still
            // reads the managed list. Bulk index copy is
            // negligible at worst-case 16k edges, irrelevant compared to the savings.
            new BuildEdgesJob
            {
                padBuffer = padBufferNative,
                caseLineCount = caseLineCount,
                caseLineData = caseLineData,
                edges = edgesNative,
                stride = stride,
                packStride = packStride,
            }.Run();
            // TraceContours reads edgesNative directly; the managed `edges` list is unused.
        }
        void TraceContours()
        {
            // BURST TRACE CONTOURS
            // Burst job walks the chain in nextNode and emits world-space vertices
            // into contoursVertsNative + contourSpansNative (CSR layout); read directly
            // via RDPSimplifyJob — no managed hydration needed.
            new TraceContoursJob
            {
                edges = edgesNative.AsArray(),
                edgeCount = edgesNative.Length,
                nextNode = nextNodeNative,
                vertexVisited = vertexVisitedNative,
                usedNodes = usedNodesNative,
                contoursVerts = contoursVertsNative,
                contourSpans = contourSpansNative,
                result = _traceResultNative,
                prevUsedNodesCount = usedNodesCount,
                packStride = packStride,
                traceWorldScale = traceWorldScale,
                traceWorldOffset = traceWorldOffset,
            }.Run();
            usedNodesCount = _traceResultNative[0];
        }

        void InitCaseLookup()
        {
            if (caseLookupInitialized) return;  // <-- Skip if already done globally!
            caseLookupInitialized = true;

            caseToLines[1] = new[] { new Vector2(0.5f, 0), new Vector2(0, 0.5f) };
            caseToLines[2] = new[] { new Vector2(1, 0.5f), new Vector2(0.5f, 0) };
            caseToLines[4] = new[] { new Vector2(0.5f, 1), new Vector2(1, 0.5f) };
            caseToLines[8] = new[] { new Vector2(0, 0.5f), new Vector2(0.5f, 1) };

            caseToLines[3] = new[] { new Vector2(1, 0.5f), new Vector2(0, 0.5f) };
            caseToLines[6] = new[] { new Vector2(0.5f, 1), new Vector2(0.5f, 0) };
            caseToLines[12] = new[] { new Vector2(0, 0.5f), new Vector2(1, 0.5f) };
            caseToLines[9] = new[] { new Vector2(0.5f, 0), new Vector2(0.5f, 1) };

            caseToLines[7] = new[] { new Vector2(0.5f, 1), new Vector2(0, 0.5f) };
            caseToLines[11] = new[] { new Vector2(1, 0.5f), new Vector2(0.5f, 1) };
            caseToLines[13] = new[] { new Vector2(0.5f, 0), new Vector2(1, 0.5f) };
            caseToLines[14] = new[] { new Vector2(0, 0.5f), new Vector2(0.5f, 0) };

            caseToLines[5] = new[] { new Vector2(0.5f, 0), new Vector2(0, 0.5f), new Vector2(0.5f, 1), new Vector2(1, 0.5f) };
            caseToLines[10] = new[] { new Vector2(1, 0.5f), new Vector2(0.5f, 0), new Vector2(0, 0.5f), new Vector2(0.5f, 1) };

            // NATIVE FLAT TABLES
            // Mirror caseToLines into Burst-friendly NativeArrays.
            // Layout: 16 cases × 4 slots = 64 float2 entries; caseLineCount[c] tells
            // BuildEdges how many of those slots to read for case c.
            if (!caseLineCount.IsCreated)
            {
                caseLineCount = new NativeArray<int>(16, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                caseLineData = new NativeArray<float2>(64, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            for (int c = 0; c < 16; c++)
            {
                Vector2[] lines = caseToLines[c];
                int count = lines == null ? 0 : lines.Length;
                caseLineCount[c] = count;
                int baseIdx = c * 4;
                for (int i = 0; i < count; i++)
                {
                    caseLineData[baseIdx + i] = new float2(lines[i].x, lines[i].y);
                }
            }
        }

        private void EmitDebrisParticle(Vector3 worldPos, Color32 pixelColor, Vector2 impactVelocity)
        {
            if (debrisParticles == null) return;

            // Use the cached struct instead of allocating a new one!
            Vector2 pushBack = impactVelocity.normalized * -0.5f;
            sharedEmitParams.position = new Vector3(worldPos.x + pushBack.x, worldPos.y + pushBack.y, -1f);
            sharedEmitParams.startColor = pixelColor;

            Vector3 reverseSpray = (Vector3)(impactVelocity * -0.3f);
            Vector3 randomJitter = new Vector3(Random.Range(-3f, 3f), Random.Range(2f, 6f), 0);
            sharedEmitParams.velocity = reverseSpray + randomJitter;

            debrisParticles.Emit(sharedEmitParams, 1);
        }
        #endregion

    }

    public class ChunkRef : MonoBehaviour { public DestructibleTerrain owner; }
}
