using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// Compact world-space controls beside the presentation TV (prev / next / stop + page label).
    /// Stays active in the scene — does not require the Quick Menu presentation panel to be open.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class PresentationTVControlsUI : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        [SerializeField] private PresentationNetworkManager networkManager;
        [SerializeField] private FirestoreRoomSync roomSync;

        [Header("UI")]
        [SerializeField] private GameObject controlsRoot;
        [SerializeField] private Button previousButton;
        [SerializeField] private Button nextButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private TextMeshProUGUI pageLabel;

        [Header("Behaviour")]
        [Tooltip("Hide the whole control strip when nothing is being presented.")]
        [SerializeField] private bool hideWhenNoPresentation = true;
        [Tooltip("Show page label for viewers; navigation buttons stay presenter-only.")]
        [SerializeField] private bool showPageLabelForViewers = true;

        int m_CurrentPage;
        int m_TotalPages = 1;
        bool m_PresentationActive;

        void Awake()
        {
            if (controlsRoot == null)
                controlsRoot = gameObject;
        }

        void Start()
        {
            StartCoroutine(InitializeRoutine());
        }

        IEnumerator InitializeRoutine()
        {
            float timeout = 8f;
            float timer = 0f;
            while (timer < timeout)
            {
                if (networkManager == null)
                    networkManager = FindFirstObjectByType<PresentationNetworkManager>();
                if (roomSync == null)
                    roomSync = FirestoreRoomSync.Instance ?? FindFirstObjectByType<FirestoreRoomSync>();

                if (networkManager != null)
                    break;

                yield return new WaitForSeconds(0.2f);
                timer += 0.2f;
            }

            WireButtons();
            SubscribeEvents();
            ApplyInitialState();
            RefreshUI();
        }

        void ApplyInitialState()
        {
            if (roomSync != null && roomSync.HasActivePresentation)
                OnRoomPresentationStateChanged(roomSync.CurrentState);
        }

        void OnDestroy()
        {
            UnsubscribeEvents();
        }

        void WireButtons()
        {
            if (previousButton != null)
            {
                previousButton.onClick.RemoveAllListeners();
                previousButton.onClick.AddListener(() => PresentationNavigator.RequestPreviousPage());
            }

            if (nextButton != null)
            {
                nextButton.onClick.RemoveAllListeners();
                nextButton.onClick.AddListener(() => PresentationNavigator.RequestNextPage());
            }

            if (stopButton != null)
            {
                stopButton.onClick.RemoveAllListeners();
                stopButton.onClick.AddListener(() => PresentationNavigator.RequestStopPresentation());
            }
        }

        void SubscribeEvents()
        {
            if (networkManager != null)
            {
                networkManager.OnPageChanged += OnPageChanged;
                networkManager.OnPresentationActiveChanged += OnPresentationActiveChanged;
            }

            if (roomSync != null)
            {
                roomSync.OnPresentationStateChanged += OnRoomPresentationStateChanged;
                roomSync.OnPresentationCleared += OnPresentationCleared;
            }
        }

        void UnsubscribeEvents()
        {
            if (networkManager != null)
            {
                networkManager.OnPageChanged -= OnPageChanged;
                networkManager.OnPresentationActiveChanged -= OnPresentationActiveChanged;
            }

            if (roomSync != null)
            {
                roomSync.OnPresentationStateChanged -= OnRoomPresentationStateChanged;
                roomSync.OnPresentationCleared -= OnPresentationCleared;
            }
        }

        void OnPageChanged(int currentPage, int totalPages)
        {
            m_CurrentPage = currentPage;
            m_TotalPages = Mathf.Max(1, totalPages);
            m_PresentationActive = m_TotalPages > 1 || !string.IsNullOrEmpty(GetActiveFileUrl());
            RefreshUI();
        }

        void OnPresentationActiveChanged(bool isActive)
        {
            m_PresentationActive = isActive;
            if (!isActive)
            {
                m_CurrentPage = 0;
                m_TotalPages = 1;
            }
            RefreshUI();
        }

        void OnRoomPresentationStateChanged(FirestoreRoomSync.PresentationState state)
        {
            if (state == null || state.IsEmpty)
                return;

            m_CurrentPage = state.currentPage;
            m_TotalPages = Mathf.Max(1, state.totalPages);
            m_PresentationActive = true;
            RefreshUI();
        }

        void OnPresentationCleared()
        {
            m_PresentationActive = false;
            m_CurrentPage = 0;
            m_TotalPages = 1;
            RefreshUI();
        }

        string GetActiveFileUrl()
        {
            if (roomSync != null && roomSync.HasActivePresentation)
                return roomSync.CurrentState.fileUrl;

            return null;
        }

        void RefreshUI()
        {
            bool showStrip = m_PresentationActive || (roomSync != null && roomSync.HasActivePresentation);
            if (hideWhenNoPresentation && !showStrip)
            {
                if (controlsRoot != null)
                    controlsRoot.SetActive(false);
                return;
            }

            if (controlsRoot != null)
                controlsRoot.SetActive(true);

            bool canNavigate = PresentationNavigator.CanControlPresentation()
                && FirebaseStorageManager.IsUserAuthenticated;

            if (previousButton != null)
                previousButton.interactable = canNavigate && m_CurrentPage > 0;
            if (nextButton != null)
                nextButton.interactable = canNavigate && m_CurrentPage < m_TotalPages - 1;
            if (stopButton != null)
                stopButton.interactable = canNavigate;

            if (pageLabel != null)
            {
                bool showLabel = showPageLabelForViewers || canNavigate;
                pageLabel.gameObject.SetActive(showLabel);
                if (showLabel)
                {
                    pageLabel.text = m_TotalPages > 1
                        ? $"{m_CurrentPage + 1} / {m_TotalPages}"
                        : "";
                }
            }
        }
    }
}
