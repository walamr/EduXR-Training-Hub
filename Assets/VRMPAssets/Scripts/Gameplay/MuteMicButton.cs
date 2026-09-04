using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace XRMultiplayer
{
    /// <summary>
    /// Controls the MuteMic Toggle button (TextTileButton_IconAndLabel_Toggle) in the Workstation Dashboard.
    /// Automatically hooks into the Unity Toggle component on the same GameObject.
    /// </summary>
    public class MuteMicButton : MonoBehaviour
    {
        [Header("Icon Display")]
        [Tooltip("The Image component used to display the microphone icon.")]
        [SerializeField] private Image iconImage;

        [Tooltip("Sprite shown when the mic is ON (mic active).")]
        [SerializeField] private Sprite spriteOn;

        [Tooltip("Sprite shown when the mic is OFF/muted.")]
        [SerializeField] private Sprite spriteOff;

        [Header("Label")]
        [SerializeField] private TMP_Text label;
        [SerializeField] private string labelWhenActive = "Mute";
        [SerializeField] private string labelWhenMuted  = "Unmute";

        private VoiceChatManager m_VoiceChatManager;
        private Toggle m_Toggle;

        private void Awake()
        {
            // Hook into the Toggle component on this GameObject (or parent)
            m_Toggle = GetComponent<Toggle>();
            if (m_Toggle == null)
                m_Toggle = GetComponentInParent<Toggle>();

            if (m_Toggle != null)
            {
                m_Toggle.onValueChanged.AddListener(OnToggleChanged);
                // Make sure toggle starts unchecked (mic ON = not muted)
                m_Toggle.SetIsOnWithoutNotify(false);
            }
            else
            {
                Debug.LogWarning("[MuteMicButton] No Toggle component found on this GameObject or its parent.");
            }

            // Find VoiceChatManager
            m_VoiceChatManager = FindFirstObjectByType<VoiceChatManager>();
            if (m_VoiceChatManager != null)
            {
                // Stay in sync if another UI changes mute state externally
                m_VoiceChatManager.selfMuted.Subscribe(SyncFromManager);
            }

            // Apply initial visuals
            ApplyVisuals(false);
        }

        private void OnDestroy()
        {
            if (m_Toggle != null)
                m_Toggle.onValueChanged.RemoveListener(OnToggleChanged);

            if (m_VoiceChatManager != null)
                m_VoiceChatManager.selfMuted.Unsubscribe(SyncFromManager);
        }

        /// <summary>
        /// Called automatically when the Toggle is clicked (on or off).
        /// isOn = true  → button is "pressed/selected" → mute the mic.
        /// isOn = false → button is "released"         → unmute the mic.
        /// </summary>
        private void OnToggleChanged(bool isOn)
        {
            bool isMuted = isOn;
            ApplyVisuals(isMuted);
            m_VoiceChatManager?.ToggleSelfMute(true, isMuted);
        }

        /// <summary>
        /// Sync visuals if an external system (e.g. Podium Mode) changes the mute state.
        /// </summary>
        private void SyncFromManager(bool isMuted)
        {
            if (m_Toggle != null)
                m_Toggle.SetIsOnWithoutNotify(isMuted);
            ApplyVisuals(isMuted);
        }

        private void ApplyVisuals(bool isMuted)
        {
            if (iconImage != null)
                iconImage.sprite = isMuted ? spriteOff : spriteOn;

            if (label != null)
                label.text = isMuted ? labelWhenMuted : labelWhenActive;
        }

        /// <summary>
        /// Optional: call this from Button OnClick if you prefer wiring manually.
        /// </summary>
        public void OnButtonPressed()
        {
            if (m_Toggle != null)
                m_Toggle.isOn = !m_Toggle.isOn;
        }
    }
}
