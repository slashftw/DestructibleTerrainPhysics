# DestructibleTerrainPhysics

Per-pixel destructible 2D terrain for Unity, with optional falling-island physics. Dig into a sprite at runtime; detached chunks break off and fall under gravity. Burst + Jobs powered, with a touch/mouse demo that works out of the box.

![Per-pixel destructible terrain demo](docs/demo-4k.gif)

▶ **[7K terrain showcase](docs/demo-7k.mp4)** ·
**[5-minute mobile stress test](https://drive.google.com/file/d/1QOl-iay3GZ7aQixn3kAwuwNDkz6seYcX/view?usp=sharing)** — 7040×4608 at max settings on a Snapdragon 855 (6 GB) mobile device, ~30–40 FPS at ~500 MB RAM.

## Features

- **Per-pixel destruction** — erase circular regions from any sprite at runtime via a single `EraseCircle(worldPos, radius)` call.
- **Falling-island physics** — terrain disconnected from its anchor is detected (async BFS) and spawned as a gravity-simulated chunk; settled chunks weld back into the mainland.
- **Burst + Jobs** — the heavy per-pixel and contour work runs in Burst-compiled jobs off the main thread, with budget-throttled collider/visual rebuilds to clip frame spikes.
- **Works with mouse and touch** — the demo input layer reads mouse (editor/desktop) and touch (device/Device Simulator) uniformly.
- **Minimal setup** — a menu item builds a ready-to-dig terrain; the `Rigidbody2D` is auto-configured and the input layer auto-bootstraps.

## Requirements

- **Unity 6000.4** (Unity 6) or newer — developed against `6000.4.6f1`.
- **Universal Render Pipeline (2D)**.
- Packages (installed by default in the URP 2D template): `com.unity.inputsystem`, `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`.
- **Active Input Handling** set to **Input System Package** (or Both).

## Quick start

1. Clone this repo and open it in Unity 6.
2. Open `Assets/Scenes/Main.unity`.
3. Press **Play** and click/drag (or tap) on the terrain to dig.

To add terrain to your own scene: **Right click on hierarchy → 2D Object → Destructible Terrain**. This creates a configured terrain (sprite assigned, `Rigidbody2D` set up), adds a `Digger` if one is missing, and repairs the demo sprite's import settings.

## Texture import settings (required)

`DestructibleTerrain` reads pixels via `Texture2D.GetRawTextureData<Color32>()`, so the source sprite **must** import like this or you'll get an error / garbled pixels:

| Setting | Value |
|---|---|
| Texture Type | Sprite (2D and UI) |
| Sprite Mode | Single |
| Read/Write | **Enabled** |
| Generate Mip Maps | Off |
| Format | **RGBA 32 bit (uncompressed)** |
| Max Size | ≥ your texture's longest dimension |

The menu item applies these to the bundled demo sprite automatically. For your own art, set them in the texture's Inspector → Advanced.

## Usage

The public API is a single method — call it from anywhere, in world space:

```csharp
using slash_DTP;

public class MyDigger : MonoBehaviour
{
    public DestructibleTerrain terrain;

    void OnFire(Vector2 worldPos)
    {
        terrain.EraseCircle(worldPos, 1f); // radius in world units
    }
}
```

Erases are **queued and batched** once per frame (BFS detection and collider/visual rebuilds run once per frame regardless of how many calls you make), so call it freely.

For pointer-driven digging, `InputManager` gives you the world-space position and the terrain under the cursor, so you call the API directly without the `Digger`:

```csharp
using slash_DTP;

void Update()
{
    if (!InputManager.Instance.PointerHeld) return;
    Vector2 world = InputManager.Instance.PointerWorldPosition(Camera.main);
    DestructibleTerrain t = InputManager.TerrainUnderPointer(world); // mainland or falling chunk
    if (t != null && t.canDig) t.EraseCircle(world, t.eraseRadius);
}
```

**Components**
- `DestructibleTerrain` — the library. Requires a `SpriteRenderer` + `Rigidbody2D` (auto-added/configured). Inspector exposes terrain, destruction, physics, and performance settings.
- `Digger` — optional demo controller. A thin example of the pattern above; delete it in real games and call `EraseCircle` from your own gameplay (weapons, explosions, tools).
- `InputManager` — pointer wrapper (mouse + touch) with `PointerPosition/Pressed/Held`, plus `PointerWorldPosition(cam)` and `TerrainUnderPointer(world)` helpers. Auto-creates itself at play start, so it never needs to be in the scene.

## Performance & mobile notes

The per-frame dig/rebuild path is allocation-free (pooled buffers, Burst jobs, no per-frame GC). The dominant mobile cost is **memory, which scales with texture area**:

| Texture (RGBA32) | Approx. runtime memory* |
|---|---|
| 4096 × 4096 | ~150–210 MB |
| 2048 × 2048 | ~40–55 MB |
| 1024 × 1024 | ~10–14 MB |

\* Source texture + a runtime clone (so the asset isn't mutated) + `solidMap` + native buffers.

**Use 1024–2048 textures on mobile.** 4096 is fine for desktop showcases but risks OOM/thermal throttling on phones.

**Set the frame rate yourself.** Android/iOS default `Application.targetFrameRate` to **30 FPS**, which makes digging feel laggy even when there's plenty of headroom (the missed 60 Hz vsync interval drops you straight to 30). The library is input-free and deliberately never touches global frame settings, so set it in your own bootstrap:

```csharp
void Awake() { Application.targetFrameRate = 60; } // or 120 on high-refresh screens
```

The demo's `GameManager` does this (120). If you extract the library into a fresh project and digging feels half-speed, this is almost always why.

If you profile on-device and need to tune frame time:
- `mainlandSimplifyTolerance` — higher = fewer collider vertices = cheaper `PolygonCollider2D.SetPath` (the dominant CPU cost during digging).
- `maxRebuildsPerFrame` / `rebuildBudgetMs` — cap collider rebuild work per frame.
- `maxVisualsPerFrame` / `visualsBudgetMs` — cap texture-upload work per frame.
- `enableGhostSweep` — turn off for hard-edged/pixel-art terrain to save per-spawn cost.
- `destroyOffscreenChunks` — destroy falling chunks once they leave the camera (frees memory + physics/draw-call cost) instead of just sleeping them off-screen. Best for mobile in games where chunks fall away and aren't revisited; off by default so chunks reappear if the camera pans back.

## Resolution presets (auto-scaling)

Settings are authored once for a **1080p** baseline and auto-scale to any texture resolution, so the same preset behaves consistently on 1080p, 1440p and 4K art.

1. Create a preset: right-click in the Project window → **Create → DTP → Terrain Preset**. A fresh asset holds the 1080p baseline.
2. Assign it to the `Preset` field on a `DestructibleTerrain`. It's applied in `Awake` and **overrides the component's inline fields**. (From code: `terrain.ApplyPreset(myPreset)` before the terrain initializes.)

Scaling is based on the texture's **longer side** vs the preset's `referenceResolution` (default `1920` = 1080p; `ratio = longSide / referenceResolution`). The model is **same world, more detail** — a higher-res texture is a sharper version of the same terrain — so per-pixel mass/gravity scale *down* with area while pixel-count limits scale *up*, keeping physics and real-world sizes identical across resolutions. (If you instead use higher resolution to mean a *bigger* world, leave per-pixel values as-is and raise `referenceResolution` to your texture's long side so it isn't scaled.)

| Field | Scales by | 1080p (1920) | 1440p (2560) | 4K (3840) |
|---|---|---|---|---|
| `chunkSize` | linear (pow2) | 128 | 128 | 256 |
| `visualChunkSize` | linear (pow2) | 256 | 256 | 512 |
| `aftermathThickness` | linear | 3 | 4 | 6 |
| `maxPhysicsPixels` | area | 100,000 | 177,800 | 400,000 |
| `minimumPhysicsPixels` | area | 500 | 889 | 2,000 |
| `maxEmbedPixels` | area | 20 | 36 | 80 |
| `minContourAreaPxSqr` | area | 256 | 455 | 1,024 |
| `massPerPixel` | 1 / area | 1e-06 | 5.6e-07 | 2.5e-07 |
| `gravityPerPixel` | 1 / area | 2e-04 | 1.1e-04 | 5e-05 |

All other fields (alpha threshold, erase radius, anchors, gravity clamps, welding velocities/angles, budgets, sleep, toggles) are resolution-independent and copied verbatim. Square textures use their side length as the long side.

## Anchoring (what stays vs. what falls)

A region of terrain falls once it's no longer connected to an **anchor**. Three options control what counts as anchored:

- `groundAnchorY` (default 0) — the bottom row(s): any solid region connected to rows `0..groundAnchorY` is held. Raise it if your art has a transparent bottom margin so the art's base still anchors.
- `anchorToSideBorders` (default off) — also anchor regions touching the left/right **texture** edges.
- `anchorWholeBody` (default off) — the terrain's whole remaining body is always anchored; it never falls as a unit even if its art doesn't touch any edge (e.g. PNGs with transparent margins). Pieces you slice off still fall.

Default behavior (all off except `groundAnchorY=0`) is "full physics": the whole image is destructible and **slicing any part free drops it**. Turn on `anchorWholeBody` if you want a ground/object that never spontaneously falls regardless of where its art sits. `maxPhysicsPixels` also acts as a safety — a detached region larger than it is treated as anchored rather than spawned as a chunk.

## Project structure

```
Assets/
├── Scenes/Main.unity              demo scene
├── 4096x4096_white.png            CC0 demo sprite
└── Scripts/
    ├── DestructibleTerrain.cs     the library
    ├── TerrainPreset.cs           resolution-aware settings preset (ScriptableObject)
    ├── DTP_Preset_1080p.asset     example preset asset (1080p baseline)
    ├── Digger.cs                  demo input controller
    ├── GameManager.cs             demo: sets Application.targetFrameRate
    ├── FpsCounter.cs              demo: on-screen FPS readout
    ├── DTP.asmdef                 runtime assembly
    ├── Controls/InputManager.cs   pointer input (mouse + touch)
    └── Editor/                    GameObject menu item + editor assembly
```

Leave a ⭐ if you found it helpful !
Feel free to use the script in your production games. Attribution welcomed !

## License

MIT [LICENSE](LICENSE).
