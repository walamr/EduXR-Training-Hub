using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

namespace XRMultiplayer
{
    /// <summary>
    /// Handles physics-based movement for the rolling office chair.
    /// This component is driven by the ChairManager when a player is seated.
    /// Movement is smooth and controlled, suitable for VR comfort.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class ChairLocomotion : NetworkBehaviour
    {
        [Header("Movement Settings")]
        [Tooltip("Acceleration force applied when moving forward/back")]
        [SerializeField] private float moveAcceleration = 15f;
        
        [Tooltip("Torque applied when rotating left/right")]
        [SerializeField] private float rotationTorque = 60f;
        
        [Tooltip("Maximum linear speed the chair can reach (m/s)")]
        [SerializeField] private float maxSpeed = 3f;
        
        [Tooltip("Maximum angular speed (degrees/s)")]
        [SerializeField] private float maxAngularSpeed = 150f;
        
        [Header("Damping")]
        [Tooltip("How quickly the chair slows down when no input (lower = more sliding)")]
        [SerializeField] private float linearDamping = 1.5f;
        
        [Tooltip("How quickly the chair stops rotating when no input")]
        [SerializeField] private float angularDamping = 3f;

        [Header("Physics Material")]
        [Tooltip("If true, creates a low-friction physics material for smooth rolling")]
        [SerializeField] private bool useLowFrictionMaterial = true;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        private Rigidbody rb;
        private NetworkTransform networkTransform;
        private Vector2 currentInput;
        private bool isLocalPlayerSeated = false;
        private PhysicsMaterial lowFrictionMaterial;
        private float groundY; // Store ground height for stability
        private bool cachedInLocalSpace;
        private bool hasCachedInLocalSpace;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            TryGetComponent(out networkTransform);
            ConfigureRigidbody();
            
            if (useLowFrictionMaterial)
            {
                CreateLowFrictionMaterial();
            }
            
            // Store initial ground height
            groundY = transform.position.y;
            
            if (debugLogs) Debug.Log($"[ChairLocomotion] Awake on {gameObject.name}, groundY={groundY}");
        }

        private void ConfigureRigidbody()
        {
            if (rb == null) return;
            
            // Apply LOW drag settings for smooth rolling
            rb.linearDamping = linearDamping;
            rb.angularDamping = angularDamping;
            
            // FREEZE ALL ROTATION - chair stays perfectly upright
            // Also freeze Y position to prevent tipping/floating
            rb.constraints = RigidbodyConstraints.FreezeRotationX 
                           | RigidbodyConstraints.FreezeRotationZ 
                           | RigidbodyConstraints.FreezePositionY;
            
            // Physics settings for smooth movement
            rb.isKinematic = false;
            rb.useGravity = false; // We freeze Y, so no need for gravity
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            
            // Lower mass = easier to move
            rb.mass = 10f;
            
            if (debugLogs) Debug.Log($"[ChairLocomotion] Rigidbody configured: mass={rb.mass}, constraints={rb.constraints}");
        }

        private void CreateLowFrictionMaterial()
        {
            // Create a physics material with very low friction for smooth rolling
            lowFrictionMaterial = new PhysicsMaterial("ChairRoller");
            lowFrictionMaterial.dynamicFriction = 0.05f;
            lowFrictionMaterial.staticFriction = 0.05f;
            lowFrictionMaterial.frictionCombine = PhysicsMaterialCombine.Minimum;
            lowFrictionMaterial.bounciness = 0f;
            lowFrictionMaterial.bounceCombine = PhysicsMaterialCombine.Minimum;
            
            // Apply to all colliders on this object
            var colliders = GetComponentsInChildren<Collider>();
            foreach (var col in colliders)
            {
                col.material = lowFrictionMaterial;
            }
            
            if (debugLogs) Debug.Log($"[ChairLocomotion] Applied low-friction material to {colliders.Length} colliders");
        }

        public override void OnNetworkSpawn()
        {
            if (debugLogs) Debug.Log($"[ChairLocomotion] OnNetworkSpawn - IsOwner={IsOwner}, IsSpawned={IsSpawned}");
            
            // Ensure rigidbody is NOT kinematic - we want physics to work
            if (rb != null)
            {
                rb.isKinematic = false;
            }
        }

        private void FixedUpdate()
        {
            // Local player seated: always drive physics (even if network spawn is still settling).
            // Remote / non-seated: only the network owner may simulate.
            if (!isLocalPlayerSeated)
            {
                if (!IsSpawned || !IsOwner)
                    return;
            }

            if (rb == null)
                return;

            if (rb.isKinematic)
            {
                if (debugLogs) Debug.Log("[ChairLocomotion] Rigidbody was kinematic, fixing...");
                rb.isKinematic = false;
            }

            ApplyMovement();
            ClampVelocities();
            StabilizeChair();
        }

        private void ApplyMovement()
        {
            if (rb == null) return;
            
            // Log input when we receive non-zero input
            if (currentInput.sqrMagnitude > 0.01f)
            {
                if (debugLogs && Time.frameCount % 60 == 0)
                {
                    Debug.Log($"[ChairLocomotion] Applying: input={currentInput}, vel={rb.linearVelocity.magnitude:F2} m/s");
                }
            }

            // Forward/Backward Movement - use Force mode for more immediate response
            if (Mathf.Abs(currentInput.y) > 0.05f)
            {
                Vector3 moveForce = transform.forward * currentInput.y * moveAcceleration;
                rb.AddForce(moveForce, ForceMode.Force);
            }

            // Rotation (Left/Right)
            if (Mathf.Abs(currentInput.x) > 0.05f)
            {
                Vector3 rotationTorqueVector = Vector3.up * currentInput.x * rotationTorque;
                rb.AddTorque(rotationTorqueVector, ForceMode.Force);
            }
        }

        private void ClampVelocities()
        {
            if (rb == null) return;
            
            // Clamp linear velocity (horizontal only)
            Vector3 horizontalVel = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
            if (horizontalVel.magnitude > maxSpeed)
            {
                Vector3 clampedVel = horizontalVel.normalized * maxSpeed;
                rb.linearVelocity = new Vector3(clampedVel.x, 0, clampedVel.z);
            }
            else
            {
                // Zero out any Y velocity
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
            }

            // Clamp angular velocity
            float maxAngularRad = maxAngularSpeed * Mathf.Deg2Rad;
            if (Mathf.Abs(rb.angularVelocity.y) > maxAngularRad)
            {
                rb.angularVelocity = new Vector3(0, Mathf.Sign(rb.angularVelocity.y) * maxAngularRad, 0);
            }
            else
            {
                // Zero out X and Z angular velocity
                rb.angularVelocity = new Vector3(0, rb.angularVelocity.y, 0);
            }
        }

        private void StabilizeChair()
        {
            // Use Rigidbody moves so physics and NetworkTransform stay in sync on device builds.
            Vector3 pos = rb.position;
            pos.y = groundY;
            rb.MovePosition(pos);

            float yAngle = transform.eulerAngles.y;
            rb.MoveRotation(Quaternion.Euler(0f, yAngle, 0f));
        }

        /// <summary>
        /// Called by the local player's ChairManager to feed input.
        /// X = Turn (-1 left, +1 right)
        /// Y = Move (-1 back, +1 forward)
        /// </summary>
        public void ProcessInput(Vector2 input)
        {
            currentInput = input;
        }

        /// <summary>
        /// Called when the local player sits/stands from this chair.
        /// This bypasses ownership checks for local control.
        /// </summary>
        public void SetLocalPlayerSeated(bool seated)
        {
            isLocalPlayerSeated = seated;
            if (debugLogs) Debug.Log($"[ChairLocomotion] SetLocalPlayerSeated: {seated}");

            ConfigureNetworkTransformForSeated(seated);
            
            if (seated && rb != null)
            {
                groundY = transform.position.y;
                
                rb.isKinematic = false;
                rb.WakeUp();
                
                rb.linearDamping = linearDamping;
                rb.angularDamping = angularDamping;
            }
        }

        void ConfigureNetworkTransformForSeated(bool seated)
        {
            if (networkTransform == null)
                return;

            if (seated)
            {
                if (!hasCachedInLocalSpace)
                {
                    cachedInLocalSpace = networkTransform.InLocalSpace;
                    hasCachedInLocalSpace = true;
                }

                // World-space sync matches rolling physics (InLocalSpace fights Rigidbody on Quest builds).
                networkTransform.InLocalSpace = false;
            }
            else if (hasCachedInLocalSpace)
            {
                networkTransform.InLocalSpace = cachedInLocalSpace;
            }
        }

        /// <summary>
        /// Immediately stops all chair movement.
        /// </summary>
        public void StopMovement()
        {
            currentInput = Vector2.zero;
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        public override void OnDestroy()
        {
            // Clean up the material we created
            if (lowFrictionMaterial != null)
            {
                Destroy(lowFrictionMaterial);
            }
            
            base.OnDestroy();
        }

        private void OnValidate()
        {
            // Ensure sensible defaults
            moveAcceleration = Mathf.Max(5f, moveAcceleration);
            rotationTorque = Mathf.Max(20f, rotationTorque);
            maxSpeed = Mathf.Clamp(maxSpeed, 1f, 10f);
            maxAngularSpeed = Mathf.Clamp(maxAngularSpeed, 60f, 360f);
        }
    }
}
