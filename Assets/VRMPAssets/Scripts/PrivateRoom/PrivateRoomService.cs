using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Service for managing private room state, lifecycle, and membership.
    /// </summary>
    public class PrivateRoomService : NetworkBehaviour
    {
        public static PrivateRoomService Instance { get; private set; }

        public const int MAX_ROOM_SIZE = 4;

        private NetworkList<PrivateRoomState> m_ActiveRooms;
        private NetworkList<PrivateRoomMember> m_RoomMembers;

        public ulong CurrentPrivateRoomId { get; private set; }
        public bool IsInPrivateRoom => CurrentPrivateRoomId != 0;

        public Action<ulong> OnJoinedPrivateRoom;
        public Action OnLeftPrivateRoom;
        public Action OnPrivateRoomTransferFailed;

        /// <summary>Raised on every client whenever room membership changes (for live UI refresh).</summary>
        public Action OnRoomMembershipChanged;

        [SerializeField] private Vector3 m_PrivateRoomLocation = new Vector3(1000f, 1000f, 1000f);
        [SerializeField] private Vector3 m_PrivateRoomSpawnOffset = new Vector3(0f, 0.1f, 0f);
        [SerializeField, Tooltip("World-space spacing between distinct private rooms so they never overlap.")]
        private float m_RoomSpacing = 200f;
        [SerializeField, Tooltip("Radius of the ring members are spread around when spawning in a room.")]
        private float m_MemberSpacingRadius = 0.9f;
        [SerializeField, Tooltip("Maximum number of private rooms that can be active at once.")]
        private int m_MaxConcurrentRooms = 8;
        [SerializeField] private string m_PrivateRoomSceneName = "PrivateRoomPreview";
        [SerializeField] private string m_MainSceneName = "SampleScene";

        private Vector3 m_MainRoomReturnPosition;
        private bool m_HasSavedReturnPosition;
        private bool m_PrivateRoomSceneLoaded;
        private Coroutine m_TransferCoroutine;
        private Scene m_MainScene;
        private Material m_SavedSkybox;
        private Color m_SavedAmbientLight;
        private float m_SavedAmbientIntensity;
        private bool m_HasSavedRenderSettings;
        private Vector3 m_CurrentRoomOffset;
        private bool m_DisconnectSubscribed;
        private bool m_MembershipNotificationsReady;
        private bool m_IsReturning;
        // Room we are currently transferring INTO (set before the scene load, cleared on completion
        // or abort). Lets a kick/removal that arrives mid-transfer be honored before CurrentPrivateRoomId
        // is committed, instead of letting the player land in a room they were just removed from.
        private ulong m_PendingTransferRoomId;
        private readonly List<GameObject> m_HiddenMainSceneRoots = new List<GameObject>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            m_ActiveRooms = new NetworkList<PrivateRoomState>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            m_RoomMembers = new NetworkList<PrivateRoomMember>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        }

        /// <summary>
        /// Scene services are owned by the session owner under Distributed Authority (IsServer stays false).
        /// </summary>
        bool HasStateAuthority => IsOwner;

        private void Start()
        {
            // Validate the private-room scene is shippable up-front, not only on first use.
            if (!Application.CanStreamedLevelBeLoaded(m_PrivateRoomSceneName))
            {
                Debug.LogError($"[PrivateRoomService] Scene '{m_PrivateRoomSceneName}' is not in Build Settings. " +
                               "Private Rooms will not work until it is added.");
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (m_RoomMembers != null)
                m_RoomMembers.OnListChanged += OnRoomMembersChanged;

            if (HasStateAuthority)
                SubscribeDisconnect();

            // Suppress "stepped into a private room" HUD spam from the initial NetworkList sync,
            // which replays existing members as Add events when we first connect.
            m_MembershipNotificationsReady = false;
            StartCoroutine(EnableMembershipNotificationsAfterSettle());
        }

        IEnumerator EnableMembershipNotificationsAfterSettle()
        {
            yield return new WaitForSeconds(2.5f);
            m_MembershipNotificationsReady = true;
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();

            if (m_RoomMembers != null)
                m_RoomMembers.OnListChanged -= OnRoomMembersChanged;

            UnsubscribeDisconnect();
        }

        // --- Distributed Authority ownership handling (host migration safe) ---

        public override void OnGainedOwnership()
        {
            base.OnGainedOwnership();
            SubscribeDisconnect();
            ReconcileStateAsNewOwner();
        }

        public override void OnLostOwnership()
        {
            base.OnLostOwnership();
            UnsubscribeDisconnect();
        }

        void SubscribeDisconnect()
        {
            if (m_DisconnectSubscribed || NetworkManager.Singleton == null || !HasStateAuthority)
                return;

            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            m_DisconnectSubscribed = true;
        }

        void UnsubscribeDisconnect()
        {
            if (!m_DisconnectSubscribed || NetworkManager.Singleton == null)
                return;

            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            m_DisconnectSubscribed = false;
        }

        /// <summary>
        /// When a client becomes the new state owner (e.g. host migration), prune any members or
        /// invites that belong to clients which are no longer connected, and close empty rooms.
        /// </summary>
        void ReconcileStateAsNewOwner()
        {
            if (!HasStateAuthority || NetworkManager.Singleton == null)
                return;

            for (int i = m_RoomMembers.Count - 1; i >= 0; i--)
            {
                if (!IsClientConnected(m_RoomMembers[i].ClientId))
                {
                    ulong roomId = m_RoomMembers[i].RoomId;
                    m_RoomMembers.RemoveAt(i);
                    TryDeactivateEmptyRoom(roomId);
                }
            }
        }

        static bool IsClientConnected(ulong clientId)
        {
            if (NetworkManager.Singleton == null)
                return false;

            var ids = NetworkManager.Singleton.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == clientId)
                    return true;
            }

            return false;
        }

        private void OnClientDisconnect(ulong clientId)
        {
            if (!HasStateAuthority) return;

            for (int i = m_RoomMembers.Count - 1; i >= 0; i--)
            {
                if (m_RoomMembers[i].ClientId == clientId)
                {
                    ulong roomId = m_RoomMembers[i].RoomId;
                    m_RoomMembers.RemoveAt(i);
                    TryDeactivateEmptyRoom(roomId);
                }
            }

            if (PrivateRoomInviteService.Instance != null)
                PrivateRoomInviteService.Instance.RemoveInvitesForClient(clientId);
        }

        void OnRoomMembersChanged(NetworkListEvent<PrivateRoomMember> changeEvent)
        {
            OnRoomMembershipChanged?.Invoke();
            ShowMembershipNotification(changeEvent);
            HandleLocalMembershipRemoval(changeEvent);
        }

        /// <summary>
        /// Membership is the single source of truth for client-side exit: if the local player is
        /// removed from the room they're currently in (kick, owner-side cleanup, etc.), tear down
        /// locally. This does not rely on a separate RPC being delivered.
        /// </summary>
        void HandleLocalMembershipRemoval(NetworkListEvent<PrivateRoomMember> changeEvent)
        {
            bool removed = changeEvent.Type == NetworkListEvent<PrivateRoomMember>.EventType.Remove ||
                           changeEvent.Type == NetworkListEvent<PrivateRoomMember>.EventType.RemoveAt;
            if (!removed || NetworkManager.Singleton == null)
                return;

            if (changeEvent.Value.ClientId != NetworkManager.Singleton.LocalClientId)
                return;

            // Consider both the room we're already in AND the room we're mid-transfer into, so a
            // kick that arrives while the private scene is still loading isn't silently ignored.
            ulong activeRoom = IsInPrivateRoom ? CurrentPrivateRoomId : m_PendingTransferRoomId;
            if (activeRoom == 0 || changeEvent.Value.RoomId != activeRoom)
                return;

            // Signal any in-flight transfer to abort before it commits, then tear down. If the scene
            // is already loaded, ReturnToMainRoom handles it now; if not, the transfer coroutine sees
            // the cleared pending id at its commit check and aborts itself.
            m_PendingTransferRoomId = 0;
            ReturnToMainRoom();
        }

        /// <summary>
        /// Lightweight "X left to / returned from a private room" HUD note for players who are NOT in
        /// that same room, so the people left behind know where someone went.
        /// </summary>
        void ShowMembershipNotification(NetworkListEvent<PrivateRoomMember> changeEvent)
        {
            if (!m_MembershipNotificationsReady)
                return;

            if (NetworkManager.Singleton == null || PlayerHudNotification.Instance == null)
                return;

            bool added = changeEvent.Type == NetworkListEvent<PrivateRoomMember>.EventType.Add;
            bool removed = changeEvent.Type == NetworkListEvent<PrivateRoomMember>.EventType.Remove ||
                           changeEvent.Type == NetworkListEvent<PrivateRoomMember>.EventType.RemoveAt;
            if (!added && !removed)
                return;

            ulong clientId = changeEvent.Value.ClientId;
            if (clientId == NetworkManager.Singleton.LocalClientId)
                return; // our own transitions already show their own HUD text

            // Don't narrate room-mates' comings and goings to people sharing that room.
            if (changeEvent.Value.RoomId == CurrentPrivateRoomId && IsInPrivateRoom)
                return;

            string name = GetClientDisplayName(clientId);
            PlayerHudNotification.Instance.ShowText(added
                ? $"<b>{name}</b> stepped into a private room"
                : $"<b>{name}</b> returned from a private room");
        }

        static string GetClientDisplayName(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out XRINetworkPlayer player))
                return player.displayName;

            return $"Player {clientId}";
        }

        public void CreateRoomAndInvitePlayers(IReadOnlyList<ulong> inviteeIds)
        {
            inviteeIds ??= new List<ulong>();

            if (inviteeIds.Count > MAX_ROOM_SIZE - 1)
            {
                NotifyTransferFailed($"A private room holds at most {MAX_ROOM_SIZE} players. Select fewer invitees.");
                return;
            }

            if (!PrivateRoomPodiumCompatibility.IsPrivateRoomAvailable(out string unavailableReason))
            {
                NotifyTransferFailed(unavailableReason);
                return;
            }

            if (!TryUseNetworkPath(out string networkBlockReason))
            {
                NotifyTransferFailed($"Private Room unavailable: {networkBlockReason}");
                return;
            }

            ulong[] targets = new ulong[inviteeIds.Count];
            for (int i = 0; i < inviteeIds.Count; i++)
                targets[i] = inviteeIds[i];

            if (HasStateAuthority)
                HandleCreateRoomAndInvite(NetworkManager.Singleton.LocalClientId, targets, NetworkManager.Singleton.LocalClientId);
            else
                CreateRoomAndInvitePlayersOwnerRpc(NetworkManager.Singleton.LocalClientId, targets);

            StartCoroutine(EnsureTransferStartedFallback());
        }

        IEnumerator EnsureTransferStartedFallback()
        {
            // Generous timeout: a non-owner round-trip plus an additive scene load can take several
            // seconds. We only declare failure if NO positive signal of progress has appeared by then.
            const float timeoutSeconds = 12f;
            float elapsed = 0f;
            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;

            // Any of these means the request is progressing and must NOT be reported as a failure:
            //   - we've entered a room / a transfer coroutine is running / a transfer is pending, or
            //   - the owner has already added us to the membership list (replicates independently).
            while (elapsed < timeoutSeconds && !TransferIsProgressing(localId))
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (TransferIsProgressing(localId))
                yield break;

            NotifyTransferFailed("Private room transfer failed. Stay connected to the lobby and try again.");
        }

        bool TransferIsProgressing(ulong localId)
        {
            return CurrentPrivateRoomId != 0
                || m_TransferCoroutine != null
                || m_PendingTransferRoomId != 0
                || IsClientInPrivateRoom(localId);
        }

        static bool TryUseNetworkPath(out string blockReason)
        {
            blockReason = null;

            if (NetworkManager.Singleton == null)
            {
                blockReason = "NetworkManager missing";
                return false;
            }

            if (!NetworkManager.Singleton.IsConnectedClient)
            {
                blockReason = "not connected to a Netcode session";
                return false;
            }

            if (Instance == null || !Instance.IsSpawned)
            {
                blockReason = "PrivateRoomService not network-spawned";
                return false;
            }

            return true;
        }

        static void NotifyTransferFailed(string message)
        {
            Debug.LogWarning("[PrivateRoomService] " + message);
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText(message, 4f);

            Instance?.OnPrivateRoomTransferFailed?.Invoke();
        }

        [Rpc(SendTo.Owner)]
        private void CreateRoomAndInvitePlayersOwnerRpc(ulong creatorId, ulong[] inviteeIds, RpcParams rpcParams = default)
        {
            HandleCreateRoomAndInvite(creatorId, inviteeIds, rpcParams.Receive.SenderClientId);
        }

        private void HandleCreateRoomAndInvite(ulong creatorId, ulong[] inviteeIds, ulong callerId)
        {
            inviteeIds ??= System.Array.Empty<ulong>();

            // Authority check: a client may only create a room as itself.
            if (creatorId != callerId)
            {
                Debug.LogWarning($"[PrivateRoomService] Rejected room creation: caller {callerId} claimed creator {creatorId}.");
                return;
            }

            Debug.Log($"[PrivateRoomService] HandleCreateRoomAndInvite. Creator = {creatorId}, InviteeCount = {inviteeIds.Length}");

            if (inviteeIds.Length > MAX_ROOM_SIZE - 1)
            {
                NotifyTransferFailed("Too many players selected for a private room.");
                return;
            }

            if (CountActiveRooms() >= m_MaxConcurrentRooms)
            {
                NotifyTransferFailed("Too many private rooms are already open. Try again later.");
                return;
            }

            if (IsClientInAnyRoom(creatorId))
            {
                NotifyTransferFailed("You are already in a private room.");
                return;
            }

            ulong newRoomId = GenerateUniqueRoomId();
            Debug.Log($"[PrivateRoomService] Generated Room ID = {newRoomId}. Adding to ActiveRooms.");

            m_ActiveRooms.Add(new PrivateRoomState
            {
                RoomId = newRoomId,
                OwnerId = creatorId,
                MaxMembers = MAX_ROOM_SIZE,
                IsActive = true,
                CellIndex = AssignRoomCell()
            });

            if (PrivateRoomInviteService.Instance != null)
            {
                foreach (ulong targetId in inviteeIds)
                {
                    bool isSelf = targetId == creatorId;
                    bool alreadyInRoom = IsClientInAnyRoom(targetId);

                    if (isSelf || alreadyInRoom)
                        continue;

                    PrivateRoomInviteService.Instance.SendInvite(creatorId, targetId, newRoomId);
                }
            }
            else
            {
                Debug.LogWarning("[PrivateRoomService] PrivateRoomInviteService.Instance is NULL on Server!");
            }

            JoinRoom(creatorId, newRoomId, roomJustCreated: true);
        }

        public void JoinRoom(ulong clientId, ulong roomId, bool roomJustCreated = false)
        {
            if (!HasStateAuthority)
                return;

            if (IsClientInAnyRoom(clientId))
                return;

            if (!roomJustCreated)
            {
                if (!TryGetRoomState(roomId, out PrivateRoomState roomState) || !roomState.IsActive)
                {
                    NotifyJoinRejected(clientId, "That private room is no longer available.");
                    return;
                }

                if (GetRoomMemberCount(roomId) >= roomState.MaxMembers)
                {
                    NotifyJoinRejected(clientId, "That private room is full.");
                    return;
                }
            }

            // Capture a stable cell + member slot now (on the authority) and thread them through the
            // transfer so the joining client never has to wait for the NetworkList to replicate.
            int cellIndex = TryGetRoomState(roomId, out PrivateRoomState state) ? state.CellIndex : 0;
            int memberSlot = GetRoomMemberCount(roomId); // 0-based index of this new member

            m_RoomMembers.Add(new PrivateRoomMember
            {
                ClientId = clientId,
                RoomId = roomId
            });

            NotifyClientJoined(clientId, roomId, cellIndex, memberSlot);
        }

        void NotifyJoinRejected(ulong clientId, string reason)
        {
            if (NetworkManager.Singleton != null && clientId == NetworkManager.Singleton.LocalClientId)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(reason, 4f);
                OnPrivateRoomTransferFailed?.Invoke();
                return;
            }

            NotifyJoinRejectedClientRpc(clientId, reason);
        }

        [ClientRpc]
        void NotifyJoinRejectedClientRpc(ulong clientId, string reason)
        {
            if (NetworkManager.Singleton.LocalClientId != clientId)
                return;

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText(reason, 4f);
            OnPrivateRoomTransferFailed?.Invoke();
        }

        void NotifyClientJoined(ulong clientId, ulong roomId, int cellIndex, int memberSlot)
        {
            if (NetworkManager.Singleton != null && clientId == NetworkManager.Singleton.LocalClientId)
            {
                PerformLocalTransfer(roomId, cellIndex, memberSlot);
                return;
            }

            NotifyClientJoinedRoomClientRpc(clientId, roomId, cellIndex, memberSlot);
        }

        [ClientRpc]
        private void NotifyClientJoinedRoomClientRpc(ulong clientId, ulong roomId, int cellIndex, int memberSlot)
        {
            if (NetworkManager.Singleton.LocalClientId != clientId)
                return;

            PerformLocalTransfer(roomId, cellIndex, memberSlot);
        }

        private void PerformLocalTransfer(ulong roomId, int cellIndex, int memberSlot)
        {
            // A new join supersedes any in-progress return.
            m_IsReturning = false;
            m_PendingTransferRoomId = roomId;

            if (m_TransferCoroutine != null)
                StopCoroutine(m_TransferCoroutine);

            m_TransferCoroutine = StartCoroutine(TransferToPrivateRoomCoroutine(roomId, cellIndex, memberSlot));
        }

        IEnumerator TransferToPrivateRoomCoroutine(ulong roomId, int cellIndex, int memberSlot)
        {
            PrivateRoomPodiumCompatibility.CleanupStateBeforeTransfer();
            PrivateRoomLocalPlayerSupport.PrepareForPrivateRoom();

            if (TryGetLocalPlayerPosition(out Vector3 currentPosition))
            {
                m_MainRoomReturnPosition = currentPosition;
                m_HasSavedReturnPosition = true;
            }

            if (!m_PrivateRoomSceneLoaded)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("Loading Private Room...", 2f);

                CaptureMainRenderSettings();

                AsyncOperation loadOperation = SceneManager.LoadSceneAsync(m_PrivateRoomSceneName, LoadSceneMode.Additive);
                if (loadOperation == null)
                {
                    Debug.LogError($"[PrivateRoomService] Failed to load scene '{m_PrivateRoomSceneName}'. Add it to Build Settings.");
                    NotifyTransferFailed("Private room scene is missing from Build Settings.");
                    m_TransferCoroutine = null;
                    yield break;
                }

                while (!loadOperation.isDone)
                    yield return null;

                Scene privateRoomScene = SceneManager.GetSceneByName(m_PrivateRoomSceneName);
                if (!privateRoomScene.IsValid() || !privateRoomScene.isLoaded)
                {
                    Debug.LogError($"[PrivateRoomService] Scene '{m_PrivateRoomSceneName}' did not load correctly.");
                    NotifyTransferFailed("Private room scene failed to load.");
                    m_TransferCoroutine = null;
                    yield break;
                }

                DisableConflictingSceneObjects(privateRoomScene);
                HideMainSceneEnvironment();
                SceneManager.SetActiveScene(privateRoomScene);
                m_PrivateRoomSceneLoaded = true;
            }

            // Always reconcile this room's geometry to its unique world offset, by moving the delta
            // from the currently-applied offset. Doing this every transfer (not only on first load)
            // keeps positioning correct even if a previous transfer was interrupted mid-setup.
            // Every client in the SAME room derives the same offset from the room's cell index, so
            // their networked avatars line up; different rooms never share a cell, so they never overlap.
            Scene roomScene = SceneManager.GetSceneByName(m_PrivateRoomSceneName);
            if (roomScene.IsValid() && roomScene.isLoaded)
            {
                Vector3 desiredOffset = GetRoomWorldOffset(cellIndex);
                Vector3 delta = desiredOffset - m_CurrentRoomOffset;
                if (delta != Vector3.zero)
                    RelocatePrivateRoomScene(roomScene, delta);
                m_CurrentRoomOffset = desiredOffset;
            }

            // Commit check: if a kick/removal arrived while the scene was loading, m_PendingTransferRoomId
            // was cleared. Abort instead of landing in a room we were removed from, restoring the main scene.
            if (m_PendingTransferRoomId != roomId)
            {
                Debug.Log("[PrivateRoomService] Transfer aborted before commit (membership removed during load).");
                m_TransferCoroutine = null;
                ReturnToMainRoom();
                yield break;
            }

            CurrentPrivateRoomId = roomId;

            Vector3 spawnPosition = GetPrivateRoomSpawnPosition() + GetMemberSlotOffset(memberSlot);
            if (!TryTeleportLocalRigTo(spawnPosition))
            {
                Debug.LogWarning("[PrivateRoomService] Could not find XROrigin to move into the private room.");
                NotifyTransferFailed("Private room loaded, but the player rig was not found.");
                // We already hid the main scene and set CurrentPrivateRoomId; fully tear down so the
                // player isn't stranded in a half-transferred state with the main scene hidden.
                m_TransferCoroutine = null;
                ReturnToMainRoom();
                yield break;
            }

            yield return null;
            TryTeleportLocalRigTo(spawnPosition);
            PrivateRoomLocalPlayerSupport.EnsureLocomotionEnabled();
            PrivateRoomLocalPlayerSupport.EnsureLocalAvatarVisible();

            var voice = UnityEngine.Object.FindAnyObjectByType<VoiceChatManager>();
            if (voice != null)
                voice.JoinPrivateChannel(roomId.ToString());

            OnJoinedPrivateRoom?.Invoke(roomId);

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("Entering Private Room...", 2f);

            m_PendingTransferRoomId = 0;
            m_TransferCoroutine = null;
        }

        IEnumerator ReturnToMainRoomCoroutine()
        {
            if (m_PrivateRoomSceneLoaded)
            {
                AsyncOperation unloadOperation = SceneManager.UnloadSceneAsync(m_PrivateRoomSceneName);
                if (unloadOperation != null)
                {
                    while (!unloadOperation.isDone)
                        yield return null;
                }

                m_PrivateRoomSceneLoaded = false;
            }

            m_CurrentRoomOffset = Vector3.zero;

            ShowMainSceneEnvironment();

            if (m_MainScene.IsValid())
                SceneManager.SetActiveScene(m_MainScene);

            RestoreMainRenderSettings();

            CurrentPrivateRoomId = 0;

            Vector3 returnPosition = m_HasSavedReturnPosition ? m_MainRoomReturnPosition : Vector3.zero;
            m_HasSavedReturnPosition = false;

            if (!TryTeleportLocalRigTo(returnPosition))
                Debug.LogWarning("[PrivateRoomService] Could not find player rig when returning to the main room.");
            else
            {
                yield return null;
                TryTeleportLocalRigTo(returnPosition);
            }

            PrivateRoomLocalPlayerSupport.RestoreAfterLeavingPrivateRoom();

            var voice = UnityEngine.Object.FindAnyObjectByType<VoiceChatManager>();
            if (voice != null)
                voice.ReturnToMainChannel();

            OnLeftPrivateRoom?.Invoke();

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("Returning to Main Room", 2f);

            m_TransferCoroutine = null;
            m_IsReturning = false;
        }

        // --- Per-room / per-member positioning -------------------------------------------------

        /// <summary>
        /// Deterministic, far-from-origin world offset unique to a room's grid cell. Cells are unique
        /// among active rooms, so two live rooms can never resolve to the same offset (unlike hashing
        /// the random room id, which collides).
        /// </summary>
        Vector3 GetRoomWorldOffset(int cellIndex)
        {
            int gridWidth = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1, m_MaxConcurrentRooms))));
            int gx = cellIndex % gridWidth;
            int gz = cellIndex / gridWidth;
            return m_PrivateRoomLocation + new Vector3(gx * m_RoomSpacing, 0f, gz * m_RoomSpacing);
        }

        /// <summary>Ring offset (by member slot) so members of a room don't all spawn on one point.</summary>
        Vector3 GetMemberSlotOffset(int memberSlot)
        {
            int slot = ((memberSlot % MAX_ROOM_SIZE) + MAX_ROOM_SIZE) % MAX_ROOM_SIZE;
            float angle = slot * (360f / MAX_ROOM_SIZE) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * m_MemberSpacingRadius;
        }

        /// <summary>Owner-only: pick the lowest grid cell not used by any currently-active room.</summary>
        int AssignRoomCell()
        {
            for (int cell = 0; cell < Mathf.Max(1, m_MaxConcurrentRooms); cell++)
            {
                bool taken = false;
                for (int i = 0; i < m_ActiveRooms.Count; i++)
                {
                    if (m_ActiveRooms[i].IsActive && m_ActiveRooms[i].CellIndex == cell)
                    {
                        taken = true;
                        break;
                    }
                }

                if (!taken)
                    return cell;
            }

            return 0;
        }

        static void RelocatePrivateRoomScene(Scene scene, Vector3 offset)
        {
            if (!scene.IsValid() || offset == Vector3.zero)
                return;

            foreach (GameObject rootObject in scene.GetRootGameObjects())
                rootObject.transform.position += offset;
        }

        void HideMainSceneEnvironment()
        {
            m_HiddenMainSceneRoots.Clear();
            m_MainScene = ResolveMainScene();

            if (!m_MainScene.IsValid())
                return;

            foreach (GameObject rootObject in m_MainScene.GetRootGameObjects())
            {
                if (ShouldKeepRootActiveDuringPrivateRoom(rootObject))
                    continue;

                if (!rootObject.activeSelf)
                    continue;

                rootObject.SetActive(false);
                m_HiddenMainSceneRoots.Add(rootObject);
            }
        }

        void CaptureMainRenderSettings()
        {
            if (m_HasSavedRenderSettings)
                return;

            m_SavedSkybox = RenderSettings.skybox;
            m_SavedAmbientLight = RenderSettings.ambientLight;
            m_SavedAmbientIntensity = RenderSettings.ambientIntensity;
            m_HasSavedRenderSettings = true;
        }

        void RestoreMainRenderSettings()
        {
            if (!m_HasSavedRenderSettings)
                return;

            EnvironmentManager environmentManager = UnityEngine.Object.FindFirstObjectByType<EnvironmentManager>();
            if (environmentManager != null)
            {
                environmentManager.RefreshCurrentEnvironmentVisuals();
            }
            else
            {
                if (m_SavedSkybox != null)
                    RenderSettings.skybox = m_SavedSkybox;

                RenderSettings.ambientLight = m_SavedAmbientLight;
                RenderSettings.ambientIntensity = m_SavedAmbientIntensity;
                DynamicGI.UpdateEnvironment();
            }

            m_HasSavedRenderSettings = false;
        }

        void ShowMainSceneEnvironment()
        {
            for (int i = 0; i < m_HiddenMainSceneRoots.Count; i++)
            {
                if (m_HiddenMainSceneRoots[i] != null)
                    m_HiddenMainSceneRoots[i].SetActive(true);
            }

            m_HiddenMainSceneRoots.Clear();
        }

        Scene ResolveMainScene()
        {
            Scene namedScene = SceneManager.GetSceneByName(m_MainSceneName);
            if (namedScene.IsValid())
                return namedScene;

            Scene privateRoomScene = SceneManager.GetSceneByName(m_PrivateRoomSceneName);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded || scene == privateRoomScene)
                    continue;

                return scene;
            }

            return SceneManager.GetActiveScene();
        }

        static bool ShouldKeepRootActiveDuringPrivateRoom(GameObject rootObject)
        {
            if (rootObject == null)
                return true;

            if (rootObject.GetComponentInChildren<NetworkManager>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<XROrigin>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<PrivateRoomService>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<PrivateRoomInviteService>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<QuickMenuManager>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<PrivateRoomUIController>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<VoiceChatManager>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<XRINetworkGameManager>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<OfflinePlayerAvatar>(true) != null)
                return true;

            if (rootObject.GetComponentInChildren<XRINetworkPlayer>(true) != null)
                return true;

            // Keep the world canvas so floating name tags stay visible inside the private room.
            if (rootObject.GetComponentInChildren<WorldCanvas>(true) != null)
                return true;

            return false;
        }

        static void DisableConflictingSceneObjects(Scene scene)
        {
            if (!scene.IsValid())
                return;

            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                foreach (Camera camera in rootObject.GetComponentsInChildren<Camera>(true))
                    camera.enabled = false;

                foreach (AudioListener listener in rootObject.GetComponentsInChildren<AudioListener>(true))
                    listener.enabled = false;
            }
        }

        Vector3 GetPrivateRoomSpawnPosition()
        {
            Scene privateRoomScene = SceneManager.GetSceneByName(m_PrivateRoomSceneName);
            if (privateRoomScene.IsValid() && privateRoomScene.isLoaded)
            {
                Transform roomTransform = FindRoomTransform(privateRoomScene);
                if (roomTransform != null)
                {
                    Renderer[] renderers = roomTransform.GetComponentsInChildren<Renderer>();
                    if (renderers.Length > 0)
                    {
                        Bounds bounds = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++)
                            bounds.Encapsulate(renderers[i].bounds);

                        return new Vector3(bounds.center.x, bounds.min.y + m_PrivateRoomSpawnOffset.y, bounds.center.z);
                    }

                    return roomTransform.position + m_PrivateRoomSpawnOffset;
                }
            }

            // Fallback: the room's world offset plus the configured spawn offset.
            return m_CurrentRoomOffset + m_PrivateRoomSpawnOffset;
        }

        static Transform FindRoomTransform(Scene scene)
        {
            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                if (rootObject.name == "Room")
                    return rootObject.transform;

                Transform directChild = rootObject.transform.Find("Room");
                if (directChild != null)
                    return directChild;

                foreach (Transform child in rootObject.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name == "Room")
                        return child;
                }
            }

            return null;
        }

        static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            XROrigin xrOrigin = PrivateRoomLocalPlayerSupport.FindLocalXROrigin();
            if (xrOrigin != null)
            {
                position = xrOrigin.transform.position;
                return true;
            }

            if (Camera.main != null)
            {
                position = Camera.main.transform.root.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        static bool TryTeleportLocalRigTo(Vector3 worldPosition)
        {
            PrivateRoomLocalPlayerSupport.ReleaseFromChairIfNeeded();

            XROrigin xrOrigin = PrivateRoomLocalPlayerSupport.FindLocalXROrigin();
            if (xrOrigin == null)
                return false;

            TeleportationProvider teleportProvider = xrOrigin.GetComponentInChildren<TeleportationProvider>(true);
            if (teleportProvider != null)
            {
                teleportProvider.QueueTeleportRequest(new TeleportRequest
                {
                    destinationPosition = worldPosition,
                    destinationRotation = xrOrigin.transform.rotation
                });
                return true;
            }

            CharacterController characterController = xrOrigin.GetComponent<CharacterController>();
            if (characterController != null)
                characterController.enabled = false;

            xrOrigin.transform.position = worldPosition;

            if (characterController != null)
                characterController.enabled = true;

            return true;
        }

        public void LeaveCurrentRoom()
        {
            if (!IsInPrivateRoom)
                return;

            ulong roomId = CurrentPrivateRoomId;

            if (TryUseNetworkPath(out _))
            {
                if (HasStateAuthority)
                    HandleLeaveRoomMembership(roomId, NetworkManager.Singleton.LocalClientId);
                else
                    LeaveRoomOwnerRpc(NetworkManager.Singleton.LocalClientId, roomId);
            }

            ReturnToMainRoom();
        }

        [Rpc(SendTo.Owner)]
        private void LeaveRoomOwnerRpc(ulong clientId, ulong roomId, RpcParams rpcParams = default)
        {
            // Authority check: a client may only remove its own membership.
            if (clientId != rpcParams.Receive.SenderClientId)
            {
                Debug.LogWarning($"[PrivateRoomService] Rejected leave: caller {rpcParams.Receive.SenderClientId} claimed client {clientId}.");
                return;
            }

            HandleLeaveRoomMembership(roomId, clientId);
        }

        private void HandleLeaveRoomMembership(ulong roomId, ulong clientId)
        {
            for (int i = m_RoomMembers.Count - 1; i >= 0; i--)
            {
                if (m_RoomMembers[i].ClientId == clientId && m_RoomMembers[i].RoomId == roomId)
                {
                    m_RoomMembers.RemoveAt(i);
                    break;
                }
            }

            TryDeactivateEmptyRoom(roomId);
        }

        // --- Owner controls: kick a member -----------------------------------------------------

        /// <summary>Requests that a member be removed from a room. Only the room's owner may kick.</summary>
        public void KickMember(ulong roomId, ulong targetClientId)
        {
            if (HasStateAuthority)
                HandleKickMember(roomId, targetClientId, NetworkManager.Singleton.LocalClientId);
            else
                KickMemberOwnerRpc(roomId, targetClientId);
        }

        [Rpc(SendTo.Owner)]
        private void KickMemberOwnerRpc(ulong roomId, ulong targetClientId, RpcParams rpcParams = default)
        {
            HandleKickMember(roomId, targetClientId, rpcParams.Receive.SenderClientId);
        }

        private void HandleKickMember(ulong roomId, ulong targetClientId, ulong requesterId)
        {
            if (!TryGetRoomState(roomId, out PrivateRoomState roomState))
                return;

            // Only the room owner can kick, and the owner can't kick themselves.
            if (roomState.OwnerId != requesterId || targetClientId == roomState.OwnerId)
                return;

            bool removed = false;
            for (int i = m_RoomMembers.Count - 1; i >= 0; i--)
            {
                if (m_RoomMembers[i].ClientId == targetClientId && m_RoomMembers[i].RoomId == roomId)
                {
                    m_RoomMembers.RemoveAt(i);
                    removed = true;
                    break;
                }
            }

            if (!removed)
                return;

            ForceReturnClientRpc(targetClientId);
            TryDeactivateEmptyRoom(roomId);
        }

        [ClientRpc]
        private void ForceReturnClientRpc(ulong targetClientId)
        {
            if (NetworkManager.Singleton.LocalClientId != targetClientId)
                return;

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("You were removed from the private room.", 4f);

            ReturnToMainRoom();
        }

        private void ReturnToMainRoom()
        {
            if (m_IsReturning)
                return;

            if (!IsInPrivateRoom && !m_PrivateRoomSceneLoaded)
                return;

            m_IsReturning = true;
            m_PendingTransferRoomId = 0;

            if (m_TransferCoroutine != null)
                StopCoroutine(m_TransferCoroutine);

            m_TransferCoroutine = StartCoroutine(ReturnToMainRoomCoroutine());
        }

        /// <summary>
        /// Local-only teardown for when the player disconnects while inside a private room (no RPCs
        /// possible). Restores the main scene/environment so a later reconnect is clean.
        /// </summary>
        public void ForceLocalExitIfActive()
        {
            if (!IsInPrivateRoom && !m_PrivateRoomSceneLoaded)
                return;

            ReturnToMainRoom();
        }

        // --- Public read helpers (used by UI) --------------------------------------------------

        public bool IsClientInPrivateRoom(ulong clientId) => IsClientInAnyRoom(clientId);

        /// <summary>
        /// The room "context" a client currently belongs to: their private room id, or 0 for the
        /// main room. Used by avatar/chat isolation so each context only sees its own members.
        /// </summary>
        public ulong GetClientRoomId(ulong clientId)
        {
            if (m_RoomMembers == null)
                return 0;

            for (int i = 0; i < m_RoomMembers.Count; i++)
            {
                if (m_RoomMembers[i].ClientId == clientId)
                    return m_RoomMembers[i].RoomId;
            }
            return 0;
        }

        public bool TryGetRoomOwner(ulong roomId, out ulong ownerId)
        {
            if (TryGetRoomState(roomId, out PrivateRoomState state))
            {
                ownerId = state.OwnerId;
                return true;
            }

            ownerId = 0;
            return false;
        }

        /// <summary>Fills <paramref name="results"/> with the client ids of everyone in a room.</summary>
        public void GetRoomMembers(ulong roomId, List<ulong> results)
        {
            if (results == null)
                return;

            results.Clear();
            for (int i = 0; i < m_RoomMembers.Count; i++)
            {
                if (m_RoomMembers[i].RoomId == roomId)
                    results.Add(m_RoomMembers[i].ClientId);
            }
        }

        private ulong GenerateUniqueRoomId()
        {
            ulong roomId;
            do
            {
                roomId = (ulong)UnityEngine.Random.Range(1, int.MaxValue);
            }
            while (TryGetRoomState(roomId, out _));

            return roomId;
        }

        private bool TryGetRoomState(ulong roomId, out PrivateRoomState roomState)
        {
            for (int i = 0; i < m_ActiveRooms.Count; i++)
            {
                if (m_ActiveRooms[i].RoomId == roomId)
                {
                    roomState = m_ActiveRooms[i];
                    return true;
                }
            }

            roomState = default;
            return false;
        }

        private int CountActiveRooms()
        {
            int count = 0;
            for (int i = 0; i < m_ActiveRooms.Count; i++)
            {
                if (m_ActiveRooms[i].IsActive)
                    count++;
            }

            return count;
        }

        private bool IsClientInAnyRoom(ulong clientId)
        {
            for (int i = 0; i < m_RoomMembers.Count; i++)
            {
                if (m_RoomMembers[i].ClientId == clientId)
                    return true;
            }

            return false;
        }

        private int GetRoomMemberCount(ulong roomId)
        {
            int count = 0;
            for (int i = 0; i < m_RoomMembers.Count; i++)
            {
                if (m_RoomMembers[i].RoomId == roomId)
                    count++;
            }

            return count;
        }

        private void TryDeactivateEmptyRoom(ulong roomId)
        {
            if (GetRoomMemberCount(roomId) > 0)
                return;

            for (int i = 0; i < m_ActiveRooms.Count; i++)
            {
                if (m_ActiveRooms[i].RoomId != roomId)
                    continue;

                var roomState = m_ActiveRooms[i];
                roomState.IsActive = false;
                m_ActiveRooms[i] = roomState;
                break;
            }

            // Clean up any dangling invites for the now-closed room.
            if (PrivateRoomInviteService.Instance != null)
                PrivateRoomInviteService.Instance.RemoveInvitesForRoom(roomId);
        }
    }
}
