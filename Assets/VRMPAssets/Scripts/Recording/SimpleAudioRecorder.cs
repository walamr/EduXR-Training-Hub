using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace XRMultiplayer.Recording
{
    /// <summary>
    /// Records audio from the AudioListener (or any AudioSource) to a WAV file.
    /// Also captures microphone input directly (without live monitoring).
    /// usage: AddComponent to the GameObject with AudioListener.
    /// </summary>
    public class SimpleAudioRecorder : MonoBehaviour
    {
        private int m_SampleRate = 48000;
        private int m_HeaderSize = 44;
        private bool m_IsRecording = false;
        private int m_ChannelCount = 2;
        private readonly object m_StreamLock = new object();
        private FileStream m_FileStream;
        private string m_FilePath;
        private float m_MaxSampleMagnitude = 0f;

        // Microphone direct capture (no live monitoring)
        // Mic data is read on main thread and stored in a queue for the audio thread
        private AudioClip m_MicrophoneClip;
        private string m_MicrophoneDevice;
        private int m_LastMicPosition = 0;
        private float m_MicrophoneVolume = 1f;
        private bool m_MicActive = false;
        private float m_MicPeakLevel = 0f;
        private int m_MicSamplesRecorded = 0;
        
        // Thread-safe queue for mic samples (main thread writes, audio thread reads)
        private readonly object m_MicQueueLock = new object();
        private Queue<float> m_MicSampleQueue = new Queue<float>();
        private const int MAX_QUEUE_SIZE = 48000 * 2; // ~2 seconds of audio buffer

        public void StartRecording(string filePath)
        {
            if (m_IsRecording) return;

            m_FilePath = filePath;
            m_SampleRate = AudioSettings.outputSampleRate;
            m_ChannelCount = 0;
            m_MaxSampleMagnitude = 0f;
            
            // Reset mic diagnostics
            m_MicPeakLevel = 0f;
            m_MicSamplesRecorded = 0;
            
            // Clear mic queue
            lock (m_MicQueueLock)
            {
                m_MicSampleQueue.Clear();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            m_FileStream = new FileStream(filePath, FileMode.Create);

            // Write empty header
            byte[] emptyByte = new byte[m_HeaderSize];
            m_FileStream.Write(emptyByte, 0, m_HeaderSize);

            m_IsRecording = true;
            Debug.Log($"[AudioRecorder] Started recording to {filePath}");
        }

        /// <summary>
        /// Sets up microphone capture without live monitoring.
        /// The mic audio will be mixed into the recording silently.
        /// </summary>
        public void SetMicrophoneSource(string deviceName, float volume = 1f)
        {
            m_MicrophoneDevice = deviceName;
            m_MicrophoneVolume = volume;
            m_LastMicPosition = 0;

            if (string.IsNullOrEmpty(deviceName))
            {
                m_MicrophoneClip = null;
                m_MicActive = false;
                return;
            }

            // Start microphone recording - audio captured directly, NOT played through speakers
            m_MicrophoneClip = Microphone.Start(deviceName, true, 30, AudioSettings.outputSampleRate);
            m_MicActive = true;
            
            // Clear the queue
            lock (m_MicQueueLock)
            {
                m_MicSampleQueue.Clear();
            }
            
            Debug.Log($"[AudioRecorder] Microphone '{deviceName}' capture started (silent monitoring), clip channels: {m_MicrophoneClip.channels}, freq: {m_MicrophoneClip.frequency}");
        }

        /// <summary>
        /// Stops microphone capture.
        /// </summary>
        public void StopMicrophoneCapture()
        {
            m_MicActive = false;
            
            if (!string.IsNullOrEmpty(m_MicrophoneDevice))
            {
                Microphone.End(m_MicrophoneDevice);
                m_MicrophoneDevice = null;
                m_MicrophoneClip = null;
                Debug.Log("[AudioRecorder] Microphone capture stopped");
            }
        }
        
        /// <summary>
        /// Called every frame on the main thread to read microphone data.
        /// </summary>
        private void Update()
        {
            if (!m_IsRecording || !m_MicActive || m_MicrophoneClip == null) return;
            if (string.IsNullOrEmpty(m_MicrophoneDevice)) return;
            if (!Microphone.IsRecording(m_MicrophoneDevice)) return;

            int micPosition = Microphone.GetPosition(m_MicrophoneDevice);
            if (micPosition < 0) return;

            int micChannels = m_MicrophoneClip.channels;
            int clipSamples = m_MicrophoneClip.samples;

            // Calculate new samples available
            int newSamples;
            if (micPosition >= m_LastMicPosition)
            {
                newSamples = micPosition - m_LastMicPosition;
            }
            else
            {
                // Wrapped around
                newSamples = (clipSamples - m_LastMicPosition) + micPosition;
            }

            if (newSamples <= 0) return;

            // Read the new samples
            float[] tempBuffer = new float[newSamples * micChannels];
            
            // Handle wrap-around
            int readStart = m_LastMicPosition;
            int samplesBeforeEnd = clipSamples - readStart;

            if (newSamples <= samplesBeforeEnd)
            {
                m_MicrophoneClip.GetData(tempBuffer, readStart);
            }
            else
            {
                // Read in two parts
                float[] part1 = new float[samplesBeforeEnd * micChannels];
                m_MicrophoneClip.GetData(part1, readStart);

                int part2Samples = newSamples - samplesBeforeEnd;
                float[] part2 = new float[part2Samples * micChannels];
                m_MicrophoneClip.GetData(part2, 0);

                Array.Copy(part1, 0, tempBuffer, 0, part1.Length);
                Array.Copy(part2, 0, tempBuffer, part1.Length, part2.Length);
            }

            // Add samples to queue (take first channel only if stereo, apply volume)
            lock (m_MicQueueLock)
            {
                for (int i = 0; i < newSamples; i++)
                {
                    float sample = tempBuffer[i * micChannels] * m_MicrophoneVolume;
                    
                    // Track peak
                    float abs = Mathf.Abs(sample);
                    if (abs > m_MicPeakLevel) m_MicPeakLevel = abs;
                    
                    // Prevent queue from growing too large
                    if (m_MicSampleQueue.Count < MAX_QUEUE_SIZE)
                    {
                        m_MicSampleQueue.Enqueue(sample);
                    }
                }
                m_MicSamplesRecorded += newSamples;
            }

            m_LastMicPosition = micPosition;
        }

        public void StopRecording()
        {
            if (!m_IsRecording) return;

            m_IsRecording = false;
            
            // Log microphone diagnostics before stopping
            Debug.Log($"[AudioRecorder] Mic diagnostics - Samples recorded: {m_MicSamplesRecorded}, Peak level: {m_MicPeakLevel:F4}");
            
            // Stop microphone capture
            StopMicrophoneCapture();
            
            lock (m_StreamLock)
            {
                WriteHeader();
                m_FileStream?.Close();
            }
            Debug.Log($"[AudioRecorder] Stopped recording. Overall peak level (linear): {m_MaxSampleMagnitude:F3}");
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!m_IsRecording || m_FileStream == null) return;

            if (m_ChannelCount == 0)
            {
                m_ChannelCount = channels;
            }

            // Create a COPY of the audio data for recording (so we don't modify playback)
            float[] recordingData = new float[data.Length];
            Array.Copy(data, recordingData, data.Length);

            // Mix microphone audio into the COPY only (not played back to user)
            MixMicrophoneAudio(recordingData, channels);

            // Convert the recording copy to Int16[] and write to stream
            // Original 'data' is unchanged - user won't hear their own mic
            byte[] bytes = new byte[recordingData.Length * 2];
            int rescaleFactor = 32767;

            for (int i = 0; i < recordingData.Length; i++)
            {
                float clamped = Mathf.Clamp(recordingData[i], -1f, 1f);
                short value = (short)(clamped * rescaleFactor);
                BitConverter.GetBytes(value).CopyTo(bytes, i * 2);

                float abs = Mathf.Abs(clamped);
                if (abs > m_MaxSampleMagnitude) m_MaxSampleMagnitude = abs;
            }

            lock (m_StreamLock)
            {
                m_FileStream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Mixes microphone audio from the queue into the data buffer.
        /// The queue is filled by Update() on the main thread.
        /// </summary>
        private void MixMicrophoneAudio(float[] data, int channels)
        {
            if (!m_MicActive) return;

            int samplesNeeded = data.Length / channels;

            lock (m_MicQueueLock)
            {
                // Mix available samples from queue
                for (int i = 0; i < samplesNeeded && m_MicSampleQueue.Count > 0; i++)
                {
                    float micSample = m_MicSampleQueue.Dequeue();

                    // Mix into all output channels
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int dataIndex = i * channels + ch;
                        if (dataIndex < data.Length)
                        {
                            data[dataIndex] += micSample;
                        }
                    }
                }
            }
        }

        private void WriteHeader()
        {
            if (m_FileStream == null) return;
            m_FileStream.Seek(0, SeekOrigin.Begin);

            int channels = m_ChannelCount > 0 ? m_ChannelCount : 2;

            int freq = m_SampleRate;
            long samples = (m_FileStream.Length - m_HeaderSize) / 2;
            long fileSize = m_FileStream.Length - 8;
            long audioFormatSize = m_FileStream.Length - m_HeaderSize;

            using (BinaryWriter writer = new BinaryWriter(m_FileStream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));
                writer.Write((int)fileSize);
                writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.UTF8.GetBytes("fmt "));
                writer.Write((int)16); // Subchunk1Size
                writer.Write((short)1); // AudioFormat
                writer.Write((short)channels);
                writer.Write((int)freq);
                writer.Write((int)(freq * channels * 2)); // ByteRate
                writer.Write((short)(channels * 2)); // BlockAlign
                writer.Write((short)16); // BitsPerSample
                writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));
                writer.Write((int)audioFormatSize);
            }
        }
    }
}
