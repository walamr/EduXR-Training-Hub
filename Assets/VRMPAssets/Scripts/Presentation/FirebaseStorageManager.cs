using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// Manager for Firebase Auth and Storage operations in Unity.
    /// VR generates a code and displays it - user enters code on web to authenticate.
    /// </summary>
    public class FirebaseStorageManager : MonoBehaviour
    {
        public static FirebaseStorageManager Instance { get; private set; }

        [Header("Auth Config")]
        [SerializeField] private string apiKey = "REDACTED_FIREBASE_KEY"; // From google-services.json

        [Header("Firebase Config")]
        [SerializeField] private string databaseUrl = "https://xr-meeting-hub-default-rtdb.europe-west1.firebasedatabase.app";
        [SerializeField] private string storageBucket = "xr-meeting-hub.firebasestorage.app";

        [Header("Code Settings")]
        [SerializeField] private float pollInterval = 2f; // How often to check for auth
        [SerializeField] private float codeExpiryMinutes = 5f;

        // State
        private string currentUserId = null;
        private string currentUserEmail = null;
        private string idToken = null;
        private string refreshToken = null;
        private string currentCode = null;
        private Coroutine pollCoroutine;
        private Coroutine refreshCoroutine;

        // Events
        public event Action<string> OnUserChanged; // userId or null
        public event Action<string> OnError;
        /// <summary>Raised when a Firebase feature is used without a session (feature label for UI).</summary>
        public event Action<string> OnAuthenticationRequired;
        public event Action<string> OnCodeGenerated; // code to display

        // Public properties for external access
        public bool IsAuthenticated => !string.IsNullOrEmpty(currentUserId) && !string.IsNullOrEmpty(idToken);
        public string CurrentUserId => currentUserId;
        public string CurrentUserEmail => currentUserEmail;

        /// <summary>True when the singleton exists and has a valid Storage session.</summary>
        public static bool IsUserAuthenticated => Instance != null && Instance.IsAuthenticated;

        /// <summary>
        /// Returns false and raises <see cref="OnAuthenticationRequired"/> when there is no valid Firebase session.
        /// </summary>
        public bool TryEnsureAuthenticated(string operationName)
        {
            if (IsAuthenticated)
                return true;

            string label = string.IsNullOrEmpty(operationName) ? "this feature" : operationName;
            OnAuthenticationRequired?.Invoke(label);
            // Canvas starts inactive — UI may not have subscribed yet; open panel directly.
            PresentationUIManager.RequestAuthentication(label);
            return false;
        }

        /// <summary>
        /// Applies Firebase ID token authorization to a Storage REST upload/download request.
        /// </summary>
        public void ApplyUploadAuthorization(UnityWebRequest request) => ApplyAuthorizationHeader(request);

        /// <summary>
        /// Adds the Firebase Storage Authorization header when a session token is available.
        /// </summary>
        public void ApplyAuthorizationHeader(UnityWebRequest request)
        {
            if (request == null || string.IsNullOrEmpty(idToken))
                return;
            request.SetRequestHeader("Authorization", $"Bearer {idToken}");
        }
        
        /// <summary>
        /// Get the upload URL for a file path in Firebase Storage.
        /// Path is automatically prefixed with audit_logs/{userId}/.
        /// </summary>
        public string GetUploadUrl(string storagePath)
        {
            if (!IsAuthenticated) return null;
            string encodedPath = Uri.EscapeDataString($"audit_logs/{currentUserId}/{storagePath}");
            string finalUrl = $"https://firebasestorage.googleapis.com/v0/b/{storageBucket}/o/{encodedPath}?uploadType=media&alt=json";
            Debug.Log($"[FirebaseStorageManager] Generated Upload URL: {finalUrl} (Path: audit_logs/{currentUserId}/{storagePath})");
            return finalUrl;
        }

        /// <summary>
        /// Get the upload URL for an arbitrary Firebase Storage path.
        /// Unlike GetUploadUrl(), this does NOT add any prefix — the caller controls the full path.
        /// Used by features like Personal Cloud Snapshots that write to Users/{userId}/... paths.
        /// </summary>
        public string GetStorageUploadUrl(string fullStoragePath)
        {
            if (!IsAuthenticated) return null;
            string encodedPath = Uri.EscapeDataString(fullStoragePath);
            return $"https://firebasestorage.googleapis.com/v0/b/{storageBucket}/o/{encodedPath}?uploadType=media&alt=json";
        }

        // Simple file info class
        [Serializable]
        public class FileInfo
        {
            public string name;
            public string fullPath;
            public bool isFolder;
        }

        // Characters for code generation (excluding confusing chars like 0/O, 1/I/L)
        private const string CODE_CHARS = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // DontDestroyOnLoad requires a root GameObject; detach from parent first if needed
            if (transform.parent != null)
                transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            Debug.Log("[FirebaseStorageManager] Ready. Checking for saved session...");
            LoadAuth();
        }

        private void OnDestroy()
        {
            if (pollCoroutine != null) StopCoroutine(pollCoroutine);
            if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
        }

        #region Auth Persistence & Refresh

        private void SaveAuth(string uid, string email, string token, string refresh)
        {
            PlayerPrefs.SetString("firebase_uid", uid);
            PlayerPrefs.SetString("firebase_email", email);
            PlayerPrefs.SetString("firebase_token", token);
            if (!string.IsNullOrEmpty(refresh)) PlayerPrefs.SetString("firebase_refresh", refresh);
            PlayerPrefs.Save();
            Debug.Log("[FirebaseStorageManager] Session saved to PlayerPrefs.");
        }

        private void LoadAuth()
        {
            if (PlayerPrefs.HasKey("firebase_uid") && PlayerPrefs.HasKey("firebase_token"))
            {
                string uid = PlayerPrefs.GetString("firebase_uid");
                string email = PlayerPrefs.GetString("firebase_email");
                string token = PlayerPrefs.GetString("firebase_token");
                string refresh = PlayerPrefs.GetString("firebase_refresh", "");

                // Resume session
                SetSession(uid, email, token, refresh);
                Debug.Log($"[FirebaseStorageManager] Restored session for {email}.");
                
                // If we have a refresh token, verify/refresh immediately
                if (!string.IsNullOrEmpty(refresh))
                {
                    RefreshAccessToken();
                }
            }
            else
            {
                Debug.Log("[FirebaseStorageManager] No saved session found.");
            }
        }

        private void ClearAuth()
        {
            PlayerPrefs.DeleteKey("firebase_uid");
            PlayerPrefs.DeleteKey("firebase_email");
            PlayerPrefs.DeleteKey("firebase_token");
            PlayerPrefs.DeleteKey("firebase_refresh");
            PlayerPrefs.Save();
        }

        private void SetSession(string uid, string email, string token, string refresh)
        {
            currentUserId = uid;
            currentUserEmail = email;
            idToken = token;
            refreshToken = refresh;
            
            OnUserChanged?.Invoke(currentUserId);
            
            // Start refresh loop if we have a refresh token
            if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                refreshCoroutine = StartCoroutine(AutoRefreshRoutine());
            }
        }

        private IEnumerator AutoRefreshRoutine()
        {
            // Refresh every 45 minutes (tokens last 1 hour)
            while (true)
            {
                yield return new WaitForSeconds(45 * 60); 
                Debug.Log("[FirebaseStorageManager] Auto-refreshing token...");
                yield return RefreshTokenCoroutine();
            }
        }

        public void RefreshAccessToken()
        {
            StartCoroutine(RefreshTokenCoroutine());
        }

        private IEnumerator RefreshTokenCoroutine()
        {
            if (string.IsNullOrEmpty(refreshToken)) yield break;

            string url = $"https://securetoken.googleapis.com/v1/token?key={apiKey}";
            
            var form = new WWWForm();
            form.AddField("grant_type", "refresh_token");
            form.AddField("refresh_token", refreshToken);

            using (UnityWebRequest request = UnityWebRequest.Post(url, form))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var response = JsonUtility.FromJson<TokenRefreshResponse>(request.downloadHandler.text);
                    if (!string.IsNullOrEmpty(response.id_token))
                    {
                        Debug.Log("[FirebaseStorageManager] Token refreshed successfully.");
                        // Update session (keep existing UID/Email)
                        idToken = response.id_token;
                        if (!string.IsNullOrEmpty(response.refresh_token))
                        {
                            refreshToken = response.refresh_token;
                        }
                        
                        // Save updated tokens
                        SaveAuth(currentUserId, currentUserEmail, idToken, refreshToken);
                    }
                }
                else
                {
                    Debug.LogWarning($"[FirebaseStorageManager] Token refresh failed: {request.error} | {request.downloadHandler.text}");
                    // If refresh fails (e.g. revoked), sign out? 
                    // For now, keep local session until 403s happen, but maybe clear persistent logic?
                    // Better to let the user stay logged in 'offline' until they try to upload and fail.
                }
            }
        }

        #endregion

        #region Code Generation & Polling

        /// <summary>
        /// Generate a 6-character code and start polling for authentication.
        /// </summary>
        public void GenerateCode()
        {
            if (pollCoroutine != null)
            {
                StopCoroutine(pollCoroutine);
            }

            currentCode = GenerateRandomCode();
            Debug.Log($"[FirebaseStorageManager] Generated code: {currentCode}");
            
            StartCoroutine(RegisterCodeAndPoll());
        }

        private string GenerateRandomCode()
        {
            char[] code = new char[6];
            for (int i = 0; i < 6; i++)
            {
                code[i] = CODE_CHARS[UnityEngine.Random.Range(0, CODE_CHARS.Length)];
            }
            return new string(code);
        }

        private IEnumerator RegisterCodeAndPoll()
        {
            // Register the code in RTDB
            string url = $"{databaseUrl}/deviceCodes/{currentCode}.json";
            
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expiresAt = now + (long)(codeExpiryMinutes * 60 * 1000);
            
            var codeData = new DeviceCodeEntry
            {
                createdAt = now,
                expiresAt = expiresAt,
                status = "pending"
            };
            
            string jsonBody = JsonUtility.ToJson(codeData);
            
            using (UnityWebRequest request = UnityWebRequest.Put(url, jsonBody))
            {
                request.method = "PUT";
                request.SetRequestHeader("Content-Type", "application/json");
                
                yield return request.SendWebRequest();
                
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[FirebaseStorageManager] Failed to register code: {request.error}");
                    OnError?.Invoke("Failed to generate code. Check internet connection.");
                    yield break;
                }
            }
            
            Debug.Log($"[FirebaseStorageManager] Code registered. Display: {currentCode}");
            OnCodeGenerated?.Invoke(currentCode);
            
            // Start polling for authentication
            pollCoroutine = StartCoroutine(PollForAuthentication());
        }

        private IEnumerator PollForAuthentication()
        {
            float elapsed = 0f;
            float maxTime = codeExpiryMinutes * 60f;
            
            while (elapsed < maxTime)
            {
                yield return new WaitForSeconds(pollInterval);
                elapsed += pollInterval;
                
                // Check if code has been authenticated
                string url = $"{databaseUrl}/deviceCodes/{currentCode}.json";
                
                using (UnityWebRequest request = UnityWebRequest.Get(url))
                {
                    yield return request.SendWebRequest();
                    
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        continue; // Retry on failure
                    }
                    
                    string responseText = request.downloadHandler.text;
                    
                    if (string.IsNullOrEmpty(responseText) || responseText == "null")
                    {
                        // Code was deleted or expired
                        Debug.Log("[FirebaseStorageManager] Code no longer exists");
                        OnError?.Invoke("Code expired or was cancelled.");
                        yield break;
                    }
                    
                    var data = JsonUtility.FromJson<DeviceCodeData>(responseText);
                    
                    if (data != null && !string.IsNullOrEmpty(data.idToken))
                    {
                        // Authenticated!
                        Debug.Log($"[FirebaseStorageManager] Authenticated as: {data.email}");
                        
                        // Save and set session
                        SetSession(data.userId, data.email, data.idToken, data.refreshToken);
                        SaveAuth(data.userId, data.email, data.idToken, data.refreshToken);
                        
                        // Delete the code (one-time use)
                        yield return DeleteCode(currentCode);
                        currentCode = null;
                        yield break;
                    }
                }
            }
            
            // Timed out
            Debug.Log("[FirebaseStorageManager] Code expired (timeout)");
            OnError?.Invoke("Code expired. Generate a new one.");
            yield return DeleteCode(currentCode);
            currentCode = null;
        }

        private IEnumerator DeleteCode(string code)
        {
            if (string.IsNullOrEmpty(code)) yield break;
            
            string url = $"{databaseUrl}/deviceCodes/{code}.json";
            
            using (UnityWebRequest request = UnityWebRequest.Delete(url))
            {
                yield return request.SendWebRequest();
                // Ignore result - code deletion is best effort
            }
        }

        public void CancelCodeGeneration()
        {
            if (pollCoroutine != null)
            {
                StopCoroutine(pollCoroutine);
                pollCoroutine = null;
            }
            
            if (!string.IsNullOrEmpty(currentCode))
            {
                StartCoroutine(DeleteCode(currentCode));
                currentCode = null;
            }
        }

        public void SignOut()
        {
            CancelCodeGeneration();
            ClearAuth(); // Clear saved session
            
            currentUserId = null;
            currentUserEmail = null;
            idToken = null;
            refreshToken = null;
            OnUserChanged?.Invoke(null);
            
            if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
        }

        /// <summary>
        /// Convenience action for QA/testing in-editor when stale tokens are suspected.
        /// </summary>
        [ContextMenu("Force Clear Saved Auth Session")]
        public void ForceClearSavedAuthSession()
        {
            Debug.Log("[FirebaseStorageManager] Force clearing saved auth session.");
            SignOut();
        }

        bool IsAuthFailure(UnityWebRequest request)
        {
            long code = request.responseCode;
            if (code == 401 || code == 403)
                return true;

            string body = request.downloadHandler?.text ?? string.Empty;
            return body.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0
                   || body.IndexOf("INVALID_ID_TOKEN", StringComparison.OrdinalIgnoreCase) >= 0
                   || body.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 && code >= 400;
        }

        void HandleAuthFailure(string context, string serverResponse = null)
        {
            Debug.LogWarning($"[FirebaseStorageManager] Auth failure during {context}. Clearing stale local session.");
            if (!string.IsNullOrEmpty(serverResponse))
                Debug.LogWarning($"[FirebaseStorageManager] Server response: {serverResponse}");

            SignOut();
            OnError?.Invoke("Session expired or unauthorized. Please authenticate again.");
        }

        /// <summary>Same as <see cref="IsAuthenticated"/> — Firebase features require a valid id token.</summary>
        public bool IsLoggedIn() => IsAuthenticated;
        public string GetUserId() => currentUserId;
        public string GetUserEmail() => currentUserEmail;
        public string GetCurrentCode() => currentCode;

        #endregion

        #region Storage Operations

        /// <summary>
        /// Lists converted documents for the current user.
        /// </summary>
        public void ListConvertedDocuments(Action<List<FileInfo>> onSuccess, Action<string> onFailure = null)
        {
            if (!TryEnsureAuthenticated("presentation files"))
            {
                onFailure?.Invoke("Not authenticated");
                return;
            }

            string prefix = $"presentations/{currentUserId}/converted/";
            StartCoroutine(ListFilesCoroutine(prefix, true, onSuccess, onFailure));
        }

        /// <summary>
        /// Lists pages in a document folder.
        /// </summary>
        public void ListDocumentPages(string documentPath, Action<List<FileInfo>> onSuccess, Action<string> onFailure = null)
        {
            if (!TryEnsureAuthenticated("presentation pages"))
            {
                onFailure?.Invoke("Not authenticated");
                return;
            }

            // Robust fix for double slashes
            string cleanPath = documentPath.TrimEnd('/');
            Debug.Log($"[FirebaseStorageManager] ListDocumentPages: Original='{documentPath}', Clean='{cleanPath}'");
            StartCoroutine(ListFilesCoroutine(cleanPath + "/", false, onSuccess, onFailure));
        }

        private IEnumerator ListFilesCoroutine(string prefix, bool foldersOnly, Action<List<FileInfo>> onSuccess, Action<string> onFailure)
        {
            // Firebase Storage REST API for listing
            string url = $"https://firebasestorage.googleapis.com/v0/b/{storageBucket}/o?prefix={UnityWebRequest.EscapeURL(prefix)}&delimiter=/";
            
            Debug.Log($"[FirebaseStorageManager] ListFiles URL: {url}");
            Debug.Log($"[FirebaseStorageManager] Prefix: {prefix}, FoldersOnly: {foldersOnly}");
            Debug.Log($"[FirebaseStorageManager] Has token: {!string.IsNullOrEmpty(idToken)}");
            
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                ApplyAuthorizationHeader(request);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[FirebaseStorageManager] List failed: {request.error}");
                    Debug.LogError($"[FirebaseStorageManager] Response: {request.downloadHandler?.text}");
                    if (IsAuthFailure(request))
                        HandleAuthFailure("list files", request.downloadHandler?.text);
                    onFailure?.Invoke(request.error);
                }
                else
                {
                    string responseText = request.downloadHandler.text;
                    Debug.Log($"[FirebaseStorageManager] Raw response: {responseText}");
                    
                    var result = new List<FileInfo>();
                    var response = JsonUtility.FromJson<StorageListResponse>(responseText);
                    
                    Debug.Log($"[FirebaseStorageManager] Prefixes count: {response.prefixes?.Length ?? 0}");
                    Debug.Log($"[FirebaseStorageManager] Items count: {response.items?.Length ?? 0}");
                    
                    if (foldersOnly && response.prefixes != null)
                    {
                        foreach (var p in response.prefixes)
                        {
                            Debug.Log($"[FirebaseStorageManager] Found prefix: {p}");
                            string[] parts = p.TrimEnd('/').Split('/');
                            string folderName = parts[parts.Length - 1];
                            result.Add(new FileInfo { name = folderName, fullPath = p, isFolder = true });
                        }
                    }
                    else if (response.items != null)
                    {
                        foreach (var item in response.items)
                        {
                            Debug.Log($"[FirebaseStorageManager] Found item: {item.name}");
                            if (item.name.EndsWith(".png"))
                            {
                                string[] parts = item.name.Split('/');
                                string fileName = parts[parts.Length - 1];
                                result.Add(new FileInfo { name = fileName, fullPath = item.name, isFolder = false });
                            }
                        }
                        result.Sort(ComparePresentationFiles);
                    }
                    
                    Debug.Log($"[FirebaseStorageManager] Found {result.Count} items to return");
                    onSuccess?.Invoke(result);
                }
            }
        }

        /// <summary>
        /// Gets the download URL for a file.
        /// </summary>
        public void GetDownloadUrl(string filePath, Action<string> onSuccess, Action<string> onFailure = null)
        {
            if (!TryEnsureAuthenticated("file download"))
            {
                onFailure?.Invoke("Not authenticated");
                return;
            }

            StartCoroutine(GetDownloadUrlCoroutine(filePath, onSuccess, onFailure));
        }

        private IEnumerator GetDownloadUrlCoroutine(string filePath, Action<string> onSuccess, Action<string> onFailure)
        {
            string encodedPath = UnityWebRequest.EscapeURL(filePath);
            string url = $"https://firebasestorage.googleapis.com/v0/b/{storageBucket}/o/{encodedPath}";
            
            Debug.Log($"[FirebaseStorageManager] GetDownloadUrl starting for: {filePath}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 15; // Prevent hanging indefinitely

                ApplyAuthorizationHeader(request);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[FirebaseStorageManager] GetDownloadUrl failed: {request.error}");
                    if (IsAuthFailure(request))
                        HandleAuthFailure("get download url", request.downloadHandler?.text);
                    onFailure?.Invoke(request.error);
                }
                else
                {
                    Debug.Log($"[FirebaseStorageManager] GetDownloadUrl success for: {filePath}");
                    var response = JsonUtility.FromJson<StorageObjectResponse>(request.downloadHandler.text);
                    
                    // Fix: If multiple tokens exist (comma separated), take the first one
                    string token = response.downloadTokens;
                    if (!string.IsNullOrEmpty(token) && token.Contains(","))
                    {
                        token = token.Split(',')[0];
                    }
                    
                    string downloadUrl = $"https://firebasestorage.googleapis.com/v0/b/{storageBucket}/o/{encodedPath}?alt=media&token={token}";
                    onSuccess?.Invoke(downloadUrl);
                }
            }
        }

        /// <summary>
        /// Uploads data to a specific path with proper authentication headers.
        /// </summary>
        public void UploadFile(string storagePath, byte[] data, string mimeType, Action<bool> onComplete)
        {
            if (!TryEnsureAuthenticated("upload"))
            {
                onComplete?.Invoke(false);
                return;
            }

            StartCoroutine(UploadFileCoroutine(storagePath, data, mimeType, onComplete));
        }

        private IEnumerator UploadFileCoroutine(string storagePath, byte[] data, string mimeType, Action<bool> onComplete)
        {
            string url = GetUploadUrl(storagePath);
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogError("[FirebaseStorageManager] Cannot upload: Not authenticated.");
                onComplete?.Invoke(false);
                yield break;
            }

            Debug.Log($"[FirebaseStorageManager] Uploading to: {url}");

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(data);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", mimeType);
                
                ApplyAuthorizationHeader(request);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[FirebaseStorageManager] Upload success: {storagePath}");
                    onComplete?.Invoke(true);
                }
                else
                {
                    Debug.LogError($"[FirebaseStorageManager] Upload failed: {request.error} | {request.downloadHandler.text}");
                    if (IsAuthFailure(request))
                        HandleAuthFailure("upload file", request.downloadHandler?.text);
                    onComplete?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// Sort pages in natural numeric order (e.g. page_2.png before page_10.png).
        /// Falls back to case-insensitive name compare for non-numbered files.
        /// </summary>
        private static int ComparePresentationFiles(FileInfo a, FileInfo b)
        {
            int aPage = ExtractLastNumber(a?.name);
            int bPage = ExtractLastNumber(b?.name);

            bool aHasPage = aPage >= 0;
            bool bHasPage = bPage >= 0;

            if (aHasPage && bHasPage)
            {
                int pageCompare = aPage.CompareTo(bPage);
                if (pageCompare != 0)
                    return pageCompare;
            }
            else if (aHasPage != bHasPage)
            {
                // Numbered page files come first.
                return aHasPage ? -1 : 1;
            }

            return string.Compare(a?.name, b?.name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the last integer found in a filename.
        /// Returns -1 when no integer is present.
        /// </summary>
        private static int ExtractLastNumber(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return -1;

            var matches = Regex.Matches(fileName, @"\d+");
            if (matches.Count == 0)
                return -1;

            string numberText = matches[matches.Count - 1].Value;
            return int.TryParse(numberText, out int value) ? value : -1;
        }

        #endregion

        #region JSON Serialization Classes

        [Serializable]
        private class DeviceCodeEntry
        {
            public long createdAt;
            public long expiresAt;
            public string status;
        }

        [Serializable]
        private class DeviceCodeData
        {
            public string userId;
            public string email;
            public string idToken;
            public string refreshToken; // ADDED: Support for refresh tokens
            public long createdAt;
            public long expiresAt;
        }

        [Serializable]
        private class TokenRefreshResponse
        {
            public string id_token;
            public string refresh_token;
            public string user_id;
            public string project_id;
        }

        [Serializable]
        private class StorageListResponse
        {
            public string[] prefixes;
            public StorageItem[] items;
        }

        [Serializable]
        private class StorageItem
        {
            public string name;
            public string bucket;
        }

        [Serializable]
        private class StorageObjectResponse
        {
            public string name;
            public string downloadTokens;
        }

        #endregion
    }
}
