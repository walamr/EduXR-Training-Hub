using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Simple transcript panel that displays transcription text.
    /// </summary>
    public class TranscriptPanel : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int maxLines = 50;
        [SerializeField] private bool autoScroll = true;
        [SerializeField] private Button recordButton; // Reference to the record button to hide for clients

        private TMP_Text transcriptText;
        private TMP_Text statusText;
        private ScrollRect scrollRect;

        // One container GameObject per transcript entry. Each language direction gets its
        // own TMP component(s), because TextMeshPro's RTL mode is per-component and a single
        // shared text block cannot mix LTR and RTL lines correctly.
        private readonly List<GameObject> entryObjects = new List<GameObject>();
        private bool entrySpacingApplied;

        private void Awake()
        {
            // Try to find scroll rect in parents
            scrollRect = GetComponentInParent<ScrollRect>();
            if (scrollRect == null)
            {
                scrollRect = GetComponentInChildren<ScrollRect>();
            }
        }

        private void Start()
        {
            // Try to load the Arabic font from the known path
            // We look in Resources or via AssetDatabase if in Editor, but for runtime reliability we should use the one we downloaded.
            // Since we can't easily use AssetDatabase at runtime, we will try to find it via Resources if the user moved it, 
            // BUT for now, we will try to check if the user assigned it or we can find it.
            
            // BETTER APPROACH: Check if the current font has the fallback. If not, warn or try to fix.
            if (transcriptText != null && transcriptText.font != null)
            {
                // We'll rely on the Editor script having set it up, but if it failed, we need to be careful.
                // The logs showed the font was missing from fallbacks.
                Debug.Log($"[TranscriptPanel] Current Font: {transcriptText.font.name}");
                if (transcriptText.font.fallbackFontAssetTable == null || transcriptText.font.fallbackFontAssetTable.Count == 0)
                {
                    Debug.LogWarning("[TranscriptPanel] No Fallback fonts found! Arabic may not display.");
                }
            }
            if (recordButton != null)
            {
                // We allow button if:
                // 1. NetworkManager is null (Offline)
                // 2. We are not connected (Offline)
                // 3. We are the Server/Host
                UpdateRecordButtonVisibility();
                
                if (XRINetworkGameManager.Instance != null)
                {
                    XRINetworkGameManager.Instance.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
                }
            }
        }

        private void OnDestroy()
        {
            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            }
        }

        private void OnSessionOwnerPromoted(ulong newOwnerId)
        {
            // If we are promoted to Host, show the button!
            UpdateRecordButtonVisibility();
        }

        private void UpdateRecordButtonVisibility()
        {
            if (recordButton == null) return;
            
            bool showButton = true;
            
            // IF online AND client AND not server/host -> Hide
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            {
                showButton = false;
            }

            if (recordButton.gameObject.activeSelf != showButton)
            {
                recordButton.gameObject.SetActive(showButton);
                Debug.Log($"[TranscriptPanel] Host Migration or Start: Record Button is now {(showButton ? "VISIBLE" : "HIDDEN")}");
            }
        }

        /// <summary>
        /// Set the transcript text component reference.
        /// </summary>
        public void SetTranscriptText(TMP_Text text)
        {
            transcriptText = text;
            Debug.Log($"[TranscriptPanel] TranscriptText set: {(text != null ? text.name : "null")}");
            
            // Attempt to fix font fallback at runtime if easy
            if (transcriptText != null && transcriptText.font != null)
            {
                 // This requires the font to be in Resources to load by string at runtime, which it might not be.
                 // So we will assume the user followed the Editor steps, but we will double check RTL settings.
                 transcriptText.isRightToLeftText = false; // DISABLE this to fix "enoyreve alleH"
            }
        }

        /// <summary>
        /// Set the status text component reference.
        /// </summary>
        public void SetStatusText(TMP_Text text)
        {
            statusText = text;
        }

        /// <summary>
        /// Set scroll rect reference.
        /// </summary>
        public void SetScrollRect(ScrollRect rect)
        {
            scrollRect = rect;
        }

        /// <summary>
        /// Add a new transcription entry.
        /// </summary>
        public void AddEntry(TranscriptionEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.text))
            {
                Debug.Log("[TranscriptPanel] Ignoring empty entry");
                return;
            }

            Debug.Log($"[TranscriptPanel] Adding: {entry.text}");

            if (transcriptText == null)
            {
                Debug.LogError("[TranscriptPanel] transcriptText is null! Cannot add entry.");
                return;
            }

            Transform contentParent = transcriptText.transform.parent;

            // First real entry: hide the "Tap REC to start..." placeholder and give the
            // list some breathing room between entries.
            if (entryObjects.Count == 0)
            {
                transcriptText.gameObject.SetActive(false);

                if (!entrySpacingApplied)
                {
                    var contentLayout = contentParent.GetComponent<VerticalLayoutGroup>();
                    if (contentLayout != null) contentLayout.spacing = 18f;
                    entrySpacingApplied = true;
                }
            }

            // Rendering per language:
            //  - English: single line, LTR, left-aligned:  [time] Speaker: text
            //  - Arabic:  single line, LTR component, right-aligned, ArabicFixer shaping
            //  - Hebrew:  metadata line + body line. The body is its OWN TMP component with
            //    native RTL mode (isRightToLeftText = true): the Hebrew string stays in
            //    normal logical order, never reversed, and wraps correctly.
            string time = entry.timestamp.ToLocalTime().ToString("HH:mm:ss");
            var container = CreateEntryContainer(contentParent);

            if (RtlTextUtility.ContainsArabic(entry.text))
            {
                CreateLine(container.transform,
                    $"<color=#4FC3F7>{entry.speakerName}:</color> {ArabicFixer.Fix(entry.text)} <color=#888888>[{time}]</color>",
                    TextAlignmentOptions.TopRight, rtl: false, transcriptText.fontSize);
            }
            else if (RtlTextUtility.ContainsHebrew(entry.text))
            {
                // Metadata stays on its own LTR line, so the timestamp/speaker can never
                // jump into the middle of the Hebrew sentence.
                CreateLine(container.transform,
                    $"<color=#888888>[{time}]</color> <color=#4FC3F7>{entry.speakerName}:</color>",
                    TextAlignmentOptions.TopRight, rtl: false, transcriptText.fontSize * 0.8f);

                CreateLine(container.transform,
                    RtlTextUtility.PrepareForRtlRendering(entry.text),
                    TextAlignmentOptions.TopRight, rtl: true, transcriptText.fontSize);
            }
            else
            {
                CreateLine(container.transform,
                    $"<color=#888888>[{time}]</color> <color=#4FC3F7>{entry.speakerName}:</color> {entry.text}",
                    TextAlignmentOptions.TopLeft, rtl: false, transcriptText.fontSize);
            }

            entryObjects.Add(container);

            // Remove old entries
            while (entryObjects.Count > maxLines)
            {
                Destroy(entryObjects[0]);
                entryObjects.RemoveAt(0);
            }

            Debug.Log($"[TranscriptPanel] Appended text (entries: {entryObjects.Count})");

            // Auto-scroll
            if (autoScroll && scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        /// <summary>
        /// Creates the per-entry container that stacks its line(s) vertically.
        /// </summary>
        private GameObject CreateEntryContainer(Transform parent)
        {
            var container = new GameObject("Entry");
            container.transform.SetParent(parent, false);
            container.layer = transcriptText.gameObject.layer;
            container.AddComponent<RectTransform>();

            var layout = container.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            return container;
        }

        /// <summary>
        /// Creates a single text line, copying the visual style from the template text.
        /// </summary>
        private TMP_Text CreateLine(Transform parent, string text, TextAlignmentOptions alignment, bool rtl, float fontSize)
        {
            var go = new GameObject(rtl ? "Line (RTL)" : "Line");
            go.transform.SetParent(parent, false);
            go.layer = transcriptText.gameObject.layer;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (transcriptText.font != null) tmp.font = transcriptText.font;

            // Hebrew body lines use the Hebrew font DIRECTLY (not via fallback resolution),
            // so glyphs always come from the correct font - in Editor and on Quest.
            if (rtl)
            {
                var hebrewFont = RtlTextUtility.GetHebrewFont();
                if (hebrewFont != null) tmp.font = hebrewFont;
            }

            tmp.fontSize = fontSize;
            tmp.color = transcriptText.color;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = true;
            tmp.raycastTarget = false;
            tmp.isRightToLeftText = rtl;
            tmp.alignment = alignment;
            tmp.text = text;
            return tmp;
        }

        /// <summary>
        /// Update status display (recording on/off shortcut).
        /// </summary>
        public void UpdateStatus(bool recording)
        {
            SetStatus(recording ? "Recording..." : "Ready",
                      recording ? new Color(1f, 0.3f, 0.3f) : Color.white);
        }

        /// <summary>
        /// Set the status label to an explicit message and color.
        /// Used for states like "Stopped", "Mic not available", "Error".
        /// </summary>
        public void SetStatus(string message, Color color)
        {
            if (statusText != null)
            {
                statusText.text = message;
                statusText.color = color;
            }
        }

        /// <summary>
        /// True when at least one transcription line has been captured.
        /// </summary>
        public bool HasContent => entryObjects.Count > 0;

        /// <summary>
        /// Clear all entries and restore the placeholder.
        /// </summary>
        public void Clear()
        {
            foreach (var obj in entryObjects)
            {
                if (obj != null) Destroy(obj);
            }
            entryObjects.Clear();

            if (transcriptText != null)
            {
                transcriptText.text = "<color=#666666>Tap REC to start transcription...</color>";
                transcriptText.gameObject.SetActive(true);
            }
        }

        // Legacy compatibility methods
        public void SetContentParent(Transform content) { }
        public void SetRecordButton(Button button) 
        {
            recordButton = button;
            UpdateRecordButtonVisibility();
        }

    }
}
