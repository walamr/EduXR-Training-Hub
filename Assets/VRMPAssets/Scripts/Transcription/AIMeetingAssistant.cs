using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Live in-world AI meeting assistant.
    ///
    /// Pulls the current meeting transcript from <see cref="TranscriptionManager"/> and asks
    /// <see cref="GeminiService"/> a question, then shows the answer on a world-space panel.
    /// Registers an "Ask AI" entry in the <see cref="QuickMenuManager"/> radial menu.
    ///
    /// Wire the preset buttons on the panel to <see cref="AskSummary"/>, <see cref="AskActionItems"/>,
    /// and <see cref="AskDecisions"/>, or call <see cref="Ask"/> with any custom question.
    /// </summary>
    public class AIMeetingAssistant : MonoBehaviour
    {
        const string k_DebugPrepend = "<color=#34A853>[AI Assistant]</color> ";

        [System.Serializable]
        public class StringEvent : UnityEvent<string> { }

        [Header("Quick Menu")]
        [SerializeField, Tooltip("Display name of the Quick Menu segment.")]
        string m_QuickMenuName = "Ask AI";

        [SerializeField, Tooltip("Optional icon for the Quick Menu segment.")]
        Sprite m_QuickMenuIcon;

        [SerializeField, Tooltip("World-space panel toggled by the Quick Menu entry. Should contain the preset buttons and the answer text.")]
        GameObject m_AssistantPanel;

        [Header("Answer Display")]
        [SerializeField, Tooltip("Text element that shows the assistant's answer.")]
        TMP_Text m_AnswerText;

        [SerializeField, Tooltip("Optional status/'thinking' text element.")]
        TMP_Text m_StatusText;

        [Header("Preset Questions")]
        [SerializeField, TextArea]
        string m_SummaryQuestion = "Give a concise summary of the meeting so far.";

        [SerializeField, TextArea]
        string m_ActionItemsQuestion = "List the concrete action items and who owns them, based on the discussion.";

        [SerializeField, TextArea]
        string m_DecisionsQuestion = "What key decisions have been made so far?";

        [Header("Events")]
        public StringEvent OnAnswer;
        public UnityEvent OnThinking;

        const float k_AnswerTimeoutSeconds = 30f;

        bool m_Subscribed;
        bool m_OwnerSubscribed;
        bool m_RegisteredWithMenu;
        // Guards the shared GeminiService.OnAnswerResult/OnError so we only react to our own requests.
        bool m_Awaiting;
        Coroutine m_TimeoutRoutine;

        void OnEnable()
        {
            SubscribeToGemini();
            StartCoroutine(RegisterWithQuickMenuWhenReady());
        }

        void OnDisable()
        {
            UnsubscribeFromGemini();

            if (m_OwnerSubscribed && XRINetworkGameManager.Instance != null)
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted -= OnSessionOwnerChanged;
            m_OwnerSubscribed = false;

            if (m_RegisteredWithMenu && QuickMenuManager.Instance != null &&
                QuickMenuManager.Instance.HasSegment(m_QuickMenuName))
            {
                QuickMenuManager.Instance.RemoveSegment(m_QuickMenuName);
            }
            m_RegisteredWithMenu = false;

            m_Awaiting = false;
            if (m_TimeoutRoutine != null) { StopCoroutine(m_TimeoutRoutine); m_TimeoutRoutine = null; }
        }

        void SubscribeToGemini()
        {
            if (m_Subscribed || GeminiService.Instance == null) return;
            GeminiService.Instance.OnAnswerResult += HandleAnswer;
            GeminiService.Instance.OnError += HandleError;
            m_Subscribed = true;
        }

        void UnsubscribeFromGemini()
        {
            if (!m_Subscribed || GeminiService.Instance == null) return;
            GeminiService.Instance.OnAnswerResult -= HandleAnswer;
            GeminiService.Instance.OnError -= HandleError;
            m_Subscribed = false;
        }

        IEnumerator RegisterWithQuickMenuWhenReady()
        {
            // GeminiService / QuickMenuManager may finish spawning a frame or two after us.
            float timeout = 10f;
            while ((QuickMenuManager.Instance == null || GeminiService.Instance == null) && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            SubscribeToGemini();

            // Re-evaluate segment visibility whenever the session owner (host) changes.
            if (XRINetworkGameManager.Instance != null && !m_OwnerSubscribed)
            {
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted += OnSessionOwnerChanged;
                m_OwnerSubscribed = true;
            }

            UpdateSegmentForHostStatus();
        }

        void OnSessionOwnerChanged(ulong newOwnerId) => UpdateSegmentForHostStatus();

        /// <summary>
        /// The assistant is host-only (it spends shared Gemini quota), so the Quick Menu segment
        /// is shown only to the session owner and removed from everyone else.
        /// </summary>
        void UpdateSegmentForHostStatus()
        {
            if (QuickMenuManager.Instance == null) return;

            bool host = IsLocalHost();
            bool has = QuickMenuManager.Instance.HasSegment(m_QuickMenuName);

            if (host && !has)
            {
                QuickMenuManager.Instance.AddSegment(m_QuickMenuName, m_QuickMenuIcon, m_AssistantPanel);
                m_RegisteredWithMenu = true;
            }
            else if (!host && has)
            {
                QuickMenuManager.Instance.RemoveSegment(m_QuickMenuName);
                m_RegisteredWithMenu = false;
                if (m_AssistantPanel != null) m_AssistantPanel.SetActive(false);
            }
        }

        static bool IsLocalHost()
        {
            return XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
        }

        // ---- Public API for UI buttons ----

        public void AskSummary() => Ask(m_SummaryQuestion);
        public void AskActionItems() => Ask(m_ActionItemsQuestion);
        public void AskDecisions() => Ask(m_DecisionsQuestion);

        /// <summary>
        /// Asks the assistant a free-form question grounded in the live transcript.
        /// </summary>
        public void Ask(string question)
        {
            // Host-only: every client carries the same API key, so a non-host asking would spend
            // the shared quota from their own device. Catch Me Up routes through the host instead.
            if (!IsLocalHost())
            {
                SetStatus("Only the host can use the AI assistant.");
                return;
            }

            if (GeminiService.Instance == null)
            {
                SetStatus("AI service unavailable.");
                return;
            }

            // The Gemini pipeline is single-flight and shared with transcription / Catch Me Up.
            if (m_Awaiting || GeminiService.Instance.IsProcessing)
            {
                SetStatus("AI is busy - one moment...");
                return;
            }

            string transcript = GetCurrentTranscript();
            m_Awaiting = true;
            SetStatus("Thinking...");
            OnThinking?.Invoke();
            GeminiService.Instance.AskQuestion(transcript, question);

            if (m_TimeoutRoutine != null) StopCoroutine(m_TimeoutRoutine);
            m_TimeoutRoutine = StartCoroutine(AnswerTimeout());
        }

        IEnumerator AnswerTimeout()
        {
            yield return new WaitForSeconds(k_AnswerTimeoutSeconds);
            if (m_Awaiting)
            {
                m_Awaiting = false;
                SetStatus("The assistant timed out - please try again.");
            }
            m_TimeoutRoutine = null;
        }

        string GetCurrentTranscript()
        {
            TranscriptionManager manager = TranscriptionManager.Instance;
            if (manager == null) return string.Empty;

            MeetingTranscript transcript = manager.GetCurrentTranscript();
            return transcript != null ? transcript.GetFullText() : string.Empty;
        }

        void HandleAnswer(string answer)
        {
            // Shared event: ignore answers for requests we didn't make (e.g. Catch Me Up's).
            if (!m_Awaiting) return;
            m_Awaiting = false;
            if (m_TimeoutRoutine != null) { StopCoroutine(m_TimeoutRoutine); m_TimeoutRoutine = null; }
            SetStatus(string.Empty);

            if (m_AnswerText != null)
                m_AnswerText.text = answer;

            if (m_AssistantPanel != null && !m_AssistantPanel.activeSelf)
                m_AssistantPanel.SetActive(true);

            OnAnswer?.Invoke(answer);
            Utils.Log($"{k_DebugPrepend}{answer}");
        }

        void HandleError(string error)
        {
            // OnError is shared with the transcription pipeline; only react to our own requests.
            if (!m_Awaiting) return;
            m_Awaiting = false;
            if (m_TimeoutRoutine != null) { StopCoroutine(m_TimeoutRoutine); m_TimeoutRoutine = null; }
            SetStatus(error);
        }

        void SetStatus(string status)
        {
            if (m_StatusText != null)
                m_StatusText.text = status;
        }
    }
}
