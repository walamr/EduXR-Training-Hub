using System.Collections;
using System.Collections.Generic;
using XRMultiplayer;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using Unity.Netcode;
using Unity.XR.CoreUtils.Bindings.Variables;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Controls the UI for creating private rooms, inviting players, and handling incoming invites.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class PrivateRoomUIController : MonoBehaviour
    {
        const string QuickMenuSegmentName = "Private Room";
        const int UiLayer = 5;
        const float CanvasScale = 0.001f;
        static readonly Vector2 CanvasSize = new Vector2(480f, 520f);

        [Header("Panels")]
        [SerializeField] private GameObject m_FloatingCanvasRoot;
        [SerializeField] private GameObject m_InvitePanel;
        [SerializeField] private GameObject m_IncomingInvitePopup;
        [SerializeField] private GameObject m_InRoomPanel;

        [Header("Invite Panel Elements")]
        [SerializeField] private Transform m_PlayerListContainer;
        [SerializeField] private GameObject m_PlayerTogglePrefab;
        [SerializeField] private Button m_CreateRoomButton;
        [SerializeField] private TMP_Text m_SelectedCountText;
        [SerializeField] private TextMeshProUGUI m_CreateRoomButtonLabel;

        [Header("Incoming Invite Elements")]
        [SerializeField] private TMP_Text m_InviteMessageText;
        [SerializeField] private Button m_AcceptButton;
        [SerializeField] private Button m_DeclineButton;

        [Header("In-Room Elements")]
        [SerializeField] private Button m_ReturnToMainButton;
        [SerializeField] private Transform m_MemberListContainer;

        [Header("Bootstrap")]
        [SerializeField, Tooltip("Builds a VR-ready floating canvas when panel references are not assigned.")]
        private bool m_AutoCreateMinimalInviteUI = true;

        readonly List<ulong> m_SelectedPlayers = new List<ulong>();
        readonly Queue<PrivateRoomInvite> m_PendingInvites = new Queue<PrivateRoomInvite>();
        readonly List<ulong> m_MemberScratch = new List<ulong>();
        ulong m_CurrentPendingInviteId;
        bool m_SubscribedToConnection;

        void Awake()
        {
            if (m_FloatingCanvasRoot == null && m_AutoCreateMinimalInviteUI)
                TryCreateFloatingUI();
        }

        void Start()
        {
            WireButtonListeners();
            HideAllPanels();
            SubscribeToConnectionState();
            StartCoroutine(SubscribeToServicesWhenReady());
        }

        void HideAllPanels()
        {
            if (m_FloatingCanvasRoot != null)
                m_FloatingCanvasRoot.SetActive(false);

            if (m_InvitePanel != null)
                m_InvitePanel.SetActive(false);

            if (m_IncomingInvitePopup != null)
                m_IncomingInvitePopup.SetActive(false);

            if (m_InRoomPanel != null)
                m_InRoomPanel.SetActive(false);
        }

        void WireButtonListeners()
        {
            if (m_CreateRoomButton != null)
            {
                m_CreateRoomButton.onClick.RemoveListener(OnCreateRoomClicked);
                m_CreateRoomButton.onClick.AddListener(OnCreateRoomClicked);
            }

            if (m_AcceptButton != null)
            {
                m_AcceptButton.onClick.RemoveListener(OnAcceptInviteClicked);
                m_AcceptButton.onClick.AddListener(OnAcceptInviteClicked);
            }

            if (m_DeclineButton != null)
            {
                m_DeclineButton.onClick.RemoveListener(OnDeclineInviteClicked);
                m_DeclineButton.onClick.AddListener(OnDeclineInviteClicked);
            }

            if (m_ReturnToMainButton != null)
            {
                m_ReturnToMainButton.onClick.RemoveListener(OnReturnToMainClicked);
                m_ReturnToMainButton.onClick.AddListener(OnReturnToMainClicked);
            }
        }

        void OnDestroy()
        {
            UnsubscribeFromConnectionState();

            if (QuickMenuManager.Instance != null && QuickMenuManager.Instance.HasSegment(QuickMenuSegmentName))
                QuickMenuManager.Instance.RemoveSegment(QuickMenuSegmentName);

            if (PrivateRoomInviteService.Instance != null)
            {
                PrivateRoomInviteService.Instance.OnInviteReceived -= HandleIncomingInvite;
                PrivateRoomInviteService.Instance.OnInviteWithdrawn -= HandleInviteWithdrawn;
            }

            if (PrivateRoomService.Instance != null)
            {
                PrivateRoomService.Instance.OnJoinedPrivateRoom -= HandleJoinedPrivateRoom;
                PrivateRoomService.Instance.OnLeftPrivateRoom -= HandleLeftPrivateRoom;
                PrivateRoomService.Instance.OnPrivateRoomTransferFailed -= HandlePrivateRoomTransferFailed;
                PrivateRoomService.Instance.OnRoomMembershipChanged -= HandleRoomMembershipChanged;
            }
        }

        IEnumerator SubscribeToServicesWhenReady()
        {
            const float timeoutSeconds = 20f;
            float elapsed = 0f;

            Debug.Log("[PrivateRoomUIController] SubscribeToServicesWhenReady started waiting for services...");
            while ((PrivateRoomInviteService.Instance == null || PrivateRoomService.Instance == null) && elapsed < timeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Debug.Log($"[PrivateRoomUIController] Services ready check. InviteService exists: {PrivateRoomInviteService.Instance != null}, Service exists: {PrivateRoomService.Instance != null}");

            if (PrivateRoomInviteService.Instance != null)
            {
                PrivateRoomInviteService.Instance.OnInviteReceived -= HandleIncomingInvite;
                PrivateRoomInviteService.Instance.OnInviteReceived += HandleIncomingInvite;
                PrivateRoomInviteService.Instance.OnInviteWithdrawn -= HandleInviteWithdrawn;
                PrivateRoomInviteService.Instance.OnInviteWithdrawn += HandleInviteWithdrawn;
                Debug.Log("[PrivateRoomUIController] Subscribed to OnInviteReceived.");

                // Process any existing pending invites that replicated before subscription was bound
                var invites = PrivateRoomInviteService.Instance.ActiveInvites;
                if (invites != null)
                {
                    Debug.Log($"[PrivateRoomUIController] Checking existing invites. Total in list = {invites.Count}");
                    for (int i = 0; i < invites.Count; i++)
                    {
                        var invite = invites[i];
                        if (invite.TargetId == NetworkManager.Singleton.LocalClientId && !invite.IsAccepted && !invite.IsDeclined)
                        {
                            Debug.Log($"[PrivateRoomUIController] Processing pre-existing pending invite: InviteId = {invite.InviteId}, Sender = {invite.SenderId}");
                            HandleIncomingInvite(invite);
                            break;
                        }
                    }
                }
            }

            if (PrivateRoomService.Instance != null)
            {
                PrivateRoomService.Instance.OnJoinedPrivateRoom -= HandleJoinedPrivateRoom;
                PrivateRoomService.Instance.OnJoinedPrivateRoom += HandleJoinedPrivateRoom;
                PrivateRoomService.Instance.OnLeftPrivateRoom -= HandleLeftPrivateRoom;
                PrivateRoomService.Instance.OnLeftPrivateRoom += HandleLeftPrivateRoom;
                PrivateRoomService.Instance.OnPrivateRoomTransferFailed -= HandlePrivateRoomTransferFailed;
                PrivateRoomService.Instance.OnPrivateRoomTransferFailed += HandlePrivateRoomTransferFailed;
                PrivateRoomService.Instance.OnRoomMembershipChanged -= HandleRoomMembershipChanged;
                PrivateRoomService.Instance.OnRoomMembershipChanged += HandleRoomMembershipChanged;

                if (PrivateRoomService.Instance.IsInPrivateRoom)
                    HandleJoinedPrivateRoom(PrivateRoomService.Instance.CurrentPrivateRoomId);
            }
        }

        void TryCreateFloatingUI()
        {
            if (m_FloatingCanvasRoot != null)
                return;

            m_FloatingCanvasRoot = CreateFloatingCanvasRoot();
            m_FloatingCanvasRoot.transform.SetParent(transform, false);
            var canvasTransform = m_FloatingCanvasRoot.transform;

            m_InvitePanel = CreateInvitePanel(canvasTransform);
            CreateIncomingInvitePopup(canvasTransform);
            CreateInRoomPanel(canvasTransform);
            UpdateSelectedCount();
            WireButtonListeners();
        }

        static GameObject CreateFloatingCanvasRoot()
        {
            var root = new GameObject("PrivateRoomFloatingCanvas");
            root.layer = UiLayer;

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 60;

            root.AddComponent<CanvasScaler>();
            root.AddComponent<GraphicRaycaster>();
            var trackedRaycaster = root.AddComponent<TrackedDeviceGraphicRaycaster>();
            trackedRaycaster.blockingMask = Physics.AllLayers;

            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = CanvasSize;
            rt.localScale = Vector3.one * CanvasScale;

            var collider = root.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(CanvasSize.x, CanvasSize.y, 10f);

            return root;
        }

        GameObject CreateInvitePanel(Transform canvasTransform)
        {
            var root = new GameObject("PrivateRoomInvitePanel");
            ApplyUiLayer(root);
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.SetParent(canvasTransform, false);
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0.106f, 0.106f, 0.106f, 0.95f);
            bg.raycastTarget = true;

            var layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(15, 15, 15, 15);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(root.transform, false);
            ApplyUiLayer(titleGo);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Private Room";
            titleTmp.fontSize = 28f;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                titleTmp.font = TMP_Settings.defaultFontAsset;

            var listGo = new GameObject("PlayerListContainer");
            listGo.transform.SetParent(root.transform, false);
            ApplyUiLayer(listGo);
            listGo.AddComponent<RectTransform>();
            var listLe = listGo.AddComponent<LayoutElement>();
            listLe.minHeight = 120f;
            listLe.flexibleHeight = 1f;

            m_CreateRoomButton = CreateButton(root.transform, "Create & Invite", out m_CreateRoomButtonLabel);
            m_SelectedCountText = CreateText(root.transform, string.Empty, 18f);
            m_PlayerListContainer = listGo.transform;

            root.SetActive(false);
            return root;
        }

        void CreateIncomingInvitePopup(Transform canvasTransform)
        {
            if (m_IncomingInvitePopup != null)
                return;

            var popup = new GameObject("PrivateRoomIncomingInvitePopup");
            ApplyUiLayer(popup);
            var popupRt = popup.AddComponent<RectTransform>();
            popupRt.SetParent(canvasTransform, false);
            popupRt.anchorMin = new Vector2(0.5f, 0.5f);
            popupRt.anchorMax = new Vector2(0.5f, 0.5f);
            popupRt.sizeDelta = new Vector2(420f, 220f);

            var bg = popup.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
            bg.raycastTarget = true;

            var layout = popup.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            m_InviteMessageText = CreateText(popup.transform, "You have been invited to a Private Room.", 22f);

            var buttonRow = new GameObject("Buttons");
            buttonRow.transform.SetParent(popup.transform, false);
            ApplyUiLayer(buttonRow);
            buttonRow.AddComponent<RectTransform>();
            var rowLayout = buttonRow.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 12f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childControlWidth = true;
            rowLayout.childForceExpandWidth = true;

            m_AcceptButton = CreateButton(buttonRow.transform, "Accept", out _);
            m_DeclineButton = CreateButton(buttonRow.transform, "Decline", out _);

            m_IncomingInvitePopup = popup;
            popup.SetActive(false);
        }

        void CreateInRoomPanel(Transform canvasTransform)
        {
            if (m_InRoomPanel != null)
                return;

            var panel = new GameObject("PrivateRoomInRoomPanel");
            ApplyUiLayer(panel);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.SetParent(canvasTransform, false);
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = new Vector2(20f, 20f);
            panelRt.offsetMax = new Vector2(-20f, -20f);

            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.08f, 0.92f);
            bg.raycastTarget = true;

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            CreateText(panel.transform, "You are in a Private Room", 24f);

            var membersGo = new GameObject("MemberListContainer");
            membersGo.transform.SetParent(panel.transform, false);
            ApplyUiLayer(membersGo);
            membersGo.AddComponent<RectTransform>();
            var membersLayout = membersGo.AddComponent<VerticalLayoutGroup>();
            membersLayout.spacing = 6f;
            membersLayout.childControlWidth = true;
            membersLayout.childControlHeight = true;
            membersLayout.childForceExpandWidth = true;
            var membersLe = membersGo.AddComponent<LayoutElement>();
            membersLe.minHeight = 120f;
            membersLe.flexibleHeight = 1f;
            m_MemberListContainer = membersGo.transform;

            m_ReturnToMainButton = CreateButton(panel.transform, "Return to Main Room", out _);
            var buttonLe = m_ReturnToMainButton.gameObject.GetComponent<LayoutElement>();
            if (buttonLe == null)
                buttonLe = m_ReturnToMainButton.gameObject.AddComponent<LayoutElement>();
            buttonLe.preferredHeight = 56f;

            m_InRoomPanel = panel;
            panel.SetActive(false);
        }

        static void ApplyUiLayer(GameObject go)
        {
            go.layer = UiLayer;
        }

        static Button CreateButton(Transform parent, string label, out TextMeshProUGUI labelText)
        {
            var btnGo = new GameObject(label.Replace(" ", string.Empty) + "Button");
            btnGo.transform.SetParent(parent, false);
            ApplyUiLayer(btnGo);
            btnGo.AddComponent<RectTransform>();
            var btnImg = btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.125f, 0.588f, 0.953f, 1f);
            btnImg.raycastTarget = true;
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            var btnLe = btnGo.AddComponent<LayoutElement>();
            btnLe.preferredHeight = 48f;

            var btnLabelGo = new GameObject("Label");
            btnLabelGo.transform.SetParent(btnGo.transform, false);
            ApplyUiLayer(btnLabelGo);
            var btnLabelRt = btnLabelGo.AddComponent<RectTransform>();
            btnLabelRt.anchorMin = Vector2.zero;
            btnLabelRt.anchorMax = Vector2.one;
            btnLabelRt.offsetMin = Vector2.zero;
            btnLabelRt.offsetMax = Vector2.zero;
            labelText = btnLabelGo.AddComponent<TextMeshProUGUI>();
            labelText.text = label;
            labelText.fontSize = 22f;
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                labelText.font = TMP_Settings.defaultFontAsset;

            return btn;
        }

        static TMP_Text CreateText(Transform parent, string text, float fontSize)
        {
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(parent, false);
            ApplyUiLayer(textGo);
            textGo.AddComponent<RectTransform>();
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }

        void SubscribeToConnectionState()
        {
            if (m_SubscribedToConnection)
                return;

            XRINetworkGameManager.Connected.Subscribe(OnConnectionStateChanged);
            m_SubscribedToConnection = true;
            OnConnectionStateChanged(XRINetworkGameManager.Connected.Value);
        }

        void UnsubscribeFromConnectionState()
        {
            if (!m_SubscribedToConnection)
                return;

            XRINetworkGameManager.Connected.Unsubscribe(OnConnectionStateChanged);
            m_SubscribedToConnection = false;
        }

        void OnConnectionStateChanged(bool connected)
        {
            if (connected)
            {
                RegisterPrivateRoomSegment();
                return;
            }

            if (QuickMenuManager.Instance != null && QuickMenuManager.Instance.HasSegment(QuickMenuSegmentName))
                QuickMenuManager.Instance.RemoveSegment(QuickMenuSegmentName);

            // If we disconnect while inside a private room, no RPCs are possible — unlock the menus
            // and tear the room down locally so a later reconnect starts clean.
            PrivateRoomMenuLock.Unlock();
            if (PrivateRoomService.Instance != null)
                PrivateRoomService.Instance.ForceLocalExitIfActive();

            m_PendingInvites.Clear();
            m_CurrentPendingInviteId = 0;

            HideAllPanels();
        }

        void RegisterPrivateRoomSegment()
        {
            if (QuickMenuManager.Instance == null || m_FloatingCanvasRoot == null)
            {
                Debug.LogWarning("[PrivateRoomUIController] QuickMenuManager or floating canvas missing — Private Room menu segment not registered.");
                return;
            }

            if (!QuickMenuManager.Instance.HasSegment(QuickMenuSegmentName))
                QuickMenuManager.Instance.AddSegment(QuickMenuSegmentName, null, m_FloatingCanvasRoot, OnPrivateRoomSegmentClicked);
        }

        void PresentFloatingCanvas(System.Action configurePanels)
        {
            if (m_FloatingCanvasRoot == null)
                return;

            configurePanels?.Invoke();

            if (QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.OpenFloatingPanel(m_FloatingCanvasRoot);
            else
                m_FloatingCanvasRoot.SetActive(true);
        }

        void OnPrivateRoomSegmentClicked()
        {
            if (!PrivateRoomPodiumCompatibility.IsPrivateRoomAvailable(out string blockReason))
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(blockReason, 3f);
                return;
            }

            if (m_IncomingInvitePopup != null)
                m_IncomingInvitePopup.SetActive(false);

            if (m_InRoomPanel != null)
                m_InRoomPanel.SetActive(false);

            if (m_InvitePanel != null)
                m_InvitePanel.SetActive(true);

            RefreshPlayerList();
            UpdateSelectedCount();
        }

        void RefreshPlayerList()
        {
            if (m_PlayerListContainer == null)
                return;

            foreach (Transform child in m_PlayerListContainer)
                Destroy(child.gameObject);

            m_SelectedPlayers.Clear();
            UpdateSelectedCount();

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
                return;

            foreach (var client in NetworkManager.Singleton.ConnectedClients)
            {
                if (client.Key == NetworkManager.Singleton.LocalClientId)
                    continue;

                // Don't offer players who are already inside a private room — the owner-side
                // create handler would skip them anyway, so showing them is misleading.
                if (PrivateRoomService.Instance != null && PrivateRoomService.Instance.IsClientInPrivateRoom(client.Key))
                    continue;

                ulong targetId = client.Key;
                string playerLabel = GetPlayerLabel(targetId);
                Toggle toggle;

                if (m_PlayerTogglePrefab != null)
                {
                    var toggleObj = Instantiate(m_PlayerTogglePrefab, m_PlayerListContainer);
                    toggle = toggleObj.GetComponent<Toggle>();
                    var label = toggleObj.GetComponentInChildren<TMP_Text>();
                    if (label != null)
                        label.text = playerLabel;
                }
                else
                {
                    toggle = CreatePlayerToggle(m_PlayerListContainer, playerLabel);
                }

                if (toggle == null)
                    continue;

                toggle.onValueChanged.AddListener(val =>
                {
                    if (val)
                    {
                        if (!m_SelectedPlayers.Contains(targetId))
                            m_SelectedPlayers.Add(targetId);
                    }
                    else
                    {
                        m_SelectedPlayers.Remove(targetId);
                    }

                    UpdateSelectedCount();
                });
            }
        }

        static Toggle CreatePlayerToggle(Transform parent, string labelText)
        {
            var row = new GameObject("PlayerToggle");
            row.transform.SetParent(parent, false);
            ApplyUiLayer(row);
            row.AddComponent<RectTransform>();
            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 36f;

            var bg = row.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            bg.raycastTarget = true;

            var toggle = row.AddComponent<Toggle>();
            toggle.targetGraphic = bg;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(row.transform, false);
            ApplyUiLayer(labelGo);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(12f, 0f);
            labelRt.offsetMax = new Vector2(-12f, 0f);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = labelText;
            label.fontSize = 18f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                label.font = TMP_Settings.defaultFontAsset;

            return toggle;
        }

        static string GetPlayerLabel(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out XRINetworkPlayer player))
            {
                return player.displayName;
            }

            return $"Player {clientId}";
        }

        static bool HasOtherPlayersInSession()
        {
            return NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsConnectedClient
                && NetworkManager.Singleton.ConnectedClients.Count > 1;
        }

        void UpdateSelectedCount()
        {
            bool soloSession = !HasOtherPlayersInSession();

            if (m_SelectedCountText != null)
            {
                m_SelectedCountText.text = soloSession
                    ? "No other players in the room — you can enter solo."
                    : $"{m_SelectedPlayers.Count} / {PrivateRoomService.MAX_ROOM_SIZE - 1} Selected";
            }

            if (m_CreateRoomButtonLabel != null)
            {
                m_CreateRoomButtonLabel.text = soloSession
                    ? "Enter Private Room"
                    : "Create & Invite";
            }

            if (m_CreateRoomButton != null)
            {
                m_CreateRoomButton.interactable = soloSession
                    || (m_SelectedPlayers.Count > 0 && m_SelectedPlayers.Count < PrivateRoomService.MAX_ROOM_SIZE);
            }
        }

        void OnCreateRoomClicked()
        {
            Debug.Log("[PrivateRoomUIController] Create private room button clicked.");

            if (!PrivateRoomPodiumCompatibility.IsPrivateRoomAvailable(out string blockReason))
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(blockReason, 3f);
                return;
            }

            if (PrivateRoomService.Instance == null)
            {
                Debug.LogWarning("[PrivateRoomUIController] PrivateRoomService is not available.");
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("Private Room service is not ready.", 3f);
                return;
            }

            if (HasOtherPlayersInSession() &&
                (m_SelectedPlayers.Count == 0 || m_SelectedPlayers.Count >= PrivateRoomService.MAX_ROOM_SIZE))
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("Select at least one player to invite.", 3f);
                return;
            }

            if (m_CreateRoomButton != null)
                m_CreateRoomButton.interactable = false;

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("Opening private room...", 2f);

            PrivateRoomService.Instance.CreateRoomAndInvitePlayers(m_SelectedPlayers);
        }

        void HandleIncomingInvite(PrivateRoomInvite invite)
        {
            Debug.Log($"[PrivateRoomUIController] HandleIncomingInvite: InviteId = {invite.InviteId}, Sender = {invite.SenderId}, RoomId = {invite.RoomId}");
            if (!PrivateRoomPodiumCompatibility.IsPrivateRoomAvailable(out string blockReason))
            {
                Debug.LogWarning($"[PrivateRoomUIController] Private Room unavailable. Reason: {blockReason}. Declining invite {invite.InviteId}.");
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(blockReason, 3f);

                if (PrivateRoomInviteService.Instance != null)
                    PrivateRoomInviteService.Instance.DeclineInvite(invite.InviteId);

                return;
            }

            // Queue invites so a second one doesn't overwrite (and orphan) the first.
            m_PendingInvites.Enqueue(invite);

            if (m_CurrentPendingInviteId == 0)
                ShowNextInvite();
        }

        void ShowNextInvite()
        {
            while (m_PendingInvites.Count > 0)
            {
                var invite = m_PendingInvites.Dequeue();
                m_CurrentPendingInviteId = invite.InviteId;

                PresentFloatingCanvas(() =>
                {
                    if (m_InvitePanel != null)
                        m_InvitePanel.SetActive(false);

                    if (m_InviteMessageText != null)
                        m_InviteMessageText.text = $"{GetPlayerLabel(invite.SenderId)} invited you to a Private Room.";

                    if (m_IncomingInvitePopup != null)
                        m_IncomingInvitePopup.SetActive(true);
                });
                return;
            }

            // No more pending invites.
            m_CurrentPendingInviteId = 0;
            if (m_IncomingInvitePopup != null)
                m_IncomingInvitePopup.SetActive(false);
        }

        void HandleInviteWithdrawn(ulong inviteId)
        {
            // If the invite currently on screen was resolved/expired elsewhere, advance the queue.
            if (m_CurrentPendingInviteId == inviteId)
            {
                m_CurrentPendingInviteId = 0;
                if (m_IncomingInvitePopup != null)
                    m_IncomingInvitePopup.SetActive(false);
                ShowNextInvite();
                return;
            }

            // Otherwise drop it from the queue if it's still waiting.
            if (m_PendingInvites.Count == 0)
                return;

            int count = m_PendingInvites.Count;
            for (int i = 0; i < count; i++)
            {
                var invite = m_PendingInvites.Dequeue();
                if (invite.InviteId != inviteId)
                    m_PendingInvites.Enqueue(invite);
            }
        }

        void HandleRoomMembershipChanged()
        {
            if (PrivateRoomService.Instance != null && PrivateRoomService.Instance.IsInPrivateRoom &&
                m_InRoomPanel != null && m_InRoomPanel.activeSelf)
            {
                RefreshMemberList();
            }
        }

        /// <summary>
        /// Ensures a member-list container exists even when the in-room panel was assigned in the
        /// inspector (the auto-build path is skipped in that case).
        /// </summary>
        void EnsureMemberListContainer()
        {
            if (m_MemberListContainer != null || m_InRoomPanel == null)
                return;

            var membersGo = new GameObject("MemberListContainer");
            membersGo.transform.SetParent(m_InRoomPanel.transform, false);
            ApplyUiLayer(membersGo);
            membersGo.AddComponent<RectTransform>();
            var layout = membersGo.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            var le = membersGo.AddComponent<LayoutElement>();
            le.minHeight = 120f;
            le.flexibleHeight = 1f;

            // Place it above the "Return to Main Room" button when present.
            if (m_ReturnToMainButton != null)
                membersGo.transform.SetSiblingIndex(Mathf.Max(0, m_ReturnToMainButton.transform.GetSiblingIndex()));

            m_MemberListContainer = membersGo.transform;
        }

        void RefreshMemberList()
        {
            EnsureMemberListContainer();

            if (m_MemberListContainer == null || PrivateRoomService.Instance == null)
                return;

            foreach (Transform child in m_MemberListContainer)
                Destroy(child.gameObject);

            ulong roomId = PrivateRoomService.Instance.CurrentPrivateRoomId;
            if (roomId == 0)
                return;

            PrivateRoomService.Instance.GetRoomMembers(roomId, m_MemberScratch);
            PrivateRoomService.Instance.TryGetRoomOwner(roomId, out ulong roomOwnerId);

            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            bool localIsRoomOwner = localId == roomOwnerId;

            foreach (ulong memberId in m_MemberScratch)
            {
                string suffix = memberId == roomOwnerId ? "  (owner)" : string.Empty;
                string label = (memberId == localId ? "You" : GetPlayerLabel(memberId)) + suffix;

                // The room owner gets a kick button next to every other member.
                if (localIsRoomOwner && memberId != localId && memberId != roomOwnerId)
                {
                    var row = new GameObject("MemberRow");
                    row.transform.SetParent(m_MemberListContainer, false);
                    ApplyUiLayer(row);
                    row.AddComponent<RectTransform>();
                    var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
                    rowLayout.spacing = 8f;
                    rowLayout.childControlWidth = true;
                    rowLayout.childForceExpandWidth = true;
                    rowLayout.childAlignment = TextAnchor.MiddleCenter;
                    var rowLe = row.AddComponent<LayoutElement>();
                    rowLe.preferredHeight = 40f;

                    var nameText = CreateText(row.transform, label, 18f);
                    nameText.alignment = TextAlignmentOptions.MidlineLeft;

                    ulong kickTarget = memberId;
                    var kickBtn = CreateButton(row.transform, "Kick", out var kickLabel);
                    var kickLe = kickBtn.gameObject.GetComponent<LayoutElement>();
                    if (kickLe != null) kickLe.flexibleWidth = 0.35f;
                    kickBtn.onClick.AddListener(() =>
                    {
                        if (PrivateRoomService.Instance != null)
                            PrivateRoomService.Instance.KickMember(roomId, kickTarget);
                    });
                }
                else
                {
                    var memberText = CreateText(m_MemberListContainer, label, 18f);
                    memberText.alignment = TextAlignmentOptions.MidlineLeft;
                }
            }
        }

        void OnAcceptInviteClicked()
        {
            ulong inviteId = m_CurrentPendingInviteId;
            m_CurrentPendingInviteId = 0;

            if (m_IncomingInvitePopup != null)
                m_IncomingInvitePopup.SetActive(false);

            if (inviteId != 0 && PrivateRoomInviteService.Instance != null)
                PrivateRoomInviteService.Instance.AcceptInvite(inviteId);

            // Accepting commits us to a room; drop any other queued invites.
            m_PendingInvites.Clear();
        }

        void OnDeclineInviteClicked()
        {
            ulong inviteId = m_CurrentPendingInviteId;
            m_CurrentPendingInviteId = 0;

            if (m_IncomingInvitePopup != null)
                m_IncomingInvitePopup.SetActive(false);

            if (inviteId != 0 && PrivateRoomInviteService.Instance != null)
                PrivateRoomInviteService.Instance.DeclineInvite(inviteId);

            // Show the next queued invite, if any; otherwise close the panel.
            if (m_PendingInvites.Count > 0)
            {
                ShowNextInvite();
                return;
            }

            if (QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.CloseFloatingPanel(m_FloatingCanvasRoot);
            else if (m_FloatingCanvasRoot != null)
                m_FloatingCanvasRoot.SetActive(false);
        }

        void OnReturnToMainClicked()
        {
            Debug.Log("[PrivateRoomUIController] Return to main room clicked.");

            if (PrivateRoomService.Instance == null)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("Private Room service is not available.", 3f);
                return;
            }

            if (!PrivateRoomService.Instance.IsInPrivateRoom)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("You are not in a private room.", 3f);
                return;
            }

            if (m_ReturnToMainButton != null)
                m_ReturnToMainButton.interactable = false;

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("Returning to main room...", 2f);

            PrivateRoomService.Instance.LeaveCurrentRoom();
        }

        void HandleJoinedPrivateRoom(ulong roomId)
        {
            WireButtonListeners();

            // We're committed to a room now; clear any leftover queued invites.
            m_PendingInvites.Clear();
            m_CurrentPendingInviteId = 0;

            PresentFloatingCanvas(() =>
            {
                if (m_InvitePanel != null)
                    m_InvitePanel.SetActive(false);

                if (m_IncomingInvitePopup != null)
                    m_IncomingInvitePopup.SetActive(false);

                if (m_InRoomPanel != null)
                    m_InRoomPanel.SetActive(true);

                if (m_ReturnToMainButton != null)
                    m_ReturnToMainButton.interactable = true;

                RefreshMemberList();
            });

            PrivateRoomMenuLock.Lock(m_FloatingCanvasRoot);
        }

        void HandlePrivateRoomTransferFailed()
        {
            if (m_CreateRoomButton != null)
                m_CreateRoomButton.interactable = true;

            UpdateSelectedCount();
        }

        void HandleLeftPrivateRoom()
        {
            PrivateRoomMenuLock.Unlock();

            if (m_InRoomPanel != null)
                m_InRoomPanel.SetActive(false);

            if (m_ReturnToMainButton != null)
                m_ReturnToMainButton.interactable = true;

            if (QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.CloseFloatingPanel(m_FloatingCanvasRoot);
            else if (m_FloatingCanvasRoot != null)
                m_FloatingCanvasRoot.SetActive(false);
        }
    }
}
