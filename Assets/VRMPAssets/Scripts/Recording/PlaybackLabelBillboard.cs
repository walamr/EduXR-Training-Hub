using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Simple billboard component that makes text labels face the camera.
    /// Used for playback avatar name labels.
    /// </summary>
    public class PlaybackLabelBillboard : MonoBehaviour
    {
        private Camera m_Camera;
        
        public void Initialize(Camera camera)
        {
            m_Camera = camera;
        }
        
        void LateUpdate()
        {
            if (m_Camera == null)
            {
                m_Camera = Camera.main;
                if (m_Camera == null) return;
            }
            
            // Face camera (billboard effect)
            transform.rotation = Quaternion.LookRotation(
                transform.position - m_Camera.transform.position
            );
        }
    }
}
