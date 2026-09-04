using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Manages player analytics and creates visual indicators on PlayerNameTags.
    /// Host-only: indicators are only visible to the session host.
    /// </summary>
    public class AnalyticsManager : MonoBehaviour
    {
        public static AnalyticsManager Instance { get; private set; }

        [Header("Gaze Settings")]
        [SerializeField] Transform m_GazeTarget;
        [SerializeField] float m_GazeAngleThreshold = 30f;
        
        [Header("Attention Debounce (prevents flickering)")]
        [Tooltip("Seconds user must look away before logging ATTENTION_LAPSE")]
        [SerializeField] float m_AttentionLapseThreshold = 1.0f;
        [Tooltip("Seconds user must look back before logging ATTENTION_RESTORED")]
        [SerializeField] float m_AttentionRestoreThreshold = 0.5f;

        [Header("Sentiment Settings")]
        [SerializeField] float m_SentimentDecayTime = 5f;

        [Header("Indicator Colors")]
        [SerializeField] Color m_PositiveColor = new Color(0.2f, 0.9f, 0.3f);
        [SerializeField] Color m_NegativeColor = new Color(0.95f, 0.2f, 0.2f);
        [SerializeField] Color m_NeutralColor = new Color(0.9f, 0.9f, 0.3f);
        [SerializeField] Color m_GazeActiveColor = new Color(0.3f, 0.95f, 0.5f);
        [SerializeField] Color m_GazeInactiveColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);

        [Header("Icon Sprites (assign in Inspector or auto-loaded)")]
        [SerializeField] Sprite m_CircleSprite;
        [SerializeField] Sprite m_GazeSprite;

        readonly Dictionary<ulong, PlayerAnalyticsData> m_PlayerData = new();
        readonly Dictionary<ulong, HeadGestureDetector> m_GestureDetectors = new();
        readonly Dictionary<ulong, AnalyticsIndicators> m_Indicators = new();

        bool m_IsHost;
        bool m_IsInitialized;
        
        // Attention state tracking with debounce
        readonly Dictionary<ulong, bool> m_ConfirmedAttentionStates = new();  // Last logged state
        readonly Dictionary<ulong, bool> m_RawAttentionStates = new();        // Current raw gaze state
        readonly Dictionary<ulong, float> m_AttentionStateChangeTime = new(); // When raw state last changed
        readonly Dictionary<ulong, float> m_LastFocusedStartTime = new();     // When user started focusing

        struct AnalyticsIndicators
        {
            public Image SentimentIcon;
            public Image GazeIcon;
            public TMP_Text SpeakingText;
            public GameObject Container;
        }

        float m_SessionStartTime;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void Start()
        {
            // Debug.Log("[AnalyticsManager] Start called");
            
            // For Distributed Authority, we check when connected to network
            XRINetworkGameManager.Connected.Subscribe(OnConnectionChanged);
            
            // Also subscribe to player state changes
            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged += OnPlayerStateChanged;
            }
        }

        void OnDestroy()
        {
            XRINetworkGameManager.Connected.Unsubscribe(OnConnectionChanged);
            
            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged -= OnPlayerStateChanged;
            }
            CleanupAll();
        }

        void OnConnectionChanged(bool connected)
        {
            // Debug.Log($"[AnalyticsManager] Connection changed: {connected}");
            
            if (connected)
            {
                m_IsInitialized = true;
                m_SessionStartTime = Time.time;
                
                // Subscribe to session owner changes (for host migration)
                if (XRINetworkGameManager.Instance != null)
                {
                    XRINetworkGameManager.Instance.OnSessionOwnerPromoted += OnSessionOwnerChanged;
                }
                
                // Check if we are the session owner (host)
                // This will be called again after player is fully spawned
                StartCoroutine(CheckSessionOwnerDelayed());
                
                // Debug.Log("[AnalyticsManager] Connected - Checking host status...");
                
                // Register existing players after a delay
                StartCoroutine(RegisterExistingPlayers());
            }
            else
            {
                m_IsInitialized = false;
                m_IsHost = false;
                
                if (XRINetworkGameManager.Instance != null)
                {
                    XRINetworkGameManager.Instance.OnSessionOwnerPromoted -= OnSessionOwnerChanged;
                }
                
                CleanupAll();
            }
        }

        System.Collections.IEnumerator CheckSessionOwnerDelayed()
        {
            // Wait for player to be fully spawned
            yield return new WaitForSeconds(2.0f);
            
            if (XRINetworkPlayer.LocalPlayer != null)
            {
                m_IsHost = XRINetworkPlayer.LocalPlayer.IsSessionOwner;
                // Debug.Log($"[AnalyticsManager] Session owner check: IsHost = {m_IsHost}");
                
                if (!m_IsHost)
                {
                    // Not host, cleanup any indicators that might have been created
                    CleanupAll();
                }
            }
            else
            {
                // No local player yet, default to false
                m_IsHost = false;
                Debug.LogWarning("[AnalyticsManager] LocalPlayer not available, defaulting IsHost to false");
            }
        }

        void OnSessionOwnerChanged(ulong newSessionOwnerId)
        {
            // Host has changed (migration occurred)
            if (XRINetworkPlayer.LocalPlayer != null)
            {
                bool wasHost = m_IsHost;
                m_IsHost = XRINetworkPlayer.LocalPlayer.IsSessionOwner;
                
                // Debug.Log($"[AnalyticsManager] Session owner changed to {newSessionOwnerId}. IsHost = {m_IsHost}");
                
                if (m_IsHost && !wasHost)
                {
                    // We became host, register players and create indicators
                    StartCoroutine(RegisterExistingPlayers());
                }
                else if (!m_IsHost && wasHost)
                {
                    // We are no longer host, cleanup indicators
                    CleanupAll();
                }
            }
        }

        System.Collections.IEnumerator RegisterExistingPlayers()
        {
            // Wait for players to be fully spawned
            yield return new WaitForSeconds(2.0f);
            
            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            // Debug.Log($"[AnalyticsManager] Found {players.Length} existing players");
            
            foreach (var player in players)
            {
                if (player != null && player.IsSpawned)
                {
                    RegisterPlayer(player.OwnerClientId);
                }
            }
        }

        void OnPlayerStateChanged(ulong playerId, bool connected)
        {
            // Debug.Log($"[AnalyticsManager] Player state changed: {playerId}, connected: {connected}");
            
            if (connected)
            {
                // Delay registration to ensure player is fully spawned
                StartCoroutine(DelayedRegister(playerId));
            }
            else
            {
                UnregisterPlayer(playerId);
            }
        }

        System.Collections.IEnumerator DelayedRegister(ulong clientId)
        {
            // Longer delay to ensure PlayerNameTag is fully initialized
            yield return new WaitForSeconds(3.0f);
            RegisterPlayer(clientId);
        }

        void RegisterPlayer(ulong playerId)
        {
            if (m_PlayerData.ContainsKey(playerId)) return;

            if (!XRINetworkGameManager.Instance.TryGetPlayerByID(playerId, out var player))
                return;

            // Initialize analytics data
            m_PlayerData[playerId] = new PlayerAnalyticsData
            {
                ClientId = playerId,
                PlayerName = player.playerName,
                Sentiment = SentimentState.Neutral
            };

            // Add gesture detector to player's head transform (network-synced)
            // Use player.head which is the network-replicated head transform
            var headTransform = player.head;
            if (headTransform != null)
            {
                // Check for existing detector to avoid duplicates (SessionAuditLogger may have added one)
                var detector = headTransform.GetComponent<HeadGestureDetector>();
                if (detector == null)
                {
                    detector = headTransform.gameObject.AddComponent<HeadGestureDetector>();
                    Debug.Log($"[AnalyticsManager] Added HeadGestureDetector to player {playerId}'s head: {headTransform.name}");
                }
                else
                {
                    Debug.Log($"[AnalyticsManager] Found existing HeadGestureDetector on player {playerId}'s head: {headTransform.name}");
                }
                
                detector.OnNodDetected += () => {
                    Debug.Log($"[AnalyticsManager] Nod detected for player {playerId}, setting to POSITIVE");
                    SetSentiment(playerId, SentimentState.Positive);
                };
                detector.OnShakeDetected += () => {
                    Debug.Log($"[AnalyticsManager] Shake detected for player {playerId}, setting to NEGATIVE");
                    SetSentiment(playerId, SentimentState.Negative);
                };
                m_GestureDetectors[playerId] = detector;
            }
            else
            {
                Debug.LogWarning($"[AnalyticsManager] No head transform found for player {playerId}");
            }

            // Create indicators (host only)
            if (m_IsHost)
            {
                CreateIndicators(playerId, player);
            }

            // Debug.Log($"[AnalyticsManager] Registered player {playerId}");
        }

        void UnregisterPlayer(ulong playerId)
        {
            if (m_GestureDetectors.TryGetValue(playerId, out var detector))
            {
                if (detector != null) Destroy(detector);
                m_GestureDetectors.Remove(playerId);
            }

            if (m_Indicators.TryGetValue(playerId, out var indicators))
            {
                if (indicators.Container != null) Destroy(indicators.Container);
                m_Indicators.Remove(playerId);
            }

            m_PlayerData.Remove(playerId);
        }

        void CreateIndicators(ulong playerId, XRINetworkPlayer player)
        {
            // Debug.Log($"[AnalyticsManager] CreateIndicators called for player {playerId}");
            
            // PlayerNameTags are reparented to WorldCanvas, so we must get them from there
            if (WorldCanvas.Instance == null)
            {
                Debug.LogWarning($"[AnalyticsManager] WorldCanvas.Instance is null");
                return;
            }
            
            var nameTag = WorldCanvas.Instance.GetPlayerNameTag(playerId);
            if (nameTag == null)
            {
                Debug.LogWarning($"[AnalyticsManager] No PlayerNameTag found for player {playerId} (may be local player or not yet registered)");
                return;
            }
            
            // Skip if name tag is disabled (local player's own tag)
            if (!nameTag.gameObject.activeInHierarchy)
            {
                // Debug.Log($"[AnalyticsManager] PlayerNameTag for player {playerId} is disabled (local player), skipping");
                return;
            }
            
            // Debug.Log($"[AnalyticsManager] Found PlayerNameTag: {nameTag.gameObject.name}");

            // Find the Layout Group in the name tag hierarchy
            var layoutGroup = nameTag.GetComponentInChildren<HorizontalLayoutGroup>();
            if (layoutGroup == null)
            {
                Debug.LogWarning($"[AnalyticsManager] No HorizontalLayoutGroup found in PlayerNameTag");
                return;
            }
            // Debug.Log($"[AnalyticsManager] Found LayoutGroup: {layoutGroup.gameObject.name}");

            // Create container for analytics icons
            var container = new GameObject($"Analytics_{playerId}");
            container.transform.SetParent(layoutGroup.transform, false);
            
            var containerRT = container.AddComponent<RectTransform>();
            containerRT.sizeDelta = new Vector2(95, 40);  // Wider to fit sentiment + gaze + percentage

            var containerLayout = container.AddComponent<HorizontalLayoutGroup>();
            containerLayout.spacing = 5;
            containerLayout.childAlignment = TextAnchor.MiddleCenter;
            containerLayout.childControlWidth = false;
            containerLayout.childControlHeight = false;

            // Load sprites if not already loaded
            LoadSpritesIfNeeded();

            // Create Sentiment Icon (colored circle)
            var sentimentObj = CreateIconObject(container.transform, "Sentiment", 20, m_CircleSprite);
            var sentimentIcon = sentimentObj.GetComponent<Image>();
            sentimentIcon.color = m_NeutralColor;

            // Create Gaze Icon (eye-like indicator)
            var gazeObj = CreateIconObject(container.transform, "Gaze", 16, m_GazeSprite);
            var gazeIcon = gazeObj.GetComponent<Image>();
            gazeIcon.color = m_GazeInactiveColor;

            // Create Speaking Percentage Text
            var speakingObj = new GameObject("SpeakingPct");
            speakingObj.transform.SetParent(container.transform, false);
            
            var speakingRT = speakingObj.AddComponent<RectTransform>();
            speakingRT.sizeDelta = new Vector2(35, 20);  // Compact size for "XX%"
            
            var speakingText = speakingObj.AddComponent<TextMeshProUGUI>();
            speakingText.text = "0%";
            speakingText.fontSize = 11;  // Slightly smaller for better fit
            speakingText.color = Color.white;
            speakingText.alignment = TextAlignmentOptions.Center;
            speakingText.fontStyle = FontStyles.Bold;

            m_Indicators[playerId] = new AnalyticsIndicators
            {
                SentimentIcon = sentimentIcon,
                GazeIcon = gazeIcon,
                SpeakingText = speakingText,
                Container = container
            };
            
            // Debug.Log($"[AnalyticsManager] Created indicators for player {playerId}");
        }

        GameObject CreateIconObject(Transform parent, string name, float size, Sprite sprite = null)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            var rt = obj.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);

            obj.AddComponent<CanvasRenderer>();
            
            var image = obj.AddComponent<Image>();
            image.color = Color.white;
            
            // Apply sprite if provided
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
            }

            var le = obj.AddComponent<LayoutElement>();
            le.preferredWidth = size;
            le.preferredHeight = size;

            return obj;
        }

        void LoadSpritesIfNeeded()
        {
            // Create circle sprite programmatically if not assigned in Inspector
            if (m_CircleSprite == null)
            {
                m_CircleSprite = CreateCircleSprite(64, true);
            }
            if (m_GazeSprite == null)
            {
                m_GazeSprite = CreateCircleSprite(64, false); // Outline version
            }
        }

        Sprite CreateCircleSprite(int size, bool filled)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            float center = size / 2f;
            float radius = size / 2f - 1;
            float innerRadius = filled ? 0 : radius - 4;
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                    
                    if (dist <= radius && dist >= innerRadius)
                    {
                        // Anti-aliased edge
                        float alpha = 1f;
                        if (dist > radius - 1) alpha = radius - dist + 1;
                        if (!filled && dist < innerRadius + 1) alpha = Mathf.Min(alpha, dist - innerRadius);
                        
                        texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(alpha)));
                    }
                    else
                    {
                        texture.SetPixel(x, y, Color.clear);
                    }
                }
            }
            
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
        }

        void Update()
        {
            if (!m_IsInitialized || !m_IsHost) return;

            // DEBUG: Keyboard shortcuts for testing sentiment (Editor only)
            #if UNITY_EDITOR
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.nKey.wasPressedThisFrame)
                {
                    // Set first remote player to Positive
                    foreach (var playerId in m_PlayerData.Keys)
                    {
                        if (m_Indicators.ContainsKey(playerId))
                        {
                            SetSentiment(playerId, SentimentState.Positive);
                            // Debug.Log($"[AnalyticsManager] TEST: Set player {playerId} sentiment to POSITIVE");
                            break;
                        }
                    }
                }
                if (keyboard.mKey.wasPressedThisFrame)
                {
                    foreach (var playerId in m_PlayerData.Keys)
                    {
                        if (m_Indicators.ContainsKey(playerId))
                        {
                            SetSentiment(playerId, SentimentState.Negative);
                            // Debug.Log($"[AnalyticsManager] TEST: Set player {playerId} sentiment to NEGATIVE");
                            break;
                        }
                    }
                }
                if (keyboard.bKey.wasPressedThisFrame)
                {
                    foreach (var playerId in m_PlayerData.Keys)
                    {
                        if (m_Indicators.ContainsKey(playerId))
                        {
                            SetSentiment(playerId, SentimentState.Neutral);
                            // Debug.Log($"[AnalyticsManager] TEST: Set player {playerId} sentiment to NEUTRAL");
                            break;
                        }
                    }
                }
            }
            #endif

            // Copy keys to avoid collection modified exception
            var playerIds = new System.Collections.Generic.List<ulong>(m_PlayerData.Keys);
            
            // First pass: Update all speaking times
            foreach (var playerId in playerIds)
            {
                if (!m_PlayerData.TryGetValue(playerId, out var data)) continue;
                
                UpdateSpeaking(playerId, ref data);
                m_PlayerData[playerId] = data;
            }
            
            // Second pass: Update other analytics and indicators
            foreach (var playerId in playerIds)
            {
                if (!m_PlayerData.TryGetValue(playerId, out var data)) continue;

                // Update gaze
                UpdateGaze(playerId, ref data);

                // Decay sentiment
                DecaySentiment(playerId, ref data);

                // Update visual indicators (only for players with indicators)
                if (m_Indicators.ContainsKey(playerId))
                {
                    UpdateIndicators(playerId, data);
                }

                m_PlayerData[playerId] = data;
            }
        }

        void UpdateGaze(ulong playerId, ref PlayerAnalyticsData data)
        {
            if (!XRINetworkGameManager.Instance.TryGetPlayerByID(playerId, out var player)) return;

            // Use the network-synced head transform
            var headTransform = player.head;
            if (headTransform == null) return;

            bool isCurrentlyLooking = false;
            string currentTargetName = "";

            // Check 1: Looking at the presentation screen (GazeTarget)
            if (m_GazeTarget != null)
            {
                var toTarget = (m_GazeTarget.position - headTransform.position).normalized;
                var angle = Vector3.Angle(headTransform.forward, toTarget);
                if (angle < m_GazeAngleThreshold)
                {
                    isCurrentlyLooking = true;
                    currentTargetName = "Presentation Screen";
                }
            }

            // Check 2: Looking at any other player's head
            if (!isCurrentlyLooking)
            {
                foreach (var otherPlayerId in m_PlayerData.Keys)
                {
                    if (otherPlayerId == playerId) continue; // Skip self
                    
                    if (XRINetworkGameManager.Instance.TryGetPlayerByID(otherPlayerId, out var otherPlayer))
                    {
                        var otherHead = otherPlayer.head;
                        if (otherHead == null) continue;
                        
                        var toOther = (otherHead.position - headTransform.position).normalized;
                        var angleToOther = Vector3.Angle(headTransform.forward, toOther);
                        if (angleToOther < m_GazeAngleThreshold)
                        {
                            isCurrentlyLooking = true;
                            currentTargetName = otherPlayer.playerName;
                            break;
                        }
                    }
                }
            }

            data.IsLookingAtTarget = isCurrentlyLooking;
            
            // Initialize tracking dictionaries if needed
            if (!m_ConfirmedAttentionStates.ContainsKey(playerId))
            {
                m_ConfirmedAttentionStates[playerId] = true; // Assume focused at start
                m_RawAttentionStates[playerId] = isCurrentlyLooking;
                m_AttentionStateChangeTime[playerId] = Time.time;
                m_LastFocusedStartTime[playerId] = Time.time;
                return;
            }
            
            bool previousRawState = m_RawAttentionStates[playerId];
            bool confirmedState = m_ConfirmedAttentionStates[playerId];
            
            // Detect raw state change
            if (isCurrentlyLooking != previousRawState)
            {
                m_RawAttentionStates[playerId] = isCurrentlyLooking;
                m_AttentionStateChangeTime[playerId] = Time.time;
            }
            
            float timeSinceStateChange = Time.time - m_AttentionStateChangeTime[playerId];
            
            // Check if we should confirm a state change (debounce)
            if (isCurrentlyLooking != confirmedState)
            {
                // User looking away - check lapse threshold
                if (!isCurrentlyLooking && timeSinceStateChange >= m_AttentionLapseThreshold)
                {
                    // Confirm LAPSE
                    m_ConfirmedAttentionStates[playerId] = false;
                    
                    // Calculate how long they were focused before lapsing
                    float focusedDuration = m_AttentionStateChangeTime[playerId] - m_LastFocusedStartTime[playerId];
                    
                    if (SessionAuditLogger.Instance != null)
                    {
                        SessionAuditLogger.Instance.OnAttentionStateChanged(playerId, false, focusedDuration);
                    }
                }
                // User looking back - check restore threshold
                else if (isCurrentlyLooking && timeSinceStateChange >= m_AttentionRestoreThreshold)
                {
                    // Confirm RESTORE
                    m_ConfirmedAttentionStates[playerId] = true;
                    m_LastFocusedStartTime[playerId] = m_AttentionStateChangeTime[playerId];
                    
                    if (SessionAuditLogger.Instance != null)
                    {
                        // Pass the target name we just found
                        SessionAuditLogger.Instance.OnAttentionStateChanged(playerId, true, 0f, currentTargetName);
                    }
                }
            }
            else if (isCurrentlyLooking && confirmedState)
            {
                // Still focused - keep tracking focus start time
                // (focus start time was set when restore was confirmed)
            }
        }

        void UpdateSpeaking(ulong playerId, ref PlayerAnalyticsData data)
        {
            if (!XRINetworkGameManager.Instance.TryGetPlayerByID(playerId, out var player)) return;
            
            if (player.playerVoiceAmp > 0.1f)
            {
                data.SpeakingTimeSeconds += Time.deltaTime;
            }
        }

        void DecaySentiment(ulong playerId, ref PlayerAnalyticsData data)
        {
            if (data.Sentiment != SentimentState.Neutral)
            {
                if (Time.time - data.LastSentimentTime > m_SentimentDecayTime)
                {
                    data.Sentiment = SentimentState.Neutral;
                }
            }
        }

        void UpdateIndicators(ulong playerId, PlayerAnalyticsData data)
        {
            if (!m_Indicators.TryGetValue(playerId, out var indicators)) return;

            // Update sentiment color
            if (indicators.SentimentIcon != null)
            {
                indicators.SentimentIcon.color = data.Sentiment switch
                {
                    SentimentState.Positive => m_PositiveColor,
                    SentimentState.Negative => m_NegativeColor,
                    _ => m_NeutralColor
                };
            }

            // Update gaze color
            if (indicators.GazeIcon != null)
            {
                indicators.GazeIcon.color = data.IsLookingAtTarget 
                    ? m_GazeActiveColor 
                    : m_GazeInactiveColor;
            }

            // Update speaking percentage (as percentage of session time this player was speaking)
            if (indicators.SpeakingText != null)
            {
                float sessionDuration = Time.time - m_SessionStartTime;
                float percentage = 0f;
                
                if (sessionDuration > 1f)
                {
                    // Show how much of the session time this player was speaking
                    percentage = (data.SpeakingTimeSeconds / sessionDuration) * 100f;
                }
                
                percentage = Mathf.Clamp(percentage, 0f, 100f);
                indicators.SpeakingText.text = $"{percentage:F0}%";
                
                // Color based on how much they're talking
                // 0-20% = white (quiet), 20-50% = green (participating), 50%+ = orange (dominating)
                if (percentage > 50f)
                    indicators.SpeakingText.color = new Color(1f, 0.5f, 0.3f); // Orange - dominating
                else if (percentage >= 20f)
                    indicators.SpeakingText.color = new Color(0.3f, 1f, 0.5f); // Green - participating
                else
                    indicators.SpeakingText.color = Color.white; // White - quiet
            }
        }

        public void SetSentiment(ulong playerId, SentimentState sentiment)
        {
            if (m_PlayerData.TryGetValue(playerId, out var data))
            {
                data.Sentiment = sentiment;
                data.LastSentimentTime = Time.time;
                m_PlayerData[playerId] = data;
            }
        }

        public void SetGazeTarget(Transform target)
        {
            m_GazeTarget = target;
        }

        void CleanupAll()
        {
            foreach (var kvp in m_GestureDetectors)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            foreach (var kvp in m_Indicators)
            {
                if (kvp.Value.Container != null) Destroy(kvp.Value.Container);
            }
            m_GestureDetectors.Clear();
            m_Indicators.Clear();
            m_PlayerData.Clear();
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("VRMP/Setup People Analytics")]
        public static void SetupAnalytics()
        {
            // Find NetworkManager
            var networkManager = FindFirstObjectByType<NetworkManager>();
            if (networkManager == null)
            {
                Debug.LogError("[AnalyticsManager] No NetworkManager found in scene!");
                return;
            }

            // Check if already exists
            if (networkManager.GetComponent<AnalyticsManager>() != null)
            {
                Debug.Log("[AnalyticsManager] Already set up!");
                return;
            }

            // Add component
            var manager = networkManager.gameObject.AddComponent<AnalyticsManager>();
            UnityEditor.EditorUtility.SetDirty(networkManager.gameObject);
            Debug.Log("[AnalyticsManager] Setup complete! Added to NetworkManager.");
        }
#endif
    }
}
