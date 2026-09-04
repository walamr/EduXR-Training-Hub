using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;

namespace XRMultiplayer
{
    /// <summary>
    /// HUD overlay that displays the voting question and options to the local player.
    /// Follows the player's head in world space.
    /// </summary>
    public class VotingHUD : MonoBehaviour
    {
        [Header("=== FOLLOW SETTINGS ===")]
        [SerializeField] private Transform m_CameraTransform;
        [SerializeField] private float m_Distance = 1.5f;
        [SerializeField] private float m_HeightOffset = 0.3f;
        [SerializeField] private float m_FollowSpeed = 5f;
        [SerializeField] private float m_DeadzoneDistance = 0.25f; // HUD won't move if within this distance

        [Header("=== UI REFERENCES ===")]
        [SerializeField] private GameObject m_HUDRoot;
        [SerializeField] private TMP_Text m_QuestionText;
        [SerializeField] private Transform m_OptionsContainer;
        [SerializeField] private GameObject m_OptionLabelPrefab;
        [SerializeField] private TMP_Text m_VoteStatusText;

        private bool m_Subscribed = false;
        private TMP_Text[] m_OptionTexts = new TMP_Text[0];
        private Image[] m_OptionProgressBars = new Image[0];
        
        // Bar colors matching VotingManager
        private readonly Color[] m_OptionColors = new Color[]
        {
            new Color(0.2f, 0.6f, 1f),   // Blue
            new Color(1f, 0.4f, 0.4f),   // Red
            new Color(0.4f, 1f, 0.4f),   // Green
            new Color(1f, 1f, 0.4f),     // Yellow
            new Color(1f, 0.6f, 0.2f),   // Orange
            new Color(0.8f, 0.4f, 1f)    // Purple
        };

        private void Start()
        {
            if (m_CameraTransform == null)
                m_CameraTransform = Camera.main?.transform;

            if (m_HUDRoot != null)
                m_HUDRoot.SetActive(false);

            StartCoroutine(WaitForVotingManagerAndSubscribe());
        }
        
        /// <summary>
        /// Ensures the options container has proper layout components for dynamic sizing.
        /// </summary>
        private void EnsureLayoutComponents()
        {
            if (m_OptionsContainer == null) return;
            
            // Add VerticalLayoutGroup if not present
            var layout = m_OptionsContainer.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
            {
                layout = m_OptionsContainer.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 8;
                layout.padding = new RectOffset(10, 10, 10, 10);
                layout.childAlignment = TextAnchor.UpperCenter;
                layout.childControlHeight = false;
                layout.childControlWidth = true;
                layout.childForceExpandHeight = false;
                layout.childForceExpandWidth = true;
            }
            
            // Add ContentSizeFitter if not present
            var fitter = m_OptionsContainer.GetComponent<ContentSizeFitter>();
            if (fitter == null)
            {
                fitter = m_OptionsContainer.gameObject.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
        }

        private IEnumerator WaitForVotingManagerAndSubscribe()
        {
            // Wait until VotingManager.Instance is available
            while (VotingManager.Instance == null)
            {
                yield return new WaitForSeconds(0.5f);
            }

            Subscribe();
        }

        private void Subscribe()
        {
            if (m_Subscribed || VotingManager.Instance == null) return;
            
            VotingManager.Instance.OnVotingStarted += OnVotingStarted;
            VotingManager.Instance.OnVotingEnded += OnVotingEnded;
            VotingManager.Instance.OnQuestionChanged += OnQuestionChanged;
            VotingManager.Instance.OnVoteCountsUpdated += OnVoteCountsUpdated;
            m_Subscribed = true;
            
            Debug.Log("[VOTE_DEBUG] Subscribed to VotingManager events");
        }

        private void OnDestroy()
        {
            if (VotingManager.Instance != null && m_Subscribed)
            {
                VotingManager.Instance.OnVotingStarted -= OnVotingStarted;
                VotingManager.Instance.OnVotingEnded -= OnVotingEnded;
                VotingManager.Instance.OnQuestionChanged -= OnQuestionChanged;
                VotingManager.Instance.OnVoteCountsUpdated -= OnVoteCountsUpdated;
            }
        }

        private void LateUpdate()
        {
            if (m_HUDRoot == null || !m_HUDRoot.activeSelf || m_CameraTransform == null)
                return;

            UpdatePosition();
        }

        private void UpdatePosition()
        {
            Vector3 forward = m_CameraTransform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f)
                forward = m_CameraTransform.forward;
            forward.Normalize();

            Vector3 targetPos = m_CameraTransform.position 
                + forward * m_Distance 
                + Vector3.up * m_HeightOffset;

            // Deadzone behavior - only move if drifted too far
            float distanceFromTarget = Vector3.Distance(m_HUDRoot.transform.position, targetPos);
            
            if (distanceFromTarget > m_DeadzoneDistance)
            {
                if (m_FollowSpeed > 0)
                    m_HUDRoot.transform.position = Vector3.Lerp(
                        m_HUDRoot.transform.position, targetPos, Time.deltaTime * m_FollowSpeed);
                else
                    m_HUDRoot.transform.position = targetPos;
            }

            // Face the camera
            Vector3 lookDir = m_HUDRoot.transform.position - m_CameraTransform.position;
            if (lookDir.sqrMagnitude > 0.01f)
                m_HUDRoot.transform.rotation = Quaternion.LookRotation(lookDir);
        }

        private void OnVotingStarted()
        {
            Debug.Log("[VOTE_DEBUG] Voting started - showing HUD");
            if (m_HUDRoot != null)
                m_HUDRoot.SetActive(true);

            EnsureLayoutComponents();
            UpdateDisplay();
            SetVoteStatus("Point at a bar to vote!");
        }

        private void OnVotingEnded()
        {
            Debug.Log("[VOTE_DEBUG] Voting ended");
            SetVoteStatus("Voting ended");
            Invoke(nameof(HideHUD), 3f);
        }

        private void HideHUD()
        {
            if (m_HUDRoot != null)
                m_HUDRoot.SetActive(false);
        }

        private void OnQuestionChanged(string question, string[] options)
        {
            Debug.Log($"[VOTE_DEBUG] Question changed: {question}");
            UpdateDisplay();
        }

        private void OnVoteCountsUpdated(int[] counts)
        {
            Debug.Log($"[VOTE_DEBUG] Vote counts updated: {string.Join(", ", counts)}");
            UpdateOptionCounts(counts);
        }

        private void UpdateDisplay()
        {
            if (VotingManager.Instance == null) return;

            // Update question
            if (m_QuestionText != null)
            {
                m_QuestionText.text = VotingManager.Instance.Question;
                Debug.Log($"[VOTE_DEBUG] Set question: {VotingManager.Instance.Question}");
            }

            // Update options - this recreates cards and sets initial counts
            string[] labels = VotingManager.Instance.GetOptionLabels();
            int[] counts = VotingManager.Instance.GetVoteCounts();
            Debug.Log($"[VOTE_DEBUG] UpdateDisplay: labels={labels.Length}, counts={counts.Length}");
            UpdateOptions(labels, counts);
        }

        private void UpdateOptions(string[] labels, int[] counts)
        {
            if (m_OptionsContainer == null) return;

            // Clear existing
            foreach (Transform child in m_OptionsContainer)
                Destroy(child.gameObject);

            m_OptionTexts = new TMP_Text[labels.Length];
            m_OptionProgressBars = new Image[labels.Length];
            
            int totalVotes = 0;
            foreach (int c in counts) totalVotes += c;
            if (totalVotes == 0) totalVotes = 1; // Prevent division by zero

            // Create color-coded option cards
            for (int i = 0; i < labels.Length; i++)
            {
                Color optionColor = i < m_OptionColors.Length ? m_OptionColors[i] : Color.gray;
                int count = i < counts.Length ? counts[i] : 0;
                float percentage = (float)count / totalVotes;
                
                // === Option Card Container ===
                GameObject cardGO = new GameObject($"OptionCard_{i}");
                cardGO.transform.SetParent(m_OptionsContainer, false);
                var cardRT = cardGO.AddComponent<RectTransform>();
                cardRT.sizeDelta = new Vector2(320, 45); // Increased width
                
                // Card background
                var cardBg = cardGO.AddComponent<Image>();
                cardBg.color = new Color(0.15f, 0.15f, 0.15f, 0.95f);
                
                // Add horizontal layout
                var layout = cardGO.AddComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 5, 5);
                layout.spacing = 10;
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = true; // Let layout group control widths
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
                
                // === Color indicator dot ===
                GameObject dotGO = new GameObject("ColorDot");
                dotGO.transform.SetParent(cardGO.transform, false);
                var dotRT = dotGO.AddComponent<RectTransform>();
                dotRT.sizeDelta = new Vector2(12, 12);
                var dotLe = dotGO.AddComponent<LayoutElement>();
                dotLe.minWidth = 12;
                dotLe.preferredWidth = 12;
                var dotImg = dotGO.AddComponent<Image>();
                dotImg.color = optionColor;
                
                // === Label text ===
                GameObject labelGO = new GameObject("Label");
                labelGO.transform.SetParent(cardGO.transform, false);
                var labelTmp = labelGO.AddComponent<TextMeshProUGUI>();
                labelTmp.text = labels[i];
                labelTmp.fontSize = 16;
                labelTmp.fontStyle = FontStyles.Bold;
                labelTmp.color = Color.white;
                labelTmp.alignment = TextAlignmentOptions.Left;
                labelTmp.enableWordWrapping = false;
                labelTmp.overflowMode = TextOverflowModes.Ellipsis;
                
                var labelLe = labelGO.AddComponent<LayoutElement>();
                labelLe.minWidth = 80;
                labelLe.preferredWidth = 100;
                labelLe.flexibleWidth = 0;
                
                // === Progress bar background (FLEXIBLE) ===
                GameObject barBgGO = new GameObject("ProgressBg");
                barBgGO.transform.SetParent(cardGO.transform, false);
                var barBgImg = barBgGO.AddComponent<Image>();
                barBgImg.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
                
                var barLe = barBgGO.AddComponent<LayoutElement>();
                barLe.minWidth = 40;
                barLe.preferredWidth = 100;
                barLe.flexibleWidth = 1; // TAKES REMAINING SPACE
                
                // === Progress bar fill ===
                GameObject barFillGO = new GameObject("ProgressFill");
                barFillGO.transform.SetParent(barBgGO.transform, false);
                var barFillRT = barFillGO.AddComponent<RectTransform>();
                barFillRT.anchorMin = Vector2.zero;
                barFillRT.anchorMax = new Vector2(percentage, 1f);
                barFillRT.offsetMin = Vector2.zero;
                barFillRT.offsetMax = Vector2.zero;
                var barFillImg = barFillGO.AddComponent<Image>();
                barFillImg.color = optionColor;
                m_OptionProgressBars[i] = barFillImg;
                
                // === Vote count text ===
                GameObject countGO = new GameObject("Count");
                countGO.transform.SetParent(cardGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                countTmp.text = count.ToString();
                countTmp.fontSize = 18;
                countTmp.fontStyle = FontStyles.Bold;
                countTmp.color = Color.white;
                countTmp.alignment = TextAlignmentOptions.Right;
                
                var countLe = countGO.AddComponent<LayoutElement>();
                countLe.minWidth = 30;
                countLe.preferredWidth = 35;
                m_OptionTexts[i] = countTmp;
            }
}

        private void UpdateOptionCounts(int[] counts)
        {
            Debug.Log($"[VOTE_DEBUG] UpdateOptionCounts called with [{string.Join(", ", counts)}], OptionTexts: {m_OptionTexts?.Length ?? 0}");
            if (m_OptionsContainer == null) return;
            
            int totalVotes = 0;
            foreach (int c in counts) totalVotes += c;
            if (totalVotes == 0) totalVotes = 1;

            // Update cached text refs
            if (m_OptionTexts != null && m_OptionTexts.Length > 0)
            {
                for (int i = 0; i < m_OptionTexts.Length && i < counts.Length; i++)
                {
                    if (m_OptionTexts[i] != null)
                    {
                        m_OptionTexts[i].text = counts[i].ToString();
                    }
                    
                    // Update progress bar
                    if (m_OptionProgressBars != null && i < m_OptionProgressBars.Length && m_OptionProgressBars[i] != null)
                    {
                        float percentage = (float)counts[i] / totalVotes;
                        var rt = m_OptionProgressBars[i].rectTransform;
                        rt.anchorMax = new Vector2(percentage, 1f);
                    }
                }
            }
        }

        private void SetVoteStatus(string status)
        {
            if (m_VoteStatusText != null)
                m_VoteStatusText.text = status;
        }

        /// <summary>
        /// Called when local player submits a vote.
        /// </summary>
        public void OnLocalVoteSubmitted(int optionIndex)
        {
            var labels = VotingManager.Instance?.GetOptionLabels() ?? new string[0];
            string label = optionIndex < labels.Length ? labels[optionIndex] : ((char)('A' + optionIndex)).ToString();
            Color optionColor = optionIndex < m_OptionColors.Length ? m_OptionColors[optionIndex] : Color.white;
            
            if (m_VoteStatusText != null)
            {
                m_VoteStatusText.text = $"✓ You voted: {label}";
                m_VoteStatusText.color = optionColor;
            }
        }
    }
}
