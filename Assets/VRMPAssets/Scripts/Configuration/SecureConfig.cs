using System;
using System.IO;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Safe configuration provider that loads API keys from local gitignored configuration
    /// or environment variables, avoiding hardcoded secrets in version control.
    /// </summary>
    public static class SecureConfig
    {
        [Serializable]
        private class SecretsData
        {
            public string geminiApiKey = "";
            public string firebaseApiKey = "";
        }

        private static bool isLoaded = false;
        private static string cachedGeminiKey = "";
        private static string cachedFirebaseKey = "";

        public static string GeminiApiKey
        {
            get
            {
                EnsureLoaded();
                return cachedGeminiKey;
            }
        }

        public static string FirebaseApiKey
        {
            get
            {
                EnsureLoaded();
                return cachedFirebaseKey;
            }
        }

        private static void EnsureLoaded()
        {
            if (isLoaded) return;
            isLoaded = true;

            // 1. Check environment variables
            string envGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrEmpty(envGemini)) cachedGeminiKey = envGemini;

            string envFirebase = Environment.GetEnvironmentVariable("FIREBASE_API_KEY");
            if (!string.IsNullOrEmpty(envFirebase)) cachedFirebaseKey = envFirebase;

            // 2. Load from StreamingAssets/secrets.json if exists and keys are not yet set
            try
            {
                string secretsPath = Path.Combine(Application.streamingAssetsPath, "secrets.json");
                if (File.Exists(secretsPath))
                {
                    string json = File.ReadAllText(secretsPath);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var data = JsonUtility.FromJson<SecretsData>(json);
                        if (data != null)
                        {
                            if (string.IsNullOrEmpty(cachedGeminiKey) && !string.IsNullOrEmpty(data.geminiApiKey))
                            {
                                cachedGeminiKey = data.geminiApiKey;
                            }
                            if (string.IsNullOrEmpty(cachedFirebaseKey) && !string.IsNullOrEmpty(data.firebaseApiKey))
                            {
                                cachedFirebaseKey = data.firebaseApiKey;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SecureConfig] Note: Could not read secrets.json: {ex.Message}");
            }
        }
    }
}
