using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Networked floating-emoji reactions. Attach to the player prefab root (same GameObject
    /// as <see cref="XRINetworkPlayer"/> / <see cref="NetworkObject"/>).
    ///
    /// <b>Flow</b> (mirrors <see cref="HighFiveSnapNetwork"/>):
    /// <list type="number">
    ///   <item>The local owner calls <see cref="SendReaction"/>.</item>
    ///   <item>Owner-only <see cref="SendReactionServerRpc"/> runs on the server, which throttles
    ///         spam and broadcasts <see cref="PlayReactionClientRpc"/> to everyone.</item>
    ///   <item>Every client spawns a <see cref="FloatingEmoji"/> above this player's head.</item>
    /// </list>
    /// The emoji is networked as an int index into <see cref="m_EmojiSprites"/>, so each client
    /// must have the same sprite list assigned (same pattern as the high-five hand-side int).
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class EmojiReactionNetwork : NetworkBehaviour
    {
        [Header("Emoji Set")]
        [SerializeField, Tooltip("Sprites selectable as reactions. The list index is what gets networked, so keep it identical across builds.")]
        Sprite[] m_EmojiSprites;

        [Header("Spawn")]
        [SerializeField, Tooltip("Optional prefab containing a SpriteRenderer and FloatingEmoji. If null, a simple emoji object is created at runtime.")]
        GameObject m_FloatingEmojiPrefab;

        [SerializeField, Tooltip("Height above the player's head transform to spawn the emoji.")]
        float m_HeadOffset = 0.45f;

        [SerializeField, Tooltip("World-space scale applied to the spawned emoji.")]
        float m_EmojiScale = 0.25f;

        [SerializeField, Tooltip("Seconds before the spawned emoji disappears.")]
        float m_Lifetime = 2.0f;

        [Header("Anti-Spam")]
        [SerializeField, Tooltip("Minimum seconds between accepted reactions from this player (enforced on the server).")]
        float m_ServerCooldown = 0.5f;

        XRINetworkPlayer m_Player;
        float m_LastServerReactionTime = float.NegativeInfinity;

        void Awake()
        {
            TryGetComponent(out m_Player);
            if (EmojiCount == 0)
                Debug.LogWarning("[EmojiReactionNetwork] No emoji sprites assigned - reactions will silently do nothing. Assign m_EmojiSprites on the player prefab.", this);
        }

        /// <summary>Number of configured emoji sprites.</summary>
        public int EmojiCount => m_EmojiSprites != null ? m_EmojiSprites.Length : 0;

        /// <summary>
        /// Call on the LOCAL player to broadcast a reaction to everyone. No-op if not the owner
        /// or if the id is out of range.
        /// </summary>
        /// <param name="emojiId">Index into the configured emoji sprite list.</param>
        public void SendReaction(int emojiId)
        {
            if (!IsOwner) return;
            if (emojiId < 0 || emojiId >= EmojiCount) return;
            SendReactionServerRpc(emojiId);
        }

        // RequireOwnership defaults to true: only the player who owns this NetworkObject can send,
        // which is exactly what we want (each player reacts for themselves).
        [ServerRpc]
        void SendReactionServerRpc(int emojiId)
        {
            // Server-side throttle to prevent spam from a single player.
            if (Time.time - m_LastServerReactionTime < m_ServerCooldown) return;
            m_LastServerReactionTime = Time.time;

            if (emojiId < 0 || emojiId >= EmojiCount) return;
            PlayReactionClientRpc(emojiId);
        }

        [ClientRpc]
        void PlayReactionClientRpc(int emojiId)
        {
            SpawnEmoji(emojiId);
        }

        void SpawnEmoji(int emojiId)
        {
            if (emojiId < 0 || emojiId >= EmojiCount) return;

            Sprite sprite = m_EmojiSprites[emojiId];
            if (sprite == null) return;

            Vector3 spawnPos = GetHeadAnchor() + Vector3.up * m_HeadOffset;

            GameObject go = m_FloatingEmojiPrefab != null
                ? Instantiate(m_FloatingEmojiPrefab, spawnPos, Quaternion.identity)
                : CreateRuntimeEmoji(spawnPos);

            go.transform.localScale = Vector3.one * m_EmojiScale;

            if (!go.TryGetComponent(out FloatingEmoji floating))
                floating = go.AddComponent<FloatingEmoji>();
            floating.Play(sprite, m_Lifetime);
        }

        Vector3 GetHeadAnchor()
        {
            if (m_Player != null && m_Player.head != null)
                return m_Player.head.position;
            // Fallback: roughly head height above the object root.
            return transform.position + Vector3.up * 1.6f;
        }

        static GameObject CreateRuntimeEmoji(Vector3 position)
        {
            var go = new GameObject("FloatingEmoji");
            go.transform.position = position;
            go.AddComponent<SpriteRenderer>();
            return go;
        }
    }
}
