#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Editor wizard to help set up the Environment Switching system in your scene.
    /// </summary>
    public class EnvironmentSetupWizard : EditorWindow
    {
        private GameObject selectedConferenceRoom;
        private GameObject selectedPlatform;

        [MenuItem("Tools/XR Multiplayer/Environment Setup Wizard")]
        public static void ShowWindow()
        {
            GetWindow<EnvironmentSetupWizard>("Environment Setup");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Environment Switching Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // ONE BUTTON TO DO EVERYTHING
            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);
            if (GUILayout.Button("🚀 SETUP EVERYTHING", GUILayout.Height(40)))
            {
                SetupEverything();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Click the button above to create all environment components at once!\n" +
                "Or use individual buttons below for manual setup.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Individual Steps (Optional)", EditorStyles.boldLabel);

            if (GUILayout.Button("1. Create Environment Manager"))
                CreateEnvironmentManager();

            if (GUILayout.Button("2. Create Planet Ground"))
                CreatePlanetGround();

            if (GUILayout.Button("3. Create Meeting Platform"))
                CreateCementPlatform();

            if (GUILayout.Button("4. Create Fade Overlay"))
                CreateFadeOverlay();

            if (GUILayout.Button("5. Create Switch Button"))
                CreateEnvironmentSwitchButton();

            EditorGUILayout.Space();
            if (GUILayout.Button("Auto-find & Link Objects"))
                AutoFindAndLink();
        }

        private void SetupEverything()
        {
            // Create all components silently
            // Create all components silently
            
            // 1. Environment Manager (with NetworkObject for sync)
            var manager = FindFirstObjectByType<EnvironmentManager>();
            if (manager == null)
            {
                var managerGO = new GameObject("EnvironmentManager");
                manager = managerGO.AddComponent<EnvironmentManager>();
                
                // Add NetworkObject for multiplayer sync
                if (!managerGO.TryGetComponent<Unity.Netcode.NetworkObject>(out _))
                {
                    managerGO.AddComponent<Unity.Netcode.NetworkObject>();
                }
                
                Undo.RegisterCreatedObjectUndo(managerGO, "Create EnvironmentManager");
            }

            // 2. Planet Ground
            var ground = GameObject.Find("PlanetGround");
            if (ground == null)
            {
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "PlanetGround";
                ground.transform.position = Vector3.zero;
                ground.transform.localScale = new Vector3(100, 1, 100);
                ground.SetActive(false);
                Undo.RegisterCreatedObjectUndo(ground, "Create PlanetGround");
            }

            // 3. Meeting Platform
            var platform = FindFirstObjectByType<MeetingPlatformBuilder>();
            if (platform == null)
            {
                var platformGO = new GameObject("MeetingPlatform");
                platform = platformGO.AddComponent<MeetingPlatformBuilder>();
                
                var table = GameObject.Find("Table");
                if (table != null)
                    platformGO.transform.position = new Vector3(table.transform.position.x, 0, table.transform.position.z);
                else
                    platformGO.transform.position = new Vector3(0, 0, -3.3f);
                
                // Load concrete material
                var concreteMat = AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/Samples/XR Interaction Toolkit/3.2.0/Starter Assets/DemoSceneAssets/Materials/Concrete Grey.mat");
                
                // Set material in serialized properties first
                if (concreteMat != null)
                {
                    var serializedPlatform = new SerializedObject(platform);
                    serializedPlatform.FindProperty("m_PlatformMaterial").objectReferenceValue = concreteMat;
                    serializedPlatform.FindProperty("m_PillarMaterial").objectReferenceValue = concreteMat;
                    serializedPlatform.FindProperty("m_RimMaterial").objectReferenceValue = concreteMat;
                    serializedPlatform.ApplyModifiedProperties();
                }
                
                // Generate platform (reads the material properties)
                platform.GeneratePlatform();
                
                // Also apply material directly to all child renderers as backup
                if (concreteMat != null)
                {
                    var renderers = platformGO.GetComponentsInChildren<Renderer>();
                    foreach (var r in renderers)
                    {
                        r.sharedMaterial = concreteMat;
                    }
                }
                
                Undo.RegisterCreatedObjectUndo(platformGO, "Create MeetingPlatform");
            }

            // 4. Fade Overlay
            var fadeOverlay = FindFirstObjectByType<FadeOverlay>();
            CanvasGroup fadeCanvasGroup = null;
            if (fadeOverlay == null)
            {
                var canvasGO = new GameObject("FadeOverlayCanvas");
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 999;
                canvasGO.AddComponent<CanvasScaler>();
                canvasGO.AddComponent<GraphicRaycaster>();

                var imageGO = new GameObject("FadeImage");
                imageGO.transform.SetParent(canvasGO.transform, false);
                var rect = imageGO.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                var image = imageGO.AddComponent<Image>();
                image.color = Color.black;
                image.raycastTarget = false;

                fadeCanvasGroup = imageGO.AddComponent<CanvasGroup>();
                fadeCanvasGroup.alpha = 0;
                fadeCanvasGroup.blocksRaycasts = false;

                imageGO.AddComponent<FadeOverlay>();
                imageGO.SetActive(false);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create FadeOverlay");
            }
            else
            {
                fadeCanvasGroup = fadeOverlay.GetComponent<CanvasGroup>();
            }

            // 5. Switch Button
            var switchUI = FindFirstObjectByType<EnvironmentSwitcherUI>();
            if (switchUI == null)
            {
                CreateEnvironmentSwitchButton();
            }

            // AUTO-LINK EVERYTHING
            AutoLinkToManager(manager, ground, platform, fadeCanvasGroup);

            // Add default environment presets
            AddDefaultPresetsToManager(manager);

            Selection.activeGameObject = manager.gameObject;

            EditorUtility.DisplayDialog("✅ Setup Complete!", 
                "All environment components created and linked!\n\n" +
                "Created:\n" +
                "• EnvironmentManager\n" +
                "• PlanetGround (1000m terrain)\n" +
                "• MeetingPlatform (3D with pillars & lights)\n" +
                "• FadeOverlay\n" +
                "• Switch Button\n\n" +
                "Next: Assign ground materials for each environment in the EnvironmentManager Inspector.", 
                "OK");
        }

        private void AutoLinkToManager(EnvironmentManager manager, GameObject ground, MeetingPlatformBuilder platform, CanvasGroup fadeGroup)
        {
            if (manager == null) return;

            var serialized = new SerializedObject(manager);

            // Find and assign Room
            var room = GameObject.Find("Room");
            if (room != null)
            {
                var roomProp = serialized.FindProperty("m_ConferenceRoom");
                if (roomProp != null) roomProp.objectReferenceValue = room;
            }

            // Assign Planet Ground
            if (ground != null)
            {
                var groundProp = serialized.FindProperty("m_PlanetGround");
                if (groundProp != null) groundProp.objectReferenceValue = ground;

                var rendererProp = serialized.FindProperty("m_GroundRenderer");
                if (rendererProp != null) rendererProp.objectReferenceValue = ground.GetComponent<Renderer>();
            }

            // Assign Platform
            if (platform != null)
            {
                var platformProp = serialized.FindProperty("m_CementPlatform");
                if (platformProp != null) platformProp.objectReferenceValue = platform.gameObject;
            }

            // Assign Fade Overlay
            if (fadeGroup != null)
            {
                var fadeProp = serialized.FindProperty("m_FadeOverlay");
                if (fadeProp != null) fadeProp.objectReferenceValue = fadeGroup;
            }

            serialized.ApplyModifiedProperties();
        }

        private void AddDefaultPresetsToManager(EnvironmentManager manager)
        {
            var serialized = new SerializedObject(manager);
            var envProp = serialized.FindProperty("m_Environments");
            
            if (envProp != null && envProp.arraySize == 0)
            {
                // Find skybox materials
                var skyboxes = new string[] {
                    "Assets/SkySeries Freebie/UnearthlyRed.mat",
                    "Assets/SkySeries Freebie/6sidedCosmicCoolCloud.mat",
                    "Assets/SkySeries Freebie/UnderTheSea4k.mat",
                    "Assets/SkySeries Freebie/PlanetaryEarth.mat"
                };

                var names = new string[] { "Mars Colony", "Cosmic Nebula", "Underwater Base", "Space Station" };
                var colors = new Color[] {
                    new Color(0.8f, 0.4f, 0.2f),
                    new Color(0.4f, 0.2f, 0.6f),
                    new Color(0.2f, 0.5f, 0.7f),
                    new Color(0.6f, 0.7f, 0.9f)
                };
                
                // Ground scales: large for planets, small for space station
                var groundScales = new float[] { 100f, 100f, 100f, 15f };

                for (int i = 0; i < 4; i++)
                {
                    envProp.InsertArrayElementAtIndex(i);
                    var element = envProp.GetArrayElementAtIndex(i);
                    
                    element.FindPropertyRelative("Name").stringValue = names[i];
                    element.FindPropertyRelative("AmbientColor").colorValue = colors[i];
                    element.FindPropertyRelative("AmbientIntensity").floatValue = 1f;
                    element.FindPropertyRelative("GroundScale").floatValue = groundScales[i];

                    var skybox = AssetDatabase.LoadAssetAtPath<Material>(skyboxes[i]);
                    if (skybox != null)
                        element.FindPropertyRelative("SkyboxMaterial").objectReferenceValue = skybox;
                }

                serialized.ApplyModifiedProperties();
            }
        }

        private void AutoFindAndLink()
        {
            var manager = FindFirstObjectByType<EnvironmentManager>();
            var ground = GameObject.Find("PlanetGround");
            var platform = FindFirstObjectByType<MeetingPlatformBuilder>();
            var fade = FindFirstObjectByType<FadeOverlay>();

            if (manager != null)
            {
                AutoLinkToManager(manager, ground, platform, fade?.GetComponent<CanvasGroup>());
                Selection.activeGameObject = manager.gameObject;
                EditorUtility.DisplayDialog("Linked!", "All found objects linked to EnvironmentManager.", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Not Found", "Create EnvironmentManager first.", "OK");
            }
        }

        private void CreateEnvironmentManager()
        {
            var existing = FindFirstObjectByType<EnvironmentManager>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Already Exists", 
                    "EnvironmentManager already exists in the scene.", "OK");
                return;
            }

            var go = new GameObject("EnvironmentManager");
            go.AddComponent<EnvironmentManager>();
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create EnvironmentManager");

            EditorUtility.DisplayDialog("Created", 
                "EnvironmentManager created!\n\nNow assign your scene references in the Inspector.", "OK");
        }

        private void CreatePlanetGround()
        {
            var existing = GameObject.Find("PlanetGround");
            if (existing != null)
            {
                Selection.activeGameObject = existing;
                EditorUtility.DisplayDialog("Already Exists", 
                    "PlanetGround already exists in the scene.", "OK");
                return;
            }

            // Create a large plane for the planet ground at Y=0
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "PlanetGround";
            go.transform.position = new Vector3(0, 0, 0); // Exactly at ground level
            go.transform.localScale = new Vector3(100, 1, 100); // 1000m x 1000m - vast terrain
            go.SetActive(false); // Start hidden

            // Add a tag for easy reference
            go.tag = "Ground";

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create PlanetGround");

            EditorUtility.DisplayDialog("Created", 
                "PlanetGround created (1000m x 1000m)!\n\n" +
                "Position: Y=0 (ground level)\n" +
                "Assign a ground material in the Renderer component.\n" +
                "Then link it to the EnvironmentManager.", "OK");
        }

        private void CreateCementPlatform()
        {
            var existing = FindFirstObjectByType<MeetingPlatformBuilder>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Already Exists", 
                    "Meeting Platform already exists in the scene.", "OK");
                return;
            }

            // Create the platform parent with the builder
            var go = new GameObject("MeetingPlatform");
            var builder = go.AddComponent<MeetingPlatformBuilder>();

            // Try to find and center on the table
            var table = GameObject.Find("Table");
            if (table != null)
            {
                go.transform.position = new Vector3(table.transform.position.x, 0, table.transform.position.z);
            }
            else
            {
                go.transform.position = new Vector3(0, 0, -3.3f); // Default position based on scene
            }

            // Generate the impressive platform
            builder.GeneratePlatform();

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Meeting Platform");

            EditorUtility.DisplayDialog("Created", 
                "Impressive Meeting Platform created!\n\n" +
                "Includes: Top surface, support pillars, edge rim, and corner lights.\n\n" +
                "Use the Inspector to customize dimensions and materials.\n" +
                "Right-click the component for 'Generate Platform' to rebuild.", "OK");
        }

        private void CreateFadeOverlay()
        {
            var existing = FindFirstObjectByType<FadeOverlay>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Already Exists", 
                    "FadeOverlay already exists in the scene.", "OK");
                return;
            }

            // Create Canvas
            var canvasGO = new GameObject("FadeOverlayCanvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999; // On top of everything

            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // Create fade image
            var imageGO = new GameObject("FadeImage");
            imageGO.transform.SetParent(canvasGO.transform, false);

            var rectTransform = imageGO.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = imageGO.AddComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = false;

            var canvasGroup = imageGO.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0;
            canvasGroup.blocksRaycasts = false;

            imageGO.AddComponent<FadeOverlay>();
            imageGO.SetActive(false);

            Selection.activeGameObject = imageGO;
            Undo.RegisterCreatedObjectUndo(canvasGO, "Create FadeOverlay");

            EditorUtility.DisplayDialog("Created", 
                "FadeOverlay Canvas created!\n\n" +
                "Link the FadeImage's CanvasGroup to the EnvironmentManager.", "OK");
        }

        private void AutoFindObjects()
        {
            // Try to find the Room object
            var room = GameObject.Find("Room");
            if (room != null)
            {
                selectedConferenceRoom = room;
                Debug.Log("Found Conference Room: " + room.name);
            }

            // Find EnvironmentManager and suggest assignments
            var manager = FindFirstObjectByType<EnvironmentManager>();
            if (manager != null)
            {
                Selection.activeGameObject = manager.gameObject;
                Debug.Log("Found EnvironmentManager. Assign references in Inspector.");
            }
        }

        private void CreateEnvironmentSwitchButton()
        {
            var existing = FindFirstObjectByType<EnvironmentSwitcherUI>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Already Exists", 
                    "Environment Switch Button already exists in the scene.", "OK");
                return;
            }

            // ========== DESIGN SYSTEM (matching other panels) ==========
            Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
            Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f); // Blue
            Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
            Color ButtonPressed = new Color(0.1f, 0.5f, 0.85f, 1f);
            Color TextPrimary = Color.white;
            
            // Load rounded corners sprite
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/VRMPAssets/Textures/UI/Round Radius 10.png");

            // ========== CREATE WORLD SPACE CANVAS ==========
            var canvasGO = new GameObject("EnvironmentSwitcher_UI");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 100f;

            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>(); 

            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(300, 400); // Taller for list
            canvasRect.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            canvasRect.position = new Vector3(2.5f, 1.3f, -3.5f);
            canvasRect.rotation = Quaternion.Euler(0, -30, 0);

            // Canvas collider for XR ray interaction
            var canvasCollider = canvasGO.AddComponent<BoxCollider>();
            canvasCollider.size = new Vector3(300, 400, 1f);
            canvasCollider.center = Vector3.zero;

            // ========== MAIN BACKGROUND ==========
            var panelGO = new GameObject("Background");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelImage = panelGO.AddComponent<Image>();
            panelImage.color = PanelBackground;
            if (roundedSprite != null)
            {
                panelImage.sprite = roundedSprite;
                panelImage.type = Image.Type.Sliced;
                panelImage.pixelsPerUnitMultiplier = 2f;
            }
            var panelRect = panelGO.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.sizeDelta = Vector2.zero;

            // ========== HEADER ==========
            var headerGO = new GameObject("Header");
            headerGO.transform.SetParent(panelGO.transform, false);
            var headerRect = headerGO.AddComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.sizeDelta = new Vector2(0, 50); // 50px header
            headerRect.anchoredPosition = Vector2.zero;

            var titleText = headerGO.AddComponent<TMPro.TextMeshProUGUI>();
            titleText.text = "Conference Room"; // Initial text
            titleText.fontSize = 20;
            titleText.fontStyle = TMPro.FontStyles.Bold;
            titleText.alignment = TMPro.TextAlignmentOptions.Center;
            titleText.color = TextPrimary;
            
            // ========== SCROLL VIEW ==========
            var scrollGO = new GameObject("ScrollView");
            scrollGO.transform.SetParent(panelGO.transform, false);
            var scrollRect = scrollGO.AddComponent<ScrollRect>();
            var scrollRectTrans = scrollGO.GetComponent<RectTransform>();
            scrollRectTrans.anchorMin = Vector2.zero;
            scrollRectTrans.anchorMax = Vector2.one;
            scrollRectTrans.offsetMax = new Vector2(0, -50); // Below header
            scrollRectTrans.offsetMin = new Vector2(0, 0);   // Bottom align

            // Viewport
            var viewportGO = new GameObject("Viewport");
            viewportGO.transform.SetParent(scrollGO.transform, false);
            var viewportImage = viewportGO.AddComponent<Image>(); // Needed for Mask
            viewportImage.raycastTarget = true; // Mask needs to be raycast target usually, or separate
            var viewportMask = viewportGO.AddComponent<Mask>();
            viewportMask.showMaskGraphic = false;
            
            var viewportRect = viewportGO.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportRect.pivot = new Vector2(0, 1);

            // Content
            var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1); // Top stretch
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 300); // Height driven by fitter

            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 10, 10);
            vlg.spacing = 5;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            var csf = contentGO.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 20;

            // ========== BUTTON TEMPLATE ==========
            var buttonGO = new GameObject("Btn_Template");
            buttonGO.transform.SetParent(contentGO.transform, false);
            
            var buttonLE = buttonGO.AddComponent<LayoutElement>();
            buttonLE.minHeight = 15; // Smaller buttons
            
            var buttonImage = buttonGO.AddComponent<Image>();
            buttonImage.color = ButtonAction;
            if (roundedSprite != null)
            {
                buttonImage.sprite = roundedSprite;
                panelImage.type = Image.Type.Sliced;
            }

            var button = buttonGO.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            
            var colors = button.colors;
            colors.normalColor = ButtonAction;
            colors.highlightedColor = ButtonHighlight;
            colors.pressedColor = ButtonPressed;
            colors.selectedColor = ButtonAction;
            button.colors = colors;

            var btnTextGO = new GameObject("Text");
            btnTextGO.transform.SetParent(buttonGO.transform, false);
            var btnTextRect = btnTextGO.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.offsetMin = new Vector2(10, 0);
            btnTextRect.offsetMax = new Vector2(-10, 0);
            
            var btnText = btnTextGO.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.text = "Environment Name";
            btnText.fontSize = 15; // Smaller font
            btnText.fontStyle = TMPro.FontStyles.Normal;
            btnText.alignment = TMPro.TextAlignmentOptions.Midline; // Vertically centered
            btnText.color = TextPrimary;

            // ========== SETUP COMPONENT ==========
            var switcherUI = canvasGO.AddComponent<EnvironmentSwitcherUI>();
            var serializedSwitcher = new SerializedObject(switcherUI);
            
            var containerProp = serializedSwitcher.FindProperty("m_ButtonsContainer");
            if (containerProp != null) containerProp.objectReferenceValue = contentGO.transform;
            
            var templateProp = serializedSwitcher.FindProperty("m_ButtonTemplate");
            if (templateProp != null) templateProp.objectReferenceValue = button;
            
            var labelProp = serializedSwitcher.FindProperty("m_CurrentEnvironmentLabel");
            if (labelProp != null) labelProp.objectReferenceValue = titleText;
            
            serializedSwitcher.ApplyModifiedProperties();

            // Force update
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);

            Selection.activeGameObject = canvasGO;
            Undo.RegisterCreatedObjectUndo(canvasGO, "Create Environment Switch UI (Scroll)");

            EditorUtility.DisplayDialog("Created", 
                "Environment Menu (Scrollable) created!\n\n" +
                "Features:\n" +
                "• Scroll View for unlimited environments\n" +
                "• Auto-sizing Content\n" +
                "• Masking to stay inside bounds\n\n" +
                "Position it where you want.", "OK");
        }
    }
}
#endif
