using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// High-Five Snap — detects hand-to-hand collisions and fires effects.
    /// Attach this to every hand GameObject (LeftHand and RightHand) on the player prefab.
    ///
    /// <b>Per-hand setup:</b>
    /// <list type="bullet">
    ///   <item>Tag the GameObject <c>"PlayerHand"</c> (already set on existing HandCollider objects).</item>
    ///   <item>Add a <see cref="Collider"/> with <c>isTrigger = true</c>
    ///         (a small SphereCollider works well).</item>
    ///   <item>Add a <see cref="Rigidbody"/> (<c>isKinematic = true</c> is fine).</item>
    /// </list>
    ///
    /// <b>Local / offline mode:</b>
    ///   Works out of the box — effects play on this client only.
    ///
    /// <b>Networked mode (NGO):</b>
    ///   Add <see cref="HighFiveSnapNetwork"/> to the player prefab root
    ///   (same GameObject as <see cref="XRINetworkPlayer"/>).
    ///   This script auto-discovers it at <c>Awake</c> and routes events through RPCs.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class HighFiveSnap : MonoBehaviour
    {
        /// <summary>Identifies which hand this component lives on.</summary>
        public enum HandSide { Left = 0, Right = 1 }

        #region Inspector Fields
        // ─────────────────────────────────────────────────────────────────────

        [Header("Hand Identity")]
        [Tooltip("Set to Left or Right to match the hand this is attached to.")]
        [SerializeField] HandSide m_HandSide = HandSide.Left;

        [Header("Detection")]
        [Tooltip("Minimum relative speed (m/s) between the two hands to register a snap. " +
                 "Typical range: 1.2 – 2.0 m/s.")]
        [SerializeField] float m_SnapSpeedThreshold = 1.5f;

        [Tooltip("Seconds before this hand can fire another snap.")]
        [SerializeField] float m_Cooldown = 0.4f;

        [Header("Clap Detection (stricter than high-five)")]
        [Tooltip("Minimum relative speed (m/s) between your own hands to register a clap.")]
        [SerializeField] float m_ClapSpeedThreshold = 1.3f;

        [Tooltip("Minimum speed (m/s) at which both hands must move toward each other.")]
        [SerializeField] float m_ClapMinClosingSpeed = 0.8f;

        [Tooltip("Minimum speed (m/s) each hand must have — rejects one hand sweeping past a still hand.")]
        [SerializeField] float m_ClapMinHandSpeed = 0.25f;

        [Header("Audio")]
        [Tooltip("One-shot sound clip for the snap.")]
        [SerializeField] AudioClip m_SnapClip;

        [Tooltip("Playback volume (0–1).")]
        [SerializeField, Range(0f, 1f)] float m_SnapVolume = 1.0f;

        [Header("Particles")]
        [Tooltip("Prefab spawned at the contact point (should contain a ParticleSystem).")]
        [SerializeField] GameObject m_SparkPrefab;

        [Tooltip("Seconds before the spark instance is destroyed.")]
        [SerializeField] float m_SparkLifetime = 1.5f;

        [Header("Haptics")]
        [Tooltip("Vibration strength sent to the XR controller (0–1).")]
        [SerializeField, Range(0f, 1f)] float m_HapticAmplitude = 0.7f;

        [Tooltip("Vibration duration in seconds.")]
        [SerializeField] float m_HapticDuration = 0.15f;

        [Header("Debug")]
        [Tooltip("Allow high-fiving your own hands (for solo testing). Disable for production.")]
        [SerializeField] bool m_AllowSelfHighFive = false;

        #endregion

        #region Runtime State
        // ─────────────────────────────────────────────────────────────────────

        // Manual velocity tracking (kinematic rigidbodies report zero velocity).
        Vector3 m_LastPosition;
        Vector3 m_TrackedVelocity;

        // Cooldown timestamp.
        float m_LastSnapTime = float.NegativeInfinity;

        // Cached references.
        Rigidbody m_Rigidbody;
        XRINetworkPlayer m_OwnerPlayer;
        HighFiveSnapNetwork m_NetworkHandler;

        #endregion

        #region Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Which hand side this component represents.</summary>
        public HandSide handSide => m_HandSide;

        /// <summary>Inspector-configured haptic amplitude.</summary>
        public float hapticAmplitude => m_HapticAmplitude;

        /// <summary>Inspector-configured haptic duration.</summary>
        public float hapticDuration => m_HapticDuration;

        public AudioClip snapClip => m_SnapClip;
        public float snapVolume => m_SnapVolume;
        public GameObject sparkPrefab => m_SparkPrefab;
        public float sparkLifetime => m_SparkLifetime;
        public float lastSnapTime => m_LastSnapTime;

        /// <summary>
        /// The network player that owns this hand.
        /// <c>null</c> when running without networking.
        /// </summary>
        public XRINetworkPlayer ownerPlayer => m_OwnerPlayer;

        /// <summary>
        /// Current velocity of the hand.
        /// Prefers <see cref="Rigidbody.linearVelocity"/> on non-kinematic bodies;
        /// falls back to manual position-delta tracking.
        /// </summary>
        public Vector3 GetVelocity()
        {
            if (m_Rigidbody != null && !m_Rigidbody.isKinematic)
            {
                Vector3 rbVel = m_Rigidbody.linearVelocity;
                if (rbVel.sqrMagnitude > 0.001f)
                    return rbVel;
            }
            return m_TrackedVelocity;
        }

        /// <summary>
        /// Spawn the snap audio and spark particle at <paramref name="point"/>.
        /// Safe to call from local trigger, from an RPC callback, or from tests.
        /// </summary>
        public void SpawnEffects(Vector3 point)
        {
            if (m_SnapClip != null)
                AudioSource.PlayClipAtPoint(m_SnapClip, point, m_SnapVolume);

            if (m_SparkPrefab != null)
            {
                GameObject fx = Instantiate(m_SparkPrefab, point, Quaternion.identity);
                Destroy(fx, m_SparkLifetime);
            }
        }

        /// <summary>
        /// <c>true</c> when this hand belongs to the local (headset-wearing) player.
        /// Defaults to <c>true</c> in offline scenarios.
        /// </summary>
        public bool IsLocalHand()
        {
            if (m_OwnerPlayer == null) return true;
            return m_OwnerPlayer == XRINetworkPlayer.LocalPlayer;
        }

/// <summary>
        /// Claps require both hands moving toward each other — not one hand sweeping past a still hand.
        /// </summary>
        bool PassesClapMotionCheck(Vector3 myVelocity, Vector3 otherVelocity, Vector3 relVelocity, Vector3 otherPosition)
        {
            float minHandSpeedSq = m_ClapMinHandSpeed * m_ClapMinHandSpeed;
            if (myVelocity.sqrMagnitude < minHandSpeedSq || otherVelocity.sqrMagnitude < minHandSpeedSq)
                return false;

            Vector3 toOther = otherPosition - transform.position;
            if (toOther.sqrMagnitude < 0.0001f)
                return false;

            toOther.Normalize();

            float closingSpeed = Vector3.Dot(relVelocity, toOther);
            return closingSpeed >= m_ClapMinClosingSpeed;
        }


        #endregion

        #region Unity Callbacks
        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            m_Rigidbody = GetComponent<Rigidbody>();
            m_LastPosition = transform.position;

            // Walk up the hierarchy to find the owning player.
            m_OwnerPlayer = GetComponentInParent<XRINetworkPlayer>();

            // Look for the optional NGO network handler on the player root.
            if (m_OwnerPlayer != null)
                m_NetworkHandler = m_OwnerPlayer.GetComponent<HighFiveSnapNetwork>();
        }

        void Update()
        {
            // Frame-by-frame velocity tracking — works even for kinematic bodies.
            if (Time.deltaTime > Mathf.Epsilon)
                m_TrackedVelocity = (transform.position - m_LastPosition) / Time.deltaTime;

            m_LastPosition = transform.position;
        }

        void OnTriggerEnter(Collider other)
        {
            // ── 1. Tag filter (fast early-out) ──
            //       Uses the project's existing "PlayerHand" tag on HandCollider objects.
            if (!other.CompareTag("PlayerHand")) return;

            // ── 2. Component filter — the other collider must also be a hand ──
            HighFiveSnap otherSnap = other.GetComponent<HighFiveSnap>();
            if (otherSnap == null) return;

            // ── 3. Handle clapping (colliding own hands together) ──
            bool isClap = otherSnap.ownerPlayer != null && otherSnap.ownerPlayer == m_OwnerPlayer;
            if (isClap)
            {
                // Prevent double-triggering: only the Left hand processes the self-collision, the Right hand returns early.
                if (m_HandSide == HandSide.Right)
                    return;
            }
            // If it is a standard high-five between two different players, don't allow it if self-high-five is disabled (safety fallback)
            else if (!m_AllowSelfHighFive &&
                     otherSnap.ownerPlayer != null &&
                     otherSnap.ownerPlayer == m_OwnerPlayer)
            {
                return;
            }

            // ── 4. Only the LOCAL player's hand should run detection ──
            //       Remote representations will also receive OnTriggerEnter;
            //       we skip those to avoid doubling effects.
            if (!IsLocalHand()) return;

            // ── 5. Cooldown ──
            if (Time.time - m_LastSnapTime < m_Cooldown) return;

            // ── 6. Speed / motion check ──
            Vector3 myVelocity = GetVelocity();
            Vector3 otherVelocity = otherSnap.GetVelocity();
            Vector3 relVelocity = myVelocity - otherVelocity;
            float relSpeed = relVelocity.magnitude;

            if (isClap)
            {
                if (relSpeed < m_ClapSpeedThreshold) return;
                if (!PassesClapMotionCheck(myVelocity, otherVelocity, relVelocity, other.transform.position))
                    return;
            }
            else if (relSpeed < m_SnapSpeedThreshold)
            {
                return;
            }

            // ════════════════════════ VALID SNAP ════════════════════════════
            m_LastSnapTime = Time.time;
            Vector3 contactPoint = (transform.position + other.transform.position) * 0.5f;

            // ---------- Networked path ----------
            if (m_NetworkHandler != null && m_NetworkHandler.IsSpawned)
            {
                ulong myClientId = m_OwnerPlayer != null
                    ? m_OwnerPlayer.OwnerClientId : 0;
                ulong otherClientId = otherSnap.ownerPlayer != null
                    ? otherSnap.ownerPlayer.OwnerClientId : 0;

                // Send haptics (vibrate both controllers immediately if clapping, otherwise vibrate own controller)
                if (isClap)
                {
                    HapticsUtil.SendHapticImpulse(HandSide.Left, m_HapticAmplitude, m_HapticDuration);
                    HapticsUtil.SendHapticImpulse(HandSide.Right, m_HapticAmplitude, m_HapticDuration);
                }
                else
                {
                    HapticsUtil.SendHapticImpulse(m_HandSide, m_HapticAmplitude, m_HapticDuration);
                }

                // Spawn effects locally for instant, zero-latency visual/audio feedback for this player.
                SpawnEffects(contactPoint);

                // Notify the server → server relays to other spectators (excluding the two involved).
                // Both clients will trigger this and play locally, and the server will safely 
                // filter out the duplicate call using a server-side cooldown check.
                m_NetworkHandler.RequestHighFiveServerRpc(
                    contactPoint,
                    (int)m_HandSide,
                    otherClientId,
                    (int)otherSnap.handSide
                );
            }
            // ---------- Local / offline path ----------
            else
            {
                SpawnEffects(contactPoint);

                if (isClap)
                {
                    HapticsUtil.SendHapticImpulse(HandSide.Left, m_HapticAmplitude, m_HapticDuration);
                    HapticsUtil.SendHapticImpulse(HandSide.Right, m_HapticAmplitude, m_HapticDuration);
                }
                else
                {
                    HapticsUtil.SendHapticImpulse(m_HandSide, m_HapticAmplitude, m_HapticDuration);

                    // If the other hand is also local (e.g. single-player testing),
                    // vibrate that controller too.
                    if (otherSnap.IsLocalHand())
                        HapticsUtil.SendHapticImpulse(otherSnap.handSide, m_HapticAmplitude, m_HapticDuration);
                }
            }
        }

        #endregion
    }
}
