using UnityEngine;

namespace XRMultiplayer.Drawing
{
    /// <summary>
    /// Visual helper for the drawing surface. Shows bounds in editor and optional preview in play mode.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class DrawingSurfaceVisualizer : MonoBehaviour
    {
        [Header("Editor Visualization")]
        [SerializeField] private Color m_GizmoColor = new Color(0f, 1f, 0.5f, 0.3f);
        [SerializeField] private Color m_GizmoWireColor = new Color(0f, 1f, 0.5f, 1f);
        
        [Header("Runtime Preview (Optional)")]
        [SerializeField] private bool m_ShowPreviewInPlayMode = false;
        [SerializeField] private Color m_PreviewColor = new Color(1f, 1f, 1f, 0.1f);
        
        private BoxCollider m_Collider;
        private GameObject m_PreviewQuad;

        private void Awake()
        {
            m_Collider = GetComponent<BoxCollider>();
            
            if (m_ShowPreviewInPlayMode)
            {
                CreatePreviewQuad();
            }
        }

        private void OnDestroy()
        {
            if (m_PreviewQuad != null)
            {
                Destroy(m_PreviewQuad);
            }
        }

        private void CreatePreviewQuad()
        {
            m_PreviewQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m_PreviewQuad.name = "DrawingSurface_Preview";
            m_PreviewQuad.transform.SetParent(transform, false);
            m_PreviewQuad.transform.localPosition = Vector3.zero;
            m_PreviewQuad.transform.localRotation = Quaternion.identity;
            
            // Match collider size
            if (m_Collider != null)
            {
                m_PreviewQuad.transform.localScale = new Vector3(m_Collider.size.x, m_Collider.size.y, 1f);
            }
            
            // Remove collider from quad (we use the BoxCollider on parent)
            Destroy(m_PreviewQuad.GetComponent<Collider>());
            
            // Apply transparent material
            var renderer = m_PreviewQuad.GetComponent<Renderer>();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetFloat("_Surface", 1); // Transparent
            mat.SetFloat("_Blend", 0); // Alpha
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            mat.color = m_PreviewColor;
            renderer.material = mat;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            DrawBoundsGizmo(false);
        }

        private void OnDrawGizmosSelected()
        {
            DrawBoundsGizmo(true);
        }

        private void DrawBoundsGizmo(bool selected)
        {
            if (m_Collider == null)
                m_Collider = GetComponent<BoxCollider>();
            
            if (m_Collider == null) return;

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            Vector3 center = m_Collider.center;
            Vector3 size = m_Collider.size;

            // Draw filled quad
            Gizmos.color = selected ? m_GizmoColor : new Color(m_GizmoColor.r, m_GizmoColor.g, m_GizmoColor.b, m_GizmoColor.a * 0.5f);
            Gizmos.DrawCube(center, new Vector3(size.x, size.y, 0.001f));

            // Draw wireframe
            Gizmos.color = selected ? m_GizmoWireColor : new Color(m_GizmoWireColor.r, m_GizmoWireColor.g, m_GizmoWireColor.b, 0.5f);
            Gizmos.DrawWireCube(center, size);

            // Draw forward direction arrow
            Gizmos.color = Color.blue;
            Vector3 arrowStart = center;
            Vector3 arrowEnd = center + Vector3.forward * 0.3f;
            Gizmos.DrawLine(arrowStart, arrowEnd);
            
            // Arrowhead
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(0.05f, 0, -0.1f));
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(-0.05f, 0, -0.1f));

            // Draw corner markers
            Gizmos.color = Color.yellow;
            float markerSize = 0.02f;
            Vector3 halfSize = size * 0.5f;
            
            // Four corners
            Gizmos.DrawSphere(center + new Vector3(-halfSize.x, -halfSize.y, 0), markerSize);
            Gizmos.DrawSphere(center + new Vector3(halfSize.x, -halfSize.y, 0), markerSize);
            Gizmos.DrawSphere(center + new Vector3(-halfSize.x, halfSize.y, 0), markerSize);
            Gizmos.DrawSphere(center + new Vector3(halfSize.x, halfSize.y, 0), markerSize);

            // Draw "FRONT" label direction indicator
            UnityEditor.Handles.matrix = transform.localToWorldMatrix;
            UnityEditor.Handles.color = Color.cyan;
            UnityEditor.Handles.Label(center + Vector3.forward * 0.35f, "FRONT\n(Draw here)");
            UnityEditor.Handles.Label(center + Vector3.back * 0.1f, "BACK\n(TV side)");

            Gizmos.matrix = oldMatrix;
        }
#endif
    }
}
