using UnityEngine;
using XRMultiplayer;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Blocks main-room menus while the local player is inside a private room.
    /// </summary>
    public static class PrivateRoomMenuLock
    {
        static bool s_IsLocked;
        static GameObject s_ExemptFloatingPanel;

        public static bool IsLocked => s_IsLocked;

        public static bool IsPanelAllowed(GameObject panel)
        {
            if (!s_IsLocked || panel == null)
                return !s_IsLocked;

            return s_ExemptFloatingPanel != null && panel == s_ExemptFloatingPanel;
        }

        public static void Lock(GameObject exemptFloatingPanel)
        {
            s_IsLocked = true;
            s_ExemptFloatingPanel = exemptFloatingPanel;
            ApplyLockState();
        }

        public static void Unlock()
        {
            s_IsLocked = false;
            s_ExemptFloatingPanel = null;
        }

        static void ApplyLockState()
        {
            if (QuickMenuManager.Instance != null)
                QuickMenuManager.Instance.EnforcePrivateRoomMenuLock(s_ExemptFloatingPanel);

            PlayerOptions playerOptions = Object.FindFirstObjectByType<PlayerOptions>(FindObjectsInactive.Include);
            if (playerOptions != null)
                playerOptions.CloseMenuIfOpen();
        }
    }
}
