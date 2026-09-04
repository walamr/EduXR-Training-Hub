using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

namespace XRMultiplayer
{
    /// <summary>
    /// UI component for switching between environments.
    /// Self-initializing - works automatically when added to a button.
    /// </summary>
    public class EnvironmentSwitcherUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private EnvironmentManager m_EnvironmentManager;
        [SerializeField] private Button m_ReturnToRoomButton;
        [SerializeField] private TextMeshProUGUI m_CurrentEnvironmentLabel;
        
        [Header("Menu Generation")]
        [SerializeField] private Transform m_ButtonsContainer;
        [SerializeField] private Button m_ButtonTemplate;

        private void Start()
        {
            if (m_EnvironmentManager == null)
                m_EnvironmentManager = FindFirstObjectByType<EnvironmentManager>();

            // Auto-configure Container if missing
            if (m_ButtonsContainer == null)
            {
                // Try to find a ScrollRect's content first
                var scroll = GetComponentInChildren<ScrollRect>();
                if (scroll != null) m_ButtonsContainer = scroll.content;
                else m_ButtonsContainer = transform; // Fallback to self
            }

            // Auto-configure Template if missing
            if (m_ButtonTemplate == null)
            {
                m_ButtonTemplate = m_ButtonsContainer.GetComponentInChildren<Button>();
                
                // Special case: If the script is ON the button, we can't use self as template easily without recursion issues.
                // But we can check for a child button.
                if (m_ButtonTemplate == null)
                {
                    Debug.LogWarning("[EnvironmentSwitcherUI] No Button Template found. Please add a button as a child to use as a template.");
                }
            }

            if (m_EnvironmentManager != null)
            {
                m_EnvironmentManager.OnEnvironmentChanged -= OnEnvironmentChanged;
                m_EnvironmentManager.OnEnvironmentChanged += OnEnvironmentChanged;
                
                // Generate Menu Buttons
                GenerateMenuButtons();
            }
            else
            {
                Debug.LogWarning("[EnvironmentSwitcherUI] EnvironmentManager not found!");
            }

            if (m_ReturnToRoomButton != null)
            {
                m_ReturnToRoomButton.onClick.RemoveListener(OnReturnToRoomClicked);
                m_ReturnToRoomButton.onClick.AddListener(OnReturnToRoomClicked);
            }

            UpdateUI();
        }

        private void GenerateMenuButtons()
        {
            if (m_ButtonTemplate == null || m_ButtonsContainer == null) return;
            
            // Hide template
            m_ButtonTemplate.gameObject.SetActive(false);

            // Clear existing buttons (except template)
            foreach (Transform child in m_ButtonsContainer)
            {
                if (child != m_ButtonTemplate.transform)
                {
                    Destroy(child.gameObject);
                }
            }

            // 1. Add "Office" (Conference Room) Option
            CreateEnvironmentButton("Office", -1);

            // 2. Add other environments
            var environments = m_EnvironmentManager.Environments;
            for (int i = 0; i < environments.Count; i++)
            {
                CreateEnvironmentButton(environments[i].Name, i);
            }
        }

        private void CreateEnvironmentButton(string label, int index)
        {
            var btnObj = Instantiate(m_ButtonTemplate.gameObject, m_ButtonsContainer);
            btnObj.name = $"Btn_{label}";
            btnObj.SetActive(true);
            
            var btn = btnObj.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnEnvironmentButtonClicked(index));

            // Set Label
            var tmp = btnObj.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null) tmp.text = label;
            else
            {
                var txt = btnObj.GetComponentInChildren<Text>();
                if (txt != null) txt.text = label;
            }
        }

        private void OnEnvironmentButtonClicked(int index)
        {
            if (m_EnvironmentManager != null)
            {
                m_EnvironmentManager.SwitchToEnvironment(index);
            }
        }

        private void OnDestroy()
        {
            if (m_EnvironmentManager != null)
                m_EnvironmentManager.OnEnvironmentChanged -= OnEnvironmentChanged;
        }

        public void OnReturnToRoomClicked()
        {
            if (m_EnvironmentManager != null)
                m_EnvironmentManager.ReturnToConferenceRoom();
        }

        private void OnEnvironmentChanged(int index)
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (m_EnvironmentManager == null) return;

            if (m_CurrentEnvironmentLabel != null)
            {
                m_CurrentEnvironmentLabel.text = m_EnvironmentManager.GetCurrentEnvironmentName();
            }

            // Only show "Return" button if NOT in conference room
            if (m_ReturnToRoomButton != null)
                m_ReturnToRoomButton.gameObject.SetActive(!m_EnvironmentManager.IsInConferenceRoom);
        }
    }
}
