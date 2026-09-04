using UnityEngine;
using System.Collections.Generic;

namespace XRMultiplayer
{
    /// <summary>
    /// Generates a procedural asteroid mesh at runtime.
    /// Used for the Meteor Shower event to avoid external asset dependencies.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Rigidbody))]
    public class ProceduralAsteroid : MonoBehaviour
    {
        [Header("Generation Settings")]
        [SerializeField] private float m_Radius = 1f;
        [SerializeField] private int m_Subdivisions = 2; // Low poly
        [SerializeField] private float m_NoiseAmplitude = 0.5f;
        [SerializeField] private float m_NoiseFrequency = 2f;
        [SerializeField] private int m_RandomSeed = 0; // 0 = random on start

        private void Start()
        {
            if (m_RandomSeed == 0) m_RandomSeed = Random.Range(1, 10000);
            GenerateMesh();
            
            // Setup Physics
            var rb = GetComponent<Rigidbody>();
            rb.useGravity = false;
            
            // Add collider if missing
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<SphereCollider>();
                col.radius = m_Radius;
            }
        }

        private void GenerateMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "AsteroidMesh";

            // 1. Create Icosahedron (Base Sphere)
            CreateIcosahedron(out Vector3[] vertices, out int[] triangles);

            // 2. Subdivide
            for (int i = 0; i < m_Subdivisions; i++)
            {
                Subdivide(ref vertices, ref triangles);
            }

            // 3. Apply Noise (Jagged Shape)
            ApplyNoise(ref vertices);

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            GetComponent<MeshFilter>().mesh = mesh;
        }

        private void ApplyNoise(ref Vector3[] vertices)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                // Project to sphere
                Vector3 p = v.normalized * m_Radius;
                
                // Add Perlin Noise for "Jaggedness"
                float noise = Mathf.PerlinNoise(
                    (p.x + m_RandomSeed) * m_NoiseFrequency, 
                    (p.y + m_RandomSeed) * m_NoiseFrequency
                );
                
                // Randomize vertex position along its normal
                float displacement = (noise * 2f - 1f) * m_NoiseAmplitude;
                vertices[i] = p + (v.normalized * displacement);
            }
        }

        // --- Icosahedron & Subdivision Helpers ---
        // (Simplified implementation for brevity, creating roughly spherical mesh)
        
        private void CreateIcosahedron(out Vector3[] vertices, out int[] triangles)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            vertices = new Vector3[] {
                new Vector3(-1, t, 0).normalized, new Vector3(1, t, 0).normalized,
                new Vector3(-1, -t, 0).normalized, new Vector3(1, -t, 0).normalized,
                new Vector3(0, -1, t).normalized, new Vector3(0, 1, t).normalized,
                new Vector3(0, -1, -t).normalized, new Vector3(0, 1, -t).normalized,
                new Vector3(t, 0, -1).normalized, new Vector3(t, 0, 1).normalized,
                new Vector3(-t, 0, -1).normalized, new Vector3(-t, 0, 1).normalized
            };

            triangles = new int[] {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };
        }

        private void Subdivide(ref Vector3[] vertices, ref int[] triangles)
        {
            var newTriangles = new List<int>();
            var newVertices = new List<Vector3>(vertices);
            var midPointCache = new Dictionary<(int, int), int>();

            int GetMidPointIndex(int p1, int p2)
            {
                int smaller = Mathf.Min(p1, p2);
                int larger = Mathf.Max(p1, p2);
                var key = (smaller, larger);

                if (midPointCache.TryGetValue(key, out int index)) return index;

                Vector3 v1 = newVertices[p1];
                Vector3 v2 = newVertices[p2];
                Vector3 mid = ((v1 + v2) / 2f).normalized; 
                
                index = newVertices.Count;
                newVertices.Add(mid);
                midPointCache[key] = index;
                return index;
            }

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v1 = triangles[i];
                int v2 = triangles[i + 1];
                int v3 = triangles[i + 2];

                int a = GetMidPointIndex(v1, v2);
                int b = GetMidPointIndex(v2, v3);
                int c = GetMidPointIndex(v3, v1);

                newTriangles.AddRange(new int[] { v1, a, c, v2, b, a, v3, c, b, a, b, c });
            }

            vertices = newVertices.ToArray();
            triangles = newTriangles.ToArray();
        }
    }
}
