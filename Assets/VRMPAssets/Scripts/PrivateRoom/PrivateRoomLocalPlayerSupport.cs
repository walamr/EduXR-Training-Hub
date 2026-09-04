using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Keeps the local XR rig and avatar usable while the main scene is hidden for a private room.
    /// </summary>
    static class PrivateRoomLocalPlayerSupport
    {
        // CharacterResetter clamps the rig to a small main-room volume (y <= 25, |x|/|z| <= 75) and
        // teleports the player back to the world origin if they leave it. Private rooms live far from
        // the origin (PrivateRoomService.m_PrivateRoomLocation, default 1000,1000,1000), so the resetter
        // would instantly yank the player out of the room into the now-hidden main scene = "the void".
        // We suspend it for the duration of the private-room stay and restore it on exit.
        static CharacterResetter s_SuspendedResetter;

        public static void PrepareForPrivateRoom()
        {
            ReleaseFromChairIfNeeded();
            SuspendCharacterResetter();
            EnsureLocomotionEnabled();
            EnsureLocalAvatarVisible();
        }

        public static void RestoreAfterLeavingPrivateRoom()
        {
            ReleaseFromChairIfNeeded();
            RestoreCharacterResetter();
            EnsureLocomotionEnabled();
            EnsureLocalAvatarVisible();
        }

        static void SuspendCharacterResetter()
        {
            XROrigin origin = FindLocalXROrigin();
            if (origin == null)
                return;

            CharacterResetter resetter = origin.GetComponentInChildren<CharacterResetter>(true);
            if (resetter != null && resetter.enabled)
            {
                resetter.enabled = false;
                s_SuspendedResetter = resetter;
            }
        }

        static void RestoreCharacterResetter()
        {
            if (s_SuspendedResetter != null)
            {
                s_SuspendedResetter.enabled = true;
                s_SuspendedResetter = null;
                return;
            }

            // Fallback: re-enable any resetter on the rig even if we lost the cached reference
            // (e.g. the rig was rebuilt, or suspend never captured one).
            XROrigin origin = FindLocalXROrigin();
            CharacterResetter resetter = origin != null ? origin.GetComponentInChildren<CharacterResetter>(true) : null;
            if (resetter != null)
                resetter.enabled = true;
        }

        public static void ReleaseFromChairIfNeeded()
        {
            if (ChairManager.Instance != null)
                ChairManager.Instance.ForceReleaseForSceneTransfer();
            else
                DetachLocalXROrigin();
        }

        static void DetachLocalXROrigin()
        {
            XROrigin origin = FindLocalXROrigin();
            if (origin != null && origin.transform.parent != null)
                origin.transform.SetParent(null, true);
        }

        public static void EnsureLocomotionEnabled()
        {
            XROrigin origin = FindLocalXROrigin();
            if (origin == null)
                return;

            var characterController = origin.GetComponent<CharacterController>();
            if (characterController != null)
                characterController.enabled = true;

            var bodyTransformer = origin.GetComponentInChildren<XRBodyTransformer>(true);
            if (bodyTransformer != null)
                bodyTransformer.enabled = true;

            foreach (var moveProvider in origin.GetComponentsInChildren<ContinuousMoveProvider>(true))
            {
                if (moveProvider != null)
                    moveProvider.enabled = true;
            }

            foreach (var turnProvider in origin.GetComponentsInChildren<ContinuousTurnProvider>(true))
            {
                if (turnProvider != null)
                    turnProvider.enabled = true;
            }

            foreach (var snapTurnProvider in origin.GetComponentsInChildren<SnapTurnProvider>(true))
            {
                if (snapTurnProvider != null)
                    snapTurnProvider.enabled = true;
            }

            foreach (var mediator in origin.GetComponentsInChildren<LocomotionMediator>(true))
            {
                if (mediator != null)
                    mediator.enabled = true;
            }
        }

        public static void EnsureLocalAvatarVisible()
        {
            if (XRINetworkPlayer.LocalPlayer != null)
            {
                // Use Mirror-layer body hiding (main room rules), not RefreshLocalBodyVisibility which
                // assigns the Player layer and makes the torso visible in the headset.
                XRINetworkPlayer.LocalPlayer.ApplyFirstPersonBodyLayers();
                return;
            }

            if (XRINetworkGameManager.Connected.Value)
                return;

            OfflinePlayerAvatar.RestoreAllAfterDisconnect();
        }

        public static XROrigin FindLocalXROrigin()
        {
            XROrigin[] origins = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < origins.Length; i++)
            {
                if (origins[i] != null)
                    return origins[i];
            }

            return null;
        }
    }
}
