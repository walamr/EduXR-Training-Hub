using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using XRMultiplayer;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// UI Manager for Firebase Presentation System.
    /// Displays generated code for the user to enter on web dashboard.
    /// </summary>
    [RequireComponent(typeof(GraphicRaycaster))]
    [RequireComponent(typeof(Canvas))]
    public class PresentationUIManager : MonoBehaviour
    {
        public static PresentationUIManager Instance { get; private set; }

        [Header("Manager References")]
        [SerializeField] private FirebaseStorageManager storageManager;
        [SerializeField] private PresentationNetworkManager networkManager;
        [SerializeField] private PresentationTVManager tvManager;
        [SerializeField] private FirestoreRoomSync roomSync;
        
        [Header("Login UI (Code Display)")]
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private TextMeshProUGUI codeDisplayText;
        [SerializeField] private Button generateCodeButton;
        [SerializeField] private TextMeshProUGUI loginInstructions;
        [SerializeField] private Button closeAuthPanelButton;
        
        [Header("File List UI")]
        [SerializeField] private GameObject filePanel;
        [SerializeField] private Transform fileListContainer;
        [SerializeField] private GameObject fileButtonPrefab;
        [SerializeField] private Button refreshButton;
        [SerializeField] private GameObject loadingIndicator;
        
        [Header("Navigation UI")]
        [SerializeField] private Button nextButton;
        [SerializeField] private Button prevButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private TextMeshProUGUI pageText;
        
        [Header("Status")]
        [SerializeField] private TextMeshProUGUI statusText;

        [Header("Debug")]
        [SerializeField] private bool debugMode = true;

        // Currently selected document's page URLs
        private List<string> currentPageUrls = new List<string>();
        private string currentDocumentName = "";
        private Coroutine m_LoadRoutine;
        private bool m_CoreReady;
        private bool m_EventSubscriptions;
        const string k_CloseAuthButtonName = "CloseAuthButton";

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[PresentationUIManager] Duplicate instance; keeping first.");
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            StartCoroutine(InitializeRoutine());
        }

        private IEnumerator InitializeRoutine()
        {
            if (debugMode) Debug.Log("[PresentationUIManager] Initializing...");

            // 1. Wait efficiently for managers
            float timeout = 5f;
            float timer = 0f;

            while (timer < timeout)
            {
                if (storageManager == null) storageManager = FindFirstObjectByType<FirebaseStorageManager>();
                if (networkManager == null) networkManager = FindFirstObjectByType<PresentationNetworkManager>();
                if (tvManager == null) tvManager = FindFirstObjectByType<PresentationTVManager>();
                if (roomSync == null) roomSync = FindFirstObjectByType<FirestoreRoomSync>();

                if (storageManager != null && networkManager != null)
                {
                    break;
                }

                yield return new WaitForSeconds(0.2f);
                timer += 0.2f;
            }

            if (storageManager == null) Debug.LogError("[PresentationUIManager] CRITICAL: FirebaseStorageManager not found!");
            if (networkManager == null) Debug.LogError("[PresentationUIManager] CRITICAL: PresentationNetworkManager not found!");

            EnsureCoreReady();

            if (networkManager != null)
            {
                networkManager.OnPageChanged -= OnPageChanged;
                networkManager.OnPageChanged += OnPageChanged;
            }
            
            if (roomSync != null)
            {
                roomSync.OnError -= OnSyncError;
                roomSync.OnError += OnSyncError;
            }
            
            if (tvManager != null)
            {
                tvManager.OnLoadingStateChanged -= OnLoadingStateChanged;
                tvManager.OnLoadingStateChanged += OnLoadingStateChanged;
            }
            
            if (refreshButton) 
            {
                refreshButton.onClick.RemoveAllListeners();
                refreshButton.onClick.AddListener(() => {
                    Debug.Log("[PresentationUIManager] 'Refresh' Button Clicked!");
                    OnRefreshClicked();
                });
            }

            if (nextButton) nextButton.onClick.AddListener(OnNextClicked);
            if (prevButton) prevButton.onClick.AddListener(OnPrevClicked);
            if (stopButton) stopButton.onClick.AddListener(OnStopClicked);
            
            // 4. Initial UI State
            if (loginInstructions)
                loginInstructions.text = "Click 'Generate Code' to start\nEnter the code on the web dashboard";
            
            if (codeDisplayText)
                codeDisplayText.text = "------";
            
            if (loadingIndicator) loadingIndicator.SetActive(false);
            
            UpdateUIState();
            
            // Deactivate/hide the canvas at startup so it does not block the HoloBoard or other spatial interactions
            HideAuthPanel();
            
            if (debugMode) Debug.Log("[PresentationUIManager] Initialization Complete.");
        }
        
        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            if (storageManager != null)
            {
                storageManager.OnUserChanged -= OnUserChanged;
                storageManager.OnError -= OnError;
                storageManager.OnAuthenticationRequired -= OnAuthenticationRequired;
                storageManager.OnCodeGenerated -= OnCodeGenerated;
            }
            if (networkManager != null)
            {
                networkManager.OnPageChanged -= OnPageChanged;
            }
            if (roomSync != null)
            {
                roomSync.OnError -= OnSyncError;
            }
            if (tvManager != null)
            {
                tvManager.OnLoadingStateChanged -= OnLoadingStateChanged;
            }
        }

        #region Event Handlers
        
        private void OnUserChanged(string userId)
        {
            UpdateUIState();
            if (!string.IsNullOrEmpty(userId))
            {
                SetStatus($"Logged in as {storageManager.GetUserEmail()}");
                OnRefreshClicked();
            }
        }
        
        private void OnError(string error)
        {
            SetStatus($"Error: {error}");
            Debug.LogError($"[PresentationUIManager] Error: {error}");
            if (loadingIndicator) loadingIndicator.SetActive(false);
            
            // Reset code display on error
            if (codeDisplayText) codeDisplayText.text = "------";
            if (generateCodeButton) generateCodeButton.interactable = true;
        }

        private void OnAuthenticationRequired(string featureContext)
        {
            ShowAuthPanel(featureContext);
        }

        /// <summary>Shows the login / pairing panel (e.g. when a Firebase feature needs auth).</summary>
        public static void RequestAuthentication(string featureContext = null)
        {
            var ui = Instance != null
                ? Instance
                : FindFirstObjectByType<PresentationUIManager>(FindObjectsInactive.Include);

            if (ui != null)
                ui.ShowAuthPanel(featureContext);
            else
                Debug.LogWarning($"[PresentationUIManager] Auth required for {featureContext ?? "Firebase"} but no UI was found.");
        }

        public void ShowAuthPanel(string featureContext = null)
        {
            EnsureCoreReady();

            if (storageManager == null)
                storageManager = FindFirstObjectByType<FirebaseStorageManager>();

            bool loggedIn = storageManager != null && storageManager.IsAuthenticated;
            if (loggedIn)
                return;

            if (loginPanel)
                loginPanel.SetActive(true);
            if (filePanel)
                filePanel.SetActive(false);

            string label = string.IsNullOrEmpty(featureContext) ? "this feature" : featureContext;
            SetStatus($"Sign in to use {label}");

            if (loginInstructions)
            {
                loginInstructions.text =
                    $"Sign in required for {label}.\nGo to xr-meeting-hub.web.app/pair\nEnter the code below (or tap Generate Code):";
            }

            if (codeDisplayText && string.IsNullOrWhiteSpace(codeDisplayText.text.Replace("-", "")))
                codeDisplayText.text = "------";
            if (generateCodeButton)
                generateCodeButton.interactable = true;

            PresentPanelInFrontOfUser();
        }

        public void HideAuthPanel()
        {
            if (QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.CloseFloatingPanel(gameObject);
            else
                gameObject.SetActive(false);
        }

        void OnCloseAuthPanelClicked()
        {
            HideAuthPanel();
        }

        void PresentPanelInFrontOfUser()
        {
            if (QuickMenuManager.Instance != null)
            {
                QuickMenuManager.Instance.OpenFloatingPanel(gameObject);
                return;
            }

            EnsureHierarchyActive();
            gameObject.SetActive(true);
            SnapToMainCamera();
        }

        void SnapToMainCamera()
        {
            var cam = Camera.main;
            if (cam == null)
                return;

            const float distance = 1.2f;
            const float heightOffset = -0.1f;

            Vector3 forward = cam.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f)
                forward = cam.transform.forward;
            forward.Normalize();

            transform.position = cam.transform.position + forward * distance + Vector3.up * heightOffset;

            Vector3 lookDir = transform.position - cam.transform.position;
            if (lookDir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(lookDir);
        }

        /// <summary>
        /// Wires Firebase listeners and auth buttons even when this canvas has never been opened from Quick Menu.
        /// </summary>
        void EnsureCoreReady()
        {
            if (m_CoreReady)
                return;

            EnsureHierarchyActive();

            if (storageManager == null)
                storageManager = FirebaseStorageManager.Instance != null
                    ? FirebaseStorageManager.Instance
                    : FindFirstObjectByType<FirebaseStorageManager>();

            EnsureEventSubscriptions();
            EnsureCloseAuthButton();
            WireAuthButtons();

            m_CoreReady = true;
        }

        void EnsureEventSubscriptions()
        {
            if (m_EventSubscriptions || storageManager == null)
                return;

            storageManager.OnUserChanged -= OnUserChanged;
            storageManager.OnUserChanged += OnUserChanged;
            storageManager.OnError -= OnError;
            storageManager.OnError += OnError;
            storageManager.OnAuthenticationRequired -= OnAuthenticationRequired;
            storageManager.OnAuthenticationRequired += OnAuthenticationRequired;
            storageManager.OnCodeGenerated -= OnCodeGenerated;
            storageManager.OnCodeGenerated += OnCodeGenerated;

            m_EventSubscriptions = true;
        }

        void WireAuthButtons()
        {
            if (generateCodeButton)
            {
                generateCodeButton.onClick.RemoveAllListeners();
                generateCodeButton.onClick.AddListener(OnGenerateCodeClicked);
            }

            if (closeAuthPanelButton)
            {
                closeAuthPanelButton.onClick.RemoveAllListeners();
                closeAuthPanelButton.onClick.AddListener(OnCloseAuthPanelClicked);
            }
        }

        void EnsureHierarchyActive()
        {
            Transform t = transform;
            while (t != null)
            {
                if (!t.gameObject.activeSelf)
                    t.gameObject.SetActive(true);
                t = t.parent;
            }
        }

        void EnsureCloseAuthButton()
        {
            if (closeAuthPanelButton != null)
                return;
            if (loginPanel == null)
                return;

            var existing = loginPanel.transform.Find(k_CloseAuthButtonName);
            if (existing != null)
            {
                closeAuthPanelButton = existing.GetComponent<Button>();
                return;
            }

            var btnGO = new GameObject(k_CloseAuthButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(loginPanel.transform, false);

            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-16f, -16f);
            rt.sizeDelta = new Vector2(52f, 52f);

            var img = btnGO.GetComponent<Image>();
            img.color = new Color(0.2f, 0.22f, 0.28f, 0.95f);
            img.raycastTarget = true;

            closeAuthPanelButton = btnGO.GetComponent<Button>();
            var colors = closeAuthPanelButton.colors;
            colors.normalColor = img.color;
            colors.highlightedColor = new Color(0.3f, 0.35f, 0.45f, 1f);
            colors.pressedColor = new Color(0.15f, 0.18f, 0.25f, 1f);
            closeAuthPanelButton.colors = colors;

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(btnGO.transform, false);
            var textRT = textGO.GetComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;

            var label = textGO.AddComponent<TextMeshProUGUI>();
            label.text = "X";
            label.fontSize = 26;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
        }
        
        private void OnSyncError(string error)
        {
            SetStatus($"Sync Error: {error}");
            Debug.LogError($"[PresentationUIManager] Sync Error: {error}");
            // Optional: Revert UI state if needed
        }
        
        private void OnCodeGenerated(string code)
        {
            Debug.Log($"[PresentationUIManager] Code generated: {code}");
            
            if (codeDisplayText)
            {
                codeDisplayText.text = code;
            }
            
            if (loginInstructions)
            {
                loginInstructions.text = "Go to xr-meeting-hub.web.app/pair\nEnter this code:";
            }
            
            SetStatus("Waiting for authentication...");
            
            // Disable generate button while waiting
            if (generateCodeButton) generateCodeButton.interactable = false;
        }
        
        private void OnLoadingStateChanged(bool isLoading)
        {
            if (loadingIndicator) loadingIndicator.SetActive(isLoading);
        }
        
        private void OnPageChanged(int currentPage, int totalPages)
        {
            UpdatePageText(currentPage, totalPages);
        }
        
        #endregion

        #region UI State

        bool EnsureAuthForAction(string operation)
        {
            if (storageManager == null)
            {
                RequestAuthentication(operation);
                return false;
            }
            return storageManager.TryEnsureAuthenticated(operation);
        }
        
        private void UpdateUIState()
        {
            bool loggedIn = storageManager != null && storageManager.IsAuthenticated;
            
            Debug.Log($"[PresentationUIManager] UpdateUIState: loggedIn={loggedIn}");
            
            if (loginPanel)
            {
                loginPanel.SetActive(!loggedIn);
                Debug.Log($"[PresentationUIManager] loginPanel.SetActive({!loggedIn})");
            }
            
            // Show file panel when logged in
            if (filePanel != null)
            {
                filePanel.SetActive(loggedIn);
                Debug.Log($"[PresentationUIManager] filePanel.SetActive({loggedIn})");
            }
            else if (fileListContainer != null && loggedIn)
            {
                // Fallback: if filePanel is null, try to activate parent hierarchy
                Debug.LogWarning("[PresentationUIManager] filePanel is NULL! Trying to show container's parent hierarchy as fallback.");
                Transform parent = fileListContainer.parent;
                while (parent != null && parent != transform)
                {
                    parent.gameObject.SetActive(true);
                    Debug.Log($"[PresentationUIManager] Activated parent: {parent.name}");
                    parent = parent.parent;
                }
            }
            
            if (refreshButton) refreshButton.interactable = loggedIn;
            if (generateCodeButton) generateCodeButton.interactable = !loggedIn;
            if (nextButton) nextButton.interactable = loggedIn;
            if (prevButton) prevButton.interactable = loggedIn;
            if (stopButton) stopButton.interactable = loggedIn;
            
            if (!loggedIn)
            {
                SetStatus("Generate a code to link your device");
                if (codeDisplayText) codeDisplayText.text = "------";
            }
        }
        
        private void UpdatePageText(int current, int total)
        {
            if (pageText)
            {
                if (total > 1)
                    pageText.text = $"{current + 1} / {total}";
                else
                    pageText.text = "";
            }
            
            if (prevButton) prevButton.interactable = current > 0;
            if (nextButton) nextButton.interactable = current < total - 1;
        }
        
        private void SetStatus(string text)
        {
            if (statusText) statusText.text = text;
        }
        
        #endregion

        #region Button Handlers
        
        private void OnGenerateCodeClicked()
        {
            if (storageManager == null)
                return;

            SetStatus("Generating code...");
            storageManager.GenerateCode();
        }

        private void OnRefreshClicked()
        {
            if (!EnsureAuthForAction("presentation files")) return;
            
            SetStatus("Loading documents...");
            
            storageManager.ListConvertedDocuments(
                (documents) => {
                    StartCoroutine(PopulateDocumentList(documents));
                },
                OnError
            );
        }
        
        private void OnNextClicked()
        {
            PresentationNavigator.RequestNextPage();
        }
        
        private void OnPrevClicked()
        {
            PresentationNavigator.RequestPreviousPage();
        }
        
        private void OnStopClicked()
        {
            PresentationNavigator.RequestStopPresentation();
            SetStatus("Presentation stopped");
            currentPageUrls.Clear();
            currentDocumentName = "";
            UpdatePageText(0, 1);
        }
        
        #endregion

        #region File List
        
        private IEnumerator PopulateDocumentList(List<FirebaseStorageManager.FileInfo> documents)
        {
            Debug.Log($"[PresentationUIManager] PopulateDocumentList with {documents.Count} documents");
            
            if (fileListContainer == null)
            {
                Debug.LogError("[PresentationUIManager] fileListContainer is NULL!");
                yield break;
            }
            
            foreach (Transform child in fileListContainer) Destroy(child.gameObject);
            yield return null;

            if (documents.Count == 0)
            {
                SetStatus("No documents found. Upload via web dashboard.");
                yield break;
            }

            foreach (var doc in documents)
            {
                CreateDocumentButton(doc);
            }
            
            yield return new WaitForEndOfFrame();
            
            if (fileListContainer != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(fileListContainer.GetComponent<RectTransform>());
            }
            
            SetStatus($"Found {documents.Count} documents");
            
            // CRITICAL FIX: Disable LayoutGroup width control to prevent zero-width forcing
            var vlg = fileListContainer.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.childControlWidth = false; 
                vlg.childForceExpandWidth = false;
            }
        }
        
        private void CreateDocumentButton(FirebaseStorageManager.FileInfo docInfo)
        {
            if (fileButtonPrefab == null || fileListContainer == null)
            {
                Debug.LogError("[PresentationUIManager] fileButtonPrefab or fileListContainer is null!");
                return;
            }

            Debug.Log($"[PresentationUIManager] Creating button for: {docInfo.name}");
            
            // FIX: Disable Mask on Viewport (parent of fileListContainer) to prevent clipping
            if (fileListContainer.parent != null)
            {
                var mask = fileListContainer.parent.GetComponent<Mask>();
                if (mask != null && mask.enabled)
                {
                    mask.enabled = false;
                    Debug.Log("[PresentationUIManager] Disabled Mask on Viewport to fix clipping");
                }
                var rectMask = fileListContainer.parent.GetComponent<RectMask2D>();
                if (rectMask != null && rectMask.enabled)
                {
                    rectMask.enabled = false;
                    Debug.Log("[PresentationUIManager] Disabled RectMask2D on Viewport to fix clipping");
                }
            }
            
            // Parent to proper fileListContainer (Content)
            GameObject btnObj = Instantiate(fileButtonPrefab, fileListContainer);
            btnObj.name = docInfo.name;
            
            // Reset transform properties
            btnObj.transform.localScale = Vector3.one;
            btnObj.transform.localPosition = Vector3.zero;
            btnObj.transform.localRotation = Quaternion.identity;
            
            // LAYOUT ELEMENT - Critical for visibility in ScrollView
            var le = btnObj.GetComponent<LayoutElement>();
            if (le == null) le = btnObj.AddComponent<LayoutElement>();
            le.minHeight = 70f;
            le.preferredHeight = 70f;
            le.flexibleHeight = 0;
            le.preferredWidth = 650f;
            le.flexibleWidth = 1f;
            
            // For RectTransform in scroll view
            var rt = btnObj.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchoredPosition = Vector2.zero;
                rt.localPosition = Vector3.zero;
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(0.5f, 1);
                rt.sizeDelta = new Vector2(-20, 70); // Slight horizontal margin
            }
            
            // STYLE: Button background with modern dark theme
            var img = btnObj.GetComponent<Image>();
            if (img != null)
            {
                // Modern dark blue-gray gradient feel
                img.color = new Color(0.15f, 0.18f, 0.25f, 0.95f);
            }
            
            // STYLE: Text with clean white color
            var tmp = btnObj.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null) 
            {
                tmp.text = docInfo.name;
                tmp.color = Color.white;
                tmp.fontSize = 26;
                tmp.fontStyle = FontStyles.Normal;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.margin = new Vector4(20, 0, 20, 0); // Left/right padding
            }
            
            // STYLE: Button hover colors
            var btn = btnObj.GetComponent<Button>();
            if (btn)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnDocumentClicked(docInfo));
                
                // Set nice color transitions
                var colors = btn.colors;
                colors.normalColor = new Color(0.15f, 0.18f, 0.25f, 0.95f);
                colors.highlightedColor = new Color(0.25f, 0.35f, 0.55f, 1f); // Blue highlight
                colors.pressedColor = new Color(0.1f, 0.4f, 0.7f, 1f);        // Bright blue press
                colors.selectedColor = new Color(0.2f, 0.3f, 0.45f, 1f);
                colors.fadeDuration = 0.1f;
                btn.colors = colors;
            }
            
            // Activate the button
            btnObj.SetActive(true);
            
            // Force Layout Rebuild
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(fileListContainer.GetComponent<RectTransform>());
        }
        
        private void OnDocumentClicked(FirebaseStorageManager.FileInfo fileInfo)
        {
            if (!EnsureAuthForAction("presentation pages")) return;

            Debug.Log($"[PresentationUIManager] Document clicked: {fileInfo.name}, Path: {fileInfo.fullPath}");
            currentDocumentName = fileInfo.name;
            if (loadingIndicator) loadingIndicator.SetActive(true);
            
            // Stop any existing loading routine before starting a new search
            if (m_LoadRoutine != null) StopCoroutine(m_LoadRoutine);
            
            // First get all pages for this document
            storageManager.ListDocumentPages(fileInfo.fullPath, 
                pages => {
                    Debug.Log($"[PresentationUIManager] Received {pages.Count} pages for {fileInfo.name}");
                    // Stop again in case another click happened while listing pages
                    if (m_LoadRoutine != null) StopCoroutine(m_LoadRoutine);
                    m_LoadRoutine = StartCoroutine(LoadDocumentPages(pages));
                },
                error => {
                    SetStatus($"Failed to load: {error}");
                    if (loadingIndicator) loadingIndicator.SetActive(false);
                }
            );
        }
        
        private IEnumerator LoadDocumentPages(List<FirebaseStorageManager.FileInfo> pages)
        {
            Debug.Log($"[PresentationUIManager] LoadDocumentPages called with {pages.Count} pages.");
            // Lazy find roomSync if missing (it might be created late by NetworkManager)
            if (roomSync == null) roomSync = FirestoreRoomSync.Instance ?? FindFirstObjectByType<FirestoreRoomSync>();

            // Use a local list for loading to avoid concurrent modification issues with currentPageUrls
            List<string> pageUrls = new List<string>(new string[pages.Count]);
            
            if (pages.Count == 0)
            {
                SetStatus("Document has no pages");
                m_LoadRoutine = null;
                yield break;
            }
            
            SetStatus($"Loading {pages.Count} pages...");
            
            // Get download URLs for all pages
            int loadedCount = 0;
            
            for (int i = 0; i < pages.Count; i++)
            {
                int index = i;
                var page = pages[i];
                
                storageManager.GetDownloadUrl(page.fullPath,
                    (url) => {
                        if (index < pageUrls.Count)
                            pageUrls[index] = url;
                        loadedCount++;
                    },
                    (error) => {
                        Debug.LogError($"Failed to get URL for {page.name}: {error}");
                        loadedCount++;
                    }
                );
            }
            
            // Wait for all URLs
            float timeOut = 20f;
            float timer = 0f;
            while (loadedCount < pages.Count)
            {
                yield return new WaitForSeconds(0.1f);
                timer += 0.1f;
                if (timer > timeOut)
                {
                    Debug.LogError("[PresentationUIManager] Timed out waiting for page URLs");
                    SetStatus("Load timed out (check logs)");
                    if (loadingIndicator) loadingIndicator.SetActive(false);
                    m_LoadRoutine = null;
                    yield break;
                }
            }
            
            // Remove any nulls (failed loads)
            pageUrls.RemoveAll(u => string.IsNullOrEmpty(u));
            
            if (pageUrls.Count == 0)
            {
                SetStatus("Failed to load pages");
                m_LoadRoutine = null;
                yield break;
            }

            // Apply results to the shared member variable now that we're done
            currentPageUrls = pageUrls;
            
            // First, sync to Firebase RTDB for late joiners
            if (roomSync != null)
            {
                Debug.Log($"[PresentationUIManager] Requesting Sync for: {currentDocumentName} with {currentPageUrls.Count} pages");
                roomSync.SetPresentation(currentDocumentName, currentPageUrls.ToArray());
                SetStatus($"Presenting: {currentDocumentName}");
            }
            
            // ALWAYS sync to network manager as well, to ensure Netcode-only viewers receive the update
            // (especially important if the Host is not logged into Firebase)
            if (networkManager != null)
            {
                networkManager.RequestSetDocument(currentPageUrls);
            }
            else if (roomSync == null)
            {
                // Local only - just show first page
                if (tvManager != null && currentPageUrls.Count > 0)
                {
                    tvManager.LoadUrl(currentPageUrls[0]);
                }
            }

            UpdatePageText(0, currentPageUrls.Count);
            if (loadingIndicator) loadingIndicator.SetActive(false);
            m_LoadRoutine = null;
        }
        
        #endregion
    }
}
