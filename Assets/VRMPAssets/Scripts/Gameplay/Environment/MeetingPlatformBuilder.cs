using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Builds an impressive 3D meeting platform with proper depth, supports, and edge details.
    /// This is not just a flat cube - it's a proper elevated platform connected to the ground.
    /// </summary>
    public class MeetingPlatformBuilder : MonoBehaviour
    {
        [Header("Platform Dimensions")]
        [SerializeField] private float m_PlatformWidth = 10f;
        [SerializeField] private float m_PlatformDepth = 10f;
        [SerializeField] private float m_PlatformHeight = 0.3f;
        [SerializeField] private float m_ElevationFromGround = 0.5f;

        [Header("Support Pillars")]
        [SerializeField] private int m_PillarsPerSide = 3;
        [SerializeField] private float m_PillarWidth = 0.3f;

        [Header("Edge Details")]
        [SerializeField] private bool m_AddEdgeRim = true;
        [SerializeField] private float m_RimHeight = 0.05f;
        [SerializeField] private float m_RimWidth = 0.1f;

        [Header("Materials")]
        [SerializeField] private Material m_PlatformMaterial;
        [SerializeField] private Material m_PillarMaterial;
        [SerializeField] private Material m_RimMaterial;

        [Header("Lighting")]
        [SerializeField] private bool m_AddEdgeLights = true;
        [SerializeField] private Color m_EdgeLightColor = new Color(0.2f, 0.6f, 1f, 1f);
        [SerializeField] private float m_EdgeLightIntensity = 2f;

        private List<GameObject> m_GeneratedParts = new List<GameObject>();

        /// <summary>
        /// Call this to generate the full platform structure.
        /// </summary>
        [ContextMenu("Generate Platform")]
        public void GeneratePlatform()
        {
            ClearPlatform();

            // Main platform top
            CreatePlatformTop();

            // Support pillars
            CreateSupportPillars();

            // Edge rim
            if (m_AddEdgeRim)
                CreateEdgeRim();

            // Edge lights
            if (m_AddEdgeLights)
                CreateEdgeLights();
        }

        [ContextMenu("Clear Platform")]
        public void ClearPlatform()
        {
            foreach (var part in m_GeneratedParts)
            {
                if (part != null)
                    DestroyImmediate(part);
            }
            m_GeneratedParts.Clear();

            // Also destroy children
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(transform.GetChild(i).gameObject);
            }
        }

        private void CreatePlatformTop()
        {
            var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
            top.name = "PlatformTop";
            top.transform.SetParent(transform);
            top.transform.localPosition = new Vector3(0, m_ElevationFromGround + m_PlatformHeight / 2f, 0);
            top.transform.localScale = new Vector3(m_PlatformWidth, m_PlatformHeight, m_PlatformDepth);

            if (m_PlatformMaterial != null)
                top.GetComponent<Renderer>().material = m_PlatformMaterial;

            m_GeneratedParts.Add(top);
        }

        private void CreateSupportPillars()
        {
            float pillarHeight = m_ElevationFromGround;
            float halfWidth = m_PlatformWidth / 2f - m_PillarWidth;
            float halfDepth = m_PlatformDepth / 2f - m_PillarWidth;

            // Create pillars along each edge
            for (int i = 0; i < m_PillarsPerSide; i++)
            {
                float t = (float)i / (m_PillarsPerSide - 1);

                // Front edge
                CreatePillar(new Vector3(Mathf.Lerp(-halfWidth, halfWidth, t), pillarHeight / 2f, -halfDepth));

                // Back edge
                CreatePillar(new Vector3(Mathf.Lerp(-halfWidth, halfWidth, t), pillarHeight / 2f, halfDepth));

                // Left edge (skip corners)
                if (i > 0 && i < m_PillarsPerSide - 1)
                    CreatePillar(new Vector3(-halfWidth, pillarHeight / 2f, Mathf.Lerp(-halfDepth, halfDepth, t)));

                // Right edge (skip corners)
                if (i > 0 && i < m_PillarsPerSide - 1)
                    CreatePillar(new Vector3(halfWidth, pillarHeight / 2f, Mathf.Lerp(-halfDepth, halfDepth, t)));
            }
        }

        private void CreatePillar(Vector3 localPos)
        {
            var pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pillar.name = "SupportPillar";
            pillar.transform.SetParent(transform);
            pillar.transform.localPosition = localPos;
            pillar.transform.localScale = new Vector3(m_PillarWidth, m_ElevationFromGround, m_PillarWidth);

            if (m_PillarMaterial != null)
                pillar.GetComponent<Renderer>().material = m_PillarMaterial;
            else if (m_PlatformMaterial != null)
                pillar.GetComponent<Renderer>().material = m_PlatformMaterial;

            m_GeneratedParts.Add(pillar);
        }

        private void CreateEdgeRim()
        {
            float rimY = m_ElevationFromGround + m_PlatformHeight + m_RimHeight / 2f;

            // Front rim
            CreateRimSection(
                new Vector3(0, rimY, -m_PlatformDepth / 2f + m_RimWidth / 2f),
                new Vector3(m_PlatformWidth, m_RimHeight, m_RimWidth));

            // Back rim
            CreateRimSection(
                new Vector3(0, rimY, m_PlatformDepth / 2f - m_RimWidth / 2f),
                new Vector3(m_PlatformWidth, m_RimHeight, m_RimWidth));

            // Left rim
            CreateRimSection(
                new Vector3(-m_PlatformWidth / 2f + m_RimWidth / 2f, rimY, 0),
                new Vector3(m_RimWidth, m_RimHeight, m_PlatformDepth - m_RimWidth * 2));

            // Right rim
            CreateRimSection(
                new Vector3(m_PlatformWidth / 2f - m_RimWidth / 2f, rimY, 0),
                new Vector3(m_RimWidth, m_RimHeight, m_PlatformDepth - m_RimWidth * 2));
        }

        private void CreateRimSection(Vector3 localPos, Vector3 scale)
        {
            var rim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rim.name = "EdgeRim";
            rim.transform.SetParent(transform);
            rim.transform.localPosition = localPos;
            rim.transform.localScale = scale;

            if (m_RimMaterial != null)
                rim.GetComponent<Renderer>().material = m_RimMaterial;

            // Remove collider from rim
            DestroyImmediate(rim.GetComponent<Collider>());

            m_GeneratedParts.Add(rim);
        }

        private void CreateEdgeLights()
        {
            float lightY = m_ElevationFromGround + m_PlatformHeight + 0.1f;
            float halfW = m_PlatformWidth / 2f;
            float halfD = m_PlatformDepth / 2f;

            // Corner lights
            CreateEdgeLight(new Vector3(-halfW, lightY, -halfD));
            CreateEdgeLight(new Vector3(halfW, lightY, -halfD));
            CreateEdgeLight(new Vector3(-halfW, lightY, halfD));
            CreateEdgeLight(new Vector3(halfW, lightY, halfD));

            // Mid-edge lights
            CreateEdgeLight(new Vector3(0, lightY, -halfD));
            CreateEdgeLight(new Vector3(0, lightY, halfD));
            CreateEdgeLight(new Vector3(-halfW, lightY, 0));
            CreateEdgeLight(new Vector3(halfW, lightY, 0));
        }

        private void CreateEdgeLight(Vector3 localPos)
        {
            var lightGO = new GameObject("EdgeLight");
            lightGO.transform.SetParent(transform);
            lightGO.transform.localPosition = localPos;

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = m_EdgeLightColor;
            light.intensity = m_EdgeLightIntensity;
            light.range = 3f;

            m_GeneratedParts.Add(lightGO);
        }

        /// <summary>
        /// Positions the platform to be centered under the meeting table.
        /// </summary>
        public void CenterOnTable(Transform tableTransform)
        {
            if (tableTransform == null) return;
            transform.position = new Vector3(tableTransform.position.x, 0, tableTransform.position.z);
        }
    }
}
