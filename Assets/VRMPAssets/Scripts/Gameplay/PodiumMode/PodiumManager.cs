using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Core controller for Podium Mode (Distributed Authority compatible).
    /// 
    /// In DA, scene-placed NetworkObjects are often owned by the session owner.
    /// Non-owners request changes via SendTo.Owner RPCs; the owner broadcasts SendTo.Everyone.
    /// </summary>
    public class PodiumManager : NetworkBehaviour
    {
        public static PodiumManager Instance { get; private set; }

        [Header("Podium Spot")]
        [Tooltip("Transform where the host is teleported when Podium Mode activates.")]
        [SerializeField] private Transform m_PodiumSpot;

        [Header("Debug")]
        [SerializeField] private bool m_DebugMode = true;

        // ── State (synced via RPCs, tracked locally on every client) ──
        private bool m_IsPodiumActive = false;
        private ulong m_HostClientId = 0;
        private readonly List<ulong> m_RaisedHandsList = new();
        private readonly HashSet<ulong> m_UnmutedByHost = new();
        private bool m_LocalHandRaised = false;
        // Remembers whether the local player was already self-muted before Podium Mode
        // force-muted them, so deactivation can restore that state instead of blanket-unmuting.
        private bool m_PrePodiumLocalMuted = false;

        // ── Public Accessors ──
        public bool IsPodiumActive => m_IsPodiumActive;
        public IReadOnlyList<ulong> RaisedHands => m_RaisedHandsList;
        public bool IsLocalHandRaised => m_LocalHandRaised;

        // ── Events ──
        public System.Action<bool> OnPodiumStateChanged;
        public System.Action<ulong> OnPlayerRaisedHand;
        public System.Action<ulong> OnPlayerLoweredHand;
        public System.Action<ulong> OnPlayerUnmutedByHost;

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged += OnPlayerStateChanged;
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            }

            // Late-join safety net: pull the current podium state from the owner in case we
            // spawned after the host's join broadcast was already sent (the broadcast is fire-and-forget).
            if (!IsOwner)
                RequestStateOnJoinOwnerRpc();

            LogDebug($"OnNetworkSpawn: IsSpawned={IsSpawned}, LocalClientId={NetworkManager.Singleton.LocalClientId}");
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();

            if (XRINetworkGameManager.Instance != null)
            {
                XRINetworkGameManager.Instance.OnPlayerStateChanged -= OnPlayerStateChanged;
                XRINetworkGameManager.Instance.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            }

            if (Instance == this) Instance = null;
        }

        #endregion

        #region Host API

        public void ActivatePodiumMode()
        {
            if (!IsSessionOwnerLocal())
            {
                LogDebug("ActivatePodiumMode: NOT session owner, aborting.");
                return;
            }
            if (m_IsPodiumActive)
            {
                LogDebug("ActivatePodiumMode: already active, aborting.");
                return;
            }
            if (!IsSpawned)
            {
                LogDebug("ActivatePodiumMode: NOT SPAWNED, aborting.");
                return;
            }

            ulong hostId = NetworkManager.Singleton.LocalClientId;
            LogDebug($"ActivatePodiumMode: Activating with hostId={hostId}");

            if (IsOwner)
                ActivatePodiumRpc(hostId);
            else
                RequestActivatePodiumOwnerRpc();
        }

        public void DeactivatePodiumMode()
        {
            if (!IsSessionOwnerLocal()) return;
            if (!m_IsPodiumActive) return;
            if (!IsSpawned) return;

            LogDebug("DeactivatePodiumMode: Deactivating...");
            if (IsOwner)
                DeactivatePodiumRpc();
            else
                RequestDeactivatePodiumOwnerRpc();
        }

        public void UnmutePlayer(ulong clientId)
        {
            if (!IsSessionOwnerLocal() || !m_IsPodiumActive || !IsSpawned) return;
            LogDebug($"UnmutePlayer: {clientId}");
            if (IsOwner)
                UnmutePlayerRpc(clientId);
            else
                RequestUnmutePlayerOwnerRpc(clientId);
        }

        public void RemutePlayer(ulong clientId)
        {
            if (!IsSessionOwnerLocal() || !m_IsPodiumActive || !IsSpawned) return;
            LogDebug($"RemutePlayer: {clientId}");
            if (IsOwner)
                RemutePlayerRpc(clientId);
            else
                RequestRemutePlayerOwnerRpc(clientId);
        }

        #endregion

        #region Client API

        public void ToggleRaiseHand()
        {
            if (!m_IsPodiumActive) return;
            if (IsSessionOwnerLocal()) return;
            if (!IsSpawned)
            {
                LogDebug("ToggleRaiseHand: NOT SPAWNED, aborting.");
                return;
            }

            m_LocalHandRaised = !m_LocalHandRaised;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            LogDebug($"ToggleRaiseHand: client={localId}, raised={m_LocalHandRaised}");

            if (IsOwner)
                RaiseHandRpc(localId, m_LocalHandRaised);
            else
                RequestRaiseHandOwnerRpc(m_LocalHandRaised);
        }

        #endregion

        #region RPCs (Owner relay + Everyone broadcast — DA compatible)

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestActivatePodiumOwnerRpc(RpcParams rpcParams = default)
        {
            // Only the session owner may activate. Derive the host id from the validated
            // sender rather than trusting a client-supplied argument.
            if (!IsSenderSessionOwner(rpcParams.Receive.SenderClientId)) return;
            ActivatePodiumRpc(rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDeactivatePodiumOwnerRpc(RpcParams rpcParams = default)
        {
            if (!IsSenderSessionOwner(rpcParams.Receive.SenderClientId)) return;
            DeactivatePodiumRpc();
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestRaiseHandOwnerRpc(bool raised, RpcParams rpcParams = default)
        {
            // A client may only raise/lower its own hand — use the verified sender id.
            RaiseHandRpc(rpcParams.Receive.SenderClientId, raised);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestUnmutePlayerOwnerRpc(ulong targetClientId, RpcParams rpcParams = default)
        {
            if (!IsSenderSessionOwner(rpcParams.Receive.SenderClientId)) return;
            UnmutePlayerRpc(targetClientId);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestRemutePlayerOwnerRpc(ulong targetClientId, RpcParams rpcParams = default)
        {
            if (!IsSenderSessionOwner(rpcParams.Receive.SenderClientId)) return;
            RemutePlayerRpc(targetClientId);
        }

        /// <summary>Late joiner asks the owner to (re)broadcast the current podium state.</summary>
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestStateOnJoinOwnerRpc(RpcParams rpcParams = default)
        {
            if (!m_IsPodiumActive) return;
            SyncStateForLateJoinerRpc(m_HostClientId, true, m_RaisedHandsList.ToArray());
        }

        [Rpc(SendTo.Everyone)]
        private void ActivatePodiumRpc(ulong hostClientId)
        {
            LogDebug($"ActivatePodiumRpc: hostId={hostClientId}");

            m_IsPodiumActive = true;
            m_HostClientId = hostClientId;
            m_RaisedHandsList.Clear();
            m_UnmutedByHost.Clear();
            m_LocalHandRaised = false;
            m_PrePodiumLocalMuted = false;

            OnPodiumStateChanged?.Invoke(true);

            ulong localId = NetworkManager.Singleton.LocalClientId;
            if (localId == hostClientId)
            {
                TeleportHostToPodium();
            }
            else
            {
                StartCoroutine(SeatAndLockLocalPlayer());
            }
        }

        [Rpc(SendTo.Everyone)]
        private void DeactivatePodiumRpc()
        {
            LogDebug("DeactivatePodiumRpc: releasing.");

            m_IsPodiumActive = false;
            m_RaisedHandsList.Clear();
            m_UnmutedByHost.Clear();
            m_LocalHandRaised = false;

            OnPodiumStateChanged?.Invoke(false);

            if (ChairManager.Instance != null)
                ChairManager.Instance.SetPodiumLock(false);

            var voiceChat = FindFirstObjectByType<VoiceChatManager>();
            if (voiceChat != null)
            {
                // Release the podium force-mute, then restore the player's own pre-podium
                // mute choice instead of blanket-unmuting someone who muted themselves earlier.
                voiceChat.ForceLocalMute(false);
                if (m_PrePodiumLocalMuted)
                    voiceChat.ToggleSelfMute(true, true);
            }
            m_PrePodiumLocalMuted = false;

            RaiseHandIndicator.DestroyAll();
        }

        [Rpc(SendTo.Everyone)]
        private void RaiseHandRpc(ulong clientId, bool raised)
        {
            if (raised)
            {
                if (!m_RaisedHandsList.Contains(clientId))
                {
                    m_RaisedHandsList.Add(clientId);
                    LogDebug($"Hand raised: {clientId}");
                    OnPlayerRaisedHand?.Invoke(clientId);
                    NotifyHostHandRaised(clientId);
                    RaiseHandIndicator.ShowForPlayer(clientId);
                }
            }
            else
            {
                if (m_RaisedHandsList.Remove(clientId))
                {
                    LogDebug($"Hand lowered: {clientId}");
                    OnPlayerLoweredHand?.Invoke(clientId);
                    RaiseHandIndicator.HideForPlayer(clientId);
                }
            }
        }

        [Rpc(SendTo.Everyone)]
        private void UnmutePlayerRpc(ulong targetClientId)
        {
            m_UnmutedByHost.Add(targetClientId);

            if (NetworkManager.Singleton.LocalClientId == targetClientId)
            {
                LogDebug("I have been unmuted by the host!");
                var voiceChat = FindFirstObjectByType<VoiceChatManager>();
                if (voiceChat != null) voiceChat.ForceLocalMute(false);
                ShowNotification("<b>Host unmuted you</b> - you may speak.", 3f);
            }

            OnPlayerUnmutedByHost?.Invoke(targetClientId);
        }

        [Rpc(SendTo.Everyone)]
        private void RemutePlayerRpc(ulong targetClientId)
        {
            m_UnmutedByHost.Remove(targetClientId);

            if (NetworkManager.Singleton.LocalClientId == targetClientId)
            {
                LogDebug("Host re-muted me.");
                var voiceChat = FindFirstObjectByType<VoiceChatManager>();
                if (voiceChat != null) voiceChat.ForceLocalMute(true);
                ShowNotification("<b>Host muted you</b>", 2f);
            }
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestSyncStateForLateJoinerOwnerRpc(ulong hostId, bool active, ulong[] raisedHands, RpcParams rpcParams = default)
        {
            if (!IsSenderSessionOwner(rpcParams.Receive.SenderClientId)) return;
            SyncStateForLateJoinerRpc(hostId, active, raisedHands);
        }

        /// <summary>RPC to sync full state for late joiners.</summary>
        [Rpc(SendTo.Everyone)]
        private void SyncStateForLateJoinerRpc(ulong hostId, bool active, ulong[] raisedHands)
        {
            // Only apply if we're the late joiner (not already synced)
            if (m_IsPodiumActive) return;
            if (!active) return;

            LogDebug($"SyncStateForLateJoinerRpc: hostId={hostId}, active={active}, hands={raisedHands.Length}");

            m_IsPodiumActive = true;
            m_HostClientId = hostId;
            m_RaisedHandsList.Clear();
            m_RaisedHandsList.AddRange(raisedHands);

            OnPodiumStateChanged?.Invoke(true);

            ulong localId = NetworkManager.Singleton.LocalClientId;
            if (localId != hostId)
            {
                StartCoroutine(SeatAndLockLocalPlayer());
            }

            // Restore raise-hand indicators
            foreach (var id in raisedHands)
                RaiseHandIndicator.ShowForPlayer(id);
        }

        #endregion

        #region Local Client Actions

        private void TeleportHostToPodium()
        {
            if (m_PodiumSpot == null)
            {
                LogDebug("TeleportHostToPodium: No podium spot assigned!");
                return;
            }

            // If the host was sitting in a chair, release the lock and stand up first so the
            // XR Origin is unparented before we teleport (otherwise the teleport fights the chair).
            if (ChairManager.Instance != null)
            {
                ChairManager.Instance.SetPodiumLock(false);
                if (ChairManager.Instance.IsPlayerSeated)
                    ChairManager.Instance.StandUp();
            }

            var resetter = FindFirstObjectByType<CharacterResetter>();
            if (resetter != null)
            {
                var tp = resetter.GetComponentInChildren<
                    UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationProvider>();
                if (tp != null)
                {
                    var request = new UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportRequest
                    {
                        destinationPosition = m_PodiumSpot.position,
                        destinationRotation = m_PodiumSpot.rotation
                    };
                    tp.QueueTeleportRequest(request);
                    LogDebug($"TeleportHostToPodium: Teleported to {m_PodiumSpot.position}");
                    return;
                }
            }

            var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin != null)
            {
                xrOrigin.transform.position = m_PodiumSpot.position;
                xrOrigin.transform.rotation = m_PodiumSpot.rotation;
                LogDebug($"TeleportHostToPodium: Moved directly to {m_PodiumSpot.position}");
            }
        }

        private IEnumerator SeatAndLockLocalPlayer()
        {
            yield return new WaitForSeconds(0.5f);

            var chair = FindAvailableChair();
            if (chair != null)
            {
                LogDebug($"SeatAndLockLocalPlayer: Seating in {chair.name}");
                chair.ForceLocalPlayerSit();

                yield return new WaitForSeconds(0.5f);

                if (ChairManager.Instance != null)
                    ChairManager.Instance.SetPodiumLock(true);
            }
            else
            {
                // No free chair — the audience member still must be muted so they can't talk
                // over the host. Seating is best-effort; muting is mandatory.
                LogDebug("SeatAndLockLocalPlayer: No available chair — muting without seating.");
            }

            var voiceChat = FindFirstObjectByType<VoiceChatManager>();
            if (voiceChat != null)
            {
                m_PrePodiumLocalMuted = voiceChat.selfMuted.Value;
                voiceChat.ForceLocalMute(true);
            }

            ShowNotification(chair != null
                ? "<b>Podium Mode</b> - You are seated and muted."
                : "<b>Podium Mode</b> - You are muted.", 3f);
        }

        private SittableChair FindAvailableChair()
        {
            var chairs = FindObjectsByType<SittableChair>(FindObjectsSortMode.None);
            foreach (var chair in chairs)
                if (!chair.IsOccupied)
                    return chair;
            return null;
        }

        #endregion

        #region Event Handlers

        private void OnPlayerStateChanged(ulong playerId, bool connected)
        {
            if (!m_IsPodiumActive) return;

            if (connected)
            {
                LogDebug($"Player {playerId} joined during Podium Mode.");
                // Host sends full state sync for the late joiner
                if (IsSessionOwnerLocal())
                {
                    ulong[] raisedHands = m_RaisedHandsList.ToArray();
                    if (IsOwner)
                        SyncStateForLateJoinerRpc(m_HostClientId, true, raisedHands);
                    else
                        RequestSyncStateForLateJoinerOwnerRpc(m_HostClientId, true, raisedHands);
                }
            }
            else
            {
                // Player left — clean up
                if (m_RaisedHandsList.Remove(playerId))
                {
                    OnPlayerLoweredHand?.Invoke(playerId);
                    RaiseHandIndicator.HideForPlayer(playerId);
                }
                m_UnmutedByHost.Remove(playerId);
            }
        }

        private void OnSessionOwnerPromoted(ulong newSessionOwnerId)
        {
            LogDebug($"Session owner promoted to {newSessionOwnerId}. Podium active: {m_IsPodiumActive}");

            if (m_IsPodiumActive && IsSessionOwnerLocal())
            {
                LogDebug("New host ending Podium Mode after host migration.");
                if (IsOwner)
                    DeactivatePodiumRpc();
                else
                    RequestDeactivatePodiumOwnerRpc();
                ShowNotification("<b>Podium Mode ended</b> - host changed.", 3f);
            }
        }

        #endregion

        #region Helpers

        private bool IsSessionOwnerLocal()
        {
            return XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
        }

        private bool IsLocalPodiumHost()
        {
            if (NetworkManager.Singleton == null) return false;
            if (IsSessionOwnerLocal()) return true;
            // Guard on the active flag rather than a non-zero host id, since client id 0 is valid.
            return m_IsPodiumActive &&
                   NetworkManager.Singleton.LocalClientId == m_HostClientId;
        }

        /// <summary>True if the given client id belongs to the current session owner (the podium host).</summary>
        private bool IsSenderSessionOwner(ulong senderClientId)
        {
            return XRINetworkGameManager.Instance != null &&
                   XRINetworkGameManager.Instance.TryGetPlayerByID(senderClientId, out var player) &&
                   player.IsSessionOwner;
        }

        private void NotifyHostHandRaised(ulong clientId)
        {
            if (!IsLocalPodiumHost()) return;
            ShowNotification($"<b>{GetPlayerName(clientId)}</b> raised their hand", 4f);
        }

        private static void ShowNotification(string text, float duration = 3f)
        {
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText(text, duration);
        }

        public bool IsPlayerUnmutedByHost(ulong clientId)
        {
            return m_UnmutedByHost.Contains(clientId);
        }

        private string GetPlayerName(ulong clientId)
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(clientId, out var player))
                return player.displayName;
            return $"Player_{clientId}";
        }

        private void LogDebug(string msg)
        {
            if (m_DebugMode)
                Debug.Log($"<color=#FF6B35>[PodiumManager]</color> {msg}");
        }

        #endregion
    }
}
