using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Unity.Netcode;

namespace XRMultiplayer.Presentation
{
    public class PresentationTVManager : NetworkBehaviour
    {
        public enum AspectMode { Auto, Fill, Fit, Stretch }
        
        [System.Serializable]
        public class TVScreen
        {
            public Renderer renderer;
            public AspectMode aspectMode = AspectMode.Auto;
        }

        [Header("Screens")]
        [SerializeField] private List<TVScreen> tvScreens = new List<TVScreen>();
        
        [Header("UI Target")]
        [SerializeField] private RawImage tvRawImage;

        private string currentUrl = "";
        private Coroutine activeDownload = null; // Track active download to prevent race conditions

        // Network Variable for Syncing URL
        // Typically managed by PresentationNetworkManager, but if we merge them:
        // Let's keep this class focused on "Display" and expose methods for the NetworkManager to call.

        public event Action<bool> OnLoadingStateChanged;

        /// <summary>
        /// Returns the transform of the first TV screen (for camera look-at target).
        /// Used by WristViewMonitor to aim the best-seat camera at the actual screen.
        /// </summary>
        public Transform GetFirstScreenTransform()
        {
            if (tvScreens != null && tvScreens.Count > 0 && tvScreens[0].renderer != null)
                return tvScreens[0].renderer.transform;
            return transform;
        }

        /// <summary>
        /// Returns the current presentation texture (from tvRawImage or first screen renderer).
        /// Used by PersonalSnapshotCapture for "Capture Slide" — capture only the presentation, not the room.
        /// </summary>
        public Texture GetCurrentPresentationTexture()
        {
            if (tvRawImage != null && tvRawImage.texture != null)
                return tvRawImage.texture;
            if (tvScreens != null && tvScreens.Count > 0 && tvScreens[0].renderer != null)
            {
                var mat = tvScreens[0].renderer.material;
                if (mat != null && mat.mainTexture != null)
                    return mat.mainTexture;
            }
            return null;
        }

        public void LoadUrl(string url)
        {
            Debug.Log($"[PresentationTVManager] LoadUrl called: {url?.Substring(0, System.Math.Min(50, url?.Length ?? 0))}...");
            
            if (string.IsNullOrEmpty(url)) 
            {
                Debug.Log("[PresentationTVManager] URL is empty, skipping");
                return;
            }
            if (url == currentUrl) 
            {
                Debug.Log("[PresentationTVManager] Same URL, skipping");
                return;
            }

            currentUrl = url;
            
            // Cancel previous download if any
            if (activeDownload != null)
            {
                StopCoroutine(activeDownload);
            }
            
            Debug.Log($"[PresentationTVManager] Starting download. tvScreens.Count={tvScreens?.Count ?? 0}, tvRawImage={tvRawImage != null}");
            activeDownload = StartCoroutine(DownloadAndApply(url));
        }
        
        public void Clear()
        {
            // Cancel any in-progress download
            if (activeDownload != null)
            {
                StopCoroutine(activeDownload);
                activeDownload = null;
            }
            
            // Dispose payload
            CleanUpActiveRequest();
            
            currentUrl = "";
            ApplyTexture(null);
        }
        
        public override void OnDestroy()
        {
            base.OnDestroy();
            
            CleanUpActiveRequest();
            
            // Cleanup current texture to prevent leaks
            if (tvRawImage != null && tvRawImage.texture != null)
            {
                Destroy(tvRawImage.texture);
            }
        }

        private UnityWebRequest activeWebRequest;

        private IEnumerator DownloadAndApply(string url)
        {
            OnLoadingStateChanged?.Invoke(true);
            string downloadUrl = url;
            
            if (tvRawImage != null) tvRawImage.color = Color.gray;
            
            Debug.Log($"[PresentationTVManager] Starting request for: {downloadUrl}");

            // Create request explicitly without 'using' so we can track it
            activeWebRequest = UnityWebRequestTexture.GetTexture(downloadUrl);
            activeWebRequest.timeout = 15;

            // Send
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = activeWebRequest.SendWebRequest();
            }
            catch (Exception e)
            {
                 Debug.LogError($"[PresentationTVManager] Failed to start request: {e.Message}");
                 CleanUpActiveRequest();
                 yield break;
            }

            // Wait with heartbeat
            float timeOut = 15f;
            float timer = 0f;
            
            while (!operation.isDone)
            {
                 yield return null;
                 timer += Time.deltaTime;
                 
                 // Check external abort
                 if (activeWebRequest == null) yield break;

                 if (timer > timeOut)
                 {
                     Debug.LogError("[PresentationTVManager] Manual Timeout reached!");
                     activeWebRequest.Abort();
                     break;
                 }
            }

            // Check cancellation
            if (activeWebRequest == null)
            {
                Debug.Log("[PresentationTVManager] Request cancelled/disposed.");
                OnLoadingStateChanged?.Invoke(false);
                yield break;
            }

            // Check mismatch
            if (url != currentUrl)
            {
                Debug.Log($"[PresentationTVManager] URL changed during download, discarding.");
                CleanUpActiveRequest();
                OnLoadingStateChanged?.Invoke(false);
                yield break;
            }

            // Process Result
            if (activeWebRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[PresentationTVManager] Error downloading texture: {activeWebRequest.error} ({activeWebRequest.responseCode})");
                if (tvRawImage != null) tvRawImage.color = new Color(1f, 0.5f, 0.5f);
            }
            else
            {
                Debug.Log("[PresentationTVManager] Download success, applying texture...");
                try 
                {
                    Texture2D texture = DownloadHandlerTexture.GetContent(activeWebRequest);
                    ApplyTexture(texture);
                    if (tvRawImage != null) tvRawImage.color = Color.white;
                }
                catch (Exception ex) 
                {
                    Debug.LogError($"[PresentationTVManager] Error applying texture: {ex.Message}");
                }
            }
            
            CleanUpActiveRequest();
            OnLoadingStateChanged?.Invoke(false);
        }

        private void CleanUpActiveRequest()
        {
            if (activeWebRequest != null)
            {
                activeWebRequest.Dispose();
                activeWebRequest = null;
            }
        }



        private void ApplyTexture(Texture2D texture)
        {
            Debug.Log($"[PresentationTVManager] ApplyTexture: texture={texture != null}, size={texture?.width}x{texture?.height}, tvScreens={tvScreens?.Count ?? 0}, tvRawImage={tvRawImage != null}");
            
            // Configure for sharp display (text, diagrams) - avoids blur on wrist view and main screen
            if (texture != null)
            {
                texture.filterMode = FilterMode.Point;
                texture.anisoLevel = 16;
            }
            
            // Cleanup previous texture if it exists and is a generated Texture2D
            if (tvRawImage != null && tvRawImage.texture != null && tvRawImage.texture is Texture2D oldTex)
            {
                Destroy(oldTex);
            }

            if (tvRawImage != null)
            {
                tvRawImage.texture = texture;
                if (texture != null)
                {
                     AspectRatioFitter fitter = tvRawImage.GetComponent<AspectRatioFitter>();
                     if (fitter != null) fitter.aspectRatio = (float)texture.width / texture.height;
                }
            }

            foreach (var screen in tvScreens)
            {
                if (screen.renderer != null)
                {
                    Material mat = screen.renderer.material;
                    
                    if (texture != null)
                    {
                        // Standard shader
                        mat.mainTexture = texture;
                        
                        // URP Lit shader uses _BaseMap instead of _MainTex
                        if (mat.HasProperty("_BaseMap"))
                        {
                            mat.SetTexture("_BaseMap", texture);
                        }
                        
                        // Reset tiling and offset to show full image (fixes zoom/crop issue)
                        mat.mainTextureScale = Vector2.one;  // Tiling = (1, 1)
                        mat.mainTextureOffset = Vector2.zero; // Offset = (0, 0)
                        
                        // For URP, also reset _BaseMap_ST if it exists
                        if (mat.HasProperty("_BaseMap_ST"))
                        {
                            mat.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0)); // (tilingX, tilingY, offsetX, offsetY)
                        }
                        
                        // Emission for "glowing TV" effect
                        mat.EnableKeyword("_EMISSION");
                        if (mat.HasProperty("_EmissionMap"))
                        {
                            mat.SetTexture("_EmissionMap", texture);
                            mat.SetColor("_EmissionColor", Color.white);
                        }
                        
                        Debug.Log($"[PresentationTVManager] Applied texture to {screen.renderer.name}, tiling reset to (1,1)");
                    }
                    else
                    {
                        mat.mainTexture = null;
                        if (mat.HasProperty("_BaseMap"))
                            mat.SetTexture("_BaseMap", null);
                        if (mat.HasProperty("_EmissionMap"))
                            mat.SetTexture("_EmissionMap", null);
                    }
                }
            }
        }
    }
}
