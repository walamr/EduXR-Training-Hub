using System.Collections;
using TMPro;
using UnityEngine;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Client-side UI for the "Catch Me Up" feature.
    /// - Detects (on a non-host) that a meeting is already in progress and nudges the player once.
    /// - Adds a "Catch Me Up" Quick Menu segment that requests an AI recap and opens the panel.
    /// - Displays the recap returned by <see cref="CatchMeUpService"/> on a world-space panel.
    /// </summary>
    public class CatchMeUpUI : MonoBehaviour
    {
        const string k_DebugPrepend = "<color=#34A853>[CatchMeUp UI]</color> ";
        const string k_Segment = "Catch Me Up";

        [Header("Quick Menu")]
        [SerializeField, Tooltip("Optional icon for the Quick Menu segment.")]
        Sprite m_QuickMenuIcon;

        [Header("Recap Panel")]
        [SerializeField, Tooltip("World-space panel toggled by the Quick Menu entry. Assign via 'VRMP/Setup Catch Me Up'.")]
        GameObject m_Panel;

        [SerializeField, Tooltip("Text element that shows the recap.")]
        TMP_Text m_RecapText;

        [SerializeField, Tooltip("Optional status/'thinking' text element.")]
        TMP_Text m_StatusText;

        [Header("Behaviour")]
        [SerializeField, Tooltip("Show a one-time HUD nudge when the player notices a meeting in progress.")]
        bool m_AutoNudge = true;

        bool m_SegmentRegistered;
        bool m_Nudged;
        bool m_Subscribed;
        bool m_ServiceSubscribed;

        void OnEnable()
        {
            StartCoroutine(SetupWhenReady());
        }

        void OnDisable()
        {
            if (TranscriptionManager.Instance != null && m_Subscribed)
                TranscriptionManager.Instance.OnRemoteTranscriptReceived -= OnRemoteTranscript;
            m_Subscribed = false;

            if (CatchMeUpService.Instance != null && m_ServiceSubscribed)
                CatchMeUpService.Instance.OnRecapReceived -= OnRecapReceived;
            m_ServiceSubscribed = false;

            RemoveSegment();
        }

        IEnumerator SetupWhenReady()
        {
            float timeout = 15f;
            while ((TranscriptionManager.Instance == null || CatchMeUpService.Instance == null) && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (TranscriptionManager.Instance != null && !m_Subscribed)
            {
                TranscriptionManager.Instance.OnRemoteTranscriptReceived += OnRemoteTranscript;
                m_Subscribed = true;

                // Already-in-progress meeting we joined a little before subscribing.
                if (TranscriptionManager.Instance.HasRemoteTranscriptActivity)
                    OnRemoteTranscript();
            }

            if (CatchMeUpService.Instance != null && !m_ServiceSubscribed)
            {
                CatchMeUpService.Instance.OnRecapReceived += OnRecapReceived;
                m_ServiceSubscribed = true;
            }
        }

        void OnRemoteTranscript()
        {
            // Host doesn't need to catch up on its own meeting.
            if (IsLocalHost()) return;

            RegisterSegment();

            if (m_AutoNudge && !m_Nudged)
            {
                m_Nudged = true;
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(
                        "<b>Meeting in progress</b> - open the menu and tap 'Catch Me Up' for a recap.", 5f);
            }
        }

        // ── Quick Menu ──

        void RegisterSegment()
        {
            if (m_SegmentRegistered || QuickMenuManager.Instance == null) return;
            if (QuickMenuManager.Instance.HasSegment(k_Segment)) return;

            QuickMenuManager.Instance.AddSegment(k_Segment, m_QuickMenuIcon, m_Panel, OnSegmentClicked);
            m_SegmentRegistered = true;
            Log("Registered 'Catch Me Up' segment.");
        }

        void RemoveSegment()
        {
            if (QuickMenuManager.Instance != null && QuickMenuManager.Instance.HasSegment(k_Segment))
                QuickMenuManager.Instance.RemoveSegment(k_Segment);
            m_SegmentRegistered = false;
        }

        void OnSegmentClicked()
        {
            if (CatchMeUpService.Instance == null)
            {
                SetStatus("Catch Me Up is unavailable.");
                return;
            }

            SetStatus("Summarizing what you missed...");
            if (m_RecapText != null) m_RecapText.text = string.Empty;
            if (m_Panel != null && !m_Panel.activeSelf) m_Panel.SetActive(true);

            // 0 = whole meeting (true latecomer). Precise "since I left" can pass a timestamp later.
            CatchMeUpService.Instance.RequestCatchMeUp(0);
        }

        // ── Recap display ──

        void OnRecapReceived(string recap)
        {
            SetStatus(string.Empty);

            if (m_RecapText != null)
                m_RecapText.text = IsRtl(recap) ? RtlTextUtility.PrepareForRtlRendering(recap) : recap;

            if (m_Panel != null && !m_Panel.activeSelf)
                m_Panel.SetActive(true);

            Log($"Recap: {recap}");
        }

        void SetStatus(string status)
        {
            if (m_StatusText != null) m_StatusText.text = status;
        }

        static bool IsRtl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0590 && c <= 0x05FF))
                    return true;
            return false;
        }

        static bool IsLocalHost()
        {
            return XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
        }

        void Log(string msg)
        {
            Debug.Log($"{k_DebugPrepend}{msg}");
        }
    }
}
