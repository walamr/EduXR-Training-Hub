using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Represents a workstation consisting of a Chair, a Computer (logic), and a Screen (Quad).
    /// Handles the local instantiation of UI when the local player sits.
    /// </summary>
    public class Workstation : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("The ID of this workstation (matches Chair ID, Quad ID, etc.)")]
        [SerializeField] private int workstationId;

        [Header("References")]
        [Tooltip("The Quad transform where the UI should appear.")]
        [SerializeField] private Transform quadTransform;

        [Tooltip("The UI Prefab to instantiate (e.g., GridMenuExample3x3).")]
        [SerializeField] private GameObject gridMenuPrefab;

        [Header("Wing Panels")]
        [Tooltip("The WingSlotManager on this Workstation (auto-found if left empty).")]
        [SerializeField] private WingSlotManager wingSlotManager;

        [Tooltip("Optional: override prefab for Browser panel (uses default if empty).")]
        [SerializeField] private GameObject browserPanelPrefab;

        [Tooltip("Optional: override prefab for Gemini panel (uses default if empty).")]
        [SerializeField] private GameObject geminiPanelPrefab;

        [Header("Runtime")]
        [SerializeField] private GameObject currentUIInstance;

        [Header("Scaling Settings")]
        [Tooltip("If true, attempts to resize the UI (RectTransform) to match the Quad size exactly (1x1).")]
        [SerializeField] private bool fitToScreen = true;
        
        [Tooltip("Additional scale multiplier if auto-fit isn't perfect.")]
        [SerializeField] private Vector3 manualScale = Vector3.one;

        [Header("Alignment Tweaks")]
        [Tooltip("Offset the position relative to the Quad center.")]
        [SerializeField] private Vector3 positionOffset = Vector3.zero;

        [Tooltip("Offset the rotation (in degrees).")]
        [SerializeField] private Vector3 rotationOffset = Vector3.zero;

        [Tooltip("Push the UI slightly forward to avoid Z-fighting (overlapping flickers).")]
        [SerializeField] private float zBias = -0.01f;

        [Header("Extras")]
        [Tooltip("If true, the Quad's MeshRenderer will be disabled when UI is active (removes glare/reflection).")]
        [SerializeField] private bool hideQuadOnSit = true;

        public int WorkstationId => workstationId;
        public Transform QuadTransform => quadTransform;
        public WingSlotManager WingSlots => wingSlotManager;

        private void Awake()
        {
            // Auto-find WingSlotManager on this GameObject if not assigned
            if (wingSlotManager == null)
                wingSlotManager = GetComponent<WingSlotManager>();
        }

        // ─── Wing Panel API (call from UI buttons) ─────────────────────

        /// <summary>Call from Browser button OnClick.</summary>
        public void OpenBrowserPanel()
        {
            if (wingSlotManager == null)
            {
                Debug.LogWarning("[Workstation] WingSlotManager not found! Add it to this GameObject.");
                return;
            }
            wingSlotManager.OpenPanel(browserPanelPrefab);
        }

        /// <summary>
        /// Use this with Toggle's "On Value Changed (Boolean)".
        /// Opens panel when isOn=true, closes right wing when isOn=false.
        /// </summary>
        public void OnBrowserToggleChanged(bool isOn)
        {
            if (wingSlotManager == null) return;
            if (isOn)
                wingSlotManager.OpenPanel(browserPanelPrefab);
            else
                wingSlotManager.ClosePanel(WingSlotManager.Wing.Right);
        }

        /// <summary>Call from Gemini button OnClick.</summary>
        public void OpenGeminiPanel()
        {
            if (wingSlotManager == null)
            {
                Debug.LogWarning("[Workstation] WingSlotManager not found!");
                return;
            }
            wingSlotManager.OpenPanel(geminiPanelPrefab);
        }

        /// <summary>
        /// Use this with Gemini Toggle's "On Value Changed (Boolean)".
        /// </summary>
        public void OnGeminiToggleChanged(bool isOn)
        {
            if (wingSlotManager == null) return;
            if (isOn)
                wingSlotManager.OpenPanel(geminiPanelPrefab);
            else
                wingSlotManager.ClosePanel(WingSlotManager.Wing.Left);
        }

        /// <summary>Closes all wing panels.</summary>
        public void CloseAllPanels()
        {
            wingSlotManager?.CloseAll();
        }

        /// <summary>
        /// Called when the LOCAL player sits on the linked chair.
        /// </summary>
        public void OnLocalPlayerSitting()
        {
            if (currentUIInstance != null)
            {
                // Already active
                return;
            }

            if (gridMenuPrefab != null && quadTransform != null)
            {
                // Instantiate the UI
                currentUIInstance = Instantiate(gridMenuPrefab, quadTransform);
                
                // Hide Quad if requested
                if (hideQuadOnSit)
                {
                    var meshRenderer = quadTransform.GetComponent<MeshRenderer>();
                    if (meshRenderer != null) meshRenderer.enabled = false;
                }
                
                // Reset local transform to match the Quad perfectly
                currentUIInstance.transform.localPosition = Vector3.zero;
                currentUIInstance.transform.localRotation = Quaternion.identity;
                
                // Apply fitting logic
                if (fitToScreen)
                {
                    // If it's a Canvas/RectTransform UI
                    RectTransform rect = currentUIInstance.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        // 1. Force Anchors and Pivot to Center
                        rect.anchorMin = new Vector2(0.5f, 0.5f);
                        rect.anchorMax = new Vector2(0.5f, 0.5f);
                        rect.pivot = new Vector2(0.5f, 0.5f);
                        
                        // 2. Reset Position completely and apply offsets
                        currentUIInstance.transform.localPosition = positionOffset + new Vector3(0, 0, zBias);
                        rect.anchoredPosition3D = currentUIInstance.transform.localPosition; // Sync RectTransform
                        currentUIInstance.transform.localRotation = Quaternion.Euler(rotationOffset);

                        // 3. SMART SCALING:
                        // Instead of resizing the Canvas to 1x1 (which breaks UI layout/pixels),
                        // we scale the canvas down so its total size equals 1 unit in local space.
                        // The Quad (Parent) defines the World Size via its own Scale.
                        
                        // Default to existing size if zero to avoid divide by zero
                        float width = rect.sizeDelta.x > 0 ? rect.sizeDelta.x : 100f;
                        float height = rect.sizeDelta.y > 0 ? rect.sizeDelta.y : 100f;
                        
                        Vector3 newScale = new Vector3(1f / width, 1f / height, 1f);
                        
                        // Apply Manual Multiplier
                        newScale.x *= manualScale.x;
                        newScale.y *= manualScale.y;
                        newScale.z *= manualScale.z;
                        
                        currentUIInstance.transform.localScale = newScale;
                        
                        Debug.Log($"[Workstation] Auto-scaled UI. Size: {rect.sizeDelta}, Scale: {newScale}");
                    }
                    else
                    {
                        // Fallback for non-UI objects: just use manual scale
                        currentUIInstance.transform.localScale = manualScale;
                        currentUIInstance.transform.localPosition = positionOffset + new Vector3(0, 0, zBias);
                        currentUIInstance.transform.localRotation = Quaternion.Euler(rotationOffset);
                    }
                }
                else
                {
                     currentUIInstance.transform.localScale = manualScale;
                     currentUIInstance.transform.localPosition = positionOffset + new Vector3(0, 0, zBias);
                     currentUIInstance.transform.localRotation = Quaternion.Euler(rotationOffset);
                }

                Debug.Log($"[Workstation {workstationId}] Local UI instantiated.");
            }
            else
            {
                Debug.LogWarning($"[Workstation {workstationId}] Cannot instantiate UI: Prefab or Quad is missing.");
            }
        }

        /// <summary>
        /// Called when the LOCAL player stands up from the linked chair.
        /// </summary>
        public void OnLocalPlayerStanding()
        {
            if (currentUIInstance != null)
            {
                Destroy(currentUIInstance);
                currentUIInstance = null;
                
                // Restore Quad Visibility
                if (hideQuadOnSit && quadTransform != null)
                {
                    var meshRenderer = quadTransform.GetComponent<MeshRenderer>();
                    if (meshRenderer != null) meshRenderer.enabled = true;
                }

                Debug.Log($"[Workstation {workstationId}] Local UI destroyed.");
            }
        }
    }
}
