#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using TMPro;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Editor wizard to setup the Quick Menu with project-consistent styling.
    /// Uses standard pixel dimensions like other project UIs.
    /// </summary>
    public class QuickMenuSetup : EditorWindow
    {
        // ========== DESIGN SYSTEM COLORS (matching project theme) ==========
        private static readonly Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
        private static readonly Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f);
        private static readonly Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
        private static readonly Color ButtonPressed = new Color(0.1f, 0.5f, 0.85f, 1f);
        private static readonly string ROUNDED_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Round Radius 10.png";
        private static readonly string CIRCLE_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Circle_60x60_Horizontal.png";

        private GameObject votingHostPanel;
        private GameObject playerOptionsPanel;
        private GameObject recordingPanel;
        private GameObject environmentPanel;
        
        // Scale: 0.001 is standard for project (1 pixel = 1mm)
        // User can adjust if needed
        private float canvasScale = 0.001f;

        [MenuItem("VRMP/Setup Quick Menu")]
        public static void ShowWindow()
        {
            GetWindow<QuickMenuSetup>("Quick Menu Setup");
        }

        private void OnGUI()
        {
            GUILayout.Label("Quick Menu Setup Wizard", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Creates a Radial Menu that follows the avatar.\nScale 0.001 = 30cm menu (standard for VR)", MessageType.Info);
            EditorGUILayout.Space();

            GUILayout.Label("Panels to Integrate", EditorStyles.boldLabel);
            votingHostPanel = (GameObject)EditorGUILayout.ObjectField("Voting Host Panel", votingHostPanel, typeof(GameObject), true);
            playerOptionsPanel = (GameObject)EditorGUILayout.ObjectField("Player Options Panel", playerOptionsPanel, typeof(GameObject), true);
            recordingPanel = (GameObject)EditorGUILayout.ObjectField("Recording Panel", recordingPanel, typeof(GameObject), true);
            environmentPanel = (GameObject)EditorGUILayout.ObjectField("Environment Panel", environmentPanel, typeof(GameObject), true);

            EditorGUILayout.Space();
            canvasScale = EditorGUILayout.FloatField("Canvas Scale", canvasScale);
            EditorGUILayout.HelpBox("0.001 = 30cm menu, 0.0005 = 15cm menu", MessageType.None);

            EditorGUILayout.Space(20);

            if (GUILayout.Button("Setup Quick Menu", GUILayout.Height(40)))
            {
                SetupMenu();
            }
        }

        private void SetupMenu()
        {
            // Load assets
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ROUNDED_SPRITE_PATH);
            Sprite circleSprite = AssetDatabase.LoadAssetAtPath<Sprite>(CIRCLE_SPRITE_PATH);
            TMP_FontAsset tmpFont = LoadTMPFont();

            // Root
            GameObject rootGO = new GameObject("QuickMenuSystem");
            Undo.RegisterCreatedObjectUndo(rootGO, "Create Quick Menu");
            var menuManager = rootGO.AddComponent<QuickMenuManager>();

            // UI Canvas - Standard pixel dimensions
            GameObject canvasGO = CreateCanvas("QuickMenuCanvas", rootGO.transform, canvasScale);

            // Menu Root
            GameObject menuRoot = CreateMenuRoot(canvasGO.transform, circleSprite, tmpFont);

            // Segment Button Prefab
            GameObject segmentPrefab = CreateSegmentButtonPrefab(roundedSprite, tmpFont);

            // Configure QuickMenuManager
            SerializedObject so = new SerializedObject(menuManager);
            so.FindProperty("m_MenuRoot").objectReferenceValue = menuRoot;
            so.FindProperty("m_SegmentContainer").objectReferenceValue = menuRoot.transform.Find("SegmentContainer");
            so.FindProperty("m_SegmentButtonPrefab").objectReferenceValue = segmentPrefab;
            so.FindProperty("m_MenuRadius").floatValue = 100f; // Standard pixel radius for radial layout

            // Add segments
            var segmentsList = so.FindProperty("m_MenuSegments");
            segmentsList.ClearArray();
            AddSegmentToList(segmentsList, "Vote", votingHostPanel);
            AddSegmentToList(segmentsList, "Settings", playerOptionsPanel);
            AddSegmentToList(segmentsList, "Record", recordingPanel);
            AddSegmentToList(segmentsList, "Environ", environmentPanel);

            so.ApplyModifiedProperties();

            // Save prefab
            SavePrefab(segmentPrefab, "Assets/VRMPAssets/Prefabs/UI/SegmentButton.prefab");

            Canvas.ForceUpdateCanvases();
            Selection.activeGameObject = rootGO;
            EditorUtility.DisplayDialog("Success", "Quick Menu Created!\nPress Numpad 1 or left Y button to toggle.\nLeft Menu opens the hand menu only.", "OK");
        }

        private TMP_FontAsset LoadTMPFont()
        {
            string[] fontPaths = new string[]
            {
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset",
                "Packages/com.unity.textmeshpro/Editor Resources/Fonts & Materials/LiberationSans SDF.asset"
            };

            foreach (string path in fontPaths)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null) return font;
            }

            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            }

            Debug.LogWarning("[QuickMenuSetup] No TMP font found.");
            return null;
        }

        private void AddSegmentToList(SerializedProperty list, string name, GameObject panel)
        {
            int idx = list.arraySize;
            list.InsertArrayElementAtIndex(idx);
            var element = list.GetArrayElementAtIndex(idx);
            element.FindPropertyRelative("Name").stringValue = name;
            element.FindPropertyRelative("PanelToToggle").objectReferenceValue = panel;
        }

        private GameObject CreateCanvas(string name, Transform parent, float scale)
        {
            GameObject canvasGO = new GameObject(name);
            canvasGO.transform.SetParent(parent);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();
            
            var rt = canvasGO.GetComponent<RectTransform>();
            // Standard pixel dimensions: 300x300 pixels
            // At scale 0.001: 300 * 0.001 = 0.3m = 30cm
            rt.sizeDelta = new Vector2(300, 300);
            rt.localScale = Vector3.one * scale;

            int uiLayer = LayerMask.NameToLayer("UI");
            canvasGO.layer = uiLayer;

            BoxCollider col = canvasGO.AddComponent<BoxCollider>();
            col.size = new Vector3(300, 300, 10);
            col.isTrigger = true;

            return canvasGO;
        }

        private GameObject CreateMenuRoot(Transform parent, Sprite circleSprite, TMP_FontAsset font)
        {
            GameObject menuRoot = new GameObject("MenuRoot");
            menuRoot.transform.SetParent(parent, false);
            var rt = menuRoot.AddComponent<RectTransform>();
            rt.anchoredPosition = Vector2.zero;
            // Standard: 280x280 pixels (fills 300px canvas with margin)
            rt.sizeDelta = new Vector2(280, 280);

            // Circular background
            var bg = new GameObject("Background");
            bg.transform.SetParent(menuRoot.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = PanelBackground;
            if (circleSprite != null)
            {
                bgImg.sprite = circleSprite;
                bgImg.preserveAspect = true;
            }
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            // Center label
            var center = new GameObject("CenterLabel");
            center.transform.SetParent(menuRoot.transform, false);
            var centerTmp = center.AddComponent<TextMeshProUGUI>();
            centerTmp.text = "MENU";
            centerTmp.fontSize = 24; // Standard font size
            centerTmp.fontStyle = FontStyles.Bold;
            centerTmp.alignment = TextAlignmentOptions.Center;
            centerTmp.color = Color.white;
            centerTmp.raycastTarget = false;
            if (font != null) centerTmp.font = font;
            var centerRt = center.GetComponent<RectTransform>();
            centerRt.anchoredPosition = Vector2.zero;
            centerRt.sizeDelta = new Vector2(100, 40);

            // Segment Container
            var container = new GameObject("SegmentContainer");
            container.transform.SetParent(menuRoot.transform, false);
            var containerRt = container.AddComponent<RectTransform>();
            containerRt.anchorMin = Vector2.zero;
            containerRt.anchorMax = Vector2.one;
            containerRt.offsetMin = Vector2.zero;
            containerRt.offsetMax = Vector2.zero;

            menuRoot.SetActive(false);
            return menuRoot;
        }

        private GameObject CreateSegmentButtonPrefab(Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject prefab = new GameObject("SegmentButton");
            var rt = prefab.AddComponent<RectTransform>();
            // Standard button size: 60x60 pixels (~6cm at 0.001 scale)
            rt.sizeDelta = new Vector2(60, 60);

            var img = prefab.AddComponent<Image>();
            img.color = ButtonAction;
            img.raycastTarget = true;
            if (roundedSprite != null)
            {
                img.sprite = roundedSprite;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 4f;
            }

            var btn = prefab.AddComponent<Button>();
            btn.targetGraphic = img;
            ConfigureButtonColors(btn, ButtonAction);

            var col = prefab.AddComponent<BoxCollider>();
            col.size = new Vector3(60, 60, 5);

            // Label with proper font
            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(prefab.transform, false);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text = "Menu";
            label.fontSize = 12; // Standard readable font size
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8;
            label.fontSizeMax = 14;
            if (font != null) label.font = font;
            
            var labelRt = labelGO.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(4, 4);
            labelRt.offsetMax = new Vector2(-4, -4);

            return prefab;
        }

        private void ConfigureButtonColors(Button btn, Color normalColor)
        {
            btn.enabled = false;
            ColorBlock colors = btn.colors;
            colors.normalColor = normalColor;
            colors.highlightedColor = ButtonHighlight;
            colors.pressedColor = ButtonPressed;
            colors.selectedColor = normalColor;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            Navigation nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;
            btn.enabled = true;
        }

        private void SavePrefab(GameObject go, string path)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(path))
            {
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
        }
    }
}
#endif
