using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

namespace XRMultiplayer
{
    /// <summary>
    /// Represents a single environment configuration with skybox, ground material, distant objects, audio, and events.
    /// </summary>
    [Serializable]
    public class EnvironmentPreset
    {
        public string Name;
        public Material SkyboxMaterial;
        public Material GroundMaterial;
        public GameObject DistantObjectsPrefab;
        public Color AmbientColor = Color.white;
        public float AmbientIntensity = 1f;
        [Tooltip("Scale of the ground plane. Use smaller values (e.g., 10) for space environments")]
        public float GroundScale = 100f;

        [Header("Audio Settings")]
        public AudioClip AmbientSound;
        [Range(0f, 1f)]
        public float AudioVolume = 0.5f;

        [Header("Events")]
        public UnityEngine.Events.UnityEvent OnEnvironmentLoaded;
        public UnityEngine.Events.UnityEvent OnEnvironmentUnloaded;
    }

    /// <summary>
    /// Manages switching between different planetary environments with fade transitions and audio.
    /// Synchronized across all players using Unity Netcode.
    /// </summary>
    public class EnvironmentManager : NetworkBehaviour
    {
        [Header("Environment Presets")]
        [SerializeField] private List<EnvironmentPreset> m_Environments = new List<EnvironmentPreset>();
        [SerializeField] private int m_DefaultEnvironmentIndex = -1; // -1 = conference room

        [Header("Scene References")]
        [SerializeField] private GameObject m_ConferenceRoom;
        [SerializeField] private GameObject m_PlanetGround;
        [SerializeField] private GameObject m_CementPlatform;
        [SerializeField] private Renderer m_GroundRenderer;
        [SerializeField] private Transform m_DistantObjectsParent;

        [Header("Audio")]
        [SerializeField] private AudioSource m_AudioSource;
        [SerializeField] private float m_FadeDuration = 1.5f;
        [Header("Office Audio")]
        [SerializeField] private AudioClip m_OfficeAmbience;
        [Range(0f, 1f)]
        [SerializeField] private float m_OfficeVolume = 0.5f;

        [Header("Objects to Fade Out")]
        [SerializeField] private List<GameObject> m_ObjectsToFade = new List<GameObject>();

        [Header("Transition Settings")]
        [SerializeField] private CanvasGroup m_FadeOverlay;

        // Network synced environment index (-1 = conference room)
        private NetworkVariable<int> m_NetworkEnvironmentIndex = new NetworkVariable<int>(
            -1, 
            NetworkVariableReadPermission.Everyone, 
            NetworkVariableWritePermission.Owner);

        private int m_LocalEnvironmentIndex = -1;
        private GameObject m_CurrentDistantObjects;
        private bool m_IsTransitioning = false;
        
        // Store original settings
        private Material m_OriginalSkybox;
        private Color m_OriginalAmbientColor;
        private float m_OriginalAmbientIntensity;
        private Vector3 m_OriginalGroundScale;

        public int CurrentEnvironmentIndex => m_LocalEnvironmentIndex;
        public bool IsInConferenceRoom => m_LocalEnvironmentIndex == -1;
        public bool IsTransitioning => m_IsTransitioning;
        public IReadOnlyList<EnvironmentPreset> Environments => m_Environments;

        public string GetCurrentEnvironmentName()
        {
            if (m_LocalEnvironmentIndex == -1)
                return "Conference Room";
            if (m_LocalEnvironmentIndex >= 0 && m_LocalEnvironmentIndex < m_Environments.Count)
                return m_Environments[m_LocalEnvironmentIndex].Name;
            return "Unknown";
        }

        public event Action<int> OnEnvironmentChanged;

        private void Awake()
        {
            // Store original render settings
            m_OriginalSkybox = RenderSettings.skybox;
            m_OriginalAmbientColor = RenderSettings.ambientLight;
            m_OriginalAmbientIntensity = RenderSettings.ambientIntensity;
            
            if (m_PlanetGround != null)
                m_OriginalGroundScale = m_PlanetGround.transform.localScale;

            // Ensure AudioSource exists
            if (m_AudioSource == null)
            {
                m_AudioSource = GetComponent<AudioSource>();
                if (m_AudioSource == null)
                    m_AudioSource = gameObject.AddComponent<AudioSource>();
            }
            
            m_AudioSource.loop = true;
            m_AudioSource.playOnAwake = false;
        }

        private void Start()
        {
            // Initialize environment for offline/single-player mode
            // OnNetworkSpawn will handle online mode
            if (!IsSpawned)
            {
                InitializeEnvironment();
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Subscribe to network variable changes
            m_NetworkEnvironmentIndex.OnValueChanged += OnNetworkEnvironmentChanged;

            // Reset environment to default on new session (Owner only)
            if (IsOwner && m_NetworkEnvironmentIndex.Value == -1)
            {
               m_NetworkEnvironmentIndex.Value = m_DefaultEnvironmentIndex;
            }

            // Initialize to current network state (for late joiners)
            if (m_NetworkEnvironmentIndex.Value != m_LocalEnvironmentIndex)
            {
                ApplyEnvironmentImmediate(m_NetworkEnvironmentIndex.Value);
            }
            else
            {
                // Initial setup for first connection
                InitializeEnvironment();
            }
        }

        public override void OnNetworkDespawn()
        {
            m_NetworkEnvironmentIndex.OnValueChanged -= OnNetworkEnvironmentChanged;
            
            // Reset to conference room when leaving multiplayer
            Debug.Log("[EnvironmentManager] OnNetworkDespawn - resetting to conference room");
            ApplyEnvironmentImmediate(-1);
            
            base.OnNetworkDespawn();
        }

        private void InitializeEnvironment()
        {
            // Platform hidden in conference room
            if (m_CementPlatform != null)
                m_CementPlatform.SetActive(false);

            // Start in conference room
            if (m_ConferenceRoom != null)
                m_ConferenceRoom.SetActive(true);

            if (m_PlanetGround != null)
                m_PlanetGround.SetActive(false);
            
            // Play Office Ambience (Fixed for initial load)
            if (m_OfficeAmbience != null)
            {
                m_AudioSource.clip = m_OfficeAmbience;
                m_AudioSource.volume = m_OfficeVolume;
                m_AudioSource.Play();
            }
            else
            {
                // Ensure stopped if no clip
                 m_AudioSource.Stop();
            }

            // Apply default environment if set (offline or owner init)
            if (IsOwner && m_DefaultEnvironmentIndex >= 0 && m_DefaultEnvironmentIndex < m_Environments.Count)
            {
                 // In offline mode we just set it ? No, Initialize implies visual state.
                 // We rely on OnNetworkSpawn to set the Value which triggers OnValueChanged
            }
        }

        /// <summary>
        /// Called when the network variable changes. Triggers transition on all clients.
        /// </summary>
        private void OnNetworkEnvironmentChanged(int previousValue, int newValue)
        {
            if (newValue != m_LocalEnvironmentIndex)
            {
                StartCoroutine(TransitionToEnvironment(newValue));
            }
        }

        /// <summary>
        /// Request to switch environment. Works both offline (Editor) and online (multiplayer).
        /// </summary>
        public void SwitchToEnvironment(int environmentIndex)
        {
            Debug.Log($"[EnvironmentManager] SwitchToEnvironment({environmentIndex}) called.");
            
            if (m_IsTransitioning) 
            {
                Debug.Log("[EnvironmentManager] Already transitioning, ignoring");
                return;
            }
            if (environmentIndex == m_LocalEnvironmentIndex) 
            {
                Debug.Log("[EnvironmentManager] Same environment, ignoring");
                return;
            }

            // OFFLINE MODE
            if (!IsSpawned)
            {
                Debug.Log("[EnvironmentManager] Offline mode - switching locally");
                StartCoroutine(TransitionToEnvironment(environmentIndex));
                return;
            }

            // MULTIPLAYER MODE
            if (IsOwner)
            {
                m_NetworkEnvironmentIndex.Value = environmentIndex;
            }
            else
            {
                RequestSwitchEnvironmentRpc(environmentIndex);
            }
        }

        [Rpc(SendTo.Owner)]
        private void RequestSwitchEnvironmentRpc(int index)
        {
             m_NetworkEnvironmentIndex.Value = index;
        }

        /// <summary>
        /// Cycle to the next environment.
        /// </summary>
        public void NextEnvironment()
        {
            int next = m_LocalEnvironmentIndex + 1;
            if (next >= m_Environments.Count)
                next = -1; // Back to conference room
            SwitchToEnvironment(next);
        }

        /// <summary>
        /// Return to the conference room.
        /// </summary>
        public void ReturnToConferenceRoom()
        {
            SwitchToEnvironment(-1);
        }

        /// <summary>
        /// Re-applies the currently selected environment visuals after external scene changes.
        /// </summary>
        public void RefreshCurrentEnvironmentVisuals()
        {
            ApplyEnvironmentImmediate(m_LocalEnvironmentIndex);
        }

        private IEnumerator TransitionToEnvironment(int targetIndex)
        {
            m_IsTransitioning = true;

            // Trigger Unload Event for current environment
            if (m_LocalEnvironmentIndex >= 0 && m_LocalEnvironmentIndex < m_Environments.Count)
            {
                m_Environments[m_LocalEnvironmentIndex].OnEnvironmentUnloaded?.Invoke();
            }

            // Fade Audio Out
            float startVolume = m_AudioSource.volume;
            float fadeAudioTimer = 0f;
            while(fadeAudioTimer < m_FadeDuration * 0.5f)
            {
                fadeAudioTimer += Time.deltaTime;
                m_AudioSource.volume = Mathf.Lerp(startVolume, 0f, fadeAudioTimer / (m_FadeDuration * 0.5f));
                yield return null; 
            }
            m_AudioSource.Stop();
            m_AudioSource.volume = 1f;

            // Fade to black
            yield return StartCoroutine(FadeScreen(0f, 1f, m_FadeDuration * 0.5f));

            // Apply environment instantly
            ApplyEnvironmentImmediate(targetIndex);

            // Fade from black
            yield return StartCoroutine(FadeScreen(1f, 0f, m_FadeDuration * 0.5f));

            m_IsTransitioning = false;
            
            // Delayed audio check
            StartCoroutine(DelayedAudioCheck());
        }

        /// <summary>
        /// Apply environment without fade (for late joiners or instant changes)
        /// </summary>
        private void ApplyEnvironmentImmediate(int index)
        {
            m_LocalEnvironmentIndex = index;
            bool isConferenceRoom = index == -1;

            // Toggle conference room
            if (m_ConferenceRoom != null)
                m_ConferenceRoom.SetActive(isConferenceRoom);

            // Toggle planet ground and platform
            if (m_PlanetGround != null)
                m_PlanetGround.SetActive(!isConferenceRoom);
            
            if (m_CementPlatform != null)
                m_CementPlatform.SetActive(!isConferenceRoom);

            // Fade objects
            foreach (var obj in m_ObjectsToFade)
            {
                if (obj != null)
                    obj.SetActive(isConferenceRoom);
            }

            // Clean up previous distant objects
            if (m_CurrentDistantObjects != null)
            {
                Destroy(m_CurrentDistantObjects);
                m_CurrentDistantObjects = null;
            }

            if (isConferenceRoom)
            {
                // Restore original settings
                if (m_OriginalSkybox != null)
                    RenderSettings.skybox = m_OriginalSkybox;
                
                RenderSettings.ambientLight = m_OriginalAmbientColor;
                RenderSettings.ambientIntensity = m_OriginalAmbientIntensity;
                
                if (m_PlanetGround != null && m_OriginalGroundScale != Vector3.zero)
                    m_PlanetGround.transform.localScale = m_OriginalGroundScale;
                
                DynamicGI.UpdateEnvironment();
                
                // Play Office Ambience
                if (m_OfficeAmbience != null)
                {
                    m_AudioSource.clip = m_OfficeAmbience;
                    m_AudioSource.volume = m_OfficeVolume;
                    m_AudioSource.Play();
                }
                else
                {
                    m_AudioSource.Stop();
                }
            }
            else if (index >= 0 && index < m_Environments.Count)
            {
                var env = m_Environments[index];

                // Apply skybox
                if (env.SkyboxMaterial != null)
                    RenderSettings.skybox = env.SkyboxMaterial;

                // Apply ground material
                if (m_GroundRenderer != null && env.GroundMaterial != null)
                {
                    m_GroundRenderer.sharedMaterial = env.GroundMaterial;
                    m_GroundRenderer.material = env.GroundMaterial;
                }

                // Apply ground scale
                if (m_PlanetGround != null && env.GroundScale > 0)
                {
                    m_PlanetGround.transform.localScale = new Vector3(env.GroundScale, 1, env.GroundScale);
                }

                // Spawn distant objects locally (not networked, visual only)
                if (env.DistantObjectsPrefab != null)
                {
                    var parent = m_DistantObjectsParent != null ? m_DistantObjectsParent : transform;
                    m_CurrentDistantObjects = Instantiate(env.DistantObjectsPrefab, parent);
                }

                // Apply ambient lighting
                RenderSettings.ambientLight = env.AmbientColor;
                RenderSettings.ambientIntensity = env.AmbientIntensity;

                DynamicGI.UpdateEnvironment();

                // Play Environment Audio
                if (env.AmbientSound != null)
                {
                    m_AudioSource.clip = env.AmbientSound;
                    m_AudioSource.volume = env.AudioVolume;
                    m_AudioSource.Play();
                    Debug.Log($"[EnvironmentManager] Playing audio: {env.AmbientSound.name}, Volume: {env.AudioVolume}, ClipLength: {env.AmbientSound.length}s, IsPlaying: {m_AudioSource.isPlaying}, Mute: {m_AudioSource.mute}, SpatialBlend: {m_AudioSource.spatialBlend}");
                }
                else
                {
                    Debug.LogWarning($"[EnvironmentManager] No AmbientSound assigned for environment: {env.Name}");
                }

                // Trigger Load Event
                env.OnEnvironmentLoaded?.Invoke();
            }

            OnEnvironmentChanged?.Invoke(index);
        }

        private IEnumerator FadeScreen(float from, float to, float duration)
        {
            if (m_FadeOverlay == null) yield break;

            m_FadeOverlay.gameObject.SetActive(true);
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                m_FadeOverlay.alpha = Mathf.Lerp(from, to, t);
                yield return null;
            }

            m_FadeOverlay.alpha = to;

            if (to == 0f)
                m_FadeOverlay.gameObject.SetActive(false);
        }

        private IEnumerator DelayedAudioCheck()
        {
            yield return new WaitForSeconds(2f);
            
            if (m_AudioSource != null)
            {
                Debug.Log($"[EnvironmentManager] AUDIO CHECK after 2s: Clip={m_AudioSource.clip?.name ?? "NULL"}, IsPlaying={m_AudioSource.isPlaying}, Volume={m_AudioSource.volume}, Time={m_AudioSource.time}");
            }
            else
            {
                Debug.LogError("[EnvironmentManager] AUDIO CHECK: AudioSource is NULL!");
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Add Default Environment Presets")]
        private void AddDefaultPresets()
        {
            m_Environments.Clear();
            m_Environments.Add(new EnvironmentPreset { Name = "Mars Colony", AmbientColor = new Color(0.8f, 0.4f, 0.2f), GroundScale = 100f });
            m_Environments.Add(new EnvironmentPreset { Name = "Cosmic Nebula", AmbientColor = new Color(0.4f, 0.2f, 0.6f), GroundScale = 100f });
            m_Environments.Add(new EnvironmentPreset { Name = "Underwater Base", AmbientColor = new Color(0.2f, 0.5f, 0.7f), GroundScale = 100f });
            m_Environments.Add(new EnvironmentPreset { Name = "Space Station", AmbientColor = new Color(0.6f, 0.7f, 0.9f), GroundScale = 15f });
        }
#endif
    }
}
