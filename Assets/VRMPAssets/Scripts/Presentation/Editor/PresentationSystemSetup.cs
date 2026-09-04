#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

namespace XRMultiplayer.Presentation.Editor
{
    /// <summary>
    /// Editor utility to quickly set up the Firebase Presentation System in a scene.
    /// Creates UI matching the existing Drive UI style - code is DISPLAYED in VR.
    /// </summary>
    public class PresentationSystemSetup : EditorWindow
    {
        [MenuItem("XR Multiplayer/Setup Presentation System")]
        public static void ShowWindow()
        {
            GetWindow<PresentationSystemSetup>("Presentation Setup");
        }

        private Renderer tvRenderer;
        private RawImage tvRawImage;
        
        // Theme colors matching DriveUISetup
        private static readonly Color PanelBgColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
        private static readonly Color ButtonColor = new Color(0.125f, 0.588f, 0.953f, 1f); // Blue
        private static readonly Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
        private static readonly Color CodeColor = new Color(0.2f, 1f, 0.2f, 1f); // Bright Green
        private static readonly Color TextColor = Color.white;
        private static readonly Color SubtextColor = new Color(0.8f, 0.8f, 0.8f);
        
        private void OnGUI()
        {
            GUILayout.Label("Firebase Presentation System Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This will create all necessary GameObjects for the Firebase Presentation system.\n\n" +
                "1. FirebaseStorageManager (DontDestroyOnLoad)\n" +
                "2. PresentationSystem (TV Manager + Network Manager)\n" +
                "3. Presentation UI Canvas (VR-ready)\n\n" +
                "The VR device will DISPLAY a code for the user to enter on the web.",
                MessageType.Info);
            
            EditorGUILayout.Space();
            
            GUILayout.Label("TV Display (Optional)", EditorStyles.boldLabel);
            tvRenderer = (Renderer)EditorGUILayout.ObjectField("TV Screen Renderer", tvRenderer, typeof(Renderer), true);
            tvRawImage = (RawImage)EditorGUILayout.ObjectField("TV RawImage (UI)", tvRawImage, typeof(RawImage), true);
            
            EditorGUILayout.Space();
            
            if (GUILayout.Button("Create Presentation System", GUILayout.Height(40)))
            {
                CreatePresentationSystem();
            }
            
            EditorGUILayout.Space();
            GUILayout.Label("After Setup:", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Position the UI Canvas where you want it in VR\n" +
                "2. Assign TV screen renderer if not done above\n" +
                "3. User clicks 'Generate Code' in VR\n" +
                "4. User enters code at xr-meeting-hub.web.app/pair",
                MessageType.None);
        }

        private void CreatePresentationSystem()
        {
            // Load theme sprite
            Sprite themeSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/VRMPAssets/Textures/UI/Round Radius 10.png");
            
            // 1. Create Firebase Storage Manager (persistent)
            var storageManager = FindFirstObjectByType<FirebaseStorageManager>();
            if (storageManager == null)
            {
                var storageGO = new GameObject("[FirebaseStorageManager]");
                storageManager = storageGO.AddComponent<FirebaseStorageManager>();
                Undo.RegisterCreatedObjectUndo(storageGO, "Create FirebaseStorageManager");
                Debug.Log("[PresentationSetup] Created FirebaseStorageManager");
            }
            
            // 2. Create Presentation System (TV + Network)
            var existingSystem = FindFirstObjectByType<PresentationNetworkManager>();
            GameObject presentationGO;
            PresentationTVManager tvManager;
            PresentationNetworkManager networkManager;
            
            if (existingSystem != null)
            {
                presentationGO = existingSystem.gameObject;
                tvManager = presentationGO.GetComponent<PresentationTVManager>();
                networkManager = existingSystem;
                Debug.Log("[PresentationSetup] Using existing PresentationSystem");
            }
            else
            {
                presentationGO = new GameObject("PresentationSystem");
                tvManager = presentationGO.AddComponent<PresentationTVManager>();
                networkManager = presentationGO.AddComponent<PresentationNetworkManager>();
                Undo.RegisterCreatedObjectUndo(presentationGO, "Create PresentationSystem");
                Debug.Log("[PresentationSetup] Created PresentationSystem");
            }
            
            // 3. Create UI Canvas (VR-ready, matching Drive UI style)
            var canvasGO = new GameObject("PresentationUI_Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>(); // For XR
            
            // Set VR-friendly transform
            canvasGO.transform.position = new Vector3(0, 1.5f, 2f);
            canvasGO.transform.rotation = Quaternion.Euler(0, 180, 0);
            
            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(800, 500);
            canvasRect.localScale = Vector3.one * 0.001f;
            
            // Add Box Collider for XR interaction
            var col = canvasGO.AddComponent<BoxCollider>();
            col.size = new Vector3(800, 500, 0.1f);
            col.isTrigger = true;
            
            // Add UI Manager
            var uiManager = canvasGO.AddComponent<PresentationUIManager>();
            
            // === CREATE LOGIN PANEL (Code Display) ===
            var loginPanel = CreatePanel(canvasGO.transform, "LoginPanel", themeSprite);
            var loginRT = loginPanel.GetComponent<RectTransform>();
            loginRT.anchorMin = new Vector2(0.05f, 0.1f);
            loginRT.anchorMax = new Vector2(0.95f, 0.95f);
            loginRT.offsetMin = Vector2.zero;
            loginRT.offsetMax = Vector2.zero;
            
            // Title
            var titleObj = CreateText(loginPanel.transform, "Title", "Firebase Presentation", 42, TextColor);
            var titleRT = titleObj.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0, 0.85f);
            titleRT.anchorMax = new Vector2(1, 1);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;
            
            // Instructions
            var instrObj = CreateText(loginPanel.transform, "Instructions", 
                "Click 'Generate Code' to start\nEnter the code at xr-meeting-hub.web.app/pair", 
                22, SubtextColor);
            var instrRT = instrObj.GetComponent<RectTransform>();
            instrRT.anchorMin = new Vector2(0.05f, 0.6f);
            instrRT.anchorMax = new Vector2(0.95f, 0.85f);
            instrRT.offsetMin = Vector2.zero;
            instrRT.offsetMax = Vector2.zero;
            
            // Code Display (Large green text)
            var codeDisplayObj = CreateText(loginPanel.transform, "CodeDisplay", "------", 72, CodeColor);
            var codeTMP = codeDisplayObj.GetComponent<TextMeshProUGUI>();
            codeTMP.fontStyle = FontStyles.Bold;
            codeTMP.characterSpacing = 15; // More spacing between chars
            var codeRT = codeDisplayObj.GetComponent<RectTransform>();
            codeRT.anchorMin = new Vector2(0.1f, 0.35f);
            codeRT.anchorMax = new Vector2(0.9f, 0.6f);
            codeRT.offsetMin = Vector2.zero;
            codeRT.offsetMax = Vector2.zero;
            
            // Close button (top-right)
            var closeButton = CreateButton(loginPanel.transform, "CloseAuthButton", "X", themeSprite);
            var closeRT = closeButton.GetComponent<RectTransform>();
            closeRT.anchorMin = new Vector2(1f, 1f);
            closeRT.anchorMax = new Vector2(1f, 1f);
            closeRT.pivot = new Vector2(1f, 1f);
            closeRT.anchoredPosition = new Vector2(-16f, -16f);
            closeRT.sizeDelta = new Vector2(52f, 52f);

            // Generate Code Button
            var generateButton = CreateButton(loginPanel.transform, "GenerateCodeButton", "📱 Generate Code", themeSprite);
            var generateRT = generateButton.GetComponent<RectTransform>();
            generateRT.anchorMin = new Vector2(0.25f, 0.12f);
            generateRT.anchorMax = new Vector2(0.75f, 0.28f);
            generateRT.offsetMin = Vector2.zero;
            generateRT.offsetMax = Vector2.zero;
            
            // Status Text on login panel
            var loginStatusObj = CreateText(loginPanel.transform, "StatusText", "Ready to connect", 18, SubtextColor);
            var loginStatusRT = loginStatusObj.GetComponent<RectTransform>();
            loginStatusRT.anchorMin = new Vector2(0.1f, 0.02f);
            loginStatusRT.anchorMax = new Vector2(0.9f, 0.1f);
            loginStatusRT.offsetMin = Vector2.zero;
            loginStatusRT.offsetMax = Vector2.zero;
            
            // === CREATE FILE LIST PANEL ===
            var filePanel = CreatePanel(canvasGO.transform, "FilePanel", themeSprite);
            var filePanelRT = filePanel.GetComponent<RectTransform>();
            filePanelRT.anchorMin = new Vector2(0.02f, 0.02f);
            filePanelRT.anchorMax = new Vector2(0.98f, 0.98f);
            filePanelRT.offsetMin = Vector2.zero;
            filePanelRT.offsetMax = Vector2.zero;
            filePanel.SetActive(false); // Hidden until logged in
            
            // File Panel Title
            var fileTitleObj = CreateText(filePanel.transform, "Title", "My Documents", 36, TextColor);
            var fileTitleRT = fileTitleObj.GetComponent<RectTransform>();
            fileTitleRT.anchorMin = new Vector2(0, 0.9f);
            fileTitleRT.anchorMax = new Vector2(0.7f, 1);
            fileTitleRT.offsetMin = new Vector2(20, 0);
            fileTitleRT.offsetMax = Vector2.zero;
            fileTitleObj.GetComponent<TextMeshProUGUI>().alignment = TextAlignmentOptions.MidlineLeft;
            
            // Refresh Button
            var refreshButton = CreateButton(filePanel.transform, "RefreshButton", "🔄 Refresh", themeSprite);
            var refreshRT = refreshButton.GetComponent<RectTransform>();
            refreshRT.anchorMin = new Vector2(0.75f, 0.91f);
            refreshRT.anchorMax = new Vector2(0.98f, 0.99f);
            refreshRT.offsetMin = Vector2.zero;
            refreshRT.offsetMax = Vector2.zero;
            
            // Create Scroll View for file list
            var scrollViewGO = CreateScrollView(filePanel.transform, "FileScrollView", themeSprite);
            var scrollViewRT = scrollViewGO.GetComponent<RectTransform>();
            scrollViewRT.anchorMin = new Vector2(0.02f, 0.15f);
            scrollViewRT.anchorMax = new Vector2(0.98f, 0.88f);
            scrollViewRT.offsetMin = Vector2.zero;
            scrollViewRT.offsetMax = Vector2.zero;
            
            var content = scrollViewGO.transform.Find("Viewport/Content");
            
            // Navigation Panel
            var navPanel = CreatePanel(filePanel.transform, "NavigationPanel", themeSprite);
            var navPanelRT = navPanel.GetComponent<RectTransform>();
            navPanelRT.anchorMin = new Vector2(0.2f, 0.02f);
            navPanelRT.anchorMax = new Vector2(0.8f, 0.12f);
            navPanelRT.offsetMin = Vector2.zero;
            navPanelRT.offsetMax = Vector2.zero;
            
            var prevButton = CreateButton(navPanel.transform, "PrevButton", "◀ Prev", themeSprite);
            var prevRT = prevButton.GetComponent<RectTransform>();
            prevRT.anchorMin = new Vector2(0.02f, 0.1f);
            prevRT.anchorMax = new Vector2(0.3f, 0.9f);
            prevRT.offsetMin = Vector2.zero;
            prevRT.offsetMax = Vector2.zero;
            
            var pageText = CreateText(navPanel.transform, "PageText", "", 24, TextColor);
            var pageTextRT = pageText.GetComponent<RectTransform>();
            pageTextRT.anchorMin = new Vector2(0.35f, 0.1f);
            pageTextRT.anchorMax = new Vector2(0.65f, 0.9f);
            pageTextRT.offsetMin = Vector2.zero;
            pageTextRT.offsetMax = Vector2.zero;
            
            var nextButton = CreateButton(navPanel.transform, "NextButton", "Next ▶", themeSprite);
            var nextRT = nextButton.GetComponent<RectTransform>();
            nextRT.anchorMin = new Vector2(0.7f, 0.1f);
            nextRT.anchorMax = new Vector2(0.98f, 0.9f);
            nextRT.offsetMin = Vector2.zero;
            nextRT.offsetMax = Vector2.zero;
            
            // Status Text in file panel
            var statusText = CreateText(filePanel.transform, "FileStatusText", "Ready", 18, SubtextColor);
            var statusTextRT = statusText.GetComponent<RectTransform>();
            statusTextRT.anchorMin = new Vector2(0.02f, 0.02f);
            statusTextRT.anchorMax = new Vector2(0.2f, 0.12f);
            statusTextRT.offsetMin = Vector2.zero;
            statusTextRT.offsetMax = Vector2.zero;
            statusText.GetComponent<TextMeshProUGUI>().alignment = TextAlignmentOptions.MidlineLeft;
            
            // Use existing DriveFileButton prefab (known to work) or create new
            GameObject buttonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VRMPAssets/Prefabs/DriveFileButton.prefab");
            if (buttonPrefab == null)
            {
                Debug.Log("[PresentationSetup] DriveFileButton.prefab not found, creating new button prefab");
                buttonPrefab = CreateButtonPrefab(themeSprite);
            }
            else
            {
                Debug.Log("[PresentationSetup] Using existing DriveFileButton.prefab (known to work)");
            }
            
            // Wire up references via SerializedObject
            var so = new SerializedObject(uiManager);
            so.FindProperty("storageManager").objectReferenceValue = storageManager;
            so.FindProperty("networkManager").objectReferenceValue = networkManager;
            so.FindProperty("tvManager").objectReferenceValue = tvManager;
            so.FindProperty("loginPanel").objectReferenceValue = loginPanel;
            so.FindProperty("codeDisplayText").objectReferenceValue = codeTMP;
            so.FindProperty("generateCodeButton").objectReferenceValue = generateButton.GetComponent<Button>();
            so.FindProperty("closeAuthPanelButton").objectReferenceValue = closeButton.GetComponent<Button>();
            so.FindProperty("loginInstructions").objectReferenceValue = instrObj.GetComponent<TextMeshProUGUI>();
            so.FindProperty("filePanel").objectReferenceValue = filePanel;
            so.FindProperty("fileListContainer").objectReferenceValue = content;
            so.FindProperty("fileButtonPrefab").objectReferenceValue = buttonPrefab;
            so.FindProperty("refreshButton").objectReferenceValue = refreshButton.GetComponent<Button>();
            so.FindProperty("nextButton").objectReferenceValue = nextButton.GetComponent<Button>();
            so.FindProperty("prevButton").objectReferenceValue = prevButton.GetComponent<Button>();
            so.FindProperty("pageText").objectReferenceValue = pageText.GetComponent<TextMeshProUGUI>();
            so.FindProperty("statusText").objectReferenceValue = loginStatusObj.GetComponent<TextMeshProUGUI>();
            so.ApplyModifiedProperties();
            
            Undo.RegisterCreatedObjectUndo(canvasGO, "Create PresentationUI");
            
            Selection.activeGameObject = canvasGO;
            Debug.Log("[PresentationSetup] ✓ Presentation System created!");
            EditorUtility.DisplayDialog("Success", 
                "Presentation System created!\n\n" +
                "Flow:\n" +
                "1. User clicks 'Generate Code' in VR\n" +
                "2. Code is displayed (e.g., 'ABC123')\n" +
                "3. User goes to web and enters code\n" +
                "4. VR auto-authenticates\n\n" +
                "Position the UI Canvas where you want in your scene.", 
                "OK");
        }
        
        private GameObject CreatePanel(Transform parent, string name, Sprite themeSprite)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            
            var img = go.AddComponent<Image>();
            img.color = PanelBgColor;
            if (themeSprite != null)
            {
                img.sprite = themeSprite;
                img.type = Image.Type.Sliced;
            }
            
            return go;
        }
        
        private GameObject CreateText(Transform parent, string name, string text, int fontSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontStyle = fontSize >= 36 ? FontStyles.Bold : FontStyles.Normal;
            
            return go;
        }
        
        private GameObject CreateButton(Transform parent, string name, string text, Sprite themeSprite)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            
            var img = go.AddComponent<Image>();
            img.color = ButtonColor;
            if (themeSprite != null)
            {
                img.sprite = themeSprite;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 4f;
            }
            
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            
            ColorBlock colors = btn.colors;
            colors.normalColor = ButtonColor;
            colors.highlightedColor = ButtonHighlight;
            colors.pressedColor = new Color(0.1f, 0.5f, 0.85f, 1f);
            colors.selectedColor = ButtonColor;
            btn.colors = colors;
            
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRT = textGO.AddComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = new Vector2(5, 5);
            textRT.offsetMax = new Vector2(-5, -5);
            
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 22;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontStyle = FontStyles.Bold;
            
            return go;
        }
        
        private GameObject CreateScrollView(Transform parent, string name, Sprite themeSprite)
        {
            var scrollGO = new GameObject(name);
            scrollGO.transform.SetParent(parent, false);
            
            var scrollRect = scrollGO.AddComponent<ScrollRect>();
            var scrollImg = scrollGO.AddComponent<Image>();
            scrollImg.color = new Color(0.05f, 0.05f, 0.05f, 0.5f);
            if (themeSprite != null)
            {
                scrollImg.sprite = themeSprite;
                scrollImg.type = Image.Type.Sliced;
            }
            
            // Viewport
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollGO.transform, false);
            var viewportRT = viewport.AddComponent<RectTransform>();
            viewportRT.anchorMin = Vector2.zero;
            viewportRT.anchorMax = Vector2.one;
            viewportRT.offsetMin = new Vector2(10, 10);
            viewportRT.offsetMax = new Vector2(-10, -10);
            viewport.AddComponent<Image>().color = Color.clear;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            
            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRT = content.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0, 1);
            contentRT.anchorMax = new Vector2(1, 1);
            contentRT.pivot = new Vector2(0.5f, 1);
            contentRT.offsetMin = Vector2.zero;
            contentRT.offsetMax = Vector2.zero;
            
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8;
            vlg.padding = new RectOffset(5, 5, 5, 5);
            vlg.childControlHeight = false;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            
            var csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            
            scrollRect.viewport = viewportRT;
            scrollRect.content = contentRT;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 50;
            
            return scrollGO;
        }
        
        private GameObject CreateButtonPrefab(Sprite themeSprite)
        {
            // Check if we have the existing DriveFileButton prefab
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VRMPAssets/Prefabs/DriveFileButton.prefab");
            if (existing != null)
            {
                Debug.Log("[PresentationSetup] Using existing DriveFileButton prefab");
                return existing;
            }
            
            // Create a simple button prefab
            var go = new GameObject("FileButton");
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(350, 60);
            
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.15f, 0.2f, 1f);
            if (themeSprite != null)
            {
                img.sprite = themeSprite;
                img.type = Image.Type.Sliced;
            }
            
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            
            ColorBlock colors = btn.colors;
            colors.normalColor = new Color(0.15f, 0.15f, 0.2f, 1f);
            colors.highlightedColor = new Color(0.25f, 0.25f, 0.35f, 1f);
            colors.pressedColor = new Color(0.1f, 0.1f, 0.15f, 1f);
            btn.colors = colors;
            
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 60;
            le.preferredHeight = 60;
            
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRT = textGO.AddComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = new Vector2(15, 0);
            textRT.offsetMax = new Vector2(-15, 0);
            
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = "Document Name";
            tmp.fontSize = 22;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            
            go.SetActive(false);
            
            // Save as prefab
            string path = "Assets/VRMPAssets/Prefabs/PresentationFileButton.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            DestroyImmediate(go);
            
            Debug.Log($"[PresentationSetup] Created button prefab at {path}");
            return prefab;
        }
    }
}
#endif
