using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Handles compatibility rules between Private Rooms and Podium Mode.
    /// </summary>
    public static class PrivateRoomPodiumCompatibility
    {
        /// <summary>
        /// Checks if a private room can be created or joined based on current podium state.
        /// </summary>
        public static bool CanEnterPrivateRoom()
        {
            // V1 Guard: Block entry if podium is active in the main room
            if (PodiumManager.Instance != null && PodiumManager.Instance.IsPodiumActive)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true when the local player is connected to a lobby session and podium rules allow private rooms.
        /// </summary>
        public static bool IsPrivateRoomAvailable(out string blockReason)
        {
            if (XRINetworkGameManager.Instance == null || !XRINetworkGameManager.Connected.Value)
            {
                blockReason = "Join a room session before using Private Room.";
                return false;
            }

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
            {
                blockReason = "Join a room session before using Private Room.";
                return false;
            }

            if (!CanEnterPrivateRoom())
            {
                blockReason = "Cannot enter a private room during Podium Mode.";
                return false;
            }

            blockReason = null;
            return true;
        }

        /// <summary>
        /// Cleans up any local podium state before transferring to a private room.
        /// </summary>
        public static void CleanupStateBeforeTransfer()
        {
            if (PodiumManager.Instance == null) return;

            // If local player has a raised hand, lower it
            if (PodiumManager.Instance.IsLocalHandRaised)
            {
                PodiumManager.Instance.ToggleRaiseHand();
            }

            // If local player is the host and podium is active, deactivate it
            // (Though CanEnterPrivateRoom should prevent this, it's a safe fallback)
            if (PodiumManager.Instance.IsPodiumActive && IsSessionOwnerLocal())
            {
                PodiumManager.Instance.DeactivatePodiumMode();
            }
        }

        private static bool IsSessionOwnerLocal()
        {
            return XRINetworkPlayer.LocalPlayer != null && XRINetworkPlayer.LocalPlayer.IsSessionOwner;
        }
    }
}
