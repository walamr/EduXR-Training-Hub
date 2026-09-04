using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// NGO networking extension for <see cref="HighFiveSnap"/>.
    /// Attach to the player prefab root (same GameObject as
    /// <see cref="XRINetworkPlayer"/> / <see cref="NetworkObject"/>).
    ///
    /// <b>RPC flow:</b>
    /// <list type="number">
    ///   <item>Local <see cref="HighFiveSnap"/> detects a valid snap and calls
    ///         <see cref="RequestHighFiveServerRpc"/>.</item>
    ///   <item>Server broadcasts <see cref="PlayEffectsClientRpc"/> to every client
    ///         <i>except</i> the sender (sender already spawned effects locally).</item>
    ///   <item>Server sends a targeted <see cref="VibrateHandClientRpc"/> to the
    ///         other involved player so their controller vibrates.</item>
    /// </list>
    ///
    /// The initiating player always vibrates their own controller immediately
    /// (inside <see cref="HighFiveSnap"/>) for low-latency haptic feedback.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class HighFiveSnapNetwork : NetworkBehaviour
    {
        #region Inspector Fields
        // ─────────────────────────────────────────────────────────────────────
        // These mirror the effect settings in HighFiveSnap so that the server
        // can relay consistent parameters to all clients.  Keep them in sync,
        // or assign the same assets / values in the inspector.

        [Header("Effect Prefabs (should match HighFiveSnap)")]
        [Tooltip("Snap audio clip relayed to remote clients.")]
        [SerializeField] AudioClip m_SnapClip;

        [Tooltip("Spark particle prefab relayed to remote clients.")]
        [SerializeField] GameObject m_SparkPrefab;

        [SerializeField] float m_SparkLifetime = 1.5f;

        [SerializeField, Range(0f, 1f)] float m_SnapVolume = 1.0f;

        [Header("Haptics (relayed to the other player)")]
        [SerializeField, Range(0f, 1f)] float m_HapticAmplitude = 0.7f;

        [SerializeField] float m_HapticDuration = 0.15f;

        #endregion

        private float m_LastServerHighFiveTime = float.NegativeInfinity;

        #region Reusable Buffers
        // Avoid GC allocs on every RPC call.
        readonly List<ulong> m_TargetIdsBuffer = new List<ulong>(16);
        #endregion

        #region ServerRpc
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by the local <see cref="HighFiveSnap"/> when a valid high-five or clap
        /// is detected.
        /// </summary>
        [ServerRpc]
        public void RequestHighFiveServerRpc(
            Vector3 contactPoint,
            int myHandSide,
            ulong otherClientId,
            int otherHandSide,
            ServerRpcParams rpcParams = default)
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;

            // Server-side duplicate prevention: ignore requests within 0.3s of the last processed high five/clap
            if (Time.time - m_LastServerHighFiveTime < 0.3f)
                return;

            m_LastServerHighFiveTime = Time.time;

            bool isClap = (otherClientId == senderClientId);

            if (isClap)
            {
                // ── [CLAP] Broadcast to EVERYONE in the room ──
                // Clapping should be heard by all users. The sender already played it locally
                // and will automatically filter out the duplicate broadcast.
                PlayEffectsClientRpc(contactPoint);
            }
            else
            {
                // ── [HIGH FIVE] Targeted ONLY to the other player ──
                // Only the two players doing the high-five hear it. The sender played it locally.
                // The other player receives this targeted RPC (and will filter it out if they also triggered locally).
                ClientRpcParams otherParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new ulong[] { otherClientId }
                    }
                };
                PlayEffectsTargetedClientRpc(contactPoint, otherParams);

                // Also trigger targeted haptics for the other player
                VibrateHandClientRpc(otherHandSide, otherParams);
            }
        }

        #endregion

        #region ClientRpcs
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Broadcasts the snap audio and spark particle to ALL clients in the room (for Clapping).
        /// </summary>
        [ClientRpc]
        public void PlayEffectsClientRpc(Vector3 contactPoint)
        {
            ExecuteEffects(contactPoint);
        }

        /// <summary>
        /// Sends the snap audio and spark particle to a targeted client (for High Five).
        /// </summary>
        [ClientRpc]
        public void PlayEffectsTargetedClientRpc(Vector3 contactPoint, ClientRpcParams clientRpcParams)
        {
            ExecuteEffects(contactPoint);
        }

        /// <summary>
        /// Core effects execution logic shared by both broadcast and targeted RPC paths.
        /// </summary>
        private void ExecuteEffects(Vector3 contactPoint)
        {
            // If the local player was one of the active participants in this High Five/Clap
            // and already played it locally (within 0.3 seconds), skip duplicate audio/visuals.
            var handSnaps = FindObjectsByType<HighFiveSnap>(FindObjectsSortMode.None);
            foreach (var snap in handSnaps)
            {
                // Safety check: snap.lastSnapTime > 0f ensures we only filter if they ACTUALLY clapped
                if (snap.IsLocalHand() && snap.lastSnapTime > 0f && Time.time - snap.lastSnapTime < 0.3f)
                {
                    return; // Skip duplicate effects on the local participant's screen
                }
            }

            AudioClip clip = m_SnapClip;
            float volume = m_SnapVolume;
            GameObject spark = m_SparkPrefab;
            float lifetime = m_SparkLifetime;

            // Self-healing fallback: if the network prefab lacks assigned references,
            // dynamically retrieve them from the active HighFiveSnap component on the hands!
            var fallbackSnap = GetComponentInChildren<HighFiveSnap>(true);
            if (fallbackSnap != null)
            {
                if (clip == null) clip = fallbackSnap.snapClip;
                if (spark == null) spark = fallbackSnap.sparkPrefab;
                if (volume <= 0.001f || volume >= 0.999f) volume = fallbackSnap.snapVolume;
                if (lifetime <= 0.01f) lifetime = fallbackSnap.sparkLifetime;
            }

            if (clip != null)
                AudioSource.PlayClipAtPoint(clip, contactPoint, volume);

            if (spark != null)
            {
                GameObject fx = Instantiate(spark, contactPoint, Quaternion.identity);
                Destroy(fx, lifetime);
            }
        }

        /// <summary>
        /// Vibrates the controller on the targeted client.
        /// </summary>
        [ClientRpc]
        public void VibrateHandClientRpc(int handSide,
                                         ClientRpcParams clientRpcParams = default)
        {
            HapticsUtil.SendHapticImpulse(
                (HighFiveSnap.HandSide)handSide,
                m_HapticAmplitude,
                m_HapticDuration);
        }

        #endregion
    }
}
