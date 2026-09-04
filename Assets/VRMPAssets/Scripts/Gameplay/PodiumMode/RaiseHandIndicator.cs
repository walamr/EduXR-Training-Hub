using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace XRMultiplayer
{
    /// <summary>
    /// Creates and manages a floating 3D "raised hand" icon above a player's head.
    /// Visible to all players when someone raises their hand during Podium Mode.
    /// </summary>
    public class RaiseHandIndicator : MonoBehaviour
    {
        // ── Static Registry ────────────────────────────────────────
        private static readonly Dictionary<ulong, RaiseHandIndicator> s_ActiveIndicators = new();

        /// <summary>
        /// Shows the raise-hand indicator for a given player.
        /// </summary>
        public static void ShowForPlayer(ulong clientId)
        {
            if (s_ActiveIndicators.TryGetValue(clientId, out var existing))
            {
                if (existing != null) existing.gameObject.SetActive(true);
                return;
            }

            // Create the indicator immediately. The player's avatar/head may not exist yet
            // (e.g. when restoring raised hands during late-join sync), so the follow target
            // is resolved lazily in Update from the client id.
            var indicator = CreateIndicator(clientId);
            s_ActiveIndicators[clientId] = indicator;
        }

        /// <summary>
        /// Hides (destroys) the raise-hand indicator for a given player.
        /// </summary>
        public static void HideForPlayer(ulong clientId)
        {
            if (s_ActiveIndicators.TryGetValue(clientId, out var indicator))
            {
                if (indicator != null) Destroy(indicator.gameObject);
                s_ActiveIndicators.Remove(clientId);
            }
        }

        /// <summary>
        /// Destroys all active indicators.
        /// </summary>
        public static void DestroyAll()
        {
            foreach (var kvp in s_ActiveIndicators)
            {
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            }
            s_ActiveIndicators.Clear();
        }

        // ── Instance Fields ────────────────────────────────────────
        private Transform m_FollowTarget;
        private float m_BobPhase;
        private ulong m_ClientId;
        private float m_ResolveTimer;

        private const float HOVER_HEIGHT = 0.45f;
        private const float BOB_AMPLITUDE = 0.03f;
        private const float BOB_SPEED = 2.5f;
        private const float ICON_SIZE = 0.12f;
        private const float RESOLVE_TIMEOUT = 10f;

        // ── Factory ────────────────────────────────────────────────
        private static RaiseHandIndicator CreateIndicator(ulong clientId)
        {
            // Create container
            var go = new GameObject($"RaiseHand_{clientId}");
            var indicator = go.AddComponent<RaiseHandIndicator>();
            indicator.m_ClientId = clientId;
            indicator.TryResolveTarget(); // resolve now if the avatar already exists

            // Create the visual: a small panel with a hand emoji/text
            var iconObj = new GameObject("HandIcon");
            iconObj.transform.SetParent(go.transform, false);

            // Use TextMeshPro for a crisp hand symbol
            var tmp = iconObj.AddComponent<TextMeshPro>();
            tmp.text = "\u270B"; // Raised hand unicode
            tmp.fontSize = 6f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 0.85f, 0.2f); // Golden yellow
            tmp.fontStyle = FontStyles.Bold;

            // Size the text rect
            var rt = iconObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(ICON_SIZE * 2f, ICON_SIZE * 2f);

            // Add a subtle background circle
            var bgObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bgObj.name = "BG";
            bgObj.transform.SetParent(go.transform, false);
            bgObj.transform.localScale = Vector3.one * ICON_SIZE;

            // Remove collider
            var col = bgObj.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Semi-transparent material
            var rend = bgObj.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                var c = new Color(1f, 0.7f, 0.1f, 0.6f);
                rend.material.color = c;
                rend.material.EnableKeyword("_EMISSION");
                rend.material.SetColor("_EmissionColor", c * 0.5f);
            }

            Debug.Log($"[RaiseHandIndicator] Created indicator for player {clientId}");
            return indicator;
        }

        /// <summary>Resolves the follow target (the player's head) from the client id, if available.</summary>
        private bool TryResolveTarget()
        {
            if (XRINetworkGameManager.Instance != null &&
                XRINetworkGameManager.Instance.TryGetPlayerByID(m_ClientId, out var player) &&
                player != null && player.head != null)
            {
                m_FollowTarget = player.head;
                return true;
            }
            return false;
        }

        // ── Update ─────────────────────────────────────────────────
        private void Update()
        {
            if (m_FollowTarget == null)
            {
                // Avatar/head not ready yet — keep trying for a while before giving up.
                if (!TryResolveTarget())
                {
                    m_ResolveTimer += Time.deltaTime;
                    if (m_ResolveTimer > RESOLVE_TIMEOUT)
                    {
                        Debug.LogWarning($"[RaiseHandIndicator] Could not resolve head for player {m_ClientId}; removing indicator.");
                        Destroy(gameObject);
                    }
                    return;
                }
            }

            // Follow head + hover above
            m_BobPhase += Time.deltaTime * BOB_SPEED;
            float bobOffset = Mathf.Sin(m_BobPhase) * BOB_AMPLITUDE;

            transform.position = m_FollowTarget.position + Vector3.up * (HOVER_HEIGHT + bobOffset);

            // Billboard: face the main camera
            if (Camera.main != null)
            {
                transform.rotation = Quaternion.LookRotation(transform.position - Camera.main.transform.position);
            }
        }

        private void OnDestroy()
        {
            // Remove from registry if still there
            ulong? keyToRemove = null;
            foreach (var kvp in s_ActiveIndicators)
            {
                if (kvp.Value == this)
                {
                    keyToRemove = kvp.Key;
                    break;
                }
            }
            if (keyToRemove.HasValue)
            {
                s_ActiveIndicators.Remove(keyToRemove.Value);
            }
        }
    }
}
