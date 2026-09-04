using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

using TMPro;

namespace XRMultiplayer
{
    /// <summary>
    /// Voting mode types.
    /// </summary>
    public enum VoteMode
    {
        None,
        MultipleChoice,  // Mode 1: Click on bar chart
        HandRaise        // Mode 2: Left hand = A, Right hand = B
    }

    /// <summary>
    /// Quick poll preset types.
    /// </summary>
    public enum QuickPollPreset
    {
        Custom,
        YesNo,
        AgreeDisagree,
        Rating1to5
    }

    /// <summary>
    /// Serializable vote data for network RPCs.
    /// </summary>
    public struct VoteData : INetworkSerializable
    {
        public VoteMode Mode;
        public FixedString128Bytes Question;
        public FixedString64Bytes Option0;
        public FixedString64Bytes Option1;
        public FixedString64Bytes Option2;
        public FixedString64Bytes Option3;
        public FixedString64Bytes Option4;
        public FixedString64Bytes Option5;
        public int OptionCount;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Mode);
            serializer.SerializeValue(ref Question);
            serializer.SerializeValue(ref OptionCount);
            serializer.SerializeValue(ref Option0);
            serializer.SerializeValue(ref Option1);
            if (OptionCount > 2) serializer.SerializeValue(ref Option2);
            if (OptionCount > 3) serializer.SerializeValue(ref Option3);
            if (OptionCount > 4) serializer.SerializeValue(ref Option4);
            if (OptionCount > 5) serializer.SerializeValue(ref Option5);
        }

        public static VoteData Create(VoteMode mode, string question, string[] options)
        {
            var data = new VoteData
            {
                Mode = mode,
                Question = question,
                OptionCount = Mathf.Clamp(options.Length, 2, 6)
            };
            if (options.Length > 0) data.Option0 = options[0];
            if (options.Length > 1) data.Option1 = options[1];
            if (options.Length > 2) data.Option2 = options[2];
            if (options.Length > 3) data.Option3 = options[3];
            if (options.Length > 4) data.Option4 = options[4];
            if (options.Length > 5) data.Option5 = options[5];
            return data;
        }

        public string[] GetOptions()
        {
            var list = new List<string>();
            if (OptionCount > 0) list.Add(Option0.ToString());
            if (OptionCount > 1) list.Add(Option1.ToString());
            if (OptionCount > 2) list.Add(Option2.ToString());
            if (OptionCount > 3) list.Add(Option3.ToString());
            if (OptionCount > 4) list.Add(Option4.ToString());
            if (OptionCount > 5) list.Add(Option5.ToString());
            return list.ToArray();
        }
    }

    /// <summary>
    /// Manages the Voting Session, Network State, and 3D Chart Visualization.
    /// Supports custom questions with options, interactable bar chart voting.
    /// </summary>
    public class VotingManager : NetworkBehaviour
    {
        public static VotingManager Instance { get; private set; }

        #region Voting Settings

        [Header("=== VOTING CONFIG ===")]
        [SerializeField] private int m_MaxOptions = 6;
        [SerializeField] private Transform m_TableTransform;
        [SerializeField] private float m_HandRaiseThreshold = 0.2f;

        #endregion

        #region UI References

        [Header("=== HOST UI ===")]
        [SerializeField] private GameObject m_HostPanel;
        [SerializeField] private Button m_StartVoteButton;
        [SerializeField] private Button m_EndVoteButton;
        [SerializeField] private TMP_InputField m_QuestionInput;
        [SerializeField] private List<TMP_InputField> m_OptionInputs;
        [SerializeField] private Transform m_OptionsContainer;
        [SerializeField] private Button m_AddOptionButton;
        [SerializeField] private Button m_RemoveOptionButton;
        
        [Header("=== TAB UI ===")]
        [SerializeField] private Button m_MultiChoiceTabButton;
        [SerializeField] private Button m_HandRaiseTabButton;
        [SerializeField] private GameObject m_MultiChoiceTabContent;
        [SerializeField] private GameObject m_HandRaiseTabContent;
        
        [Header("=== QUICK POLLS ===")]
        [SerializeField] private Button m_YesNoButton;
        [SerializeField] private Button m_AgreeDisagreeButton;
        [SerializeField] private Button m_Rating1to5Button;

        [Header("=== CLIENT UI ===")]
        [SerializeField] private TMP_Text m_StatusText;
        [SerializeField] private GameObject m_HandRaiseInstructions;

        #endregion

        #region Chart Settings

        [Header("=== 3D CHART ===")]
        [SerializeField] private Transform m_ChartOrigin;
        [SerializeField] private GameObject m_BarPrefab;
        [SerializeField] private float m_BarSpacing = 0.15f;
        [SerializeField] private float m_MaxBarHeight = 0.5f;
        [SerializeField] private float m_BarWidth = 0.1f;
        [SerializeField] private float m_ChartAnimationSpeed = 5f;
        [SerializeField] private Color[] m_BarColors = new Color[]
        {
            new Color(0.2f, 0.6f, 1f),   // Blue
            new Color(1f, 0.4f, 0.4f),   // Red
            new Color(0.4f, 1f, 0.4f),   // Green
            new Color(1f, 1f, 0.4f),     // Yellow
            new Color(1f, 0.6f, 0.2f),   // Orange
            new Color(0.8f, 0.4f, 1f)    // Purple
        };
        [SerializeField] private Color m_BarHoverColor = new Color(1f, 1f, 1f, 0.8f);
        [SerializeField] private Color m_BarSelectedColor = new Color(0.2f, 1f, 0.4f, 1f);

        #endregion

        #region Network Variables

        private NetworkVariable<bool> m_IsVotingActive = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<VoteMode> m_CurrentMode = new NetworkVariable<VoteMode>(
            VoteMode.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<int> m_OptionCount = new NetworkVariable<int>(
            2, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<FixedString128Bytes> m_Question = new NetworkVariable<FixedString128Bytes>(
            "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkList<FixedString64Bytes> m_OptionLabels;
        private NetworkList<int> m_VoteCounts;

        #endregion

        #region Private State

        private HashSet<ulong> m_PlayersVoted = new HashSet<ulong>();
        private Dictionary<ulong, int> m_PlayerVoteIndex = new Dictionary<ulong, int>(); // Track vote per player for re-voting
        private Dictionary<ulong, int> m_HandRaiseVotes = new Dictionary<ulong, int>();
        private List<Transform> m_Bars = new List<Transform>();
        private List<float> m_TargetHeights = new List<float>();
        private int m_MaxVotes = 1;
        private VoteMode m_SelectedMode = VoteMode.MultipleChoice;
        private int? m_LocalVote = null;
        private List<Renderer> m_BarRenderers = new List<Renderer>();
        private List<Color> m_OriginalBarColors = new List<Color>();
        private List<TextMeshPro> m_BarLabelTexts = new List<TextMeshPro>(); // For dynamic label updates
        private List<TextMeshPro> m_BarCountTexts = new List<TextMeshPro>(); // For vote count display
        private int m_HoveredBarIndex = -1; // Currently hovered bar for fail-safe click detection
        private bool m_LastTriggerState = false; // For edge detection

        #endregion

        #region Events

        public event Action OnVotingStarted;
        public event Action OnVotingEnded;
        public event Action<int[]> OnVoteCountsUpdated;
        public event Action<string, string[]> OnQuestionChanged;

        #endregion

        #region Public Accessors

        public bool IsVotingActive => m_IsVotingActive.Value;
        public VoteMode CurrentMode => m_CurrentMode.Value;
        public int OptionCount => m_OptionCount.Value;
        public string Question => m_Question.Value.ToString();
        public Transform TableTransform => m_TableTransform;

        public string[] GetOptionLabels()
        {
            string[] labels = new string[m_OptionLabels.Count];
            for (int i = 0; i < m_OptionLabels.Count; i++)
                labels[i] = m_OptionLabels[i].ToString();
            return labels;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            m_VoteCounts = new NetworkList<int>(new List<int>(), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            m_OptionLabels = new NetworkList<FixedString64Bytes>(new List<FixedString64Bytes>(), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

            // UI button listeners
            if (m_StartVoteButton != null)
                m_StartVoteButton.onClick.AddListener(OnStartVoteClicked);
            if (m_EndVoteButton != null)
                m_EndVoteButton.onClick.AddListener(OnEndVoteClicked);
            
            // Tab buttons
            if (m_MultiChoiceTabButton != null)
                m_MultiChoiceTabButton.onClick.AddListener(() => SelectTab(VoteMode.MultipleChoice));
            if (m_HandRaiseTabButton != null)
                m_HandRaiseTabButton.onClick.AddListener(() => SelectTab(VoteMode.HandRaise));
            
            // Quick poll buttons
            if (m_YesNoButton != null)
                m_YesNoButton.onClick.AddListener(() => ApplyQuickPoll(QuickPollPreset.YesNo));
            if (m_AgreeDisagreeButton != null)
                m_AgreeDisagreeButton.onClick.AddListener(() => ApplyQuickPoll(QuickPollPreset.AgreeDisagree));
            if (m_Rating1to5Button != null)
                m_Rating1to5Button.onClick.AddListener(() => ApplyQuickPoll(QuickPollPreset.Rating1to5));
            
            // Add/Remove option buttons
            if (m_AddOptionButton != null)
                m_AddOptionButton.onClick.AddListener(OnAddOption);
            if (m_RemoveOptionButton != null)
                m_RemoveOptionButton.onClick.AddListener(OnRemoveOption);
            
            // Hide chart initially
            SetChartVisible(false);
            if (m_HostPanel != null) m_HostPanel.SetActive(false);

            FixHostPanelLayout();
            SelectTab(VoteMode.MultipleChoice);
            
            // Initialize visible options to 2
            UpdateVisibleOptionInputs(2);
        }

        /// <summary>Re-applies host panel layout (call when the Vote quick-menu panel is opened).</summary>
        public void RefreshHostPanelLayout() => FixHostPanelLayout();

        void FixHostPanelLayout()
        {
            if (m_HostPanel == null)
                return;

            Transform bottomButtons = m_StartVoteButton != null
                ? m_StartVoteButton.transform.parent
                : null;

            var hostRect = m_HostPanel.GetComponent<RectTransform>();
            VotingUILayoutUtility.ConfigureHostPanel(
                hostRect,
                m_MultiChoiceTabContent,
                m_HandRaiseTabContent,
                m_OptionsContainer,
                bottomButtons);

            VotingUILayoutUtility.RebuildHostPanel(hostRect);
        }

        void OnEnable()
        {
            if (m_HostPanel != null && m_HostPanel.activeInHierarchy)
                FixHostPanelLayout();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            m_IsVotingActive.OnValueChanged += OnVotingActiveChanged;
            m_VoteCounts.OnListChanged += OnVoteCountsListChanged;
            m_Question.OnValueChanged += OnQuestionValueChanged;
            m_OptionLabels.OnListChanged += OnOptionLabelsChanged;

            if (IsOwner && m_VoteCounts.Count == 0)
            {
                for (int i = 0; i < m_MaxOptions; i++)
                    m_VoteCounts.Add(0);
            }

            UpdateUIState();
            
            // LocalPlayer might not be available yet, so re-check UI state periodically
            InvokeRepeating(nameof(UpdateUIState), 0.5f, 1.0f);
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            CancelInvoke(nameof(UpdateUIState));
            m_IsVotingActive.OnValueChanged -= OnVotingActiveChanged;
            m_VoteCounts.OnListChanged -= OnVoteCountsListChanged;
            m_Question.OnValueChanged -= OnQuestionValueChanged;
            m_OptionLabels.OnListChanged -= OnOptionLabelsChanged;
        }

        private void Update()
        {
            // Mode 2: Check hand positions
            if (IsOwner && m_IsVotingActive.Value && m_CurrentMode.Value == VoteMode.HandRaise)
                UpdateHandRaiseVotes();

            // Animate chart bars
            AnimateChartBars();
            
            // Fail-safe: Manual trigger detection (like SittableChair)
            CheckManualTriggerInput();
        }
        
        /// <summary>
        /// Fail-safe manual trigger detection when XRI select events don't fire.
        /// </summary>
        private void CheckManualTriggerInput()
        {
            if (!m_IsVotingActive.Value || m_CurrentMode.Value != VoteMode.MultipleChoice)
                return;
            if (m_HoveredBarIndex < 0 || m_LocalVote.HasValue)
                return;
                
            bool triggerPressed = false;
            
            // Check XR controllers
            var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.Controller, devices);
            
            foreach (var device in devices)
            {
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool pressed) && pressed)
                {
                    triggerPressed = true;
                    break;
                }
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float value) && value > 0.5f)
                {
                    triggerPressed = true;
                    break;
                }
            }
            
            #if UNITY_EDITOR
            // Editor fallback: mouse click
            if (!triggerPressed)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null && mouse.leftButton.isPressed)
                    triggerPressed = true;
            }
            #endif
            
            // Edge detection: only trigger on press, not hold
            if (triggerPressed && !m_LastTriggerState)
            {
                Debug.Log($"[VOTE_DEBUG] Manual trigger detected for bar {m_HoveredBarIndex}");
                OnBarSelected(m_HoveredBarIndex);
            }
            
            m_LastTriggerState = triggerPressed;
        }

        #endregion

        #region Tab Management

        private void SelectTab(VoteMode mode)
        {
            m_SelectedMode = mode;
            
            if (m_MultiChoiceTabContent != null)
                m_MultiChoiceTabContent.SetActive(mode == VoteMode.MultipleChoice);
            if (m_HandRaiseTabContent != null)
                m_HandRaiseTabContent.SetActive(mode == VoteMode.HandRaise);
            
            // Visual feedback for tab buttons
            UpdateTabVisuals();

            if (mode == VoteMode.MultipleChoice && m_HostPanel != null && m_HostPanel.activeInHierarchy)
                FixHostPanelLayout();
        }

        private void UpdateTabVisuals()
        {
            if (m_MultiChoiceTabButton != null)
            {
                var colors = m_MultiChoiceTabButton.colors;
                colors.normalColor = m_SelectedMode == VoteMode.MultipleChoice 
                    ? new Color(0.125f, 0.588f, 0.953f) : new Color(0.18f, 0.18f, 0.18f);
                m_MultiChoiceTabButton.colors = colors;
            }
            if (m_HandRaiseTabButton != null)
            {
                var colors = m_HandRaiseTabButton.colors;
                colors.normalColor = m_SelectedMode == VoteMode.HandRaise 
                    ? new Color(0.125f, 0.588f, 0.953f) : new Color(0.18f, 0.18f, 0.18f);
                m_HandRaiseTabButton.colors = colors;
            }
        }

        #endregion

        #region Quick Poll Presets

        public void ApplyQuickPoll(QuickPollPreset preset)
        {
            switch (preset)
            {
                case QuickPollPreset.YesNo:
                    if (m_QuestionInput != null) m_QuestionInput.text = "Do you agree?";
                    SetOptionInputs(new[] { "Yes", "No" });
                    break;
                case QuickPollPreset.AgreeDisagree:
                    if (m_QuestionInput != null) m_QuestionInput.text = "What do you think?";
                    SetOptionInputs(new[] { "Agree", "Disagree", "Neutral" });
                    break;
                case QuickPollPreset.Rating1to5:
                    if (m_QuestionInput != null) m_QuestionInput.text = "Rate this (1-5):";
                    SetOptionInputs(new[] { "1", "2", "3", "4", "5" });
                    break;
            }
        }

        private void SetOptionInputs(string[] options)
        {
            if (m_OptionInputs == null) return;
            for (int i = 0; i < m_OptionInputs.Count; i++)
            {
                if (m_OptionInputs[i] != null)
                {
                    if (i < options.Length)
                    {
                        m_OptionInputs[i].text = options[i];
                        m_OptionInputs[i].gameObject.SetActive(true);
                    }
                    else
                    {
                        m_OptionInputs[i].text = "";
                        m_OptionInputs[i].gameObject.SetActive(false);
                    }
                }
            }
        }

        #endregion

        #region Voting Public API

        public void StartVote(VoteMode mode, string question, string[] options)
        {
            var voteData = VoteData.Create(mode, question, options);
            if (!IsOwner) { StartVoteOwnerRpc(voteData); return; }
            StartVoteInternal(mode, question, options);
        }

        public void EndVote()
        {
            if (!IsOwner) { EndVoteOwnerRpc(); return; }
            EndVoteInternal();
        }

        public void SubmitVote(int optionIndex)
        {
            if (!m_IsVotingActive.Value || m_CurrentMode.Value != VoteMode.MultipleChoice) 
            {
                Debug.Log($"[VOTE_DEBUG] SubmitVote rejected - voting active: {m_IsVotingActive.Value}, mode: {m_CurrentMode.Value}");
                return;
            }
            // Allow re-voting: if already voted, clear previous vote
            if (m_LocalVote.HasValue) 
            {
                Debug.Log($"[VOTE_DEBUG] Changing vote from {m_LocalVote.Value} to {optionIndex}");
            }
            
            Debug.Log($"[VOTE_DEBUG] Submitting vote for option {optionIndex}, IsServer={IsServer}, IsClient={IsClient}, IsSpawned={IsSpawned}, IsOwner={IsOwner}, OwnerClientId={NetworkObject.OwnerClientId}, LocalClientId={NetworkManager.Singleton.LocalClientId}");
            SubmitVoteOwnerRpc(optionIndex);
            Debug.Log($"[VOTE_DEBUG] OwnerRpc call sent to owner (ClientId={NetworkObject.OwnerClientId})");
            m_LocalVote = optionIndex;
            HighlightSelectedBar(optionIndex);
            SetStatusText($"You voted: {GetOptionLabel(optionIndex)}");
            
            // Notify HUD
            var hud = FindFirstObjectByType<VotingHUD>();
            if (hud != null) hud.OnLocalVoteSubmitted(optionIndex);
        }

        public int[] GetVoteCounts()
        {
            int[] counts = new int[m_VoteCounts.Count];
            for (int i = 0; i < m_VoteCounts.Count; i++)
                counts[i] = m_VoteCounts[i];
            return counts;
        }

        private string GetOptionLabel(int index)
        {
            if (index >= 0 && index < m_OptionLabels.Count)
                return m_OptionLabels[index].ToString();
            return ((char)('A' + index)).ToString();
        }

        #endregion

        #region Owner RPCs (Distributed Authority)

        [Rpc(SendTo.Owner)]
        private void StartVoteOwnerRpc(VoteData voteData, RpcParams rpcParams = default)
        {
            Debug.Log($"[VOTE_DEBUG] StartVoteOwnerRpc received from client {rpcParams.Receive.SenderClientId}");
            var player = GetPlayerByClientId(rpcParams.Receive.SenderClientId);
            if (player == null || !player.IsSessionOwner) return;
            StartVoteInternal(voteData.Mode, voteData.Question.ToString(), voteData.GetOptions());
        }

        [Rpc(SendTo.Owner)]
        private void EndVoteOwnerRpc(RpcParams rpcParams = default)
        {
            Debug.Log($"[VOTE_DEBUG] EndVoteOwnerRpc received from client {rpcParams.Receive.SenderClientId}");
            var player = GetPlayerByClientId(rpcParams.Receive.SenderClientId);
            if (player == null || !player.IsSessionOwner) return;
            EndVoteInternal();
        }

        [Rpc(SendTo.Owner)]
        private void SubmitVoteOwnerRpc(int optionIndex, RpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            Debug.Log($"[VOTE_DEBUG] SubmitVoteOwnerRpc received: option={optionIndex}, client={clientId}, m_OptionCount={m_OptionCount.Value}, m_VoteCounts.Count={m_VoteCounts.Count}, IsOwner={IsOwner}");
            
            // Validate option index using actual VoteCounts length as backup
            int maxOption = Mathf.Max(m_OptionCount.Value, m_VoteCounts.Count);
            if (optionIndex < 0 || optionIndex >= maxOption)
            {
                Debug.LogWarning($"[VOTE_DEBUG] Invalid vote index {optionIndex}, max is {maxOption}");
                return;
            }
            
            // Check if player already voted - if so, allow changing vote
            if (m_PlayerVoteIndex.ContainsKey(clientId))
            {
                int previousVote = m_PlayerVoteIndex[clientId];
                if (previousVote == optionIndex) return; // Same vote, ignore
                
                // Decrement old vote, increment new
                if (previousVote >= 0 && previousVote < m_VoteCounts.Count)
                    m_VoteCounts[previousVote] = Mathf.Max(0, m_VoteCounts[previousVote] - 1);
                    
                m_VoteCounts[optionIndex] = m_VoteCounts[optionIndex] + 1;
                m_PlayerVoteIndex[clientId] = optionIndex;
                Debug.Log($"[VOTE_DEBUG] Player {clientId} changed vote from {previousVote} to {optionIndex}. New count: {m_VoteCounts[optionIndex]}");
            }
            else
            {
                // First vote
                m_PlayersVoted.Add(clientId);
                m_PlayerVoteIndex[clientId] = optionIndex;
                m_VoteCounts[optionIndex] = m_VoteCounts[optionIndex] + 1;
                Debug.Log($"[VOTE_DEBUG] Player {clientId} voted for option {optionIndex}. New count: {m_VoteCounts[optionIndex]}");
            }
        }

        #endregion

        #region Voting Internal

        private void StartVoteInternal(VoteMode mode, string question, string[] options)
        {
            m_PlayersVoted.Clear();
            m_HandRaiseVotes.Clear();
            m_LocalVote = null;
            m_PlayersVoted.Clear();
            m_PlayerVoteIndex.Clear();
            
            for (int i = 0; i < m_VoteCounts.Count; i++)
                m_VoteCounts[i] = 0;

            // Set question
            m_Question.Value = question;
            
            // Set option labels
            m_OptionLabels.Clear();
            int count = Mathf.Clamp(options.Length, 2, m_MaxOptions);
            for (int i = 0; i < count; i++)
                m_OptionLabels.Add(options[i]);

            m_OptionCount.Value = count;
            m_CurrentMode.Value = mode;
            m_IsVotingActive.Value = true;
        }

        private void EndVoteInternal()
        {
            m_IsVotingActive.Value = false;
            m_CurrentMode.Value = VoteMode.None;
        }

        private void OnVotingActiveChanged(bool prev, bool curr)
        {
            m_LocalVote = null;
            if (curr) { OnVotingStarted?.Invoke(); OnVotingStartedUI(); }
            else { OnVotingEnded?.Invoke(); OnVotingEndedUI(); }
            UpdateUIState();
        }

        private void OnVoteCountsListChanged(NetworkListEvent<int> evt)
        {
            int[] counts = GetVoteCounts();
            Debug.Log($"[VOTE_DEBUG] Vote counts changed: [{string.Join(", ", counts)}], BarCountTexts: {m_BarCountTexts.Count}");
            OnVoteCountsUpdated?.Invoke(counts);
            UpdateChart(counts);
            UpdateBarCountLabels(counts); // Update floating count labels on bars
        }

        private void OnQuestionValueChanged(FixedString128Bytes prev, FixedString128Bytes curr)
        {
            OnQuestionChanged?.Invoke(curr.ToString(), GetOptionLabels());
        }

        private void OnOptionLabelsChanged(NetworkListEvent<FixedString64Bytes> evt)
        {
            OnQuestionChanged?.Invoke(m_Question.Value.ToString(), GetOptionLabels());
            UpdateAllBarLabels(); // Update bar labels with actual option text
        }

        private void UpdateHandRaiseVotes()
        {
            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            foreach (var p in players)
            {
                if (p.head == null || p.leftHand == null || p.rightHand == null) continue;

                ulong id = p.OwnerClientId;
                float headY = p.head.position.y;
                bool leftUp = p.leftHand.position.y > headY + m_HandRaiseThreshold;
                bool rightUp = p.rightHand.position.y > headY + m_HandRaiseThreshold;

                int newVote = -1;
                if (leftUp && !rightUp) newVote = 0;
                else if (rightUp && !leftUp) newVote = 1;

                if (!m_HandRaiseVotes.TryGetValue(id, out int curr)) curr = -1;

                if (newVote != curr)
                {
                    if (curr >= 0 && curr < m_VoteCounts.Count)
                        m_VoteCounts[curr] = Mathf.Max(0, m_VoteCounts[curr] - 1);
                    if (newVote >= 0 && newVote < m_VoteCounts.Count)
                        m_VoteCounts[newVote] = m_VoteCounts[newVote] + 1;
                    m_HandRaiseVotes[id] = newVote;
                }
            }
        }

        private XRINetworkPlayer GetPlayerByClientId(ulong clientId)
        {
            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            foreach (var p in players)
                if (p.OwnerClientId == clientId) return p;
            return null;
        }

        #endregion

        #region UI

        private void OnStartVoteClicked()
        {
            // Check for conflict with recording playback
            if (XRMultiplayer.Recording.MeetingPlaybackManager.Instance != null && 
                XRMultiplayer.Recording.MeetingPlaybackManager.Instance.IsPlaybackActive)
            {
                SetStatusText("Cannot vote during playback.");
                return;
            }

            string question = m_QuestionInput != null ? m_QuestionInput.text : "Vote:";
            List<string> options = new List<string>();
            
            if (m_OptionInputs != null)
            {
                foreach (var input in m_OptionInputs)
                {
                    if (input != null && input.gameObject.activeSelf && !string.IsNullOrEmpty(input.text))
                        options.Add(input.text);
                }
            }
            
            if (options.Count < 2)
                options = new List<string> { "A", "B" };

            StartVote(m_SelectedMode, question, options.ToArray());
        }

        private void OnEndVoteClicked() => EndVote();

        private void OnVotingStartedUI()
        {
            // Host setup panel overlaps the head-follow HUD and 3D chart — close it when the vote goes live.
            if (m_HostPanel != null)
            {
                if (QuickMenuManager.Instance != null)
                    QuickMenuManager.Instance.CloseFloatingPanel(m_HostPanel);
                else
                    m_HostPanel.SetActive(false);
            }

            // Show and configure chart
            SetChartVisible(true);
            EnsureBarCount(m_OptionCount.Value);
            MakeBarsInteractable(true);
            
            // Update labels with actual option text (may have synced by now)
            UpdateAllBarLabels();
            UpdateBarCountLabels(GetVoteCounts());
            
            if (m_CurrentMode.Value == VoteMode.MultipleChoice)
            {
                SetStatusText($"Vote by clicking on bars");
            }
            else
            {
                SetStatusText($"Left hand = {GetOptionLabel(0)}, Right hand = {GetOptionLabel(1)}");
            }
        }

        private void OnVotingEndedUI()
        {
            MakeBarsInteractable(false);
            SetStatusText("Voting ended.");
            
            // Hide chart after a delay
            Invoke(nameof(HideChart), 5f);
        }
        
        private void HideChart()
        {
            if (!m_IsVotingActive.Value)
                SetChartVisible(false);
        }

        private void UpdateUIState()
        {
            bool voting = m_IsVotingActive.Value;
            bool host = NetworkManager.Singleton != null &&
                        XRINetworkPlayer.LocalPlayer != null &&
                        XRINetworkPlayer.LocalPlayer.IsSessionOwner;

            // Host panel visibility is controlled by QuickMenuManager. We do not touch it here.
            // if (m_HostPanel != null && !host) m_HostPanel.SetActive(false);
            if (m_StartVoteButton != null) m_StartVoteButton.interactable = host && !voting;
            if (m_EndVoteButton != null) m_EndVoteButton.interactable = host && voting;
            if (m_HandRaiseInstructions != null)
                m_HandRaiseInstructions.SetActive(voting && m_CurrentMode.Value == VoteMode.HandRaise);
        }

        private void SetStatusText(string msg)
        {
            if (m_StatusText != null) m_StatusText.text = msg;
        }
        
        #endregion
        
        #region Option Management
        
        private int m_VisibleOptionCount = 2;
        
        private void OnAddOption()
        {
            if (m_OptionInputs == null) return;
            if (m_VisibleOptionCount >= m_MaxOptions) return;
            
            m_VisibleOptionCount++;
            UpdateVisibleOptionInputs(m_VisibleOptionCount);
        }
        
        private void OnRemoveOption()
        {
            if (m_OptionInputs == null) return;
            if (m_VisibleOptionCount <= 2) return;
            
            m_VisibleOptionCount--;
            UpdateVisibleOptionInputs(m_VisibleOptionCount);
        }
        
        private void UpdateVisibleOptionInputs(int count)
        {
            m_VisibleOptionCount = Mathf.Clamp(count, 2, m_MaxOptions);
            
            if (m_OptionInputs == null) return;
            
            // Ensure options container has proper layout for dynamic sizing
            EnsureOptionsContainerLayout();
            
            for (int i = 0; i < m_OptionInputs.Count; i++)
            {
                if (m_OptionInputs[i] != null)
                    m_OptionInputs[i].gameObject.SetActive(i < m_VisibleOptionCount);
            }

            if (m_OptionsContainer != null)
            {
                for (int i = 0; i < m_OptionsContainer.childCount; i++)
                {
                    var row = m_OptionsContainer.GetChild(i) as RectTransform;
                    if (row != null)
                        VotingUILayoutUtility.ApplyLayoutRow(row, 30);
                }
            }

            if (m_HostPanel != null && m_HostPanel.activeInHierarchy)
                VotingUILayoutUtility.RebuildHostPanel(m_HostPanel.GetComponent<RectTransform>());
            
            // Update add/remove button states
            if (m_AddOptionButton != null)
                m_AddOptionButton.interactable = m_VisibleOptionCount < m_MaxOptions;
            if (m_RemoveOptionButton != null)
                m_RemoveOptionButton.interactable = m_VisibleOptionCount > 2;
        }
        
        private void EnsureOptionsContainerLayout()
        {
            VotingUILayoutUtility.ConfigureOptionsList(m_OptionsContainer);
        }
        
        private void SetChartVisible(bool visible)
        {
            if (m_ChartOrigin != null)
                m_ChartOrigin.gameObject.SetActive(visible);
            
            // Also hide individual bars
            foreach (var bar in m_Bars)
            {
                if (bar != null)
                    bar.gameObject.SetActive(visible);
            }
        }

        #endregion

        #region 3D Chart

        private void UpdateChart(int[] counts)
        {
            // FIX: Don't show chart if voting isn't active (prevents bars appearing on join)
            if (!m_IsVotingActive.Value)
            {
                SetChartVisible(false);
                return;
            }

            if (counts == null || counts.Length == 0) return;

            // FIX: Use m_OptionCount if available to limit visible bars
            int visibleCount = counts.Length;
            if (m_OptionCount.Value > 0 && m_OptionCount.Value < counts.Length)
                visibleCount = m_OptionCount.Value;

            // FIX: Scale bars relative to TOTAL PLAYERS, not just the leader
            // This prevents "1 vote = full bar" and allows bars to grow as more people vote
            int totalPlayers = NetworkManager.Singleton.ConnectedClientsIds.Count;
            m_MaxVotes = Mathf.Max(1, totalPlayers);

            EnsureBarCount(visibleCount);

            m_TargetHeights.Clear();
            for (int i = 0; i < visibleCount; i++)
            {
                float h = Mathf.Max(0.05f, (float)counts[i] / m_MaxVotes * m_MaxBarHeight);
                m_TargetHeights.Add(h);
            }

            if (m_ChartOrigin != null) m_ChartOrigin.gameObject.SetActive(true);
        }

        private void AnimateChartBars()
        {
            for (int i = 0; i < m_Bars.Count; i++)
            {
                if (m_Bars[i] == null) continue;
                
                // Find the BarMesh child (the actual cylinder that grows)
                Transform barMesh = m_Bars[i].Find("BarMesh");
                if (barMesh == null) barMesh = m_Bars[i]; // Fallback for old structure
                
                float target = m_TargetHeights.Count > i ? m_TargetHeights[i] : 0f;
                Vector3 scale = barMesh.localScale;
                float newH = Mathf.Lerp(scale.y, target, Time.deltaTime * m_ChartAnimationSpeed);
                barMesh.localScale = new Vector3(m_BarWidth, newH, m_BarWidth);
                barMesh.localPosition = new Vector3(0, newH / 2f + 0.01f, 0); // Sits slightly above base
            }
        }

        private void EnsureBarCount(int count)
        {
            while (m_Bars.Count > count)
            {
                int last = m_Bars.Count - 1;
                if (m_Bars[last] != null) Destroy(m_Bars[last].gameObject);
                m_Bars.RemoveAt(last);
                if (m_BarRenderers.Count > last) m_BarRenderers.RemoveAt(last);
                if (m_OriginalBarColors.Count > last) m_OriginalBarColors.RemoveAt(last);
                if (m_BarLabelTexts.Count > last) m_BarLabelTexts.RemoveAt(last);
                if (m_BarCountTexts.Count > last) m_BarCountTexts.RemoveAt(last);
            }

            while (m_Bars.Count < count)
                CreateBar(m_Bars.Count);

            RepositionBars();
        }

        private void CreateBar(int idx)
        {
            Transform origin = m_ChartOrigin != null ? m_ChartOrigin : transform;
            
            // === Create bar container ===
            GameObject barContainer = new GameObject($"Bar_{idx}");
            barContainer.transform.SetParent(origin);
            barContainer.transform.localScale = Vector3.one;

            // === Create base platform (glowing disc) ===
            GameObject basePlatform = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            basePlatform.name = "BasePlatform";
            basePlatform.transform.SetParent(barContainer.transform);
            basePlatform.transform.localPosition = Vector3.zero;
            basePlatform.transform.localScale = new Vector3(m_BarWidth * 1.8f, 0.01f, m_BarWidth * 1.8f);
            
            var baseCol = basePlatform.GetComponent<Collider>();
            if (baseCol != null) Destroy(baseCol);
            
            Color barColor = idx < m_BarColors.Length ? m_BarColors[idx] : Color.gray;
            var baseRend = basePlatform.GetComponent<Renderer>();
            if (baseRend != null)
            {
                baseRend.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                baseRend.material.color = barColor * 0.5f;
                baseRend.material.EnableKeyword("_EMISSION");
                baseRend.material.SetColor("_EmissionColor", barColor * 0.3f);
            }

            // === Create main bar (cylinder for modern look) ===
            GameObject barMesh = m_BarPrefab != null ? Instantiate(m_BarPrefab, barContainer.transform) : GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            barMesh.name = "BarMesh";
            barMesh.transform.SetParent(barContainer.transform);
            barMesh.transform.localScale = new Vector3(m_BarWidth, 0.05f, m_BarWidth);
            barMesh.transform.localPosition = new Vector3(0, 0.025f, 0); // Sits on base
            
            var meshCol = barMesh.GetComponent<Collider>();
            if (meshCol != null) Destroy(meshCol);

            // Premium material with gradient and emission
            var rend = barMesh.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                rend.material.color = barColor;
                rend.material.EnableKeyword("_EMISSION");
                rend.material.SetColor("_EmissionColor", barColor * 0.4f); // Subtle glow
                rend.material.SetFloat("_Smoothness", 0.8f); // Shiny surface
            }

            // Use container as interaction target
            barContainer.layer = LayerMask.NameToLayer("Default");

            // === Add larger collider for easier VR interaction ===
            BoxCollider col = barContainer.AddComponent<BoxCollider>();
            col.size = new Vector3(m_BarWidth * 2f, 0.5f, m_BarWidth * 2f); // Much larger hitbox
            col.center = new Vector3(0, 0.25f, 0);
            col.isTrigger = false;

            // === Add XR interactable ===
            var interactable = barContainer.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
            int barIndex = idx;
            // Use selectEntered for ray+trigger interaction (plus manual fail-safe in Update)
            interactable.selectEntered.AddListener((args) => OnBarSelected(barIndex));
            interactable.hoverEntered.AddListener((args) => OnBarHoverEnter(barIndex));
            interactable.hoverExited.AddListener((args) => OnBarHoverExit(barIndex));

            m_Bars.Add(barContainer.transform);
            m_BarRenderers.Add(rend);
            m_OriginalBarColors.Add(barColor);

            // Add labels (parent to container, not barMesh which scales)
            CreateBarLabel(barContainer.transform, idx);
        }


        private void CreateBarLabel(Transform container, int idx)
        {
            // === VOTE COUNT LABEL (floating above bar) ===
            GameObject countGO = new GameObject($"CountLabel_{idx}");
            countGO.transform.SetParent(container);
            countGO.transform.localPosition = new Vector3(0, 0.8f, 0); // Above the bar
            countGO.transform.localRotation = Quaternion.identity;
            countGO.transform.localScale = Vector3.one; // Container doesn't scale

            var countTmp = countGO.AddComponent<TextMeshPro>();
            countTmp.text = "0";
            countTmp.fontSize = 1.5f; // Smaller text
            countTmp.fontStyle = FontStyles.Bold;
            countTmp.alignment = TextAlignmentOptions.Center;
            countTmp.color = Color.white;
            m_BarCountTexts.Add(countTmp);

            // === OPTION TEXT LABEL (at base of bar) ===
            GameObject labelGO = new GameObject($"Label_{idx}");
            labelGO.transform.SetParent(container);
            labelGO.transform.localPosition = new Vector3(0, 0.25f, 0); // Raised to be visible above base
            labelGO.transform.localRotation = Quaternion.identity;
            labelGO.transform.localScale = Vector3.one; // Container doesn't scale

            var tmp = labelGO.AddComponent<TextMeshPro>();
            tmp.text = GetOptionLabel(idx); // Will update when options change
            tmp.fontSize = 2;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            m_BarLabelTexts.Add(tmp);
        }

        /// <summary>
        /// Updates all bar labels with current option text.
        /// Called when m_OptionLabels changes.
        /// </summary>
        private void UpdateAllBarLabels()
        {
            for (int i = 0; i < m_BarLabelTexts.Count && i < m_OptionLabels.Count; i++)
            {
                if (m_BarLabelTexts[i] != null)
                    m_BarLabelTexts[i].text = m_OptionLabels[i].ToString();
            }
        }

        /// <summary>
        /// Updates vote count labels on bars.
        /// </summary>
        private void UpdateBarCountLabels(int[] counts)
        {
            for (int i = 0; i < m_BarCountTexts.Count && i < counts.Length; i++)
            {
                if (m_BarCountTexts[i] != null)
                    m_BarCountTexts[i].text = counts[i].ToString();
            }
        }

        private void RepositionBars()
        {
            if (m_Bars.Count == 0) return;
            float totalW = (m_Bars.Count - 1) * m_BarSpacing;
            float startX = -totalW / 2f;
            for (int i = 0; i < m_Bars.Count; i++)
            {
                if (m_Bars[i] == null) continue;
                m_Bars[i].localPosition = new Vector3(startX + i * m_BarSpacing, 0, 0);
            }
        }

        private void MakeBarsInteractable(bool interactable)
        {
            foreach (var bar in m_Bars)
            {
                if (bar == null) continue;
                var xrInt = bar.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
                if (xrInt != null) xrInt.enabled = interactable;
            }
        }

        private void OnBarSelected(int idx)
        {
            if (!m_IsVotingActive.Value || m_CurrentMode.Value != VoteMode.MultipleChoice) return;
            SubmitVote(idx);
        }

        private void OnBarHoverEnter(int idx)
        {
            m_HoveredBarIndex = idx; // Track for fail-safe click detection
            
            if (idx < m_BarRenderers.Count && m_BarRenderers[idx] != null)
            {
                // Color change
                m_BarRenderers[idx].material.color = m_BarHoverColor;
                // Boost emission glow
                m_BarRenderers[idx].material.SetColor("_EmissionColor", m_BarHoverColor * 0.8f);
            }
            
            // Scale up animation (on container)
            if (idx < m_Bars.Count && m_Bars[idx] != null)
            {
                m_Bars[idx].localScale = Vector3.one * 1.1f;
            }
        }

        private void OnBarHoverExit(int idx)
        {
            if (m_HoveredBarIndex == idx)
                m_HoveredBarIndex = -1; // Clear hover tracking
            
            if (idx < m_BarRenderers.Count && m_BarRenderers[idx] != null)
            {
                Color targetColor;
                if (m_LocalVote.HasValue && m_LocalVote.Value == idx)
                    targetColor = m_BarSelectedColor;
                else if (idx < m_OriginalBarColors.Count)
                    targetColor = m_OriginalBarColors[idx];
                else
                    targetColor = Color.gray;
                    
                m_BarRenderers[idx].material.color = targetColor;
                // Reset emission to normal glow
                m_BarRenderers[idx].material.SetColor("_EmissionColor", targetColor * 0.4f);
            }
            
            // Scale back to normal
            if (idx < m_Bars.Count && m_Bars[idx] != null)
            {
                m_Bars[idx].localScale = Vector3.one;
            }
        }

        private void HighlightSelectedBar(int idx)
        {
            // Reset all bars first
            for (int i = 0; i < m_BarRenderers.Count; i++)
            {
                if (m_BarRenderers[i] != null && i < m_OriginalBarColors.Count)
                    m_BarRenderers[i].material.color = m_OriginalBarColors[i];
            }
            
            // Highlight selected
            if (idx < m_BarRenderers.Count && m_BarRenderers[idx] != null)
                m_BarRenderers[idx].material.color = m_BarSelectedColor;
        }

        public void ResetChart()
        {
            m_TargetHeights.Clear();
            for (int i = 0; i < m_Bars.Count; i++)
                m_TargetHeights.Add(0.05f);
            m_LocalVote = null;
        }

        #endregion
    }
}
