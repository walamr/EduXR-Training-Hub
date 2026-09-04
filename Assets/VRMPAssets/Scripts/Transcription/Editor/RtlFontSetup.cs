#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Net;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace XRMultiplayer.Transcription.Editor
{
    /// <summary>
    /// One-click setup for Right-To-Left (Hebrew + Arabic) font support.
    ///
    /// What it does:
    ///  1. Ensures a Hebrew TMP font asset exists (downloads Noto Sans Hebrew and creates a
    ///     DYNAMIC SDF asset so glyphs are rendered on demand on both Editor and Quest).
    ///  2. Re-uses the existing Arabic TMP font asset (kept untouched so ArabicFixer keeps working).
    ///  3. Registers BOTH fonts in TextMeshPro's GLOBAL fallback list. This is the reusable part:
    ///     every TMP text in the project (transcript, summary, language buttons, status text and
    ///     any future UI) automatically falls back to these fonts - no per-object wiring needed.
    ///  4. Also patches the common UI fonts' local fallback tables for extra safety.
    /// </summary>
    public static class RtlFontSetup
    {
        private const string k_FontFolderPath = "Assets/VRMPAssets/Fonts";
        private const string k_TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        // Hebrew (Noto Sans Hebrew) - direct raw URL (the googlefonts org now redirects to notofonts)
        private const string k_HebrewFontUrl = "https://raw.githubusercontent.com/notofonts/noto-fonts/main/hinted/ttf/NotoSansHebrew/NotoSansHebrew-Regular.ttf";
        private const string k_HebrewTtfName = "NotoSansHebrew-Regular.ttf";
        private const string k_HebrewSdfName = "NotoSansHebrew-Regular SDF";

        // Arabic (already set up by ArabicFontSetup)
        private const string k_ArabicSdfName = "NotoSansArabic-Regular SDF";

        [MenuItem("VRMP/Setup RTL Fonts (Hebrew + Arabic)")]
        public static void SetupRtlFonts()
        {
            try
            {
                EditorUtility.DisplayProgressBar("RTL Font Setup", "Preparing...", 0.05f);

                // 1. Hebrew font asset (create if missing)
                TMP_FontAsset hebrewFont = EnsureHebrewFontAsset();
                if (hebrewFont == null)
                {
                    EditorUtility.ClearProgressBar();
                    return; // EnsureHebrewFontAsset already reported the problem
                }

                // 2. Arabic font asset (re-use existing)
                TMP_FontAsset arabicFont = FindFontAsset(k_ArabicSdfName);
                if (arabicFont == null)
                {
                    Debug.LogWarning("[RtlFontSetup] Arabic font not found. Run 'VRMP/Setup Arabic Font Support' first if you need Arabic. Continuing with Hebrew only.");
                }

                // 3. Register both as GLOBAL TMP fallbacks (the reusable part).
                EditorUtility.DisplayProgressBar("RTL Font Setup", "Registering global fallbacks...", 0.7f);
                var globals = new List<TMP_FontAsset>();
                if (arabicFont != null) globals.Add(arabicFont); // Arabic first (needs shaping support)
                globals.Add(hebrewFont);
                RegisterGlobalFallbacks(globals);

                // 4. Patch common UI fonts' local fallback tables for redundancy.
                //    IMPORTANT: the RTL fonts must come FIRST in each fallback list, before
                //    TMP's bundled 'LiberationSans SDF - Fallback'. That bundled dynamic asset
                //    can hold stale cached entries for Hebrew codepoints (recorded before the
                //    Hebrew font existed), which makes TMP draw blanks instead of ever reaching
                //    the real Hebrew font.
                EditorUtility.DisplayProgressBar("RTL Font Setup", "Patching UI fonts...", 0.85f);
                foreach (var mainFontName in new[] { "Inter-Regular SDF", "LiberationSans SDF" })
                {
                    var mainFont = FindFontAsset(mainFontName);
                    if (mainFont == null) continue;
                    PrependLocalFallback(mainFont, hebrewFont);
                    if (arabicFont != null) PrependLocalFallback(mainFont, arabicFont);
                }

                // Also patch the project's configured default TMP font.
                var defaultFont = TMP_Settings.defaultFontAsset;
                if (defaultFont != null)
                {
                    PrependLocalFallback(defaultFont, hebrewFont);
                    if (arabicFont != null) PrependLocalFallback(defaultFont, arabicFont);
                }

                // 5. Clear stale cached Hebrew entries from the bundled fallback font.
                EditorUtility.DisplayProgressBar("RTL Font Setup", "Clearing stale fallback caches...", 0.95f);
                ClearStaleFallbackCache();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();

                EditorUtility.DisplayDialog("RTL Font Setup",
                    "Hebrew + Arabic fallback fonts are configured!\n\n" +
                    "- Hebrew font created as a DYNAMIC SDF asset (renders on Editor and Quest).\n" +
                    "- Both fonts added to TextMeshPro's global fallback list, so ALL TMP text " +
                    "in the project now supports Hebrew and Arabic automatically.\n\n" +
                    "Use 'VRMP/Test RTL Sample Text' to verify in the scene.",
                    "OK");
            }
            catch (System.Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[RtlFontSetup] Setup failed: {e}");
            }
        }

        /// <summary>
        /// Loads the Hebrew TMP font asset, creating it (and downloading the TTF) if needed.
        /// The asset is created in DYNAMIC mode so any Hebrew glyph is rendered on demand.
        /// </summary>
        private static TMP_FontAsset EnsureHebrewFontAsset()
        {
            // Already created?
            TMP_FontAsset existing = FindFontAsset(k_HebrewSdfName);
            if (existing != null)
            {
                EnsureDynamic(existing);
                return existing;
            }

            if (!Directory.Exists(k_FontFolderPath))
                Directory.CreateDirectory(k_FontFolderPath);

            string ttfPath = Path.Combine(k_FontFolderPath, k_HebrewTtfName);

            // Download the TTF if needed.
            if (!File.Exists(ttfPath))
            {
                EditorUtility.DisplayProgressBar("RTL Font Setup", "Downloading Noto Sans Hebrew...", 0.2f);
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        client.DownloadFile(k_HebrewFontUrl, ttfPath);
                    }
                    AssetDatabase.Refresh();
                    Debug.Log($"[RtlFontSetup] Downloaded Hebrew font to: {ttfPath}");
                }
                catch (System.Exception e)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError($"[RtlFontSetup] Failed to download Hebrew font: {e.Message}");
                    EditorUtility.DisplayDialog("Hebrew Font Download Failed",
                        "Could not download Noto Sans Hebrew automatically.\n\n" +
                        "Please download 'NotoSansHebrew-Regular.ttf' from Google Fonts and place it in:\n" +
                        k_FontFolderPath + "\n\nThen run this menu again.",
                        "OK");
                    return null;
                }
            }

            // Make sure the TTF is imported and includes font data (needed for dynamic at runtime).
            var ttfImporter = AssetImporter.GetAtPath(ttfPath) as TrueTypeFontImporter;
            if (ttfImporter != null && !ttfImporter.includeFontData)
            {
                ttfImporter.includeFontData = true;
                ttfImporter.SaveAndReimport();
            }

            Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (sourceFont == null)
            {
                Debug.LogError($"[RtlFontSetup] Failed to load Hebrew TTF at: {ttfPath}");
                return null;
            }

            EditorUtility.DisplayProgressBar("RTL Font Setup", "Creating Hebrew TMP font asset...", 0.5f);

            // Create a DYNAMIC SDF font asset: glyphs are added to the atlas on demand,
            // which works in the Editor and in builds (including Quest).
            TMP_FontAsset hebrewFont = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                90,   // sampling point size
                9,    // atlas padding
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                1024, // atlas width
                1024, // atlas height
                AtlasPopulationMode.Dynamic,
                true  // enable multi-atlas support
            );

            if (hebrewFont == null)
            {
                Debug.LogError("[RtlFontSetup] Failed to create Hebrew TMP font asset.");
                return null;
            }

            string sdfPath = Path.Combine(k_FontFolderPath, k_HebrewSdfName + ".asset");
            AssetDatabase.CreateAsset(hebrewFont, sdfPath);

            // The atlas texture & material must be stored as sub-assets of the font asset,
            // otherwise the font renders with an invalid (pink) material.
            if (hebrewFont.atlasTexture != null &&
                string.IsNullOrEmpty(AssetDatabase.GetAssetPath(hebrewFont.atlasTexture)))
            {
                hebrewFont.atlasTexture.name = k_HebrewSdfName + " Atlas";
                AssetDatabase.AddObjectToAsset(hebrewFont.atlasTexture, hebrewFont);
            }
            if (hebrewFont.material != null &&
                string.IsNullOrEmpty(AssetDatabase.GetAssetPath(hebrewFont.material)))
            {
                hebrewFont.material.name = k_HebrewSdfName + " Material";
                AssetDatabase.AddObjectToAsset(hebrewFont.material, hebrewFont);
            }

            EditorUtility.SetDirty(hebrewFont);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(sdfPath);

            Debug.Log($"[RtlFontSetup] Created dynamic Hebrew font asset at: {sdfPath}");
            return hebrewFont;
        }

        private static void EnsureDynamic(TMP_FontAsset font)
        {
            if (font != null && font.atlasPopulationMode != AtlasPopulationMode.Dynamic)
            {
                font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                EditorUtility.SetDirty(font);
            }
        }

        /// <summary>
        /// Adds the given fonts to TextMeshPro's global fallback list (TMP Settings),
        /// so every TMP text in the project can render their glyphs.
        /// </summary>
        private static void RegisterGlobalFallbacks(List<TMP_FontAsset> fonts)
        {
            var tmpSettings = AssetDatabase.LoadAssetAtPath<TMP_Settings>(k_TmpSettingsPath);
            if (tmpSettings == null)
            {
                Debug.LogWarning("[RtlFontSetup] Could not find TMP Settings asset; skipping global fallback registration.");
                return;
            }

            var so = new SerializedObject(tmpSettings);
            var listProp = so.FindProperty("m_fallbackFontAssets");
            if (listProp == null)
            {
                Debug.LogWarning("[RtlFontSetup] TMP Settings has no 'm_fallbackFontAssets' field.");
                return;
            }

            foreach (var font in fonts)
            {
                if (font == null) continue;
                if (ListContains(listProp, font)) continue;

                listProp.arraySize++;
                listProp.GetArrayElementAtIndex(listProp.arraySize - 1).objectReferenceValue = font;
                Debug.Log($"[RtlFontSetup] Added '{font.name}' to TMP global fallback list.");
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(tmpSettings);
        }

        private static bool ListContains(SerializedProperty listProp, Object obj)
        {
            for (int i = 0; i < listProp.arraySize; i++)
            {
                if (listProp.GetArrayElementAtIndex(i).objectReferenceValue == obj)
                    return true;
            }
            return false;
        }

        private static void AddLocalFallback(TMP_FontAsset mainFont, TMP_FontAsset fallback)
        {
            if (mainFont == null || fallback == null || mainFont == fallback) return;

            if (mainFont.fallbackFontAssetTable == null)
                mainFont.fallbackFontAssetTable = new List<TMP_FontAsset>();

            if (!mainFont.fallbackFontAssetTable.Contains(fallback))
            {
                mainFont.fallbackFontAssetTable.Add(fallback);
                EditorUtility.SetDirty(mainFont);
                Debug.Log($"[RtlFontSetup] Added '{fallback.name}' as local fallback to '{mainFont.name}'.");
            }
        }

        /// <summary>
        /// Inserts the fallback at the FRONT of the main font's fallback list (moving it
        /// there if it is already present), so it wins over TMP's bundled fallback assets.
        /// </summary>
        private static void PrependLocalFallback(TMP_FontAsset mainFont, TMP_FontAsset fallback)
        {
            if (mainFont == null || fallback == null || mainFont == fallback) return;

            if (mainFont.fallbackFontAssetTable == null)
                mainFont.fallbackFontAssetTable = new List<TMP_FontAsset>();

            int existing = mainFont.fallbackFontAssetTable.IndexOf(fallback);
            if (existing == 0) return; // already first
            if (existing > 0) mainFont.fallbackFontAssetTable.RemoveAt(existing);

            mainFont.fallbackFontAssetTable.Insert(0, fallback);
            EditorUtility.SetDirty(mainFont);
            Debug.Log($"[RtlFontSetup] Moved '{fallback.name}' to the FRONT of '{mainFont.name}' fallbacks.");
        }

        /// <summary>
        /// Clears stale cached glyph/character data from TMP's bundled dynamic fallback font.
        /// That asset can contain saved Hebrew character entries from sessions before the
        /// Hebrew font existed; those entries hijack Hebrew letters and render them blank.
        /// Safe to run anytime: dynamic assets regenerate their data on demand.
        /// </summary>
        [MenuItem("VRMP/Fix Hebrew Glyphs (Clear Stale Font Cache)")]
        public static void ClearStaleFallbackCache()
        {
            int cleared = 0;

            var bundledFallback = FindFontAsset("LiberationSans SDF - Fallback");
            if (bundledFallback != null)
            {
                bundledFallback.ClearFontAssetData(true);
                EditorUtility.SetDirty(bundledFallback);
                cleared++;
                Debug.Log("[RtlFontSetup] Cleared stale dynamic cache on 'LiberationSans SDF - Fallback'.");
            }

            // Also reset the Hebrew font's own cache so it regenerates cleanly.
            var hebrew = FindFontAsset(k_HebrewSdfName);
            if (hebrew != null && hebrew.atlasPopulationMode == AtlasPopulationMode.Dynamic)
            {
                hebrew.ClearFontAssetData(true);
                EditorUtility.SetDirty(hebrew);
                cleared++;
                Debug.Log($"[RtlFontSetup] Cleared dynamic cache on '{k_HebrewSdfName}'.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[RtlFontSetup] Stale font cache cleanup done ({cleared} assets cleared).");
        }

        // -----------------------------------------------------------------------------
        // TEST HELPERS
        // -----------------------------------------------------------------------------

        [MenuItem("VRMP/Test Long AI Summary Popup")]
        public static void TestLongSummaryPopup()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Test Long AI Summary",
                    "Enter Play mode first, then run this menu again.\n\n" +
                    "It opens the AI summary popup with a long multilingual test text so you can " +
                    "verify wrapping, scrolling, RTL alignment and the close button.", "OK");
                return;
            }

            var system = Object.FindFirstObjectByType<TranscriptionSystem>();
            if (system == null)
            {
                Debug.LogError("[RtlFontSetup] No TranscriptionSystem found in the scene.");
                return;
            }

            system.ShowTestSummary();
        }

        [MenuItem("VRMP/Test RTL Sample Text")]
        public static void TestRtlSampleText()
        {
            // If we're in Play mode and the transcript panel is live, push the sample through
            // the REAL pipeline (RTL detection + ArabicFixer + fallback fonts).
            if (Application.isPlaying)
            {
                var system = Object.FindFirstObjectByType<TranscriptionSystem>();
                if (system != null)
                {
                    system.AddSampleTranscriptText();
                    Debug.Log("[RtlFontSetup] Injected EN/HE/AR sample lines into the live transcript panel.");
                    return;
                }
            }

            // Otherwise create a standalone world-space sample so glyphs can be verified in edit mode.
            CreateStandaloneSampleCanvas();
        }

        private static void CreateStandaloneSampleCanvas()
        {
            var canvasObj = new GameObject("RTL Font Test Canvas");
            Undo.RegisterCreatedObjectUndo(canvasObj, "Create RTL Font Test");

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(800, 500);
            canvasRect.localScale = Vector3.one * 0.001f;

            // Place it roughly in front of the scene view camera if possible.
            var sceneCam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            if (sceneCam != null)
            {
                canvasObj.transform.position = sceneCam.transform.position + sceneCam.transform.forward * 1.5f;
                canvasObj.transform.rotation = Quaternion.LookRotation(canvasObj.transform.position - sceneCam.transform.position);
            }
            else
            {
                canvasObj.transform.position = new Vector3(0, 1.5f, 1f);
            }

            // Dark background panel
            var bg = new GameObject("BG");
            bg.transform.SetParent(canvasObj.transform, false);
            var bgRect = bg.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero; bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero; bgRect.offsetMax = Vector2.zero;
            bg.AddComponent<UnityEngine.UI.Image>().color = new Color(0.106f, 0.106f, 0.106f, 0.95f);

            // Content column with the same per-line structure the live transcript uses:
            // English = LTR line; Arabic = LTR component + ArabicFixer, right-aligned;
            // Hebrew = metadata line + logical-order body on a native-RTL TMP component.
            var column = new GameObject("Lines");
            column.transform.SetParent(canvasObj.transform, false);
            var columnRect = column.AddComponent<RectTransform>();
            columnRect.anchorMin = Vector2.zero; columnRect.anchorMax = Vector2.one;
            columnRect.offsetMin = new Vector2(30, 30); columnRect.offsetMax = new Vector2(-30, -30);
            var columnLayout = column.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            columnLayout.spacing = 18f;
            columnLayout.childControlWidth = true;
            columnLayout.childControlHeight = true;
            columnLayout.childForceExpandWidth = true;
            columnLayout.childForceExpandHeight = false;

            TMP_Text MakeLine(string content, TextAlignmentOptions align, bool rtl, float size)
            {
                var go = new GameObject(rtl ? "Line (RTL)" : "Line");
                go.transform.SetParent(column.transform, false);
                var t = go.AddComponent<TextMeshProUGUI>();
                t.fontSize = size;
                t.color = Color.white;
                t.richText = true;
                t.textWrappingMode = TextWrappingModes.Normal;
                t.isRightToLeftText = rtl;
                t.alignment = align;
                t.text = content;
                return t;
            }

            string time = System.DateTime.Now.ToString("HH:mm:ss");

            // English
            MakeLine($"<color=#888888>[{time}]</color> <color=#4FC3F7>EN:</color> Hello, this is an English test.",
                TextAlignmentOptions.TopLeft, rtl: false, 34);

            // Hebrew: metadata line + native-RTL body in normal logical order
            MakeLine($"<color=#888888>[{time}]</color> <color=#4FC3F7>HE:</color>",
                TextAlignmentOptions.TopRight, rtl: false, 27);
            MakeLine(RtlTextUtility.PrepareForRtlRendering("שלום, זאת בדיקה בעברית. השעה 10:30 בבוקר."),
                TextAlignmentOptions.TopRight, rtl: true, 34);

            // Arabic: ArabicFixer shaping on a normal LTR component
            MakeLine($"<color=#4FC3F7>AR:</color> {ArabicFixer.Fix("مرحبا، هذا اختبار باللغة العربية.")} <color=#888888>[{time}]</color>",
                TextAlignmentOptions.TopRight, rtl: false, 34);

            Selection.activeGameObject = canvasObj;
            Debug.Log("[RtlFontSetup] Created standalone RTL test canvas using the same per-line structure as the live transcript panel.");
        }

        private static TMP_FontAsset FindFontAsset(string assetName)
        {
            string[] guids = AssetDatabase.FindAssets($"{assetName} t:TMP_FontAsset");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == assetName)
                    return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            }
            // Fallback: first match
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }
    }
}
#endif
