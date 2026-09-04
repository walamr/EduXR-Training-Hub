using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Detects head nod (Yes) and shake (No) gestures with improved accuracy.
    /// Features: noise filtering, peak/valley detection, confidence scoring, and rhythm analysis.
    /// </summary>
    public class HeadGestureDetector : MonoBehaviour
    {
        [Header("Detection Settings")]
        [Tooltip("Time window in seconds to analyze for gestures")]
        [SerializeField] float m_TimeWindow = 1.0f;
        
        [Tooltip("Minimum angle change to register as a peak/valley (degrees)")]
        [SerializeField] float m_PeakThreshold = 5.0f;  // Lowered from 8 for easier detection
        
        [Tooltip("Minimum angular velocity to consider intentional gesture (degrees/sec)")]
        [SerializeField] float m_MinVelocity = 20.0f;  // Lowered from 30 for easier detection
        
        [Tooltip("Number of oscillations (peaks + valleys) required for detection")]
        [SerializeField] int m_RequiredOscillations = 2;  // Lowered from 3 for easier detection
        
        [Tooltip("Max total range of movement - filters out 'looking around' (degrees)")]
        [SerializeField] float m_MaxTotalRange = 60.0f;  // Increased from 50 for more tolerance
        
        [Tooltip("Min range required - filters out micro-movements (degrees)")]
        [SerializeField] float m_MinTotalRange = 6.0f;  // Lowered from 10 for easier detection
        
        [Tooltip("Cooldown between gesture detections (seconds)")]
        [SerializeField] float m_Cooldown = 1.0f;  // Lowered from 1.2 for faster response
        
        [Tooltip("Minimum confidence score required (0-1)")]
        [SerializeField] float m_MinConfidence = 0.4f;  // Lowered from 0.6 for easier detection
        
        [Header("Noise Filtering")]
        [Tooltip("Smoothing factor for exponential moving average (0 = max smooth, 1 = no smooth)")]
        [SerializeField] float m_SmoothingFactor = 0.3f;
        
        [Tooltip("Velocity smoothing factor")]
        [SerializeField] float m_VelocitySmoothingFactor = 0.4f;
        
        [Header("Debug")]
        [SerializeField] bool m_DebugMode = true;

        // Events with confidence scores
        public event Action OnNodDetected;
        public event Action OnShakeDetected;
        public event Action<float> OnNodDetectedWithConfidence;
        public event Action<float> OnShakeDetectedWithConfidence;

        struct Sample 
        { 
            public float Time;
            public float Pitch;          // Raw pitch
            public float Yaw;             // Raw yaw
            public float SmoothedPitch;   // Filtered pitch
            public float SmoothedYaw;     // Filtered yaw
            public float PitchVelocity;   // Smoothed velocity
            public float YawVelocity;     // Smoothed velocity
        }
        
        struct Peak
        {
            public float Time;
            public float Value;
            public bool IsMax;  // true = peak, false = valley
        }
        
        readonly List<Sample> m_History = new(128);
        float m_LastGestureTime;
        float m_LastPitch;
        float m_LastYaw;
        float m_SmoothedPitch;
        float m_SmoothedYaw;
        float m_SmoothedPitchVelocity;
        float m_SmoothedYawVelocity;
        float m_LastSampleTime;
        bool m_Initialized;
        float m_LastDebugTime;

        void Update()
        {
            RecordSample();
            CleanHistory();
            DetectGestures();
            
            // Debug: show stats periodically
            if (m_DebugMode && Time.time - m_LastDebugTime > 3f && m_History.Count > 0)
            {
                var pitchAnalysis = AnalyzeAxis(s => s.SmoothedPitch, s => s.PitchVelocity);
                var yawAnalysis = AnalyzeAxis(s => s.SmoothedYaw, s => s.YawVelocity);
                
                if (pitchAnalysis.oscillations > 0 || yawAnalysis.oscillations > 0)
                {
                    Debug.Log($"[HeadGesture] Pitch: osc={pitchAnalysis.oscillations}, conf={pitchAnalysis.confidence:F2} | " +
                              $"Yaw: osc={yawAnalysis.oscillations}, conf={yawAnalysis.confidence:F2}");
                }
                m_LastDebugTime = Time.time;
            }
        }

        void RecordSample()
        {
            var euler = transform.rotation.eulerAngles;
            
            // Normalize angles to -180 to 180 range
            float pitch = euler.x > 180 ? euler.x - 360 : euler.x;
            float yaw = euler.y > 180 ? euler.y - 360 : euler.y;
            
            if (!m_Initialized)
            {
                m_LastPitch = pitch;
                m_LastYaw = yaw;
                m_SmoothedPitch = pitch;
                m_SmoothedYaw = yaw;
                m_SmoothedPitchVelocity = 0;
                m_SmoothedYawVelocity = 0;
                m_LastSampleTime = Time.time;
                m_Initialized = true;
                return;
            }
            
            float deltaTime = Time.time - m_LastSampleTime;
            if (deltaTime < 0.016f) return; // ~60 FPS minimum sample rate
            
            // Apply exponential moving average for noise filtering
            m_SmoothedPitch = Mathf.LerpAngle(m_SmoothedPitch, pitch, m_SmoothingFactor);
            m_SmoothedYaw = Mathf.LerpAngle(m_SmoothedYaw, yaw, m_SmoothingFactor);
            
            // Calculate angular velocity from smoothed values
            float pitchDelta = Mathf.DeltaAngle(m_LastPitch, m_SmoothedPitch);
            float yawDelta = Mathf.DeltaAngle(m_LastYaw, m_SmoothedYaw);
            
            float rawPitchVelocity = Mathf.Abs(pitchDelta / deltaTime);
            float rawYawVelocity = Mathf.Abs(yawDelta / deltaTime);
            
            // Smooth the velocity to reduce spikes
            m_SmoothedPitchVelocity = Mathf.Lerp(m_SmoothedPitchVelocity, rawPitchVelocity, m_VelocitySmoothingFactor);
            m_SmoothedYawVelocity = Mathf.Lerp(m_SmoothedYawVelocity, rawYawVelocity, m_VelocitySmoothingFactor);
            
            m_History.Add(new Sample
            {
                Time = Time.time,
                Pitch = pitch,
                Yaw = yaw,
                SmoothedPitch = m_SmoothedPitch,
                SmoothedYaw = m_SmoothedYaw,
                PitchVelocity = m_SmoothedPitchVelocity,
                YawVelocity = m_SmoothedYawVelocity
            });
            
            m_LastPitch = m_SmoothedPitch;
            m_LastYaw = m_SmoothedYaw;
            m_LastSampleTime = Time.time;
        }

        void CleanHistory()
        {
            float cutoff = Time.time - m_TimeWindow;
            m_History.RemoveAll(s => s.Time < cutoff);
        }

        void DetectGestures()
        {
            if (Time.time - m_LastGestureTime < m_Cooldown || m_History.Count < 10)
                return;

            var pitchAnalysis = AnalyzeAxis(s => s.SmoothedPitch, s => s.PitchVelocity);
            var yawAnalysis = AnalyzeAxis(s => s.SmoothedYaw, s => s.YawVelocity);

            // Check for NOD (pitch oscillation) - must be dominant axis
            bool isValidNod = pitchAnalysis.oscillations >= m_RequiredOscillations 
                           && pitchAnalysis.oscillations > yawAnalysis.oscillations
                           && pitchAnalysis.avgVelocity >= m_MinVelocity
                           && pitchAnalysis.range >= m_MinTotalRange
                           && pitchAnalysis.range <= m_MaxTotalRange
                           && pitchAnalysis.confidence >= m_MinConfidence;

            // Check for SHAKE (yaw oscillation) - must be dominant axis
            bool isValidShake = yawAnalysis.oscillations >= m_RequiredOscillations 
                             && yawAnalysis.oscillations > pitchAnalysis.oscillations
                             && yawAnalysis.avgVelocity >= m_MinVelocity
                             && yawAnalysis.range >= m_MinTotalRange
                             && yawAnalysis.range <= m_MaxTotalRange
                             && yawAnalysis.confidence >= m_MinConfidence;

            if (isValidNod)
            {
                m_LastGestureTime = Time.time;
                m_History.Clear();
                if (m_DebugMode)
                    Debug.Log($"[HeadGesture] ✅ NOD detected! (osc={pitchAnalysis.oscillations}, range={pitchAnalysis.range:F0}°, " +
                              $"vel={pitchAnalysis.avgVelocity:F0}°/s, conf={pitchAnalysis.confidence:F2}, rhythm={pitchAnalysis.rhythmScore:F2})");
                OnNodDetected?.Invoke();
                OnNodDetectedWithConfidence?.Invoke(pitchAnalysis.confidence);
            }
            else if (isValidShake)
            {
                m_LastGestureTime = Time.time;
                m_History.Clear();
                if (m_DebugMode)
                    Debug.Log($"[HeadGesture] ✅ SHAKE detected! (osc={yawAnalysis.oscillations}, range={yawAnalysis.range:F0}°, " +
                              $"vel={yawAnalysis.avgVelocity:F0}°/s, conf={yawAnalysis.confidence:F2}, rhythm={yawAnalysis.rhythmScore:F2})");
                OnShakeDetected?.Invoke();
                OnShakeDetectedWithConfidence?.Invoke(yawAnalysis.confidence);
            }
        }

        /// <summary>
        /// Result of axis analysis with confidence scoring.
        /// </summary>
        struct AxisAnalysis
        {
            public int oscillations;
            public float range;
            public float avgVelocity;
            public float confidence;      // 0-1 overall confidence
            public float rhythmScore;     // 0-1 how rhythmic/consistent the oscillations are
        }

        /// <summary>
        /// Analyzes an axis using peak/valley detection for accurate oscillation counting.
        /// Includes confidence scoring based on rhythm consistency and velocity patterns.
        /// </summary>
        AxisAnalysis AnalyzeAxis(Func<Sample, float> getAngle, Func<Sample, float> getVelocity)
        {
            var result = new AxisAnalysis();
            
            if (m_History.Count < 5)
                return result;

            // Find all peaks and valleys
            List<Peak> peaks = FindPeaksAndValleys(getAngle);
            
            if (peaks.Count < 2)
                return result;
            
            // Count oscillations (alternating peaks and valleys)
            result.oscillations = CountValidOscillations(peaks);
            
            // Calculate range
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (var sample in m_History)
            {
                float angle = getAngle(sample);
                min = Mathf.Min(min, angle);
                max = Mathf.Max(max, angle);
            }
            result.range = max - min;
            
            // Calculate average velocity during fast movements
            float totalVelocity = 0f;
            int velocitySamples = 0;
            float peakVelocity = 0f;
            
            foreach (var sample in m_History)
            {
                float velocity = getVelocity(sample);
                peakVelocity = Mathf.Max(peakVelocity, velocity);
                
                if (velocity > m_MinVelocity * 0.3f)
                {
                    totalVelocity += velocity;
                    velocitySamples++;
                }
            }
            result.avgVelocity = velocitySamples > 0 ? totalVelocity / velocitySamples : 0;
            
            // Calculate rhythm score (consistency of oscillation timing)
            result.rhythmScore = CalculateRhythmScore(peaks);
            
            // Calculate overall confidence
            result.confidence = CalculateConfidence(result, peaks, peakVelocity);
            
            return result;
        }
        
        /// <summary>
        /// Find peaks and valleys in the angle data using local extrema detection.
        /// </summary>
        List<Peak> FindPeaksAndValleys(Func<Sample, float> getAngle)
        {
            List<Peak> peaks = new List<Peak>();
            
            if (m_History.Count < 3)
                return peaks;
            
            // Sliding window to find local extrema
            const int WINDOW = 3;
            
            for (int i = WINDOW; i < m_History.Count - WINDOW; i++)
            {
                float current = getAngle(m_History[i]);
                bool isLocalMax = true;
                bool isLocalMin = true;
                
                // Check if current is local max or min
                for (int j = -WINDOW; j <= WINDOW; j++)
                {
                    if (j == 0) continue;
                    
                    float neighbor = getAngle(m_History[i + j]);
                    if (neighbor >= current) isLocalMax = false;
                    if (neighbor <= current) isLocalMin = false;
                }
                
                if (isLocalMax || isLocalMin)
                {
                    // Check if this peak is significant (far enough from last peak)
                    if (peaks.Count > 0)
                    {
                        float lastPeakValue = peaks[peaks.Count - 1].Value;
                        float diff = Mathf.Abs(current - lastPeakValue);
                        
                        if (diff < m_PeakThreshold)
                            continue; // Too close to last peak, skip
                        
                        // Ensure alternating (peak after valley or vice versa)
                        bool lastWasMax = peaks[peaks.Count - 1].IsMax;
                        if (isLocalMax == lastWasMax)
                        {
                            // Same type - update if this one is more extreme
                            if ((isLocalMax && current > lastPeakValue) ||
                                (!isLocalMax && current < lastPeakValue))
                            {
                                peaks[peaks.Count - 1] = new Peak
                                {
                                    Time = m_History[i].Time,
                                    Value = current,
                                    IsMax = isLocalMax
                                };
                            }
                            continue;
                        }
                    }
                    
                    peaks.Add(new Peak
                    {
                        Time = m_History[i].Time,
                        Value = current,
                        IsMax = isLocalMax
                    });
                }
            }
            
            return peaks;
        }
        
        /// <summary>
        /// Count valid oscillations from peaks list (alternating peaks/valleys).
        /// </summary>
        int CountValidOscillations(List<Peak> peaks)
        {
            if (peaks.Count < 2) return 0;
            
            int oscillations = 0;
            for (int i = 1; i < peaks.Count; i++)
            {
                // Count direction changes
                if (peaks[i].IsMax != peaks[i - 1].IsMax)
                {
                    oscillations++;
                }
            }
            
            return oscillations;
        }
        
        /// <summary>
        /// Calculate rhythm consistency score (0-1).
        /// Measures how evenly spaced the oscillations are.
        /// </summary>
        float CalculateRhythmScore(List<Peak> peaks)
        {
            if (peaks.Count < 3) return 0f;
            
            // Calculate intervals between peaks
            List<float> intervals = new List<float>();
            for (int i = 1; i < peaks.Count; i++)
            {
                intervals.Add(peaks[i].Time - peaks[i - 1].Time);
            }
            
            if (intervals.Count < 2) return 0.5f;
            
            // Calculate mean and standard deviation of intervals
            float sum = 0f;
            foreach (float interval in intervals)
                sum += interval;
            float mean = sum / intervals.Count;
            
            float sumSquaredDiff = 0f;
            foreach (float interval in intervals)
                sumSquaredDiff += (interval - mean) * (interval - mean);
            float stdDev = Mathf.Sqrt(sumSquaredDiff / intervals.Count);
            
            // Coefficient of variation (lower = more consistent)
            float cv = mean > 0 ? stdDev / mean : 1f;
            
            // Convert to 0-1 score (1 = perfectly consistent)
            // CV of 0 = perfect, CV of 1+ = very inconsistent
            return Mathf.Clamp01(1f - cv);
        }
        
        /// <summary>
        /// Calculate overall confidence score (0-1) for the gesture.
        /// </summary>
        float CalculateConfidence(AxisAnalysis analysis, List<Peak> peaks, float peakVelocity)
        {
            if (analysis.oscillations < 2) return 0f;
            
            float confidence = 0f;
            
            // Factor 1: Number of oscillations (more = more confident, up to a point)
            float oscScore = Mathf.Clamp01((float)(analysis.oscillations - 1) / 4f);
            confidence += oscScore * 0.25f;
            
            // Factor 2: Velocity strength
            float velScore = Mathf.Clamp01((analysis.avgVelocity - m_MinVelocity) / (m_MinVelocity * 2f));
            confidence += velScore * 0.25f;
            
            // Factor 3: Range appropriateness (not too small, not too large)
            float idealRange = (m_MinTotalRange + m_MaxTotalRange) / 2f;
            float rangeDiff = Mathf.Abs(analysis.range - idealRange) / idealRange;
            float rangeScore = Mathf.Clamp01(1f - rangeDiff);
            confidence += rangeScore * 0.2f;
            
            // Factor 4: Rhythm consistency
            confidence += analysis.rhythmScore * 0.2f;
            
            // Factor 5: Peak/valley amplitude consistency
            float ampScore = CalculateAmplitudeConsistency(peaks);
            confidence += ampScore * 0.1f;
            
            return Mathf.Clamp01(confidence);
        }
        
        /// <summary>
        /// Calculate how consistent the peak/valley amplitudes are (0-1).
        /// </summary>
        float CalculateAmplitudeConsistency(List<Peak> peaks)
        {
            if (peaks.Count < 3) return 0.5f;
            
            List<float> amplitudes = new List<float>();
            for (int i = 1; i < peaks.Count; i++)
            {
                amplitudes.Add(Mathf.Abs(peaks[i].Value - peaks[i - 1].Value));
            }
            
            if (amplitudes.Count < 2) return 0.5f;
            
            // Calculate coefficient of variation
            float sum = 0f;
            foreach (float amp in amplitudes)
                sum += amp;
            float mean = sum / amplitudes.Count;
            
            if (mean < 0.01f) return 0f;
            
            float sumSquaredDiff = 0f;
            foreach (float amp in amplitudes)
                sumSquaredDiff += (amp - mean) * (amp - mean);
            float stdDev = Mathf.Sqrt(sumSquaredDiff / amplitudes.Count);
            
            float cv = stdDev / mean;
            return Mathf.Clamp01(1f - cv);
        }

        public void ResetHistory()
        {
            m_History.Clear();
            m_Initialized = false;
            m_SmoothedPitchVelocity = 0;
            m_SmoothedYawVelocity = 0;
        }
        
        /// <summary>
        /// Get the current smoothed pitch angle.
        /// </summary>
        public float GetSmoothedPitch() => m_SmoothedPitch;
        
        /// <summary>
        /// Get the current smoothed yaw angle.
        /// </summary>
        public float GetSmoothedYaw() => m_SmoothedYaw;
    }
}

