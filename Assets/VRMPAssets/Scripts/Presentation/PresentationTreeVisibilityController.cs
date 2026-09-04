using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer.Presentation
{
    /// <summary>
    /// Controls cumulative tree reveal by shared presentation pages:
    /// start with hidden trees, then reveal more trees on each newly shared document.
    /// </summary>
    public class PresentationTreeVisibilityController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PresentationNetworkManager networkManager;
        [SerializeField] private Transform forestContainer;

        [Header("Tree Selection")]
        [SerializeField, Tooltip("If empty, uses all direct children under Forest Container.")]
        private List<GameObject> treeRoots = new List<GameObject>();

        [Header("Terrain Trees")]
        [SerializeField, Tooltip("Disable Terrain tree/foliage rendering when not presenting.")]
        private bool controlTerrainTreeRendering = true;
        [SerializeField] private List<Terrain> controlledTerrains = new List<Terrain>();

        private bool m_Subscribed;
        private int m_RevealedTreeCount;
        private int m_LastSyncedCumulative = -1;

        private void Awake()
        {
            AutoAssignReferences();
            CacheTreesIfNeeded();

            // Deterministic startup state: hide all trees until first shared document.
            m_RevealedTreeCount = 0;
            ApplyVisibility();
        }

        private void OnEnable()
        {
            AutoAssignReferences();
            TrySubscribe();
            ApplyVisibility();
        }

        private void Update()
        {
            // Late/self-healing binding in case manager spawns after this component enables.
            if (!m_Subscribed)
            {
                AutoAssignReferences();
                TrySubscribe();
            }

            // Hard runtime sync: even if an event is missed, always converge to current cumulative pages.
            if (networkManager != null)
            {
                int cumulative = Mathf.Max(0, networkManager.GetCumulativePageCount());
                if (cumulative != m_LastSyncedCumulative)
                {
                    m_LastSyncedCumulative = cumulative;
                    OnCumulativePageCountChanged(cumulative);
                }
            }
        }

        private void OnDisable()
        {
            if (networkManager != null && m_Subscribed)
            {
                networkManager.OnPresentationActiveChanged -= OnPresentationActiveChanged;
                networkManager.OnCumulativePageCountChanged -= OnCumulativePageCountChanged;
                m_Subscribed = false;
            }
        }

        private void TrySubscribe()
        {
            if (networkManager == null || m_Subscribed)
                return;

            networkManager.OnPresentationActiveChanged += OnPresentationActiveChanged;
            networkManager.OnCumulativePageCountChanged += OnCumulativePageCountChanged;
            
            // Initial sync: use current cumulative count if available
            m_RevealedTreeCount = networkManager.GetCumulativePageCount();
            m_LastSyncedCumulative = m_RevealedTreeCount;
            ApplyVisibility();
            
            m_Subscribed = true;
        }

        private void OnCumulativePageCountChanged(int totalCumulativePages)
        {
            CacheTreesIfNeeded();
            int maxTrees = treeRoots.Count;
            m_RevealedTreeCount = Mathf.Clamp(totalCumulativePages, 0, maxTrees);
            ApplyVisibility();
        }

        private void OnPresentationActiveChanged(bool isActive)
        {
            // Trees persist after reveal, so active/inactive does not hide them.
            if (controlTerrainTreeRendering)
            {
                CacheTerrainsIfNeeded();
                for (int i = 0; i < controlledTerrains.Count; i++)
                {
                    Terrain terrain = controlledTerrains[i];
                    if (terrain != null)
                        terrain.drawTreesAndFoliage = isActive;
                }
            }
        }

        private void ApplyVisibility()
        {
            CacheTreesIfNeeded();
            for (int i = 0; i < treeRoots.Count; i++)
            {
                GameObject root = treeRoots[i];
                if (root != null)
                    root.SetActive(i < m_RevealedTreeCount);
            }
        }

        private void AutoAssignReferences()
        {
            if (networkManager == null)
                networkManager = FindFirstObjectByType<PresentationNetworkManager>();

            if (forestContainer == null)
            {
                GameObject forest = GameObject.Find("Forest_Container");
                if (forest != null)
                    forestContainer = forest.transform;
            }
        }

        private void CacheTreesIfNeeded()
        {
            if (treeRoots != null && treeRoots.Count > 0)
                return;

            treeRoots = new List<GameObject>();
            if (forestContainer != null)
            {
                for (int i = 0; i < forestContainer.childCount; i++)
                {
                    Transform child = forestContainer.GetChild(i);
                    if (child != null && LooksLikeTreeName(child.name))
                        treeRoots.Add(child.gameObject);
                }
            }
            else
            {
                Transform[] all = FindObjectsByType<Transform>(FindObjectsSortMode.None);
                foreach (Transform t in all)
                {
                    if (t == null || t.gameObject == null || t.parent == null)
                        continue;

                    if (!LooksLikeTreeName(t.name))
                        continue;

                    if (t.parent.name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    treeRoots.Add(t.gameObject);
                }
            }

            if (forestContainer != null)
            {
                treeRoots.Sort((a, b) =>
                {
                    float distA = Vector3.Distance(a.transform.position, forestContainer.position);
                    float distB = Vector3.Distance(b.transform.position, forestContainer.position);
                    return distA.CompareTo(distB);
                });
            }
            else
            {
                treeRoots.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            }
            }

            private void CacheTerrainsIfNeeded()
        {
            if (controlledTerrains != null && controlledTerrains.Count > 0)
                return;

            controlledTerrains = new List<Terrain>(FindObjectsByType<Terrain>(FindObjectsSortMode.None));
        }

        public static bool LooksLikeTreeName(string name)
        {
            return name.IndexOf("Spruce", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Pine", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
