using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;

namespace XRMultiplayer.Presentation
{
    [RequireComponent(typeof(PresentationTVManager))]
    public class PresentationNetworkManager : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private FirestoreRoomSync roomSync;

        private PresentationTVManager tvManager;
        
        // Use 4096 bytes for long Firebase URLs (Token can differ)
        private NetworkVariable<FixedString4096Bytes> m_CurrentFileUrl = new NetworkVariable<FixedString4096Bytes>(
            "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            
        // Track WHO is presenting (ClientId)
        private NetworkVariable<ulong> m_PresenterId = new NetworkVariable<ulong>(
            ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            
        // Pagination for multi-page documents
        private NetworkVariable<int> m_CurrentPageIndex = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<int> m_TotalPages = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<int> m_DocumentShareCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private NetworkVariable<int> m_CumulativePageCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            
        // Sync the Room/Lobby ID to all clients to ensure they share the same Firestore node
        public NetworkVariable<FixedString64Bytes> SyncedRoomId = new NetworkVariable<FixedString64Bytes>(
            "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
            
        // Local cache of page URLs (only presenter needs this)
        private List<string> m_PageUrls = new List<string>();
        private string m_LastRegisteredDocumentUrl = string.Empty;
        private string m_PendingSessionId;
        private bool m_FirebaseAuthSubscribed;
        
        // Events for UI
        public event Action<int, int> OnPageChanged; // (currentPage, totalPages)
        public event Action<string> OnFileChanged; // (fileUrl) - fired when a new file is loaded
        public event Action<bool> OnPresentationActiveChanged; // (isActive)
        public event Action<int> OnDocumentShared; // (pageCount)
        public event Action<int> OnCumulativePageCountChanged; // (totalCumulativePages)

        private void Awake()
        {
            tvManager = GetComponent<PresentationTVManager>();
        }

        public override void OnNetworkSpawn()
        {
            // Always start clean
            tvManager.Clear();
            OnPresentationActiveChanged?.Invoke(false);

            m_CurrentFileUrl.OnValueChanged += OnUrlChanged;
            m_CurrentPageIndex.OnValueChanged += OnPageIndexChanged;
            m_TotalPages.OnValueChanged += OnTotalPagesChanged;
            m_DocumentShareCount.OnValueChanged += OnDocumentShareCountChanged;
            m_CumulativePageCount.OnValueChanged += OnCumulativePageCountChangedInternal;
            
            // Wait for RoomSync to be ready
            StartCoroutine(InitializeRoomSyncRoutine());

            // Server-side: Listen for disconnects to clear screen
            if (IsServer)
            {
                // On Server, determine the Room ID and sync it
                string sessionId = GetCurrentSessionId();
                
                // If "local_session", keep it generic so all local clients join the same room
                if (sessionId == "local_session")
                {
                    // No random ID!
                }
                
                SyncedRoomId.Value = new FixedString64Bytes(sessionId);
                
                if (NetworkManager.Singleton != null)
                {
                    NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
                }
            }
        }
        
        private System.Collections.IEnumerator InitializeRoomSyncRoutine()
        {
            // 1. Wait for Singleton
            while (FirestoreRoomSync.Instance == null)
            {
                 // Try find
                 roomSync = FindFirstObjectByType<FirestoreRoomSync>();
                 if (roomSync == null)
                 {
                    // Create lazy
                    var syncObj = new GameObject("FirestoreRoomSync");
                    roomSync = syncObj.AddComponent<FirestoreRoomSync>();
                    DontDestroyOnLoad(syncObj);
                 }
                 yield return null;
            }
            
            roomSync = FirestoreRoomSync.Instance;

            // 2. Wait for Session Manager / Network Connection
            // CRITICAL FIX: Wait until we are actually connected so LocalClientId is valid.
            float timeout = 10f; // Increased timeout
            float timer = 0f;
            while (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsConnectedClient && timer < timeout)
            {
                 yield return null;
                 timer += Time.deltaTime;
            }
            
            // 3. Wait for SyncedRoomId to be populated by Server
            timer = 0f;
            while (string.IsNullOrEmpty(SyncedRoomId.Value.ToString()) && timer < timeout)
            {
                // If we are server, we set it above, so this should pass instantly.
                // If client, we wait for replication.
                if (timer % 1f < 0.1f) Debug.Log("[PresentationNetworkManager] Waiting for SyncedRoomId...");
                yield return null;
                timer += Time.deltaTime;
            }

            string finalSessionId = SyncedRoomId.Value.ToString();

            if (string.IsNullOrEmpty(finalSessionId))
            {
                Debug.LogWarning("[PresentationNetworkManager] Failed to receive SyncedRoomId from Server. Using fallback.");
                finalSessionId = "local_session"; // Default shareable ID
            }
            
            // Additional safety wait
            yield return new WaitForSeconds(0.2f);

            m_PendingSessionId = finalSessionId;

            // CRITICAL FIX: Do NOT block the whole routine waiting for Firebase Auth.
            // Players should still receive initial Netcode sync even if not logged in.
            // Initialize room sync immediately (works anonymously for public page sync!)
            InitializeRoomSync(finalSessionId);

            // Still subscribe to Firebase changes in case they log in later
            var firebase = FirebaseStorageManager.Instance ?? FindFirstObjectByType<FirebaseStorageManager>();
            if (firebase != null && !m_FirebaseAuthSubscribed)
            {
                firebase.OnUserChanged += OnFirebaseAuthChanged;
                m_FirebaseAuthSubscribed = true;
            }
            
            // Initial sync if not empty (from Netcode NetworkVariables)
            if (!string.IsNullOrEmpty(m_CurrentFileUrl.Value.ToString()))
            {
                tvManager.LoadUrl(m_CurrentFileUrl.Value.ToString());
            }
            
            // Notify UI of initial page state
            OnPageChanged?.Invoke(m_CurrentPageIndex.Value, m_TotalPages.Value);
        }

        private IEnumerator WaitForFirebaseAndInitializeRoomSync(string sessionId)
        {
            // Firebase auth is required for presentation sync and Storage-backed slides
            while (true)
            {
                var firebase = FirebaseStorageManager.Instance ?? FindFirstObjectByType<FirebaseStorageManager>();
                if (firebase != null && firebase.IsAuthenticated)
                {
                    if (!m_FirebaseAuthSubscribed)
                    {
                        firebase.OnUserChanged += OnFirebaseAuthChanged;
                        m_FirebaseAuthSubscribed = true;
                    }
                    break;
                }

                yield return new WaitForSeconds(0.5f);
            }
            
            InitializeRoomSync(sessionId);
        }

        private void InitializeRoomSync(string sessionIdOverride = null)
        {
            // Always prefer the Singleton instance
            roomSync = FirestoreRoomSync.Instance;

            // Find or create if missing
            if (roomSync == null)
            {
                roomSync = FindFirstObjectByType<FirestoreRoomSync>();
            }
            
            if (roomSync == null)
            {
                // Create one if it doesn't exist
                var syncObj = new GameObject("FirestoreRoomSync");
                roomSync = syncObj.AddComponent<FirestoreRoomSync>();
                DontDestroyOnLoad(syncObj);
            }

            // Get our Client ID
            string userId = NetworkManager.Singleton.LocalClientId.ToString();
            
            // Determine Session ID (Room ID)
            // Use the Override if provided (from SyncedRoomId or Fallback), otherwise default to local lookup
            string sessionId = !string.IsNullOrEmpty(sessionIdOverride) ? sessionIdOverride : GetCurrentSessionId();
            
            // Determine if we are Host
            // CRITICAL: Trust Netcode's IsHost/IsServer status for authority check
            bool isHost = IsServer; 
            
            Debug.Log($"[PresentationNetworkManager] Initializing with SessionID: {sessionId}. IsHost: {isHost}");

            roomSync.InitializeRoom(sessionId, userId, GetLocalPlayerName(), isHost);
            
            // Subscribe to room sync events
            roomSync.OnPresentationStateChanged += OnRoomSyncStateChanged;
            roomSync.OnPresentationCleared += OnRoomSyncCleared;
        }
        
        private string GetCurrentSessionId()
        {
            // Try to get from XRINetworkGameManager
            if (XRINetworkGameManager.Instance?.sessionManager?.currentSession != null)
            {
                return XRINetworkGameManager.Instance.sessionManager.currentSession.Id;
            }
            return "local_session";
        }
        
        private string GetLocalPlayerName()
        {
            if (XRINetworkPlayer.LocalPlayer != null)
            {
                return XRINetworkPlayer.LocalPlayer.playerName;
            }
            return "Player";
        }
        
        private void OnRoomSyncStateChanged(FirestoreRoomSync.PresentationState state)
        {
            if (state == null) return;
            if (!FirebaseStorageManager.IsUserAuthenticated) return;

            Debug.Log($"[PresentationNetworkManager] OnRoomSyncStateChanged: url={state.fileUrl}, page={state.currentPage}, isOwner={IsOwner}, isSpawned={IsSpawned}");
            
            // DEBUG: Trace why we think we are/aren't the owner
            if (state != null)
            {
                string localId = NetworkManager.Singleton?.LocalClientId.ToString() ?? "null";
                Debug.Log($"[PresentationNetworkManager] ID Check: LocalClient={localId}, RTDB_Presenter={state.presenterId}, IsLocalUserPresenter={roomSync.IsLocalUserPresenter}");
            }

            if (IsOwner || !IsSpawned)
            {
                RegisterDocumentShareIfNeeded(state.fileUrl, state.currentPage, state.totalPages);

                // We are the owner of the NetworkObject (host) OR we are offline/testing
                // Update NetworkVariables to sync with non-firebase clients (if any)
                // AND this triggers OnUrlChanged locally for us too
                // Fix: Compare as strings to prevent default FixedString32Bytes implicit cast truncating long URLs
                if (m_CurrentFileUrl.Value.ToString() != state.fileUrl)
                {
                    Debug.Log($"[PresentationNetworkManager] Updating NetworkVariable URL to: {state.fileUrl}");
                    m_CurrentFileUrl.Value = new FixedString4096Bytes(state.fileUrl);
                }
                
                // CRITICAL FIX: Update TotalPages BEFORE CurrentPage to ensure events show correct Total.
                if (m_TotalPages.Value != state.totalPages)
                {
                    m_TotalPages.Value = state.totalPages;
                }
                
                // This will trigger OnPageIndexChanged if value changes
                if (m_CurrentPageIndex.Value != state.currentPage)
                {
                    m_CurrentPageIndex.Value = state.currentPage;
                }
            }
            else
            {
                // We are a client, so we trust Firebase directly (reduced latency)
                // instead of waiting for NetworkVariable replication
                Debug.Log($"[PresentationNetworkManager] Client loading URL directly: {state.fileUrl}");
                tvManager.LoadUrl(state.fileUrl);
            }
            
            // REMOVED: Explicit OnPageChanged invoke. 
            // We rely on NetworkVariables (OnPageIndexChanged/OnTotalPagesChanged) to drive this event consistently.
        }
        
        private void OnRoomSyncCleared()
        {
            if (!FirebaseStorageManager.IsUserAuthenticated) return;

            Debug.Log("[PresentationNetworkManager] Room sync cleared - clearing TV display");
            
            // Always clear the TV display immediately
            tvManager.Clear();
            OnPresentationActiveChanged?.Invoke(false);
            
            // Also update NetworkVariables if we're the owner
            if (IsOwner || !IsSpawned)
            {
                m_CurrentFileUrl.Value = "";
                m_CurrentPageIndex.Value = 0;
                m_TotalPages.Value = 1;
                m_PresenterId.Value = ulong.MaxValue;
            }
            
            m_PageUrls.Clear();
            m_LastRegisteredDocumentUrl = string.Empty;
            OnPageChanged?.Invoke(0, 1);
        }

        void OnFirebaseAuthChanged(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                // Let the active anonymous sync persist on sign-out
                return;
            }

            if (roomSync != null && !roomSync.IsRoomInitialized &&
                NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
            {
                string sessionId = SyncedRoomId.Value.ToString();
                if (string.IsNullOrEmpty(sessionId))
                    sessionId = m_PendingSessionId;
                if (string.IsNullOrEmpty(sessionId))
                    sessionId = GetCurrentSessionId();

                if (!string.IsNullOrEmpty(sessionId))
                    InitializeRoomSync(sessionId);
            }
        }

        static bool RequireFirebaseAuth(string operation)
        {
            var firebase = FirebaseStorageManager.Instance;
            if (firebase != null)
                return firebase.TryEnsureAuthenticated(operation);

            PresentationUIManager.RequestAuthentication(operation);
            return false;
        }

        public override void OnNetworkDespawn()
        {
            if (m_FirebaseAuthSubscribed)
            {
                var firebase = FirebaseStorageManager.Instance;
                if (firebase != null)
                    firebase.OnUserChanged -= OnFirebaseAuthChanged;
                m_FirebaseAuthSubscribed = false;
            }

            m_CurrentFileUrl.OnValueChanged -= OnUrlChanged;
            m_CurrentPageIndex.OnValueChanged -= OnPageIndexChanged;
            m_DocumentShareCount.OnValueChanged -= OnDocumentShareCountChanged;
            m_CumulativePageCount.OnValueChanged -= OnCumulativePageCountChangedInternal;
            
            // CRITICAL FIX for shared-object dev setups:
            // In development environments where multiple clients share the same scene objects,
            // only the PRESENTER should interact with roomSync during cleanup.
            // Non-presenters touching roomSync (even just unsubscribing) corrupts the shared state.
            if (roomSync != null)
            {
                // CRITICAL FIX: Only clean up the shared/local roomSync if THIS instance is ours.
                // If a remote player leaves, their Proxy instance despawns on our machine, 
                // but we shouldn't touch OUR roomSync (which is a global singleton).
                if (!IsOwner)
                {
                    Debug.Log($"[PresentationNetworkManager] Remote player object despawned (IsOwner=false). Ignoring roomSync cleanup.");
                }
                else
                {
                    // This is OUR player object despawning (we are disconnecting/leaving).
                    bool isPresenter = roomSync.IsLocalUserPresenter;
                    
                    if (isPresenter)
                    {
                        Debug.Log("[PresentationNetworkManager] Presenter leaving (IsOwner=true) - cleaning up room sync.");
                        roomSync.OnPresentationStateChanged -= OnRoomSyncStateChanged;
                        roomSync.OnPresentationCleared -= OnRoomSyncCleared;
                        roomSync.LeaveRoom();
                    }
                    else
                    {
                        // Non-presenter: Do NOT touch roomSync at all to preserve presenter's state
                        Debug.Log("[PresentationNetworkManager] Viewer leaving (IsOwner=true) - preserving roomSync state for presenter.");
                    }
                }
            }
            
            if (IsServer && NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            }
            
            // CRITICAL FIX: Only clear the TV if WE were the actual presenter.
            // In distributed authority or shared-object dev setups, a viewer leaving
            // should NOT wipe the presentation that the presenter is still showing.
            // The TV state should be driven by RTDB, not local despawn events.
            bool wasActualPresenter = roomSync != null && roomSync.IsLocalUserPresenter;
            
            if (wasActualPresenter)
            {
                Debug.Log($"[PresentationNetworkManager] OnNetworkDespawn: Presenter leaving, clearing TV.");
                tvManager.Clear();
            }
            else
            {
                Debug.Log($"[PresentationNetworkManager] OnNetworkDespawn: Viewer leaving, preserving TV state for other clients.");
            }
        }

        private void OnClientDisconnect(ulong clientId)
        {
            // If the disconnected client was the presenter, clear the screen
            if (m_PresenterId.Value == clientId)
            {
                Debug.Log($"[PresentationNetworkManager] Presenter {clientId} left. Clearing screen.");
                
                // Notify room sync to clear RTDB
                if (roomSync != null)
                {
                    roomSync.OnPresenterDisconnected(clientId.ToString());
                }
                
                m_CurrentFileUrl.Value = "";
                m_PresenterId.Value = ulong.MaxValue;
                m_CurrentPageIndex.Value = 0;
                m_TotalPages.Value = 1;
            }
        }

        private void OnUrlChanged(FixedString4096Bytes oldVal, FixedString4096Bytes newVal)
        {
            string url = newVal.ToString();
            
            // REMOVED: Authentication check. Viewers should be able to see the presentation 
            // via Netcode even if they aren't logged into Firebase.
            // if (!string.IsNullOrEmpty(url) && !FirebaseStorageManager.IsUserAuthenticated)
            //     return;

            Debug.Log($"[PresentationNetworkManager] URL Changed: {(string.IsNullOrEmpty(url) ? "CLEARED" : "LOADED")}");
            
            if (string.IsNullOrEmpty(url))
            {
                tvManager.Clear();
                m_LastRegisteredDocumentUrl = string.Empty;
                OnPresentationActiveChanged?.Invoke(false);
            }
            else
            {
                tvManager.LoadUrl(url);
                OnPresentationActiveChanged?.Invoke(true);
                OnFileChanged?.Invoke(url);
            }
        }

        private void OnPageIndexChanged(int oldVal, int newVal)
        {
            Debug.Log($"[PresentationNetworkManager] Page Changed: {newVal + 1}/{m_TotalPages.Value}");
            OnPageChanged?.Invoke(newVal, m_TotalPages.Value);
            
            // Load the new page URL if we have cached URLs (presenter)
            // Non-presenters will receive URL via OnUrlChanged
        }

        private void OnTotalPagesChanged(int oldVal, int newVal)
        {
            Debug.Log($"[PresentationNetworkManager] Total Pages Updated: {newVal} (Create event for current page: {m_CurrentPageIndex.Value + 1})");
            OnPageChanged?.Invoke(m_CurrentPageIndex.Value, newVal);
        }

        private void OnCumulativePageCountChangedInternal(int oldVal, int newVal)
        {
            OnCumulativePageCountChanged?.Invoke(newVal);
        }

        private void OnDocumentShareCountChanged(int oldVal, int newVal)
        {
            int pages = Mathf.Max(0, m_TotalPages.Value);
            Debug.Log($"[PresentationNetworkManager] Document shared event: +{pages} pages (share #{newVal}).");
            OnDocumentShared?.Invoke(pages);
        }

        /// <summary>
        /// Set a multi-page document. Pass in all page URLs.
        /// </summary>
        public void RequestSetDocument(List<string> pageUrls)
        {
            if (!RequireFirebaseAuth("sharing a presentation"))
                return;

            if (pageUrls == null || pageUrls.Count == 0) return;
            
            if (NetworkManager.Singleton == null)
            {
                Debug.LogWarning("[PresentationNetworkManager] NetworkManager not available.");
                return;
            }
            
            m_PageUrls = new List<string>(pageUrls);
            ulong senderId = NetworkManager.Singleton.LocalClientId;

            // Safety check: Make sure we're network spawned before sending RPCs
            if (!IsSpawned)
            {
                Debug.LogWarning("[PresentationNetworkManager] Not spawned yet, executing locally");
                SetDocumentContent(pageUrls[0], senderId, 0, pageUrls.Count);
                return;
            }

            if (IsOwner)
            {
                SetDocumentContent(pageUrls[0], senderId, 0, pageUrls.Count);
            }
            else
            {
                try
                {
                    SetDocumentOwnerRpc(pageUrls[0], senderId, 0, pageUrls.Count);
                }
                catch (System.Collections.Generic.KeyNotFoundException e)
                {
                    Debug.LogWarning($"[PresentationNetworkManager] RPC failed (not fully initialized), executing locally: {e.Message}");
                    SetDocumentContent(pageUrls[0], senderId, 0, pageUrls.Count);
                }
            }
        }

        /// <summary>
        /// Set a single file (backwards compatible).
        /// </summary>
        public void RequestSetFile(string url)
        {
            RequestSetDocument(new List<string> { url });
        }

        public void RequestNextPage()
        {
            if (!RequireFirebaseAuth("presentation navigation"))
                return;

            if (m_CurrentPageIndex.Value >= m_TotalPages.Value - 1) return;
            if (m_PageUrls.Count == 0) return;
            
            int newIndex = m_CurrentPageIndex.Value + 1;
            string newUrl = m_PageUrls[newIndex];
            
            if (!IsSpawned || IsOwner)
            {
                m_CurrentPageIndex.Value = newIndex;
                m_CurrentFileUrl.Value = newUrl;
            }
            else
            {
                try { NavigatePageOwnerRpc(newIndex, newUrl); }
                catch (System.Collections.Generic.KeyNotFoundException)
                {
                    m_CurrentPageIndex.Value = newIndex;
                    m_CurrentFileUrl.Value = newUrl;
                }
            }
        }
        
        public void RequestPrevPage()
        {
            if (!RequireFirebaseAuth("presentation navigation"))
                return;

            if (m_CurrentPageIndex.Value <= 0) return;
            if (m_PageUrls.Count == 0) return;
            
            int newIndex = m_CurrentPageIndex.Value - 1;
            string newUrl = m_PageUrls[newIndex];
            
            if (!IsSpawned || IsOwner)
            {
                m_CurrentPageIndex.Value = newIndex;
                m_CurrentFileUrl.Value = newUrl;
            }
            else
            {
                try { NavigatePageOwnerRpc(newIndex, newUrl); }
                catch (System.Collections.Generic.KeyNotFoundException)
                {
                    m_CurrentPageIndex.Value = newIndex;
                    m_CurrentFileUrl.Value = newUrl;
                }
            }
        }

        public void RequestClear()
        {
            if (!RequireFirebaseAuth("stopping presentation"))
                return;

            m_PageUrls.Clear();
            
            if (!IsSpawned || IsOwner)
            {
                SetDocumentContent("", ulong.MaxValue, 0, 1);
            }
            else
            {
                try { SetDocumentOwnerRpc("", ulong.MaxValue, 0, 1); }
                catch (System.Collections.Generic.KeyNotFoundException)
                {
                    SetDocumentContent("", ulong.MaxValue, 0, 1);
                }
            }
        }
        
        private void SetDocumentContent(string url, ulong presenterId, int pageIndex, int totalPages)
        {
            m_CurrentFileUrl.Value = url;
            m_PresenterId.Value = presenterId;
            m_CurrentPageIndex.Value = pageIndex;
            m_TotalPages.Value = totalPages;

            RegisterDocumentShareIfNeeded(url, pageIndex, totalPages);

            // Fallback for offline/local execution paths where NetworkVariable callbacks may not fire.
            if (!IsSpawned)
            {
                bool isActive = !string.IsNullOrEmpty(url);
                OnPresentationActiveChanged?.Invoke(isActive);

                if (isActive && pageIndex == 0)
                {
                    OnDocumentShared?.Invoke(Mathf.Max(0, totalPages));
                    OnCumulativePageCountChanged?.Invoke(m_CumulativePageCount.Value);
                }
            }
        }

        private void RegisterDocumentShareIfNeeded(string url, int pageIndex, int totalPages)
        {
            if (string.IsNullOrEmpty(url) || pageIndex != 0 || totalPages <= 0)
                return;

            // Prevent double-counting when the same shared URL is echoed back through sync.
            if (string.Equals(m_LastRegisteredDocumentUrl, url, StringComparison.Ordinal))
                return;

            m_LastRegisteredDocumentUrl = url;
            m_DocumentShareCount.Value = m_DocumentShareCount.Value + 1;
            m_CumulativePageCount.Value = m_CumulativePageCount.Value + totalPages;
        }

        [Rpc(SendTo.Owner)]
        private void SetDocumentOwnerRpc(string url, ulong senderId, int pageIndex, int totalPages)
        {
            SetDocumentContent(url, senderId, pageIndex, totalPages);
        }
        
        [Rpc(SendTo.Owner)]
        private void NavigatePageOwnerRpc(int pageIndex, string url)
        {
            m_CurrentPageIndex.Value = pageIndex;
            m_CurrentFileUrl.Value = url;
        }
        
        // Public getters for UI
        public int GetCurrentPage() => m_CurrentPageIndex.Value;
        public int GetTotalPages() => m_TotalPages.Value;
        public int GetCumulativePageCount() => m_CumulativePageCount.Value;
    }
}
