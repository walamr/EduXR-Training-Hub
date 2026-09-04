using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;
using XRMultiplayer; // For XRINetworkPlayer
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Main transcription controller. Captures audio, sends to Google Gemini,
    /// and broadcasts results to all players.
    /// </summary>
    [RequireComponent(typeof(GeminiService))]
    public class TranscriptionManager : NetworkBehaviour
    {
        #region Singleton
        public static TranscriptionManager Instance { get; private set; }
        #endregion

        #region Serialized Fields
        [Header("Configuration")]
        [Header("Gemini Configuration")]
        [SerializeField, Tooltip("Google AI Studio API Key")]
        private string apiKey = "REDACTED_GEMINI_KEY";

        [SerializeField, Tooltip("Seconds of audio to buffer before sending")]
        private float audioBufferSeconds = 30f; // Increased to allow 15s batches (Gemini Free Tier)

        [SerializeField, Tooltip("Minimum volume to trigger transcription")]
        private float volumeThreshold = 0.01f;

        [SerializeField, Tooltip("Force-send captured audio at least this often (seconds) so the transcript feels live")]
        private float maxBatchIntervalSeconds = 5f;

        [Header("References")]
        [SerializeField] private TranscriptPanel transcriptPanel;

        [Header("Events")]
        public UnityEvent<bool> OnTranscriptionStateChanged;
        public UnityEvent<string> OnSummaryReceived;
        #endregion

        #region Status Colors
        private static readonly Color k_ColorReady = Color.white;
        private static readonly Color k_ColorRecording = new Color(1f, 0.3f, 0.3f);
        private static readonly Color k_ColorStopped = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color k_ColorError = new Color(1f, 0.6f, 0.1f);
        private static readonly Color k_ColorProcessing = new Color(0.4f, 0.8f, 1f);
        #endregion

        #region Private Fields
        private GeminiService geminiService;
        private MeetingTranscript currentTranscript;
        private bool isTranscribing;
        private AudioClip micClip;
        private int lastSamplePosition;
        private float[] audioBuffer;
        private int audioBufferIndex;
        private float silenceTimer;
        private float debugTimer;
        private string currentMicDevice;
        private TranscriptionLanguage currentLanguage = TranscriptionLanguage.English;
        private float timeSinceLastSend;
        private bool wasVoiced;
        private float busyLogTimer;
        private const int SAMPLE_RATE = 16000;
        private const float SILENCE_THRESHOLD = 1.5f;
        private const string k_DebugPrepend = "<color=#00FF7F>[Transcription]</color> ";
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
        }

        private void Start()
        {
            // Get or create GeminiService
            geminiService = GetComponent<GeminiService>();
            if (geminiService == null)
            {
                geminiService = gameObject.AddComponent<GeminiService>();
            }

            // Set API key if configured
            if (!string.IsNullOrEmpty(apiKey))
            {
                geminiService.SetApiKey(apiKey);
            }

            // Subscribe to events
            geminiService.OnTranscriptionResult += OnGeminiResult;
            geminiService.OnSummaryResult += OnGeminiSummary;
            geminiService.OnError += OnGeminiError;
            geminiService.OnStatusUpdate += OnGeminiStatus;
            geminiService.OnRequestFinished += OnGeminiRequestFinished;

            // Initialize audio buffer
            int bufferSize = (int)(SAMPLE_RATE * audioBufferSeconds);
            audioBuffer = new float[bufferSize];
        }

        private void Update()
        {
            if (isTranscribing && micClip != null)
            {
                ProcessMicrophoneInput();
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            
            if (Instance == this)
            {
                Instance = null;
            }

            if (geminiService != null)
            {
                geminiService.OnTranscriptionResult -= OnGeminiResult;
                geminiService.OnSummaryResult -= OnGeminiSummary;
                geminiService.OnError -= OnGeminiError;
                geminiService.OnStatusUpdate -= OnGeminiStatus;
                geminiService.OnRequestFinished -= OnGeminiRequestFinished;
            }

            StopTranscription();
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Sets the transcript panel reference.
        /// </summary>
        public void SetTranscriptPanel(TranscriptPanel panel)
        {
            transcriptPanel = panel;
            Utils.Log($"{k_DebugPrepend}TranscriptPanel assigned: {(panel != null ? "Yes" : "No")}");
        }

        /// <summary>
        /// Selects the language used for speech recognition and summaries.
        /// Can be changed before or during recording.
        /// </summary>
        public void SetLanguage(TranscriptionLanguage language)
        {
            currentLanguage = language;

            if (geminiService == null)
            {
                geminiService = GetComponent<GeminiService>();
            }
            geminiService?.SetLanguage(language);

            Utils.Log($"{k_DebugPrepend}Language set to {language.DisplayName()}");
        }

        /// <summary>The currently selected transcription language.</summary>
        public TranscriptionLanguage CurrentLanguage => currentLanguage;

        /// <summary>
        /// Starts transcription recording.
        /// </summary>
        public void StartTranscription()
        {
            if (isTranscribing) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            // Quest/Android: microphone permission must be granted before recording.
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Utils.Log($"{k_DebugPrepend}Microphone permission not granted - requesting...", 1);
                transcriptPanel?.SetStatus("Microphone permission required", k_ColorError);

                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ =>
                {
                    Utils.Log($"{k_DebugPrepend}Microphone permission granted - starting transcription.");
                    StartTranscription();
                };
                callbacks.PermissionDenied += _ =>
                {
                    Utils.Log($"{k_DebugPrepend}Microphone permission DENIED.", 2);
                    transcriptPanel?.SetStatus("Microphone permission required", k_ColorError);
                };
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
                return;
            }
#endif

            // HOST ONLY restriction to save API quota
            // We allow if:
            // 1. Offline mode (NetworkManager is null or not listening)
            // 2. We are the Session Owner (Host in Distributed Authority)
            bool isOnline = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
            bool isSessionOwner = XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
            
            if (isOnline && !isSessionOwner)
            {
                Utils.Log($"{k_DebugPrepend}Transcription is restricted to HOST only to save API quota.");
                return;
            }

            string roomName = GetRoomName();
            Utils.Log($"{k_DebugPrepend}Starting transcription for room: {roomName}");

            // Create new transcript
            currentTranscript = new MeetingTranscript(roomName);

            // List available microphones
            string[] devices = Microphone.devices;
            Utils.Log($"{k_DebugPrepend}Available microphones ({devices.Length}):");
            for (int i = 0; i < devices.Length; i++)
            {
                Utils.Log($"{k_DebugPrepend}  [{i}] {devices[i]}");
            }

            // Start microphone - platform-aware device selection
            // In Editor: use first device (usually Realtek PC mic - Oculus Virtual only works with Link)
            // On Device: prefer Oculus/Headset device
            if (devices.Length > 0)
            {
                string deviceName = devices[0]; // Default to first device
                
#if !UNITY_EDITOR
                // On actual device (Quest), prefer Oculus/Headset microphone
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].ToLower().Contains("oculus") || devices[i].ToLower().Contains("headset"))
                    {
                        deviceName = devices[i];
                        break;
                    }
                }
#else
                Utils.Log($"{k_DebugPrepend}Editor detected - using default microphone (Oculus Virtual requires Link)");
#endif
                Utils.Log($"{k_DebugPrepend}Using microphone: {deviceName}");
                
                // Stop any existing recording first
                if (Microphone.IsRecording(deviceName))
                {
                    Utils.Log($"{k_DebugPrepend}Microphone already recording, stopping first...");
                    Microphone.End(deviceName);
                }

                // Store device name for later use
                currentMicDevice = deviceName;
                micClip = Microphone.Start(deviceName, true, 10, SAMPLE_RATE);
                
                // Wait for microphone to be ready
                int timeout = 100;
                while (Microphone.GetPosition(deviceName) <= 0 && timeout > 0)
                {
                    timeout--;
                    System.Threading.Thread.Sleep(10);
                }
                
                if (timeout <= 0)
                {
                    Utils.Log($"{k_DebugPrepend}Microphone failed to start!", 2);
                    transcriptPanel?.SetStatus("Mic not available", k_ColorError);
                    OnTranscriptionStateChanged?.Invoke(false);
                    return;
                }
                
                Utils.Log($"{k_DebugPrepend}Mic started ({deviceName})");
                lastSamplePosition = 0;
                audioBufferIndex = 0;
                timeSinceLastSend = 0f;
                wasVoiced = false;
            }
            else
            {
                Utils.Log($"{k_DebugPrepend}No microphone available!", 2);
                transcriptPanel?.SetStatus("Mic not available", k_ColorError);
                OnTranscriptionStateChanged?.Invoke(false);
                return;
            }

            isTranscribing = true;
            OnTranscriptionStateChanged?.Invoke(true);
            transcriptPanel?.SetStatus("Recording...", k_ColorRecording);
        }

        /// <summary>
        /// Stops transcription recording.
        /// </summary>
        public void StopTranscription()
        {
            if (!isTranscribing) return;

            Utils.Log($"{k_DebugPrepend}Stopping transcription");

            Microphone.End(currentMicDevice);
            currentMicDevice = null;
            micClip = null;
            isTranscribing = false;

            if (currentTranscript != null)
            {
                currentTranscript.End();
            }

            OnTranscriptionStateChanged?.Invoke(false);
            transcriptPanel?.SetStatus("Stopped", k_ColorStopped);
        }

        /// <summary>
        /// Toggles transcription on/off.
        /// </summary>
        public void ToggleTranscription()
        {
            if (isTranscribing)
                StopTranscription();
            else
                StartTranscription();
        }

        /// <summary>
        /// Gets the current meeting transcript.
        /// </summary>
        public MeetingTranscript GetCurrentTranscript() => currentTranscript;

        /// <summary>
        /// Raised on a (non-host) client whenever a transcript line is received from the host.
        /// Used by the "Catch Me Up" feature to detect that a meeting is in progress.
        /// </summary>
        public event System.Action OnRemoteTranscriptReceived;

        /// <summary>True once this client has received at least one transcript line from the host.</summary>
        public bool HasRemoteTranscriptActivity { get; private set; }

        /// <summary>
        /// Host-side: builds the transcript text for entries at or after <paramref name="sinceUtcTicks"/>.
        /// Pass 0 (or less) for the whole meeting. Used to generate "catch me up" recaps.
        /// </summary>
        public string GetTranscriptTextSince(long sinceUtcTicks)
        {
            if (currentTranscript == null) return string.Empty;

            var since = sinceUtcTicks <= 0
                ? System.DateTime.MinValue
                : new System.DateTime(sinceUtcTicks, System.DateTimeKind.Utc);

            var sb = new System.Text.StringBuilder();
            foreach (var entry in currentTranscript.entries)
            {
                if (entry.timestamp >= since)
                    sb.AppendLine($"[{entry.speakerName}]: {entry.text}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Requests a summary of the current transcript from Gemini.
        /// </summary>
        public void RequestSummary()
        {
            string fullText = currentTranscript != null ? currentTranscript.GetFullText() : null;

            // Nothing captured yet -> show it in the STATUS LINE, never as a popup.
            // (The popup is reserved for real summaries; this also guarantees a stray
            // AI-button trigger can never interrupt the user with an empty popup.)
            if (string.IsNullOrWhiteSpace(fullText) || fullText.Trim().Length < 10)
            {
                Utils.Log($"{k_DebugPrepend}No transcript to summarize yet.");
                transcriptPanel?.SetStatus("No transcript to summarize yet", k_ColorError);
                return;
            }

            if (geminiService == null)
            {
                transcriptPanel?.SetStatus("AI service not available", k_ColorError);
                return;
            }

            Utils.Log($"{k_DebugPrepend}Requesting summary for {fullText.Length} chars...");
            geminiService.GenerateSummary(fullText);
        }

        /// <summary>
        /// Whether transcription is currently active.
        /// </summary>
        public bool IsTranscribing => isTranscribing;
        #endregion

        #region Private Methods
        private void ProcessMicrophoneInput()
        {
            int currentPosition = Microphone.GetPosition(currentMicDevice);
            if (currentPosition < 0 || micClip == null) return;

            // Calculate samples to read
            int samplesToRead;
            if (currentPosition < lastSamplePosition)
            {
                samplesToRead = (micClip.samples - lastSamplePosition) + currentPosition;
            }
            else
            {
                samplesToRead = currentPosition - lastSamplePosition;
            }

            if (samplesToRead <= 0) return;

            // Read samples
            float[] samples = new float[samplesToRead];
            micClip.GetData(samples, lastSamplePosition);
            lastSamplePosition = currentPosition;

            // Check volume
            float maxVolume = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > maxVolume) maxVolume = abs;
            }

            // Periodic debug logging
            debugTimer += Time.deltaTime;
            if (debugTimer > 2f)
            {
                debugTimer = 0f;
                Utils.Log($"{k_DebugPrepend}Capturing audio - level: {maxVolume:F4}, buffer: {audioBufferIndex}/{audioBuffer.Length}, threshold: {volumeThreshold}");
            }

            // Add to buffer
            bool hasVoice = maxVolume > volumeThreshold;
            timeSinceLastSend += Time.deltaTime;

            // Log voice/silence transitions (not every frame).
            if (hasVoice != wasVoiced)
            {
                if (hasVoice)
                    Utils.Log($"{k_DebugPrepend}Speech detected (level {maxVolume:F4})");
                else if (audioBufferIndex > 0)
                    Utils.Log($"{k_DebugPrepend}Silence detected (buffered ~{(float)audioBufferIndex / SAMPLE_RATE:F1}s)");
                wasVoiced = hasVoice;
            }

            if (hasVoice)
            {
                silenceTimer = 0f;
                AddToBuffer(samples);
            }
            else
            {
                silenceTimer += Time.deltaTime;

                // Speech ended: send whatever we have right away (forced, so the
                // 'min batch seconds' quota optimization can't swallow short sentences).
                if (audioBufferIndex > SAMPLE_RATE / 2 && silenceTimer > SILENCE_THRESHOLD)
                {
                    SendBufferForTranscription(force: true);
                }
            }

            // Liveness guarantee: while the user keeps talking, force a batch out every
            // few seconds instead of waiting for silence or a full buffer.
            if (audioBufferIndex > SAMPLE_RATE && timeSinceLastSend >= maxBatchIntervalSeconds)
            {
                SendBufferForTranscription(force: true);
            }

            // Auto-send if buffer is full
            if (audioBufferIndex >= audioBuffer.Length - SAMPLE_RATE)
            {
                SendBufferForTranscription(force: true);
            }
        }

        private void AddToBuffer(float[] samples)
        {
            for (int i = 0; i < samples.Length && audioBufferIndex < audioBuffer.Length; i++)
            {
                audioBuffer[audioBufferIndex++] = samples[i];
            }
        }

        private void SendBufferForTranscription(bool force = false)
        {
            float currentSeconds = (float)audioBufferIndex / SAMPLE_RATE;

            // Quota optimization: batch up audio before sending. Forced sends (silence
            // detected / periodic liveness / full buffer) bypass this so short sentences
            // appear quickly instead of waiting for MinBatchSeconds of speech.
            if (!force)
            {
                float minSeconds = geminiService != null ? geminiService.MinBatchSeconds : 3f;
                if (currentSeconds < minSeconds && audioBufferIndex < audioBuffer.Length - SAMPLE_RATE)
                {
                    return; // wait for more audio
                }
            }

            // Gemini rejects clips shorter than ~0.5s.
            if (audioBufferIndex < SAMPLE_RATE / 2 || geminiService == null)
            {
                return;
            }

            // A request (possibly retrying) is still in flight; keep buffering.
            if (geminiService.IsProcessing)
            {
                busyLogTimer += Time.deltaTime;
                if (busyLogTimer > 2f)
                {
                    busyLogTimer = 0f;
                    Utils.Log($"{k_DebugPrepend}Audio batch ready (~{currentSeconds:F1}s) but service busy - holding");
                }
                return;
            }
            busyLogTimer = 0f;

            Utils.Log($"{k_DebugPrepend}Audio batch ready (~{currentSeconds:F1}s captured)");
            Utils.Log($"{k_DebugPrepend}Sending audio batch ({audioBufferIndex} samples, ~{currentSeconds:F1}s)");
            timeSinceLastSend = 0f;

            // Copy buffer
            float[] audioToSend = new float[audioBufferIndex];
            System.Array.Copy(audioBuffer, audioToSend, audioBufferIndex);

            // Reset buffer
            audioBufferIndex = 0;
            silenceTimer = 0f;

            // Send to Gemini
            geminiService.TranscribeAudio(audioToSend, SAMPLE_RATE);
            transcriptPanel?.SetStatus("Transcribing...", k_ColorProcessing);
        }

        private void OnGeminiResult(string text, float confidence)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Get player info
            string speakerName = GetLocalPlayerName();
            ulong speakerId = GetLocalPlayerId();

            // Create entry
            var entry = new TranscriptionEntry(text, speakerName, speakerId);
            entry.confidence = confidence;
            entry.DetectRTL();

            // Add to transcript
            currentTranscript?.AddEntry(entry);

            // Update UI locally
            transcriptPanel?.AddEntry(entry);
            Utils.Log($"{k_DebugPrepend}[TranscriptPanel] Appended live transcription: {text}");

            // Recover the status label after any transient error on a previous batch.
            if (isTranscribing)
            {
                transcriptPanel?.SetStatus("Recording...", k_ColorRecording);
            }

            // Broadcast to all players
            if (IsSpawned)
            {
                BroadcastTranscriptionServerRpc(text, speakerName, speakerId);
            }
        }

        private void OnGeminiSummary(string summary)
        {
            Utils.Log($"{k_DebugPrepend}Summary received!");
            // Broadcast or display locally? 
            // For now, simple local event. A networked summary board would sync this string.
            OnSummaryReceived?.Invoke(summary);
        }

        private void OnGeminiError(string error)
        {
            Utils.Log($"{k_DebugPrepend}Error: {error}", 2);

            // Surface the SPECIFIC reason in the UI (e.g. "API quota/rate limit", "Server busy",
            // "API key invalid", "Network error", "Invalid audio format") instead of a generic message.
            transcriptPanel?.SetStatus(string.IsNullOrEmpty(error) ? "API request failed" : error, k_ColorError);
        }

        /// <summary>
        /// Transient, non-fatal status from GeminiService (e.g. while retrying a 503).
        /// </summary>
        private void OnGeminiStatus(string message)
        {
            Utils.Log($"{k_DebugPrepend}{message}");
            transcriptPanel?.SetStatus(message, k_ColorError);
        }

        /// <summary>
        /// A transcription request fully finished. On success, restore the normal status
        /// (covers the "no speech in batch" case where no text event fires). On failure,
        /// keep the error message that OnGeminiError already put on screen.
        /// </summary>
        private void OnGeminiRequestFinished(bool success)
        {
            if (!success) return;

            if (isTranscribing)
                transcriptPanel?.SetStatus("Recording...", k_ColorRecording);
            else
                transcriptPanel?.SetStatus("Ready", k_ColorReady);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void BroadcastTranscriptionServerRpc(string text, string speakerName, ulong speakerId)
        {
            Utils.Log($"{k_DebugPrepend}Server received transcription from {speakerName} ({speakerId}). Broadcasting...");
            BroadcastTranscriptionClientRpc(text, speakerName, speakerId);
        }

        [ClientRpc]
        private void BroadcastTranscriptionClientRpc(string text, string speakerName, ulong speakerId)
        {
            // Don't process our own broadcasts (we already added it locally)
            if (speakerId == GetLocalPlayerId()) return;

            Utils.Log($"{k_DebugPrepend}Client received transcription from {speakerName}: {text}");

            var entry = new TranscriptionEntry(text, speakerName, speakerId);
            // We don't need entry.DetectRTL() here because TranscriptPanel.AddEntry handles it internally now
            // But we keep it if other systems use entry.isRTL
            entry.DetectRTL();

            currentTranscript?.AddEntry(entry);
            transcriptPanel?.AddEntry(entry);

            // Signal "a meeting is in progress" so latecomers can be offered a catch-up recap.
            HasRemoteTranscriptActivity = true;
            OnRemoteTranscriptReceived?.Invoke();
        }

        private string GetRoomName()
        {
            return XRINetworkGameManager.ConnectedRoomName?.Value ?? "Offline";
        }

        private string GetLocalPlayerName()
        {
            return XRINetworkGameManager.LocalPlayerName?.Value ?? "Player";
        }

        private ulong GetLocalPlayerId()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
            {
                return NetworkManager.Singleton.LocalClientId;
            }
            return 0;
        }
        #endregion
    }
}
