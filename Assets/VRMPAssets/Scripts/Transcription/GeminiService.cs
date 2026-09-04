using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Google Gemini 1.5 Flash Speech-to-Text & Utility Service.
    /// Uses the Multimodal capabilities of Gemini for Transcription and Intelligence.
    /// Optimized for Free Tier (15 RPM).
    /// </summary>
    public class GeminiService : MonoBehaviour
    {
        #region Singleton
        public static GeminiService Instance { get; private set; }
        #endregion

        #region Configuration
        [Header("Gemini Configuration")]
        [SerializeField, Tooltip("Google AI Studio API Key")]
        private string apiKey = "";

        [SerializeField, Tooltip("Model Name")]
        private string modelVersion = "gemini-flash-latest"; // Stable alias for current Flash model

        [SerializeField, Tooltip("Fallback model used automatically when the primary model returns 429 (quota) or 503 (overloaded). Flash-Lite has higher free-tier limits.")]
        private string fallbackModel = "gemini-flash-lite-latest";

        [Header("Optimization")]
        [SerializeField, Tooltip("Minimum seconds of audio to batch before sending (to save requests)")]
        private float minBatchSeconds = 15.0f; // Increased to avoid 429 Too Many Requests

        private const string k_ApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent?key={1}";
        private const string k_DebugPrepend = "<color=#4285F4>[Gemini]</color> ";
        #endregion

        #region Events
        public event Action<string, float> OnTranscriptionResult;
        public event Action<string> OnSummaryResult;
        /// <summary>Fired with the assistant's answer to an <see cref="AskQuestion"/> request.</summary>
        public event Action<string> OnAnswerResult;
        public event Action<string> OnError;
        /// <summary>Transient, non-fatal status updates (e.g. "Server busy (retrying)").</summary>
        public event Action<string> OnStatusUpdate;
        /// <summary>Fired when a transcription request fully finishes. Argument = success.</summary>
        public event Action<bool> OnRequestFinished;
        #endregion

        #region Retry Settings
        [Header("Retry")]
        [SerializeField, Tooltip("How many times to retry transient failures (503/429/network) before giving up")]
        private int maxRetries = 3;
        [SerializeField, Tooltip("Base seconds for exponential backoff between retries")]
        private float retryBaseDelay = 2f;
        [SerializeField, Tooltip("Per-attempt HTTP timeout in seconds. Prevents a hung request from blocking the service forever.")]
        private int requestTimeoutSeconds = 20;
        #endregion

        #region Private Fields
        private bool isProcessing;
        // Currently selected transcription language. Defaults to English.
        private TranscriptionLanguage currentLanguage = TranscriptionLanguage.English;
        // Model currently in use; switches to fallbackModel when the primary hits quota/overload.
        private string activeModel;
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
            
            activeModel = modelVersion;

            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = SecureConfig.GeminiApiKey;
            }

            // Log config (no forced override)
            Utils.Log($"{k_DebugPrepend}Config: Model={modelVersion}, Fallback={fallbackModel}, Endpoint=v1beta, Timeout={requestTimeoutSeconds}s");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
        #endregion

        #region Public Methods
        public void SetApiKey(string key)
        {
            apiKey = key;
        }

        /// <summary>
        /// Sets the language Gemini should transcribe in. This makes recognition far more
        /// accurate than relying on auto-detection (especially for Hebrew/Arabic).
        /// </summary>
        public void SetLanguage(TranscriptionLanguage language)
        {
            currentLanguage = language;
            Utils.Log($"{k_DebugPrepend}Transcription language set to {language.DisplayName()} ({language.Code()})");
        }

        public TranscriptionLanguage CurrentLanguage => currentLanguage;

        /// <summary>
        /// Sends audio to Gemini for transcription.
        /// </summary>
        public void TranscribeAudio(float[] samples, int sampleRate)
        {
            if (isProcessing)
            {
                // The manager keeps unsent audio in its own buffer, so skipping here loses nothing.
                Utils.Log($"{k_DebugPrepend}Skipping batch - a request is already in flight (audio stays buffered)", 1);
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                Utils.Log($"{k_DebugPrepend}Missing API Key!", 2);
                OnError?.Invoke("Missing Gemini API Key");
                return;
            }

            // We let the manager handle the buffer accumulation, 
            // but we double check duration here just in case.
            float duration = (float)samples.Length / sampleRate;
            if (duration < 0.5f) return; // Too short

            StartCoroutine(SendToGemini(samples, sampleRate));
        }

        /// <summary>
        /// Generates a summary and action items from the provided text.
        /// </summary>
        public void GenerateSummary(string transcriptText)
        {
            // Single-flight: never start a second request while one is in flight. We do NOT fire
            // OnError here — OnError is a shared event and would be mis-consumed by whichever
            // consumer is currently awaiting the in-flight request. Callers should check IsProcessing.
            if (isProcessing)
            {
                Utils.Log($"{k_DebugPrepend}GenerateSummary skipped - a request is already in flight.", 1);
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                OnError?.Invoke("Missing Gemini API Key");
                return;
            }

            if (string.IsNullOrEmpty(transcriptText) || transcriptText.Length < 10)
            {
                OnError?.Invoke("Transcript too short to summarize.");
                return;
            }

            StartCoroutine(SendSummaryRequest(transcriptText));
        }

        /// <summary>
        /// Asks the AI assistant a free-form question, grounded in the supplied meeting transcript.
        /// The answer is delivered via <see cref="OnAnswerResult"/> (or <see cref="OnError"/> on failure).
        /// </summary>
        /// <param name="transcriptContext">The meeting transcript to ground the answer in (may be empty).</param>
        /// <param name="question">The user's question.</param>
        public void AskQuestion(string transcriptContext, string question)
        {
            // Single-flight: see note in GenerateSummary. Prevents overlapping requests from
            // clobbering isProcessing and mis-routing the shared OnAnswerResult/OnError events.
            if (isProcessing)
            {
                Utils.Log($"{k_DebugPrepend}AskQuestion skipped - a request is already in flight.", 1);
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                OnError?.Invoke("Missing Gemini API Key");
                return;
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                OnError?.Invoke("Question is empty.");
                return;
            }

            StartCoroutine(SendQuestionRequest(transcriptContext ?? string.Empty, question));
        }

        public bool IsProcessing => isProcessing;
        public float MinBatchSeconds => minBatchSeconds;
        #endregion

        #region Private Methods
        private IEnumerator SendQuestionRequest(string transcript, string question)
        {
            isProcessing = true;

            try
            {
                if (string.IsNullOrEmpty(activeModel)) activeModel = modelVersion;

                string languageName = currentLanguage.DisplayName();
                string transcriptBlock = string.IsNullOrWhiteSpace(transcript)
                    ? "(no transcript has been captured yet)"
                    : transcript;

                string prompt =
                    "You are a helpful meeting assistant inside a live VR meeting. " +
                    "Answer the user's question using ONLY the meeting transcript below. " +
                    "If the answer is not in the transcript, say so briefly instead of guessing.\n" +
                    $"Write the ENTIRE response in {languageName} - do not use any other language for the body or labels.\n" +
                    "Use PLAIN TEXT ONLY - no markdown, no ** or # symbols. Be concise: at most 100 words.\n\n" +
                    "TRANSCRIPT:\n" + transcriptBlock + "\n\n" +
                    "QUESTION: " + question;

                string jsonBody = BuildJsonRequest(null, prompt, true);
                string url = string.Format(k_ApiUrl, activeModel, apiKey);

                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonBody);
                    request.uploadHandler = new UploadHandlerRaw(jsonToSend);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = Mathf.Max(5, requestTimeoutSeconds);

                    Utils.Log($"{k_DebugPrepend}Asking assistant (model={activeModel})...");
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        string answer = ParseGeminiResponse(request.downloadHandler.text);
                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            Utils.Log($"{k_DebugPrepend}Assistant answered.");
                            OnAnswerResult?.Invoke(answer);
                        }
                        else
                        {
                            OnError?.Invoke("Assistant returned an empty answer.");
                        }
                    }
                    else
                    {
                        bool retryable;
                        string userMessage = ClassifyError(request, request.downloadHandler?.text, out retryable);
                        Utils.Log($"{k_DebugPrepend}Assistant request failed: {request.error}", 2);
                        OnError?.Invoke($"Assistant failed: {userMessage}");
                    }
                }
            }
            finally
            {
                isProcessing = false;
                Utils.Log($"{k_DebugPrepend}Busy reset");
            }
        }

        private IEnumerator SendSummaryRequest(string text)
        {
            isProcessing = true;

            try
            {
                if (string.IsNullOrEmpty(activeModel)) activeModel = modelVersion;

                // Build prompt. Write the summary - including section titles - entirely in
                // the selected language, so RTL summaries don't mix English labels into
                // right-to-left lines.
                string languageName = currentLanguage.DisplayName();
                string sectionTitles;
                switch (currentLanguage)
                {
                    case TranscriptionLanguage.Hebrew:
                        sectionTitles = "Use EXACTLY these section titles:\n" +
                                        "\u05E1\u05D9\u05DB\u05D5\u05DD:\n" +                                  // סיכום:
                                        "\u05D4\u05D7\u05DC\u05D8\u05D5\u05EA \u05E2\u05D9\u05E7\u05E8\u05D9\u05D5\u05EA:\n" + // החלטות עיקריות:
                                        "\u05E4\u05E2\u05D5\u05DC\u05D5\u05EA \u05DC\u05D1\u05D9\u05E6\u05D5\u05E2:\n";        // פעולות לביצוע:
                        break;
                    case TranscriptionLanguage.Arabic:
                        sectionTitles = "Use EXACTLY these section titles:\n" +
                                        "\u0627\u0644\u0645\u0644\u062E\u0635:\n" +                            // الملخص:
                                        "\u0627\u0644\u0642\u0631\u0627\u0631\u0627\u062A \u0627\u0644\u0631\u0626\u064A\u0633\u064A\u0629:\n" + // القرارات الرئيسية:
                                        "\u0628\u0646\u0648\u062F \u0627\u0644\u0639\u0645\u0644:\n";          // بنود العمل:
                        break;
                    default:
                        sectionTitles = "Use these section titles: Summary: / Key Decisions: / Action Items:\n";
                        break;
                }

                string prompt = $"Summarize the following meeting transcript. Write the ENTIRE response in {languageName}, " +
                               $"including all section titles - do not use any English words if the language is not English.\n" +
                               "Be concise: at most 120 words total.\n" +
                               "Use PLAIN TEXT ONLY - no markdown, no ** or # symbols.\n" +
                               sectionTitles +
                               "Each section: one short paragraph or short bullet lines.\n\n" +
                               "TRANSCRIPT:\n" + text;

                string jsonBody = BuildJsonRequest(null, prompt, true); // Modified helper to handle text-only requests
                string url = string.Format(k_ApiUrl, activeModel, apiKey);

                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonBody);
                    request.uploadHandler = new UploadHandlerRaw(jsonToSend);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = Mathf.Max(5, requestTimeoutSeconds);

                    Utils.Log($"{k_DebugPrepend}Generating summary (model={activeModel})...");
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        string response = request.downloadHandler.text;
                        string summary = ParseGeminiResponse(response);

                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            Utils.Log($"{k_DebugPrepend}Summary Generated!");
                            OnSummaryResult?.Invoke(summary);
                        }
                    }
                    else
                    {
                        bool retryable;
                        string userMessage = ClassifyError(request, request.downloadHandler?.text, out retryable);
                        Utils.Log($"{k_DebugPrepend}Summary request failed: {request.error}", 2);
                        OnError?.Invoke($"Summary failed: {userMessage}");
                    }
                }
            }
            finally
            {
                isProcessing = false;
                Utils.Log($"{k_DebugPrepend}Busy reset");
            }
        }
        private IEnumerator SendToGemini(float[] samples, int sampleRate)
        {
            isProcessing = true;

            // try/finally guarantees the busy flag is ALWAYS cleared - on success, failure,
            // timeout, exception, early exit or object destruction (Unity disposes the
            // iterator, which runs the finally block).
            try
            {
                // --- Validate API key ---
                if (string.IsNullOrEmpty(apiKey))
                {
                    Utils.Log($"{k_DebugPrepend}API key missing!", 2);
                    OnError?.Invoke("API key missing");
                    OnRequestFinished?.Invoke(false);
                    yield break;
                }

                if (string.IsNullOrEmpty(activeModel)) activeModel = modelVersion;

                // --- 1. Convert audio to WAV (16-bit PCM, mono) and base64 ---
                byte[] wavData = ConvertToWav(samples, sampleRate);
                string base64Audio = Convert.ToBase64String(wavData);
                float durationSec = (float)samples.Length / sampleRate;
                Utils.Log($"{k_DebugPrepend}Audio format: audio/wav, {sampleRate}Hz mono 16-bit, {wavData.Length} bytes (~{durationSec:F1}s)");

                // --- 2. Build request payload (language-aware) ---
                string languageName = currentLanguage.DisplayName();
                string prompt =
                    $"Transcribe the audio. The spoken language is {languageName}. " +
                    $"Output ONLY the transcribed text written in {languageName} using its native script. " +
                    "Do not translate, do not add timestamps, speaker labels, quotes or any extra words. " +
                    "If there is no clear speech, output an empty string.";
                string jsonBody = BuildJsonRequest(base64Audio, prompt);

                // Mask the key so it never appears in logs.
                string maskedKey = apiKey.Length > 8 ? apiKey.Substring(0, 4) + "..." + apiKey.Substring(apiKey.Length - 4) : "(set)";
                Utils.Log($"{k_DebugPrepend}Request URL/model: model={activeModel}, endpoint=v1beta, key={maskedKey}, lang={languageName} ({currentLanguage.Code()})");

                // --- 3. Send with retry/backoff and automatic model fallback ---
                int attempts = Mathf.Max(1, maxRetries + 1);
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    string url = string.Format(k_ApiUrl, activeModel, apiKey);
                    using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                    {
                        byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonBody);
                        request.uploadHandler = new UploadHandlerRaw(jsonToSend);
                        request.downloadHandler = new DownloadHandlerBuffer();
                        request.SetRequestHeader("Content-Type", "application/json");
                        request.timeout = Mathf.Max(5, requestTimeoutSeconds);

                        Utils.Log($"{k_DebugPrepend}Request started (attempt {attempt}/{attempts}, model={activeModel}, timeout={request.timeout}s)");
                        yield return request.SendWebRequest();

                        long httpCode = request.responseCode;
                        string response = request.downloadHandler != null ? request.downloadHandler.text : "";
                        string preview = string.IsNullOrEmpty(response) ? "(empty)" : response.Substring(0, Mathf.Min(300, response.Length)).Replace("\n", " ");
                        Utils.Log($"{k_DebugPrepend}Response received - HTTP status: {httpCode} ({request.result})");

                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            Utils.Log($"{k_DebugPrepend}Raw response preview: {preview}");
                            string text = ParseGeminiResponse(response);
                            Utils.Log($"{k_DebugPrepend}Parsed text: {(string.IsNullOrWhiteSpace(text) ? "(empty / no speech)" : text)}");

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                OnTranscriptionResult?.Invoke(text, 1.0f);
                            }
                            OnRequestFinished?.Invoke(true);
                            yield break;
                        }

                        // Failure: log details and classify.
                        Utils.Log($"{k_DebugPrepend}Request FAILED: {request.error}\nRaw response preview: {preview}", 2);
                        bool retryable;
                        string userMessage = ClassifyError(request, response, out retryable);

                        // Quota/overload on the primary model -> switch to the fallback model
                        // and retry immediately (no backoff; it is a different quota bucket).
                        bool quotaOrOverload = httpCode == 429 || httpCode == 503;
                        if (quotaOrOverload && !string.IsNullOrEmpty(fallbackModel) && activeModel != fallbackModel)
                        {
                            Utils.Log($"{k_DebugPrepend}'{activeModel}' unavailable ({httpCode}) - switching to fallback model '{fallbackModel}'", 1);
                            OnStatusUpdate?.Invoke("Switching AI model...");
                            activeModel = fallbackModel;
                            if (attempt < attempts) continue;
                        }

                        if (retryable && attempt < attempts)
                        {
                            float delay = retryBaseDelay * Mathf.Pow(2, attempt - 1); // 2s, 4s, 8s...
                            Utils.Log($"{k_DebugPrepend}{userMessage} - retrying in {delay:F0}s (attempt {attempt}/{attempts})", 1);
                            OnStatusUpdate?.Invoke($"{userMessage} (retrying)");
                            yield return new WaitForSeconds(delay);
                            continue; // try again
                        }

                        // Out of retries (or non-retryable): fail gracefully and let the next batch through.
                        Utils.Log($"{k_DebugPrepend}Request failed after retries: {userMessage}", 2);
                        OnError?.Invoke(userMessage);
                        OnRequestFinished?.Invoke(false);
                        yield break;
                    }
                }
            }
            finally
            {
                isProcessing = false;
                Utils.Log($"{k_DebugPrepend}Busy reset");
            }
        }

        /// <summary>
        /// Maps an HTTP failure to a short, user-facing message and whether it is worth retrying.
        /// </summary>
        private string ClassifyError(UnityWebRequest request, string body, out bool retryable)
        {
            retryable = false;
            body = body ?? "";

            if (!string.IsNullOrEmpty(request.error) &&
                request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                retryable = true;
                return "Transcription timeout";
            }

            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.DataProcessingError)
            {
                retryable = true;
                return "Network error";
            }

            long code = request.responseCode;
            switch (code)
            {
                case 400:
                    // 400 with an audio/inline_data complaint usually means a bad audio payload.
                    if (body.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        body.IndexOf("inline_data", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        body.IndexOf("mime", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Invalid audio format";
                    return "API request failed (400)";
                case 401:
                case 403:
                    return "API key invalid";
                case 429:
                    retryable = true;
                    return "API quota/rate limit";
                case 500:
                case 502:
                case 503:
                case 504:
                    retryable = true;
                    return "Server busy";
                default:
                    return $"API request failed ({code})";
            }
        }

        private string BuildJsonRequest(string base64Audio, string promptText, bool isTextOnly = false)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"contents\": [{");
            sb.Append("\"parts\": [");
            sb.Append($"{{\"text\": \"{EscapeJson(promptText)}\"}}");
            
            if (!isTextOnly && !string.IsNullOrEmpty(base64Audio))
            {
                sb.Append(",");
                sb.Append("{\"inline_data\": { \"mime_type\": \"audio/wav\", \"data\": \"");
                sb.Append(base64Audio);
                sb.Append("\" }}");
            }
            
            sb.Append("]");
            sb.Append("}]");
            sb.Append("}");

            return sb.ToString();
        }

        private string EscapeJson(string s)
        {
            if (s == null) return "";

            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;          // drop carriage returns
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Escape any remaining control characters (U+0000–U+001F) which are illegal
                        // raw inside a JSON string and would otherwise produce a 400 from the API.
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ── Response DTOs (JsonUtility ignores unknown fields and decodes \uXXXX natively) ──
        [Serializable] private class GeminiResponse { public GeminiCandidate[] candidates; }
        [Serializable] private class GeminiCandidate { public GeminiContent content; public string finishReason; }
        [Serializable] private class GeminiContent { public GeminiPart[] parts; }
        [Serializable] private class GeminiPart { public string text; }

        private string ParseGeminiResponse(string json)
        {
            // Primary path: strict JSON via JsonUtility. This correctly decodes all escape
            // sequences - crucially \uXXXX, which the old manual parser passed through literally
            // and corrupted Hebrew/Arabic answers. Concatenates all parts (Gemini may split text).
            try
            {
                var parsed = JsonUtility.FromJson<GeminiResponse>(json);
                if (parsed?.candidates != null && parsed.candidates.Length > 0)
                {
                    var content = parsed.candidates[0].content;
                    if (content?.parts != null && content.parts.Length > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in content.parts)
                            if (!string.IsNullOrEmpty(part?.text)) sb.Append(part.text);

                        string text = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }

                    // No text but a finishReason explains why (MAX_TOKENS, SAFETY, etc.).
                    string reason = parsed.candidates[0].finishReason;
                    if (!string.IsNullOrEmpty(reason) && reason != "STOP")
                        Utils.Log($"{k_DebugPrepend}No text in response (finishReason={reason}).", 1);
                }
            }
            catch (Exception e)
            {
                Utils.Log($"{k_DebugPrepend}JSON parse failed, falling back to manual scan: {e.Message}", 1);
            }

            // Fallback: tolerant manual scan (handles "text":" with or without a space) that also
            // decodes \uXXXX, in case the response shape ever differs from the DTOs above.
            return ManualExtractText(json);
        }

        private string ManualExtractText(string json)
        {
            try
            {
                int keyIdx = json.IndexOf("\"text\"", StringComparison.Ordinal);
                if (keyIdx == -1) return "";

                // Skip past the key, optional whitespace, the colon, optional whitespace, opening quote.
                int i = keyIdx + 6;
                while (i < json.Length && json[i] != '"') i++; // find opening quote of the value
                if (i >= json.Length) return "";
                i++; // move past opening quote

                StringBuilder result = new StringBuilder();
                bool escaped = false;
                for (; i < json.Length; i++)
                {
                    char c = json[i];
                    if (escaped)
                    {
                        switch (c)
                        {
                            case 'n': result.Append('\n'); break;
                            case 'r': break;
                            case 't': result.Append('\t'); break;
                            case 'u':
                                if (i + 4 < json.Length &&
                                    int.TryParse(json.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out int code))
                                {
                                    result.Append((char)code);
                                    i += 4;
                                }
                                break;
                            default: result.Append(c); break;
                        }
                        escaped = false;
                    }
                    else if (c == '\\') escaped = true;
                    else if (c == '"') break;
                    else result.Append(c);
                }
                return result.ToString().Trim();
            }
            catch (Exception e)
            {
                Utils.Log($"{k_DebugPrepend}Parse Error: {e.Message}");
                return "";
            }
        }

        private byte[] ConvertToWav(float[] samples, int sampleRate)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int channels = 1;
                int bitsPerSample = 16;
                short[] pcmSamples = new short[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = Mathf.Clamp(samples[i], -1f, 1f);
                    pcmSamples[i] = (short)(sample * 32767);
                }

                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcmSamples.Length * 2);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1); 
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8);
                writer.Write((short)(channels * bitsPerSample / 8));
                writer.Write((short)bitsPerSample);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(pcmSamples.Length * 2);

                foreach (short sample in pcmSamples) writer.Write(sample);
                return stream.ToArray();
            }
        }
        #endregion
    }
}
