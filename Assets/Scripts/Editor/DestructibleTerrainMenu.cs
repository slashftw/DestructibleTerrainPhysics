using UnityEditor;
using UnityEngine;

namespace slash_DTP
{
    /// <summary>
    /// Editor menu that spawns a fully-configured, ready-to-dig destructible
    /// terrain in one click: GameObject → 2D Object → Destructible Terrain.
    ///
    /// It also guarantees the bundled demo sprite imports in a DT-compatible
    /// way (readable, uncompressed RGBA32, no mip maps, single sprite) — these
    /// are the exact import settings DestructibleTerrain requires because it
    /// reads pixels via Texture2D.GetRawTextureData&lt;Color32&gt;().
    /// </summary>
    static class DestructibleTerrainMenu
    {
        const string DemoSpritePath = "Assets/4096x4096_white.png";

        [MenuItem("GameObject/2D Object/Destructible Terrain", false, 10)]
        static void Create(MenuCommand cmd)
        {
            Sprite sprite = LoadDemoSprite();

            var go = new GameObject("Destructible Terrain");
            var sr = go.AddComponent<SpriteRenderer>();
            if (sprite != null) sr.sprite = sprite;
            var dt = go.AddComponent<DestructibleTerrain>(); // RB2D auto-added + Reset()-configured
            dt.groundAnchorY = -1; // demo default: no bottom-row ground anchor

            if (Object.FindAnyObjectByType<Digger>() == null)
            {
                var digger = new GameObject("Digger");
                digger.AddComponent<Digger>();
                Undo.RegisterCreatedObjectUndo(digger, "Create Digger");
            }

            if (Camera.main == null)
            {
                Debug.LogWarning("[DestructibleTerrain] No camera tagged 'MainCamera' " +
                    "in the scene. Digger needs one to convert taps to world space.");
            }

            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Destructible Terrain");
            Selection.activeObject = go;
        }

        // Loads the bundled sprite, repairing its import settings if needed so the
        // one-click terrain works without the user touching the Inspector.
        static Sprite LoadDemoSprite()
        {
            if (AssetImporter.GetAtPath(DemoSpritePath) is not TextureImporter importer)
            {
                Debug.LogWarning($"[DestructibleTerrain] Demo sprite not found at " +
                    $"'{DemoSpritePath}'. Created terrain has no sprite — assign one " +
                    "(readable, uncompressed RGBA32, no mip maps) to its SpriteRenderer.");
                return null;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite; dirty = true;
            }
            if (importer.spriteImportMode != SpriteImportMode.Single)
            {
                importer.spriteImportMode = SpriteImportMode.Single; dirty = true;
            }
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }

            var ps = importer.GetDefaultPlatformTextureSettings();
            if (ps.format != TextureImporterFormat.RGBA32 ||
                ps.textureCompression != TextureImporterCompression.Uncompressed ||
                ps.maxTextureSize < 4096)
            {
                ps.format = TextureImporterFormat.RGBA32;
                ps.textureCompression = TextureImporterCompression.Uncompressed;
                ps.maxTextureSize = 4096;
                importer.SetPlatformTextureSettings(ps);
                dirty = true;
            }

            if (dirty) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(DemoSpritePath);
        }
    }
}
