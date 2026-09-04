using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.Audio;
using Unity.Services.Vivox;
using Unity.Services.Vivox.AudioTaps;

namespace XRMultiplayer.Recording
{
    /// <summary>
    /// Records the 3D motion of all players in the session.
    /// Host-Only: Captures data and saves it locally.
    /// </summary>
    public class MeetingRecorder : NetworkBehaviour
    {
        public static MeetingRecorder Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private float m_RecordingFps = 30f;
        [SerializeField] private string m_RecordingSubDir = "MeetingRecordings";

        [Header("Audio Recording")]
        [SerializeField] private AudioListener m_RecordingListener;
        [SerializeField] private AudioMixerGroup m_RecordingMixerGroup;
        [SerializeField] private bool m_RouteAllAudioSourcesToMixer = true;
        [SerializeField] private bool m_RecordMicrophone = true;
        [SerializeField] private string m_PreferredMicrophoneDevice = "";

        private bool m_IsRecording = false;
        private float m_RecordStartTime;
        private float m_TimeSinceLastFrame;
        private float m_FrameInterval;

        private RecordingData m_CurrentSession;
        
        // Cache player references to avoid FindObjects every frame
        private List<XRINetworkPlayer> m_ActivePlayers = new List<XRINetworkPlayer>();
        private SimpleAudioRecorder m_AudioRecorder;
        private string m_ActiveMicrophoneDevice;
        
        // Vivox channel audio tap for capturing all remote players' voices
        private GameObject m_VivoxChannelTapObject;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            m_FrameInterval = 1f / m_RecordingFps;
        }

        public bool StartRecording()
        {
            // In Distributed Authority mode, check session ownership via XRINetworkPlayer
            bool isHost = XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
            
            if (!isHost)
            {
                Debug.LogWarning("[MeetingRecorder] Only Host can record meetings.");
                return false;
            }

            if (m_IsRecording) return true;

            Debug.Log("[MeetingRecorder] Recording Started");
            m_IsRecording = true;
            m_RecordStartTime = Time.time;
            m_TimeSinceLastFrame = 0;

            m_CurrentSession = new RecordingData
            {
                sessionName = $"Meeting_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
                date = DateTime.Now.ToString(),
                playerRecordings = new List<PlayerRecording>()
            };

            // Capture scene geometry (table and chairs) for accurate playback
            CaptureSceneGeometry();

            // Initial player scan
            RefreshPlayerList();

            // Start Audio Recording
            string audioPath = Path.Combine(Application.persistentDataPath, m_RecordingSubDir, $"{m_CurrentSession.sessionName}.wav");
            m_CurrentSession.audioFileName = $"{m_CurrentSession.sessionName}.wav";
            StartAudioRecording(audioPath);
            return true;
        }

        private void StartAudioRecording(string path)
        {
            AudioListener listener = EnsureRecordingListener();
            if (listener == null)
            {
                Debug.LogWarning("[MeetingRecorder] No AudioListener found for recording!");
                return;
            }

            RouteAudioSourcesToRecorder();

            m_AudioRecorder = listener.GetComponent<SimpleAudioRecorder>();
            if (m_AudioRecorder == null)
            {
                m_AudioRecorder = listener.gameObject.AddComponent<SimpleAudioRecorder>();
            }
            
            // Set up microphone capture (silent - no live monitoring)
            // Boost mic volume to 3.0 since mic input is typically quieter than other audio sources
            if (m_RecordMicrophone)
            {
                string device = PickMicrophoneDevice();
                if (!string.IsNullOrEmpty(device))
                {
                    m_ActiveMicrophoneDevice = device;
                    m_AudioRecorder.SetMicrophoneSource(device, 3f);
                    Debug.Log($"[MeetingRecorder] Microphone '{device}' set up for silent recording");
                }
                else
                {
                    Debug.LogWarning("[MeetingRecorder] No microphone device found!");
                }
            }
            
            // Set up Vivox channel audio tap to capture all remote players' voices
            SetupVivoxChannelTap(listener);
            
            m_AudioRecorder.StartRecording(path);
        }

        public void StopRecording()
        {
            if (!m_IsRecording) return;
            m_IsRecording = false;
            
            m_CurrentSession.duration = Time.time - m_RecordStartTime;
            
            if (m_AudioRecorder != null)
            {
                // This also stops microphone capture internally
                m_AudioRecorder.StopRecording();
                Destroy(m_AudioRecorder);
                m_AudioRecorder = null;
            }
            
            // Clean up Vivox channel tap
            CleanupVivoxChannelTap();
            
            // Clear microphone device reference
            m_ActiveMicrophoneDevice = null;

            SaveRecording(m_CurrentSession);
            VerifyAudioFileSize();
            Debug.Log($"Meeting Recording Stopped. Duration: {m_CurrentSession.duration:F2}s");
            
            m_CurrentSession = null;
        }

        public bool IsRecording => m_IsRecording;

        private void Update()
        {
            // Use XRINetworkPlayer for Distributed Authority mode host check
            if (!m_IsRecording) return;
            bool isHost = XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
            if (!isHost) return;

            m_TimeSinceLastFrame += Time.deltaTime;

            if (m_TimeSinceLastFrame >= m_FrameInterval)
            {
                RecordFrame();
                m_TimeSinceLastFrame = 0;
            }
        }

        private void RecordFrame()
        {
            // Occasionally refresh list to catch late joiners? 
            // Better: Subscribe to connection events. For now, simple poll every 60 frames or just cache carefully.
            // Let's rely on cached list but update it if count mismatches or periodically.
            // For robustness in this sprint, let's just find them. It's not *that* expensive for <20 players.
            // Optimization: Update cached list.
            if (Time.frameCount % 60 == 0) RefreshPlayerList();

            float currentTime = Time.time - m_RecordStartTime;

            foreach (var player in m_ActivePlayers)
            {
                if (player == null) continue;

                // Find or create the track for this player
                var record = GetOrCreatePlayerRecording(player);

                // Capture Frame
                RecordingFrame frame = new RecordingFrame
                {
                    time = currentTime,
                    headPos = player.head.position,
                    headRot = player.head.rotation,
                    leftHandPos = player.leftHand.position,
                    leftHandRot = player.leftHand.rotation,
                    rightHandPos = player.rightHand.position,
                    rightHandRot = player.rightHand.rotation
                };

                record.frames.Add(frame);
            }
        }

        private void RefreshPlayerList()
        {
            m_ActivePlayers.Clear();
            var players = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            m_ActivePlayers.AddRange(players);
        }

        private PlayerRecording GetOrCreatePlayerRecording(XRINetworkPlayer player)
        {
            ulong id = player.OwnerClientId;
            foreach (var r in m_CurrentSession.playerRecordings)
            {
                if (r.networkClientId == id) return r;
            }

            // New player track
            PlayerRecording newRecord = new PlayerRecording
            {
                networkClientId = id,
                playerName = player.displayName, // Capture unique name (e.g. "User (2)")
                frames = new List<RecordingFrame>()
            };
            m_CurrentSession.playerRecordings.Add(newRecord);
            return newRecord;
        }

        private void SaveRecording(RecordingData data)
        {
            string json = JsonUtility.ToJson(data, true);
            string dirPath = Path.Combine(Application.persistentDataPath, m_RecordingSubDir);
            
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }

            string filePath = Path.Combine(dirPath, $"{data.sessionName}.json");
            File.WriteAllText(filePath, json);
            Debug.Log($"Recording saved to: {filePath}");
        }

        public string[] GetRecordingFiles()
        {
            string dirPath = Path.Combine(Application.persistentDataPath, m_RecordingSubDir);
            if (!Directory.Exists(dirPath)) return new string[0];
            return Directory.GetFiles(dirPath, "*.json");
        }

        /// <summary>
        /// Captures the positions of the meeting table and chairs for accurate playback.
        /// Looks for objects in the scene hierarchy: Environment > ConferenceRoom_Full > Assets > Table, Chairs
        /// </summary>
        private void CaptureSceneGeometry()
        {
            m_CurrentSession.sceneGeometry = new SceneGeometry();
            
            // Try to find the table by name - check various common naming patterns
            Transform tableTransform = null;
            string[] tableNames = { "Table", "MeetingTable", "ConferenceTable" };
            
            foreach (string tableName in tableNames)
            {
                GameObject tableObj = GameObject.Find(tableName);
                if (tableObj != null)
                {
                    tableTransform = tableObj.transform;
                    break;
                }
            }
            
            // Also try to find via hierarchy (ConferenceRoom_Full > Assets > Table)
            if (tableTransform == null)
            {
                GameObject conferenceRoom = GameObject.Find("ConferenceRoom_Full");
                if (conferenceRoom != null)
                {
                    Transform assets = conferenceRoom.transform.Find("Assets");
                    if (assets != null)
                    {
                        tableTransform = assets.Find("Table");
                    }
                }
            }
            
            if (tableTransform != null)
            {
                m_CurrentSession.sceneGeometry.tablePosition = tableTransform.position;
                m_CurrentSession.sceneGeometry.tableRotation = tableTransform.rotation;
                m_CurrentSession.sceneGeometry.tableScale = tableTransform.lossyScale;
                Debug.Log($"[MeetingRecorder] Captured table position: {tableTransform.position}");
            }
            else
            {
                Debug.LogWarning("[MeetingRecorder] Could not find Table in scene. Using default position.");
                m_CurrentSession.sceneGeometry.tablePosition = Vector3.zero;
                m_CurrentSession.sceneGeometry.tableRotation = Quaternion.identity;
                m_CurrentSession.sceneGeometry.tableScale = Vector3.one;
            }
            
            // Find chairs - look in ConferenceRoom_Full > Assets > Chairs folder
            m_CurrentSession.sceneGeometry.chairs = new System.Collections.Generic.List<ChairData>();
            
            GameObject conferenceRoomForChairs = GameObject.Find("ConferenceRoom_Full");
            if (conferenceRoomForChairs != null)
            {
                Transform assets = conferenceRoomForChairs.transform.Find("Assets");
                if (assets != null)
                {
                    Transform chairsFolder = assets.Find("Chairs");
                    if (chairsFolder != null)
                    {
                        foreach (Transform chair in chairsFolder)
                        {
                            m_CurrentSession.sceneGeometry.chairs.Add(new ChairData
                            {
                                position = chair.position,
                                rotation = chair.rotation
                            });
                        }
                        Debug.Log($"[MeetingRecorder] Captured {m_CurrentSession.sceneGeometry.chairs.Count} chair positions");
                    }
                }
            }
            
            // Fallback: Find all objects named "Chair X"
            if (m_CurrentSession.sceneGeometry.chairs.Count == 0)
            {
                for (int i = 1; i <= 20; i++)
                {
                    GameObject chairObj = GameObject.Find($"Chair {i}");
                    if (chairObj != null)
                    {
                        m_CurrentSession.sceneGeometry.chairs.Add(new ChairData
                        {
                            position = chairObj.transform.position,
                            rotation = chairObj.transform.rotation
                        });
                    }
                }
                if (m_CurrentSession.sceneGeometry.chairs.Count > 0)
                {
                    Debug.Log($"[MeetingRecorder] Found {m_CurrentSession.sceneGeometry.chairs.Count} chairs via fallback search");
                }
            }
        }

        private AudioListener EnsureRecordingListener()
        {
            if (m_RecordingListener != null) return m_RecordingListener;

            m_RecordingListener = FindFirstObjectByType<AudioListener>();
            if (m_RecordingListener != null) return m_RecordingListener;

            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                m_RecordingListener = mainCamera.GetComponent<AudioListener>();
                if (m_RecordingListener == null)
                {
                    m_RecordingListener = mainCamera.gameObject.AddComponent<AudioListener>();
                }
            }
            else
            {
                GameObject go = new GameObject("RecordingAudioListener");
                m_RecordingListener = go.AddComponent<AudioListener>();
            }

            return m_RecordingListener;
        }

        private void RouteAudioSourcesToRecorder()
        {
            if (!m_RouteAllAudioSourcesToMixer || m_RecordingMixerGroup == null) return;

            var audioSources = FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var source in audioSources)
            {
                if (source == null) continue;

                if (source.outputAudioMixerGroup == null)
                {
                    source.outputAudioMixerGroup = m_RecordingMixerGroup;
                }
            }
        }


        private string PickMicrophoneDevice()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0) return null;

            if (!string.IsNullOrEmpty(m_PreferredMicrophoneDevice))
            {
                foreach (string device in Microphone.devices)
                {
                    if (device == m_PreferredMicrophoneDevice)
                    {
                        return device;
                    }
                }
            }

            return Microphone.devices[0];
        }

        /// <summary>
        /// Creates a VivoxChannelAudioTap to capture all remote players' voices.
        /// The tap outputs through an AudioSource which plays to the AudioListener,
        /// allowing the SimpleAudioRecorder to capture it.
        /// </summary>
        private void SetupVivoxChannelTap(AudioListener listener)
        {
            // Check if Vivox is available and we're in a voice channel
            if (VivoxService.Instance == null || !VivoxService.Instance.IsLoggedIn)
            {
                Debug.Log("[MeetingRecorder] Vivox not available, skipping remote voice capture");
                return;
            }

            if (VivoxService.Instance.ActiveChannels == null || VivoxService.Instance.ActiveChannels.Count == 0)
            {
                Debug.Log("[MeetingRecorder] No active Vivox channels, skipping remote voice capture");
                return;
            }

            try
            {
                // Create a new GameObject for the Vivox channel tap
                m_VivoxChannelTapObject = new GameObject("RecordingVivoxChannelTap");
                m_VivoxChannelTapObject.transform.SetParent(listener.transform);
                
                // Add the VivoxChannelAudioTap component (automatically adds AudioSource)
                var channelTap = m_VivoxChannelTapObject.AddComponent<VivoxChannelAudioTap>();
                
                // Set AutoAcquireChannel to true so it automatically captures from the current voice channel
                channelTap.AutoAcquireChannel = true;
                
                // Get the AudioSource that was auto-added
                var audioSource = m_VivoxChannelTapObject.GetComponent<AudioSource>();
                if (audioSource != null)
                {
                    // Configure the AudioSource to play at full volume to the listener
                    audioSource.playOnAwake = true;
                    audioSource.spatialBlend = 0f; // 2D audio (not spatialized)
                    audioSource.volume = 1f;
                    
                    // Route through mixer if available
                    if (m_RecordingMixerGroup != null)
                    {
                        audioSource.outputAudioMixerGroup = m_RecordingMixerGroup;
                    }
                }
                
                Debug.Log("[MeetingRecorder] Vivox channel audio tap created for recording remote players' voices");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeetingRecorder] Failed to create Vivox channel tap: {e.Message}");
                CleanupVivoxChannelTap();
            }
        }

        /// <summary>
        /// Cleans up the VivoxChannelAudioTap when recording stops.
        /// </summary>
        private void CleanupVivoxChannelTap()
        {
            if (m_VivoxChannelTapObject != null)
            {
                Destroy(m_VivoxChannelTapObject);
                m_VivoxChannelTapObject = null;
                Debug.Log("[MeetingRecorder] Vivox channel audio tap destroyed");
            }
        }

        private void VerifyAudioFileSize()
        {
            string dirPath = Path.Combine(Application.persistentDataPath, m_RecordingSubDir);
            string filePath = Path.Combine(dirPath, m_CurrentSession?.audioFileName ?? "");
            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"[MeetingRecorder] Audio file missing: {filePath}");
                return;
            }

            long length = new FileInfo(filePath).Length;
            if (length <= 44)
            {
                Debug.LogWarning($"[MeetingRecorder] Audio file is empty or header-only (size={length}). Check AudioListener/Mixer routing.");
            }
            else
            {
                Debug.Log($"[MeetingRecorder] Audio file saved ({length} bytes): {filePath}");
            }
        }
    }
}

