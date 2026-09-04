#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using TMPro;
using System.IO;
using UnityEngine.InputSystem;
using Unity.Netcode;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace XRMultiplayer.Drawing
{
    /// <summary>
    /// Editor script that sets up the entire Ray Drawing System in one click.
    /// Creates UI, manager, links references, and deletes old pen system.
    /// Uses the same styling as DriveUISetup and TranscriptionSystem.
    /// </summary>
    public class RayDrawingSetup : EditorWindow
    {
        // ========== DESIGN SYSTEM COLORS (exact match from DriveUISetup.cs) ==========
        private static readonly Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
        private static readonly Color ButtonNormal = new Color(0.18f, 0.18f, 0.18f, 1f); // Dark gray list button
        private static readonly Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f); // Blue action button
        private static readonly Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
        private static readonly Color ButtonPressed = new Color(0.1f, 0.5f, 0.85f, 1f);
        private static readonly Color TextPrimary = Color.white;
        
        // Drawing colors
        private static readonly Color BLUE_COLOR = new Color(0.2f, 0.5f, 1f);
        private static readonly Color GREEN_COLOR = new Color(0.2f, 0.9f, 0.3f);
        private static readonly Color RED_COLOR = new Color(1f, 0.3f, 0.3f);
        
        // Rounded corners sprite path (same as other UI)
        private static readonly string ROUNDED_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Round Radius 10.png";

        [MenuItem("Tools/VR Meeting/Setup Ray Drawing System")]
        public static void ShowWindow()
        {
            GetWindow<RayDrawingSetup>("Ray Drawing Setup");
        }

        private void OnGUI()
        {
            GUILayout.Label("Ray Drawing System Setup", EditorStyles.boldLabel);
            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "This will:\n" +
                "1. Create Drawing UI Canvas next to TV\n" +
                "2. Add RayDrawingManager to scene\n" +
                "3. Wire up all references\n" +
                "4. Delete old pen system files",
                MessageType.Info);

            GUILayout.Space(10);

            if (GUILayout.Button("Setup Ray Drawing System", GUILayout.Height(40)))
            {
                SetupRayDrawingSystem();
            }

            GUILayout.Space(10);

            if (GUILayout.Button("Delete Old Pen System Only", GUILayout.Height(30)))
            {
                DeleteOldPenSystem();
            }
        }

        [MenuItem("Tools/VR Meeting/Setup Ray Drawing System (Quick)")]
        public static void SetupRayDrawingSystem()
        {
            Debug.Log("[RayDrawingSetup] Starting setup...");

            // Step 1: Create Drawing Manager
            GameObject manager = CreateDrawingManager();

            // Step 2: Create Drawing UI
            GameObject ui = CreateDrawingUI();

            // Step 3: Wire up references
            WireUpReferences(manager, ui);

            // Step 4: Position UI next to TV
            PositionUINextToTV(ui);

            // Step 5: Delete old pen system
            DeleteOldPenSystem();

            // Step 6: Select the created objects
            Selection.activeGameObject = ui;

            Debug.Log("[RayDrawingSetup] Setup complete!");
            EditorUtility.DisplayDialog("Setup Complete",
                "Ray Drawing System has been set up successfully!\n\n" +
                "• RayDrawing_Manager created\n" +
                "• DrawingTools_UI created\n" +
                "• Old pen system deleted\n\n" +
                "Remember to:\n" +
                "1. Assign the controller's ray origin\n" +
                "2. Set the Drawing Surface layer on your TV screen\n" +
                "3. Add NetworkObject component if not present",
                "OK");
        }

        private static GameObject CreateDrawingManager()
        {
            // Check if already exists
            var existing = GameObject.Find("RayDrawing_Manager");
            if (existing != null)
            {
                Debug.Log("[RayDrawingSetup] Manager already exists, reusing...");
                return existing;
            }

            GameObject manager = new GameObject("RayDrawing_Manager");

            // Add required components
            var drawingManager = manager.AddComponent<RayDrawingManager>();

            // Try to add NetworkObject if Netcode is available
            if (!manager.TryGetComponent<NetworkObject>(out _))
            {
                manager.AddComponent<NetworkObject>();
            }

            // Try to find XR controller for ray origin
            var rightController = GameObject.Find("Right Controller");
            if (rightController == null)
                rightController = GameObject.Find("RightHand Controller");
            if (rightController == null)
                rightController = GameObject.Find("XR Controller Right");
            if (rightController == null)
                rightController = GameObject.Find("RightHand");

            if (rightController != null)
            {
                var rayInteractor = rightController.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>();
                if (rayInteractor != null)
                {
                    drawingManager.SetRayOrigin(rayInteractor.transform);
                    Debug.Log("[RayDrawingSetup] Found and assigned ray origin from XRRayInteractor");
                }
                else
                {
                    drawingManager.SetRayOrigin(rightController.transform);
                    Debug.Log("[RayDrawingSetup] Assigned right controller as ray origin");
                }
            }
            else
            {
                Debug.LogWarning("[RayDrawingSetup] Could not find XR controller. Please assign Ray Origin manually.");
            }

            // Try to find and assign input action
            string[] inputActionPaths = new string[]
            {
                "Assets/Samples/XR Interaction Toolkit/3.2.0/Starter Assets/XRI Default Input Actions.inputactions",
                "Assets/Samples/XR Interaction Toolkit/3.0.0/Starter Assets/XRI Default Input Actions.inputactions",
                "Assets/XRI Default Input Actions.inputactions"
            };

            foreach (string path in inputActionPaths)
            {
                var inputActions = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
                if (inputActions != null)
                {
                    // Find the Activate action on the right hand
                    var actionMap = inputActions.FindActionMap("XRI RightHand Interaction");
                    if (actionMap == null)
                        actionMap = inputActions.FindActionMap("XRI Right");
                    
                    if (actionMap != null)
                    {
                        var activateAction = actionMap.FindAction("Activate");
                        if (activateAction == null)
                            activateAction = actionMap.FindAction("Select");
                        
                        if (activateAction != null)
                        {
                            // Create InputActionReference
                            var actionRefs = AssetDatabase.LoadAllAssetsAtPath(path);
                            foreach (var asset in actionRefs)
                            {
                                if (asset is InputActionReference actionRef && actionRef.action == activateAction)
                                {
                                    drawingManager.SetDrawAction(actionRef);
                                    Debug.Log($"[RayDrawingSetup] Found and assigned input action: {actionRef.action.name}");
                                    break;
                                }
                            }
                        }
                    }
                    break;
                }
            }
            
            // If no input action found, log warning
            SerializedObject serializedManager = new SerializedObject(drawingManager);
            if (serializedManager.FindProperty("m_DrawAction").objectReferenceValue == null)
            {
                Debug.LogWarning("[RayDrawingSetup] Could not find input action automatically. Please assign Draw Action manually in Inspector.");
            }

            // Create line material
            string matPath = "Assets/VRMPAssets/Materials/DrawingLineMaterial.mat";
            Material lineMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (lineMat == null)
            {
                lineMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                lineMat.color = Color.white;
                
                // Ensure directory exists
                string dir = Path.GetDirectoryName(matPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                    
                AssetDatabase.CreateAsset(lineMat, matPath);
            }
            drawingManager.SetLineMaterial(lineMat);

            // Create drawing surface plane in front of TV
            GameObject drawingSurface = CreateDrawingSurface();
            if (drawingSurface != null)
            {
                BoxCollider surfaceCollider = drawingSurface.GetComponent<BoxCollider>();
                if (surfaceCollider != null)
                {
                    drawingManager.SetDrawingSurface(surfaceCollider);
                    Debug.Log("[RayDrawingSetup] Created and assigned drawing surface in front of TV");
                }
            }

            Undo.RegisterCreatedObjectUndo(manager, "Create Ray Drawing Manager");

            return manager;
        }

        private static GameObject CreateDrawingSurface()
        {
            // Check if already exists
            var existing = GameObject.Find("DrawingSurface");
            if (existing != null)
            {
                Debug.Log("[RayDrawingSetup] Drawing surface already exists, reusing...");
                return existing;
            }

            // Try to find TV/DriveTVManager in scene to position the surface
            var tvManager = Object.FindFirstObjectByType<XRMultiplayer.Presentation.PresentationTVManager>();
            
            // Create invisible drawing surface
            GameObject surface = new GameObject("DrawingSurface");
            
            // Add BoxCollider for raycast detection
            BoxCollider col = surface.AddComponent<BoxCollider>();
            col.size = new Vector3(2f, 1.2f, 0.01f); // Matches typical TV screen size
            col.isTrigger = true; // Make it a trigger so it doesn't interfere with physics
            
            // Add visualizer to show bounds in editor (green quad, corners, front direction)
            surface.AddComponent<DrawingSurfaceVisualizer>();
            
            if (tvManager != null)
            {
                // Position in front of TV screen
                surface.transform.position = tvManager.transform.position + tvManager.transform.forward * 0.02f;
                surface.transform.rotation = tvManager.transform.rotation;
                
                // Try to match TV screen size if we can find renderers
                var renderers = tvManager.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    foreach (var r in renderers)
                    {
                        bounds.Encapsulate(r.bounds);
                    }
                    col.size = new Vector3(bounds.size.x, bounds.size.y, 0.01f);
                    surface.transform.position = bounds.center + tvManager.transform.forward * 0.02f;
                }
                
                Debug.Log("[RayDrawingSetup] Positioned drawing surface in front of PresentationTVManager");
            }
            else
            {
                // Default position
                surface.transform.position = new Vector3(0f, 1.5f, 2f);
                Debug.LogWarning("[RayDrawingSetup] PresentationTVManager not found. Drawing surface placed at default position.");
            }
            
            Undo.RegisterCreatedObjectUndo(surface, "Create Drawing Surface");
            
            return surface;
        }

        private static GameObject CreateDrawingUI()
        {
            // Check if already exists
            var existing = GameObject.Find("DrawingTools_UI");
            if (existing != null)
            {
                Debug.Log("[RayDrawingSetup] UI already exists, deleting and recreating...");
                Object.DestroyImmediate(existing);
            }

            // Load rounded corners sprite (same as other project UI)
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ROUNDED_SPRITE_PATH);

            // ========== CREATE CANVAS ==========
            GameObject canvasObj = new GameObject("DrawingTools_UI");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 100f;

            canvasObj.AddComponent<GraphicRaycaster>();
            canvasObj.AddComponent<TrackedDeviceGraphicRaycaster>();

            // Set canvas size for world space (calculate total height needed)
            // Title: 35 + ColorRow: 50 + 3 buttons: 45*3=135 + padding: 30 + spacing: 12*4=48 = 298
            RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(260, 340);
            canvasRect.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            // Add BoxCollider for XR ray interaction
            BoxCollider canvasCollider = canvasObj.AddComponent<BoxCollider>();
            canvasCollider.size = new Vector3(260, 340, 1f);
            canvasCollider.center = Vector3.zero;

            // ========== CREATE MAIN PANEL (with rounded corners) ==========
            GameObject panelObj = new GameObject("MainPanel");
            panelObj.transform.SetParent(canvasObj.transform, false);

            Image panelImage = panelObj.AddComponent<Image>();
            panelImage.color = PanelBackground;
            panelImage.raycastTarget = true;

            if (roundedSprite != null)
            {
                panelImage.sprite = roundedSprite;
                panelImage.type = Image.Type.Sliced;
                panelImage.pixelsPerUnitMultiplier = 2f;
            }

            // Add CanvasGroup for fade effects (matching other UI)
            panelObj.AddComponent<CanvasGroup>();

            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            // Add VerticalLayoutGroup (matching DriveUISetup style)
            VerticalLayoutGroup vlg = panelObj.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(15, 15, 15, 15);
            vlg.spacing = 12;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            // ========== TITLE ==========
            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(panelObj.transform, false);

            LayoutElement titleLE = titleObj.AddComponent<LayoutElement>();
            titleLE.preferredHeight = 35;

            TextMeshProUGUI titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "Drawing Tools";
            titleText.fontSize = 24;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = TextPrimary;

            // ========== COLOR BUTTONS ROW ==========
            GameObject colorRow = new GameObject("ColorButtons");
            colorRow.transform.SetParent(panelObj.transform, false);

            LayoutElement colorRowLE = colorRow.AddComponent<LayoutElement>();
            colorRowLE.preferredHeight = 50;

            HorizontalLayoutGroup colorHLG = colorRow.AddComponent<HorizontalLayoutGroup>();
            colorHLG.spacing = 15;
            colorHLG.childControlWidth = true;
            colorHLG.childControlHeight = true;
            colorHLG.childForceExpandWidth = true;
            colorHLG.childForceExpandHeight = true;
            colorHLG.childAlignment = TextAnchor.MiddleCenter;

            // Create color buttons
            GameObject blueBtn = CreateColorButton("BlueButton", colorRow.transform, BLUE_COLOR, roundedSprite);
            GameObject greenBtn = CreateColorButton("GreenButton", colorRow.transform, GREEN_COLOR, roundedSprite);
            GameObject redBtn = CreateColorButton("RedButton", colorRow.transform, RED_COLOR, roundedSprite);

            // ========== TOOL BUTTONS ==========
            GameObject eraserBtn = CreateToolButton("EraserButton", panelObj.transform, "Eraser", ButtonNormal, roundedSprite);
            GameObject resetBtn = CreateToolButton("ResetButton", panelObj.transform, "Reset All", ButtonAction, roundedSprite);
            GameObject pointerBtn = CreateToolButton("PointerButton", panelObj.transform, "Pointer", ButtonNormal, roundedSprite);

            // ========== ADD UI CONTROLLER ==========
            RayDrawingUI uiController = canvasObj.AddComponent<RayDrawingUI>();

            // Wire up button references
            SerializedObject serializedUI = new SerializedObject(uiController);
            serializedUI.FindProperty("m_BlueButton").objectReferenceValue = blueBtn.GetComponent<Button>();
            serializedUI.FindProperty("m_GreenButton").objectReferenceValue = greenBtn.GetComponent<Button>();
            serializedUI.FindProperty("m_RedButton").objectReferenceValue = redBtn.GetComponent<Button>();
            serializedUI.FindProperty("m_EraserButton").objectReferenceValue = eraserBtn.GetComponent<Button>();
            serializedUI.FindProperty("m_ResetButton").objectReferenceValue = resetBtn.GetComponent<Button>();
            serializedUI.FindProperty("m_PointerButton").objectReferenceValue = pointerBtn.GetComponent<Button>();

            // Create and wire indicators (selection highlight)
            GameObject blueInd = CreateSelectionIndicator("BlueIndicator", blueBtn.transform);
            GameObject greenInd = CreateSelectionIndicator("GreenIndicator", greenBtn.transform);
            GameObject redInd = CreateSelectionIndicator("RedIndicator", redBtn.transform);
            GameObject eraserInd = CreateSelectionIndicator("EraserIndicator", eraserBtn.transform);
            GameObject pointerInd = CreateSelectionIndicator("PointerIndicator", pointerBtn.transform);

            serializedUI.FindProperty("m_BlueIndicator").objectReferenceValue = blueInd.GetComponent<Image>();
            serializedUI.FindProperty("m_GreenIndicator").objectReferenceValue = greenInd.GetComponent<Image>();
            serializedUI.FindProperty("m_RedIndicator").objectReferenceValue = redInd.GetComponent<Image>();
            serializedUI.FindProperty("m_EraserIndicator").objectReferenceValue = eraserInd.GetComponent<Image>();
            serializedUI.FindProperty("m_PointerIndicator").objectReferenceValue = pointerInd.GetComponent<Image>();

            serializedUI.ApplyModifiedProperties();

            // Force layout rebuild
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);

            Undo.RegisterCreatedObjectUndo(canvasObj, "Create Drawing UI");

            return canvasObj;
        }

        private static GameObject CreateColorButton(string name, Transform parent, Color color, Sprite roundedSprite)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            Image image = obj.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;

            if (roundedSprite != null)
            {
                image.sprite = roundedSprite;
                image.type = Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 4f;
            }

            Button button = obj.AddComponent<Button>();
            button.targetGraphic = image;

            // Disable the button temporarily to avoid OnEnable issues during setup
            button.enabled = false;

            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.3f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.2f);
            colors.selectedColor = color;
            colors.disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f); // Ensure disabled color is set
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            button.colors = colors;

            // Set navigation mode to None to avoid navigation-related array issues
            Navigation nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;

            // Re-enable the button after all properties are set
            button.enabled = true;

            // Add BoxCollider for VR interaction
            BoxCollider col = obj.AddComponent<BoxCollider>();
            col.size = new Vector3(60, 60, 1);
            col.center = Vector3.zero;

            return obj;
        }

        private static GameObject CreateToolButton(string name, Transform parent, string text, Color buttonColor, Sprite roundedSprite)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            // Layout element for consistent height
            LayoutElement le = obj.AddComponent<LayoutElement>();
            le.preferredHeight = 45;
            le.minHeight = 45;

            Image image = obj.AddComponent<Image>();
            image.color = buttonColor;
            image.raycastTarget = true;

            if (roundedSprite != null)
            {
                image.sprite = roundedSprite;
                image.type = Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 4f;
            }

            Button button = obj.AddComponent<Button>();
            button.targetGraphic = image;

            // Disable the button temporarily to avoid OnEnable issues during setup
            button.enabled = false;

            // Set button colors based on whether it's an action button (blue) or normal (gray)
            bool isActionButton = buttonColor == ButtonAction;
            ColorBlock colors = button.colors;
            colors.normalColor = buttonColor;
            colors.highlightedColor = isActionButton ? ButtonHighlight : new Color(0.25f, 0.25f, 0.25f, 1f);
            colors.pressedColor = isActionButton ? ButtonPressed : new Color(0.35f, 0.35f, 0.35f, 1f);
            colors.selectedColor = isActionButton ? ButtonAction : new Color(0.125f, 0.588f, 0.953f, 1f);
            colors.disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f); // Ensure disabled color is set
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            button.colors = colors;

            // Set navigation mode to None to avoid navigation-related array issues
            Navigation nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;

            // Re-enable the button after all properties are set
            button.enabled = true;

            // Add BoxCollider for VR interaction
            BoxCollider col = obj.AddComponent<BoxCollider>();
            col.size = new Vector3(230, 45, 1);
            col.center = Vector3.zero;

            // Add text
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(obj.transform, false);

            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(15, 0);
            textRect.offsetMax = new Vector2(-15, 0);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 22;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = TextPrimary;
            tmp.raycastTarget = false;

            return obj;
        }

        private static GameObject CreateSelectionIndicator(string name, Transform parent)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, -3);
            rect.sizeDelta = new Vector2(-10, 5);

            Image image = obj.AddComponent<Image>();
            image.color = Color.white;
            image.enabled = false; // Start hidden, enabled when selected

            return obj;
        }

        private static void WireUpReferences(GameObject manager, GameObject ui)
        {
            var drawingManager = manager.GetComponent<RayDrawingManager>();
            var uiController = ui.GetComponent<RayDrawingUI>();

            if (drawingManager != null && uiController != null)
            {
                SerializedObject serializedUI = new SerializedObject(uiController);
                serializedUI.FindProperty("m_DrawingManager").objectReferenceValue = drawingManager;
                serializedUI.ApplyModifiedProperties();
            }
        }

        private static void PositionUINextToTV(GameObject ui)
        {
            // Try to find TV/DriveTVManager in scene
            var tvManager = Object.FindFirstObjectByType<XRMultiplayer.Presentation.PresentationTVManager>();
            if (tvManager != null)
            {
                // Position UI to the left of the TV (presenter side)
                ui.transform.position = tvManager.transform.position + tvManager.transform.right * -1.0f + Vector3.up * 0.3f;
                ui.transform.rotation = tvManager.transform.rotation;
                Debug.Log("[RayDrawingSetup] Positioned UI next to PresentationTVManager");
            }
            else
            {
                // Default position
                ui.transform.position = new Vector3(-1f, 1.5f, 2f);
                ui.transform.rotation = Quaternion.identity;
                Debug.LogWarning("[RayDrawingSetup] PresentationTVManager not found. UI placed at default position.");
            }
        }

        private static void DeleteOldPenSystem()
        {
            string[] filesToDelete = new string[]
            {
                "Assets/VRMPAssets/Scripts/Gameplay/Drawing/SimplePen.cs",
                "Assets/VRMPAssets/Scripts/Gameplay/Drawing/PenTrail.cs"
            };

            string[] foldersToDelete = new string[]
            {
                "Assets/VRMPAssets/Prefabs/NetworkedPrefabs/NetworkedPen"
            };

            int deletedCount = 0;

            foreach (string file in filesToDelete)
            {
                if (File.Exists(file))
                {
                    AssetDatabase.DeleteAsset(file);
                    Debug.Log($"[RayDrawingSetup] Deleted: {file}");
                    deletedCount++;
                }
            }

            foreach (string folder in foldersToDelete)
            {
                if (Directory.Exists(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                    Debug.Log($"[RayDrawingSetup] Deleted folder: {folder}");
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[RayDrawingSetup] Deleted {deletedCount} old pen system files/folders.");
            }
            else
            {
                Debug.Log("[RayDrawingSetup] No old pen system files found to delete.");
            }
        }
    }
}
#endif
