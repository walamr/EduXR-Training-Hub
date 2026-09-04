#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Net;
using TMPro;

namespace XRMultiplayer.Transcription.Editor
{
    /// <summary>
    /// Editor utility to set up Arabic/Hebrew font support for transcription.
    /// </summary>
    public static class ArabicFontSetup
    {
        private const string k_FontUrl = "https://github.com/googlefonts/noto-fonts/raw/main/hinted/ttf/NotoSansArabic/NotoSansArabic-Regular.ttf";
        private const string k_FontFolderPath = "Assets/VRMPAssets/Fonts";
        private const string k_FontFileName = "NotoSansArabic-Regular.ttf";
        private const string k_TMPFontAssetName = "NotoSansArabic SDF";

        [MenuItem("VRMP/Setup Arabic Font Support")]
        public static void SetupArabicFont()
        {
            // 1. Create fonts folder if needed
            if (!Directory.Exists(k_FontFolderPath))
            {
                Directory.CreateDirectory(k_FontFolderPath);
            }

            string fontPath = Path.Combine(k_FontFolderPath, k_FontFileName);
            
            // 2. Download font if not exists
            if (!File.Exists(fontPath))
            {
                EditorUtility.DisplayProgressBar("Arabic Font Setup", "Downloading Noto Sans Arabic...", 0.2f);
                
                try
                {
                    using (WebClient client = new WebClient())
                    {
                        client.DownloadFile(k_FontUrl, fontPath);
                    }
                    AssetDatabase.Refresh();
                    Debug.Log($"[ArabicFontSetup] Downloaded font to: {fontPath}");
                }
                catch (System.Exception e)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError($"[ArabicFontSetup] Failed to download font: {e.Message}");
                    EditorUtility.DisplayDialog("Error", 
                        "Failed to download font. Please manually download NotoSansArabic-Regular.ttf from Google Fonts and place it in Assets/VRMPAssets/Fonts/", 
                        "OK");
                    return;
                }
            }

            EditorUtility.DisplayProgressBar("Arabic Font Setup", "Creating TMP Font Asset...", 0.5f);

            // 3. Load the font
            Font font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[ArabicFontSetup] Failed to load font from: {fontPath}");
                return;
            }

            // 4. Check if TMP font asset already exists
            string tmpFontPath = Path.Combine(k_FontFolderPath, k_TMPFontAssetName + ".asset");
            TMP_FontAsset existingAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(tmpFontPath);
            
            if (existingAsset != null)
            {
                EditorUtility.ClearProgressBar();
                Debug.Log($"[ArabicFontSetup] TMP Font Asset already exists at: {tmpFontPath}");
                SetupFallbackFont(existingAsset);
                return;
            }

            // 5. Create TMP Font Asset
            EditorUtility.DisplayProgressBar("Arabic Font Setup", "Generating font atlas (this may take a moment)...", 0.7f);

            // Use TMP Font Asset Creator settings for Arabic
            TMP_FontAsset tmpFont = TMP_FontAsset.CreateFontAsset(
                font,
                90,  // Sampling point size
                9,   // Padding
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                1024, // Atlas width
                1024  // Atlas height
            );

            if (tmpFont == null)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("[ArabicFontSetup] Failed to create TMP Font Asset!");
                return;
            }

            // 6. Save the asset
            AssetDatabase.CreateAsset(tmpFont, tmpFontPath);
            AssetDatabase.SaveAssets();
            
            Debug.Log($"[ArabicFontSetup] Created TMP Font Asset at: {tmpFontPath}");

            // 7. Add Arabic character range to the font
            EditorUtility.DisplayProgressBar("Arabic Font Setup", "Adding Arabic characters...", 0.9f);
            
            // This requires manual step in TMP Font Asset Creator
            EditorUtility.ClearProgressBar();
            
            SetupFallbackFont(tmpFont);
            
            EditorUtility.DisplayDialog("Arabic Font Setup", 
                "Font asset created! Now you need to:\n\n" +
                "1. Select 'NotoSansArabic SDF' in Project window\n" +
                "2. Click 'Update Atlas Texture' in Inspector\n" +
                "3. Add character range: 0600-06FF,0750-077F,FB50-FDFF,FE70-FEFF (Arabic)\n" +
                "4. Click 'Generate Font Atlas'\n\n" +
                "The font has been set as fallback for LiberationSans.", 
                "OK");
            
            // Select the created asset
            Selection.activeObject = tmpFont;
        }

        private static void SetupFallbackFont(TMP_FontAsset arabicFont)
        {
            // Find LiberationSans SDF and add Arabic font as fallback
            string[] guids = AssetDatabase.FindAssets("LiberationSans SDF t:TMP_FontAsset");
            
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset mainFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                
                if (mainFont != null && !path.Contains("Fallback"))
                {
                    // Check if already in fallback list
                    if (mainFont.fallbackFontAssetTable == null)
                    {
                        mainFont.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
                    }
                    
                    if (!mainFont.fallbackFontAssetTable.Contains(arabicFont))
                    {
                        mainFont.fallbackFontAssetTable.Add(arabicFont);
                        EditorUtility.SetDirty(mainFont);
                        AssetDatabase.SaveAssets();
                        Debug.Log($"[ArabicFontSetup] Added {arabicFont.name} as fallback to {mainFont.name}");
                    }
                }
            }
        }

        [MenuItem("VRMP/Force Link Arabic Font (Fix Squares)")]
        public static void ForceLinkFonts()
        {
            // 1. Find Arabic Font
            string arabicPath = "Assets/VRMPAssets/Fonts/NotoSansArabic-Regular SDF.asset";
            TMP_FontAsset arabicFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(arabicPath);
            
            if (arabicFont == null)
            {
                // Try to find it by search
                string[] results = AssetDatabase.FindAssets("NotoSansArabic-Regular SDF t:TMP_FontAsset");
                if (results.Length > 0)
                {
                    arabicFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(results[0]));
                }
            }

            if (arabicFont == null)
            {
                EditorUtility.DisplayDialog("Error", "Could not find 'NotoSansArabic-Regular SDF' asset! Please run 'Setup Arabic Font Support' first.", "OK");
                return;
            }

            // 2. Find Main Fonts to patch
            string[] fontGuids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            int patchCount = 0;
            
            foreach (var guid in fontGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == AssetDatabase.GetAssetPath(arabicFont)) continue; // Don't add to self

                TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                
                // We mainly want to patch fonts that are likely to be used for UI like "LiberationSans", "Inter", etc.
                if (font != null)
                {
                    if (font.fallbackFontAssetTable == null)
                        font.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();

                    if (!font.fallbackFontAssetTable.Contains(arabicFont))
                    {
                        font.fallbackFontAssetTable.Add(arabicFont);
                        EditorUtility.SetDirty(font);
                        patchCount++;
                        Debug.Log($"[ForceLink] Added Arabic fallback to: {font.name}");
                    }
                }
            }
            
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Success", $"Linked Arabic font to {patchCount} fonts!\nThe squares should be gone now.", "OK");
        }

        [MenuItem("VRMP/Open TMP Font Asset Creator")]
        public static void OpenFontAssetCreator()
        {
            EditorApplication.ExecuteMenuItem("Window/TextMeshPro/Font Asset Creator");
        }
    }
}
#endif
