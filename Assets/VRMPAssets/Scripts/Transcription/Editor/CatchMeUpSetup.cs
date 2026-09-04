#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using TMPro;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// One-click setup for the "Catch Me Up" feature.
    /// Adds <see cref="CatchMeUpService"/> + <see cref="CatchMeUpUI"/> to the existing
    /// TranscriptionManager object and builds the world-space recap panel.
    /// </summary>
    public class CatchMeUpSetup : EditorWindow
    {
        private static readonly Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
        private static readonly Color Accent = new Color(0.2f, 0.66f, 0.33f, 1f); // Gemini green
        private const string ROUNDED_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Round Radius 10.png";

        private float canvasScale = 0.001f;

        [MenuItem("VRMP/Setup Catch Me Up")]
        public static void ShowWindow() => GetWindow<CatchMeUpSetup>("Catch Me Up Setup");

        private void OnGUI()
        {
            GUILayout.Label("Catch Me Up Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Adds the AI 'Catch Me Up' recap feature:\n" +
                "- CatchMeUpService (networked request/recap)\n" +
                "- CatchMeUpUI (Quick Menu segment + nudge + recap panel)\n\n" +
                "Requires a TranscriptionManager already present in the scene\n" +
                "(run 'VRMP/Setup AI Assistant + Reactions' first if needed).\n\n" +
                "The 'Catch Me Up' button appears in the Quick Menu for non-host players\n" +
                "once a meeting with transcription is in progress.",
                MessageType.Info);
            EditorGUILayout.Space();

            canvasScale = EditorGUILayout.FloatField("Panel Canvas Scale", canvasScale);
            EditorGUILayout.Space(15);

            if (GUILayout.Button("Setup Catch Me Up", GUILayout.Height(40)))
                Setup();
        }

        private void Setup()
        {
            var manager = FindFirstObjectByType<TranscriptionManager>();
            if (manager == null)
            {
                EditorUtility.DisplayDialog("Catch Me Up Setup",
                    "No TranscriptionManager found in the scene.\n\n" +
                    "Run 'VRMP/Setup AI Assistant + Reactions' first, then run this setup.",
                    "OK");
                return;
            }

            GameObject root = manager.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(root, "Setup Catch Me Up");

            var service = root.GetComponent<CatchMeUpService>();
            if (service == null) service = Undo.AddComponent<CatchMeUpService>(root);

            var ui = root.GetComponent<CatchMeUpUI>();
            if (ui == null) ui = Undo.AddComponent<CatchMeUpUI>(root);

            // Build (or reuse) the recap panel.
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ROUNDED_SPRITE_PATH);
            TMP_FontAsset font = LoadTMPFont();

            Transform existing = root.transform.Find("CatchMeUpPanel");
            GameObject panel = existing != null ? existing.gameObject : BuildPanel(root.transform, roundedSprite, font);

            var recapText = panel.transform.Find("Panel/Recap")?.GetComponent<TMP_Text>();
            var statusText = panel.transform.Find("Panel/Status")?.GetComponent<TMP_Text>();

            var soUI = new SerializedObject(ui);
            soUI.FindProperty("m_Panel").objectReferenceValue = panel;
            if (recapText != null) soUI.FindProperty("m_RecapText").objectReferenceValue = recapText;
            if (statusText != null) soUI.FindProperty("m_StatusText").objectReferenceValue = statusText;
            soUI.ApplyModifiedProperties();

            EditorUtility.SetDirty(root);
            Selection.activeGameObject = root;

            Debug.Log("[CatchMeUpSetup] Setup complete!");
            EditorUtility.DisplayDialog("Catch Me Up Setup",
                "Catch Me Up is ready!\n\n" +
                "- Non-host players see a 'Catch Me Up' button in the Quick Menu when a meeting is in progress.\n" +
                "- Tapping it asks the host's AI for a recap and shows it on the panel.",
                "OK");
        }

        private GameObject BuildPanel(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject canvasGO = new GameObject("CatchMeUpPanel");
            canvasGO.transform.SetParent(parent, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 71;
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rt = canvasGO.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(340, 380);
            rt.localScale = Vector3.one * canvasScale;
            canvasGO.layer = LayerMask.NameToLayer("UI");

            var col = canvasGO.AddComponent<BoxCollider>();
            col.size = new Vector3(340, 380, 10);
            col.isTrigger = true;

            // Background panel with vertical layout.
            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGO.transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = PanelBackground;
            if (roundedSprite != null) { panelImg.sprite = roundedSprite; panelImg.type = Image.Type.Sliced; panelImg.pixelsPerUnitMultiplier = 2f; }

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 16, 16);
            vlg.spacing = 8;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            CreateLabel(panel.transform, "Title", "🗣 Catch Me Up", font, 18, FontStyles.Bold, 30, Accent);
            CreateLabel(panel.transform, "Status", "", font, 12, FontStyles.Italic, 22, new Color(0.7f, 0.7f, 0.7f));

            var recap = CreateLabel(panel.transform, "Recap", "", font, 14, FontStyles.Normal, 0, Color.white);
            recap.alignment = TextAlignmentOptions.TopLeft;
            recap.textWrappingMode = TextWrappingModes.Normal;
            var recapLe = recap.GetComponent<LayoutElement>();
            recapLe.flexibleHeight = 1;
            recapLe.minHeight = 250;

            canvasGO.SetActive(false);
            return canvasGO;
        }

        private TMP_Text CreateLabel(Transform parent, string name, string text, TMP_FontAsset font,
            int fontSize, FontStyles style, float height, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            if (height > 0) { le.preferredHeight = height; le.minHeight = height; }
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.raycastTarget = false;
            if (font != null) tmp.font = font;
            return tmp;
        }

        private TMP_FontAsset LoadTMPFont()
        {
            string[] fontPaths = {
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset",
                "Packages/com.unity.textmeshpro/Editor Resources/Fonts & Materials/LiberationSans SDF.asset"
            };
            foreach (var path in fontPaths)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null) return font;
            }
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }
    }
}
#endif
