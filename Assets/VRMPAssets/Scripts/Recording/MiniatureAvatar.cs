using UnityEngine;

namespace XRMultiplayer.Recording
{
    /// <summary>
    /// Visual representation of a miniature avatar used for playback visualization.
    /// Contains references to head and hand transforms for animation.
    /// </summary>
    public class MiniatureAvatar : MonoBehaviour
    {
        public Transform head;
        public Transform leftHand;
        public Transform rightHand;

        /// <summary>
        /// Sets the color of all avatar renderers with emission for visibility.
        /// </summary>
        public void SetColor(Color color)
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return;
            
            foreach (var r in renderers)
            {
                r.material.color = color;
                r.material.EnableKeyword("_EMISSION");
                r.material.SetColor("_EmissionColor", color * 0.6f);
            }
        }
    }
}
