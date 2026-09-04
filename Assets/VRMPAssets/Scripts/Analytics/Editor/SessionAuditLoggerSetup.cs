#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using XRMultiplayer.Presentation;

namespace XRMultiplayer
{
    /// <summary>
    /// Editor utilities for setting up the SessionAuditLogger system.
    /// </summary>
    public static class SessionAuditLoggerSetup
    {
        [MenuItem("VRMP/Setup Session Audit Logger")]
        public static void SetupAuditLogger()
        {
            // Find or create the SessionAuditLogger
            var existingLogger = Object.FindFirstObjectByType<SessionAuditLogger>();
            if (existingLogger != null)
            {
                Debug.Log("[SessionAuditLogger] Already set up!");
                Selection.activeGameObject = existingLogger.gameObject;
                return;
            }

            // Find the NetworkManager to attach the logger to
            var networkManager = Object.FindFirstObjectByType<NetworkManager>();
            GameObject targetObject;

            if (networkManager != null)
            {
                targetObject = networkManager.gameObject;
                Debug.Log("[SessionAuditLogger] Adding to NetworkManager GameObject");
            }
            else
            {
                // Create a new persistent object
                targetObject = new GameObject("SessionAuditLogger");
                Debug.Log("[SessionAuditLogger] Created new SessionAuditLogger GameObject");
            }

            // Add the SessionAuditLogger
            var logger = targetObject.AddComponent<SessionAuditLogger>();
            EditorUtility.SetDirty(targetObject);

            // Check FirebaseStorageManager exists
            var firebaseManager = Object.FindFirstObjectByType<FirebaseStorageManager>();
            if (firebaseManager != null)
            {
                Debug.Log("[SessionAuditLogger] FirebaseStorageManager found - uploads enabled");
            }
            else
            {
                Debug.LogWarning("[SessionAuditLogger] FirebaseStorageManager not found - upload feature will be disabled.");
            }

            Selection.activeGameObject = targetObject;
            Debug.Log("<color=#00FF00>[SessionAuditLogger] Setup complete!</color>\n" +
                     "The audit logger will automatically:\n" +
                     "- Log player joins/leaves with device type\n" +
                     "- Log mute toggles\n" +
                     "- Log speaking contributions (>5s)\n" +
                     "- Log file loads and slide changes\n" +
                     "- Log head nods and shakes\n" +
                     "- Log attention lapses\n" +
                     "- Save CSV to persistentDataPath on session end\n" +
                     "- Upload to Firebase Storage if authenticated");
        }

        [MenuItem("VRMP/Open Audit Logs Folder")]
        public static void OpenAuditLogsFolder()
        {
            string path = Application.persistentDataPath;
            
#if UNITY_EDITOR_WIN
            path = path.Replace("/", "\\");
            System.Diagnostics.Process.Start("explorer.exe", path);
#elif UNITY_EDITOR_OSX
            System.Diagnostics.Process.Start("open", path);
#else
            Debug.Log($"Audit logs are saved to: {path}");
#endif
        }

        [MenuItem("VRMP/Validate Audit Logger Setup")]
        public static void ValidateSetup()
        {
            bool isValid = true;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<color=#FFD700>[SessionAuditLogger Validation]</color>");
            sb.AppendLine("");

            // Check SessionAuditLogger
            var logger = Object.FindFirstObjectByType<SessionAuditLogger>();
            if (logger != null)
            {
                sb.AppendLine("✅ SessionAuditLogger found");
            }
            else
            {
                sb.AppendLine("❌ SessionAuditLogger NOT found - run Setup");
                isValid = false;
            }

            // Check FirebaseStorageManager
            var firebaseManager = Object.FindFirstObjectByType<FirebaseStorageManager>();
            if (firebaseManager != null)
            {
                sb.AppendLine("✅ FirebaseStorageManager found");
            }
            else
            {
                sb.AppendLine("⚠️ FirebaseStorageManager NOT found - upload disabled");
            }

            // Check XRINetworkGameManager
            var networkManager = Object.FindFirstObjectByType<XRINetworkGameManager>();
            if (networkManager != null)
            {
                sb.AppendLine("✅ XRINetworkGameManager found");
            }
            else
            {
                sb.AppendLine("❌ XRINetworkGameManager NOT found");
                isValid = false;
            }

            // Check VoiceChatManager
            var voiceManager = Object.FindFirstObjectByType<VoiceChatManager>();
            if (voiceManager != null)
            {
                sb.AppendLine("✅ VoiceChatManager found");
            }
            else
            {
                sb.AppendLine("⚠️ VoiceChatManager NOT found - mute logging disabled");
            }

            // Check PresentationNetworkManager
            var presentationNetwork = Object.FindFirstObjectByType<PresentationNetworkManager>();
            if (presentationNetwork != null)
            {
                sb.AppendLine("✅ PresentationNetworkManager found");
            }
            else
            {
                sb.AppendLine("⚠️ PresentationNetworkManager NOT found - slide logging disabled");
            }

            // Check AnalyticsManager
            var analytics = Object.FindFirstObjectByType<AnalyticsManager>();
            if (analytics != null)
            {
                sb.AppendLine("✅ AnalyticsManager found");
            }
            else
            {
                sb.AppendLine("⚠️ AnalyticsManager NOT found - attention tracking disabled");
            }

            sb.AppendLine("");
            if (isValid)
            {
                sb.AppendLine("<color=#00FF00>Setup is valid! Ready to record.</color>");
            }
            else
            {
                sb.AppendLine("<color=#FF0000>Setup incomplete. Run VRMP > Setup Session Audit Logger</color>");
            }

            Debug.Log(sb.ToString());
        }
    }
}
#endif
