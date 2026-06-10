using UnityEngine;

namespace slash_DTP
{
    // Resolution-aware settings preset. The values below are the 1080p baseline; when
    // applied to a terrain (DestructibleTerrain.ApplyPreset) the resolution-dependent
    // ones auto-scale to that terrain's texture size. Same-world model: a higher-res
    // texture is a sharper version of the SAME terrain, so per-pixel mass/gravity scale
    // DOWN with area and pixel-count limits scale UP — physics + real sizes stay constant.
    [CreateAssetMenu(menuName = "DTP/Terrain Preset", fileName = "DTP_Preset_1080p")]
    public class TerrainPreset : ScriptableObject
    {
        [Tooltip("Texture long side (px) at which these values apply 1:1. 1920 = 1080p; ratio = textureLongSide / this.")]
        public int referenceResolution = 1920;

        [Header("Chunk")]
        public int chunkSize = 128;              // scales x linear (pow2)
        public float alphaThreshold = 0.1f;
        public float simplifyTolerance = 1f;

        [Header("Destruction")]
        public float eraseRadius = 0.3f;
        public bool canDig = true;
        public int maxPhysicsPixels = 100000;    // scales x area
        public bool anchorToSideBorders = false;
        public bool anchorWholeBody = false;

        [Header("Visual FX")]
        public Color32 aftermathColor = new Color32(0, 0, 0, 255);
        public int aftermathThickness = 3;       // scales x linear
        [Range(0f, 1f)] public float particleSpawnChance = 0.05f;
        public bool enableDebrisParticles = false;

        [Header("Physics")]
        public int groundAnchorY = 0;
        public float massPerPixel = 1e-06f;      // scales / area
        public float gravityPerPixel = 0.0002f;  // scales / area
        public float minChunkGravity = 1f;
        public float maxChunkGravity = 5f;
        public bool enableIslandFalling = true;
        public bool enableImpactWelding = true;
        public bool destroyOffscreenChunks = false;
        public int minimumPhysicsPixels = 500;   // scales x area

        [Header("Impact & Welding")]
        public float minMergeVelocity = 5f;
        public float minEmbedVelocity = 5f;
        public float embedMultiplier = 0.05f;
        public int maxEmbedPixels = 20;          // scales x area
        public float mergeDuration = 0.25f;
        public float maxMergeAngle = 45f;
        public float smashVelocity = 15f;

        [Header("Visual Chunks / Performance")]
        public int visualChunkSize = 256;        // scales x linear (pow2)
        public int maxRebuildsPerFrame = 16;
        public float rebuildBudgetMs = 4f;
        public float visualsBudgetMs = 3f;
        public float mainlandSimplifyTolerance = 0.5f;
        public bool enableGhostSweep = true;
        public float sleepIdleSeconds = 3f;
        public float sleepWakeRadius = 5f;
        public int maxVisualsPerFrame = 8;
        public float minContourAreaPxSqr = 256f; // scales x area
    }
}
