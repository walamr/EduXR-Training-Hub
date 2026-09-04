using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using Newtonsoft.Json;

namespace XRMultiplayer
{
    /// <summary>
    /// "The Black Box Recorder" - Silently logs all session events to CSV.
    /// Automatically uploads to Firebase Storage when the session ends.
    /// </summary>
    public class SessionAuditLogger : MonoBehaviour
    {
        public static SessionAuditLogger Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private bool m_EnableLogging = true;
        [SerializeField] private float m_SpeakingThreshold = 0.1f;
        [SerializeField] private float m_SpeakingMinDuration = 1f;
        [SerializeField] private bool m_UploadOnSessionEnd = true;

        [Header("Debug")]
        [SerializeField] private bool m_DebugMode = false;

        // CSV data
        private StringBuilder m_CsvBuilder;
        private string m_SessionId;
        private string m_SessionStartTime;
        private string m_CsvFilePath;
        
        // Periodic flush to prevent data loss on crash
        private float m_LastFlushTime = 0f;
        private const float FLUSH_INTERVAL = 30f; // Flush every 30 seconds
        private int m_LastFlushedLineCount = 0;

        // Tracking states
        private readonly Dictionary<ulong, bool> m_SpeakingStates = new();
        private readonly Dictionary<ulong, float> m_SpeakingStartTimes = new();
        private readonly Dictionary<ulong, bool> m_AttentionStates = new();
        private readonly Dictionary<ulong, HeadGestureDetector> m_GestureDetectors = new();
        
        // Store event handlers for proper unsubscription (fixes lambda closure bug)
        private readonly Dictionary<ulong, Action> m_NodHandlers = new();
        private readonly Dictionary<ulong, Action> m_ShakeHandlers = new();
        
        // Cache player names to prevent "Unknown_X" on leave (player destroyed before name lookup)
        private readonly Dictionary<ulong, string> m_PlayerNameCache = new();
        private readonly Dictionary<ulong, string> m_PlayerDeviceCache = new();
        
        // Slide change deduplication (prevents double logging from network sync)
        private int m_LastLoggedPage = -1;
        private int m_LastLoggedTotalPages = -1;
        private string m_LastLoggedFileId = "";
        private float m_LastSlideLogTime = 0f;
        private const float SLIDE_LOG_DEBOUNCE = 0.1f; // 100ms debounce
        
        // Current presentation tracking
        private string m_CurrentFileName = "";
        private string m_CurrentFileId = "";
        private bool m_IsDocumentLoading = false;

        private bool m_IsSessionActive;
        private bool m_WasOriginalHost; // Track if this user was the original session host
        private Presentation.FirebaseStorageManager m_FirebaseStorage;

        private const string CSV_HEADER = "Timestamp,SessionId,EventType,ClientId,PlayerName,DeviceType,Details";

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Find FirebaseStorageManager for uploads
            m_FirebaseStorage = FindFirstObjectByType<Presentation.FirebaseStorageManager>();
            if (m_FirebaseStorage == null && m_DebugMode)
            {
                Debug.Log("<color=#FFD700>[SessionAuditLogger]</color> FirebaseStorageManager not found - upload will be disabled");
            }
        }

        void Start()
        {
            // Subscribe to connection state
            XRINetworkGameManager.Connected.Subscribe(OnConnectionStateChanged);
        }

        void OnDestroy()
        {
            XRINetworkGameManager.Connected.Unsubscribe(OnConnectionStateChanged);
            EndSession();
        }

        void Update()
        {
            if (!m_EnableLogging || !m_IsSessionActive) return;
            
            // Poll speaking states for all tracked players
            UpdateSpeakingStates();
            
            // Periodic flush to prevent data loss on crash
            if (Time.time - m_LastFlushTime >= FLUSH_INTERVAL)
            {
                FlushToDisk();
            }
        }

        #region Session Lifecycle

        private void OnConnectionStateChanged(bool connected)
        {
            if (connected)
            {
                StartSession();
            }
            else
            {
                EndSession();
            }
        }

        private void StartSession()
        {
            if (m_IsSessionActive) return;
            m_IsSessionActive = true;

            m_SessionId = Guid.NewGuid().ToString().Substring(0, 8);
            m_SessionStartTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            m_CsvFilePath = Path.Combine(Application.persistentDataPath, $"AuditLog_{m_SessionStartTime}_{m_SessionId}.csv");

            m_CsvBuilder = new StringBuilder();
            m_CsvBuilder.AppendLine(CSV_HEADER);

            // Subscribe to events
            SubscribeToEvents();

            // Check if we are the original session host (checked after a delay to ensure player is spawned)
            StartCoroutine(CheckOriginalHostStatus());

            LogEvent("SESSION_START", 0, "LocalHost", "N/A", "");
            LogDebug($"Session started: {m_SessionId}");

            // Register existing players after delay
            StartCoroutine(RegisterExistingPlayersDelayed());
        }

        /// <summary>
        /// Checks if the local player is the original session host.
        /// Only the original host should upload the CSV to their Drive.
        /// </summary>
        private System.Collections.IEnumerator CheckOriginalHostStatus()
        {
            // Wait for player to be fully spawned
            yield return new WaitForSeconds(2.0f);

            if (XRINetworkPlayer.LocalPlayer != null)
            {
                m_WasOriginalHost = XRINetworkPlayer.LocalPlayer.IsSessionOwner;
                LogDebug($"Original host check: IsSessionOwner = {m_WasOriginalHost}");
            }
            else
            {
                // No local player yet, assume not original host
                m_WasOriginalHost = false;
                LogDebug("Original host check: LocalPlayer not available, defaulting to false");
            }
        }

        private void EndSession()
        {
            if (!m_IsSessionActive) return;
            m_IsSessionActive = false;

            LogEvent("SESSION_END", 0, "LocalHost", "N/A", "");

            // Unsubscribe from events
            UnsubscribeFromEvents();

            // Save CSV
            SaveCsvFile();

            // Upload to Drive only if enabled AND we were the original session host
            // This prevents migrated hosts from uploading duplicate logs
            if (m_UploadOnSessionEnd && m_WasOriginalHost)
            {
                UploadAuditLog();
                LogDebug("Uploading audit log (original host)");
            }
            else if (m_UploadOnSessionEnd && !m_WasOriginalHost)
            {
                LogDebug("Skipping upload - not the original session host");
            }

            // Cleanup
            m_SpeakingStates.Clear();
            m_SpeakingStartTimes.Clear();
            m_AttentionStates.Clear();
            m_PlayerNameCache.Clear();
            m_PlayerDeviceCache.Clear();
            CleanupGestureDetectors();
            
            // Reset slide tracking
            m_LastLoggedPage = -1;
            m_LastLoggedTotalPages = -1;
            m_LastLoggedFileId = "";
            m_CurrentFileName = "";
            m_CurrentFileId = "";

            LogDebug($"Session ended: {m_SessionId}");
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged += OnPlayerStateChanged;
            }

            // Voice mute (local player)
            var voiceChat = FindFirstObjectByType<VoiceChatManager>();
            if (voiceChat != null)
            {
                voiceChat.selfMuted.Subscribe(OnLocalMuteChanged);
            }

            // Presentation content changes
            var presentationNetwork = FindFirstObjectByType<Presentation.PresentationNetworkManager>();
            if (presentationNetwork != null)
            {
                presentationNetwork.OnPageChanged += OnPageChanged;
                presentationNetwork.OnFileChanged += OnFileLoaded;
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged -= OnPlayerStateChanged;
            }

            var voiceChat = FindFirstObjectByType<VoiceChatManager>();
            if (voiceChat != null)
            {
                voiceChat.selfMuted.Unsubscribe(OnLocalMuteChanged);
            }

            var presentationNetwork = FindFirstObjectByType<Presentation.PresentationNetworkManager>();
            if (presentationNetwork != null)
            {
                presentationNetwork.OnPageChanged -= OnPageChanged;
                presentationNetwork.OnFileChanged -= OnFileLoaded;
            }
        }

        private System.Collections.IEnumerator RegisterExistingPlayersDelayed()
        {
            yield return new WaitForSeconds(2f);

            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            foreach (var player in players)
            {
                if (player != null && player.IsSpawned)
                {
                    RegisterPlayerForTracking(player);
                }
            }
        }

        #endregion

        #region Event Handlers

        private void OnPlayerStateChanged(ulong clientId, bool connected)
        {
            if (connected)
            {
                StartCoroutine(OnPlayerJoinedDelayed(clientId));
            }
            else
            {
                OnPlayerLeft(clientId);
            }
        }

        private System.Collections.IEnumerator OnPlayerJoinedDelayed(ulong clientId)
        {
            yield return new WaitForSeconds(1f);

            string playerName = GetPlayerName(clientId);
            string deviceType = GetDeviceType(clientId);
            
            // Cache the name/device for when player leaves (object may be destroyed)
            m_PlayerNameCache[clientId] = playerName;
            m_PlayerDeviceCache[clientId] = deviceType;

            LogEvent("PLAYER_JOIN", clientId, playerName, deviceType, "");

            // Register for tracking
            if (XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out var player))
            {
                RegisterPlayerForTracking(player);
            }
        }

        private void OnPlayerLeft(ulong clientId)
        {
            // Use cached name/device (player object may already be destroyed)
            string playerName = m_PlayerNameCache.TryGetValue(clientId, out var cachedName) 
                ? cachedName 
                : GetPlayerName(clientId);
            string deviceType = m_PlayerDeviceCache.TryGetValue(clientId, out var cachedDevice) 
                ? cachedDevice 
                : GetDeviceType(clientId);

            // Log final speaking duration if they were speaking
            if (m_SpeakingStates.TryGetValue(clientId, out bool wasSpeaking) && wasSpeaking)
            {
                float duration = Time.time - m_SpeakingStartTimes[clientId];
                if (duration >= m_SpeakingMinDuration)
                {
                    LogEvent("SPEAKING_END", clientId, playerName, deviceType, $"Duration={duration:F1}s");
                }
            }

            LogEvent("PLAYER_LEAVE", clientId, playerName, deviceType, "");
            
            // Clean up caches
            m_PlayerNameCache.Remove(clientId);
            m_PlayerDeviceCache.Remove(clientId);

            // Cleanup tracking
            m_SpeakingStates.Remove(clientId);
            m_SpeakingStartTimes.Remove(clientId);
            m_AttentionStates.Remove(clientId);
            
            if (m_GestureDetectors.TryGetValue(clientId, out var detector))
            {
                if (detector != null)
                {
                    // Use stored handlers for proper unsubscription
                    if (m_NodHandlers.TryGetValue(clientId, out var nodHandler))
                    {
                        detector.OnNodDetected -= nodHandler;
                    }
                    if (m_ShakeHandlers.TryGetValue(clientId, out var shakeHandler))
                    {
                        detector.OnShakeDetected -= shakeHandler;
                    }
                }
                m_GestureDetectors.Remove(clientId);
            }
            m_NodHandlers.Remove(clientId);
            m_ShakeHandlers.Remove(clientId);
        }

        private void RegisterPlayerForTracking(XRINetworkPlayer player)
        {
            ulong clientId = player.OwnerClientId;

            // Initialize states
            m_SpeakingStates[clientId] = false;
            m_SpeakingStartTimes[clientId] = 0f;
            
            // Start attention as TRUE so first lapse requires actual looking away
            // Don't add to attention states yet - let AnalyticsManager initialize it properly
            // This prevents false "0.0 duration" lapses on spawn
            if (!m_AttentionStates.ContainsKey(clientId))
            {
                m_AttentionStates[clientId] = true;
            }

            // Subscribe to mute changes for all players
            player.selfMuted.OnValueChanged += (old, current) => OnPlayerMuteChanged(clientId, current);

            // Setup gesture detection
            var headTransform = player.head;
            if (headTransform != null && !m_GestureDetectors.ContainsKey(clientId))
            {
                var detector = headTransform.GetComponent<HeadGestureDetector>();
                if (detector == null)
                {
                    detector = headTransform.gameObject.AddComponent<HeadGestureDetector>();
                    LogDebug($"Added HeadGestureDetector to player {clientId}");
                }
                else
                {
                    LogDebug($"Found existing HeadGestureDetector on player {clientId}");
                }
                
                // Store handlers so we can properly unsubscribe later
                Action nodHandler = () => OnNodDetected(clientId);
                Action shakeHandler = () => OnShakeDetected(clientId);
                
                detector.OnNodDetected += nodHandler;
                detector.OnShakeDetected += shakeHandler;
                
                m_NodHandlers[clientId] = nodHandler;
                m_ShakeHandlers[clientId] = shakeHandler;
                m_GestureDetectors[clientId] = detector;
            }
        }

        private void OnLocalMuteChanged(bool isMuted)
        {
            if (XRINetworkPlayer.LocalPlayer != null)
            {
                ulong clientId = XRINetworkPlayer.LocalPlayer.OwnerClientId;
                string playerName = GetPlayerName(clientId);
                string deviceType = GetDeviceType(clientId);
                LogEvent("MUTE_TOGGLE", clientId, playerName, deviceType, new Dictionary<string, object>
                {
                    { "muted", isMuted }
                });
            }
        }

        private void OnPlayerMuteChanged(ulong clientId, bool isMuted)
        {
            // Skip local player (already handled by OnLocalMuteChanged)
            if (XRINetworkPlayer.LocalPlayer != null && clientId == XRINetworkPlayer.LocalPlayer.OwnerClientId)
                return;

            string playerName = GetPlayerName(clientId);
            string deviceType = GetDeviceType(clientId);
            LogEvent("MUTE_TOGGLE", clientId, playerName, deviceType, new Dictionary<string, object>
            {
                { "muted", isMuted }
            });
        }

        private void OnLoadingStateChanged(bool isLoading)
        {
            m_IsDocumentLoading = isLoading;
            
            // Reset dedup state when loading starts (new document)
            if (isLoading)
            {
                m_LastLoggedPage = -1;
                m_LastLoggedTotalPages = -1;
            }
        }

        private void OnPageChanged(int currentPage, int totalPages)
        {
            // Skip logging during document loading (total_pages changes as pages load)
            if (m_IsDocumentLoading)
            {
                return;
            }
            
            // Deduplication: Skip if same page/total logged within debounce window
            int displayPage = currentPage + 1;
            float timeSinceLastLog = Time.time - m_LastSlideLogTime;
            
            if (displayPage == m_LastLoggedPage && 
                totalPages == m_LastLoggedTotalPages && 
                timeSinceLastLog < SLIDE_LOG_DEBOUNCE)
            {
                return; // Duplicate event, skip
            }
            
            m_LastLoggedPage = displayPage;
            m_LastLoggedTotalPages = totalPages;
            m_LastSlideLogTime = Time.time;
            
            ulong presenterId = XRINetworkPlayer.LocalPlayer?.OwnerClientId ?? 0;
            string presenterName = GetPlayerName(presenterId);
            string deviceType = GetDeviceType(presenterId);
            
            var details = new Dictionary<string, object>
            {
                { "current_page", displayPage },
                { "total_pages", totalPages }
            };
            
            // Include file info if available
            if (!string.IsNullOrEmpty(m_CurrentFileId))
            {
                details["file_id"] = m_CurrentFileId;
            }
            if (!string.IsNullOrEmpty(m_CurrentFileName))
            {
                details["file_name"] = m_CurrentFileName;
            }
            
            LogEvent("SLIDE_CHANGE", presenterId, presenterName, deviceType, details);
        }

        private void OnFileLoaded(string fileName)
        {
            m_CurrentFileName = fileName;
            m_CurrentFileId = fileName; // Use name as ID for Firebase-based system
            
            // Reset slide tracking for new file
            m_LastLoggedPage = -1;
            m_LastLoggedTotalPages = -1;
            m_LastLoggedFileId = fileName;
            
            ulong presenterId = XRINetworkPlayer.LocalPlayer?.OwnerClientId ?? 0;
            string presenterName = GetPlayerName(presenterId);
            string deviceType = GetDeviceType(presenterId);
            
            var details = new Dictionary<string, object>
            {
                { "file_name", fileName }
            };
            
            LogEvent("FILE_LOAD", presenterId, presenterName, deviceType, details);
        }

        // Gesture debounce to prevent log spam
        private float m_LastNodTime = 0f;
        private float m_LastShakeTime = 0f;
        private const float GESTURE_COOLDOWN = 1.0f;

        private void OnNodDetected(ulong clientId)
        {
            if (Time.time - m_LastNodTime < GESTURE_COOLDOWN) return;
            m_LastNodTime = Time.time;

            string playerName = GetPlayerName(clientId);
            string deviceType = GetDeviceType(clientId);
            LogEvent("CONSENSUS_NOD", clientId, playerName, deviceType, "");
        }

        private void OnShakeDetected(ulong clientId)
        {
            if (Time.time - m_LastShakeTime < GESTURE_COOLDOWN) return;
            m_LastShakeTime = Time.time;

            string playerName = GetPlayerName(clientId);
            string deviceType = GetDeviceType(clientId);
            LogEvent("DISAGREEMENT_SHAKE", clientId, playerName, deviceType, "");
        }

        #endregion

        #region Speaking Detection

        private void UpdateSpeakingStates()
        {
            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            foreach (var player in players)
            {
                if (player == null || !player.IsSpawned) continue;

                ulong clientId = player.OwnerClientId;
                bool isSpeaking = player.playerVoiceAmp > m_SpeakingThreshold;

                if (!m_SpeakingStates.ContainsKey(clientId))
                {
                    m_SpeakingStates[clientId] = false;
                    m_SpeakingStartTimes[clientId] = 0f;
                }

                bool wasSpeaking = m_SpeakingStates[clientId];

                if (isSpeaking && !wasSpeaking)
                {
                    // Started speaking
                    m_SpeakingStates[clientId] = true;
                    m_SpeakingStartTimes[clientId] = Time.time;
                }
                else if (!isSpeaking && wasSpeaking)
                {
                    // Stopped speaking
                    m_SpeakingStates[clientId] = false;
                    float duration = Time.time - m_SpeakingStartTimes[clientId];

                    // Only log if spoke for minimum duration
                    if (duration >= m_SpeakingMinDuration)
                    {
                        string playerName = GetPlayerName(clientId);
                        string deviceType = GetDeviceType(clientId);
                        LogEvent("SPEAKING_CONTRIBUTION", clientId, playerName, deviceType, new Dictionary<string, object>
                        {
                            { "duration_seconds", Math.Round(duration, 1) }
                        });
                    }
                }
            }
        }

        #endregion

        #region Attention Tracking

        /// <summary>
        /// Called by AnalyticsManager when attention state changes (with debounce).
        /// </summary>
        /// <param name="clientId">The player's client ID</param>
        /// <param name="isAttentive">True if now focused, false if lapsed</param>
        /// <param name="focusedDuration">How long they were focused before lapsing (only relevant for LAPSE events)</param>
        public void OnAttentionStateChanged(ulong clientId, bool isAttentive, float focusedDuration = 0f, string focusTarget = "")
        {
            if (!m_AttentionStates.ContainsKey(clientId))
            {
                // First time seeing this player - initialize to current state, don't log
                m_AttentionStates[clientId] = isAttentive;
                return; // Skip logging the initial state
            }

            bool wasAttentive = m_AttentionStates[clientId];
            
            // Skip if no actual change
            if (wasAttentive == isAttentive)
            {
                return;
            }
            
            m_AttentionStates[clientId] = isAttentive;

            if (wasAttentive && !isAttentive)
            {
                // Skip logging if focused duration is less than 0.5s (prevents false lapses on spawn)
                if (focusedDuration < 0.5f)
                {
                    return;
                }
                
                // Attention lapsed
                string playerName = GetPlayerName(clientId);
                string deviceType = GetDeviceType(clientId);
                
                var details = new Dictionary<string, object>
                {
                    { "duration_focused_before_lapse", Math.Round(focusedDuration, 1) }
                };
                
                LogEvent("ATTENTION_LAPSE", clientId, playerName, deviceType, details);
            }
            else if (!wasAttentive && isAttentive)
            {
                // Attention restored
                string playerName = GetPlayerName(clientId);
                string deviceType = GetDeviceType(clientId);
                
                var details = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(focusTarget))
                {
                    details["target"] = focusTarget;
                }
                
                LogEvent("ATTENTION_RESTORED", clientId, playerName, deviceType, details);
            }
        }

        #endregion

        #region Helpers

        private string GetPlayerName(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out var player))
            {
                // Use displayName for unique identification when players have duplicate names
                string name = player.displayName;
                return string.IsNullOrEmpty(name) ? $"User_{clientId}" : name.Trim();
            }
            return $"Unknown_{clientId}";
        }

        private string GetDeviceType(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out var player))
            {
                int platformType = player.platformType.Value;
                return platformType switch
                {
                    0 => "Unknown",
                    1 => "Quest",
                    2 => "PCVR",
                    3 => "Desktop",
                    _ => $"Platform_{platformType}"
                };
            }
            return "Unknown";
        }

        /// <summary>
        /// Escapes a field for CSV format. Handles quotes, commas, and newlines.
        /// </summary>
        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            
            // Replace newlines with spaces
            field = field.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            
            // Check if we need to wrap in quotes
            bool needsQuotes = field.Contains(",") || field.Contains("\"") || field.Contains(" ");
            
            // Escape double quotes by doubling them
            field = field.Replace("\"", "\"\"");
            
            return needsQuotes ? $"\"{field}\"" : field;
        }

        /// <summary>
        /// Logs an event with a string details field (legacy support).
        /// </summary>
        private void LogEvent(string eventType, ulong clientId, string playerName, string deviceType, string details)
        {
            if (m_CsvBuilder == null) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            
            // Escape all fields properly for CSV
            string escapedPlayerName = EscapeCsvField(playerName);
            string escapedDeviceType = EscapeCsvField(deviceType);
            string escapedDetails = EscapeCsvField(details);
            
            string line = $"{timestamp},{m_SessionId},{eventType},{clientId},{escapedPlayerName},{escapedDeviceType},{escapedDetails}";
            
            m_CsvBuilder.AppendLine(line);
            LogDebug($"[AUDIT] {eventType}: {playerName} - {details}");
        }

        /// <summary>
        /// Logs an event with a structured JSON details field.
        /// </summary>
        private void LogEvent(string eventType, ulong clientId, string playerName, string deviceType, Dictionary<string, object> details)
        {
            if (m_CsvBuilder == null) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            
            // Serialize to JSON
            string jsonDetails = details != null && details.Count > 0 
                ? JsonConvert.SerializeObject(details, Formatting.None)
                : "{}";
            
            // Escape all fields properly for CSV
            string escapedPlayerName = EscapeCsvField(playerName);
            string escapedDeviceType = EscapeCsvField(deviceType);
            string escapedDetails = EscapeCsvField(jsonDetails);
            
            string line = $"{timestamp},{m_SessionId},{eventType},{clientId},{escapedPlayerName},{escapedDeviceType},{escapedDetails}";
            
            m_CsvBuilder.AppendLine(line);
            LogDebug($"[AUDIT] {eventType}: {playerName} - {jsonDetails}");
        }

        private void LogDebug(string message)
        {
            if (m_DebugMode)
            {
                Debug.Log($"<color=#FFD700>[SessionAuditLogger]</color> {message}");
            }
        }

        #endregion

        #region File Operations

        private void SaveCsvFile()
        {
            if (m_CsvBuilder == null || string.IsNullOrEmpty(m_CsvFilePath)) return;

            try
            {
                File.WriteAllText(m_CsvFilePath, m_CsvBuilder.ToString());
                m_LastFlushedLineCount = m_CsvBuilder.ToString().Split('\n').Length;
                LogDebug($"Audit log saved: {m_CsvFilePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionAuditLogger] Failed to save CSV: {e.Message}");
            }
        }
        
        /// <summary>
        /// Flush to disk periodically to prevent data loss on crash.
        /// Only writes if there are new lines since last flush.
        /// </summary>
        private void FlushToDisk()
        {
            if (m_CsvBuilder == null || string.IsNullOrEmpty(m_CsvFilePath)) return;
            
            int currentLineCount = m_CsvBuilder.ToString().Split('\n').Length;
            
            // Only flush if there are new lines
            if (currentLineCount > m_LastFlushedLineCount)
            {
                try
                {
                    File.WriteAllText(m_CsvFilePath, m_CsvBuilder.ToString());
                    m_LastFlushedLineCount = currentLineCount;
                    m_LastFlushTime = Time.time;
                    LogDebug($"Flushed {currentLineCount - m_LastFlushedLineCount + 1} new lines to disk");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SessionAuditLogger] Failed to flush CSV: {e.Message}");
                }
            }
            else
            {
                m_LastFlushTime = Time.time; // Update time even if no new lines
            }
        }

        private void UploadAuditLog()
        {
            if (string.IsNullOrEmpty(m_CsvFilePath) || !File.Exists(m_CsvFilePath)) return;

            if (m_FirebaseStorage == null)
            {
                m_FirebaseStorage = FindFirstObjectByType<Presentation.FirebaseStorageManager>();
            }

            if (m_FirebaseStorage != null && m_FirebaseStorage.IsAuthenticated)
            {
                string fileName = Path.GetFileName(m_CsvFilePath);
                // User requested path: /audit_logs/{UID}/audit_logs/{FileName}
                string storagePath = $"audit_logs/{fileName}"; 
                
                // Read file content and upload
                byte[] fileData = File.ReadAllBytes(m_CsvFilePath);
                
                m_FirebaseStorage.UploadFile(storagePath, fileData, "text/csv", (success) => {
                    if (success) LogDebug($"Audit log uploaded: {fileName}");
                    else LogDebug($"Audit log upload failed: {fileName}");
                });
            }
            else
            {
                if (m_FirebaseStorage == null)
                {
                    LogDebug("Upload skipped: FirebaseStorageManager not found");
                }
                else
                {
                    LogDebug("Upload skipped: Not authenticated. Use the Device Code on the TV to log in.");
                }
            }
        }

        private void CleanupGestureDetectors()
        {
            foreach (var kvp in m_GestureDetectors)
            {
                // Don't destroy - AnalyticsManager may be using them
            }
            m_GestureDetectors.Clear();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Manually log a custom event.
        /// </summary>
        public void LogCustomEvent(string eventType, ulong clientId, string details)
        {
            string playerName = GetPlayerName(clientId);
            string deviceType = GetDeviceType(clientId);
            LogEvent(eventType, clientId, playerName, deviceType, details);
        }

        /// <summary>
        /// Force save the current audit log.
        /// </summary>
        [ContextMenu("Force Save Audit Log")]
        public void ForceSave()
        {
            SaveCsvFile();
        }

        /// <summary>
        /// Get the path to the current CSV file.
        /// </summary>
        public string GetCurrentLogPath() => m_CsvFilePath;

        [ContextMenu("Force Upload to Firebase")]
        public void ForceUpload()
        {
            if (string.IsNullOrEmpty(m_CsvFilePath) || !File.Exists(m_CsvFilePath))
            {
                Debug.LogError("[SessionAuditLogger] No CSV file found to upload.");
                return;
            }
            
            LogDebug("Manual upload triggered...");
            UploadAuditLog();
        }

        #endregion
    }
}
