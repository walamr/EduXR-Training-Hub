using UnityEngine;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// Shared presentation navigation (next / prev / stop) for file UI and TV controls.
    /// </summary>
    public static class PresentationNavigator
    {
        public static bool EnsureAuthForNavigation()
        {
            var firebase = FirebaseStorageManager.Instance
                ?? Object.FindFirstObjectByType<FirebaseStorageManager>();

            if (firebase != null)
                return firebase.TryEnsureAuthenticated("presentation navigation");

            PresentationUIManager.RequestAuthentication("presentation navigation");
            return false;
        }

        public static void RequestNextPage()
        {
            if (!EnsureAuthForNavigation())
                return;

            var roomSync = FirestoreRoomSync.Instance ?? Object.FindFirstObjectByType<FirestoreRoomSync>();
            if (roomSync != null && roomSync.IsLocalUserPresenter)
            {
                roomSync.NextPage();
            }

            var network = Object.FindFirstObjectByType<PresentationNetworkManager>();
            network?.RequestNextPage();
        }

        public static void RequestPreviousPage()
        {
            if (!EnsureAuthForNavigation())
                return;

            var roomSync = FirestoreRoomSync.Instance ?? Object.FindFirstObjectByType<FirestoreRoomSync>();
            if (roomSync != null && roomSync.IsLocalUserPresenter)
            {
                roomSync.PreviousPage();
            }

            var network = Object.FindFirstObjectByType<PresentationNetworkManager>();
            network?.RequestPrevPage();
        }

        public static void RequestStopPresentation()
        {
            var firebase = FirebaseStorageManager.Instance
                ?? Object.FindFirstObjectByType<FirebaseStorageManager>();
            if (firebase != null)
            {
                if (!firebase.TryEnsureAuthenticated("stopping presentation"))
                    return;
            }
            else
            {
                PresentationUIManager.RequestAuthentication("stopping presentation");
                return;
            }

            var roomSync = FirestoreRoomSync.Instance ?? Object.FindFirstObjectByType<FirestoreRoomSync>();
            if (roomSync != null)
            {
                roomSync.StopPresentation();
            }

            var network = Object.FindFirstObjectByType<PresentationNetworkManager>();
            network?.RequestClear();
        }

        public static bool CanControlPresentation()
        {
            var roomSync = FirestoreRoomSync.Instance ?? Object.FindFirstObjectByType<FirestoreRoomSync>();
            if (roomSync != null && roomSync.HasActivePresentation)
                return roomSync.IsLocalUserPresenter;

            var network = Object.FindFirstObjectByType<PresentationNetworkManager>();
            if (network == null)
                return false;

            return !network.IsSpawned || network.IsOwner;
        }
    }
}
