using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Premium-quality environment events with layered effects, dynamic lighting, and immersive audio.
    /// </summary>
    public class EnvironmentEvents : MonoBehaviour
    {
        [Header("Global Settings")]
        [SerializeField] private float m_MinEventInterval = 45f;
        [SerializeField] private float m_MaxEventInterval = 90f;

        #region Mars Dust Storm Settings
        [Header("═══ MARS: DUST STORM ═══")]
        [SerializeField] private float m_StormDuration = 30f;
        [SerializeField] private float m_StormBuildupTime = 5f;
        [SerializeField] private float m_StormFadeTime = 5f;
        [SerializeField] private AudioClip m_WindSound;
        [Tooltip("Number of dust layers (near/mid/far)")]
        [SerializeField] private int m_DustLayers = 3;
        [SerializeField] private int m_ParticlesPerLayer = 50;
        [SerializeField] private Color m_StormAmbientColor = new Color(0.9f, 0.5f, 0.3f);
        [SerializeField] private Color m_DustColorNear = new Color(0.85f, 0.65f, 0.45f, 0.8f);
        [SerializeField] private Color m_DustColorFar = new Color(0.75f, 0.55f, 0.35f, 0.3f);
        [SerializeField] private float m_LightningChance = 0.02f;
        #endregion

        #region Nebula UFO Settings
        [Header("═══ NEBULA: UFO ENCOUNTER ═══")]
        [SerializeField] private int m_UFOCount = 3;
        [SerializeField] private float m_UFOSpeed = 4f;
        [SerializeField] private float m_UFODistance = 40f;
        [SerializeField] private float m_UFOScale = 2.5f;
        [SerializeField] private Color m_UFOGlowColor1 = new Color(0.3f, 1f, 0.5f);
        [SerializeField] private Color m_UFOGlowColor2 = new Color(0.5f, 0.3f, 1f);
        [SerializeField] private float m_UFOGlowIntensity = 4f;
        [SerializeField] private AudioClip m_UFOSound;
        [SerializeField] private bool m_EnableScanBeam = true;
        #endregion

        #region Underwater Dolphin Settings
        [Header("═══ UNDERWATER: DOLPHIN POD ═══")]
        [SerializeField] private GameObject m_CreaturePrefab;
        [SerializeField] private int m_PodSize = 4;
        [SerializeField] private float m_CreatureSpeed = 4f;
        [SerializeField] private float m_CreatureDistance = 15f;
        [SerializeField] private float m_CreatureScale = 4f;
        [SerializeField] private AudioClip m_DolphinSound;
        [SerializeField] private Color m_BioluminescentColor = new Color(0.3f, 0.8f, 1f);
        [SerializeField] private bool m_EnableBubbles = true;
        #endregion

        #region Space Station Meteor Settings
        [Header("═══ SPACE STATION: METEOR STORM ═══")]
        [SerializeField] private GameObject m_MeteorPrefab;
        [SerializeField] private float m_MeteorDuration = 25f;
        [SerializeField] private float m_MeteorSpawnRate = 0.2f;
        [SerializeField] private float m_MeteorMinSpeed = 30f;
        [SerializeField] private float m_MeteorMaxSpeed = 60f;
        [SerializeField] private float m_SpawnDistance = 70f;
        [SerializeField] private float m_DramaticMeteorChance = 0.1f;
        [SerializeField] private AudioClip m_MeteorWhoosh;
        [SerializeField] private AudioClip m_MeteorRumble;
        #endregion

        private AudioSource m_AudioSource;
        private AudioSource m_SecondaryAudio;
        private Camera m_MainCamera;
        private Coroutine m_CurrentEvent;
        private List<GameObject> m_ActiveObjects = new List<GameObject>();
        private Color m_OriginalAmbientColor;
        private float m_OriginalAmbientIntensity;

        private void Awake()
        {
            // Create a DEDICATED AudioSource for event sounds (don't steal EnvironmentManager's)
            m_AudioSource = gameObject.AddComponent<AudioSource>();
            m_AudioSource.spatialBlend = 0f;
            m_AudioSource.loop = true;
            m_AudioSource.playOnAwake = false;
            
            // Secondary audio for layered sounds
            m_SecondaryAudio = gameObject.AddComponent<AudioSource>();
            m_SecondaryAudio.spatialBlend = 0f;
            m_SecondaryAudio.loop = false;
            m_SecondaryAudio.playOnAwake = false;
            
            m_MainCamera = Camera.main;
            m_OriginalAmbientColor = RenderSettings.ambientLight;
            m_OriginalAmbientIntensity = RenderSettings.ambientIntensity;
        }

        #region Public Event Triggers
        public void TriggerDustStorm()
        {
            Debug.Log("<color=orange>[EnvironmentEvents] >>> DUST STORM (Loop Started) <<<</color>");
            StopCurrentEvent();
            m_CurrentEvent = StartCoroutine(StartEventLoop(PremiumDustStormRoutine));
        }

        public void TriggerZeroG() => TriggerUFOFlyby();

        public void TriggerUFOFlyby()
        {
            Debug.Log("<color=green>[EnvironmentEvents] >>> UFO ENCOUNTER (Loop Started) <<<</color>");
            StopCurrentEvent();
            m_CurrentEvent = StartCoroutine(StartEventLoop(PremiumUFORoutine));
        }

        public void TriggerWhalePass()
        {
            Debug.Log("<color=cyan>[EnvironmentEvents] >>> DOLPHIN POD <<<</color>");
            if (m_CreaturePrefab == null)
            {
                Debug.LogError("[EnvironmentEvents] CreaturePrefab is NULL!");
                return;
            }
            StopCurrentEvent();
            m_CurrentEvent = StartCoroutine(StartEventLoop(PremiumDolphinPodRoutine));
        }

        public void TriggerMeteorShower()
        {
            Debug.Log("<color=red>[EnvironmentEvents] >>> METEOR STORM <<<</color>");
            if (m_MeteorPrefab == null)
            {
                Debug.LogError("[EnvironmentEvents] MeteorPrefab is NULL!");
                return;
            }
            StopCurrentEvent();
            m_CurrentEvent = StartCoroutine(StartEventLoop(PremiumMeteorStormRoutine));
        }

        public void StopCurrentEvent()
        {
            if (m_CurrentEvent != null)
            {
                StopCoroutine(m_CurrentEvent);
                m_CurrentEvent = null;
            }
            CleanupActiveObjects();
            RestoreAmbient();
            if (m_AudioSource != null) m_AudioSource.Stop();
            if (m_SecondaryAudio != null) m_SecondaryAudio.Stop();
        }
        #endregion

        // Helper to run an event and then wait for a random interval before repeating
        private IEnumerator StartEventLoop(System.Func<IEnumerator> eventRoutine)
        {
            // Loop forever
            while (true)
            {
                // Run the event routine directly on this coroutine
                // This ensures that when StopCoroutine(m_CurrentEvent) is called, 
                // the active event logic stops immediately.
                yield return eventRoutine();

                float delay = Random.Range(m_MinEventInterval, m_MaxEventInterval);
                yield return new WaitForSeconds(delay);
            }
        }

        #region ═══════════════════════════════════════════════════════════════
        // MARS DUST STORM - Premium Implementation
        #endregion

        private IEnumerator PremiumDustStormRoutine()
        {
            Vector3 playerPos = GetPlayerPosition();
            float elapsed = 0f;
            float intensity = 0f;
            
            // Store original settings
            Color originalAmbient = RenderSettings.ambientLight;
            
            // Create dust material with transparency
            Material[] dustMaterials = new Material[m_DustLayers];
            for (int i = 0; i < m_DustLayers; i++)
            {
                float t = (float)i / (m_DustLayers - 1);
                dustMaterials[i] = CreateUnlitMaterial(Color.Lerp(m_DustColorNear, m_DustColorFar, t));
            }
            
            // Start wind sound quietly
            if (m_WindSound != null)
            {
                m_AudioSource.clip = m_WindSound;
                m_AudioSource.volume = 0f;
                m_AudioSource.Play();
            }
            
            // Spawn initial dust layers
            Dictionary<int, List<DustParticle>> layers = new Dictionary<int, List<DustParticle>>();
            for (int layer = 0; layer < m_DustLayers; layer++)
            {
                layers[layer] = new List<DustParticle>();
            }
            
            // Main storm loop
            while (elapsed < m_StormDuration)
            {
                elapsed += Time.deltaTime;
                playerPos = GetPlayerPosition();
                
                // Calculate intensity curve (buildup -> peak -> fade)
                if (elapsed < m_StormBuildupTime)
                    intensity = Mathf.SmoothStep(0f, 1f, elapsed / m_StormBuildupTime);
                else if (elapsed > m_StormDuration - m_StormFadeTime)
                    intensity = Mathf.SmoothStep(1f, 0f, (elapsed - (m_StormDuration - m_StormFadeTime)) / m_StormFadeTime);
                else
                    intensity = 1f;
                
                // Update ambient light
                RenderSettings.ambientLight = Color.Lerp(originalAmbient, m_StormAmbientColor, intensity * 0.7f);
                
                // Update wind volume
                if (m_AudioSource != null)
                    m_AudioSource.volume = intensity * 0.8f;
                
                // Spawn particles per layer
                for (int layer = 0; layer < m_DustLayers; layer++)
                {
                    float layerT = (float)layer / (m_DustLayers - 1);
                    float layerDistance = Mathf.Lerp(5f, 40f, layerT);
                    float layerSpeed = Mathf.Lerp(20f, 8f, layerT);
                    float layerScale = Mathf.Lerp(0.05f, 0.25f, layerT); // Much smaller particles
                    
                    // Spawn new particles based on intensity
                    if (layers[layer].Count < m_ParticlesPerLayer * intensity)
                    {
                        if (Random.value < 0.3f * intensity)
                        {
                            SpawnDustParticle(playerPos, layer, layerDistance, layerScale, dustMaterials[layer], layers[layer]);
                        }
                    }
                    
                    // Update existing particles
                    for (int i = layers[layer].Count - 1; i >= 0; i--)
                    {
                        var p = layers[layer][i];
                        if (p.obj == null)
                        {
                            layers[layer].RemoveAt(i);
                            continue;
                        }
                        
                        // Move with wind + turbulence
                        float turbulence = Mathf.PerlinNoise(Time.time * 0.5f + i * 0.1f, layer) * 2f - 1f;
                        Vector3 windDir = (Vector3.right + Vector3.up * turbulence * 0.3f).normalized;
                        p.obj.transform.position += windDir * layerSpeed * intensity * Time.deltaTime;
                        
                        // Tumble rotation
                        p.obj.transform.Rotate(p.rotationSpeed * Time.deltaTime * intensity);
                        
                            // Fade based on distance and lifetime
                            float dist = Vector3.Distance(p.obj.transform.position, playerPos);
                            
                            // Scale logic: Scale up at start, scale down at end
                            if (p.obj.transform.localScale.x < p.targetScale)
                            {
                                float newScale = Mathf.MoveTowards(p.obj.transform.localScale.x, p.targetScale, Time.deltaTime * 2f);
                                p.obj.transform.localScale = Vector3.one * newScale;
                            }
                            else if (dist > 40f) // Start scaling down if far
                            {
                                float newScale = Mathf.MoveTowards(p.obj.transform.localScale.x, 0f, Time.deltaTime * 2f);
                                p.obj.transform.localScale = Vector3.one * newScale;
                            }

                            if (dist > 50f || (dist > 40f && p.obj.transform.localScale.x < 0.01f))
                            {
                                Destroy(p.obj);
                                layers[layer].RemoveAt(i);
                            }
                    }
                }
                
                // Lightning flash (random chance)
                if (intensity > 0.5f && Random.value < m_LightningChance * intensity)
                {
                    StartCoroutine(LightningFlash());
                }
                
                // Camera shake based on intensity
                if (m_MainCamera != null && intensity > 0.3f)
                {
                    float shake = intensity * 0.015f;
                    m_MainCamera.transform.localPosition += Random.insideUnitSphere * shake;
                }
                
                yield return null;
            }
            
            // Cleanup
            foreach (var layer in layers.Values)
            {
                foreach (var p in layer)
                {
                    if (p.obj != null) Destroy(p.obj);
                }
            }
            
            foreach (var mat in dustMaterials)
            {
                if (mat != null) Destroy(mat);
            }
            
            RenderSettings.ambientLight = originalAmbient;
            if (m_AudioSource != null) m_AudioSource.Stop();
        }

        private void SpawnDustParticle(Vector3 playerPos, int layer, float distance, float scale, Material mat, List<DustParticle> list)
        {
            // Spawn to the left of player at varying heights
            Vector3 spawnPos = playerPos + Vector3.left * (distance + 20f);
            spawnPos.y += Random.Range(-8f, 15f);
            spawnPos.z += Random.Range(-15f, 15f);
            
            // Create varied shapes
            PrimitiveType[] shapes = { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule };
            GameObject dust = GameObject.CreatePrimitive(shapes[Random.Range(0, shapes.Length)]);
            dust.name = $"Dust_L{layer}";
            dust.transform.position = spawnPos;
            dust.name = $"Dust_L{layer}";
            dust.transform.position = spawnPos;
            float targetScale = scale * Random.Range(0.3f, 1.0f);
            dust.transform.localScale = Vector3.zero; // Start invisible
            dust.transform.rotation = Random.rotation;
            
            var renderer = dust.GetComponent<Renderer>();
            renderer.material = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            
            Destroy(dust.GetComponent<Collider>());
            
            list.Add(new DustParticle { obj = dust, rotationSpeed = Random.insideUnitSphere * 200f, targetScale = targetScale });
            m_ActiveObjects.Add(dust);
        }

        private IEnumerator LightningFlash()
        {
            Color original = RenderSettings.ambientLight;
            RenderSettings.ambientLight = Color.white;
            yield return new WaitForSeconds(0.05f);
            RenderSettings.ambientLight = original;
            yield return new WaitForSeconds(0.1f);
            RenderSettings.ambientLight = Color.white * 0.7f;
            yield return new WaitForSeconds(0.03f);
            RenderSettings.ambientLight = original;
        }

        private struct DustParticle
        {
            public GameObject obj;
            public Vector3 rotationSpeed;
            public float targetScale;
        }

        #region ═══════════════════════════════════════════════════════════════
        // NEBULA UFO ENCOUNTER - Premium Implementation
        #endregion

        private IEnumerator PremiumUFORoutine()
        {
            Vector3 playerPos = GetPlayerPosition();
            List<UFOData> ufos = new List<UFOData>();
            
            // Start eerie sound
            if (m_UFOSound != null)
            {
                m_AudioSource.clip = m_UFOSound;
                m_AudioSource.volume = 0f;
                m_AudioSource.Play();
            }
            
            // Create UFO formation
            for (int i = 0; i < m_UFOCount; i++)
            {
                // Spread horizontally with some depth variation (V-formation)
                float horizontalOffset = (i - (m_UFOCount - 1) / 2f) * 12f; // Spread left-right
                float depthOffset = Mathf.Abs(i - (m_UFOCount - 1) / 2f) * 5f; // V-shape depth
                float delay = i * 0.3f;
                
                UFOData data = new UFOData
                {
                    ufo = CreatePremiumUFO(i),
                    startDelay = delay,
                    horizontalOffset = horizontalOffset,
                    depthOffset = depthOffset,
                    phaseOffset = i * 0.8f
                };
                
                data.ufo.SetActive(false);
                ufos.Add(data);
                m_ActiveObjects.Add(data.ufo);
            }
            
            // Define curved path control points
            Vector3 start = playerPos + Vector3.left * m_UFODistance + Vector3.up * 25f + Vector3.forward * 15f;
            Vector3 mid1 = playerPos + Vector3.up * 30f + Vector3.forward * 5f;
            Vector3 mid2 = playerPos + Vector3.up * 20f + Vector3.back * 10f;
            Vector3 end = playerPos + Vector3.right * m_UFODistance + Vector3.up * 35f + Vector3.forward * 10f;
            
            float duration = (m_UFODistance * 2.5f) / m_UFOSpeed;
            float elapsed = 0f;
            
            while (elapsed < duration + m_UFOCount * 0.5f)
            {
                elapsed += Time.deltaTime;
                
                // Fade in audio
                if (m_AudioSource != null)
                    m_AudioSource.volume = Mathf.Clamp01(elapsed * 0.5f) * 0.6f;
                
                foreach (var data in ufos)
                {
                    float ufoTime = elapsed - data.startDelay;
                    if (ufoTime < 0) continue;
                    
                    if (!data.ufo.activeSelf) data.ufo.SetActive(true);
                    
                    float t = Mathf.Clamp01(ufoTime / duration);
                    
                    // Cubic bezier curve for smooth path
                    Vector3 pos = CubicBezier(start, mid1, mid2, end, t);
                    pos.x += data.horizontalOffset; // Spread horizontally
                    pos.z += data.depthOffset; // V-formation depth
                    pos.y += Mathf.Sin(ufoTime * 1.5f + data.phaseOffset) * 1.5f; // Subtle hover wobble
                    
                    data.ufo.transform.position = pos;
                    data.ufo.transform.Rotate(Vector3.up, 45f * Time.deltaTime);
                    
                    // Scale in/out to hide spawn/despawn
                    float scaleMult = 1f;
                    if (t < 0.1f) scaleMult = t / 0.1f;
                    else if (t > 0.9f) scaleMult = (1f - t) / 0.1f;
                    data.ufo.transform.localScale = Vector3.one * m_UFOScale * scaleMult;
                    
                    // Pulsating glow
                    UpdateUFOGlow(data.ufo, ufoTime, data.phaseOffset);
                    
                    // Update scan beam
                    if (m_EnableScanBeam)
                    {
                        UpdateScanBeam(data.ufo, t);
                    }
                }
                
                yield return null;
            }
            
            // Cleanup
            foreach (var data in ufos)
            {
                if (data.ufo != null) Destroy(data.ufo);
            }
            
            if (m_AudioSource != null) m_AudioSource.Stop();
        }

        private GameObject CreatePremiumUFO(int index)
        {
            GameObject ufo = new GameObject($"UFO_{index}");
            
            // Main saucer body
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Body";
            body.transform.SetParent(ufo.transform);
            body.transform.localPosition = Vector3.zero;
            body.transform.localScale = new Vector3(4f, 0.6f, 4f);
            Destroy(body.GetComponent<Collider>());
            
            // Dome (cockpit)
            GameObject dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "Dome";
            dome.transform.SetParent(ufo.transform);
            dome.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            dome.transform.localScale = new Vector3(1.5f, 0.8f, 1.5f);
            Destroy(dome.GetComponent<Collider>());
            
            // Outer ring
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(ufo.transform);
            ring.transform.localPosition = Vector3.zero;
            ring.transform.localScale = new Vector3(4.5f, 0.08f, 4.5f);
            Destroy(ring.GetComponent<Collider>());
            
            // Inner core (glowing center underneath)
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "Core";
            core.transform.SetParent(ufo.transform);
            core.transform.localPosition = new Vector3(0f, -0.2f, 0f);
            core.transform.localScale = new Vector3(1.2f, 0.4f, 1.2f);
            Destroy(core.GetComponent<Collider>());
            
            // Create emissive materials
            Material bodyMat = CreateEmissiveMaterial(m_UFOGlowColor1 * 0.3f, m_UFOGlowIntensity * 0.3f);
            Material domeMat = CreateEmissiveMaterial(m_UFOGlowColor2, m_UFOGlowIntensity * 1.5f);
            Material ringMat = CreateEmissiveMaterial(Color.Lerp(m_UFOGlowColor1, m_UFOGlowColor2, 0.5f), m_UFOGlowIntensity * 0.6f);
            Material coreMat = CreateEmissiveMaterial(m_UFOGlowColor1, m_UFOGlowIntensity * 2f);
            
            body.GetComponent<Renderer>().material = bodyMat;
            dome.GetComponent<Renderer>().material = domeMat;
            ring.GetComponent<Renderer>().material = ringMat;
            core.GetComponent<Renderer>().material = coreMat;
            
            // Add lights around the ring perimeter (6 lights in a circle)
            for (int i = 0; i < 6; i++)
            {
                float angle = i * 60f * Mathf.Deg2Rad;
                Vector3 lightPos = new Vector3(Mathf.Cos(angle) * 2f, -0.1f, Mathf.Sin(angle) * 2f);
                
                GameObject lightOrb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                lightOrb.name = $"RingLight_{i}";
                lightOrb.transform.SetParent(ufo.transform);
                lightOrb.transform.localPosition = lightPos;
                lightOrb.transform.localScale = Vector3.one * 0.2f;
                Destroy(lightOrb.GetComponent<Collider>());
                
                // Alternating colors
                Color orbColor = (i % 2 == 0) ? m_UFOGlowColor1 : m_UFOGlowColor2;
                lightOrb.GetComponent<Renderer>().material = CreateEmissiveMaterial(orbColor, m_UFOGlowIntensity * 2f);
                
                // Add point light to each orb
                Light orbLight = lightOrb.AddComponent<Light>();
                orbLight.type = LightType.Point;
                orbLight.color = orbColor;
                orbLight.intensity = m_UFOGlowIntensity * 0.5f;
                orbLight.range = 5f;
            }
            
            // Main glow light
            Light mainLight = ufo.AddComponent<Light>();
            mainLight.type = LightType.Point;
            mainLight.color = m_UFOGlowColor1;
            mainLight.intensity = m_UFOGlowIntensity;
            mainLight.range = 25f;
            
            // Scan beam (cone of light underneath)
            if (m_EnableScanBeam)
            {
                GameObject beamObj = new GameObject("ScanBeam");
                beamObj.transform.SetParent(ufo.transform);
                beamObj.transform.localPosition = new Vector3(0f, -0.5f, 0f);
                
                Light beam = beamObj.AddComponent<Light>();
                beam.type = LightType.Spot;
                beam.color = m_UFOGlowColor1;
                beam.intensity = m_UFOGlowIntensity * 3f;
                beam.range = 60f;
                beam.spotAngle = 35f;
                beam.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            
            ufo.transform.localScale = Vector3.one * m_UFOScale;
            return ufo;
        }

        private void UpdateUFOGlow(GameObject ufo, float time, float phaseOffset)
        {
            Light[] lights = ufo.GetComponentsInChildren<Light>();
            float pulse = (Mathf.Sin(time * 3f + phaseOffset) + 1f) / 2f;
            Color pulsedColor = Color.Lerp(m_UFOGlowColor1, m_UFOGlowColor2, pulse);
            
            foreach (var light in lights)
            {
                light.color = pulsedColor;
                light.intensity = m_UFOGlowIntensity * (0.7f + pulse * 0.6f);
            }
        }

        private void UpdateScanBeam(GameObject ufo, float t)
        {
            Transform beam = ufo.transform.Find("ScanBeam");
            if (beam != null)
            {
                // Sweep the beam back and forth
                float sweep = Mathf.Sin(t * Mathf.PI * 4f) * 30f;
                beam.localRotation = Quaternion.Euler(90f + sweep * 0.5f, sweep, 0f);
            }
        }

        private struct UFOData
        {
            public GameObject ufo;
            public float startDelay;
            public float horizontalOffset;
            public float depthOffset;
            public float phaseOffset;
        }

        #region ═══════════════════════════════════════════════════════════════
        // UNDERWATER DOLPHIN POD - Premium Implementation
        #endregion

        private IEnumerator PremiumDolphinPodRoutine()
        {
            Vector3 playerPos = GetPlayerPosition();
            List<DolphinData> pod = new List<DolphinData>();
            
            // Play dolphin sounds
            if (m_DolphinSound != null)
            {
                m_AudioSource.clip = m_DolphinSound;
                m_AudioSource.loop = true;
                m_AudioSource.volume = 0.7f;
                m_AudioSource.Play();
            }
            
            // Create pod with varying sizes and positions
            for (int i = 0; i < m_PodSize; i++)
            {
                float sizeVariation = Random.Range(0.7f, 1.3f);
                float horizontalOffset = (i - (m_PodSize - 1) / 2f) * 4f + Random.Range(-1f, 1f);
                float depthOffset = Random.Range(-3f, 3f);
                float delay = i * 0.8f + Random.Range(0f, 0.3f);
                
                GameObject dolphin = Instantiate(m_CreaturePrefab);
                dolphin.name = $"Dolphin_{i}";
                float finalScale = m_CreatureScale * sizeVariation;
                dolphin.transform.localScale = Vector3.zero; // Start invisible
                dolphin.SetActive(false);
                
                // Add bioluminescent glow
                AddBioluminescentGlow(dolphin);
                
                DolphinData data = new DolphinData
                {
                    obj = dolphin,
                    startDelay = delay,
                    horizontalOffset = horizontalOffset,
                    depthOffset = depthOffset,
                    swimPhase = Random.Range(0f, Mathf.PI * 2f),
                    baseScale = finalScale
                };
                
                pod.Add(data);
                m_ActiveObjects.Add(dolphin);
            }
            
            // Path setup
            Vector3 start = playerPos + Vector3.left * m_CreatureDistance + Vector3.forward * 5f;
            Vector3 end = playerPos + Vector3.right * m_CreatureDistance + Vector3.forward * 5f;
            Vector3 mid = (start + end) / 2f + Vector3.up * 5f;
            
            float duration = (m_CreatureDistance * 2f) / m_CreatureSpeed;
            float elapsed = 0f;
            
            while (elapsed < duration + m_PodSize * 0.8f + 2f)
            {
                elapsed += Time.deltaTime;
                
                foreach (var data in pod)
                {
                    float dolphinTime = elapsed - data.startDelay;
                    if (dolphinTime < 0) continue;
                    
                    if (!data.obj.activeSelf) data.obj.SetActive(true);
                    
                    float t = Mathf.Clamp01(dolphinTime / duration);
                    
                    // Bezier curve with individual offsets
                    Vector3 basePos = QuadraticBezier(start, mid, end, t);
                    Vector3 pos = basePos;
                    pos.x += data.horizontalOffset;
                    pos.z += data.depthOffset;
                    
                    // Sinusoidal swimming motion
                    float swimY = Mathf.Sin(dolphinTime * 2.5f + data.swimPhase) * 1.5f;
                    float swimZ = Mathf.Cos(dolphinTime * 1.8f + data.swimPhase) * 0.8f;
                    pos.y += swimY;
                    pos.z += swimZ;
                    
                    data.obj.transform.position = pos;
                    
                    // Scale in/out logic
                    float scaleMult = 1f;
                    if (t < 0.1f) scaleMult = t / 0.1f;
                    else if (t > 0.9f) scaleMult = (1f - t) / 0.1f;
                    data.obj.transform.localScale = Vector3.one * data.baseScale * scaleMult;
                    
                    // Smooth rotation following movement
                    if (t < 0.99f)
                    {
                        float nextT = Mathf.Clamp01((dolphinTime + 0.1f) / duration);
                        Vector3 nextPos = QuadraticBezier(start, mid, end, nextT);
                        nextPos.y += Mathf.Sin((dolphinTime + 0.1f) * 2.5f + data.swimPhase) * 1.5f;
                        
                        Vector3 dir = (nextPos - pos).normalized;
                        if (dir.magnitude > 0.01f)
                        {
                            // Add body tilt based on swimming
                            Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
                            float tilt = Mathf.Sin(dolphinTime * 2.5f + data.swimPhase) * 15f;
                            targetRot *= Quaternion.Euler(tilt, 0f, 0f);
                            data.obj.transform.rotation = Quaternion.Slerp(data.obj.transform.rotation, targetRot, Time.deltaTime * 5f);
                        }
                    }
                    
                    // Spawn bubbles based on time
                    if (m_EnableBubbles && Random.value < 0.05f)
                    {
                        SpawnBubble(pos + Vector3.back * 0.5f);
                    }
                    
                    // Update bioluminescent pulse
                    UpdateBioluminescence(data.obj, dolphinTime);
                }
                
                yield return null;
            }
            
            // Cleanup
            foreach (var data in pod)
            {
                if (data.obj != null) Destroy(data.obj);
            }
            
            if (m_AudioSource != null) m_AudioSource.Stop();
        }

        private void AddBioluminescentGlow(GameObject dolphin)
        {
            Light glow = dolphin.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = m_BioluminescentColor;
            glow.intensity = 2f;
            glow.range = 8f;
        }

        private void UpdateBioluminescence(GameObject dolphin, float time)
        {
            Light glow = dolphin.GetComponent<Light>();
            if (glow != null)
            {
                float pulse = (Mathf.Sin(time * 4f) + 1f) / 2f;
                glow.intensity = 1f + pulse * 2f;
            }
        }

        private void SpawnBubble(Vector3 pos)
        {
            GameObject bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bubble.name = "Bubble";
            bubble.transform.position = pos + Random.insideUnitSphere * 0.3f;
            float size = Random.Range(0.05f, 0.2f);
            bubble.transform.localScale = Vector3.one * size;
            
            Material bubbleMat = CreateUnlitMaterial(new Color(0.8f, 0.9f, 1f, 0.3f));
            bubble.GetComponent<Renderer>().material = bubbleMat;
            Destroy(bubble.GetComponent<Collider>());
            
            m_ActiveObjects.Add(bubble);
            StartCoroutine(AnimateBubble(bubble, bubbleMat));
        }

        private IEnumerator AnimateBubble(GameObject bubble, Material mat)
        {
            float lifetime = Random.Range(1f, 2f);
            float elapsed = 0f;
            float speed = Random.Range(1f, 2f);
            
            while (elapsed < lifetime && bubble != null)
            {
                elapsed += Time.deltaTime;
                bubble.transform.position += Vector3.up * speed * Time.deltaTime;
                bubble.transform.position += Random.insideUnitSphere * 0.01f; // Wobble
                yield return null;
            }
            
            if (bubble != null) Destroy(bubble);
            if (mat != null) Destroy(mat);
        }

        private class DolphinData
        {
            public GameObject obj;
            public float startDelay;
            public float horizontalOffset;
            public float depthOffset;
            public float swimPhase;
            public float baseScale;
        }

        #region ═══════════════════════════════════════════════════════════════
        // SPACE STATION METEOR STORM - Premium Implementation
        #endregion

        private IEnumerator PremiumMeteorStormRoutine()
        {
            float elapsed = 0f;
            float nextSpawn = 0f;
            int spawnCount = 0;
            float intensity = 0f;
            
            // Start with distant rumble
            if (m_MeteorRumble != null)
            {
                m_AudioSource.clip = m_MeteorRumble;
                m_AudioSource.loop = true;
                m_AudioSource.volume = 0.3f;
                m_AudioSource.Play();
            }
            
            while (elapsed < m_MeteorDuration)
            {
                elapsed += Time.deltaTime;
                
                // Intensity curve
                if (elapsed < 3f)
                    intensity = Mathf.SmoothStep(0f, 1f, elapsed / 3f);
                else if (elapsed > m_MeteorDuration - 3f)
                    intensity = Mathf.SmoothStep(1f, 0f, (elapsed - (m_MeteorDuration - 3f)) / 3f);
                else
                    intensity = 1f;
                
                // Update rumble volume
                if (m_AudioSource != null)
                    m_AudioSource.volume = 0.3f + intensity * 0.4f;
                
                // Spawn meteors
                if (elapsed >= nextSpawn)
                {
                    float adjustedRate = m_MeteorSpawnRate / intensity.Clamp(0.3f, 1f);
                    nextSpawn = elapsed + adjustedRate;
                    
                    // Chance for dramatic meteor
                    bool dramatic = Random.value < m_DramaticMeteorChance * intensity;
                    SpawnPremiumMeteor(dramatic);
                    spawnCount++;
                }
                
                yield return null;
            }
            
            if (m_AudioSource != null) m_AudioSource.Stop();
        }

        private void SpawnPremiumMeteor(bool dramatic)
        {
            Vector3 playerPos = GetPlayerPosition();
            
            // All meteors travel in the same direction (left to right, slightly above)
            // Spawn far to the left and above the player, travel to the right
            float spawnHeight = playerPos.y + Random.Range(20f, 50f); // Well above platform
            float spawnDepth = playerPos.z + Random.Range(-30f, 30f); // Varied depth
            float spawnDistance = m_SpawnDistance + Random.Range(0f, 20f);
            
            Vector3 spawnPos = new Vector3(
                playerPos.x - spawnDistance,
                spawnHeight,
                spawnDepth
            );
            
            // Move direction: parallel to ground, left to right with slight downward angle
            Vector3 moveDir = new Vector3(1f, -0.1f, Random.Range(-0.1f, 0.1f)).normalized;
            
            GameObject meteor = Instantiate(m_MeteorPrefab, spawnPos, Random.rotation);
            float scale = dramatic ? Random.Range(5f, 8f) : Random.Range(0.5f, 3f);
            meteor.transform.localScale = Vector3.one * scale;
            
            // Setup rigidbody
            Rigidbody rb = meteor.GetComponent<Rigidbody>();
            if (rb == null) rb = meteor.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            
            float speed = dramatic ? m_MeteorMaxSpeed * 1.5f : Random.Range(m_MeteorMinSpeed, m_MeteorMaxSpeed);
            rb.linearVelocity = moveDir * speed;
            rb.angularVelocity = Random.insideUnitSphere * (dramatic ? 5f : 3f);
            
            // Premium trail
            AddPremiumTrail(meteor, scale, dramatic);
            
            // Add glow light for dramatic meteors
            if (dramatic)
            {
                Light glow = meteor.AddComponent<Light>();
                glow.type = LightType.Point;
                glow.color = new Color(1f, 0.5f, 0.2f);
                glow.intensity = 5f;
                glow.range = scale * 5f;
                
                // Play whoosh sound
                if (m_MeteorWhoosh != null)
                {
                    m_SecondaryAudio.PlayOneShot(m_MeteorWhoosh, 0.8f);
                }
                
                // Camera shake for near passes
                StartCoroutine(MeteorShake(0.5f, 0.03f));
            }
            
            m_ActiveObjects.Add(meteor);
            float lifetime = (spawnDistance * 2.5f) / speed;
            Destroy(meteor, lifetime);
            
            // Animate scale in/out
            StartCoroutine(AnimateMeteorScale(meteor, scale, lifetime));
        }

        private IEnumerator AnimateMeteorScale(GameObject meteor, float targetScale, float lifetime)
        {
            if (meteor == null) yield break;
            
            float elapsed = 0f;
            meteor.transform.localScale = Vector3.zero;
            
            // Scale In
            while (elapsed < 1f && meteor != null)
            {
                elapsed += Time.deltaTime;
                float s = Mathf.Lerp(0f, targetScale, elapsed);
                meteor.transform.localScale = Vector3.one * s;
                yield return null;
            }
            
            if (meteor == null) yield break;
            
            // Wait
            float waitTime = lifetime - 2f; // 1s in, 1s out
            if (waitTime > 0)
            {
                yield return new WaitForSeconds(waitTime);
            }
            
            // Scale Out
            elapsed = 0f;
            while (elapsed < 1f && meteor != null)
            {
                elapsed += Time.deltaTime;
                float s = Mathf.Lerp(targetScale, 0f, elapsed);
                meteor.transform.localScale = Vector3.one * s;
                yield return null;
            }
        }

        private void AddPremiumTrail(GameObject meteor, float scale, bool dramatic)
        {
            TrailRenderer trail = meteor.GetComponent<TrailRenderer>();
            if (trail == null) trail = meteor.AddComponent<TrailRenderer>();
            
            trail.time = dramatic ? 1f : 0.5f;
            trail.startWidth = scale * (dramatic ? 1f : 0.5f);
            trail.endWidth = 0f;
            
            // Gradient for fire effect
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 0.9f, 0.6f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.3f),
                    new GradientColorKey(new Color(0.8f, 0.2f, 0f), 0.7f),
                    new GradientColorKey(new Color(0.3f, 0.1f, 0f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            trail.colorGradient = gradient;
            
            Material trailMat = new Material(Shader.Find("Particles/Standard Unlit"));
            if (trailMat != null)
            {
                trailMat.SetColor("_Color", Color.white);
            }
            trail.material = trailMat;
        }

        private IEnumerator MeteorShake(float duration, float magnitude)
        {
            if (m_MainCamera == null) yield break;
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float decay = 1f - (elapsed / duration);
                m_MainCamera.transform.localPosition += Random.insideUnitSphere * magnitude * decay;
                yield return null;
            }
        }

        #region Helper Methods

        private Vector3 GetPlayerPosition()
        {
            return m_MainCamera != null ? m_MainCamera.transform.position : transform.position;
        }

        private void CleanupActiveObjects()
        {
            foreach (var obj in m_ActiveObjects)
            {
                if (obj != null) Destroy(obj);
            }
            m_ActiveObjects.Clear();
        }

        private void RestoreAmbient()
        {
            RenderSettings.ambientLight = m_OriginalAmbientColor;
            RenderSettings.ambientIntensity = m_OriginalAmbientIntensity;
        }

        private Material CreateUnlitMaterial(Color color)
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (mat == null) mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = color;
            return mat;
        }

        private Material CreateEmissiveMaterial(Color color, float intensity)
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat == null) mat = new Material(Shader.Find("Standard"));
            mat.color = color;
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * intensity);
            return mat;
        }

        private Vector3 QuadraticBezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            Vector3 ab = Vector3.Lerp(a, b, t);
            Vector3 bc = Vector3.Lerp(b, c, t);
            return Vector3.Lerp(ab, bc, t);
        }

        private Vector3 CubicBezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            Vector3 ab = Vector3.Lerp(a, b, t);
            Vector3 bc = Vector3.Lerp(b, c, t);
            Vector3 cd = Vector3.Lerp(c, d, t);
            Vector3 abc = Vector3.Lerp(ab, bc, t);
            Vector3 bcd = Vector3.Lerp(bc, cd, t);
            return Vector3.Lerp(abc, bcd, t);
        }

        #endregion
    }

    public static class FloatExtensions
    {
        public static float Clamp(this float value, float min, float max)
        {
            return Mathf.Clamp(value, min, max);
        }
    }
}
