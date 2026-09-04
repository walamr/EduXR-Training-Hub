using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// Manages real-time synchronization of presentation state with Firebase RTDB.
    /// Handles room initialization, presenter tracking, late-joiner sync, and cleanup.
    /// </summary>
    public class FirestoreRoomSync : MonoBehaviour
    {
        public static FirestoreRoomSync Instance { get; private set; }

        [Header("Firebase Config")]
        [SerializeField] private string databaseUrl = "https://xr-meeting-hub-default-rtdb.europe-west1.firebasedatabase.app";

        [Header("Sync Settings")]
        [SerializeField] private float pollInterval = 1.0f; // Real-time polling interval
        [SerializeField] private bool debugMode = true;

        // Current room state
        private string currentRoomId;
        private string localUserId;
        private string localUserName;
        private bool isInitialized = false;
        private Coroutine pollCoroutine;

        // Cached presentation state
        private PresentationState cachedState = new PresentationState();
        private long lastUpdateTimestamp = 0;

        // Events for UI/Network sync
        public event Action<PresentationState> OnPresentationStateChanged;
        public event Action OnPresentationCleared;
        public event Action<string> OnError;

        // Is the local user currently the presenter?
        public bool IsLocalUserPresenter 
        {
            get 
            {
                // CRITICAL SAFETY: If we are not the host, but our ID is "0" (which happens in rare race conditions),
                // we must NOT assume we are the presenter if the remote presenter is also "0" (The real Host).
                // This prevents "Identity Theft" where a Client wipes the Host's presentation.
                if (localUserId == "0" && !isHost) return false;

                return !string.IsNullOrEmpty(cachedState.presenterId) && 
                        cachedState.presenterId == localUserId;
            }
        }
        public bool HasActivePresentation => !string.IsNullOrEmpty(cachedState.fileUrl);
        public PresentationState CurrentState => cachedState;
        public bool IsRoomInitialized => isInitialized && !string.IsNullOrEmpty(currentRoomId);

        [Serializable]
        public class PresentationState
        {
            public string fileUrl = "";
            public int currentPage = 0;
            public int totalPages = 1;
            public string presenterId = "";
            public string presenterName = "";
            public string documentName = "";
            public long updatedAt = 0;
            
            // Page URLs for multi-page documents (not stored in RTDB, cached locally by presenter)
            [NonSerialized] public string[] pageUrls;

            public bool IsEmpty => string.IsNullOrEmpty(fileUrl);
            
            public void Clear()
            {
                fileUrl = "";
                currentPage = 0;
                totalPages = 1;
                presenterId = "";
                presenterName = "";
                documentName = "";
                updatedAt = 0;
                pageUrls = null;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            SubscribeFirebaseAuth();
        }

        private void OnDisable()
        {
            UnsubscribeFirebaseAuth();
        }

        private void OnDestroy()
        {
            UnsubscribeFirebaseAuth();
            StopPolling();
            
            // If we were the presenter, clear the state synchronously
            if (IsLocalUserPresenter && isInitialized && !string.IsNullOrEmpty(currentRoomId))
            {
                ClearPresentationSync();
            }
        }
        
        private void OnApplicationQuit()
        {
            // Clear presentation when app quits (if we're presenting)
            if (IsLocalUserPresenter && isInitialized && !string.IsNullOrEmpty(currentRoomId))
            {
                ClearPresentationSync();
            }
        }
        
        /// <summary>
        /// Synchronous version for OnDestroy/OnApplicationQuit where we can't use coroutines.
        /// </summary>
        private void ClearPresentationSync()
        {
            try
            {
                string url = $"{databaseUrl}/rooms/{currentRoomId}/presentation.json";
                
                var emptyState = new PresentationState
                {
                    updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                string json = JsonUtility.ToJson(emptyState);
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                
                using (UnityWebRequest request = new UnityWebRequest(url, "PUT"))
                {
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    
                    // Send synchronously (blocking) - acceptable for quit/destroy
                    var operation = request.SendWebRequest();
                    
                    // Wait briefly for completion (best effort)
                    float timeout = 2f;
                    float elapsed = 0f;
                    while (!operation.isDone && elapsed < timeout)
                    {
                        System.Threading.Thread.Sleep(50);
                        elapsed += 0.05f;
                    }
                    
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        LogDebug("Cleared presentation state (sync)");
                    }
                }
            }
            catch (Exception e)
            {
                LogDebug($"Failed to clear presentation sync: {e.Message}");
            }
            
            cachedState.Clear();
        }

        #region Initialization

        private bool isHost = false;

        /// <summary>
        /// Initialize room sync when joining/creating a session.
        /// </summary>
        public void InitializeRoom(string roomId, string userId, string userName, bool isHost)
        {

            if (string.IsNullOrEmpty(roomId))
            {
                LogDebug("Cannot initialize with empty roomId");
                return;
            }

            currentRoomId = roomId;
            localUserId = userId;
            localUserName = userName;
            this.isHost = isHost;
            isInitialized = true;

            LogDebug($"Initialized room sync: roomId={roomId}, userId={userId}, isHost={isHost}");

            // Start real-time polling
            StartPolling();

            // Initialize room in RTDB (creates empty structure if not exists)
            StartCoroutine(InitializeRoomInRTDB());
        }

        // Flag to track if WE actually started a presentation this session
        // This prevents us from accidentally clearing a "stale" session that belongs to us from a previous run
        // just because we joined and left without doing anything.
        private bool m_HasActivelyPresented = false;

        /// <summary>
        /// Cleanup when leaving room.
        /// </summary>
        public void LeaveRoom()
        {
            LogDebug("Leaving room, cleaning up...");
            
            StopPolling();

            // If we were presenting, AND we actually started a presentation this session
            if (IsLocalUserPresenter && m_HasActivelyPresented)
            {
                // Fire-and-forget cleanup
                StartCoroutine(ClearPresentationInRTDB(currentRoomId));
            }

            cachedState.Clear();
            currentRoomId = null;
            isInitialized = false;
            m_HasActivelyPresented = false;
        }

        private IEnumerator InitializeRoomInRTDB()
        {
            // Reset flag on new room join
            m_HasActivelyPresented = false;

            // Check if room exists, if not create empty structure
            string url = $"{databaseUrl}/rooms/{currentRoomId}/presentation.json";

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string response = request.downloadHandler.text;
                    
                    if (response == "null" || string.IsNullOrEmpty(response))
                    {
                        // Room doesn't exist or is empty
                        // CRITICAL FIX: Only the HOST should create/initialize room state.
                        // If a non-host creates "empty" state, it can WIPE existing data that
                        // they just haven't received yet due to network timing.
                        if (isHost)
                        {
                            yield return CreateEmptyRoomState();
                            LogDebug("Host created empty room state");
                        }
                        else
                        {
                            LogDebug("Room is empty. Waiting for host to create state or presenter to share.");
                        }
                        // Notify listeners that this room is clear (so UI knows there's no presentation)
                        OnPresentationCleared?.Invoke();
                    }
                    else
                    {
                        // Room exists, parse and cache state
                        try
                        {
                            var state = JsonUtility.FromJson<PresentationState>(response);
                            if (state != null)
                            {
                                cachedState = state;
                                lastUpdateTimestamp = state.updatedAt;
                                
                                // Notify listeners of initial state
                                if (!state.IsEmpty)
                                {
                                    LogDebug($"Found existing presentation: {state.documentName} page {state.currentPage + 1}/{state.totalPages}");
                                    OnPresentationStateChanged?.Invoke(state);
                                }
                                else
                                {
                                    // State exists but is empty
                                    OnPresentationCleared?.Invoke();
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            LogDebug($"Failed to parse room state: {e.Message}");
                        }
                    }
                }
                else
                {
                    LogDebug($"Failed to check room: {request.error}");
                }
            }
        }

        private IEnumerator CreateEmptyRoomState()
        {
            string url = $"{databaseUrl}/rooms/{currentRoomId}/presentation.json";
            
            var emptyState = new PresentationState
            {
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
            string json = JsonUtility.ToJson(emptyState);

            using (UnityWebRequest request = UnityWebRequest.Put(url, json))
            {
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    LogDebug("Created empty room state");
                }
            }
        }

        #endregion

        #region Presentation Control

        /// <summary>
        /// Start presenting a document (called by presenter).
        /// </summary>
        public void SetPresentation(string documentName, string[] pageUrls)
        {
            if (!RequireFirebaseAuth("presenting"))
                return;

            if (!isInitialized || pageUrls == null || pageUrls.Length == 0)
            {
                OnError?.Invoke("Cannot start presentation: not initialized or no pages");
                return;
            }

            // We are starting a presentation, so we own this session now
            m_HasActivelyPresented = true;

            var newState = new PresentationState
            {
                fileUrl = pageUrls[0],
                currentPage = 0,
                totalPages = pageUrls.Length,
                presenterId = localUserId,
                presenterName = localUserName,
                documentName = documentName,
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pageUrls = pageUrls
            };

            StartCoroutine(UpdatePresentationInRTDB(newState));
        }

        /// <summary>
        /// Navigate to a specific page (presenter only).
        /// </summary>
        public void NavigateToPage(int pageIndex)
        {
            if (!RequireFirebaseAuth("presentation navigation"))
                return;

            if (!IsLocalUserPresenter)
            {
                LogDebug("Cannot navigate: not the presenter");
                return;
            }

            if (cachedState.pageUrls == null || pageIndex < 0 || pageIndex >= cachedState.pageUrls.Length)
            {
                LogDebug($"Invalid page index: {pageIndex}");
                return;
            }

            cachedState.currentPage = pageIndex;
            cachedState.fileUrl = cachedState.pageUrls[pageIndex];
            cachedState.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            StartCoroutine(UpdatePresentationInRTDB(cachedState));
        }

        /// <summary>
        /// Go to next page.
        /// </summary>
        public void NextPage()
        {
            if (cachedState.currentPage < cachedState.totalPages - 1)
            {
                NavigateToPage(cachedState.currentPage + 1);
            }
        }

        /// <summary>
        /// Go to previous page.
        /// </summary>
        public void PreviousPage()
        {
            if (cachedState.currentPage > 0)
            {
                NavigateToPage(cachedState.currentPage - 1);
            }
        }

        /// <summary>
        /// Stop presenting (clears state for everyone).
        /// </summary>
        public void StopPresentation()
        {
            if (!RequireFirebaseAuth("stopping presentation"))
                return;

            // Allow stopping if we're the presenter OR if there's no active presentation
            if (!IsLocalUserPresenter && HasActivePresentation)
            {
                LogDebug("Cannot stop: not the presenter");
                return;
            }

            LogDebug("StopPresentation called - clearing RTDB");
            StartCoroutine(ClearPresentationInRTDB(currentRoomId));
        }
        
        /// <summary>
        /// Force clear presentation (used by server when presenter disconnects).
        /// </summary>
        public void ForceClearPresentation()
        {
            if (!RequireFirebaseAuth("clearing presentation"))
                return;

            LogDebug("ForceClearPresentation called");
            StartCoroutine(ClearPresentationInRTDB(currentRoomId));
        }

        private IEnumerator UpdatePresentationInRTDB(PresentationState state)
        {
            string url = $"{databaseUrl}/rooms/{currentRoomId}/presentation.json";
            
            // Create a copy without pageUrls (don't store in RTDB)
            var stateToSave = new PresentationState
            {
                fileUrl = state.fileUrl,
                currentPage = state.currentPage,
                totalPages = state.totalPages,
                presenterId = state.presenterId,
                presenterName = state.presenterName,
                documentName = state.documentName,
                updatedAt = state.updatedAt
            };
            
            string json = JsonUtility.ToJson(stateToSave);

            using (UnityWebRequest request = UnityWebRequest.Put(url, json))
            {
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 10;
                
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    cachedState = state; // Keep local copy with pageUrls
                    lastUpdateTimestamp = state.updatedAt;
                    LogDebug($"Updated presentation: {state.documentName} page {state.currentPage + 1}");
                    OnPresentationStateChanged?.Invoke(state);
                }
                else
                {
                    LogDebug($"Failed to update presentation: {request.error}");
                    OnError?.Invoke($"Sync failed: {request.error}");
                }
            }
        }

        private IEnumerator ClearPresentationInRTDB(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                cachedState.Clear();
                OnPresentationCleared?.Invoke();
                yield break;
            }

            string url = $"{databaseUrl}/rooms/{roomId}/presentation.json";
            
            var emptyState = new PresentationState
            {
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
            string json = JsonUtility.ToJson(emptyState);

            using (UnityWebRequest request = UnityWebRequest.Put(url, json))
            {
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    LogDebug("Cleared presentation state");
                }
                else
                {
                    LogDebug($"Failed to clear presentation: {request.error}");
                    OnError?.Invoke($"Clear failed: {request.error}");
                }
            }

            cachedState.Clear();
            OnPresentationCleared?.Invoke();
        }

        #endregion

        #region Real-Time Polling

        private void StartPolling()
        {
            StopPolling();
            pollCoroutine = StartCoroutine(PollForChanges());
        }

        private void StopPolling()
        {
            if (pollCoroutine != null)
            {
                StopCoroutine(pollCoroutine);
                pollCoroutine = null;
            }
        }

        private IEnumerator PollForChanges()
        {
            while (isInitialized && !string.IsNullOrEmpty(currentRoomId))
            {
                yield return new WaitForSeconds(pollInterval);

                // Poll anonymously even when not authenticated to allow seamless presentation state sync

                string url = $"{databaseUrl}/rooms/{currentRoomId}/presentation.json";

                using (UnityWebRequest request = UnityWebRequest.Get(url))
                {
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        ProcessPollResponse(request.downloadHandler.text);
                    }
                }
            }
        }

        private void ProcessPollResponse(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null")
            {
                // Room was deleted or empty
                if (HasActivePresentation)
                {
                    cachedState.Clear();
                    OnPresentationCleared?.Invoke();
                }
                return;
            }

            try
            {
                var state = JsonUtility.FromJson<PresentationState>(json);
                
                if (state == null) return;

                // Only process if newer than our cached state
                if (state.updatedAt <= lastUpdateTimestamp)
                {
                    return;
                }

                lastUpdateTimestamp = state.updatedAt;

                // Check if presentation was cleared
                if (state.IsEmpty && HasActivePresentation)
                {
                    LogDebug("Presentation cleared by presenter");
                    cachedState.Clear();
                    OnPresentationCleared?.Invoke();
                    return;
                }

                // Check if state actually changed
                bool changed = state.fileUrl != cachedState.fileUrl ||
                              state.currentPage != cachedState.currentPage ||
                              state.presenterId != cachedState.presenterId;

                if (changed && !state.IsEmpty)
                {
                    // Preserve local pageUrls if we're the presenter
                    if (IsLocalUserPresenter && cachedState.pageUrls != null)
                    {
                        state.pageUrls = cachedState.pageUrls;
                    }

                    cachedState = state;
                    LogDebug($"State updated: {state.documentName} page {state.currentPage + 1}/{state.totalPages}");
                    OnPresentationStateChanged?.Invoke(state);
                }
            }
            catch (Exception e)
            {
                LogDebug($"Failed to parse poll response: {e.Message}");
            }
        }

        #endregion

        #region Utility

        static bool IsFirebaseAuthenticated()
        {
            return FirebaseStorageManager.Instance != null && FirebaseStorageManager.Instance.IsAuthenticated;
        }

        bool RequireFirebaseAuth(string operationName)
        {
            var firebase = FirebaseStorageManager.Instance;
            if (firebase != null)
                return firebase.TryEnsureAuthenticated(operationName);

            PresentationUIManager.RequestAuthentication(operationName);
            return false;
        }

        void SubscribeFirebaseAuth()
        {
            var firebase = FirebaseStorageManager.Instance;
            if (firebase == null)
                firebase = FindFirstObjectByType<FirebaseStorageManager>();
            if (firebase != null)
                firebase.OnUserChanged -= OnFirebaseUserChanged;
            if (firebase != null)
                firebase.OnUserChanged += OnFirebaseUserChanged;
        }

        void UnsubscribeFirebaseAuth()
        {
            var firebase = FirebaseStorageManager.Instance;
            if (firebase == null)
                firebase = FindFirstObjectByType<FirebaseStorageManager>();
            if (firebase != null)
                firebase.OnUserChanged -= OnFirebaseUserChanged;
        }

        void OnFirebaseUserChanged(string userId)
        {
            // Allow active anonymous sync to persist even on sign-out
        }

        private void LogDebug(string message)
        {
            if (debugMode)
            {
                Debug.Log($"<color=#FFA500>[FirestoreRoomSync]</color> {message}");
            }
        }

        /// <summary>
        /// Called when presenter disconnects unexpectedly (from network callbacks).
        /// This is called on the SERVER when it detects a client left.
        /// </summary>
        public void OnPresenterDisconnected(string presenterId)
        {
            LogDebug($"OnPresenterDisconnected called for {presenterId}, current presenter: {cachedState.presenterId}");
            
            // BUG FIX: Only clear if the ACTUAL presenter disconnected.
            // Previously this cleared if *anyone* disconnected while I (Host) was not the presenter.
            if (cachedState.presenterId == presenterId)
            {
                LogDebug($"Presenter {presenterId} disconnected, clearing state");
                ForceClearPresentation();
            }
        }

        #endregion
    }
}
