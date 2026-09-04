using System.Collections;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Scene-side bridge so UI buttons (e.g. in the Quick Menu) can trigger emoji reactions
    /// without holding a reference to the runtime-spawned local player.
    ///
    /// Wire a button's OnClick to <see cref="SendReaction"/> with the desired emoji index;
    /// at click time it resolves <see cref="XRINetworkPlayer.LocalPlayer"/> and forwards to its
    /// <see cref="EmojiReactionNetwork"/>. Also auto-registers a Quick Menu segment that toggles
    /// the reactions panel.
    /// </summary>
    public class EmojiReactionUI : MonoBehaviour
    {
        [Header("Quick Menu")]
        [SerializeField, Tooltip("Display name of the Quick Menu segment.")]
        string m_QuickMenuName = "React";

        [SerializeField, Tooltip("Optional icon for the Quick Menu segment.")]
        Sprite m_QuickMenuIcon;

        [SerializeField, Tooltip("World-space panel (the reactions grid) toggled by the Quick Menu entry.")]
        GameObject m_ReactionPanel;

        [Header("Behaviour")]
        [SerializeField, Tooltip("Close the reactions panel automatically after a reaction is sent.")]
        bool m_CloseAfterReaction = true;

        bool m_RegisteredWithMenu;

        void OnEnable()
        {
            StartCoroutine(RegisterWithQuickMenuWhenReady());
        }

        void OnDisable()
        {
            if (m_RegisteredWithMenu && QuickMenuManager.Instance != null &&
                QuickMenuManager.Instance.HasSegment(m_QuickMenuName))
            {
                QuickMenuManager.Instance.RemoveSegment(m_QuickMenuName);
            }
            m_RegisteredWithMenu = false;
        }

        IEnumerator RegisterWithQuickMenuWhenReady()
        {
            float timeout = 10f;
            while (QuickMenuManager.Instance == null && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (QuickMenuManager.Instance != null && !m_RegisteredWithMenu &&
                !QuickMenuManager.Instance.HasSegment(m_QuickMenuName))
            {
                QuickMenuManager.Instance.AddSegment(m_QuickMenuName, m_QuickMenuIcon, m_ReactionPanel);
                m_RegisteredWithMenu = true;
            }
        }

        /// <summary>
        /// Sends the given reaction from the local player. Safe to call before the player has
        /// spawned (no-op in that case).
        /// </summary>
        /// <param name="emojiId">Index into the local player's EmojiReactionNetwork sprite list.</param>
        public void SendReaction(int emojiId)
        {
            XRINetworkPlayer local = XRINetworkPlayer.LocalPlayer;
            if (local == null)
            {
                Utils.Log("EmojiReactionUI: No local player yet; reaction ignored.", 1);
                return;
            }

            if (local.TryGetComponent(out EmojiReactionNetwork reactionNetwork))
                reactionNetwork.SendReaction(emojiId);
            else
                Utils.Log("EmojiReactionUI: Local player has no EmojiReactionNetwork component.", 1);

            if (m_CloseAfterReaction && m_ReactionPanel != null && QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.CloseFloatingPanel(m_ReactionPanel);
        }
    }
}
