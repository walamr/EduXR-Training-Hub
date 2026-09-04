using UnityEngine;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Defines the visual configuration for a private room scene.
    /// </summary>
    [CreateAssetMenu(fileName = "NewPrivateRoomVisualProfile", menuName = "XR Multiplayer/Private Room Visual Profile")]
    public class PrivateRoomVisualProfile : ScriptableObject
    {
        [Header("Theme: Cozy Lounge")]
        [Tooltip("The skybox material to use for this private room.")]
        public Material SkyboxMaterial;

        [Header("Lighting")]
        [Tooltip("Ambient light color (e.g., warm 2700K-3200K).")]
        public Color AmbientColor = new Color(1.0f, 0.9f, 0.8f);
        [Tooltip("Ambient light intensity.")]
        public float AmbientIntensity = 1.2f;

        [Header("Materials")]
        [Tooltip("Material for the floor (e.g., wood or carpet).")]
        public Material FloorMaterial;
        [Tooltip("Material for the walls (e.g., warm neutral fabric or paint).")]
        public Material WallMaterial;

        [Header("UI Skin")]
        [Tooltip("Base color for UI panels in the private room.")]
        public Color UIPanelColor = new Color(0.95f, 0.9f, 0.85f, 0.9f);
        [Tooltip("Accent color for UI elements (e.g., buttons, highlights).")]
        public Color UIAccentColor = new Color(0.8f, 0.5f, 0.3f, 1.0f);

        [Header("Audio")]
        [Tooltip("Subtle ambient room tone (e.g., soft hum or air).")]
        public AudioClip AmbientAudioClip;
        [Range(0f, 1f)]
        public float AmbientAudioVolume = 0.2f;

        /// <summary>
        /// Applies this visual profile to the current scene.
        /// </summary>
        public void Apply()
        {
            if (SkyboxMaterial != null)
            {
                RenderSettings.skybox = SkyboxMaterial;
            }

            RenderSettings.ambientLight = AmbientColor;
            RenderSettings.ambientIntensity = AmbientIntensity;
            DynamicGI.UpdateEnvironment();

            // Note: Applying materials to specific objects and updating UI colors 
            // would require references to those objects in the scene, which could be 
            // handled by a PrivateRoomSceneController that reads this profile.
        }
    }
}
