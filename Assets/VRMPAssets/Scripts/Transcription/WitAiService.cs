using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Wit.ai Speech-to-Text service.
    /// Sends audio to Wit.ai cloud and returns transcribed text.
    /// </summary>
    public class WitAiService : MonoBehaviour
    {
        #region Singleton
        public static WitAiService Instance { get; private set; }
        #endregion

        #region Configuration
        [Header("Wit.ai Configuration")]
        [SerializeField, Tooltip("Your Wit.ai Server Access Token")]
        private string serverAccessToken = "";

        [Header("Audio Settings")]
        [SerializeField] private int sampleRate = 16000;
        [SerializeField] private int recordingSeconds = 5;

        private const string WIT_API_URL = "https://api.wit.ai/speech";
        private const string k_DebugPrepend = "<color=#00BFFF>[Wit.ai]</color> ";
        #endregion

        #region Events
        public event Action<string, float> OnTranscriptionResult;
        public event Action<string> OnTranscriptionError;
        #endregion

        #region Private Fields
        private AudioClip micClip;
        private bool isRecording;
        private bool isProcessing;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                StopRecording();
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Sets the Wit.ai server access token.
        /// </summary>
        public void SetToken(string token)
        {
            serverAccessToken = token;
        }

        /// <summary>
        /// Starts recording from microphone.
        /// </summary>
        public void StartRecording()
        {
            if (isRecording) return;

            if (string.IsNullOrEmpty(serverAccessToken))
            {
                Utils.Log($"{k_DebugPrepend}No API token set!", 2);
                OnTranscriptionError?.Invoke("No Wit.ai API token configured");
                return;
            }

            if (Microphone.devices.Length == 0)
            {
                Utils.Log($"{k_DebugPrepend}No microphone found!", 2);
                OnTranscriptionError?.Invoke("No microphone available");
                return;
            }

            string device = Microphone.devices[0];
            Utils.Log($"{k_DebugPrepend}Starting recording on: {device}");

            micClip = Microphone.Start(device, true, recordingSeconds, sampleRate);
            isRecording = true;
        }

        /// <summary>
        /// Stops recording and sends audio to Wit.ai for transcription.
        /// </summary>
        public void StopRecordingAndTranscribe()
        {
            if (!isRecording) return;

            isRecording = false;
            Microphone.End(null);

            if (micClip == null)
            {
                OnTranscriptionError?.Invoke("No audio recorded");
                return;
            }

            StartCoroutine(TranscribeAudioCoroutine(micClip));
        }

        /// <summary>
        /// Transcribes audio samples directly.
        /// </summary>
        public void TranscribeAudio(float[] samples, int sampleRate)
        {
            if (isProcessing)
            {
                Utils.Log($"{k_DebugPrepend}Already processing...", 1);
                return;
            }

            StartCoroutine(TranscribeSamplesCoroutine(samples, sampleRate));
        }

        /// <summary>
        /// Whether the service is currently processing audio.
        /// </summary>
        public bool IsProcessing => isProcessing;

        /// <summary>
        /// Whether recording is active.
        /// </summary>
        public bool IsRecording => isRecording;
        #endregion

        #region Private Methods
        private IEnumerator TranscribeAudioCoroutine(AudioClip clip)
        {
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            yield return TranscribeSamplesCoroutine(samples, clip.frequency);
        }

        private IEnumerator TranscribeSamplesCoroutine(float[] samples, int rate)
        {
            isProcessing = true;
            Utils.Log($"{k_DebugPrepend}Processing {samples.Length} samples...");

            // Convert to WAV
            byte[] wavData = ConvertToWav(samples, rate);

            // Send to Wit.ai
            using (UnityWebRequest request = new UnityWebRequest(WIT_API_URL, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(wavData);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", $"Bearer {serverAccessToken}");
                request.SetRequestHeader("Content-Type", "audio/wav");

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string response = request.downloadHandler.text;
                    Utils.Log($"{k_DebugPrepend}Response: {response}");

                    // Parse the final text (Wit.ai may return multiple lines)
                    string finalText = ParseWitResponse(response);

                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        Utils.Log($"{k_DebugPrepend}Transcription: {finalText}");
                        OnTranscriptionResult?.Invoke(finalText, 0.9f);
                    }
                    else
                    {
                        Utils.Log($"{k_DebugPrepend}No speech detected.", 1);
                    }
                }
                else
                {
                    Utils.Log($"{k_DebugPrepend}Error: {request.error}", 2);
                    OnTranscriptionError?.Invoke(request.error);
                }
            }

            isProcessing = false;
        }

        private string ParseWitResponse(string response)
        {
            // Wit.ai returns JSON lines, we want the final transcription
            // The response may contain multiple JSON objects
            string[] lines = response.Split('\n');
            string lastText = "";

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    // Find "text" field in JSON
                    int textIndex = line.IndexOf("\"text\"");
                    if (textIndex >= 0)
                    {
                        int colonIndex = line.IndexOf(':', textIndex);
                        int startQuote = line.IndexOf('"', colonIndex + 1);
                        int endQuote = line.IndexOf('"', startQuote + 1);

                        if (startQuote >= 0 && endQuote > startQuote)
                        {
                            lastText = line.Substring(startQuote + 1, endQuote - startQuote - 1);
                        }
                    }
                }
                catch (Exception e)
                {
                    Utils.Log($"{k_DebugPrepend}Parse error: {e.Message}", 1);
                }
            }

            return lastText;
        }

        private byte[] ConvertToWav(float[] samples, int sampleRate)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int channels = 1;
                int bitsPerSample = 16;

                // Convert float samples to 16-bit PCM
                short[] pcmSamples = new short[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = Mathf.Clamp(samples[i], -1f, 1f);
                    pcmSamples[i] = (short)(sample * 32767);
                }

                // WAV header
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcmSamples.Length * 2);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });

                // fmt chunk
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16); // chunk size
                writer.Write((short)1); // audio format (PCM)
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
                writer.Write((short)(channels * bitsPerSample / 8)); // block align
                writer.Write((short)bitsPerSample);

                // data chunk
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(pcmSamples.Length * 2);

                foreach (short sample in pcmSamples)
                {
                    writer.Write(sample);
                }

                return stream.ToArray();
            }
        }

        private void StopRecording()
        {
            if (isRecording)
            {
                Microphone.End(null);
                isRecording = false;
            }
        }
        #endregion
    }
}
