using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;

namespace XRMultiplayer
{
    /// <summary>
    /// Runtime UI controller for Podium Mode.
    /// - Dynamically adds a "Podium" button to the Quick Menu for the host only.
    /// - Clicking it directly toggles Podium Mode (no panel needed).
    /// - Dynamically adds a "Raise Hand" button for non-host players when Podium Mode is active.
    /// - Shows a floating raised-hands HUD list for the host during Podium Mode.
    /// </summary>
    public class PodiumModeUI : MonoBehaviour
    {
        private const string SEGMENT_PODIUM = "Podium";
        private const string SEGMENT_RAISE_HAND = "Raise Hand";

        [Header("Raised Hands HUD (Host)")]
        [Tooltip("World-space canvas that shows the raised-hands list. Assign via 'VRMP/Setup Podium Mode'; if left null the host HUD is simply not shown.")]
        [SerializeField] private GameObject m_RaisedHandsHUD;
        [Tooltip("Container the per-player hand entries are parented to. Should contain an 'EmptyText' placeholder child.")]
        [SerializeField] private Transform m_RaisedHandsListContainer;
        [SerializeField] private GameObject m_RaisedHandEntryPrefab;

        [Header("Colors")]
        [SerializeField] private Color m_HandRaisedColor = new Color(1f, 0.85f, 0.2f);

        [Header("Debug")]
        [SerializeField] private bool m_DebugLogs = true;

        private readonly Dictionary<ulong, GameObject> m_HandEntries = new();
        private bool m_IsHost;
        private bool m_SegmentsRegistered;
        private bool m_EventsSubscribed;

        private void Update()
        {
            // 1. Subscribe to PodiumManager events once it's available
            if (!m_EventsSubscribed && PodiumManager.Instance != null)
            {
                SubscribeToEvents();
                m_EventsSubscribed = true;
            }

            // 2. Register Quick Menu segments once in session
            if (!m_SegmentsRegistered && QuickMenuManager.Instance != null && IsInSession() && XRINetworkPlayer.LocalPlayer != null)
            {
                RegisterSegments();
                m_SegmentsRegistered = true;
            }

            // 3. Update HUD position to follow camera
            if (m_RaisedHandsHUD != null && m_RaisedHandsHUD.activeSelf && Camera.main != null)
            {
                UpdateHUDPosition();
            }
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
            RemoveAllSegments();
        }

        private void OnDestroy()
        {
            UnsubscribeFromEvents();
            RemoveAllSegments();
        }

        #region Event Subscription

        private void SubscribeToEvents()
        {
            if (PodiumManager.Instance == null) return;

            PodiumManager.Instance.OnPodiumStateChanged += OnPodiumStateChanged;
            PodiumManager.Instance.OnPlayerRaisedHand += OnPlayerRaisedHand;
            PodiumManager.Instance.OnPlayerLoweredHand += OnPlayerLoweredHand;
            Log("Subscribed to PodiumManager events.");

            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted += OnSessionOwnerChanged;
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (PodiumManager.Instance != null)
            {
                PodiumManager.Instance.OnPodiumStateChanged -= OnPodiumStateChanged;
                PodiumManager.Instance.OnPlayerRaisedHand -= OnPlayerRaisedHand;
                PodiumManager.Instance.OnPlayerLoweredHand -= OnPlayerLoweredHand;
            }

            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted -= OnSessionOwnerChanged;
            }

            m_EventsSubscribed = false;
        }

        #endregion

        #region Quick Menu Integration

        private void RegisterSegments()
        {
            RefreshHostCache();
            Log($"RegisterSegments: IsHost={m_IsHost}");

            // Host gets the Podium toggle button
            if (m_IsHost && !QuickMenuManager.Instance.HasSegment(SEGMENT_PODIUM))
            {
                QuickMenuManager.Instance.AddSegment(SEGMENT_PODIUM, null, null, OnPodiumSegmentClicked);
                Log("Added 'Podium' segment to Quick Menu for host.");
            }

            // Non-host gets Raise Hand only when podium is active
            UpdateRaiseHandSegment();
        }

        private void RemoveAllSegments()
        {
            if (QuickMenuManager.Instance == null) return;

            if (QuickMenuManager.Instance.HasSegment(SEGMENT_PODIUM))
                QuickMenuManager.Instance.RemoveSegment(SEGMENT_PODIUM);

            if (QuickMenuManager.Instance.HasSegment(SEGMENT_RAISE_HAND))
                QuickMenuManager.Instance.RemoveSegment(SEGMENT_RAISE_HAND);

            m_SegmentsRegistered = false;
        }

        private void OnPodiumSegmentClicked()
        {
            Log("Podium segment clicked!");

            if (PodiumManager.Instance == null)
            {
                Log("ERROR: PodiumManager.Instance is null!");
                return;
            }

            if (PodiumManager.Instance.IsPodiumActive)
            {
                Log("Deactivating Podium Mode...");
                PodiumManager.Instance.DeactivatePodiumMode();
            }
            else
            {
                Log("Activating Podium Mode...");
                PodiumManager.Instance.ActivatePodiumMode();
            }
        }

        private void OnRaiseHandSegmentClicked()
        {
            if (PodiumManager.Instance == null) return;
            PodiumManager.Instance.ToggleRaiseHand();

            // Update the label to reflect state
            if (QuickMenuManager.Instance != null)
            {
                bool raised = PodiumManager.Instance.IsLocalHandRaised;
                QuickMenuManager.Instance.UpdateSegmentLabel(
                    SEGMENT_RAISE_HAND,
                    raised ? "\u270B Raised" : "\u270B Raise Hand");
            }
        }

        private void UpdateRaiseHandSegment()
        {
            if (QuickMenuManager.Instance == null) return;

            bool podiumActive = PodiumManager.Instance != null && PodiumManager.Instance.IsPodiumActive;
            bool isHost = XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;

            if (!isHost && podiumActive)
            {
                if (!QuickMenuManager.Instance.HasSegment(SEGMENT_RAISE_HAND))
                {
                    QuickMenuManager.Instance.AddSegment(SEGMENT_RAISE_HAND, null, null, OnRaiseHandSegmentClicked);
                    Log("Added 'Raise Hand' segment for non-host.");
                }
            }
            else
            {
                if (QuickMenuManager.Instance.HasSegment(SEGMENT_RAISE_HAND))
                {
                    QuickMenuManager.Instance.RemoveSegment(SEGMENT_RAISE_HAND);
                    Log("Removed 'Raise Hand' segment.");
                }
            }
        }

        private void UpdatePodiumSegmentLabel()
        {
            if (QuickMenuManager.Instance == null) return;
            if (!QuickMenuManager.Instance.HasSegment(SEGMENT_PODIUM)) return;

            bool active = PodiumManager.Instance != null && PodiumManager.Instance.IsPodiumActive;
            QuickMenuManager.Instance.UpdateSegmentLabel(
                SEGMENT_PODIUM,
                active ? "End Podium" : "Podium");
        }

        #endregion

        #region Event Handlers

        private void OnPodiumStateChanged(bool active)
        {
            RefreshHostCache();
            Log($"OnPodiumStateChanged: active={active}, IsHost={m_IsHost}");
            UpdatePodiumSegmentLabel();
            UpdateRaiseHandSegment();
            UpdateRaisedHandsHUD(active);

            if (!active)
            {
                ClearAllHandEntries();
            }
            else if (IsLocalHost() && PodiumManager.Instance != null)
            {
                ClearAllHandEntries();
                foreach (ulong clientId in PodiumManager.Instance.RaisedHands)
                    AddHandEntry(clientId);
            }
        }

        private void OnPlayerRaisedHand(ulong clientId)
        {
            if (!IsLocalHost()) return;
            AddHandEntry(clientId);
        }

        private void OnPlayerLoweredHand(ulong clientId)
        {
            RemoveHandEntry(clientId);
        }

        private void OnSessionOwnerChanged(ulong newOwnerId)
        {
            Log($"Session owner changed to {newOwnerId}, re-registering segments.");
            RemoveAllSegments();
            m_SegmentsRegistered = false; // Will re-register in Update
        }

        #endregion

        #region Raised Hands HUD (Host)

        private void UpdateRaisedHandsHUD(bool podiumActive)
        {
            if (m_RaisedHandsHUD != null)
                m_RaisedHandsHUD.SetActive(IsLocalHost() && podiumActive);
        }

        private void UpdateHUDPosition()
        {
            if (m_RaisedHandsHUD == null || Camera.main == null) return;

            var cam = Camera.main.transform;
            Vector3 forward = cam.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = cam.forward;
            forward.Normalize();

            Vector3 right = cam.right;
            right.y = 0;
            right.Normalize();

            Vector3 targetPos = cam.position + forward * 1.5f + right * 0.6f + Vector3.up * -0.1f;
            m_RaisedHandsHUD.transform.position = Vector3.Lerp(
                m_RaisedHandsHUD.transform.position, targetPos, Time.deltaTime * 8f);

            Vector3 lookDir = m_RaisedHandsHUD.transform.position - cam.position;
            if (lookDir.sqrMagnitude > 0.01f)
                m_RaisedHandsHUD.transform.rotation = Quaternion.LookRotation(lookDir);
        }

        private void AddHandEntry(ulong clientId)
        {
            if (m_HandEntries.ContainsKey(clientId)) return;
            if (m_RaisedHandsListContainer == null) return;

            string playerName = GetPlayerName(clientId);

            if (m_RaisedHandEntryPrefab != null)
            {
                var entry = Instantiate(m_RaisedHandEntryPrefab, m_RaisedHandsListContainer);
                entry.SetActive(true);

                var texts = entry.GetComponentsInChildren<TMP_Text>(true);
                if (texts.Length > 0)
                    texts[0].text = $"\u270B {playerName}";

                var button = entry.GetComponentInChildren<Button>(true);
                if (button != null)
                {
                    ulong capturedId = clientId;
                    button.onClick.AddListener(() => OnUnmuteClicked(capturedId));
                }

                m_HandEntries[clientId] = entry;
            }

            UpdateEmptyPlaceholder();
        }

        private void RemoveHandEntry(ulong clientId)
        {
            if (m_HandEntries.TryGetValue(clientId, out var entry))
            {
                if (entry != null) Destroy(entry);
                m_HandEntries.Remove(clientId);
            }

            UpdateEmptyPlaceholder();
        }

        private void ClearAllHandEntries()
        {
            foreach (var kvp in m_HandEntries)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            m_HandEntries.Clear();

            UpdateEmptyPlaceholder();
        }

        /// <summary>Shows the "No hands raised yet..." placeholder only while the list is empty.</summary>
        private void UpdateEmptyPlaceholder()
        {
            if (m_RaisedHandsListContainer == null) return;

            var placeholder = m_RaisedHandsListContainer.Find("EmptyText");
            if (placeholder != null)
                placeholder.gameObject.SetActive(m_HandEntries.Count == 0);
        }

        private void OnUnmuteClicked(ulong clientId)
        {
            if (PodiumManager.Instance == null) return;

            if (PodiumManager.Instance.IsPlayerUnmutedByHost(clientId))
            {
                PodiumManager.Instance.RemutePlayer(clientId);
            }
            else
            {
                PodiumManager.Instance.UnmutePlayer(clientId);
            }
        }

        #endregion

        #region Helpers

        private bool IsInSession()
        {
            return NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
        }

        private bool IsLocalHost()
        {
            return XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
        }

        private void RefreshHostCache()
        {
            m_IsHost = IsLocalHost();
        }

        private string GetPlayerName(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out var player))
            {
                return player.displayName;
            }
            return $"Player_{clientId}";
        }

        private void Log(string msg)
        {
            if (m_DebugLogs)
                Debug.Log($"<color=#FF9800>[PodiumModeUI]</color> {msg}");
        }

        #endregion
    }
}
