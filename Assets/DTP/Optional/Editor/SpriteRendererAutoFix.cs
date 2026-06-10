using UnityEditor;
using UnityEngine;

namespace slash_DTP
{
    [CustomEditor(typeof(SpriteRenderer))]
    public class SpriteRendererAutoFix : Editor
    {
        private Editor defaultEditor;
        private SpriteRenderer spriteRenderer;
        private Sprite previousSprite;

        void OnEnable()
        {
            spriteRenderer = (SpriteRenderer)target;
            previousSprite = spriteRenderer.sprite;
            
            // Create default editor for normal inspector
            defaultEditor = CreateEditor(target, typeof(Editor).Assembly.GetType("UnityEditor.SpriteRendererEditor"));
        }

        void OnDisable()
        {
            if (defaultEditor != null)
                DestroyImmediate(defaultEditor);
        }

        public override void OnInspectorGUI()
        {
            // Draw default inspector
            if (defaultEditor != null)
                defaultEditor.OnInspectorGUI();

            // Check if sprite changed
            if (spriteRenderer.sprite != previousSprite)
            {
                Sprite newSprite = spriteRenderer.sprite;
                previousSprite = newSprite;

                // Check if sprite needs fixing
                if (newSprite != null && newSprite.texture != null)
                {
                    var dt = spriteRenderer.GetComponent<DestructibleTerrain>();
                    if (dt != null && dt.textureSettings.autoFixImportSettings)
                    {
                        if (NeedsFixing(newSprite.texture))
                        {
                            FixTextureImportSettings(newSprite.texture);
                        }
                    }
                }
            }
        }

        private bool NeedsFixing(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return false;

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;

            // Check ALL required settings
            if (!importer.isReadable) return true;
            if (importer.textureType != TextureImporterType.Sprite) return true;
            if (importer.spriteImportMode != SpriteImportMode.Single) return true;
            if (importer.mipmapEnabled) return true;
            if (!importer.alphaIsTransparency) return true;

            var platformSettings = importer.GetDefaultPlatformTextureSettings();
            if (platformSettings.format != TextureImporterFormat.RGBA32) return true;
            if (platformSettings.textureCompression != TextureImporterCompression.Uncompressed) return true;

            return false;
        }

        private void FixTextureImportSettings(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return;

            Debug.LogWarning($"[SpriteRendererAutoFix] Auto-fixing texture '{texture.name}' import settings...");

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;

                if (!importer.isReadable)
                {
                    importer.isReadable = true;
                    changed = true;
                }
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    changed = true;
                }
                if (importer.spriteImportMode != SpriteImportMode.Single)
                {
                    importer.spriteImportMode = SpriteImportMode.Single;
                    changed = true;
                }
                if (importer.mipmapEnabled)
                {
                    importer.mipmapEnabled = false;
                    changed = true;
                }
                if (!importer.alphaIsTransparency)
                {
                    importer.alphaIsTransparency = true;
                    changed = true;
                }

                var platformSettings = importer.GetDefaultPlatformTextureSettings();
                if (platformSettings.format != TextureImporterFormat.RGBA32 ||
                    platformSettings.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    platformSettings.format = TextureImporterFormat.RGBA32;
                    platformSettings.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.SetPlatformTextureSettings(platformSettings);
                    changed = true;
                }

                if (changed)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    
                    // Reassign sprite after import
                    EditorApplication.delayCall += () =>
                    {
                        if (spriteRenderer != null)
                        {
                            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                            foreach (var asset in assets)
                            {
                                if (asset is Sprite sprite)
                                {
                                    spriteRenderer.sprite = sprite;
                                    previousSprite = sprite;
                                    EditorUtility.SetDirty(spriteRenderer);
                                    Debug.Log($"[SpriteRendererAutoFix] Fixed texture '{texture.name}'. Sprite will persist.");
                                    break;
                                }
                            }
                        }
                    };
                }
            }
        }
    }
}
