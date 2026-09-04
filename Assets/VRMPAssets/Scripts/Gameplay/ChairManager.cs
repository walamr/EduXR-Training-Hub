using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

namespace XRMultiplayer
{
    /// <summary>
    /// Singleton manager that handles the player's seated state in a chair.
    /// Manages input routing, XR Origin parenting, and locomotion system toggling.
    /// </summary>
    public class ChairManager : MonoBehaviour
    {
        public static ChairManager Instance { get; private set; }

        [Header("Input Actions")]
        [Tooltip("Button to stand up from the chair (e.g., A/X button or Primary Button)")]
        [SerializeField] private InputActionReference standUpAction;
        
        [Tooltip("Joystick for chair movement (left thumbstick)")]
        [SerializeField] private InputActionReference moveAction;

        [Header("Movement Settings")]
        [Tooltip("Invert forward/back controls")]
        [SerializeField] private bool invertForwardBack = true;
        
        [Tooltip("Enable head-steering (look direction affects turn) - Can cause unwanted turning")]
        [SerializeField] private bool enableHeadSteering = false;
        
        [Tooltip("How much head direction influences steering (0-1)")]
        [SerializeField, Range(0f, 1f)] private float headSteeringStrength = 0.3f;

        [Header("Stand Up Settings")]
        [SerializeField] private float standUpForwardOffset = 0.5f;
        
        [Header("Seat Calibration")]
        [Tooltip("If true, the camera height will be forced to match the seat point height. Helpful if the player is standing physically but sitting virtually.")]
        [SerializeField] private bool matchCameraHeightToSeat = true;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        [Tooltip("Ignore small thumbstick noise on standalone VR builds.")]
        [SerializeField] private float thumbstickDeadzone = 0.15f;

        // Runtime state
        private SittableChair currentChair;
        private bool isSeated = false;
        private Transform xrOrigin;
        private Transform originalParent;
        private CharacterController xrCharacterController;
        
        // XR Locomotion components to disable
        private XRBodyTransformer xrBodyTransformer;
        
        // Auto-created input actions (fallback if not assigned)
        private InputAction autoStandAction;
        private InputAction autoMoveAction;
        
        // For edge detection of XR buttons
        private bool lastXRButtonState = false;
        
        // Podium Mode lock state
        private bool m_PodiumLocked = false;

        public bool IsPlayerSeated => isSeated;
        public SittableChair CurrentChair => currentChair;
        public bool IsPodiumLocked => m_PodiumLocked;

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // DontDestroyOnLoad requires a root GameObject; detach from parent first if needed
            if (transform.parent != null)
                transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            FindXROrigin();
            SetupInputActions();
        }

        private void OnEnable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            EnableInputActions();
        }

        private void OnDisable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            DisableInputActions();
        }

        private void OnDestroy()
        {
            if (autoStandAction != null) autoStandAction.Dispose();
            if (autoMoveAction != null) autoMoveAction.Dispose();
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            FindXROrigin();
        }

        private void Update()
        {
            // Check for stand up input directly in Update as backup
            CheckStandUpInput();
            
            if (!isSeated || currentChair == null) 
            {
                if (isSeated && currentChair == null)
                {
                    ForceStandLocal();
                }
                return;
            }

            ProcessChairInput();
        }

        #endregion

        #region Input Setup

        private void FindXROrigin()
        {
            var xrOriginComponent = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOriginComponent != null)
            {
                xrOrigin = xrOriginComponent.transform;
                originalParent = xrOrigin.parent;
                xrCharacterController = xrOrigin.GetComponent<CharacterController>();
                xrBodyTransformer = xrOrigin.GetComponentInChildren<XRBodyTransformer>(true);
                
                if (debugLogs)
                {
                    Debug.Log($"[ChairManager] Found XROrigin: {xrOrigin.name}");
                    Debug.Log($"[ChairManager] CharacterController: {(xrCharacterController != null ? "Found" : "Not found")}");
                    Debug.Log($"[ChairManager] XRBodyTransformer: {(xrBodyTransformer != null ? "Found" : "Not found")}");
                }
            }
            else
            {
                Debug.LogWarning("[ChairManager] XROrigin not found in this scene.");
            }
        }

        private void SetupInputActions()
        {
            // Stand Up Action
            if (standUpAction == null || standUpAction.action == null)
            {
                if (debugLogs) Debug.Log("[ChairManager] Creating fallback Stand Up action");
                autoStandAction = new InputAction("ChairStandUp", InputActionType.Button);
                autoStandAction.AddBinding("<XRController>{RightHand}/primaryButton");
                autoStandAction.AddBinding("<XRController>{LeftHand}/primaryButton");
                autoStandAction.AddBinding("<Keyboard>/u");
                autoStandAction.performed += OnStandUpPressed;
            }
            else
            {
                if (debugLogs) Debug.Log("[ChairManager] Using assigned Stand Up action");
                standUpAction.action.performed += OnStandUpPressed;
            }

            // Move Action
            if (moveAction == null || moveAction.action == null)
            {
                if (debugLogs) Debug.Log("[ChairManager] Creating fallback Move action");
                autoMoveAction = new InputAction("ChairMove", InputActionType.Value);
                autoMoveAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
                autoMoveAction.AddBinding("<XRController>{LeftHand}/primary2DAxis");
                autoMoveAction.AddBinding("<XRController>{LeftHand}/thumbstick");
                autoMoveAction.AddBinding("<Gamepad>/leftStick");
                autoMoveAction.AddBinding("<Keyboard>/wasd", interactions: "", processors: "");
            }
            else
            {
                if (debugLogs) Debug.Log("[ChairManager] Using assigned Move action");
            }
        }

        private void EnableInputActions()
        {
            if (standUpAction != null && standUpAction.action != null)
                standUpAction.action.Enable();
            if (moveAction != null && moveAction.action != null)
                moveAction.action.Enable();
            
            autoStandAction?.Enable();
            autoMoveAction?.Enable();
            
            if (debugLogs) Debug.Log("[ChairManager] Input actions enabled");
        }

        private void DisableInputActions()
        {
            autoStandAction?.Disable();
            autoMoveAction?.Disable();
        }

        private void OnStandUpPressed(InputAction.CallbackContext context)
        {
            if (debugLogs) Debug.Log($"[ChairManager] Stand up pressed! isSeated={isSeated}, podiumLocked={m_PodiumLocked}");
            if (m_PodiumLocked)
            {
                if (debugLogs) Debug.Log("[ChairManager] Stand up blocked - Podium Mode active.");
                return;
            }
            if (isSeated)
            {
                StandUp();
            }
        }
        
        private void CheckStandUpInput()
        {
            if (!isSeated || m_PodiumLocked) return;
            
            // Check Input System actions
            bool standPressed = false;
            
            if (autoStandAction != null && autoStandAction.WasPressedThisFrame())
            {
                standPressed = true;
                if (debugLogs) Debug.Log("[ChairManager] Stand up via autoStandAction");
            }
            else if (standUpAction != null && standUpAction.action != null && standUpAction.action.WasPressedThisFrame())
            {
                standPressed = true;
                if (debugLogs) Debug.Log("[ChairManager] Stand up via standUpAction");
            }
            
            // Also check XR device buttons directly as fallback (with edge detection)
            bool currentXRButtonState = false;
            if (!standPressed)
            {
                var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
                UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.Controller, devices);
                
                foreach (var device in devices)
                {
                    // Check Primary Button (A/X button)
                    if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool primary) && primary)
                    {
                        currentXRButtonState = true;
                        break;
                    }
                    
                    // Check Secondary Button (B/Y button) as alternative
                    if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool secondary) && secondary)
                    {
                        currentXRButtonState = true;
                        break;
                    }
                }
                
                // Edge detection: only trigger on rising edge (button just pressed)
                if (currentXRButtonState && !lastXRButtonState)
                {
                    standPressed = true;
                    if (debugLogs) Debug.Log("[ChairManager] Stand up via XR button (edge detected)");
                }
            }
            lastXRButtonState = currentXRButtonState;
            
            // Also check keyboard 'U' as fallback
            if (!standPressed && Keyboard.current != null && Keyboard.current.uKey.wasPressedThisFrame)
            {
                standPressed = true;
                if (debugLogs) Debug.Log("[ChairManager] Stand up via 'U' key");
            }
            
            if (standPressed)
            {
                StandUp();
            }
        }

        #endregion

        #region Chair Input Processing

        private void ProcessChairInput()
        {
            // Block chair movement input when podium-locked
            if (m_PodiumLocked)
            {
                if (currentChair != null)
                {
                    var locomotion = currentChair.GetComponent<ChairLocomotion>();
                    if (locomotion != null) locomotion.ProcessInput(Vector2.zero);
                }
                return;
            }
            
            Vector2 rawInput = GetMoveInput();
            
            // Debug log raw input occasionally
            if (rawInput.sqrMagnitude > 0.01f)
            {
                if (debugLogs && Time.frameCount % 30 == 0)
                {
                    Debug.Log($"[ChairManager] Input: {rawInput}");
                }
            }
            
            // Apply inversion if enabled
            if (invertForwardBack)
            {
                rawInput.y = -rawInput.y;
            }

            // Apply head steering if enabled and moving
            if (enableHeadSteering && Mathf.Abs(rawInput.y) > 0.1f)
            {
                float headSteeringInput = CalculateHeadSteering();
                
                // Blend head steering with manual joystick input
                if (Mathf.Abs(rawInput.x) < 0.15f)
                {
                    // No manual turn input, use head steering
                    rawInput.x = headSteeringInput * headSteeringStrength;
                }
                else
                {
                    // Manual input present, blend slightly
                    rawInput.x += headSteeringInput * headSteeringStrength * 0.3f;
                }
            }

            // Send to locomotion
            if (currentChair != null)
            {
                var locomotion = currentChair.GetComponent<ChairLocomotion>();
                if (locomotion != null)
                {
                    locomotion.ProcessInput(rawInput);
                }
                else if (debugLogs && Time.frameCount % 60 == 0)
                {
                    Debug.LogWarning("[ChairManager] ChairLocomotion component not found on chair!");
                }
            }
        }

        private Vector2 GetMoveInput()
        {
            Vector2 input = Vector2.zero;
            
            if (moveAction != null && moveAction.action != null && moveAction.action.enabled)
            {
                input = moveAction.action.ReadValue<Vector2>();
            }
            
            if (input.sqrMagnitude < 0.01f && autoMoveAction != null && autoMoveAction.enabled)
            {
                input = autoMoveAction.ReadValue<Vector2>();
            }

            // Standalone VR builds: read Quest / OpenXR thumbstick directly (Editor often uses keyboard instead).
            if (input.sqrMagnitude < 0.01f)
            {
                input = ReadXRPrimary2DAxis();
            }
            
            // Keyboard WASD for Editor / desktop testing only
            if (input.sqrMagnitude < 0.01f && Keyboard.current != null)
            {
                float x = 0, y = 0;
                if (Keyboard.current.wKey.isPressed) y += 1;
                if (Keyboard.current.sKey.isPressed) y -= 1;
                if (Keyboard.current.aKey.isPressed) x -= 1;
                if (Keyboard.current.dKey.isPressed) x += 1;
                input = new Vector2(x, y);
            }

            return ApplyThumbstickDeadzone(input);
        }

        static Vector2 ApplyThumbstickDeadzone(Vector2 input, float deadzone)
        {
            if (input.sqrMagnitude <= deadzone * deadzone)
                return Vector2.zero;

            if (input.magnitude >= 1f)
                return input.normalized;

            return input;
        }

        Vector2 ApplyThumbstickDeadzone(Vector2 input) => ApplyThumbstickDeadzone(input, thumbstickDeadzone);

        /// <summary>Reads the left controller thumbstick via the XR Input API (reliable on Quest builds).</summary>
        static Vector2 ReadXRPrimary2DAxis()
        {
            var devices = new List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                UnityEngine.XR.InputDeviceCharacteristics.Controller | UnityEngine.XR.InputDeviceCharacteristics.Left,
                devices);

            foreach (var device in devices)
            {
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis))
                    return axis;
            }

            return Vector2.zero;
        }

        private float CalculateHeadSteering()
        {
            var camera = Camera.main;
            if (camera == null || currentChair == null) return 0f;

            // Get horizontal forward directions
            Vector3 chairForward = currentChair.transform.forward;
            chairForward.y = 0;
            chairForward.Normalize();

            Vector3 cameraForward = camera.transform.forward;
            cameraForward.y = 0;
            cameraForward.Normalize();

            // Calculate signed angle between chair and camera direction
            float angle = Vector3.SignedAngle(chairForward, cameraForward, Vector3.up);
            
            // Map to steering input (-1 to 1), with 45 degrees = full turn
            return Mathf.Clamp(angle / 45f, -1f, 1f);
        }

        #endregion

        #region Sit/Stand Logic

    public void SitOnChair(SittableChair chair)
        {
            if (isSeated || chair == null)
            {
                Debug.LogWarning("[ChairManager] Cannot sit: Already seated or chair is null.");
                return;
            }

            Debug.Log($"[ChairManager] Sitting on chair: {chair.name}");

            currentChair = chair;
            isSeated = true;
            
            // Trigger Workstation UI if linked
            if (chair.LinkedWorkstation != null)
            {
                chair.LinkedWorkstation.OnLocalPlayerSitting();
            }
            
            // Enable chair locomotion for local player
            var locomotion = chair.GetComponent<ChairLocomotion>();
            if (locomotion != null)
            {
                locomotion.SetLocalPlayerSeated(true);
                Debug.Log("[ChairManager] ChairLocomotion found and enabled for local player");
            }
            else
            {
                Debug.LogError("[ChairManager] ChairLocomotion component NOT FOUND on chair! Movement won't work.");
            }

            if (xrOrigin != null)
            {
                // Disable standard locomotion FIRST
                SetLocomotionEnabled(false);

                // Position XR Origin at chair
                Vector3 targetPos = chair.SeatPosition;

                if (matchCameraHeightToSeat)
                {
                    // Align Camera to SeatPosition
                    var originComponent = xrOrigin.GetComponent<Unity.XR.CoreUtils.XROrigin>();
                    if (originComponent != null && originComponent.Camera != null)
                    {
                         // Calculate the offset needed to put the Camera at targetPos.y
                         // Use InverseTransformPoint to get height relative to XR Origin root (accounts for Camera Offset objects)
                         float cameraHeightInOrigin = xrOrigin.InverseTransformPoint(originComponent.Camera.transform.position).y;
                         targetPos.y = chair.SeatPosition.y - cameraHeightInOrigin;
                    }
                    else
                    {
                         // Fallback
                         targetPos.y = chair.transform.position.y;
                    }
                }
                else
                {
                    targetPos.y = chair.transform.position.y; // Use chair base Y for floor reference
                }
                
                // Apply seat rotation, compensating for camera's local Y rotation (head tracking offset)
                // so the actual view faces chair.SeatRotation regardless of where the head was looking
                var xrOriginComp = xrOrigin.GetComponent<Unity.XR.CoreUtils.XROrigin>();
                if (xrOriginComp != null && xrOriginComp.Camera != null)
                {
                    float headYaw = xrOriginComp.Camera.transform.localEulerAngles.y;
                    xrOrigin.rotation = chair.SeatRotation * Quaternion.Euler(0f, -headYaw, 0f);
                }
                else
                {
                    xrOrigin.rotation = chair.SeatRotation;
                }
                xrOrigin.position = targetPos;
                
                // Compensate for camera offset (recentering horizontal only)
                CenterCameraOnChair(targetPos);

                // Parent to chair for movement
                xrOrigin.SetParent(chair.transform);
                
                Debug.Log($"[ChairManager] Player now seated and parented. MatchHeight={matchCameraHeightToSeat}, TargetY={targetPos.y}");
            }
        }

        private void CenterCameraOnChair(Vector3 targetPos)
        {
            var xrOriginComponent = xrOrigin.GetComponent<Unity.XR.CoreUtils.XROrigin>();
            if (xrOriginComponent != null && xrOriginComponent.Camera != null)
            {
                Vector3 cameraPos = xrOriginComponent.Camera.transform.position;
                Vector3 diff = cameraPos - targetPos;
                diff.y = 0; // Ignore vertical offset
                
                if (diff.magnitude > 0.05f)
                {
                    xrOrigin.position -= diff;
                }
            }
        }

        public void StandUp()
        {
            if (!isSeated) return;

            Debug.Log("[ChairManager] Standing up");

            if (currentChair != null)
            {
                 // Disable Workstation UI if linked
                if (currentChair.LinkedWorkstation != null)
                {
                    currentChair.LinkedWorkstation.OnLocalPlayerStanding();
                }

                // Disable chair locomotion for local player
                var locomotion = currentChair.GetComponent<ChairLocomotion>();
                if (locomotion != null)
                {
                    locomotion.SetLocalPlayerSeated(false);
                    locomotion.StopMovement();
                }
                
                currentChair.RequestStand();

                if (xrOrigin != null)
                {
                    // Unparent FIRST
                    xrOrigin.SetParent(originalParent);
                    
                    // Position in front of chair
                    Vector3 standPos = currentChair.SeatPosition + currentChair.transform.forward * standUpForwardOffset;
                    xrOrigin.position = standPos;
                    
                    // Face forward
                    Vector3 forward = currentChair.transform.forward;
                    forward.y = 0;
                    if (forward != Vector3.zero)
                    {
                        xrOrigin.rotation = Quaternion.LookRotation(forward);
                    }

                    // Re-enable locomotion AFTER repositioning
                    SetLocomotionEnabled(true);
                }
            }
            else
            {
                ForceStandLocal();
            }

            currentChair = null;
            isSeated = false;
        }

        private void ForceStandLocal()
        {
            Debug.LogWarning("[ChairManager] Force standing (chair lost)");
            
            if (xrOrigin != null)
            {
                xrOrigin.SetParent(originalParent);
                xrOrigin.rotation = Quaternion.Euler(0, xrOrigin.rotation.eulerAngles.y, 0);
                SetLocomotionEnabled(true);
            }
            
            currentChair = null;
            isSeated = false;
        }

        #region Podium Mode API

        /// <summary>
        /// Locks or unlocks the chair for Podium Mode.
        /// When locked: stand-up and chair rolling are both disabled.
        /// When unlocked: stand-up and chair rolling are re-enabled (player stays seated).
        /// </summary>
        public void SetPodiumLock(bool locked)
        {
            m_PodiumLocked = locked;
            
            if (debugLogs) Debug.Log($"[ChairManager] Podium lock: {locked}");
            
            // If unlocking, stop any residual movement
            if (!locked && currentChair != null)
            {
                var locomotion = currentChair.GetComponent<ChairLocomotion>();
                if (locomotion != null) locomotion.StopMovement();
            }
        }

        /// <summary>
        /// Detaches the local rig from any chair and re-enables standard locomotion.
        /// Used when the main scene is hidden for a private room transfer.
        /// </summary>
        public void ForceReleaseForSceneTransfer()
        {
            SetPodiumLock(false);

            if (currentChair != null)
            {
                var locomotion = currentChair.GetComponent<ChairLocomotion>();
                if (locomotion != null)
                {
                    locomotion.SetLocalPlayerSeated(false);
                    locomotion.StopMovement();
                }
            }

            if (isSeated)
                ForceStandLocal();
            else
                SetLocomotionEnabled(true);
        }

        #endregion

        private void SetLocomotionEnabled(bool enabled)
        {
            if (xrOrigin == null) return;

            // Toggle CharacterController
            if (xrCharacterController != null)
            {
                xrCharacterController.enabled = enabled;
            }
            
            // Toggle XRBodyTransformer - THIS IS THE KEY FIX
            if (xrBodyTransformer != null)
            {
                xrBodyTransformer.enabled = enabled;
            }

            // Toggle Continuous Move Providers
            var moveProviders = xrOrigin.GetComponentsInChildren<ContinuousMoveProvider>(true);
            foreach (var mp in moveProviders)
            {
                mp.enabled = enabled;
            }

            // Toggle Continuous Turn Providers
            var turnProviders = xrOrigin.GetComponentsInChildren<ContinuousTurnProvider>(true);
            foreach (var tp in turnProviders)
            {
                tp.enabled = enabled;
            }
            
            // Toggle LocomotionMediators
            var mediators = xrOrigin.GetComponentsInChildren<LocomotionMediator>(true);
            foreach (var m in mediators)
            {
                m.enabled = enabled;
            }
            
            // Toggle GravityProviders
            var gravityProviders = xrOrigin.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity.GravityProvider>(true);
            foreach (var gp in gravityProviders)
            {
                gp.enabled = enabled;
            }
            
            // Toggle JumpProviders
            var jumpProviders = xrOrigin.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.Jump.JumpProvider>(true);
            foreach (var jp in jumpProviders)
            {
                jp.enabled = enabled;
            }

            if (debugLogs) Debug.Log($"[ChairManager] Locomotion enabled: {enabled}");
        }

        #endregion
    }
}
