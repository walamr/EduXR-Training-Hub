using System;
using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Service for managing private room invites.
    /// </summary>
    public class PrivateRoomInviteService : NetworkBehaviour
    {
        public static PrivateRoomInviteService Instance { get; private set; }

        [SerializeField, Tooltip("Seconds before an unanswered invite automatically expires.")]
        private float m_InviteExpirySeconds = 60f;

        private NetworkList<PrivateRoomInvite> m_ActiveInvites;

        public NetworkList<PrivateRoomInvite> ActiveInvites => m_ActiveInvites;

        /// <summary>Raised on the target client when a new invite addressed to them arrives.</summary>
        public Action<PrivateRoomInvite> OnInviteReceived;

        /// <summary>Raised on every client when an invite is removed (resolved, expired, or cancelled).</summary>
        public Action<ulong> OnInviteWithdrawn;

        private float m_ExpiryCheckTimer;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            m_ActiveInvites = new NetworkList<PrivateRoomInvite>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        }

        bool HasStateAuthority => IsOwner;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_ActiveInvites.OnListChanged += OnInvitesChanged;
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            m_ActiveInvites.OnListChanged -= OnInvitesChanged;
        }

        private void Update()
        {
            // Owner-only: periodically prune expired pending invites.
            if (!HasStateAuthority || m_ActiveInvites == null)
                return;

            m_ExpiryCheckTimer += Time.unscaledDeltaTime;
            if (m_ExpiryCheckTimer < 1f)
                return;
            m_ExpiryCheckTimer = 0f;

            double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
            for (int i = m_ActiveInvites.Count - 1; i >= 0; i--)
            {
                var invite = m_ActiveInvites[i];
                bool pending = !invite.IsAccepted && !invite.IsDeclined;
                if (pending && m_InviteExpirySeconds > 0f && now - invite.CreatedServerTime > m_InviteExpirySeconds)
                {
                    m_ActiveInvites.RemoveAt(i);
                }
            }
        }

        private void OnInvitesChanged(NetworkListEvent<PrivateRoomInvite> changeEvent)
        {
            if (changeEvent.Type == NetworkListEvent<PrivateRoomInvite>.EventType.Add)
            {
                var invite = changeEvent.Value;
                if (invite.TargetId == NetworkManager.Singleton.LocalClientId && !invite.IsAccepted && !invite.IsDeclined)
                {
                    OnInviteReceived?.Invoke(invite);
                }
                return;
            }

            if (changeEvent.Type == NetworkListEvent<PrivateRoomInvite>.EventType.Remove ||
                changeEvent.Type == NetworkListEvent<PrivateRoomInvite>.EventType.RemoveAt)
            {
                // changeEvent.Value holds the removed item for Remove/RemoveAt in NGO.
                OnInviteWithdrawn?.Invoke(changeEvent.Value.InviteId);
            }
        }

        public void SendInvite(ulong senderId, ulong targetId, ulong roomId)
        {
            if (HasStateAuthority)
                HandleSendInvite(senderId, targetId, roomId, senderId);
            else
                SendInviteOwnerRpc(senderId, targetId, roomId);
        }

        [Rpc(SendTo.Owner)]
        private void SendInviteOwnerRpc(ulong senderId, ulong targetId, ulong roomId, RpcParams rpcParams = default)
        {
            HandleSendInvite(senderId, targetId, roomId, rpcParams.Receive.SenderClientId);
        }

        private void HandleSendInvite(ulong senderId, ulong targetId, ulong roomId, ulong callerId)
        {
            // Authority check: the caller may only send invites as themselves.
            if (senderId != callerId)
            {
                Debug.LogWarning($"[PrivateRoomInviteService] Rejected spoofed invite: caller {callerId} claimed sender {senderId}.");
                return;
            }

            if (targetId == senderId)
                return;

            var invite = new PrivateRoomInvite
            {
                InviteId = (ulong)UnityEngine.Random.Range(1, int.MaxValue),
                RoomId = roomId,
                SenderId = senderId,
                TargetId = targetId,
                IsAccepted = false,
                IsDeclined = false,
                CreatedServerTime = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d
            };

            m_ActiveInvites.Add(invite);
        }

        public void AcceptInvite(ulong inviteId)
        {
            if (HasStateAuthority)
                HandleRespondToInvite(inviteId, true, NetworkManager.Singleton.LocalClientId);
            else
                RespondToInviteOwnerRpc(inviteId, true);
        }

        public void DeclineInvite(ulong inviteId)
        {
            if (HasStateAuthority)
                HandleRespondToInvite(inviteId, false, NetworkManager.Singleton.LocalClientId);
            else
                RespondToInviteOwnerRpc(inviteId, false);
        }

        [Rpc(SendTo.Owner)]
        private void RespondToInviteOwnerRpc(ulong inviteId, bool accept, RpcParams rpcParams = default)
        {
            HandleRespondToInvite(inviteId, accept, rpcParams.Receive.SenderClientId);
        }

        private void HandleRespondToInvite(ulong inviteId, bool accept, ulong responderId)
        {
            for (int i = 0; i < m_ActiveInvites.Count; i++)
            {
                if (m_ActiveInvites[i].InviteId != inviteId)
                    continue;

                var invite = m_ActiveInvites[i];
                if (invite.IsAccepted || invite.IsDeclined)
                    return;

                // Authority check: only the invite's target may respond to it.
                if (invite.TargetId != responderId)
                {
                    Debug.LogWarning($"[PrivateRoomInviteService] Rejected invite response: {responderId} is not the target of invite {inviteId}.");
                    return;
                }

                // Resolve and remove the invite so the list does not grow unbounded.
                m_ActiveInvites.RemoveAt(i);

                if (accept && PrivateRoomService.Instance != null)
                {
                    PrivateRoomService.Instance.JoinRoom(invite.TargetId, invite.RoomId);
                }

                return;
            }

            // Invite not found (expired/withdrawn between display and response). Tell the accepter so
            // the action doesn't silently dead-end.
            if (accept)
                NotifyInviteUnavailable(responderId);
        }

        void NotifyInviteUnavailable(ulong responderId)
        {
            if (NetworkManager.Singleton != null && responderId == NetworkManager.Singleton.LocalClientId)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText("That invite is no longer available.", 4f);
                return;
            }

            NotifyInviteUnavailableClientRpc(responderId);
        }

        [ClientRpc]
        void NotifyInviteUnavailableClientRpc(ulong responderId)
        {
            if (NetworkManager.Singleton.LocalClientId != responderId)
                return;

            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("That invite is no longer available.", 4f);
        }

        /// <summary>Owner-only: removes every invite that targets or was sent by the given client.</summary>
        public void RemoveInvitesForClient(ulong clientId)
        {
            if (!HasStateAuthority || m_ActiveInvites == null)
                return;

            for (int i = m_ActiveInvites.Count - 1; i >= 0; i--)
            {
                if (m_ActiveInvites[i].TargetId == clientId || m_ActiveInvites[i].SenderId == clientId)
                    m_ActiveInvites.RemoveAt(i);
            }
        }

        /// <summary>Owner-only: removes every invite belonging to a room (e.g. when it closes).</summary>
        public void RemoveInvitesForRoom(ulong roomId)
        {
            if (!HasStateAuthority || m_ActiveInvites == null)
                return;

            for (int i = m_ActiveInvites.Count - 1; i >= 0; i--)
            {
                if (m_ActiveInvites[i].RoomId == roomId)
                    m_ActiveInvites.RemoveAt(i);
            }
        }
    }
}
