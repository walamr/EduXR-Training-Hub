using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using Unity.Netcode;

namespace XRMultiplayer
{
    /// <summary>
    /// Makes a chair sittable via XR ray interaction and synchronizes its state over the network.
    /// When selected (trigger press), the player will sit on this chair.
    /// </summary>
    [RequireComponent(typeof(XRSimpleInteractable))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(ChairLocomotion))]
    public class SittableChair : NetworkBehaviour
    {
        [Header("Seat Configuration")]
        [Tooltip("The point where the player will be positioned when seated. If null, uses this transform with seatHeightOffset.")]
        [SerializeField] private Transform seatPoint;
        
        [Tooltip("Height offset from chair origin if no seatPoint is specified")]
        [SerializeField] private float seatHeightOffset = 0.0f;
        
        [Tooltip("Forward offset from chair to position player")]
        [SerializeField] private float seatForwardOffset = 0f;

        [Tooltip("If set, player will always face this exact Transform when seated (overrides Workstation and rotation offset).")]
        [SerializeField] private Transform lookAtTarget;

        [Tooltip("Rotation offset (Y-axis degrees) used as fallback when no Workstation or LookAt target is linked.")]
        [SerializeField] private float seatRotationOffsetY = 0f;

        [Header("Workstation Link")]
        [Tooltip("Optional: The Workstation this chair belongs to.")]
        [SerializeField] private Workstation workstation;
        public Workstation LinkedWorkstation => workstation;

        [Header("Visual Feedback")]
        [Tooltip("Optional highlight object to show when hovering")]
        [SerializeField] private GameObject hoverHighlight;
        
        [Tooltip("Color to tint chair when hovering (if using renderer)")]
        [SerializeField] private Color hoverColor = new Color(0.5f, 0.8f, 1f, 1f);
        
        [Header("Network State")]
         // Use Owner write permission - Owner controls their chair
        private NetworkVariable<bool> isOccupied = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<ulong> currentSitterId = new NetworkVariable<ulong>(ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private XRSimpleInteractable interactable;
        private Renderer[] renderers;
        private Color[] originalColors;
        private bool isHovered = false;

        private Vector3 initialPosition;
        private Quaternion initialRotation;

        public bool IsOccupied => isOccupied.Value;
        public ulong CurrentSitterId => currentSitterId.Value;

        public Vector3 SeatPosition
        {
            get
            {
                // ADDITIVE logic: even if seatPoint is set, we still add the offsets.
                // This allows fine-tuning in the inspector without moving the child object.
                Vector3 basePos = seatPoint != null ? seatPoint.position : transform.position;
                return basePos + Vector3.up * seatHeightOffset + transform.forward * seatForwardOffset;
            }
        }

        public Quaternion SeatRotation
        {
            get
            {
                // Priority 1: explicit LookAt target
                if (lookAtTarget != null)
                {
                    Vector3 toTarget = lookAtTarget.position - SeatPosition;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 0.001f)
                        return Quaternion.LookRotation(toTarget.normalized);
                }

                // Priority 2: linked Workstation monitor
                if (workstation != null && workstation.QuadTransform != null)
                {
                    Vector3 toMonitor = workstation.QuadTransform.position - SeatPosition;
                    toMonitor.y = 0f;
                    if (toMonitor.sqrMagnitude > 0.001f)
                        return Quaternion.LookRotation(toMonitor.normalized);
                }

                // Priority 3: manual rotation offset
                Quaternion baseRot = seatPoint != null ? seatPoint.rotation : transform.rotation;
                return baseRot * Quaternion.Euler(0f, seatRotationOffsetY, 0f);
            }
        }

        private void Awake()
        {
            interactable = GetComponent<XRSimpleInteractable>();
            Debug.Log($"[SittableChair] Awake - GameObject: {name}");
            
            // Store initial transform for resets
            initialPosition = transform.position;
            initialRotation = transform.rotation;

            renderers = GetComponentsInChildren<Renderer>();
            originalColors = new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].material.HasProperty("_Color"))
                    originalColors[i] = renderers[i].material.color;
            }
            
            if (hoverHighlight != null) hoverHighlight.SetActive(false);
            
            EnsureChairManagerExists();
        }
        
        private void EnsureChairManagerExists()
        {
            if (ChairManager.Instance == null)
            {
                Debug.Log("[SittableChair] ChairManager not found, creating one...");
                var chairManagerGO = new GameObject("ChairManager");
                chairManagerGO.AddComponent<ChairManager>();
            }
        }

        public override void OnNetworkSpawn()
        {
            isOccupied.OnValueChanged += OnOccupiedChanged;
        }

        public override void OnNetworkDespawn()
        {
            isOccupied.OnValueChanged -= OnOccupiedChanged;
            ResetChairPosition();
        }

        private void ResetChairPosition()
        {
            Debug.Log($"[SittableChair] Resetting position for {name}");
            transform.position = initialPosition;
            transform.rotation = initialRotation;
            
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void OnOccupiedChanged(bool previousValue, bool newValue)
        {
            Debug.Log($"[SittableChair] Occupied changed: {previousValue} -> {newValue}, owner={OwnerClientId}");
        }

        private void OnEnable()
        {
            if (interactable != null)
            {
                interactable.hoverEntered.AddListener(OnHoverEnter);
                interactable.hoverExited.AddListener(OnHoverExit);
                interactable.selectEntered.AddListener(OnSelectEnter);
                interactable.activated.AddListener(OnActivate);
            }
        }

        private void OnDisable()
        {
            if (interactable != null)
            {
                interactable.hoverEntered.RemoveListener(OnHoverEnter);
                interactable.hoverExited.RemoveListener(OnHoverExit);
                interactable.selectEntered.RemoveListener(OnSelectEnter);
                interactable.activated.RemoveListener(OnActivate);
            }
            if (isHovered) ResetHoverVisual();
        }

        private void OnHoverEnter(HoverEnterEventArgs args)
        {
            if ((ChairManager.Instance != null && ChairManager.Instance.IsPlayerSeated) || IsOccupied) return;

            isHovered = true;
            if (hoverHighlight != null) hoverHighlight.SetActive(true);
            
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].material.HasProperty("_Color"))
                    renderers[i].material.color = hoverColor;
            }
        }

        private void OnHoverExit(HoverExitEventArgs args)
        {
            ResetHoverVisual();
        }

        private void ResetHoverVisual()
        {
            isHovered = false;
            if (hoverHighlight != null) hoverHighlight.SetActive(false);
            
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].material.HasProperty("_Color"))
                    renderers[i].material.color = originalColors[i];
            }
        }

        private void OnActivate(ActivateEventArgs args)
        {
             TrySit();
        }

        private bool lastTriggerState = false;

        private void Update()
        {
            // Fail-safe: Check for manual input if XRI Select isn't firing
            if (isHovered && !IsOccupied)
            {
                bool triggerPressed = false;
                bool hasController = false;

                var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
                UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.Controller, devices);

                foreach (var device in devices)
                {
                    hasController = true;
                    if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool pressed) && pressed)
                    {
                        triggerPressed = true;
                        break;
                    }
                     if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float value) && value > 0.1f)
                    {
                        triggerPressed = true;
                        break;
                    }
                }

                #if UNITY_EDITOR
                if (!hasController || !triggerPressed)
                {
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    if (mouse != null && mouse.leftButton.isPressed)
                    {
                        triggerPressed = true;
                    }
                }
                #endif

                if (triggerPressed && !lastTriggerState)
                {
                    TrySit();
                }

                lastTriggerState = triggerPressed;
            }
            else
            {
                lastTriggerState = false;
            }
        }

        private void OnSelectEnter(SelectEnterEventArgs args)
        {
            TrySit();
        }

        private void TrySit()
        {
            // Already seated somewhere?
            if (ChairManager.Instance != null && ChairManager.Instance.IsPlayerSeated)
            {
                Debug.Log("[SittableChair] Already seated in a chair.");
                return;
            }
            
            // This chair already occupied?
            if (IsOccupied) 
            {
                Debug.Log($"[SittableChair] Cannot sit - chair is already occupied.");
                return; 
            }

             // Offline / Not networked check
            bool isNetworkActive = NetworkManager.Singleton != null && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer);
            if (!isNetworkActive || !IsSpawned)
            {
                Debug.Log("[SittableChair] Offline mode - sitting locally.");
                SitLocally();
                return;
            }

            // We are networked. 
            // In Distributed Authority or Server Auth where we are NOT owner, we must request ownership first.
            if (!IsOwner)
            {
                Debug.Log("[SittableChair] Requesting ownership from server...");
                RequestOwnershipServerRpc(NetworkManager.Singleton.LocalClientId);
            }
            else
            {
                // We are already owner, just sit
                Debug.Log("[SittableChair] We are already owner, sitting...");
                FinalizeSit();
            }
        }
        
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestOwnershipServerRpc(ulong clientId)
        {
            if (isOccupied.Value)
            {
                 Debug.LogWarning($"[SittableChair] Ownership request denied: Chair occupied.");
                 return;
            }
            
            Debug.Log($"[SittableChair] Server: Transferring ownership to {clientId}");
            
            // IMPORTANT: The NetworkObject Prefab MUST have 'Allow Ownership Transfer' checked in Inspector!
            // Otherwise this will throw an error/warning and fail.
            try
            {
               NetworkObject.ChangeOwnership(clientId);
               
               // Inform client they have ownership now (implicit via OnOwnershipChanged, but we can also compel a sit via ClientRpc)
               NotifyOwnershipTransferredClientRpc(clientId);
            }
            catch(System.Exception e)
            {
                Debug.LogError($"[SittableChair] Failed to change ownership! Is 'Allow Ownership Transfer' enabled on your NetworkObject? Error: {e.Message}");
            }
        }

        [ClientRpc]
        private void NotifyOwnershipTransferredClientRpc(ulong newOwnerData)
        {
            // If this message is for us, verify we own it and sit
            if (NetworkManager.Singleton.LocalClientId == newOwnerData)
            {
                Debug.Log("[SittableChair] Ownership transfer confirmed via ClientRpc. Sitting...");
                FinalizeSit();
            }
        }

        private void FinalizeSit()
        {
            if (IsOwner)
            {
                isOccupied.Value = true;
                currentSitterId.Value = NetworkManager.Singleton.LocalClientId;
                SitLocally();
                ResetHoverVisual();
            }
            else
            {
                Debug.LogError("[SittableChair] FinalizeSit called but we are not Owner! Ownership transfer likely failed.");
            }
        }

        private void SitLocally()
        {
            if (ChairManager.Instance != null)
            {
                 ChairManager.Instance.SitOnChair(this);
            }
        }

        public void RequestStand()
        {
            if (!IsSpawned) return;

            // Update state before leaving
            if (IsOwner)
            {
                isOccupied.Value = false;
                currentSitterId.Value = ulong.MaxValue;
 
                // We keep ownership for now, or could yield it back to Server
                // NetworkObject.RemoveOwnership(); 
            }
        }

        #region Podium Mode - Force Sit API

        /// <summary>
        /// Called by PodiumManager to programmatically seat the local player into this chair
        /// without requiring XR ray interaction or ownership negotiation.
        /// </summary>
        public void ForceLocalPlayerSit()
        {
            if (ChairManager.Instance != null && ChairManager.Instance.IsPlayerSeated)
            {
                Debug.Log("[SittableChair] ForceLocalPlayerSit - already seated, skipping.");
                return;
            }

            bool isNetworkActive = NetworkManager.Singleton != null && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer);
            if (!isNetworkActive || !IsSpawned)
            {
                Debug.Log("[SittableChair] ForceLocalPlayerSit - offline, sitting locally.");
                SitLocally();
                return;
            }

            if (!IsOwner)
            {
                Debug.Log("[SittableChair] ForceLocalPlayerSit - requesting ownership...");
                RequestOwnershipServerRpc(NetworkManager.Singleton.LocalClientId);
            }
            else
            {
                Debug.Log("[SittableChair] ForceLocalPlayerSit - already owner, sitting...");
                FinalizeSit();
            }
        }

        #endregion

        [ContextMenu("Create Seat Point")]
        private void CreateSeatPoint()
        {
            if (seatPoint != null) return;
            GameObject seatPointObj = new GameObject("SeatPoint");
            seatPointObj.transform.SetParent(transform);
            seatPointObj.transform.localPosition = Vector3.zero;
            seatPoint = seatPointObj.transform;
        }
    }
}
