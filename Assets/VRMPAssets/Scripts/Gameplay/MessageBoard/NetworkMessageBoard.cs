using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using System;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;
using Unity.Services.Multiplayer;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace XRMultiplayer
{
    /// <summary>
    /// Represents a message board that allows players to submit and display messages in a networked environment.
    /// </summary>
    public class NetworkMessageBoard : NetworkBehaviour
    {
        /// <summary>
        /// The prefab for the message text.
        /// </summary>
        [SerializeField] GameObject m_MessagePrefab;

        /// <summary>
        /// The transform that contains the viewport for the messages.
        /// </summary>
        [SerializeField] Transform m_ContentViewport;

        /// <summary>
        /// The maximum number of messages that can be displayed.
        /// </summary>
        [SerializeField] int m_MaxMessageCount = 100;

        /// <summary>
        /// The maximum number of characters that can be displayed in a message.
        /// </summary>
        [SerializeField] int m_MaxCharacterCount = 256;

        /// <summary>
        /// A chat message tagged with the room "context" it belongs to (0 = main room, otherwise a
        /// private room id). Isolation is by display-filtering: clients only render messages whose
        /// RoomId matches their current context.
        /// </summary>
        public struct ChatMessage : INetworkSerializable, IEquatable<ChatMessage>
        {
            public ulong RoomId;
            public FixedString512Bytes Text;

            public ChatMessage(ulong roomId, FixedString512Bytes text)
            {
                RoomId = roomId;
                Text = text;
            }

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref RoomId);
                serializer.SerializeValue(ref Text);
            }

            public bool Equals(ChatMessage other) => RoomId == other.RoomId && Text.Equals(other.Text);
        }

        /// <summary>
        /// The list of current messages (room-tagged).
        /// </summary>
        NetworkList<ChatMessage> messageList;

        bool m_PrivateRoomSubscribed;

        /// <summary>The local player's current room context (0 = main room).</summary>
        static ulong LocalContext()
        {
            var svc = XRMultiplayer.PrivateRoom.PrivateRoomService.Instance;
            return svc != null ? svc.CurrentPrivateRoomId : 0;
        }

        /// <summary>
        /// The meeting notes text object (displayed at the top).
        /// </summary>
        private GameObject m_MeetingNotesObject;

        /// <inheritdoc/>
        void Start()
        {
            XRINetworkGameManager.Connected.Subscribe(ConnectedToNetwork);
            messageList = new NetworkList<ChatMessage>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        }

        /// <summary>
        /// Called when the network connection status changes.
        /// </summary>
        /// <param name="connected">Indicates whether the player is connected to the network.</param>
        void ConnectedToNetwork(bool connected)
        {
            if (!connected)
            {
                if (m_MeetingNotesObject != null)
                {
                    Destroy(m_MeetingNotesObject);
                    m_MeetingNotesObject = null;
                }
                if (m_ContentViewport != null)
                {
                    foreach (Transform t in m_ContentViewport)
                    {
                        Destroy(t.gameObject);
                    }
                }
            }
            else
            {
                // Display meeting notes if available - try immediately and after a delay
                DisplayMeetingNotes();
                StartCoroutine(DelayedDisplayMeetingNotes());
            }
        }

        /// <summary>
        /// Displays meeting notes after short delays to ensure session properties are loaded.
        /// Uses multiple retry attempts with increasing delays.
        /// </summary>
        IEnumerator DelayedDisplayMeetingNotes()
        {
            // Multiple retry attempts with increasing delays
            float[] delays = { 1.0f, 2.0f, 3.0f, 5.0f };
            
            foreach (float delay in delays)
            {
                yield return new WaitForSeconds(delay);
                
                // Check if we already have notes displayed
                if (m_MeetingNotesObject != null)
                {
                    Debug.Log("[NetworkMessageBoard] Meeting notes already displayed, stopping retry");
                    yield break;
                }
                
                DisplayMeetingNotes();
                
                // If notes were displayed, stop retrying
                if (m_MeetingNotesObject != null)
                {
                    Debug.Log("[NetworkMessageBoard] Meeting notes displayed successfully after delay");
                    yield break;
                }
                
                Debug.Log($"[NetworkMessageBoard] Retry after {delay}s - notes still not available");
            }
        }

        /// <summary>
        /// Displays meeting notes at the top of the message board if available.
        /// </summary>
        void DisplayMeetingNotes()
        {
            // Clean up existing notes
            if (m_MeetingNotesObject != null)
            {
                Destroy(m_MeetingNotesObject);
                m_MeetingNotesObject = null;
            }

            // Try to get meeting notes from session properties
            string notes = GetMeetingNotes();
            Debug.Log($"[NetworkMessageBoard] Getting meeting notes: '{notes}' (length: {notes.Length})");

            if (!string.IsNullOrEmpty(notes))
            {
                Debug.Log($"[NetworkMessageBoard] Displaying meeting notes: {notes}");
                m_MeetingNotesObject = Instantiate(m_MessagePrefab, m_ContentViewport);
                m_MeetingNotesObject.transform.SetAsFirstSibling(); // Put at the top
                string notesText = $"<b>📝 Meeting Notes:</b><br><br>{notes}";
                m_MeetingNotesObject.GetComponent<MessageText>().SetMessage(notesText, "");
            }
            else
            {
                Debug.Log("[NetworkMessageBoard] No meeting notes to display");
            }
        }

        /// <summary>
        /// Gets meeting notes from the current session properties.
        /// </summary>
        /// <returns>The meeting notes, or empty string if not available.</returns>
        string GetMeetingNotes()
        {
            try
            {
                var sessionManager = FindFirstObjectByType<XRMultiplayer.SessionManager>();
                if (sessionManager != null)
                {
                    return sessionManager.GetMeetingNotes();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[NetworkMessageBoard] Error getting meeting notes: {e.Message}");
            }
            return "";
        }

        // Called from XRIKeyboardDisplay
        public void ToggleKeyboardOpen(bool toggle)
        {
            GlobalNonNativeKeyboard.instance.keyboard.closeOnSubmit = !toggle;
        }

        /// <inheritdoc/>
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsOwner)
            {
                messageList.Clear();
            }
            ulong ctx = LocalContext();
            foreach (ChatMessage message in messageList)
            {
                if (message.RoomId == ctx)
                    CreateText(message.Text.ToString());
            }

            StartCoroutine(SubscribeToPrivateRoomWhenReady());
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (m_PrivateRoomSubscribed && XRMultiplayer.PrivateRoom.PrivateRoomService.Instance != null)
            {
                XRMultiplayer.PrivateRoom.PrivateRoomService.Instance.OnJoinedPrivateRoom -= OnLocalRoomChanged;
                XRMultiplayer.PrivateRoom.PrivateRoomService.Instance.OnLeftPrivateRoom -= RebuildBoard;
            }
            m_PrivateRoomSubscribed = false;
        }

        // Rebuild the board when the local player crosses a room boundary, so stale out-of-context
        // bubbles are cleared and the now-relevant ones are shown.
        IEnumerator SubscribeToPrivateRoomWhenReady()
        {
            float timeout = 5f;
            while (XRMultiplayer.PrivateRoom.PrivateRoomService.Instance == null && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            var svc = XRMultiplayer.PrivateRoom.PrivateRoomService.Instance;
            if (svc == null || m_PrivateRoomSubscribed) yield break;

            svc.OnJoinedPrivateRoom += OnLocalRoomChanged;
            svc.OnLeftPrivateRoom += RebuildBoard;
            m_PrivateRoomSubscribed = true;
        }

        void OnLocalRoomChanged(ulong _) => RebuildBoard();

        /// <summary>Clears and re-creates the visible messages for the local player's current context.</summary>
        void RebuildBoard()
        {
            if (m_ContentViewport == null) return;

            if (m_MeetingNotesObject != null)
            {
                Destroy(m_MeetingNotesObject);
                m_MeetingNotesObject = null;
            }
            foreach (Transform t in m_ContentViewport)
                Destroy(t.gameObject);

            ulong ctx = LocalContext();
            if (messageList != null)
            {
                foreach (ChatMessage message in messageList)
                    if (message.RoomId == ctx)
                        CreateText(message.Text.ToString(), notify: false);
            }

            // Meeting notes belong to the main room only.
            if (ctx == 0)
                DisplayMeetingNotes();
        }

        /// <summary>
        /// Submits a text message locally.
        /// </summary>
        /// <param name="text">The text message to submit.</param>
        public void SubmitTextLocal(string text)
        {
            Debug.Log($"[NetworkMessageBoard] SubmitTextLocal Invoked! Raw text: '{text}'");
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[NetworkMessageBoard] Text was empty or whitespace. Aborting submission.");
                return;
            }
            
            // Check if LocalPlayer is available
            if (XRINetworkPlayer.LocalPlayer == null)
            {
                Debug.LogError("[NetworkMessageBoard] Cannot submit message: LocalPlayer is not available yet. (XRINetworkPlayer.LocalPlayer is null)");
                return;
            }
            
            Debug.Log($"[NetworkMessageBoard] LocalPlayer found: {XRINetworkPlayer.LocalPlayer.playerName}");
            string textToSend = $"<b>{XRINetworkPlayer.LocalPlayer.playerName}</b>:<br><br>{text}";

            if (textToSend.Length > m_MaxCharacterCount)
            {
                Debug.Log($"[NetworkMessageBoard] Text exceeded max characters ({m_MaxCharacterCount}), truncating...");
                textToSend = textToSend.Substring(0, m_MaxCharacterCount);
            }

            FixedString512Bytes newText = new FixedString512Bytes(DateTime.Now.ToString("h:mm tt") + "|||" + textToSend);
            // Tag the message with the sender's current room context so only that context displays it.
            var message = new ChatMessage(LocalContext(), newText);
            Debug.Log($"[NetworkMessageBoard] Calling SubmitMessageOwnerRpc (room={message.RoomId}): {newText}");
            SubmitMessageOwnerRpc(message);
        }

        /// <summary>
        /// Submits a message to the server.
        /// </summary>
        /// <param name="text">The message to submit.</param>
        [Rpc(SendTo.Owner)]
        void SubmitMessageOwnerRpc(ChatMessage message)
        {
            messageList.Add(message);
            if (messageList.Count > m_MaxMessageCount)
            {
                messageList.RemoveAt(0);
            }
            SubmitMessageRpc(message);
        }

        /// <summary>
        /// Submits a message to the clients. Each client only renders it if the message's room
        /// context matches their own (private-room isolation).
        /// </summary>
        /// <param name="message">The room-tagged message to submit.</param>
        [Rpc(SendTo.Everyone)]
        void SubmitMessageRpc(ChatMessage message)
        {
            if (message.RoomId == LocalContext())
                CreateText(message.Text.ToString());
        }

        /// <summary>
        /// Fired whenever a new message is displayed. Parameters: (messageText, timeText).
        /// Other UI (e.g. Chat Window) can subscribe to mirror messages.
        /// </summary>
        public static event System.Action<string, string> OnMessageCreated;

        /// <summary>
        /// Exposes the message prefab so other scripts can instantiate identical bubbles.
        /// </summary>
        public GameObject MessagePrefab => m_MessagePrefab;

        /// <summary>
        /// Returns all existing messages as strings so the Chat Window can reload them on reopen.
        /// </summary>
        public System.Collections.Generic.List<string> GetAllMessages()
        {
            var result = new System.Collections.Generic.List<string>();
            if (messageList != null)
            {
                ulong ctx = LocalContext();
                foreach (var msg in messageList)
                {
                    if (msg.RoomId == ctx)
                        result.Add(msg.Text.ToString());
                }
            }
            return result;
        }

        /// <summary>
        /// Creates a text message and adds it to the message board.
        /// </summary>
        /// <param name="text">The text of the message.</param>
        void CreateText(string text, bool notify = true)
        {
            // Parse time from stored format: "time|||message"
            string timeStr;
            string msgText;
            int sep = text.IndexOf("|||");
            if (sep >= 0)
            {
                timeStr = text.Substring(0, sep);
                msgText = text.Substring(sep + 3);
            }
            else
            {
                // Old format without time
                timeStr = DateTime.Now.ToString("h:mm tt");
                msgText = text;
            }

            Instantiate(m_MessagePrefab, m_ContentViewport).GetComponent<MessageText>().SetMessage(msgText, timeStr);

            if (m_ContentViewport.childCount > m_MaxMessageCount)
            {
                Destroy(m_ContentViewport.GetChild(0).gameObject);
            }

            // Notify any listeners (e.g. Chat Window) about the new message. Suppressed during a
            // full rebuild so the Chat Window mirror isn't flooded with duplicates on a room change.
            if (notify)
                OnMessageCreated?.Invoke(msgText, timeStr);
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(NetworkMessageBoard), true), CanEditMultipleObjects]
    public class NetworkMessageBoardEditor : Editor
    {

        [SerializeField, TextArea(10, 15)] string m_DebugText;
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            GUILayout.Space(10);
            GUILayout.Label("Debug Area", EditorStyles.boldLabel);
            GUI.enabled = XRINetworkGameManager.Connected.Value;
            if(!XRINetworkGameManager.Connected.Value)
            {
                GUILayout.Label("Connect to a network to submit messages.", EditorStyles.helpBox);
            }
            else
            {
                GUILayout.Label("Debug Text");
                m_DebugText = GUILayout.TextArea(m_DebugText);
            }
            if (GUILayout.Button("Submit Text Debug"))
            {
                ((NetworkMessageBoard)target).SubmitTextLocal(m_DebugText);
            }
            GUI.enabled = true;
        }
    }
#endif
}
