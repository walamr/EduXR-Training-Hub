using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

namespace XRMultiplayer.Drawing
{
    /// <summary>
    /// Manages ray-based drawing on the TV/presentation surface.
    /// Supports late-joiner sync, batched networking, and robust erasing.
    /// </summary>
    public class RayDrawingManager : NetworkBehaviour
    {
        public static RayDrawingManager Instance { get; private set; }

        public enum DrawingMode
        {
            Draw,
            Erase,
            Pointer
        }

        // Serializable stroke data for networking
        [System.Serializable]
        public struct StrokeData : INetworkSerializable
        {
            public string StrokeId; // GUID
            public int ColorIndex;
            public Vector3[] Points;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref StrokeId);
                serializer.SerializeValue(ref ColorIndex);
                serializer.SerializeValue(ref Points);
            }
        }

        [Header("Input")]
        [SerializeField] private InputActionReference m_DrawAction;
        [SerializeField] private Transform m_RayOrigin; // Controller transform

        [Header("Drawing Surface")]
        [Tooltip("Assign a specific collider for the drawing surface (recommended). If null, will use layer mask instead.")]
        [SerializeField] private Collider m_DrawingSurfaceCollider;
        [Tooltip("Only used if DrawingSurfaceCollider is not set. Use a dedicated layer to avoid conflicts.")]
        [SerializeField] private LayerMask m_DrawingSurfaceLayer;

        [Header("Drawing Settings")]
        [SerializeField] private float m_LineWidth = 0.005f;
        [SerializeField] private Material m_LineMaterial;

        [Header("Colors")]
        [SerializeField] private Color m_BlueColor = new Color(0.2f, 0.5f, 1f);
        [SerializeField] private Color m_GreenColor = new Color(0.2f, 0.9f, 0.3f);
        [SerializeField] private Color m_RedColor = new Color(1f, 0.3f, 0.3f);

        [Header("Pointer")]
        [SerializeField] private GameObject m_PointerDotPrefab;
        private GameObject m_PointerDot;

        // State
        private DrawingMode m_CurrentMode = DrawingMode.Draw;
        private Color m_CurrentColor;
        private bool m_IsDrawing = false;
        private DrawingStroke m_CurrentStroke;
        
        // Synced History
        private List<DrawingStroke> m_ActiveStrokes = new List<DrawingStroke>();
        private Dictionary<string, DrawingStroke> m_StrokeMap = new Dictionary<string, DrawingStroke>();

        // Network Optimization: Batching
        private List<Vector3> m_PendingPoints = new List<Vector3>();
        private float m_LastBatchTime = 0f;
        private const float BATCH_INTERVAL = 0.05f; // Send points every 50ms (20Hz) instead of every frame
        private const int MAX_BATCH_SIZE = 10;
        
        // Input Setup
        private bool m_InputSetup = false;
        private bool m_UsingFallbackInput = false;
        private static bool s_FallbackLogged = false;
        private UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor m_RayInteractor;
        private bool m_TriggerWasPressed = false;
        private const float TRIGGER_THRESHOLD = 0.5f;

        private void Awake()
        {
            Instance = this;
            m_CurrentColor = m_BlueColor;
        }

        private void Start()
        {
            SetupInput();
            CreatePointerDot();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            SetupInput();
            CreatePointerDot();

            XRINetworkGameManager.Connected.Subscribe(OnConnectionChanged);
            
            // Request existing strokes from owner (or existing players)
            if (!IsOwner)
            {
                RequestStateRpc(NetworkManager.Singleton.LocalClientId);
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            CleanupEventListeners();
            XRINetworkGameManager.Connected.Unsubscribe(OnConnectionChanged);
            
            // Clear strokes on disconnect
            ResetLocalStrokes();
        }

        private void Update()
        {
            // Input Handling
            if (m_UsingFallbackInput) CheckFallbackTriggerInput();

            if (m_RayOrigin != null) UpdatePointer();

            // Drawing Logic
            if (m_IsDrawing && m_CurrentMode == DrawingMode.Draw)
            {
                ContinueDrawing();
                ProcessPointBatching();
            }
            
            if (m_TriggerWasPressed && m_CurrentMode == DrawingMode.Erase)
            {
                TryErase();
            }
        }
        
        #region Networking - Batched Points & State Sync
        
        // 1. New Joiner requests state
        [Rpc(SendTo.Everyone)]
        private void RequestStateRpc(ulong requestorId)
        {
            // Only the owner (or a master client logic) should respond to avoid spam
            if (IsOwner)
            {
                // Send all existing strokes to the new player
                // Break into chunks if necessary (simplified here: one RPC per stroke for reliability)
                foreach(var stroke in m_ActiveStrokes)
                {
                    if (stroke == null) continue;
                    
                    var data = new StrokeData 
                    {
                        StrokeId = stroke.StrokeId,
                        ColorIndex = GetColorIndex(stroke.GetColor()),
                        Points = stroke.GetRawPoints()
                    };
                    
                    SendStrokeStateRpc(data, RpcTarget.Single(requestorId, RpcTargetUse.Temp)); 
                }
            }
        }

        // 2. Owner sends full stroke data to specific client
        [Rpc(SendTo.SpecifiedInParams)]
        private void SendStrokeStateRpc(StrokeData data, RpcParams rpcParams)
        {
            // Reconstruct stroke locally
            if (m_StrokeMap.ContainsKey(data.StrokeId)) return; // Already have it

            SpawnLocalStroke(data.StrokeId, GetColorFromIndex(data.ColorIndex), data.Points[0]);
            var stroke = m_StrokeMap[data.StrokeId];
            
            for(int i = 1; i < data.Points.Length; i++)
            {
                stroke.AddPoint(data.Points[i]);
            }
            stroke.FinalizeStroke();
        }

        private void ProcessPointBatching()
        {
            // Check if it's time to send batch
            if (Time.time - m_LastBatchTime >= BATCH_INTERVAL || m_PendingPoints.Count >= MAX_BATCH_SIZE)
            {
                if (m_PendingPoints.Count > 0 && m_CurrentStroke != null)
                {
                    BroadcastBatchedPointsRpc(m_CurrentStroke.StrokeId, m_PendingPoints.ToArray(), NetworkManager.Singleton.LocalClientId);
                    m_PendingPoints.Clear();
                    m_LastBatchTime = Time.time;
                }
            }
        }

        #endregion

        #region Drawing Logic

        private void StartDrawing()
        {
            if (m_IsDrawing) return;

            if (TryRaycast(out RaycastHit hit))
            {
                m_IsDrawing = true;
                string newStrokeId = System.Guid.NewGuid().ToString();

                // Create local stroke
                SpawnLocalStroke(newStrokeId, m_CurrentColor, hit.point);
                m_CurrentStroke = m_StrokeMap[newStrokeId];

                // Network start
                if (IsSpawned)
                {
                    BroadcastSpawnStrokeRpc(newStrokeId, hit.point, GetColorIndex(m_CurrentColor), NetworkManager.Singleton.LocalClientId);
                }
            }
        }

        private void ContinueDrawing()
        {
            if (!m_IsDrawing || m_CurrentStroke == null) return;

            if (TryRaycast(out RaycastHit hit))
            {
                // Add point locally
                m_CurrentStroke.AddPoint(hit.point);
                
                // Add to batch for networking
                if (IsSpawned)
                {
                    m_PendingPoints.Add(hit.point);
                }
            }
        }

        private void StopDrawing()
        {
            if (!m_IsDrawing) return;
            m_IsDrawing = false;

            // Send any remaining points
            if (IsSpawned && m_PendingPoints.Count > 0 && m_CurrentStroke != null)
            {
                BroadcastBatchedPointsRpc(m_CurrentStroke.StrokeId, m_PendingPoints.ToArray(), NetworkManager.Singleton.LocalClientId);
                m_PendingPoints.Clear();
            }

            if (m_CurrentStroke != null)
            {
                m_CurrentStroke.FinalizeStroke();
                
                // Network finalize
                if (IsSpawned)
                {
                    BroadcastFinalizeStrokeRpc(m_CurrentStroke.StrokeId, NetworkManager.Singleton.LocalClientId);
                }
                
                m_CurrentStroke = null;
            }
        }

        private void SpawnLocalStroke(string id, Color color, Vector3 startPoint)
        {
            GameObject strokeObj = new GameObject($"Stroke_{id.Substring(0,8)}");
            var stroke = strokeObj.AddComponent<DrawingStroke>();
            stroke.Initialize(color, m_LineWidth, m_LineMaterial);
            stroke.StrokeId = id; // New property in DrawingStroke
            stroke.AddPoint(startPoint);
            
            m_ActiveStrokes.Add(stroke);
            m_StrokeMap[id] = stroke;
        }

        private const float ERASER_RADIUS = 0.05f;

        private void TryErase()
        {
            if (m_RayOrigin == null) return;
            Ray ray = new Ray(m_RayOrigin.position, m_RayOrigin.forward);
            RaycastHit[] hits = Physics.SphereCastAll(ray, ERASER_RADIUS, 100f);

            if (hits.Length > 0)
            {
                foreach (var hit in hits)
                {
                    var stroke = hit.collider.GetComponentInParent<DrawingStroke>();
                    if (stroke != null)
                    {
                        string id = stroke.StrokeId;
                        if (m_StrokeMap.ContainsKey(id))
                        {
                            // Remove locally
                            RemoveStrokeLocal(id);

                            // Sync deletion
                            if (IsSpawned)
                            {
                                BroadcastDeleteStrokeRpc(id, NetworkManager.Singleton.LocalClientId);
                            }
                            return; // One at a time
                        }
                    }
                }
            }
        }
        
        private void RemoveStrokeLocal(string id)
        {
            if (m_StrokeMap.TryGetValue(id, out DrawingStroke stroke))
            {
                m_ActiveStrokes.Remove(stroke);
                m_StrokeMap.Remove(id);
                if (stroke != null) Destroy(stroke.gameObject);
            }
        }

        public void ResetAllStrokes()
        {
            ResetLocalStrokes();

            if (IsSpawned)
            {
                BroadcastResetAllRpc(NetworkManager.Singleton.LocalClientId);
            }
        }
        
        private void ResetLocalStrokes()
        {
            foreach (var stroke in m_ActiveStrokes)
            {
                if (stroke != null) Destroy(stroke.gameObject);
            }
            m_ActiveStrokes.Clear();
            m_StrokeMap.Clear();
        }

        #endregion

        #region RPCS

        [Rpc(SendTo.Everyone)]
        private void BroadcastSpawnStrokeRpc(string id, Vector3 startPoint, int colorIndex, ulong senderId)
        {
            if (NetworkManager.Singleton.LocalClientId == senderId) return;
            SpawnLocalStroke(id, GetColorFromIndex(colorIndex), startPoint);
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastBatchedPointsRpc(string id, Vector3[] points, ulong senderId)
        {
            if (NetworkManager.Singleton.LocalClientId == senderId) return;

            if (m_StrokeMap.TryGetValue(id, out DrawingStroke stroke))
            {
                foreach(var p in points) stroke.AddPoint(p);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastFinalizeStrokeRpc(string id, ulong senderId)
        {
            if (NetworkManager.Singleton.LocalClientId == senderId) return;

            if (m_StrokeMap.TryGetValue(id, out DrawingStroke stroke))
            {
                stroke.FinalizeStroke();
            }
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastDeleteStrokeRpc(string id, ulong senderId)
        {
            if (NetworkManager.Singleton.LocalClientId == senderId) return;
            RemoveStrokeLocal(id);
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastResetAllRpc(ulong senderId)
        {
            if (NetworkManager.Singleton.LocalClientId == senderId) return;
            ResetLocalStrokes();
        }

        #endregion

        #region Helpers & Input (Keep existing SetupInput, pointer, color logic)
        
        private void OnConnectionChanged(bool connected)
        {
            if (!connected) ResetLocalStrokes();
        }

        private void SetupInput()
        {
            if (m_InputSetup) return;
            
            // Try to find XRRayInteractor if not set
            if (m_RayInteractor == null && m_RayOrigin != null)
            {
                m_RayInteractor = m_RayOrigin.GetComponentInParent<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>();
            }
            
            if (m_RayInteractor == null)
            {
                var allInteractors = FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>(FindObjectsSortMode.None);
                
                if (allInteractors.Length > 0)
                {
                    foreach (var interactor in allInteractors)
                    {
                        // Loose matching for compatibility
                        string objName = interactor.gameObject.name.ToLower();
                        if (!objName.Contains("teleport"))
                        {
                            m_RayInteractor = interactor;
                            m_RayOrigin = interactor.transform;
                            Debug.Log($"[RayDrawingManager] Found Interactor: {interactor.name}");
                            break;
                        }
                    }
                    
                    // Fallback to first if no non-teleport found
                    if (m_RayInteractor == null)
                    {
                         m_RayInteractor = allInteractors[0];
                         m_RayOrigin = allInteractors[0].transform;
                    }
                }
            }
            
            if (m_RayInteractor != null)
            {
                m_RayInteractor.selectEntered.AddListener(OnSelectEntered);
                m_RayInteractor.selectExited.AddListener(OnSelectExited);
                m_InputSetup = true;
            }
            else if (m_DrawAction != null && m_DrawAction.action != null)
            {
                m_DrawAction.action.Enable();
                m_DrawAction.action.performed += OnDrawActionPerformed;
                m_DrawAction.action.canceled += OnDrawActionCanceled;
                m_InputSetup = true;
            }
            else
            {
                if (!s_FallbackLogged)
                {
                    Debug.Log("[RayDrawingManager] No XRRayInteractor or Draw Action found - using fallback trigger detection.");
                    s_FallbackLogged = true;
                }
                m_UsingFallbackInput = true;
                m_InputSetup = true;
            }
        }
        
        private void OnSelectEntered(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args) { OnTriggerPressed(); }
        private void OnSelectExited(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args) { OnTriggerReleased(); }
        
        // ... (Keep Pointer Dot Logic, TryRaycast, Color Helpers, etc same as original)
        
        private void CreatePointerDot()
        {
            if (m_PointerDot != null) return;
            if (m_PointerDotPrefab != null) { m_PointerDot = Instantiate(m_PointerDotPrefab); m_PointerDot.SetActive(false); }
            else
            {
                m_PointerDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                m_PointerDot.name = "PointerDot";
                m_PointerDot.transform.localScale = Vector3.one * 0.02f;
                m_PointerDot.GetComponent<Collider>().enabled = false;
                var renderer = m_PointerDot.GetComponent<Renderer>();
                renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                renderer.material.color = Color.red;
                m_PointerDot.SetActive(false);
            }
        }
        
        private void UpdatePointer()
        {
            if (m_PointerDot == null) return;
            if (TryRaycast(out RaycastHit hit))
            {
                m_PointerDot.SetActive(true);
                m_PointerDot.transform.position = hit.point + hit.normal * 0.002f;
                m_PointerDot.transform.rotation = Quaternion.LookRotation(-hit.normal);
                var renderer = m_PointerDot.GetComponent<Renderer>();
                if (renderer != null) renderer.material.color = m_CurrentMode == DrawingMode.Draw ? m_CurrentColor : Color.white;
            }
            else m_PointerDot.SetActive(false);
        }

        private bool TryRaycast(out RaycastHit hit)
        {
            if (m_RayOrigin == null) { hit = default; return false; }
            Ray ray = new Ray(m_RayOrigin.position, m_RayOrigin.forward);
            if (m_DrawingSurfaceCollider != null && m_DrawingSurfaceCollider.Raycast(ray, out hit, 100f)) return true;
            return Physics.Raycast(ray, out hit, 100f, m_DrawingSurfaceLayer);
        }

        private void CheckFallbackTriggerInput() 
        {
            // Simplified fallback logic
             bool triggerPressed = false;
            // ... (keep existing check logic or simplify) ...
            // For brevity, assuming original check logic
             // Try to read trigger from right hand controller
            var rightHandDevices = new List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                UnityEngine.XR.InputDeviceCharacteristics.Right | 
                UnityEngine.XR.InputDeviceCharacteristics.Controller, 
                rightHandDevices);
            
            if (rightHandDevices.Count > 0)
            {
               rightHandDevices[0].TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float val);
               triggerPressed = val > 0.5f;
            }
            #if UNITY_EDITOR
            if (rightHandDevices.Count == 0 && UnityEngine.InputSystem.Mouse.current.leftButton.isPressed) triggerPressed = true;
            #endif

            if (triggerPressed && !m_TriggerWasPressed) OnTriggerPressed();
            else if (!triggerPressed && m_TriggerWasPressed) OnTriggerReleased();
            m_TriggerWasPressed = triggerPressed;
        }

        private void OnTriggerPressed()
        {
            if (m_CurrentMode == DrawingMode.Draw) StartDrawing();
            else if (m_CurrentMode == DrawingMode.Erase) TryErase();
        }
        private void OnTriggerReleased() { if(m_CurrentMode == DrawingMode.Draw) StopDrawing(); }
        
        private void OnDrawActionPerformed(InputAction.CallbackContext context) { OnTriggerPressed(); }
        private void OnDrawActionCanceled(InputAction.CallbackContext context) { OnTriggerReleased(); }

        public void SetColorBlue() => SetColor(0);
        public void SetColorGreen() => SetColor(1);
        public void SetColorRed() => SetColor(2);
        public void SetModeErase() => SetMode(DrawingMode.Erase);
        public void SetModeDraw() => SetMode(DrawingMode.Draw);
        public void SetModePointer() => SetMode(DrawingMode.Pointer);

        private void SetColor(int index)
        {
            m_CurrentColor = GetColorFromIndex(index);
            SetMode(DrawingMode.Draw);
        }
        private void SetMode(DrawingMode mode)
        {
            m_CurrentMode = mode;
            if (mode != DrawingMode.Pointer && m_PointerDot != null) m_PointerDot.SetActive(false);
        }
        private int GetColorIndex(Color c) => (c == m_GreenColor) ? 1 : (c == m_RedColor) ? 2 : 0;
        private Color GetColorFromIndex(int i) => i switch { 1 => m_GreenColor, 2 => m_RedColor, _ => m_BlueColor };

        private void CleanupEventListeners()
        {
             if (m_RayInteractor != null) { m_RayInteractor.selectEntered.RemoveListener(OnSelectEntered); m_RayInteractor.selectExited.RemoveListener(OnSelectExited); }
             if (m_DrawAction != null && m_DrawAction.action != null) { m_DrawAction.action.performed -= OnDrawActionPerformed; m_DrawAction.action.canceled -= OnDrawActionCanceled; }
        }

        #endregion

        #region Setup Helpers

        public void SetRayOrigin(Transform origin)
        {
            m_RayOrigin = origin;
        }

        public void SetDrawAction(InputActionReference action)
        {
            m_DrawAction = action;
        }

        public void SetDrawingSurfaceLayer(LayerMask layer)
        {
            m_DrawingSurfaceLayer = layer;
        }

        public void SetLineMaterial(Material mat)
        {
            m_LineMaterial = mat;
        }

        public void SetDrawingSurface(Collider collider)
        {
            m_DrawingSurfaceCollider = collider;
        }

        #endregion


    }
}
