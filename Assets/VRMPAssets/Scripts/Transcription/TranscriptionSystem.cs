using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using XRMultiplayer; // For QuickMenuManager

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Smart Transcription System with Google Gemini.
    /// Uses Gemini 1.5 Flash for free, high-quality transcription and summarization.
    /// </summary>
    [RequireComponent(typeof(GeminiService))]
    [RequireComponent(typeof(TranscriptionManager))]
    public class TranscriptionSystem : MonoBehaviour
    {
#if UNITY_EDITOR
        [UnityEditor.MenuItem("VRMP/Setup Transcription UI")]
        public static void SetupTranscriptionUI()
        {
            // Check if already exists
            var existing = FindFirstObjectByType<TranscriptionSystem>();
            if (existing != null)
            {
                // Just regenerate UI
                existing.GenerateUI();
                UnityEditor.Selection.activeGameObject = existing.gameObject;
                Debug.Log("[TranscriptionSystem] UI regenerated on existing TranscriptionSystem.");
                return;
            }
            
            // Create new system
            GameObject systemObj = new GameObject("TranscriptionSystem");
            var system = systemObj.AddComponent<TranscriptionSystem>();
            system.createUIOnStart = false; // We're generating manually
            system.GenerateUI();
            
            UnityEditor.Selection.activeGameObject = systemObj;
            Debug.Log("[TranscriptionSystem] Transcription UI Setup Complete!");
        }
#endif

        [Header("Gemini Configuration")]
        [SerializeField, Tooltip("Google AI Studio API Key")]
        private string apiKey = "";

        [Header("UI Settings")]
        [SerializeField] private bool createUIOnStart = true;
        [SerializeField, Tooltip("If true, panel starts hidden and can be toggled from Quick Menu.")]
        private bool startHidden = true;
        [SerializeField, Tooltip("If true, auto-registers with QuickMenuManager for toggle from menu.")]
        private bool addToQuickMenu = true;
        [SerializeField, Tooltip("Name shown in Quick Menu.")]
        private string quickMenuName = "Transcript";
        [SerializeField, Tooltip("If true, positions panel in front of camera. If false, uses fixedWorldPosition.")]
        private bool positionInFrontOfCamera = false; // Changed to false for manual positioning
        [SerializeField, Tooltip("Distance in front of camera (meters)")]
        private float distanceFromCamera = 2f;
        [SerializeField, Tooltip("Height offset from camera (meters, negative = below eye level)")]
        private float heightOffset = -0.3f;
        [SerializeField, Tooltip("World position when positionInFrontOfCamera is false")]
        private Vector3 fixedWorldPosition = new Vector3(-2f, 1.8f, 0f); // Side wall position
        [SerializeField] private float panelWidth = 0.8f;  // World units (meters)
        [SerializeField] private float panelHeight = 0.6f; // World units (meters)

        [Header("Theme Settings")]
        [SerializeField] private Sprite themeBackgroundSprite; // For rounded corners
        [SerializeField] private TMP_FontAsset themeFont;      // For consistent text style
        [SerializeField] private float pixelsPerUnitMultiplier = 2f; // Background sprite scaling (4 for buttons)

        [Header("References (Auto-filled)")]
        [SerializeField] private GeminiService geminiService;
        [SerializeField] private TranscriptionManager transcriptionManager;
        [SerializeField] private TranscriptPanel transcriptPanel;
        [SerializeField] private SummaryPanel summaryPanel;

        // UI Elements
        private Canvas canvas;
        private GameObject panelObject;
        private TMP_Text transcriptText;
        private TMP_Text statusText;
        private Button recordButton;
        private Button summaryButton;

        // Language selector
        private TranscriptionLanguage currentLanguage = TranscriptionLanguage.English;
        private readonly TranscriptionLanguage[] languageOrder =
        {
            TranscriptionLanguage.English,
            TranscriptionLanguage.Hebrew,
            TranscriptionLanguage.Arabic
        };
        private readonly System.Collections.Generic.Dictionary<TranscriptionLanguage, Button> languageButtons
            = new System.Collections.Generic.Dictionary<TranscriptionLanguage, Button>();
        private static readonly Color k_LangSelected = new Color(0.2f, 0.6f, 1f, 1f);
        private static readonly Color k_LangIdle = new Color(0.25f, 0.25f, 0.28f, 1f);

        private void Awake()
        {
            // Get components
            geminiService = GetComponent<GeminiService>();
            transcriptionManager = GetComponent<TranscriptionManager>();

            // Set API key if provided here
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = SecureConfig.GeminiApiKey;
            }

            if (geminiService != null && !string.IsNullOrEmpty(apiKey))
            {
                geminiService.SetApiKey(apiKey);
            }

            // Subscribe to summary
            if (transcriptionManager != null)
            {
                transcriptionManager.OnSummaryReceived.AddListener(OnSummaryReceived);

                // Apply the default language so Gemini is configured before first recording.
                transcriptionManager.SetLanguage(currentLanguage);
            }

            // Create UI. Also run when a panel already exists in the scene, because runtime
            // button listeners and the language selector are not serialized and must be
            // (re)built every time we enter Play mode.
            bool panelExists = FindFirstObjectByType<TranscriptPanel>() != null;
            if (createUIOnStart || panelExists)
            {
                CreateSimpleUI();
                
                // Hide panel if configured (only applies to freshly created canvases)
                if (createUIOnStart && startHidden && canvas != null)
                {
                    canvas.gameObject.SetActive(false);
                }
            }

            Debug.Log("[TranscriptionSystem] Ready! Use StartTranscription() to begin.");
        }

        private void Start()
        {
            // Auto-register with Quick Menu (in Start so QuickMenuManager.Instance is ready)
            if (addToQuickMenu && canvas != null)
            {
                RegisterWithQuickMenu();
            }
        }

        public void StartTranscription()
        {
            transcriptionManager?.StartTranscription();
            UpdateRecordButtonVisual(true);
        }

        public void StopTranscription()
        {
            transcriptionManager?.StopTranscription();
            UpdateRecordButtonVisual(false);
        }

        public void ToggleTranscription()
        {
            transcriptionManager?.ToggleTranscription();
            bool isRecording = transcriptionManager != null && transcriptionManager.IsTranscribing;
            UpdateRecordButtonVisual(isRecording);
        }

        /// <summary>
        /// Selects the transcription language and updates the selector highlight.
        /// Works before or during recording.
        /// </summary>
        public void SetLanguage(TranscriptionLanguage language)
        {
            currentLanguage = language;
            transcriptionManager?.SetLanguage(language);
            UpdateLanguageButtonVisuals();
            Debug.Log($"[TranscriptionSystem] Language switched to {language.DisplayName()}");
        }

        private void UpdateLanguageButtonVisuals()
        {
            foreach (var kvp in languageButtons)
            {
                if (kvp.Value == null) continue;
                var img = kvp.Value.targetGraphic as Image;
                if (img != null)
                {
                    img.color = (kvp.Key == currentLanguage) ? k_LangSelected : k_LangIdle;
                }
            }
        }

        /// <summary>
        /// Returns the canvas GameObject that can be toggled by QuickMenuManager.
        /// </summary>
        public GameObject GetPanelObject()
        {
            return canvas != null ? canvas.gameObject : null;
        }

        /// <summary>
        /// Registers this transcript panel with the QuickMenuManager.
        /// </summary>
        private void RegisterWithQuickMenu()
        {
            if (QuickMenuManager.Instance == null)
            {
                Debug.LogWarning("[TranscriptionSystem] QuickMenuManager not found yet. Retrying in 1 second...");
                StartCoroutine(RetryRegisterWithQuickMenu());
                return;
            }

            DoRegisterWithQuickMenu();
        }

        private System.Collections.IEnumerator RetryRegisterWithQuickMenu()
        {
            yield return new WaitForSeconds(1f);
            
            if (QuickMenuManager.Instance == null)
            {
                Debug.LogError("[TranscriptionSystem] QuickMenuManager still not found! Please ensure it exists in the scene.");
                yield break;
            }

            DoRegisterWithQuickMenu();
        }

        private void DoRegisterWithQuickMenu()
        {
            GameObject panelToToggle = GetPanelObject();
            if (panelToToggle == null)
            {
                Debug.LogWarning("[TranscriptionSystem] Panel not created yet. Cannot register.");
                return;
            }

            QuickMenuManager.Instance.AddSegment(quickMenuName, null, panelToToggle);
            Debug.Log($"[TranscriptionSystem] Registered '{quickMenuName}' with Quick Menu!");
        }

        private void UpdateRecordButtonVisual(bool recording)
        {
            // NOTE: the status text label is owned by TranscriptionManager so it can show
            // richer states (Recording... / Stopped / Mic not available / Error). Here we
            // only update the button itself.
            if (recordButton != null)
            {
                var btnText = recordButton.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (btnText != null)
                {
                    btnText.text = recording ? "STOP" : "REC";
                }
                
                var img = recordButton.targetGraphic as Image;
                if (img != null)
                {
                    // Dark Red when recording, Normal Blue/Red when idle
                    img.color = recording ? new Color(0.6f, 0f, 0f) : new Color(0.9f, 0.2f, 0.2f);
                }
            }
        }

        [ContextMenu("Generate UI")]
        public void GenerateUI()
        {
            // Allow running in Editor mode
            CreateSimpleUI();
        }

        /// <summary>
        /// Pushes sample English / Hebrew / Arabic lines through the real transcript pipeline
        /// (RTL detection + ArabicFixer + fallback fonts). Use to verify font rendering in VR
        /// or Play mode without speaking. Available via the component context menu.
        /// </summary>
        [ContextMenu("Test: Add Sample RTL Text")]
        public void AddSampleTranscriptText()
        {
            if (transcriptPanel == null)
            {
                Debug.LogWarning("[TranscriptionSystem] No transcript panel available. Enter Play mode first.");
                return;
            }

            transcriptPanel.AddEntry(new TranscriptionEntry("Hello, this is an English test.", "EN", 0));
            transcriptPanel.AddEntry(new TranscriptionEntry("שלום, מה קורה?", "HE", 0));
            transcriptPanel.AddEntry(new TranscriptionEntry("קיום הפגישה מחר בשעה 09:00.", "HE", 0));
            transcriptPanel.AddEntry(new TranscriptionEntry("צריך להכין את החדר לפני תחילת הפגישה.", "HE", 0));
            transcriptPanel.AddEntry(new TranscriptionEntry("לא צוין אחראי אחד.", "HE", 0));
            transcriptPanel.AddEntry(new TranscriptionEntry("مرحبا، هذا اختبار باللغة العربية.", "AR", 0));
            Debug.Log("[TranscriptionSystem] Added EN/HE/AR sample lines to the transcript.");
        }

        /// <summary>
        /// Shows a fake summary in the popup so wrapping, scrolling and RTL rendering can
        /// be verified without calling the API. Content depends on the selected language:
        /// Hebrew shows a full Hebrew summary with Hebrew section titles.
        /// </summary>
        [ContextMenu("Test: Show Long AI Summary")]
        public void ShowTestSummary()
        {
            if (transcriptionManager != null && transcriptionManager.CurrentLanguage == TranscriptionLanguage.Hebrew)
            {
                string hebrewSummary =
                    "סיכום:\n" +
                    "שלום, מה קורה? נקבע כי קיום הפגישה מחר בשעה 09:00. " +
                    "צריך להכין את החדר לפני תחילת הפגישה.\n\n" +
                    "החלטות עיקריות:\n" +
                    "\u2022 קיום הפגישה מחר בשעה 09:00.\n\n" +
                    "פעולות לביצוע:\n" +
                    "\u2022 צריך להכין את החדר לפני תחילת הפגישה.\n" +
                    "\u2022 לא צוין אחראי אחד.";

                OnSummaryReceived(hebrewSummary);
                Debug.Log("[TranscriptionSystem] Showing Hebrew test summary in popup.");
                return;
            }

            string longSummary =
                "Summary: This is a long test summary used to verify that the popup keeps all " +
                "text inside the panel, wraps long lines correctly, and scrolls vertically when " +
                "the content does not fit. It intentionally repeats content to force scrolling.\n\n" +
                "Key Decisions:\n" +
                "\u2022 The transcript panel layout was approved.\n" +
                "\u2022 The summary popup must never overflow outside the dark panel.\n" +
                "\u2022 Hebrew and Arabic lines must be right-aligned.\n\n" +
                "סיכום בעברית: זהו טקסט בדיקה ארוך בעברית כדי לוודא שהשורה מיושרת לימין ונשארת בתוך הפאנל.\n\n" +
                "ملخص بالعربية: هذا نص اختبار طويل باللغة العربية للتأكد من أن السطر محاذى لليمين ويبقى داخل اللوحة.\n\n" +
                "Action Items:\n" +
                "\u2022 Test scrolling with the controller thumbstick while pointing at the popup.\n" +
                "\u2022 Confirm the close button stays visible and clickable.\n" +
                "\u2022 Confirm the REC and AI buttons are not covered by the popup.\n\n" +
                "Extra paragraph 1: Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do " +
                "eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n" +
                "Extra paragraph 2: Ut enim ad minim veniam, quis nostrud exercitation ullamco " +
                "laboris nisi ut aliquip ex ea commodo consequat.\n\n" +
                "Extra paragraph 3: Duis aute irure dolor in reprehenderit in voluptate velit esse " +
                "cillum dolore eu fugiat nulla pariatur. End of test summary.";

            OnSummaryReceived(longSummary);
            Debug.Log("[TranscriptionSystem] Showing long test summary in popup.");
        }

        private void CreateSimpleUI()
        {
            // Auto-load theme sprite if not assigned (Editor only)
            #if UNITY_EDITOR
            if (themeBackgroundSprite == null)
            {
                themeBackgroundSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/VRMPAssets/Textures/UI/Round Radius 10.png");
                if (themeBackgroundSprite != null)
                    Debug.Log("[TranscriptionSystem] Auto-loaded Round Radius 10 sprite for rounded corners.");
            }
            #endif

            // Device/build fallback (Quest): the sprite is used by other UIs in the scene,
            // so it is already loaded - find it among loaded objects by name.
            if (themeBackgroundSprite == null)
            {
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sprite != null && sprite.name == "Round Radius 10")
                    {
                        themeBackgroundSprite = sprite;
                        Debug.Log("[TranscriptionSystem] Found theme sprite among loaded assets (build runtime).");
                        break;
                    }
                }
            }

            if (themeBackgroundSprite == null) Debug.LogWarning("[TranscriptionSystem] Theme Background Sprite not assigned! UI will have sharp corners.");
            
            // Check if exists
            var existing = FindFirstObjectByType<TranscriptPanel>();
            if (existing != null)
            {
                Debug.Log("[TranscriptionSystem] Found existing UI. Linking references and preserving position.");
                transcriptPanel = existing;
                transcriptionManager?.SetTranscriptPanel(transcriptPanel);
                
                // REWIRE LISTENERS - Critical because runtime listeners aren't saved.
                // We assign a FRESH onClick event instance: unlike RemoveAllListeners(),
                // this also wipes any persistent (Inspector-serialized) listeners, so each
                // button can only ever do its one job.
                var buttons = existing.GetComponentsInChildren<Button>(true);
                foreach(var btn in buttons)
                {
                    var txt = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (txt == null) continue;

                    string label = txt.text.Trim();
                    if (label == "REC" || label == "STOP")
                    {
                        recordButton = btn;
                        recordButton.onClick = new Button.ButtonClickedEvent();
                        recordButton.onClick.AddListener(() => {
                            Debug.Log("[Transcription] REC clicked");
                            ToggleTranscription();
                        });
                        Debug.Log("[TranscriptionSystem] Re-wired REC button listener.");
                    }
                    else if (label == "AI")
                    {
                        summaryButton = btn;
                        summaryButton.onClick = new Button.ButtonClickedEvent();
                        summaryButton.onClick.AddListener(() => {
                            Debug.Log("[Transcription] AI summary clicked");
                            transcriptionManager?.RequestSummary();
                        });
                        Debug.Log("[TranscriptionSystem] Re-wired AI button listener.");
                    }
                }

                // REWIRE TEXT REFERENCES - Vital for displaying text
                // Look for the "Text" object inside the Scroll View container structure
                var textComps = existing.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                foreach(var t in textComps)
                {
                    // Identify the main transcript text by name or content or parent
                    if (t.gameObject.name == "TranscriptText")
                    {
                        transcriptText = t;
                        transcriptPanel.SetTranscriptText(transcriptText);
                        Debug.Log("[TranscriptionSystem] Re-wired TranscriptText reference.");
                    }
                    // Rewire Status Text (usually named "StatusText" or similar, let's assume "Text" in header if not named uniquely)
                    // In CreateHeader, we created 'statusText = statusObj.AddComponent<TextMeshProUGUI>();' 
                    // Let's find it by content "Ready" or "RECORDING" or if it is the one in the Header
                    // Ideally we should have named it "StatusText" in CreateHeader.
                    // For now, let's look for the one that is NOT "TranscriptText" and NOT "REC" (the button text)
                    else if (t.text == "Ready" || t.text.Contains("RECORDING") || t.text.Contains("Recording")
                             || t.text == "Stopped" || t.transform.parent.name == "Status")
                    {
                        statusText = t;
                        Debug.Log("[TranscriptionSystem] Re-wired StatusText reference.");
                    }
                }

                // Ensure the language selector exists on the (already serialized) panel.
                EnsureLanguageSelector(existing.transform);

                return; 
            }

            // Calculate pixel size (high resolution for VR)
            float pixelsPerUnit = 1000f; // 1000 pixels per meter
            float pixelWidth = panelWidth * pixelsPerUnit;   // 1200 pixels
            float pixelHeight = panelHeight * pixelsPerUnit; // 800 pixels
            float scale = 1f / pixelsPerUnit; // 0.001 scale

            // Calculate position
            Vector3 position;
            Quaternion rotation = Quaternion.identity;
            
            if (positionInFrontOfCamera)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 forward = cam.transform.forward;
                    forward.y = 0; // Keep panel vertical
                    forward.Normalize();
                    
                    position = cam.transform.position + forward * distanceFromCamera;
                    position.y = cam.transform.position.y + heightOffset;
                    
                    // Face the camera
                    rotation = Quaternion.LookRotation(forward);
                }
                else
                {
                    position = fixedWorldPosition;
                    Debug.LogWarning("[TranscriptionSystem] No main camera found, using fixed position");
                }
            }
            else
            {
                position = fixedWorldPosition;
            }
            
            // Ensure EventSystem exists (required for button clicks)
            // Use FindFirstObjectByType because EventSystem.current might be null in Awake
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                Debug.LogWarning("[TranscriptionSystem] No EventSystem found in scene! Creating one...");
                var eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystem.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
            }

            // Create Canvas
            var canvasObj = new GameObject("TranscriptCanvas");
            // Only set auto-position for NEW canvas
            canvasObj.transform.position = position;
            canvasObj.transform.rotation = rotation;
            
            canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            if (Camera.main != null) canvas.worldCamera = Camera.main;

            var canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(pixelWidth, pixelHeight);
            canvasRect.localScale = Vector3.one * scale;

            canvasObj.AddComponent<CanvasScaler>();
            // Add BOTH raycasters for maximum compatibility
            canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>(); // For mouse/desktop testing
            canvasObj.AddComponent<TrackedDeviceGraphicRaycaster>(); // For XR controllers

            // IMPORTANT: Set to UI Layer for VR Interaction
            int uiLayer = LayerMask.NameToLayer("UI");
            canvasObj.layer = uiLayer;

            // Add Collider as backup for Ray Interactors that rely on physics
            var collider = canvasObj.AddComponent<BoxCollider>();
            collider.size = new Vector3(pixelWidth, pixelHeight, 0.1f / scale); // Thickness relative to scale
            collider.center = new Vector3(0, 0, (0.05f / scale)); // Offset to be slightly behind or centered
            collider.isTrigger = true; // Use Trigger to avoid physical collisions but allow Raycasts

            // Create Main Panel with background
            panelObject = new GameObject("Panel");
            panelObject.transform.SetParent(canvasObj.transform, false);
            panelObject.layer = uiLayer;
            
            var panelRect = panelObject.AddComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            var panelImage = panelObject.AddComponent<Image>();
            // Match Player Appearance UI theme exactly: (0.106, 0.106, 0.106, 0.90)
            panelImage.color = new Color(0.106f, 0.106f, 0.106f, 0.90f);
            
            // Apply Rounded Corners if sprite is provided
            if (themeBackgroundSprite != null)
            {
                panelImage.sprite = themeBackgroundSprite;
                panelImage.type = Image.Type.Sliced;
                panelImage.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
            }

            // Add CanvasGroup for fade effects (like other UIs)
            panelObject.AddComponent<CanvasGroup>();

            // --- REFACTOR: Use Manual Anchors (Drive UI Style) instead of VerticalLayoutGroup ---
            // This ensures pixel-perfect alignment and removes "spacing" artifacts.
            
            // Create Header (Docked Top)
            CreateHeader(panelObject.transform);

            // Create Transcript Area (Stretched to fill remaining space)
            CreateTranscriptArea(panelObject.transform);

            // Create the language selector bar (EN / HE / AR) and push the transcript down.
            EnsureLanguageSelector(panelObject.transform);

            // Recursively set layer
            foreach(Transform t in canvasObj.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = uiLayer;
            }

            // Add TranscriptPanel component
            transcriptPanel = panelObject.AddComponent<TranscriptPanel>();
            transcriptPanel.SetTranscriptText(transcriptText);
            transcriptPanel.SetStatusText(statusText);
            transcriptPanel.SetScrollRect(panelObject.GetComponentInChildren<ScrollRect>());

            // Link to manager
            transcriptionManager?.SetTranscriptPanel(transcriptPanel);

            Debug.Log($"[TranscriptionSystem] UI created! Canvas scale: {scale}, Panel size: {pixelWidth}x{pixelHeight}");
        }

        private void CreateHeader(Transform parent)
        {
            // Header container
            var header = new GameObject("Header");
            header.transform.SetParent(parent, false);

            // DOCK TOP: Height 80
            var headerRect = header.AddComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1); // Top Left
            headerRect.anchorMax = new Vector2(1, 1); // Top Right
            headerRect.pivot = new Vector2(0.5f, 1);  // Pivot Top
            headerRect.sizeDelta = new Vector2(0, 80); // Height 80 (Stretches width)
            headerRect.anchoredPosition = Vector2.zero; // At top perfectly

            var headerBg = header.AddComponent<Image>();
            headerBg.color = new Color(0f, 0f, 0f, 0f);

            var headerLayout = header.AddComponent<HorizontalLayoutGroup>();
            headerLayout.padding = new RectOffset(20, 20, 10, 10);
            headerLayout.spacing = 10;
            headerLayout.childAlignment = TextAnchor.MiddleCenter;
            headerLayout.childControlWidth = false; // Allow Spacer to expand
            headerLayout.childControlHeight = true; // Constrain children to header height
            headerLayout.childForceExpandHeight = false; // Respect preferred height (50px), don't stretch to 60px

            // Title
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(header.transform, false);
            
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "TRANSCRIPT";
            if (themeFont != null) titleText.font = themeFont;
            titleText.fontSize = 18; // Match Player Appearance UI header
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = Color.white;
            titleText.alignment = TextAlignmentOptions.Left;
            titleText.textWrappingMode = TextWrappingModes.NoWrap;

            // Title Layout
            var titleLE = titleObj.AddComponent<LayoutElement>();
            titleLE.preferredWidth = 220;
            titleObj.GetComponent<RectTransform>().sizeDelta = new Vector2(220, 50);

            // SPACER
            var spacer1 = new GameObject("Spacer");
            spacer1.transform.SetParent(header.transform, false);
            var spacer1LE = spacer1.AddComponent<LayoutElement>();
            spacer1LE.flexibleWidth = 1; 

            // Status
            var statusObj = new GameObject("Status");
            statusObj.transform.SetParent(header.transform, false);

            statusText = statusObj.AddComponent<TextMeshProUGUI>();
            statusText.text = "Ready";
            if (themeFont != null) statusText.font = themeFont;
            statusText.fontSize = 16; 
            statusText.color = new Color(0.894f, 0.894f, 0.894f, 1f);
            statusText.alignment = TextAlignmentOptions.Right;
            statusText.textWrappingMode = TextWrappingModes.NoWrap;

            var statusLE = statusObj.AddComponent<LayoutElement>();
            statusLE.preferredWidth = 120;
            statusObj.GetComponent<RectTransform>().sizeDelta = new Vector2(120, 50);

            // Record Button
            var buttonObj = new GameObject("RecordButton");
            buttonObj.transform.SetParent(header.transform, false);

            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.9f, 0.2f, 0.2f, 1f);
            if (themeBackgroundSprite != null)
            {
                buttonImage.sprite = themeBackgroundSprite;
                buttonImage.type = Image.Type.Sliced;
                buttonImage.pixelsPerUnitMultiplier = 4f; // Match Player Appearance UI buttons
            }

            var buttonLE = buttonObj.AddComponent<LayoutElement>();
            buttonLE.preferredWidth = 100;
            buttonLE.preferredHeight = 50; // Fits within header (80 - 20 padding = 60 available)
            // Set the rect explicitly too, so the button has a real clickable area even though
            // the HorizontalLayoutGroup is set to not control child width.
            buttonObj.GetComponent<RectTransform>().sizeDelta = new Vector2(100, 50);

            recordButton = buttonObj.AddComponent<Button>();
            recordButton.targetGraphic = buttonImage;

            var colors = recordButton.colors;
            colors.normalColor = new Color(0.9f, 0.2f, 0.2f, 1f);
            colors.highlightedColor = new Color(1f, 0.3f, 0.3f, 1f);
            colors.pressedColor = new Color(0.7f, 0.15f, 0.15f, 1f);
            colors.selectedColor = new Color(1f, 0.3f, 0.3f, 1f);
            colors.disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f);
            colors.fadeDuration = 0.1f;
            recordButton.colors = colors;

            // Navigation None avoids navigation-related array issues in world-space UI.
            var nav = recordButton.navigation;
            nav.mode = Navigation.Mode.None;
            recordButton.navigation = nav;

            recordButton.onClick.AddListener(() => {
                Debug.Log("[Transcription] REC clicked");
                ToggleTranscription();
            });

            // --- SUMMARY (AI) BUTTON ---
            CreateSummaryButton(header.transform);

            // Button text
            var buttonTextObj = new GameObject("Text");
            buttonTextObj.transform.SetParent(buttonObj.transform, false);

            var buttonTextRect = buttonTextObj.AddComponent<RectTransform>();
            buttonTextRect.anchorMin = Vector2.zero;
            buttonTextRect.anchorMax = Vector2.one;
            buttonTextRect.offsetMin = Vector2.zero;
            buttonTextRect.offsetMax = Vector2.zero;

            var buttonText = buttonTextObj.AddComponent<TextMeshProUGUI>();
            buttonText.text = "REC";
            if (themeFont != null) buttonText.font = themeFont;
            buttonText.fontSize = 20; 
            buttonText.fontStyle = FontStyles.Bold;
            buttonText.color = Color.white;
            buttonText.alignment = TextAlignmentOptions.Center;
        }

        private void CreateTranscriptArea(Transform parent)
        {
            // Scroll View container
            var scrollObj = new GameObject("ScrollView");
            scrollObj.transform.SetParent(parent, false);

            // FIX: NO IMAGE COMPONENT. This prevents the "White Box" artifact.
            // The main panel background is sufficient.

            var scrollRect = scrollObj.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 50f;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // Anchor Stretch-Stretch: Fill remaining space
            // FIX: ScrollRect already adds a RectTransform, so we get it instead of adding it.
            var scrollRectTrans = scrollObj.GetComponent<RectTransform>();
            if (scrollRectTrans == null) scrollRectTrans = scrollObj.AddComponent<RectTransform>();
            
            scrollRectTrans.anchorMin = new Vector2(0, 0); // Bottom Left
            scrollRectTrans.anchorMax = new Vector2(1, 1); // Top Right
            
            // Offsets:
            // Top: -80 (Start below Header)
            // Bottom: 20 (Margin - Match Player Appearance UI)
            // Left/Right: 20 (Margin - Match Player Appearance UI)
            scrollRectTrans.offsetMin = new Vector2(20, 20);
            scrollRectTrans.offsetMax = new Vector2(-20, -80);

            // Viewport
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollObj.transform, false);
            
            var viewportRect = viewport.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            // More padding inside the dark box
            viewportRect.offsetMin = new Vector2(20, 20); 
            viewportRect.offsetMax = new Vector2(-20, -20);

            var viewportMask = viewport.AddComponent<RectMask2D>();
            
            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.offsetMin = new Vector2(0, 0);
            contentRect.offsetMax = new Vector2(0, 0);

            var contentFitter = content.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childForceExpandWidth = true;
            
            // Text object
            var textObj = new GameObject("TranscriptText");
            textObj.transform.SetParent(content.transform, false);

            transcriptText = textObj.AddComponent<TextMeshProUGUI>();
            transcriptText.text = "Tap REC to start..."; // Simple prompt
            if (themeFont != null) transcriptText.font = themeFont;
            transcriptText.fontSize = 28; 
            transcriptText.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            transcriptText.alignment = TextAlignmentOptions.TopLeft;
            transcriptText.textWrappingMode = TextWrappingModes.Normal;
            transcriptText.overflowMode = TextOverflowModes.Overflow;
            transcriptText.richText = true;

            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
        }

        private void CreateSummaryButton(Transform parent)
        {
            // Creates a small "Sparkle" button next to record
            
            var buttonObj = new GameObject("SummaryButton");
            buttonObj.transform.SetParent(parent, false);

            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.2f, 0.6f, 1f, 1f); // Blue
            if (themeBackgroundSprite != null)
            {
                buttonImage.sprite = themeBackgroundSprite;
                buttonImage.type = Image.Type.Sliced;
                buttonImage.pixelsPerUnitMultiplier = 4f;
            }

            var buttonLE = buttonObj.AddComponent<LayoutElement>();
            buttonLE.preferredWidth = 50; // Smaller square button
            buttonLE.preferredHeight = 50;
            buttonObj.GetComponent<RectTransform>().sizeDelta = new Vector2(50, 50);

            summaryButton = buttonObj.AddComponent<Button>();
            summaryButton.targetGraphic = buttonImage;
            var sumNav = summaryButton.navigation;
            sumNav.mode = Navigation.Mode.None;
            summaryButton.navigation = sumNav;
            summaryButton.onClick.AddListener(() => {
                Debug.Log("[Transcription] AI summary clicked");
                transcriptionManager?.RequestSummary();
            });

            // Text / Icon (Using text "Ai" for now)
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(buttonObj.transform, false);
            
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero; 
            textRect.anchorMax = Vector2.one;

            var tmText = textObj.AddComponent<TextMeshProUGUI>();
            tmText.text = "AI"; // Or a star icon if font supports it
            if (themeFont != null) tmText.font = themeFont;
            tmText.fontSize = 18;
            tmText.fontStyle = FontStyles.Bold;
            tmText.alignment = TextAlignmentOptions.Center;
            tmText.color = Color.white;
        }

        /// <summary>
        /// Creates (or re-wires) the EN / HE / AR language selector bar under the header,
        /// and pushes the transcript scroll area down so nothing overlaps. Safe to call on
        /// both freshly generated UIs and pre-existing serialized panels.
        /// </summary>
        private void EnsureLanguageSelector(Transform panel)
        {
            if (panel == null) return;

            int uiLayer = LayerMask.NameToLayer("UI");

            // Already present? Just re-wire the click listeners (runtime listeners aren't saved).
            Transform existingBar = panel.Find("LanguageBar");
            if (existingBar != null)
            {
                languageButtons.Clear();
                foreach (var btn in existingBar.GetComponentsInChildren<Button>(true))
                {
                    var label = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (label == null) continue;

                    TranscriptionLanguage lang = LanguageFromLabel(label.text);
                    languageButtons[lang] = btn;
                    btn.onClick = new Button.ButtonClickedEvent(); // wipes runtime AND serialized listeners
                    var captured = lang;
                    btn.onClick.AddListener(() => SetLanguage(captured));
                }
                UpdateLanguageButtonVisuals();
                return;
            }

            // --- Build a new language bar docked just below the 80px header ---
            const float headerHeight = 80f;
            const float barHeight = 60f;

            var bar = new GameObject("LanguageBar");
            bar.transform.SetParent(panel, false);
            bar.layer = uiLayer;

            var barRect = bar.AddComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0, 1);
            barRect.anchorMax = new Vector2(1, 1);
            barRect.pivot = new Vector2(0.5f, 1);
            barRect.sizeDelta = new Vector2(0, barHeight);
            barRect.anchoredPosition = new Vector2(0, -headerHeight);

            // Transparent background so it blends with the panel.
            var barBg = bar.AddComponent<Image>();
            barBg.color = new Color(0f, 0f, 0f, 0f);

            var barLayout = bar.AddComponent<HorizontalLayoutGroup>();
            barLayout.padding = new RectOffset(20, 20, 6, 6);
            barLayout.spacing = 10;
            barLayout.childAlignment = TextAnchor.MiddleLeft;
            barLayout.childControlWidth = false;
            barLayout.childControlHeight = true;
            barLayout.childForceExpandHeight = false;
            barLayout.childForceExpandWidth = false;

            // "Language" caption
            var captionObj = new GameObject("Caption");
            captionObj.transform.SetParent(bar.transform, false);
            var caption = captionObj.AddComponent<TextMeshProUGUI>();
            caption.text = "Language";
            if (themeFont != null) caption.font = themeFont;
            caption.fontSize = 16;
            caption.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            caption.alignment = TextAlignmentOptions.Left;
            caption.textWrappingMode = TextWrappingModes.NoWrap;
            var captionLE = captionObj.AddComponent<LayoutElement>();
            captionLE.preferredWidth = 120;
            captionLE.preferredHeight = 44;
            captionObj.GetComponent<RectTransform>().sizeDelta = new Vector2(120, 44);

            // Language buttons
            languageButtons.Clear();
            foreach (var lang in languageOrder)
            {
                CreateLanguageButton(bar.transform, lang);
            }

            // Push the transcript scroll area down so it starts below the language bar.
            var scroll = panel.GetComponentInChildren<ScrollRect>(true);
            if (scroll != null)
            {
                var sRect = scroll.GetComponent<RectTransform>();
                var off = sRect.offsetMax;
                off.y = -(headerHeight + barHeight);
                sRect.offsetMax = off;
            }

            // Make sure everything is on the UI layer for XR raycasting.
            foreach (Transform t in bar.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = uiLayer;
            }

            UpdateLanguageButtonVisuals();
            Debug.Log("[TranscriptionSystem] Language selector created (EN / HE / AR).");
        }

        private void CreateLanguageButton(Transform parent, TranscriptionLanguage lang)
        {
            var buttonObj = new GameObject($"Lang_{lang.ShortLabel()}");
            buttonObj.transform.SetParent(parent, false);

            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = k_LangIdle;
            if (themeBackgroundSprite != null)
            {
                buttonImage.sprite = themeBackgroundSprite;
                buttonImage.type = Image.Type.Sliced;
                buttonImage.pixelsPerUnitMultiplier = 4f;
            }

            var buttonLE = buttonObj.AddComponent<LayoutElement>();
            buttonLE.preferredWidth = 70;
            buttonLE.preferredHeight = 44;
            // Set the rect explicitly too, so it sizes correctly even though the
            // HorizontalLayoutGroup is set to not control child width.
            var buttonRect = buttonObj.GetComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(70, 44);

            var button = buttonObj.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            var nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;

            var captured = lang;
            button.onClick.AddListener(() => SetLanguage(captured));

            languageButtons[lang] = button;

            // Label
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(buttonObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var tmText = textObj.AddComponent<TextMeshProUGUI>();
            tmText.text = lang.ShortLabel();
            if (themeFont != null) tmText.font = themeFont;
            tmText.fontSize = 20;
            tmText.fontStyle = FontStyles.Bold;
            tmText.alignment = TextAlignmentOptions.Center;
            tmText.color = Color.white;
        }

        private TranscriptionLanguage LanguageFromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return TranscriptionLanguage.English;
            switch (label.Trim().ToUpperInvariant())
            {
                case "HE": return TranscriptionLanguage.Hebrew;
                case "AR": return TranscriptionLanguage.Arabic;
                default: return TranscriptionLanguage.English;
            }
        }

        private void OnSummaryReceived(string summary)
        {
            // Show Local Popup
            Debug.Log($"[TranscriptionSystem] Showing Summary: {summary.Substring(0, Mathf.Min(20, summary.Length))}...");
            
            // Lazy create the panel if it doesn't exist
            if (summaryPanel == null)
            {
                CreateSummaryPanel();
            }
            summaryPanel?.Show(summary);
        }

        private void CreateSummaryPanel()
        {
            int uiLayer = LayerMask.NameToLayer("UI");

            // The canvas field is only set on the fresh-creation path; when an existing
            // serialized panel was rewired, resolve the canvas from the transcript panel.
            if (canvas == null && transcriptPanel != null)
            {
                canvas = transcriptPanel.GetComponentInParent<Canvas>();
            }
            if (canvas == null)
            {
                Debug.LogError("[TranscriptionSystem] No canvas available for the summary popup.");
                return;
            }

            // Fixed-size overlay panel. Top anchor stays BELOW the header (y 0.85) so the
            // popup never covers the REC / AI buttons.
            var panelObj = new GameObject("SummaryPanel");
            panelObj.transform.SetParent(canvas.transform, false);

            var rect = panelObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.05f, 0.05f);
            rect.anchorMax = new Vector2(0.95f, 0.85f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var img = panelObj.AddComponent<Image>();
            img.color = new Color(0.1f, 0.1f, 0.15f, 0.98f); // Dark blue background
            if (themeBackgroundSprite != null) {
                img.sprite = themeBackgroundSprite;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
            }

            // Title (top left)
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(panelObj.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0, 1);
            titleRect.pivot = new Vector2(0, 1);
            titleRect.anchoredPosition = new Vector2(20, -14);
            titleRect.sizeDelta = new Vector2(300, 36);
            var titleTmp = titleObj.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "AI SUMMARY";
            if (themeFont != null) titleTmp.font = themeFont;
            titleTmp.fontSize = 18;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Left;

            // Close Button (top right)
            var closeObj = new GameObject("CloseBtn");
            closeObj.transform.SetParent(panelObj.transform, false);
            var closeRect = closeObj.AddComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1, 1);
            closeRect.anchorMax = new Vector2(1, 1);
            closeRect.pivot = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-10, -10);
            closeRect.sizeDelta = new Vector2(44, 44);

            var closeImg = closeObj.AddComponent<Image>();
            closeImg.color = new Color(0.85f, 0.2f, 0.2f, 1f);
            if (themeBackgroundSprite != null)
            {
                closeImg.sprite = themeBackgroundSprite;
                closeImg.type = Image.Type.Sliced;
                closeImg.pixelsPerUnitMultiplier = 4f;
            }
            var closeBtn = closeObj.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            var closeNav = closeBtn.navigation;
            closeNav.mode = Navigation.Mode.None;
            closeBtn.navigation = closeNav;

            var closeLabelObj = new GameObject("Text");
            closeLabelObj.transform.SetParent(closeObj.transform, false);
            var closeLabelRect = closeLabelObj.AddComponent<RectTransform>();
            closeLabelRect.anchorMin = Vector2.zero;
            closeLabelRect.anchorMax = Vector2.one;
            closeLabelRect.offsetMin = Vector2.zero;
            closeLabelRect.offsetMax = Vector2.zero;
            var closeLabel = closeLabelObj.AddComponent<TextMeshProUGUI>();
            closeLabel.text = "X";
            if (themeFont != null) closeLabel.font = themeFont;
            closeLabel.fontSize = 20;
            closeLabel.fontStyle = FontStyles.Bold;
            closeLabel.color = Color.white;
            closeLabel.alignment = TextAlignmentOptions.Center;

            // --- Scrollable content area (keeps long summaries inside the panel) ---
            var scrollObj = new GameObject("SummaryScroll");
            scrollObj.transform.SetParent(panelObj.transform, false);
            var scroll = scrollObj.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 40f;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var scrollRectTrans = scrollObj.GetComponent<RectTransform>();
            scrollRectTrans.anchorMin = new Vector2(0, 0);
            scrollRectTrans.anchorMax = new Vector2(1, 1);
            scrollRectTrans.offsetMin = new Vector2(20, 20);   // bottom/left padding
            scrollRectTrans.offsetMax = new Vector2(-20, -56); // below title/close row

            var viewportObj = new GameObject("Viewport");
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var viewportRect = viewportObj.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportObj.AddComponent<RectMask2D>(); // hard-clips overflowing text

            // The text itself is the scroll content; it grows vertically with the summary.
            var textObj = new GameObject("SummaryText");
            textObj.transform.SetParent(viewportObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 1);
            textRect.anchorMax = new Vector2(1, 1);
            textRect.pivot = new Vector2(0.5f, 1);
            textRect.offsetMin = new Vector2(0, 0);
            textRect.offsetMax = new Vector2(0, 0);

            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            if (themeFont != null) tmp.font = themeFont;
            tmp.fontSize = 26; // readable in VR
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow; // grows; mask + scroll handle it
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.richText = true;

            var fitter = textObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewportRect;
            scroll.content = textRect;

            // Everything on the UI layer so XR ray interaction works on the popup too.
            panelObj.layer = uiLayer;
            foreach (Transform t in panelObj.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = uiLayer;
            }

            summaryPanel = panelObj.AddComponent<SummaryPanel>();
            var cg = panelObj.AddComponent<CanvasGroup>();
            summaryPanel.Setup(tmp, closeBtn, cg, scroll);

            panelObj.SetActive(false);
        }


    }
}
