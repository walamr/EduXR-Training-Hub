using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;

namespace XRMultiplayer.Recording
{
    public class MeetingPlaybackManager : NetworkBehaviour
    {
        public static MeetingPlaybackManager Instance { get; private set; }

        [Header("Playback Transform")]
        [SerializeField] private Transform m_TableTopTransform;
        
        [Header("Scale Settings")]
        [Tooltip("Overall scale of the entire playback scene (container transform)")]
        [SerializeField] private float m_OverallScale = 0.1f;
        [SerializeField] private float m_MinScale = 0.02f;
        [SerializeField] private float m_MaxScale = 0.5f;
        
        [Tooltip("Additional scale multiplier for avatars (1.0 = match overall scale)")]
        [SerializeField] private float m_AvatarScale = 1.0f;
        
        [Tooltip("Additional scale multiplier for furniture prefabs (shrinks full-size assets)")]
        [SerializeField] private float m_FurnitureScale = 0.1f;
        
        [Header("Avatar Prefabs")]
        [Tooltip("The Player Avatar prefab (XRI_Network_Player_Avatar). Uses the same avatar as multiplayer players.")]
        [SerializeField] private GameObject m_PlayerAvatarPrefab;
        [Tooltip("Fallback: Simple miniature avatar prefab if player avatar not assigned")]
        [SerializeField] private GameObject m_MiniatureAvatarPrefab;
        
        [Header("Miniature Scene Prefabs")]
        [Tooltip("Assign the Table Black prefab from Conference Room Vol.1")]
        [SerializeField] private GameObject m_TablePrefab;
        [Tooltip("Assign the Chair Black prefab from Conference Room Vol.1")]
        [SerializeField] private GameObject m_ChairPrefab;
        [SerializeField] private int m_ChairCount = 6;

        /// <summary>
        /// Current overall scale. Use SetPlaybackScale() to change at runtime.
        /// </summary>
        public float CurrentScale => m_OverallScale;

        private RecordingData m_LoadedSession;
        private Coroutine m_PlaybackCoroutine;
        private AudioSource m_AudioSource;
        private Vector3 m_RecordedTableCenter; // Cached table center from recording

        private Dictionary<ulong, MiniatureAvatar> m_SpawnedAvatars = new Dictionary<ulong, MiniatureAvatar>();
        private GameObject m_MiniatureTable; // Table/chairs for context
        private float m_LastAppliedScale; // Track last scale to detect Inspector changes

        // ========== NETWORK SYNCHRONIZATION ==========
        // NetworkVariables for synchronized playback state
        private NetworkVariable<bool> m_NetIsPlaying = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<ulong> m_NetPlaybackInitiator = new NetworkVariable<ulong>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Local state for network playback
        private bool m_IsInitiator = false; // Am I the one who started this playback?
        private float m_NetworkPlaybackStartTime;
        private float m_NetworkPlaybackDuration;
        private List<NetworkPlayerInfo> m_NetworkPlayerInfos = new List<NetworkPlayerInfo>();

        // ========== AUDIO STREAMING ==========
        private AudioClip m_LoadedAudioClip;
        private float[] m_AudioSamples;
        private int m_AudioSampleRate;
        private int m_AudioChannels;
        private int m_CurrentAudioSampleIndex = 0;
        private const int AUDIO_CHUNK_SIZE = 2048; // Samples per RPC

        // For receivers: streaming audio buffer
        private AudioClip m_StreamingAudioClip;
        private int m_StreamingWritePosition = 0;
        private bool m_StreamingAudioStarted = false;


        /// <summary>
        /// Returns true if any playback is currently active (local or network).
        /// </summary>
        public bool IsPlaybackActive => m_NetIsPlaying.Value || m_PlaybackCoroutine != null;

        /// <summary>
        /// Called when Inspector values change. Updates playback scale in real-time.
        /// </summary>
        private void OnValidate()
        {
            // Only update if scale actually changed and we're playing
            if (Application.isPlaying && m_MiniatureTable != null && m_OverallScale != m_LastAppliedScale)
            {
                SetPlaybackScale(m_OverallScale);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            m_AudioSource = gameObject.AddComponent<AudioSource>();
            m_AudioSource.spatialBlend = 0f; // 2D sound for Master Mix so it's clear
            m_LastAppliedScale = m_OverallScale;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_NetIsPlaying.OnValueChanged += OnNetIsPlayingChanged;
            
            // If we're joining late and playback is active, we'll receive setup via RPC
            if (m_NetIsPlaying.Value && !m_IsInitiator)
            {
                Debug.Log("[MeetingPlaybackManager] Late joiner detected active playback - awaiting frame data");
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            m_NetIsPlaying.OnValueChanged -= OnNetIsPlayingChanged;
            CleanupPlayback();
        }

        private void OnNetIsPlayingChanged(bool previousValue, bool newValue)
        {
            if (!newValue && previousValue)
            {
                // Playback stopped by initiator - cleanup locally
                if (!m_IsInitiator)
                {
                    Debug.Log("[MeetingPlaybackManager] Playback stopped by initiator - cleaning up");
                    CleanupPlayback();
                }
            }
        }

        /// <summary>
        /// Starts playback from local storage and broadcasts to all participants.
        /// Anyone can call this - reads from their own local files and streams to others.
        /// </summary>
        public void PlayRecording(string fileName)
        {
            // Check for conflict with active voting
            if (XRMultiplayer.VotingManager.Instance != null && 
                XRMultiplayer.VotingManager.Instance.IsVotingActive)
            {
                Debug.LogWarning("[MeetingPlaybackManager] Cannot play recording while voting is active.");
                return;
            }

            // Check if playback is already active
            if (m_NetIsPlaying.Value)
            {
                Debug.LogWarning("[MeetingPlaybackManager] Playback already in progress. Please wait for it to finish.");
                return;
            }

            string path = Path.Combine(Application.persistentDataPath, "MeetingRecordings", fileName);
            if (!File.Exists(path))
            {
                Debug.LogError($"Recording file not found: {path}");
                return;
            }

            string json = File.ReadAllText(path);
            m_LoadedSession = JsonUtility.FromJson<RecordingData>(json);

            // Cleanup previous playback FIRST (before setting m_IsInitiator)
            CleanupPlayback();

            // Mark ourselves as the initiator AFTER cleanup
            m_IsInitiator = true;

            // Update network state - need to request owner to update NetworkVariables
            if (IsOwner)
            {
                m_NetIsPlaying.Value = true;
                m_NetPlaybackInitiator.Value = NetworkManager.Singleton.LocalClientId;
            }
            else
            {
                // Request the owner to update state
                RequestSetPlaybackStateRpc(true, NetworkManager.Singleton.LocalClientId);
            }

            // Start playback coroutine which handles audio loading and broadcasting
            m_PlaybackCoroutine = StartCoroutine(NetworkedPlaybackRoutine(fileName));
        }

        [Rpc(SendTo.Owner)]
        private void RequestSetPlaybackStateRpc(bool isPlaying, ulong initiatorId)
        {
            m_NetIsPlaying.Value = isPlaying;
            m_NetPlaybackInitiator.Value = initiatorId;
        }

        public void StopPlayback()
        {
            // Only the initiator can stop a networked playback
            if (m_NetIsPlaying.Value && !m_IsInitiator)
            {
                Debug.LogWarning("[MeetingPlaybackManager] Only the player who started playback can stop it.");
                return;
            }

            // If we're the initiator, broadcast stop to everyone
            if (m_IsInitiator && m_NetIsPlaying.Value)
            {
                BroadcastPlaybackStopRpc();
                
                // Update network state
                if (IsOwner)
                {
                    m_NetIsPlaying.Value = false;
                    m_NetPlaybackInitiator.Value = 0;
                }
                else
                {
                    RequestSetPlaybackStateRpc(false, 0);
                }
            }

            CleanupPlayback();
            m_IsInitiator = false;
        }

        /// <summary>
        /// Internal cleanup without network checks - used by both initiator and receivers.
        /// </summary>
        private void CleanupPlayback()
        {
            if (m_PlaybackCoroutine != null)
            {
                StopCoroutine(m_PlaybackCoroutine);
                m_PlaybackCoroutine = null;
            }
            
            // Destroy spawned avatars (local objects)
            foreach (var kvp in m_SpawnedAvatars)
            {
                if (kvp.Value != null)
                {
                    Destroy(kvp.Value.gameObject);
                }
            }
            m_SpawnedAvatars.Clear();
            
            // Destroy miniature table
            if (m_MiniatureTable != null)
            {
                Destroy(m_MiniatureTable);
                m_MiniatureTable = null;
            }
            
            m_AudioSource.Stop();
            
            // Cleanup audio streaming
            m_AudioSamples = null;
            m_LoadedAudioClip = null;
            m_CurrentAudioSampleIndex = 0;
            if (m_StreamingAudioClip != null)
            {
                Destroy(m_StreamingAudioClip);
                m_StreamingAudioClip = null;
            }
            m_StreamingWritePosition = 0;
            m_StreamingAudioStarted = false;
            
            m_IsInitiator = false;
        }

        // ========== NETWORK RPCs ==========

        /// <summary>
        /// Broadcast playback setup to all clients. Creates table and avatars on each client.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void BroadcastPlaybackSetupRpc(NetworkPlayerInfo[] players, float duration, Vector3 tablePosition, Quaternion tableRotation, ChairData[] chairs, int audioSampleRate, int audioChannels, int totalAudioSamples)
        {
            Debug.Log($"[MeetingPlaybackManager] Received playback setup: {players.Length} players, {duration}s, {chairs.Length} chairs, audio: {audioSampleRate}Hz {audioChannels}ch");
            
            m_NetworkPlaybackStartTime = Time.time;
            m_NetworkPlaybackDuration = duration;
            m_NetworkPlayerInfos.Clear();
            m_NetworkPlayerInfos.AddRange(players);
            
            // Initialize audio streaming for receivers (not initiator)
            if (!m_IsInitiator && audioSampleRate > 0 && totalAudioSamples > 0)
            {
                InitializeAudioReceiver(audioSampleRate, audioChannels, totalAudioSamples);
            }
            
            // If we're the initiator, we already set up locally via PlayRecording
            if (m_IsInitiator) return;
            
            // Set up playback scene locally (table and avatars) with correct position, rotation and chairs
            SetupPlaybackSceneFromNetwork(players, tablePosition, tableRotation, chairs);
        }

        /// <summary>
        /// Initialize audio streaming receiver to play incoming audio chunks.
        /// </summary>
        private void InitializeAudioReceiver(int sampleRate, int channels, int totalSamples)
        {
            m_AudioSampleRate = sampleRate;
            m_AudioChannels = channels;
            m_StreamingWritePosition = 0;
            m_StreamingAudioStarted = false;
            
            // Create streaming audio clip with enough length
            int samplesPerChannel = totalSamples / channels + sampleRate; // Add 1 second buffer
            m_StreamingAudioClip = AudioClip.Create("StreamingPlaybackAudio", 
                samplesPerChannel,
                channels, 
                sampleRate, 
                false);
            
            m_AudioSource.clip = m_StreamingAudioClip;
            m_AudioSource.loop = false;
            
            Debug.Log($"[MeetingPlaybackManager] Initialized audio receiver: {sampleRate}Hz, {channels}ch, buffer for {(float)samplesPerChannel/sampleRate:F1}s");
        }

        /// <summary>
        /// Broadcast audio samples from initiator to all other clients.
        /// </summary>
        [Rpc(SendTo.NotMe)]
        private void BroadcastAudioChunkRpc(float[] samples)
        {
            if (m_StreamingAudioClip == null || samples == null || samples.Length == 0) return;
            
            // Write samples to the streaming audio clip
            int samplesToWrite = samples.Length / m_AudioChannels;
            if (m_StreamingWritePosition + samplesToWrite <= m_StreamingAudioClip.samples)
            {
                m_StreamingAudioClip.SetData(samples, m_StreamingWritePosition);
                m_StreamingWritePosition += samplesToWrite;
            }
            
            // Start playing after we have enough buffer (0.3 seconds worth)
            if (!m_StreamingAudioStarted && m_StreamingWritePosition > m_AudioSampleRate * 0.3f)
            {
                m_AudioSource.Play();
                m_StreamingAudioStarted = true;
                Debug.Log("[MeetingPlaybackManager] Started streaming audio playback");
            }
        }

        /// <summary>
        /// Broadcast frame data from initiator to all other clients.
        /// </summary>
        [Rpc(SendTo.NotMe)]
        private void BroadcastFrameDataRpc(NetworkPlayerFrame[] frames)
        {
            // Apply frame data to local avatars
            ApplyNetworkFrameData(frames);
        }

        /// <summary>
        /// Broadcast playback stop to all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void BroadcastPlaybackStopRpc()
        {
            Debug.Log("[MeetingPlaybackManager] Received playback stop broadcast");
            CleanupPlayback();
        }

        /// <summary>
        /// Sets up the playback scene on non-initiator clients.
        /// </summary>
        private void SetupPlaybackSceneFromNetwork(NetworkPlayerInfo[] players, Vector3 tablePosition, Quaternion tableRotation, ChairData[] chairs)
        {
            // Create miniature table with received position, rotation and chairs
            SceneGeometry geometry = new SceneGeometry 
            { 
                tablePosition = tablePosition,
                tableRotation = tableRotation,
                chairs = new System.Collections.Generic.List<ChairData>(chairs)
            };
            CreateMiniatureTable(geometry);
            SetPlaybackScale(m_OverallScale);
            
            // Create avatars for each recorded player
            foreach (var playerInfo in players)
            {
                GameObject prefabToUse = m_PlayerAvatarPrefab != null ? m_PlayerAvatarPrefab : m_MiniatureAvatarPrefab;
                if (prefabToUse == null) continue;
                
                GameObject go = Instantiate(prefabToUse, m_MiniatureTable.transform);
                go.name = $"PlaybackAvatar_{playerInfo.playerName}";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * m_AvatarScale;
                
                MiniatureAvatar avatar = go.GetComponent<MiniatureAvatar>();
                if (avatar == null) avatar = go.AddComponent<MiniatureAvatar>();
                
                var networkPlayer = go.GetComponent<XRINetworkPlayer>();
                if (networkPlayer != null)
                {
                    avatar.head = networkPlayer.head;
                    avatar.leftHand = networkPlayer.leftHand;
                    avatar.rightHand = networkPlayer.rightHand;
                }
                else
                {
                    if (avatar.head == null) avatar.head = go.transform.Find("Head");
                    if (avatar.leftHand == null) avatar.leftHand = go.transform.Find("LeftHand");
                    if (avatar.rightHand == null) avatar.rightHand = go.transform.Find("RightHand");
                }
                
                DisableNetworkComponents(go);
                
                var avatarIK = go.GetComponentInChildren<XRAvatarIK>();
                if (avatarIK != null)
                {
                    var field = typeof(XRAvatarIK).GetField("m_HeadHeightOffset", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null) field.SetValue(avatarIK, 0f);
                }
                
                var nameTag = go.GetComponentInChildren<PlayerNameTag>();
                if (nameTag != null) Destroy(nameTag.gameObject);
                
                if (avatar.head != null)
                    CreatePlaybackNameLabel(avatar.head, playerInfo.playerName.ToString());
                
                if (avatar.head == null)
                {
                    GameObject fallbackHead = new GameObject("Head");
                    fallbackHead.transform.SetParent(go.transform);
                    avatar.head = fallbackHead.transform;
                }
                
                m_SpawnedAvatars[playerInfo.clientId] = avatar;
            }
        }

        /// <summary>
        /// Applies frame data received via network to local avatars.
        /// </summary>
        private void ApplyNetworkFrameData(NetworkPlayerFrame[] frames)
        {
            if (m_MiniatureTable == null) return;
            Transform container = m_MiniatureTable.transform;
            
            foreach (var frame in frames)
            {
                if (!m_SpawnedAvatars.TryGetValue(frame.playerId, out MiniatureAvatar avatar)) continue;
                if (avatar == null || avatar.head == null) continue;
                
                // Apply positions/rotations (already relative to table center)
                Vector3 headWorld = container.TransformPoint(frame.headPos);
                Vector3 leftHandWorld = container.TransformPoint(frame.leftHandPos);
                Vector3 rightHandWorld = container.TransformPoint(frame.rightHandPos);
                
                avatar.head.position = headWorld;
                avatar.head.rotation = frame.headRot;
                
                if (avatar.leftHand != null)
                {
                    avatar.leftHand.position = leftHandWorld;
                    avatar.leftHand.rotation = frame.leftHandRot;
                }
                
                if (avatar.rightHand != null)
                {
                    avatar.rightHand.position = rightHandWorld;
                    avatar.rightHand.rotation = frame.rightHandRot;
                }
            }
        }

        /// <summary>
        /// Networked playback routine - initiator reads local data and streams to all clients.
        /// </summary>
        private IEnumerator NetworkedPlaybackRoutine(string fileName)
        {
            float duration = m_LoadedSession.duration;
            
            // Cache table center
            if (m_LoadedSession.sceneGeometry != null && m_LoadedSession.sceneGeometry.tablePosition != Vector3.zero)
            {
                m_RecordedTableCenter = m_LoadedSession.sceneGeometry.tablePosition;
            }
            else if (m_LoadedSession.playerRecordings.Count > 0 && m_LoadedSession.playerRecordings[0].frames.Count > 0)
            {
                var firstFrame = m_LoadedSession.playerRecordings[0].frames[0];
                m_RecordedTableCenter = new Vector3(firstFrame.headPos.x, 0, firstFrame.headPos.z);
            }
            else
            {
                m_RecordedTableCenter = Vector3.zero;
            }
            
            // ========== LOAD AUDIO FOR STREAMING ==========
            string audioFile = m_LoadedSession.audioFileName;
            if (string.IsNullOrEmpty(audioFile))
            {
                audioFile = Path.GetFileNameWithoutExtension(fileName) + ".wav";
            }
            string audioPath = Path.Combine(Application.persistentDataPath, "MeetingRecordings", audioFile);
            
            // Load audio if file exists
            if (File.Exists(audioPath))
            {
                yield return LoadAudioForStreaming(audioPath);
            }
            
            // ========== BUILD PLAYER INFO AND BROADCAST SETUP ==========
            var playerInfos = new NetworkPlayerInfo[m_LoadedSession.playerRecordings.Count];
            for (int i = 0; i < m_LoadedSession.playerRecordings.Count; i++)
            {
                playerInfos[i] = new NetworkPlayerInfo
                {
                    clientId = m_LoadedSession.playerRecordings[i].networkClientId,
                    playerName = new Unity.Collections.FixedString64Bytes(m_LoadedSession.playerRecordings[i].playerName)
                };
            }

            Quaternion tableRotation = m_LoadedSession.sceneGeometry?.tableRotation ?? Quaternion.identity;
            Vector3 tablePosition = m_LoadedSession.sceneGeometry?.tablePosition ?? Vector3.zero;
            ChairData[] chairs = m_LoadedSession.sceneGeometry?.chairs?.ToArray() ?? new ChairData[0];
            
            // Audio info for streaming
            int audioSampleRate = m_LoadedAudioClip != null ? m_LoadedAudioClip.frequency : 0;
            int audioChannels = m_LoadedAudioClip != null ? m_LoadedAudioClip.channels : 0;
            int totalAudioSamples = m_AudioSamples != null ? m_AudioSamples.Length : 0;
            
            // Broadcast setup to all clients (including audio info)
            BroadcastPlaybackSetupRpc(playerInfos, duration, tablePosition, tableRotation, chairs, 
                audioSampleRate, audioChannels, totalAudioSamples);
            
            // ========== CREATE LOCAL SCENE ==========
            CreateMiniatureTable(m_LoadedSession.sceneGeometry);
            SetPlaybackScale(m_OverallScale);
            
            foreach (var playerRecord in m_LoadedSession.playerRecordings)
            {
                GameObject prefabToUse = m_PlayerAvatarPrefab != null ? m_PlayerAvatarPrefab : m_MiniatureAvatarPrefab;
                if (prefabToUse == null) continue;
                
                GameObject go = Instantiate(prefabToUse, m_MiniatureTable.transform);
                go.name = $"PlaybackAvatar_{playerRecord.playerName}";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * m_AvatarScale;
                
                MiniatureAvatar avatar = go.GetComponent<MiniatureAvatar>();
                if (avatar == null) avatar = go.AddComponent<MiniatureAvatar>();
                
                var networkPlayer = go.GetComponent<XRINetworkPlayer>();
                if (networkPlayer != null)
                {
                    avatar.head = networkPlayer.head;
                    avatar.leftHand = networkPlayer.leftHand;
                    avatar.rightHand = networkPlayer.rightHand;
                }
                else
                {
                    if (avatar.head == null) avatar.head = go.transform.Find("Head");
                    if (avatar.leftHand == null) avatar.leftHand = go.transform.Find("LeftHand");
                    if (avatar.rightHand == null) avatar.rightHand = go.transform.Find("RightHand");
                }
                
                DisableNetworkComponents(go);
                
                var avatarIK = go.GetComponentInChildren<XRAvatarIK>();
                if (avatarIK != null)
                {
                    var field = typeof(XRAvatarIK).GetField("m_HeadHeightOffset", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null) field.SetValue(avatarIK, 0f);
                }
                
                var nameTag = go.GetComponentInChildren<PlayerNameTag>();
                if (nameTag != null) Destroy(nameTag.gameObject);
                
                CreatePlaybackNameLabel(avatar.head, playerRecord.playerName);
                
                if (avatar.head == null)
                {
                    GameObject fallbackHead = new GameObject("Head");
                    fallbackHead.transform.SetParent(go.transform);
                    avatar.head = fallbackHead.transform;
                }
                
                m_SpawnedAvatars[playerRecord.networkClientId] = avatar;
            }
            
            // ========== PLAY AUDIO LOCALLY ON INITIATOR ==========
            if (m_LoadedAudioClip != null)
            {
                m_AudioSource.clip = m_LoadedAudioClip;
                m_AudioSource.volume = 1f;
                m_AudioSource.loop = false;
                m_AudioSource.Play();
                Debug.Log($"[MeetingPlaybackManager] Started local audio playback: {m_LoadedAudioClip.length:F1}s");
            }
            
            // ========== MAIN PLAYBACK LOOP ==========
            float startTime = Time.time;
            
            while (Time.time - startTime < duration)
            {
                float t = Time.time - startTime;
                
                // Update local avatars
                UpdateAvatars(t);
                
                // Build frame data and broadcast to other clients
                var frames = new NetworkPlayerFrame[m_LoadedSession.playerRecordings.Count];
                for (int i = 0; i < m_LoadedSession.playerRecordings.Count; i++)
                {
                    var playerRecord = m_LoadedSession.playerRecordings[i];
                    RecordingFrame frame = GetFrameAtTime(playerRecord, t);
                    
                    // Calculate relative positions for network transfer
                    frames[i] = new NetworkPlayerFrame
                    {
                        playerId = playerRecord.networkClientId,
                        headPos = frame.headPos - m_RecordedTableCenter,
                        headRot = frame.headRot,
                        leftHandPos = frame.leftHandPos - m_RecordedTableCenter,
                        leftHandRot = frame.leftHandRot,
                        rightHandPos = frame.rightHandPos - m_RecordedTableCenter,
                        rightHandRot = frame.rightHandRot
                    };
                }
                
                BroadcastFrameDataRpc(frames);
                
                // ========== STREAM AUDIO CHUNKS ==========
                if (m_AudioSamples != null && m_CurrentAudioSampleIndex < m_AudioSamples.Length)
                {
                    int samplesToSend = Mathf.Min(AUDIO_CHUNK_SIZE, m_AudioSamples.Length - m_CurrentAudioSampleIndex);
                    float[] chunk = new float[samplesToSend];
                    System.Array.Copy(m_AudioSamples, m_CurrentAudioSampleIndex, chunk, 0, samplesToSend);
                    
                    BroadcastAudioChunkRpc(chunk);
                    m_CurrentAudioSampleIndex += samplesToSend;
                }
                
                yield return null;
            }
            
            // Playback finished naturally
            StopPlayback();
        }

        /// <summary>
        /// Loads audio file and extracts samples for network streaming.
        /// </summary>
        private IEnumerator LoadAudioForStreaming(string path)
        {
            using (var uwr = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
            {
                yield return uwr.SendWebRequest();
                if (uwr.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    m_LoadedAudioClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(uwr);
                    if (m_LoadedAudioClip != null && m_LoadedAudioClip.samples > 0)
                    {
                        m_AudioSampleRate = m_LoadedAudioClip.frequency;
                        m_AudioChannels = m_LoadedAudioClip.channels;
                        
                        // Extract all samples for streaming
                        m_AudioSamples = new float[m_LoadedAudioClip.samples * m_LoadedAudioClip.channels];
                        m_LoadedAudioClip.GetData(m_AudioSamples, 0);
                        m_CurrentAudioSampleIndex = 0;
                        
                        Debug.Log($"[MeetingPlaybackManager] Loaded audio for streaming: {m_LoadedAudioClip.length:F1}s, {m_AudioSampleRate}Hz, {m_AudioChannels}ch, {m_AudioSamples.Length} samples");
                    }
                }
                else
                {
                    Debug.LogWarning($"[MeetingPlaybackManager] Failed to load audio for streaming: {uwr.error}");
                }
            }
        }

        /// <summary>
        /// Adjusts the scale of the entire playback scene at runtime.
        /// Scales the container transform which includes table, chairs, and avatars.
        /// </summary>
        /// <param name="newScale">New scale value (clamped to min/max range)</param>
        public void SetPlaybackScale(float newScale)
        {
            m_OverallScale = Mathf.Clamp(newScale, m_MinScale, m_MaxScale);
            
            // Scale the entire container - this scales everything together
            if (m_MiniatureTable != null)
            {
                m_MiniatureTable.transform.localScale = Vector3.one * m_OverallScale;
            }
            
            // Track applied scale for Inspector change detection
            m_LastAppliedScale = m_OverallScale;
            
            Debug.Log($"[MeetingPlaybackManager] Playback scale set to {m_OverallScale}");
        }

        /// <summary>
        /// Increases playback scale by a step amount.
        /// </summary>
        public void ScaleUp(float step = 0.02f)
        {
            SetPlaybackScale(m_OverallScale + step);
        }

        /// <summary>
        /// Decreases playback scale by a step amount.
        /// </summary>
        public void ScaleDown(float step = 0.02f)
        {
            SetPlaybackScale(m_OverallScale - step);
        }

        private IEnumerator PlaybackRoutine()
        {
            float duration = m_LoadedSession.duration;
            float startTime = Time.time;
            
            // Cache the recorded table center for avatar position calculations
            if (m_LoadedSession.sceneGeometry != null && m_LoadedSession.sceneGeometry.tablePosition != Vector3.zero)
            {
                m_RecordedTableCenter = m_LoadedSession.sceneGeometry.tablePosition;
                Debug.Log($"[MeetingPlaybackManager] Using recorded table center: {m_RecordedTableCenter}");
            }
            else
            {
                // Fallback: estimate table center from first player's initial position
                if (m_LoadedSession.playerRecordings.Count > 0 && m_LoadedSession.playerRecordings[0].frames.Count > 0)
                {
                    var firstFrame = m_LoadedSession.playerRecordings[0].frames[0];
                    m_RecordedTableCenter = new Vector3(firstFrame.headPos.x, 0, firstFrame.headPos.z);
                }
                else
                {
                    m_RecordedTableCenter = Vector3.zero;
                }
                Debug.Log($"[MeetingPlaybackManager] No recorded geometry, using estimated center: {m_RecordedTableCenter}");
            }
            
            // Create miniature table for context using recorded geometry if available
            CreateMiniatureTable(m_LoadedSession.sceneGeometry);
            
            // Apply initial scale to container
            SetPlaybackScale(m_OverallScale);
            
            // Instantiate Avatars - prefer player avatar (with IK), fallback to miniature
            foreach (var playerRecord in m_LoadedSession.playerRecordings)
            {
                // Prefer player avatar for full visual fidelity with IK body animation
                // Fallback to MiniatureAvatar if player avatar not assigned
                GameObject prefabToUse = m_PlayerAvatarPrefab != null ? m_PlayerAvatarPrefab : m_MiniatureAvatarPrefab;
                
                if (prefabToUse == null)
                {
                    Debug.LogError("[MeetingPlaybackManager] No avatar prefab assigned!");
                    yield break;
                }

                // Instantiate avatar parented to the container (so it scales with everything)
                GameObject go = Instantiate(prefabToUse, m_MiniatureTable.transform);
                go.name = $"PlaybackAvatar_{playerRecord.playerName}";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * m_AvatarScale;
                
                // Create MiniatureAvatar wrapper to store transform references
                MiniatureAvatar avatar = go.GetComponent<MiniatureAvatar>();
                if (avatar == null)
                {
                    avatar = go.AddComponent<MiniatureAvatar>();
                }
                
                // Cache transform references from XRINetworkPlayer BEFORE destroying it
                var networkPlayer = go.GetComponent<XRINetworkPlayer>();
                if (networkPlayer != null)
                {
                    avatar.head = networkPlayer.head;
                    avatar.leftHand = networkPlayer.leftHand;
                    avatar.rightHand = networkPlayer.rightHand;
                }
                else
                {
                    // MiniatureAvatar prefab - transforms are already assigned or find by name
                    if (avatar.head == null) avatar.head = go.transform.Find("Head");
                    if (avatar.leftHand == null) avatar.leftHand = go.transform.Find("LeftHand");
                    if (avatar.rightHand == null) avatar.rightHand = go.transform.Find("RightHand");
                }
                
                // Destroy network components (they require NetworkManager context)
                // NOTE: Keep XRAvatarIK - it animates the body based on head transform!
                DisableNetworkComponents(go);
                
                // Zero out XRAvatarIK's head height offset so head isn't pushed down
                // (XRAvatarIK normally offsets head visuals down by 0.3 for neck)
                var avatarIK = go.GetComponentInChildren<XRAvatarIK>();
                if (avatarIK != null)
                {
                    // Use reflection to set private m_HeadHeightOffset to 0
                    var field = typeof(XRAvatarIK).GetField("m_HeadHeightOffset", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        field.SetValue(avatarIK, 0f);
                    }
                }
                
                // Handle PlayerNameTag - destroy it (has complex XRINetworkPlayer dependencies)
                // and create a simple 3D text label instead
                var nameTag = go.GetComponentInChildren<PlayerNameTag>();
                if (nameTag != null)
                {
                    Destroy(nameTag.gameObject);
                }
                
                // Create simple floating name label above head
                CreatePlaybackNameLabel(avatar.head, playerRecord.playerName);
                
                // Create fallback head if not found
                if (avatar.head == null)
                {
                    Debug.LogWarning($"[MeetingPlaybackManager] Could not find head transform for {playerRecord.playerName}");
                    GameObject fallbackHead = new GameObject("Head");
                    fallbackHead.transform.SetParent(go.transform);
                    avatar.head = fallbackHead.transform;
                }
                
                m_SpawnedAvatars.Add(playerRecord.networkClientId, avatar);
                Debug.Log($"[MeetingPlaybackManager] Spawned playback avatar for {playerRecord.playerName} (using {prefabToUse.name})");
            }

            while (Time.time - startTime < duration)
            {
                float t = Time.time - startTime;
                UpdateAvatars(t);
                yield return null;
            }

            StopPlayback();
        }

        private void UpdateAvatars(float time)
        {
            if (m_MiniatureTable == null) return;
            
            Transform container = m_MiniatureTable.transform;
            
            foreach (var playerRecord in m_LoadedSession.playerRecordings)
            {
                if (!m_SpawnedAvatars.ContainsKey(playerRecord.networkClientId)) continue;
                
                MiniatureAvatar avatar = m_SpawnedAvatars[playerRecord.networkClientId];
                if (avatar == null || avatar.head == null) continue;
                
                RecordingFrame frame = GetFrameAtTime(playerRecord, time);

                // Calculate positions RELATIVE to recorded table center
                Vector3 headRelative = frame.headPos - m_RecordedTableCenter;
                Vector3 leftHandRelative = frame.leftHandPos - m_RecordedTableCenter;
                Vector3 rightHandRelative = frame.rightHandPos - m_RecordedTableCenter;
                
                // Convert to WORLD positions using container's transform
                // This applies the container's position, rotation, and scale (miniature effect)
                // IMPORTANT: XRAvatarIK reads head.position (world position) to animate the body!
                Vector3 headWorld = container.TransformPoint(headRelative);
                Vector3 leftHandWorld = container.TransformPoint(leftHandRelative);
                Vector3 rightHandWorld = container.TransformPoint(rightHandRelative);
                
                // Set head world position and rotation
                // XRAvatarIK.Update() will read this and animate the body
                avatar.head.position = headWorld;
                avatar.head.rotation = frame.headRot;
                
                // Set hand world positions and rotations
                if (avatar.leftHand != null)
                {
                    avatar.leftHand.position = leftHandWorld;
                    avatar.leftHand.rotation = frame.leftHandRot;
                }
                
                if (avatar.rightHand != null)
                {
                    avatar.rightHand.position = rightHandWorld;
                    avatar.rightHand.rotation = frame.rightHandRot;
                }
            }
        }

        private RecordingFrame GetFrameAtTime(PlayerRecording recording, float time)
        {
            // Simple linear search or interpolation. For now, find closest previous frame.
            // Optimization: Track last index per player.
            
            if (recording.frames.Count == 0) return new RecordingFrame();
            
            // For MVP, just iterate/binary search.
            // Or assume 30fps and index = time * 30.
            
            // Linear scan for now from 0 is slow.
            // Let's just grab last known good.
            // Better: List.FindIndex or just a rough approximation
            int index = (int)(time * 30); // Assuming 30fps
            index = Mathf.Clamp(index, 0, recording.frames.Count - 1);
            
            // Validate time match roughly?
            if (recording.frames[index].time > time)
            {
                // Search backwards
                while(index > 0 && recording.frames[index].time > time) index--;
            }
            else
            {
                // Search forwards
                while(index < recording.frames.Count - 1 && recording.frames[index+1].time <= time) index++;
            }
            
            return recording.frames[index];
        }

        /// <summary>
        /// Disables/destroys network-related components on instantiated player avatar to prevent network errors.
        /// IMPORTANT: Keeps XRAvatarIK component - it animates the body based on head transform position.
        /// </summary>
        private void DisableNetworkComponents(GameObject go)
        {
            // Destroy XRAvatarVisuals first (it subscribes to XRINetworkPlayer events)
            var avatarVisuals = go.GetComponent<XRAvatarVisuals>();
            if (avatarVisuals != null)
            {
                Destroy(avatarVisuals);
            }
            
            // Destroy XRINetworkPlayer (extends NetworkBehaviour)
            var networkPlayer = go.GetComponent<XRINetworkPlayer>();
            if (networkPlayer != null)
            {
                Destroy(networkPlayer);
            }
            
            // Destroy all NetworkBehaviour components
            var networkBehaviours = go.GetComponents<Unity.Netcode.NetworkBehaviour>();
            foreach (var nb in networkBehaviours)
            {
                Destroy(nb);
            }
            
            // Destroy NetworkObject last
            var networkObject = go.GetComponent<Unity.Netcode.NetworkObject>();
            if (networkObject != null)
            {
                Destroy(networkObject);
            }
            
            // NOTE: XRAvatarIK is intentionally NOT destroyed!
            // It reads head transform position and animates the body accordingly.
            // This gives us full body animation during playback.
        }

        /// <summary>
        /// Creates a simple floating text label above an avatar's head for playback.
        /// </summary>
        private void CreatePlaybackNameLabel(Transform headTransform, string playerName)
        {
            if (headTransform == null || string.IsNullOrEmpty(playerName)) return;
            
            // Create canvas for the label (world space)
            GameObject canvasObj = new GameObject($"PlaybackNameLabel_{playerName}");
            canvasObj.transform.SetParent(headTransform, false);
            canvasObj.transform.localPosition = new Vector3(0, 0.7f, 0); // Above head (higher)
            
            // Add Canvas component
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            
            // Scale canvas to appropriate size (in world units, will be scaled by container)
            RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(200, 50);
            canvasObj.transform.localScale = Vector3.one * 0.005f; // Appropriate scale for world space
            
            // Add CanvasScaler for consistent sizing
            canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            
            // Create text object
            GameObject textObj = new GameObject("NameText");
            textObj.transform.SetParent(canvasObj.transform, false);
            
            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            
            // Add TextMeshPro UGUI component
            var textMesh = textObj.AddComponent<TMPro.TextMeshProUGUI>();
            textMesh.text = playerName;
            textMesh.fontSize = 24;
            textMesh.alignment = TMPro.TextAlignmentOptions.Center;
            textMesh.color = Color.white;
            textMesh.fontStyle = TMPro.FontStyles.Bold;
            
            // Add background for visibility (with rounded corners)
            GameObject bgObj = new GameObject("Background");
            bgObj.transform.SetParent(canvasObj.transform, false);
            bgObj.transform.SetSiblingIndex(0); // Behind text
            
            RectTransform bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-10, -5);
            bgRect.offsetMax = new Vector2(10, 5);
            
            var bgImage = bgObj.AddComponent<UnityEngine.UI.Image>();
            bgImage.color = new Color(0, 0, 0, 0.7f); // Semi-transparent black
            bgImage.sprite = CreateRoundedRectSprite(128, 48, 12); // Rounded corners
            bgImage.type = UnityEngine.UI.Image.Type.Sliced;
            bgImage.pixelsPerUnitMultiplier = 1f;
            
            // Make it face camera (billboard style)
            var billboard = canvasObj.AddComponent<PlaybackLabelBillboard>();
            billboard.Initialize(Camera.main);
        }

        /// <summary>
        /// Creates a rounded rectangle sprite at runtime.
        /// </summary>
        private Sprite CreateRoundedRectSprite(int width, int height, int cornerRadius)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[width * height];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Check if pixel is in rounded corner area
                    bool inCorner = false;
                    float dist = 0;
                    
                    // Bottom-left corner
                    if (x < cornerRadius && y < cornerRadius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(cornerRadius, cornerRadius));
                        inCorner = true;
                    }
                    // Bottom-right corner
                    else if (x >= width - cornerRadius && y < cornerRadius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(width - cornerRadius - 1, cornerRadius));
                        inCorner = true;
                    }
                    // Top-left corner
                    else if (x < cornerRadius && y >= height - cornerRadius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(cornerRadius, height - cornerRadius - 1));
                        inCorner = true;
                    }
                    // Top-right corner
                    else if (x >= width - cornerRadius && y >= height - cornerRadius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(width - cornerRadius - 1, height - cornerRadius - 1));
                        inCorner = true;
                    }
                    
                    if (inCorner && dist > cornerRadius)
                    {
                        pixels[y * width + x] = Color.clear;
                    }
                    else
                    {
                        pixels[y * width + x] = Color.white;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            // Create sprite with 9-slice borders
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(cornerRadius, cornerRadius, cornerRadius, cornerRadius));
        }

        /// <summary>
        /// Recursively searches for a child transform by name.
        /// </summary>
        private Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    return child;
                
                Transform result = FindChildRecursive(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        private IEnumerator LoadAndPlayAudio(string path)
        {
            if (!File.Exists(path)) yield break;
            
            // Load via WWW for WAV support
            using (var uwr = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
            {
                yield return uwr.SendWebRequest();
                if (uwr.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(uwr);
                    if (clip == null || clip.length <= 0.01f)
                    {
                        Debug.LogWarning($"[MeetingPlaybackManager] Loaded audio clip is empty: {path}");
                    }
                    else
                    {
                        float peak = ComputePeak(clip);
                        Debug.Log($"[MeetingPlaybackManager] Playing audio {path} ({clip.length:F1}s, peak={peak:F3})");
                        m_AudioSource.clip = clip;
                        m_AudioSource.volume = 1f;
                        m_AudioSource.loop = false;
                        m_AudioSource.Play();
                    }
                }
                else
                {
                    Debug.LogError($"Failed to load audio: {uwr.error}");
                }
            }
        }

        private float ComputePeak(AudioClip clip)
        {
            if (clip == null) return 0f;
            int sampleCount = Mathf.Min(clip.samples * clip.channels, 48000); // first ~0.5s at 48k
            float[] samples = new float[sampleCount];
            clip.GetData(samples, 0);
            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > peak) peak = abs;
            }
            return peak;
        }

        /// <summary>
        /// Creates a miniature boardroom table with chairs for visual context during playback.
        /// Uses recorded scene geometry if available for accurate positioning.
        /// </summary>
        private void CreateMiniatureTable(SceneGeometry geometry)
        {
            if (m_TableTopTransform == null) return;
            
            Vector3 origin = m_TableTopTransform.position;
            
            // Create container - this will be scaled by SetPlaybackScale
            m_MiniatureTable = new GameObject("MiniatureTableSetup");
            m_MiniatureTable.transform.position = origin;
            
            // =========== LIGHTING ===========
            // Add a point light to illuminate the playback scene
            GameObject lightObj = new GameObject("PlaybackLight");
            lightObj.transform.SetParent(m_MiniatureTable.transform);
            lightObj.transform.localPosition = new Vector3(0, 2f, 0); // Above the table
            Light playbackLight = lightObj.AddComponent<Light>();
            playbackLight.type = LightType.Point;
            playbackLight.color = new Color(1f, 0.95f, 0.9f); // Warm white
            playbackLight.intensity = 1.5f;
            playbackLight.range = 5f; // Will scale with container
            playbackLight.shadows = LightShadows.Soft;
            
            // Calculate table center offset (to position miniature correctly)
            Vector3 recordedTableCenter = (geometry != null && geometry.tablePosition != Vector3.zero) 
                ? geometry.tablePosition 
                : Vector3.zero;
            
            // Furniture scale: just the multiplier to shrink full-size prefabs to proportional size
            // Container scaling (SetPlaybackScale) handles overall miniature scale
            float furnitureBaseScale = m_FurnitureScale;
            
            // =========== TABLE ===========
            if (m_TablePrefab != null)
            {
                // Use actual Conference Room table prefab
                GameObject table = Instantiate(m_TablePrefab, m_MiniatureTable.transform);
                table.name = "MiniTable";
                table.transform.localScale = Vector3.one * furnitureBaseScale;
                table.transform.localPosition = Vector3.zero;
                
                // Apply recorded rotation if available
                if (geometry != null && geometry.tableRotation != Quaternion.identity)
                {
                    table.transform.localRotation = geometry.tableRotation;
                }
                
                // Disable colliders on miniature
                foreach (var col in table.GetComponentsInChildren<Collider>())
                {
                    col.enabled = false;
                }
                
                Debug.Log($"[MeetingPlaybackManager] Created miniature table with base scale {furnitureBaseScale}");
            }
            else
            {
                // Fallback: Create rectangular table using a cube (at real-world proportions)
                float tableLength = 4.0f;
                float tableWidth = 1.4f;
                float tableHeight = 0.05f;
                
                GameObject tableTop = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tableTop.name = "Table_Fallback";
                tableTop.transform.SetParent(m_MiniatureTable.transform);
                tableTop.transform.localPosition = Vector3.zero;
                tableTop.transform.localScale = new Vector3(tableWidth, tableHeight, tableLength);
                
                Material tableMat = new Material(Shader.Find("Standard"));
                tableMat.color = new Color(0.12f, 0.10f, 0.08f);
                tableMat.SetFloat("_Glossiness", 0.7f);
                tableMat.SetFloat("_Metallic", 0.1f);
                tableTop.GetComponent<Renderer>().material = tableMat;
                Destroy(tableTop.GetComponent<Collider>());
            }
            
            // =========== CHAIRS ===========
            // Use recorded chair positions if available
            bool useRecordedChairs = geometry != null && geometry.chairs != null && geometry.chairs.Count > 0;
            
            if (useRecordedChairs)
            {
                Debug.Log($"[MeetingPlaybackManager] Using {geometry.chairs.Count} recorded chair positions");
                
                // Create chairs at recorded positions (relative to table center, no additional scaling since container handles it)
                for (int i = 0; i < geometry.chairs.Count; i++)
                {
                    Vector3 relativePos = geometry.chairs[i].position - recordedTableCenter;
                    
                    if (m_ChairPrefab != null)
                    {
                        GameObject chair = Instantiate(m_ChairPrefab, m_MiniatureTable.transform);
                        chair.name = $"MiniChair_{i + 1}";
                        chair.transform.localPosition = relativePos;
                        chair.transform.localRotation = geometry.chairs[i].rotation;
                        chair.transform.localScale = Vector3.one * furnitureBaseScale;
                        
                        foreach (var col in chair.GetComponentsInChildren<Collider>())
                        {
                            col.enabled = false;
                        }
                    }
                    else
                    {
                        CreateFallbackChair(relativePos, geometry.chairs[i].rotation, i + 1);
                    }
                }
            }
            else
            {
                // Fallback: Generate chair positions in a grid pattern (real-world proportions)
                float tableLength = 4.0f;
                float tableWidth = 1.4f;
                int chairsPerSide = 7;
                float chairSpacing = tableLength / (chairsPerSide + 1);
                float chairOffset = (tableWidth / 2) + 0.4f;
                
                List<Vector3> chairPositions = new List<Vector3>();
                List<float> chairRotations = new List<float>();
                
                for (int i = 0; i < chairsPerSide; i++)
                {
                    float zPos = -tableLength / 2 + chairSpacing * (i + 1);
                    chairPositions.Add(new Vector3(-chairOffset, 0, zPos));
                    chairRotations.Add(90f);
                }
                
                for (int i = 0; i < chairsPerSide; i++)
                {
                    float zPos = -tableLength / 2 + chairSpacing * (i + 1);
                    chairPositions.Add(new Vector3(chairOffset, 0, zPos));
                    chairRotations.Add(-90f);
                }
            
                int chairsToSpawn = Mathf.Min(m_ChairCount, chairPositions.Count);
            
                for (int i = 0; i < chairsToSpawn; i++)
                {
                    if (m_ChairPrefab != null)
                    {
                        // Use actual Conference Room chair prefab
                        GameObject chair = Instantiate(m_ChairPrefab, m_MiniatureTable.transform);
                        chair.name = $"MiniChair_{i + 1}";
                        chair.transform.localPosition = chairPositions[i];
                        chair.transform.localRotation = Quaternion.Euler(0, chairRotations[i], 0);
                        chair.transform.localScale = Vector3.one * furnitureBaseScale;
                        
                        // Disable colliders
                        foreach (var col in chair.GetComponentsInChildren<Collider>())
                        {
                            col.enabled = false;
                        }
                    }
                    else
                    {
                        CreateFallbackChair(chairPositions[i], Quaternion.Euler(0, chairRotations[i], 0), i + 1);
                    }
                }
                
                Debug.Log($"[MeetingPlaybackManager] Created miniature boardroom scene with {chairsToSpawn} chairs");
            }
        }

        /// <summary>
        /// Creates a simple fallback chair using primitives.
        /// </summary>
        private void CreateFallbackChair(Vector3 localPosition, Quaternion rotation, int index)
        {
            GameObject chairRoot = new GameObject($"Chair_Fallback_{index}");
            chairRoot.transform.SetParent(m_MiniatureTable.transform);
            chairRoot.transform.localPosition = localPosition;
            chairRoot.transform.localRotation = rotation;
            
            // Chair dimensions (real-world proportions, container scaling handles size)
            // Seat
            GameObject seat = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seat.name = "Seat";
            seat.transform.SetParent(chairRoot.transform);
            seat.transform.localPosition = new Vector3(0, 0.45f, 0);
            seat.transform.localScale = new Vector3(0.45f, 0.05f, 0.45f);
            
            // Back
            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Back";
            back.transform.SetParent(chairRoot.transform);
            back.transform.localPosition = new Vector3(0, 0.75f, -0.2f);
            back.transform.localScale = new Vector3(0.42f, 0.55f, 0.05f);
            
            Material chairMat = new Material(Shader.Find("Standard"));
            chairMat.color = new Color(0.1f, 0.1f, 0.12f); // Dark gray/black
            seat.GetComponent<Renderer>().material = chairMat;
            back.GetComponent<Renderer>().material = chairMat;
            
            Destroy(seat.GetComponent<Collider>());
            Destroy(back.GetComponent<Collider>());
        }
    }
}

