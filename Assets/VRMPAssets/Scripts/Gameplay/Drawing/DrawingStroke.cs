using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer.Drawing
{
    /// <summary>
    /// Represents a single drawing stroke using LineRenderer.
    /// Supports color, width, hand shake smoothing, and optional mesh baking for interactable deletion.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class DrawingStroke : MonoBehaviour
    {
        // Add ID for networking/erasing
        public string StrokeId { get; set; }

        private LineRenderer m_LineRenderer;
        private List<Vector3> m_Points = new List<Vector3>();           // Raw input points
        private List<Vector3> m_SmoothedPoints = new List<Vector3>();   // Smoothed points for display
        private bool m_IsFinalized = false;
        private Color m_Color;

        private MeshCollider m_MeshCollider;
        
        // ========== Hand Shake Smoothing Settings ==========
        [Header("Smoothing Settings")]
        // TUNED: Reduced buffer to 2 (minimal averaging to prevent corner cutting)
        private const int SMOOTHING_BUFFER_SIZE = 2;        
        private const float MIN_DISTANCE = 0.001f;          // 1mm (capture almost all points)
        // TUNED: Increased factor to 0.9 (90% raw input) for max responsiveness
        private const float SMOOTHING_FACTOR = 0.9f;        
        private const int SPLINE_SUBDIVISIONS = 2;          // Subdivisions between points for Catmull-Rom spline
        
        private Queue<Vector3> m_SmoothingBuffer = new Queue<Vector3>();
        private Vector3 m_LastSmoothedPoint;
        private bool m_HasFirstPoint = false;

        public void Initialize(Color color, float width, Material material)
        {
            m_Color = color;

            m_LineRenderer = GetComponent<LineRenderer>();
            if (m_LineRenderer == null)
            {
                m_LineRenderer = gameObject.AddComponent<LineRenderer>();
            }

            // Configure LineRenderer
            m_LineRenderer.positionCount = 0;
            m_LineRenderer.startWidth = width;
            m_LineRenderer.endWidth = width;
            m_LineRenderer.useWorldSpace = true;
            m_LineRenderer.numCapVertices = 5;  // Increased for smoother caps
            m_LineRenderer.numCornerVertices = 5;  // Increased for smoother corners

            // Set material
            if (material != null)
            {
                m_LineRenderer.material = new Material(material);
            }
            else
            {
                // Create unlit material for drawing lines
                m_LineRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            }

            m_LineRenderer.material.color = color;
            m_LineRenderer.startColor = color;
            m_LineRenderer.endColor = color;
            
            // Clear smoothing state
            m_SmoothingBuffer.Clear();
            m_HasFirstPoint = false;
        }

        /// <summary>
        /// Add a point to the stroke with hand shake smoothing applied.
        /// Uses a combination of moving average and exponential smoothing to reduce jitter.
        /// </summary>
        public void AddPoint(Vector3 worldPoint)
        {
            if (m_IsFinalized) return;

            // Store raw point
            m_Points.Add(worldPoint);
            
            // Apply smoothing
            Vector3 smoothedPoint = ApplySmoothing(worldPoint);
            
            // For the first point, add it directly
            if (!m_HasFirstPoint)
            {
                m_HasFirstPoint = true;
                m_LastSmoothedPoint = smoothedPoint;
                m_SmoothedPoints.Add(smoothedPoint);
                UpdateLineRenderer();
                return;
            }
            
            // Check minimum distance from last smoothed point
            if (Vector3.Distance(m_LastSmoothedPoint, smoothedPoint) < MIN_DISTANCE)
            {
                return;
            }
            
            // Add the smoothed point
            m_SmoothedPoints.Add(smoothedPoint);
            m_LastSmoothedPoint = smoothedPoint;
            
            UpdateLineRenderer();
        }
        
        /// <summary>
        /// Apply moving average + exponential smoothing to reduce hand shake.
        /// </summary>
        private Vector3 ApplySmoothing(Vector3 newPoint)
        {
            // Add to smoothing buffer
            m_SmoothingBuffer.Enqueue(newPoint);
            
            // Keep buffer at max size
            while (m_SmoothingBuffer.Count > SMOOTHING_BUFFER_SIZE)
            {
                m_SmoothingBuffer.Dequeue();
            }
            
            // Calculate moving average
            Vector3 average = Vector3.zero;
            foreach (var point in m_SmoothingBuffer)
            {
                average += point;
            }
            average /= m_SmoothingBuffer.Count;
            
            // Apply exponential smoothing if we have a previous point
            if (m_HasFirstPoint)
            {
                // Blend between last smoothed point and new average
                // Lower SMOOTHING_FACTOR = smoother but more lag
                return Vector3.Lerp(m_LastSmoothedPoint, average, SMOOTHING_FACTOR);
            }
            
            return average;
        }
        
        /// <summary>
        /// Update the LineRenderer with optionally spline-interpolated points.
        /// </summary>
        private void UpdateLineRenderer()
        {
            if (m_SmoothedPoints.Count < 2)
            {
                // Not enough points for spline, just display what we have
                m_LineRenderer.positionCount = m_SmoothedPoints.Count;
                for (int i = 0; i < m_SmoothedPoints.Count; i++)
                {
                    m_LineRenderer.SetPosition(i, m_SmoothedPoints[i]);
                }
                return;
            }
            
            // For real-time drawing, use direct points (spline smoothing applied on finalize)
            // This provides a good balance between responsiveness and smoothness
            m_LineRenderer.positionCount = m_SmoothedPoints.Count;
            for (int i = 0; i < m_SmoothedPoints.Count; i++)
            {
                m_LineRenderer.SetPosition(i, m_SmoothedPoints[i]);
            }
        }

        public void FinalizeStroke()
        {
            if (m_IsFinalized) return;
            m_IsFinalized = true;

            // Apply final Catmull-Rom spline smoothing for extra polish
            if (m_SmoothedPoints.Count >= 4)
            {
                ApplyCatmullRomSpline();
            }

            // Bake mesh for collision detection (eraser functionality)
            if (m_SmoothedPoints.Count >= 2)
            {
                BakeMeshCollider();
            }
        }
        
        /// <summary>
        /// Apply Catmull-Rom spline interpolation for final ultra-smooth curves.
        /// Called on finalization for maximum visual quality.
        /// </summary>
        private void ApplyCatmullRomSpline()
        {
            if (m_SmoothedPoints.Count < 4) return;
            
            List<Vector3> splinePoints = new List<Vector3>();
            
            // Add the first point
            splinePoints.Add(m_SmoothedPoints[0]);
            
            // Interpolate between each segment
            for (int i = 0; i < m_SmoothedPoints.Count - 1; i++)
            {
                Vector3 p0 = m_SmoothedPoints[Mathf.Max(0, i - 1)];
                Vector3 p1 = m_SmoothedPoints[i];
                Vector3 p2 = m_SmoothedPoints[Mathf.Min(m_SmoothedPoints.Count - 1, i + 1)];
                Vector3 p3 = m_SmoothedPoints[Mathf.Min(m_SmoothedPoints.Count - 1, i + 2)];
                
                // Add subdivided points along the spline segment
                for (int j = 1; j <= SPLINE_SUBDIVISIONS; j++)
                {
                    float t = (float)j / (SPLINE_SUBDIVISIONS + 1);
                    Vector3 interpolated = CatmullRom(p0, p1, p2, p3, t);
                    splinePoints.Add(interpolated);
                }
                
                // Add the end point of this segment (except for the last segment)
                if (i < m_SmoothedPoints.Count - 2)
                {
                    splinePoints.Add(p2);
                }
            }
            
            // Add the last point
            splinePoints.Add(m_SmoothedPoints[m_SmoothedPoints.Count - 1]);
            
            // Update the line renderer with spline points
            m_LineRenderer.positionCount = splinePoints.Count;
            m_LineRenderer.SetPositions(splinePoints.ToArray());
            
            // Update internal list for collider baking
            m_SmoothedPoints = splinePoints;
        }
        
        /// <summary>
        /// Catmull-Rom spline interpolation between four control points.
        /// </summary>
        private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        private void BakeMeshCollider()
        {
            try
            {
                // Use BoxCollider on the bounding box - much more reliable than MeshCollider
                if (m_SmoothedPoints.Count < 2) return;
                
                // Calculate bounds of all points
                Bounds bounds = new Bounds(m_SmoothedPoints[0], Vector3.zero);
                foreach (var point in m_SmoothedPoints)
                {
                    bounds.Encapsulate(point);
                }
                
                // Expand bounds slightly for the line width
                float lineWidth = m_LineRenderer.startWidth;
                bounds.Expand(lineWidth * 2f);
                
                // Add BoxCollider
                BoxCollider boxCollider = gameObject.AddComponent<BoxCollider>();
                
                // Convert world bounds to local space
                boxCollider.center = transform.InverseTransformPoint(bounds.center);
                boxCollider.size = bounds.size;
                
                Debug.Log($"[DrawingStroke] Created BoxCollider: center={boxCollider.center}, size={boxCollider.size}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DrawingStroke] Failed to create collider: {e.Message}");
            }
        }

        public Color GetColor()
        {
            return m_Color;
        }

        public int GetPointCount()
        {
            return m_SmoothedPoints.Count;
        }

        public Vector3[] GetPoints()
        {
            return m_SmoothedPoints.ToArray();
        }
        
        /// <summary>
        /// Get the raw (unsmoothed) points for debugging.
        /// </summary>
        public Vector3[] GetRawPoints()
        {
            return m_Points.ToArray();
        }
    }
}
