using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace XRMultiplayer.Recording
{
    /// <summary>
    /// UI Panel for Holographic Meeting Recording.
    /// Dynamically refreshes file list and shows recording status.
    /// </summary>
    public class RecordingPanel : MonoBehaviour
    {
        [Header("References")]
        public Transform fileListContainer;
        public GameObject fileButtonPrefab;
        public TextMeshProUGUI statusText;
        public Image recordButtonImage;
        public Image statusDotImage;

        [Header("Colors")]
        public Color idleColor = Color.white;
        public Color recordingColor = Color.red;
        public Color selectedColor = new Color(0.2f, 0.6f, 1f, 1f);
        public Color statusGreenColor = new Color(0.13f, 0.8f, 0.4f, 1f);

        [Header("Settings")]
        [SerializeField] private string m_RecordingsFolder = "MeetingRecordings";

        private MeetingRecorder m_Recorder;
        private MeetingPlaybackManager m_Playback;
        private string m_SelectedFile;
        private float m_RecordingStartTime;
        private bool m_WasRecording;

        private void OnEnable()
        {
            // Try to find managers - they may not exist on non-hosts
            m_Recorder = MeetingRecorder.Instance;
            m_Playback = MeetingPlaybackManager.Instance;
            
            // Always refresh file list from disk
            RefreshFileList();
        }

        private void Update()
        {
            UpdateStatusDisplay();
        }

        private void UpdateStatusDisplay()
        {
            if (statusText == null) return;

            // Check if we have a recorder and it's recording
            if (m_Recorder != null && m_Recorder.IsRecording)
            {
                if (!m_WasRecording)
                {
                    m_RecordingStartTime = Time.time;
                    m_WasRecording = true;
                }

                float elapsed = Time.time - m_RecordingStartTime;
                int minutes = Mathf.FloorToInt(elapsed / 60f);
                int seconds = Mathf.FloorToInt(elapsed % 60f);
                
                // Blink effect with time display
                bool blink = Mathf.FloorToInt(Time.time * 2) % 2 == 0;
                statusText.text = blink ? $"REC {minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
                statusText.color = recordingColor;
                
                if (recordButtonImage != null) 
                    recordButtonImage.color = recordingColor;
                    
                // Update status dot to red when recording
                if (statusDotImage != null)
                    statusDotImage.color = recordingColor;
            }
            else
            {
                m_WasRecording = false;
                
                statusText.text = "Ready";
                statusText.color = statusGreenColor;
                
                if (recordButtonImage != null) 
                    recordButtonImage.color = idleColor;
                    
                // Update status dot to green when ready
                if (statusDotImage != null)
                    statusDotImage.color = statusGreenColor;
            }
        }

        public void ToggleRecording()
        {
            // Retry getting instance if null
            if (m_Recorder == null) m_Recorder = MeetingRecorder.Instance;
            
            if (m_Recorder == null)
            {
                ShowTemporaryMessage("No Recorder Found", Color.red);
                return;
            }
            
            if (m_Recorder.IsRecording)
            {
                m_Recorder.StopRecording();
                RefreshFileList();
                ShowTemporaryMessage("Recording Saved!", Color.green);
            }
            else
            {
                bool started = m_Recorder.StartRecording();
                if (!started)
                {
                    ShowTemporaryMessage("HOST ONLY", Color.red);
                }
            }
        }

        public void PlaySelected()
        {
            if (string.IsNullOrEmpty(m_SelectedFile))
            {
                ShowTemporaryMessage("Select a recording first", Color.yellow);
                return;
            }
            
            // Retry getting instance if null
            if (m_Playback == null) m_Playback = MeetingPlaybackManager.Instance;
            
            if (m_Playback == null)
            {
                ShowTemporaryMessage("No Playback Manager", Color.red);
                return;
            }
            
            // Check if playback is already active
            if (m_Playback.IsPlaybackActive)
            {
                ShowTemporaryMessage("Playback in progress", Color.yellow);
                return;
            }
            
            m_Playback.PlayRecording(m_SelectedFile);
            ShowTemporaryMessage($"Playing: {Path.GetFileNameWithoutExtension(m_SelectedFile)}", Color.green);
        }

        public void StopPlayback()
        {
            if (m_Playback == null) m_Playback = MeetingPlaybackManager.Instance;
            
            if (m_Playback != null)
            {
                m_Playback.StopPlayback();
                // StopPlayback will show warning if not the initiator
            }
            
            m_SelectedFile = null;
            ShowTemporaryMessage("Stopped", Color.white);
        }

        /// <summary>
        /// Increases playback scale (zoom in on miniature scene).
        /// </summary>
        public void ScaleUp()
        {
            if (m_Playback == null) m_Playback = MeetingPlaybackManager.Instance;
            if (m_Playback != null)
            {
                m_Playback.ScaleUp();
                ShowTemporaryMessage($"Scale: {m_Playback.CurrentScale:F2}", Color.cyan);
            }
        }

        /// <summary>
        /// Decreases playback scale (zoom out on miniature scene).
        /// </summary>
        public void ScaleDown()
        {
            if (m_Playback == null) m_Playback = MeetingPlaybackManager.Instance;
            if (m_Playback != null)
            {
                m_Playback.ScaleDown();
                ShowTemporaryMessage($"Scale: {m_Playback.CurrentScale:F2}", Color.cyan);
            }
        }

        public void RefreshFileList()
        {
            if (fileListContainer == null || fileButtonPrefab == null) return;

            // Clear existing buttons
            foreach (Transform child in fileListContainer)
            {
                if (child.gameObject.activeSelf) // Don't destroy the template
                    Destroy(child.gameObject);
            }

            // Get files directly from disk - works for any client
            string dirPath = Path.Combine(Application.persistentDataPath, m_RecordingsFolder);
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
                return;
            }

            string[] files = Directory.GetFiles(dirPath, "*.json");
            
            // Sort by date (newest first)
            System.Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));

            foreach (string path in files)
            {
                string fileName = Path.GetFileName(path);
                CreateFileButton(fileName);
            }
        }

        private void CreateFileButton(string fileName)
        {
            GameObject btnObj = Instantiate(fileButtonPrefab, fileListContainer);
            btnObj.SetActive(true);
            btnObj.name = fileName;
            
            // Setup Text - show friendly name without extension
            TextMeshProUGUI txt = btnObj.GetComponentInChildren<TextMeshProUGUI>();
            if (txt != null)
            {
                string displayName = Path.GetFileNameWithoutExtension(fileName);
                // Format: "Meeting_2025-12-31_18-30-00" -> "Dec 31, 18:30"
                txt.text = FormatRecordingName(displayName);
            }

            // Setup Click Handler
            Button btn = btnObj.GetComponent<Button>();
            if (btn != null)
            {
                string capturedFileName = fileName;
                btn.onClick.AddListener(() => SelectFile(capturedFileName));
            }
        }

        private string FormatRecordingName(string rawName)
        {
            // Try to parse "Meeting_yyyy-MM-dd_HH-mm-ss"
            if (rawName.StartsWith("Meeting_") && rawName.Length >= 25)
            {
                try
                {
                    string datePart = rawName.Substring(8, 10); // "2025-12-31"
                    string timePart = rawName.Substring(19, 8); // "18-30-00"
                    
                    string[] dateParts = datePart.Split('-');
                    string[] timeParts = timePart.Split('-');
                    
                    string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
                    int monthIndex = int.Parse(dateParts[1]) - 1;
                    
                    return $"{months[monthIndex]} {dateParts[2]}, {timeParts[0]}:{timeParts[1]}";
                }
                catch
                {
                    return rawName;
                }
            }
            return rawName;
        }

        private void SelectFile(string fileName)
        {
            m_SelectedFile = fileName;
            
            // Update button visuals - highlight selected
            foreach (Transform child in fileListContainer)
            {
                if (!child.gameObject.activeSelf) continue;
                
                Image img = child.GetComponent<Image>();
                if (img != null)
                {
                    img.color = (child.name == fileName) ? selectedColor : Color.gray;
                }
            }
        }

        private void ShowTemporaryMessage(string message, Color color)
        {
            StopAllCoroutines();
            StartCoroutine(ShowMessageCoroutine(message, color));
        }

        private IEnumerator ShowMessageCoroutine(string message, Color color)
        {
            if (statusText == null) yield break;
            
            statusText.text = message;
            statusText.color = color;
            
            // Temporarily disable Update from overwriting
            enabled = false;
            yield return new WaitForSeconds(2f);
            enabled = true;
        }
    }
}
