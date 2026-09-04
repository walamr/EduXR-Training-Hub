using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace XRMultiplayer.Drawing
{
    /// <summary>
    /// UI panel controller for drawing tools.
    /// Provides color selection, eraser, reset, and pointer mode buttons.
    /// </summary>
    public class RayDrawingUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RayDrawingManager m_DrawingManager;

        [Header("Buttons")]
        [SerializeField] private Button m_BlueButton;
        [SerializeField] private Button m_GreenButton;
        [SerializeField] private Button m_RedButton;
        [SerializeField] private Button m_EraserButton;
        [SerializeField] private Button m_ResetButton;
        [SerializeField] private Button m_PointerButton;

        [Header("Selection Indicators")]
        [SerializeField] private Image m_BlueIndicator;
        [SerializeField] private Image m_GreenIndicator;
        [SerializeField] private Image m_RedIndicator;
        [SerializeField] private Image m_EraserIndicator;
        [SerializeField] private Image m_PointerIndicator;

        private void Start()
        {
            SetupButtonListeners();
            
            // Default to blue selected
            UpdateSelectionIndicators("blue");
        }

        private void OnDestroy()
        {
            RemoveButtonListeners();
        }

        private void SetupButtonListeners()
        {
            if (m_BlueButton != null)
                m_BlueButton.onClick.AddListener(OnBlueClicked);
            
            if (m_GreenButton != null)
                m_GreenButton.onClick.AddListener(OnGreenClicked);
            
            if (m_RedButton != null)
                m_RedButton.onClick.AddListener(OnRedClicked);
            
            if (m_EraserButton != null)
                m_EraserButton.onClick.AddListener(OnEraserClicked);
            
            if (m_ResetButton != null)
                m_ResetButton.onClick.AddListener(OnResetClicked);
            
            if (m_PointerButton != null)
                m_PointerButton.onClick.AddListener(OnPointerClicked);
        }

        private void RemoveButtonListeners()
        {
            if (m_BlueButton != null)
                m_BlueButton.onClick.RemoveListener(OnBlueClicked);
            
            if (m_GreenButton != null)
                m_GreenButton.onClick.RemoveListener(OnGreenClicked);
            
            if (m_RedButton != null)
                m_RedButton.onClick.RemoveListener(OnRedClicked);
            
            if (m_EraserButton != null)
                m_EraserButton.onClick.RemoveListener(OnEraserClicked);
            
            if (m_ResetButton != null)
                m_ResetButton.onClick.RemoveListener(OnResetClicked);
            
            if (m_PointerButton != null)
                m_PointerButton.onClick.RemoveListener(OnPointerClicked);
        }

        #region Button Handlers

        private void OnBlueClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.SetColorBlue();
                UpdateSelectionIndicators("blue");
            }
        }

        private void OnGreenClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.SetColorGreen();
                UpdateSelectionIndicators("green");
            }
        }

        private void OnRedClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.SetColorRed();
                UpdateSelectionIndicators("red");
            }
        }

        private void OnEraserClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.SetModeErase();
                UpdateSelectionIndicators("eraser");
            }
        }

        private void OnResetClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.ResetAllStrokes();
            }
        }

        private void OnPointerClicked()
        {
            if (m_DrawingManager != null)
            {
                m_DrawingManager.SetModePointer();
                UpdateSelectionIndicators("pointer");
            }
        }

        #endregion

        #region Visual Feedback

        private void UpdateSelectionIndicators(string selected)
        {
            // Hide all indicators
            SetIndicatorActive(m_BlueIndicator, false);
            SetIndicatorActive(m_GreenIndicator, false);
            SetIndicatorActive(m_RedIndicator, false);
            SetIndicatorActive(m_EraserIndicator, false);
            SetIndicatorActive(m_PointerIndicator, false);

            // Show selected indicator
            switch (selected)
            {
                case "blue":
                    SetIndicatorActive(m_BlueIndicator, true);
                    break;
                case "green":
                    SetIndicatorActive(m_GreenIndicator, true);
                    break;
                case "red":
                    SetIndicatorActive(m_RedIndicator, true);
                    break;
                case "eraser":
                    SetIndicatorActive(m_EraserIndicator, true);
                    break;
                case "pointer":
                    SetIndicatorActive(m_PointerIndicator, true);
                    break;
            }
        }

        private void SetIndicatorActive(Image indicator, bool active)
        {
            if (indicator != null)
            {
                indicator.enabled = active;
            }
        }

        #endregion

        #region Setup

        public void SetDrawingManager(RayDrawingManager manager)
        {
            m_DrawingManager = manager;
        }

        #endregion
    }
}
