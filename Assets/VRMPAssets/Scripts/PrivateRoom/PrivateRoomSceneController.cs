using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Component that listens for private room visual profile changes and applies them to the scene.
    /// </summary>
    public class PrivateRoomSceneController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Renderer m_FloorRenderer;
        [SerializeField] private Renderer m_WallRenderer;
        [SerializeField] private Image[] m_UIPanels;
        [SerializeField] private Graphic[] m_UIAccents;

        [Header("Profile")]
        [SerializeField] private PrivateRoomVisualProfile m_ActiveProfile;

        public PrivateRoomVisualProfile ActiveProfile
        {
            get => m_ActiveProfile;
            set
            {
                m_ActiveProfile = value;
                ApplyProfile();
            }
        }

        private void Start()
        {
            if (m_ActiveProfile != null)
            {
                ApplyProfile();
            }
        }

        [ContextMenu("Apply Profile")]
        public void ApplyProfile()
        {
            if (m_ActiveProfile == null) return;

            m_ActiveProfile.Apply();

            if (m_FloorRenderer != null && m_ActiveProfile.FloorMaterial != null)
                m_FloorRenderer.sharedMaterial = m_ActiveProfile.FloorMaterial;

            if (m_WallRenderer != null && m_ActiveProfile.WallMaterial != null)
                m_WallRenderer.sharedMaterial = m_ActiveProfile.WallMaterial;

            // Apply UI Colors
            if (m_UIPanels != null)
            {
                foreach (var panel in m_UIPanels)
                {
                    if (panel != null) panel.color = m_ActiveProfile.UIPanelColor;
                }
            }

            if (m_UIAccents != null)
            {
                foreach (var accent in m_UIAccents)
                {
                    if (accent != null) accent.color = m_ActiveProfile.UIAccentColor;
                }
            }
        }
}
}
