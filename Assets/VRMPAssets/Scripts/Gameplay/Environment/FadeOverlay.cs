using UnityEngine;
using UnityEngine.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Simple fade overlay for environment transitions.
    /// Add this to a full-screen UI Image with a CanvasGroup.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    [RequireComponent(typeof(Image))]
    public class FadeOverlay : MonoBehaviour
    {
        private CanvasGroup m_CanvasGroup;
        private Image m_Image;

        private void Awake()
        {
            m_CanvasGroup = GetComponent<CanvasGroup>();
            m_Image = GetComponent<Image>();

            // Setup for fade overlay
            m_CanvasGroup.alpha = 0f;
            m_CanvasGroup.blocksRaycasts = false;
            m_CanvasGroup.interactable = false;

            m_Image.color = Color.black;
            m_Image.raycastTarget = false;

            gameObject.SetActive(false);
        }

        /// <summary>
        /// Get the CanvasGroup for the EnvironmentManager to control.
        /// </summary>
        public CanvasGroup GetCanvasGroup() => m_CanvasGroup;
    }
}
