using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// "Catch Me Up": lets a player who joined a meeting late ask the host's AI for a short
    /// recap of what they missed.
    ///
    /// Only the host (session owner) holds a transcript and runs Gemini, so recaps are generated
    /// on the host and delivered back to the specific requester:
    ///   requester --RequestCatchMeUpOwnerRpc--> host (Gemini) --DeliverCatchMeUpClientRpc--> requester
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class CatchMeUpService : NetworkBehaviour
    {
        public static CatchMeUpService Instance { get; private set; }

        [Header("Recap")]
        [Tooltip("Reuse the last whole-meeting recap if it is newer than this (seconds) to save AI quota.")]
        [SerializeField] private float m_RecapCacheSeconds = 30f;

        [Tooltip("Minimum transcript length (characters) before a recap is attempted.")]
        [SerializeField] private int m_MinTranscriptChars = 40;

        [Header("Debug")]
        [SerializeField] private bool m_DebugLogs = true;

        /// <summary>Raised on the requesting client when its recap (or an error message) arrives.</summary>
        public event Action<string> OnRecapReceived;

        // ── Host-side request processing ──
        private readonly Queue<RecapRequest> m_PendingRequests = new();
        private readonly HashSet<ulong> m_QueuedRequesters = new();
        private bool m_Processing;
        private bool m_AwaitingGemini;
        private RecapRequest m_Current;
        private string m_CachedRecap;
        private DateTime m_CachedRecapUtc = DateTime.MinValue;

        private struct RecapRequest
        {
            public ulong requester;
            public long sinceUtcTicks;
        }

        #region Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (GeminiService.Instance != null)
            {
                GeminiService.Instance.OnAnswerResult += OnGeminiAnswer;
                GeminiService.Instance.OnError += OnGeminiError;
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (GeminiService.Instance != null)
            {
                GeminiService.Instance.OnAnswerResult -= OnGeminiAnswer;
                GeminiService.Instance.OnError -= OnGeminiError;
            }
            if (Instance == this) Instance = null;
        }

        #endregion

        #region Client API

        /// <summary>
        /// Local (late-joining) player requests a recap. Pass 0 for a whole-meeting recap, or the
        /// UTC tick count of when they left/joined to recap only what happened since.
        /// </summary>
        public void RequestCatchMeUp(long sinceUtcTicks = 0)
        {
            if (!IsSpawned)
            {
                Log("RequestCatchMeUp: not spawned.");
                return;
            }
            Log($"RequestCatchMeUp: since={sinceUtcTicks}");
            RequestCatchMeUpOwnerRpc(sinceUtcTicks);
        }

        #endregion

        #region RPCs

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestCatchMeUpOwnerRpc(long sinceUtcTicks, RpcParams rpcParams = default)
        {
            ulong requester = rpcParams.Receive.SenderClientId;
            Log($"Host received recap request from {requester} (since={sinceUtcTicks}).");

            // De-dupe: ignore a requester already waiting in the queue or being served.
            if (m_QueuedRequesters.Contains(requester) || (m_AwaitingGemini && m_Current.requester == requester))
                return;

            // Serve a fresh cached whole-meeting recap immediately (quota saver).
            if (sinceUtcTicks <= 0 && !string.IsNullOrEmpty(m_CachedRecap) &&
                (DateTime.UtcNow - m_CachedRecapUtc).TotalSeconds < m_RecapCacheSeconds)
            {
                Log($"Serving cached recap to {requester}.");
                DeliverToRequester(requester, m_CachedRecap);
                return;
            }

            // Nothing meaningful to summarize yet.
            string transcript = TranscriptionManager.Instance != null
                ? TranscriptionManager.Instance.GetTranscriptTextSince(sinceUtcTicks)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(transcript) || transcript.Trim().Length < m_MinTranscriptChars)
            {
                DeliverToRequester(requester, "Nothing to catch up on yet — the meeting just started.");
                return;
            }

            m_PendingRequests.Enqueue(new RecapRequest { requester = requester, sinceUtcTicks = sinceUtcTicks });
            m_QueuedRequesters.Add(requester);

            if (!m_Processing)
                StartCoroutine(ProcessQueue());
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void DeliverCatchMeUpClientRpc(string recap, RpcParams rpcParams)
        {
            Log("Recap delivered to local client.");
            OnRecapReceived?.Invoke(recap);
        }

        #endregion

        #region Host-side processing

        private IEnumerator ProcessQueue()
        {
            m_Processing = true;

            while (m_PendingRequests.Count > 0)
            {
                // Share the single Gemini pipeline with the rest of the app — wait our turn.
                while (GeminiService.Instance != null && GeminiService.Instance.IsProcessing)
                    yield return null;

                if (GeminiService.Instance == null)
                {
                    // No AI available; drain the queue with a graceful message.
                    while (m_PendingRequests.Count > 0)
                    {
                        var r = m_PendingRequests.Dequeue();
                        m_QueuedRequesters.Remove(r.requester);
                        DeliverToRequester(r.requester, "AI service is unavailable right now.");
                    }
                    break;
                }

                m_Current = m_PendingRequests.Dequeue();
                m_QueuedRequesters.Remove(m_Current.requester);

                string transcript = TranscriptionManager.Instance != null
                    ? TranscriptionManager.Instance.GetTranscriptTextSince(m_Current.sinceUtcTicks)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    DeliverToRequester(m_Current.requester, "Nothing to catch up on yet.");
                    continue;
                }

                m_AwaitingGemini = true;
                GeminiService.Instance.AskQuestion(transcript, BuildPrompt());

                // Wait for OnGeminiAnswer / OnGeminiError to clear the flag (with a safety timeout).
                float timeout = 30f;
                while (m_AwaitingGemini && timeout > 0f)
                {
                    timeout -= Time.deltaTime;
                    yield return null;
                }

                if (m_AwaitingGemini)
                {
                    // Timed out without a callback.
                    m_AwaitingGemini = false;
                    DeliverToRequester(m_Current.requester, "The recap timed out — please try again.");
                }
            }

            m_Processing = false;
        }

        private string BuildPrompt()
        {
            string language = TranscriptionManager.Instance != null
                ? TranscriptionManager.Instance.CurrentLanguage.DisplayName()
                : "English";

            return
                "A participant just joined a live meeting that is already in progress. " +
                "From the transcript below, give them a brief catch-up in up to three short sections — " +
                "Key points, Decisions made, and Open action items. Be concise and present-tense, " +
                "no more than about 120 words. Omit any section that has nothing. " +
                $"Write the reply in {language}.";
        }

        private void OnGeminiAnswer(string answer)
        {
            // OnAnswerResult is shared with AIMeetingAssistant; only handle it if WE asked.
            if (!m_AwaitingGemini) return;
            m_AwaitingGemini = false;

            m_CachedRecap = answer;
            m_CachedRecapUtc = DateTime.UtcNow;
            DeliverToRequester(m_Current.requester, answer);
        }

        private void OnGeminiError(string error)
        {
            if (!m_AwaitingGemini) return;
            m_AwaitingGemini = false;
            DeliverToRequester(m_Current.requester,
                string.IsNullOrEmpty(error) ? "Could not generate a recap." : $"Recap failed: {error}");
        }

        private void DeliverToRequester(ulong requester, string text)
        {
            // If the host requested its own recap, just raise locally.
            if (requester == NetworkManager.Singleton.LocalClientId)
            {
                OnRecapReceived?.Invoke(text);
                return;
            }
            DeliverCatchMeUpClientRpc(text, RpcTarget.Single(requester, RpcTargetUse.Temp));
        }

        #endregion

        private void Log(string msg)
        {
            if (m_DebugLogs) Debug.Log($"<color=#34A853>[CatchMeUp]</color> {msg}");
        }
    }
}
