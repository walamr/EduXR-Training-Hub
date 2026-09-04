using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using Unity.Netcode;
using TMPro;
using UnityEngine.XR.Interaction.Toolkit.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Events;
#endif

namespace XRMultiplayer.Recording
{
    public class HolographicRecordingUISetup : MonoBehaviour
    {
#if UNITY_EDITOR
        [MenuItem("VRMP/Setup Holographic Recording UI")]
        public static void CreateRecordingUI()
        {
            // 0a. Create Folders
            if (!AssetDatabase.IsValidFolder("Assets/VRMPAssets/Prefabs/Recording"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/VRMPAssets/Prefabs"))
                    AssetDatabase.CreateFolder("Assets/VRMPAssets", "Prefabs");
                AssetDatabase.CreateFolder("Assets/VRMPAssets/Prefabs", "Recording");
            }

            // 0b. Create Miniature Avatar Prefab (local-only, not networked)
            GameObject avatarPrefab = CreateMiniatureAvatarPrefab();

            // 0d. Create Managers if not exist
            MeetingRecorder recorder = Object.FindFirstObjectByType<MeetingRecorder>();
            MeetingPlaybackManager playback = Object.FindFirstObjectByType<MeetingPlaybackManager>();

            if (recorder == null || playback == null)
            {
                GameObject managers = new GameObject("RecordingManagers");
                recorder = managers.AddComponent<MeetingRecorder>();
                playback = managers.AddComponent<MeetingPlaybackManager>();
                // AudioRecorder attaches dynamically
                
                // Setup Playback References
                // Create Table Center
                GameObject tableCenter = new GameObject("RecordingTableCenter");
                tableCenter.transform.position = new Vector3(0, 0.8f, 0.5f); // Reasonable table height
                
                // Load Conference Room prefabs for accurate miniature
                GameObject tablePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AK Studio Art/Conference Room Vol.1/Prefabs/Table Black.prefab");
                GameObject chairPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AK Studio Art/Conference Room Vol.1/Prefabs/Chair Black.prefab");
                
                // Use Reflection to set private fields if needed, or assume serialized fields can be set in Editor
                SerializedObject serializedPlayback = new SerializedObject(playback);
                
                // Load player avatar prefab (same as multiplayer players)
                GameObject playerAvatarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VRMPAssets/Prefabs/PlayerPrefabs/XRI_Network_Player_Avatar.prefab");
                if (playerAvatarPrefab != null)
                {
                    serializedPlayback.FindProperty("m_PlayerAvatarPrefab").objectReferenceValue = playerAvatarPrefab;
                    Debug.Log("[HolographicRecordingUISetup] Using player avatar prefab for playback");
                }
                
                // Fallback miniature avatar
                serializedPlayback.FindProperty("m_MiniatureAvatarPrefab").objectReferenceValue = avatarPrefab;
                serializedPlayback.FindProperty("m_TableTopTransform").objectReferenceValue = tableCenter.transform;
                serializedPlayback.FindProperty("m_OverallScale").floatValue = 0.15f; // Scaled for tabletop display
                serializedPlayback.FindProperty("m_AvatarScale").floatValue = 1.0f; // 1:1 with overall
                serializedPlayback.FindProperty("m_FurnitureScale").floatValue = 0.1f; // Shrink real-world prefabs
                serializedPlayback.FindProperty("m_TablePrefab").objectReferenceValue = tablePrefab;
                serializedPlayback.FindProperty("m_ChairPrefab").objectReferenceValue = chairPrefab;
                serializedPlayback.FindProperty("m_ChairCount").intValue = 14; // 14 chairs like the actual boardroom
                serializedPlayback.ApplyModifiedProperties();
                
                if (tablePrefab == null || chairPrefab == null)
                {
                    Debug.LogWarning("[HolographicRecordingUISetup] Conference Room prefabs not found. Playback will use fallback primitives.");
                }
                
                Selection.activeGameObject = managers;
            }

            // 1. Create Canvas
            if (GameObject.Find("HolographicRecordingUI") != null)
            {
                Debug.Log("UI already exists.");
                return;
            }

            // Color constants from RayDrawingSetup design
            Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
            Color ButtonNormal = new Color(0.18f, 0.18f, 0.18f, 1f);
            Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f);
            Color StatusGreen = new Color(0.13f, 0.8f, 0.4f, 1f);
            Color RecordingRed = new Color(0.9f, 0.2f, 0.2f, 1f);
            
            // Load sprites
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/VRMPAssets/Textures/UI/Round Radius 10.png");
            Sprite circleSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/VRMPAssets/Textures/UI/Circle_60x60_Horizontal.png");

            GameObject canvasObj = new GameObject("HolographicRecordingUI");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();
            canvasObj.AddComponent<TrackedDeviceGraphicRaycaster>();

            RectTransform canvasRT = canvasObj.GetComponent<RectTransform>();
            canvasRT.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            canvasRT.sizeDelta = new Vector2(420, 420); // Wider width
            canvas.transform.position = new Vector3(0, 1.2f, 0.5f);
            canvas.sortingOrder = 50; // Match RayDrawing setup
            
            // Set Layer to UI for Interaction
            int uiLayer = LayerMask.NameToLayer("UI");
            canvasObj.layer = uiLayer;
            
            BoxCollider col = canvasObj.AddComponent<BoxCollider>();
            col.size = new Vector3(420, 420, 10);
            col.isTrigger = true;
            
            // EventSystem check removed - assume scene already has one or XR Simulator handles it

            // 2. Main Panel
            GameObject panelObj = new GameObject("MainPanel");
            panelObj.transform.SetParent(canvasObj.transform, false);
            Image bg = panelObj.AddComponent<Image>();
            bg.color = PanelBackground;
            
            if (roundedSprite != null)
            {
                bg.sprite = roundedSprite;
                bg.type = Image.Type.Sliced;
                bg.pixelsPerUnitMultiplier = 2f;
            }

            RectTransform panelRT = panelObj.GetComponent<RectTransform>();
            panelRT.anchorMin = Vector2.zero;
            panelRT.anchorMax = Vector2.one;
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;
            
            // Vertical Layout for the whole panel
            VerticalLayoutGroup vlg = panelObj.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(15, 15, 15, 15);
            vlg.spacing = 10;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true; // Fix: Control height to allow flexible element to fill space
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            // 3. Header Row
            GameObject headerRow = new GameObject("HeaderRow");
            headerRow.transform.SetParent(panelObj.transform, false);
            LayoutElement headerLE = headerRow.AddComponent<LayoutElement>();
            headerLE.preferredHeight = 35;
            headerLE.minHeight = 35;
            
            HorizontalLayoutGroup headerHLG = headerRow.AddComponent<HorizontalLayoutGroup>();
            headerHLG.childControlWidth = true;
            headerHLG.childForceExpandWidth = true; // Fix: Force expand to ensure Title takes space
            headerHLG.childControlHeight = true;
            headerHLG.childForceExpandHeight = false;
            
            // Title
            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(headerRow.transform, false);
            LayoutElement titleLE = titleObj.AddComponent<LayoutElement>();
            titleLE.flexibleWidth = 1;
            
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "MEETING RECORDER";
            titleText.fontSize = 18;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.MidlineLeft;
            titleText.color = Color.white;
            titleText.textWrappingMode = TextWrappingModes.NoWrap;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.enableAutoSizing = true;
            titleText.fontSizeMin = 8; // Allow smaller font
            titleText.fontSizeMax = 18;

            // Status Container
            GameObject statusObj = new GameObject("Status");
            statusObj.transform.SetParent(headerRow.transform, false);
            HorizontalLayoutGroup statusHLG = statusObj.AddComponent<HorizontalLayoutGroup>();
            statusHLG.spacing = 5;
            statusHLG.childControlWidth = true; // Enable control to enforce size
            statusHLG.childControlHeight = true;
            statusHLG.childForceExpandHeight = false;
            statusHLG.childAlignment = TextAnchor.MiddleRight;
            LayoutElement statusContainerLE = statusObj.AddComponent<LayoutElement>();
            statusContainerLE.preferredWidth = 100;
            
            // Status Dot
            GameObject dotObj = new GameObject("Dot");
            dotObj.transform.SetParent(statusObj.transform, false);
            Image dotImg = dotObj.AddComponent<Image>();
            dotImg.color = StatusGreen;
            
            if (circleSprite != null)
            {
                dotImg.sprite = circleSprite;
            }
            dotImg.preserveAspect = true; // Fix: Prevent distortion
            
            LayoutElement dotLE = dotObj.AddComponent<LayoutElement>();
            dotLE.minWidth = 12;
            dotLE.minHeight = 12;
            dotLE.preferredWidth = 12;
            dotLE.preferredHeight = 12;
            
            // Status Text
            GameObject statusTxtObj = new GameObject("StatusText");
            statusTxtObj.transform.SetParent(statusObj.transform, false);
            var statusTxt = statusTxtObj.AddComponent<TextMeshProUGUI>();
            statusTxt.text = "Ready";
            statusTxt.fontSize = 16;
            statusTxt.color = StatusGreen;
            statusTxt.alignment = TextAlignmentOptions.MidlineRight;
            LayoutElement txtLE = statusTxtObj.AddComponent<LayoutElement>();
            txtLE.preferredWidth = 60;
            txtLE.preferredHeight = 25;

            // 4. File List Panel (Darker background)
            GameObject fileListPanel = new GameObject("FileListPanel");
            fileListPanel.transform.SetParent(panelObj.transform, false);
            LayoutElement listLE = fileListPanel.AddComponent<LayoutElement>();
            listLE.preferredHeight = 180;
            listLE.flexibleHeight = 1; // Grow to fill space
            
            Image listBg = fileListPanel.AddComponent<Image>();
            listBg.color = new Color(0.06f, 0.06f, 0.06f, 1f);
            if (roundedSprite != null)
            {
                listBg.sprite = roundedSprite;
                listBg.type = Image.Type.Sliced;
                listBg.pixelsPerUnitMultiplier = 2f;
            }

            // ScrollView
            GameObject scrollObj = new GameObject("FilesScrollView");
            scrollObj.transform.SetParent(fileListPanel.transform, false);
            ScrollRect scroll = scrollObj.AddComponent<ScrollRect>();
            scroll.horizontal = false; // Fix: Disable horizontal scrolling
            scroll.vertical = true;
            RectTransform scrollRT = scrollObj.GetComponent<RectTransform>();
            scrollRT.anchorMin = Vector2.zero;
            scrollRT.anchorMax = Vector2.one;
            scrollRT.offsetMin = new Vector2(5, 5);
            scrollRT.offsetMax = new Vector2(-5, -5);
            
            GameObject viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollObj.transform, false);
            RectMask2D mask = viewport.AddComponent<RectMask2D>();
            RectTransform vpRT = viewport.GetComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero;
            vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = Vector2.zero;
            vpRT.offsetMax = Vector2.zero;
            scroll.viewport = vpRT;
            
            GameObject content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            VerticalLayoutGroup contentVLG = content.AddComponent<VerticalLayoutGroup>();
            contentVLG.spacing = 2;
            contentVLG.padding = new RectOffset(15, 15, 5, 5); // Increased padding to show corners
            contentVLG.childControlHeight = true;
            contentVLG.childControlWidth = true;
            contentVLG.childForceExpandHeight = false;
            contentVLG.childForceExpandWidth = true;
            contentVLG.childControlHeight = true;
            contentVLG.childControlWidth = true;
            contentVLG.childForceExpandHeight = false;
            contentVLG.childForceExpandWidth = true;
            ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            RectTransform contentRT = content.GetComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0, 1);
            contentRT.anchorMax = new Vector2(1, 1);
            contentRT.pivot = new Vector2(0.5f, 1);
            scroll.content = contentRT;

            // Template Button (Disabled)
            GameObject templateBtn = CreateRecordingButton("FileButtonTemplate", content.transform, "REC 001", ButtonNormal, roundedSprite, 40);
            
            // Add LayoutElement to template to prevent weird stretching
            LayoutElement tmplLE = templateBtn.GetComponent<LayoutElement>();
            if (tmplLE == null) tmplLE = templateBtn.AddComponent<LayoutElement>();
            tmplLE.minHeight = 40;
            tmplLE.preferredHeight = 40;
            templateBtn.SetActive(false);

            // 6. Controls Row
            GameObject controlsRow = new GameObject("ControlsRow");
            controlsRow.transform.SetParent(panelObj.transform, false);
            LayoutElement controlsLE = controlsRow.AddComponent<LayoutElement>();
            controlsLE.preferredHeight = 45;
            
            HorizontalLayoutGroup controlsHLG = controlsRow.AddComponent<HorizontalLayoutGroup>();
            controlsHLG.spacing = 10;
            controlsHLG.childControlWidth = true;
            controlsHLG.childForceExpandWidth = true;
            controlsHLG.childControlHeight = true;
            controlsHLG.childForceExpandHeight = true;

            GameObject recBtn = CreateRecordingButton("RecordBtn", controlsRow.transform, "● REC", RecordingRed, roundedSprite, 45);
            GameObject playBtn = CreateRecordingButton("PlayBtn", controlsRow.transform, "▶ PLAY", ButtonAction, roundedSprite, 45);
            GameObject stopBtn = CreateRecordingButton("StopBtn", controlsRow.transform, "■ STOP", ButtonNormal, roundedSprite, 45);

            // 7. Refresh Button
            GameObject refreshBtn = CreateRecordingButton("RefreshBtn", panelObj.transform, "REFRESH LIST", ButtonNormal, roundedSprite, 35);

            // 8. Add Logic
            RecordingPanel rp = canvasObj.AddComponent<RecordingPanel>();
            rp.fileListContainer = content.transform;
            rp.fileButtonPrefab = templateBtn;
            rp.statusText = statusTxt;
            rp.recordButtonImage = recBtn.GetComponent<Image>();
            rp.statusDotImage = dotImg;
            
            UnityEventTools.AddPersistentListener(recBtn.GetComponent<Button>().onClick, rp.ToggleRecording);
            UnityEventTools.AddPersistentListener(playBtn.GetComponent<Button>().onClick, rp.PlaySelected);
            UnityEventTools.AddPersistentListener(stopBtn.GetComponent<Button>().onClick, rp.StopPlayback);
            UnityEventTools.AddPersistentListener(refreshBtn.GetComponent<Button>().onClick, rp.RefreshFileList);

            // Force layout rebuild
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelRT);
            
            Debug.Log("Recording UI Setup Complete with new design!");
        }

        private static GameObject CreateMiniatureAvatarPrefab()
        {
            string path = "Assets/VRMPAssets/Prefabs/Recording/MiniatureAvatar.prefab";
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            // Create Temp Object - Humanoid-style avatar
            GameObject root = new GameObject("MiniatureAvatar");
            
            // Materials
            Material bodyMat = new Material(Shader.Find("Standard"));
            bodyMat.color = new Color(0.6f, 0.2f, 0.8f, 1f); // Purple
            bodyMat.EnableKeyword("_EMISSION");
            bodyMat.SetColor("_EmissionColor", new Color(0.4f, 0.1f, 0.5f, 1f)); // Purple glow
            
            Material skinMat = new Material(Shader.Find("Standard"));
            skinMat.color = new Color(0.9f, 0.75f, 0.65f, 1f); // Skin tone
            skinMat.EnableKeyword("_EMISSION");
            skinMat.SetColor("_EmissionColor", new Color(0.3f, 0.25f, 0.2f, 1f)); // Subtle glow
            
            // Head (sphere) - positioned above where body would be
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform);
            head.transform.localScale = new Vector3(0.15f, 0.18f, 0.15f); // Slightly oval head
            head.GetComponent<Renderer>().material = skinMat;
            DestroyImmediate(head.GetComponent<Collider>());
            
            // Body/Torso (capsule)
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(head.transform); // Parent to head so it follows
            body.transform.localPosition = new Vector3(0, -1.2f, 0); // Below head
            body.transform.localScale = new Vector3(0.6f, 0.8f, 0.4f); // Torso proportions
            body.GetComponent<Renderer>().material = bodyMat;
            DestroyImmediate(body.GetComponent<Collider>());
            
            // Neck (small cylinder connecting head to body)
            GameObject neck = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            neck.name = "Neck";
            neck.transform.SetParent(head.transform);
            neck.transform.localPosition = new Vector3(0, -0.5f, 0);
            neck.transform.localScale = new Vector3(0.25f, 0.15f, 0.25f);
            neck.GetComponent<Renderer>().material = skinMat;
            DestroyImmediate(neck.GetComponent<Collider>());
            
            // Left Hand (capsule for more natural look)
            GameObject lHand = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            lHand.name = "LeftHand";
            lHand.transform.SetParent(root.transform);
            lHand.transform.localScale = new Vector3(0.04f, 0.06f, 0.04f);
            lHand.GetComponent<Renderer>().material = skinMat;
            DestroyImmediate(lHand.GetComponent<Collider>());
            
            // Right Hand (capsule)
            GameObject rHand = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            rHand.name = "RightHand";
            rHand.transform.SetParent(root.transform);
            rHand.transform.localScale = new Vector3(0.04f, 0.06f, 0.04f);
            rHand.GetComponent<Renderer>().material = skinMat;
            DestroyImmediate(rHand.GetComponent<Collider>());

            // Components (local-only, no NetworkObject needed)
            MiniatureAvatar ma = root.AddComponent<MiniatureAvatar>();
            ma.head = head.transform;
            ma.leftHand = lHand.transform;
            ma.rightHand = rHand.transform;

            // Save Prefab
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            DestroyImmediate(root);
            
            return prefab;
        }

        private static void RegisterNetworkPrefab(GameObject prefab)
        {
            NetworkManager nm = Object.FindFirstObjectByType<NetworkManager>();
            if (nm == null)
            {
                Debug.LogWarning("NetworkManager not found in scene. Cannot register prefab.");
                return;
            }

            // Using SerializedObject to safely edit the list
            SerializedObject so = new SerializedObject(nm);
            SerializedProperty prefabsProp = so.FindProperty("NetworkConfig.Prefabs");
            
            // Check if already in list
            // Check if already in list 
            {
                 // Handling varies by Netcode version.
                 // Usually NetworkConfig -> NetworkPrefabs
                 // Let's try NetworkConfig.Prefabs (NetworkPrefab list)
            }
            
            // Since Netcode structure varies, let's try a simpler approach if possible.
            // But Editor script must use SerializedObject for persistent changes.
            // Assuming default NetworkManager inspector structure.
            // NetworkConfig is usually a class, not a property directly on MonoBehaviour in some versions?
            // Actually it is. 
            
            try 
            {
                // Fallback: Add to the NetworkManager's list via runtime method if running? No, this is editor.
                // NOTE: We cannot easily edit NetworkConfig via SerializedProperty if it's not exposed cleanly.
                // However, let's try to add it to the NetworkPrefabsList if one exists (Netcode 1.0+ often uses a ScriptableObject list).
                // Or just add to the list on the component.
                
                // For safety in this environment: Just log instruction if automatic fails?
                // But user asked to "make everything".
                
                // Let's try standard Netcode 1.x way:
                // NetworkManager has a field [SerializeField] NetworkPrefabsList NetworkPrefabsLists; 
                // OR it has a list inside NetworkConfig.
                
                // Let's just create a NetworkPrefab and add it.
                // Since I can't see the Netcode source here, I'll attempt to set it via the NetworkManager inspector structure if I can find it.
                // Actually, NetworkManager.Singleton.AddNetworkPrefab(prefab) works at runtime.
                // At Edit time:
                 
                 nm.AddNetworkPrefab(prefab);
                 Debug.Log("Added prefab to NetworkManager");
                 EditorUtility.SetDirty(nm);
            }
            catch
            {
                Debug.LogWarning("Could not automatically register prefab with NetworkManager. Please add 'MiniatureAvatar' to NetworkManager's NetworkPrefabs list manually.");
            }
        }

        private static GameObject CreateButton(string name, Color color, Sprite sprite)
        {
            GameObject go = new GameObject(name);
            Image img = go.AddComponent<Image>();
            img.color = color;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = Image.Type.Sliced;
            }
            
            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            
            // Disable the button temporarily to avoid OnEnable issues during setup
            btn.enabled = false;
            
            // Ensure ColorBlock is properly initialized with all required colors
            ColorBlock colors = btn.colors;
            colors.disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f);
            colors.fadeDuration = 0.1f;
            btn.colors = colors;
            
            // Set navigation mode to None to avoid navigation-related array issues
            Navigation nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;
            
            // Re-enable the button after all properties are set
            btn.enabled = true;
            
            GameObject text = new GameObject("Text");
            text.transform.SetParent(go.transform, false);
            Text t = text.AddComponent<Text>();
            t.text = "Button";
            t.alignment = TextAnchor.MiddleLeft; // Left align for file names
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.color = Color.white; // White text for visibility
            t.fontSize = 18;
            
            RectTransform trt = text.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10, 0); // Left padding
            trt.offsetMax = new Vector2(-10, 0); // Right padding
            
            // Add BoxCollider for XR interaction
            BoxCollider col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(200, 40, 1);
            col.isTrigger = true;
            
            return go;
        }

        private static GameObject CreateRecordingButton(string name, Transform parent, string text, Color buttonColor, Sprite roundedSprite, float height)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            // Layout element for consistent height
            LayoutElement le = obj.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

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

            // Colors
            Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f);
            Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
            Color ButtonPressed = new Color(0.1f, 0.5f, 0.85f, 1f);
            Color ButtonDisabled = new Color(0.784f, 0.784f, 0.784f, 0.502f); // Standard disabled color

            bool isActionButton = buttonColor == ButtonAction || buttonColor.r > 0.8f; // simplified check
            ColorBlock colors = button.colors;
            colors.normalColor = buttonColor;
            // Use lighter version for highlight if not action button
            colors.highlightedColor = isActionButton ? ButtonHighlight : Color.Lerp(buttonColor, Color.white, 0.1f);
            colors.pressedColor = isActionButton ? ButtonPressed : Color.Lerp(buttonColor, Color.black, 0.1f);
            colors.selectedColor = buttonColor;
            colors.disabledColor = ButtonDisabled; // Ensure disabled color is set
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
            col.size = new Vector3(200, height, 1); // approximate width, will stretch but collider needs size
            col.center = Vector3.zero;

            // Add text
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(obj.transform, false);

            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 0);
            textRect.offsetMax = new Vector2(-10, 0);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = height * 0.45f; // Scale font with height
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;

            return obj;
        }

        private static void TryAddComponent(GameObject go, string componentName)
        {
            // Reflection attempt to add component by name since assembly might not be referenced directly in this simplified script
            System.Type type = System.Type.GetType(componentName);
            // Also try specific known assemblies
            if (type == null) type = System.Type.GetType(componentName + ", Unity.XR.Interaction.Toolkit.UI");
            if (type == null) type = System.Type.GetType(componentName + ", UnityEngine.XR.Interaction.Toolkit");
             
            if (type != null)
            {
                go.AddComponent(type);
            }
        }
#endif
    }
}
