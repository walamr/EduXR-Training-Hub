using UnityEngine;

namespace XRMultiplayer
{
    /// <summary>
    /// Lightweight cosmetic emoji that floats upward, fades out, billboards toward the local
    /// camera, then self-destructs. Spawned (locally on each client) by <see cref="EmojiReactionNetwork"/>.
    /// Requires a <see cref="SpriteRenderer"/> on this object or a child; one is added if missing.
    /// </summary>
    public class FloatingEmoji : MonoBehaviour
    {
        [SerializeField, Tooltip("Seconds before the emoji is destroyed.")]
        float m_Lifetime = 2.0f;

        [SerializeField, Tooltip("Upward world-space speed (metres/second).")]
        float m_RiseSpeed = 0.6f;

        [SerializeField, Range(0f, 1f), Tooltip("Fraction of the lifetime after which the emoji starts fading out.")]
        float m_FadeStartFraction = 0.5f;

        [SerializeField, Tooltip("Maximum random horizontal drift so stacked reactions don't perfectly overlap.")]
        Vector3 m_RandomDriftMax = new Vector3(0.1f, 0f, 0.1f);

        SpriteRenderer m_SpriteRenderer;
        float m_Age;
        Vector3 m_Drift;
        Transform m_Camera;

        void Awake()
        {
            m_SpriteRenderer = GetComponentInChildren<SpriteRenderer>();
            if (Camera.main != null)
                m_Camera = Camera.main.transform;
        }

        /// <summary>
        /// Assigns the emoji sprite and (re)starts the float/fade lifecycle.
        /// </summary>
        /// <param name="sprite">Sprite to display.</param>
        /// <param name="lifetime">Optional lifetime override; ignored when &lt;= 0.</param>
        public void Play(Sprite sprite, float lifetime = -1f)
        {
            if (m_SpriteRenderer == null)
                m_SpriteRenderer = GetComponentInChildren<SpriteRenderer>();
            if (m_SpriteRenderer == null)
                m_SpriteRenderer = gameObject.AddComponent<SpriteRenderer>();

            if (sprite != null)
                m_SpriteRenderer.sprite = sprite;

            if (lifetime > 0f)
                m_Lifetime = lifetime;

            m_Age = 0f;
            m_Drift = new Vector3(
                Random.Range(-m_RandomDriftMax.x, m_RandomDriftMax.x),
                0f,
                Random.Range(-m_RandomDriftMax.z, m_RandomDriftMax.z));

            // Reset alpha in case this instance is pooled/reused.
            if (m_SpriteRenderer != null)
            {
                Color c = m_SpriteRenderer.color;
                c.a = 1f;
                m_SpriteRenderer.color = c;
            }
        }

        void Update()
        {
            m_Age += Time.deltaTime;
            transform.position += (Vector3.up * m_RiseSpeed + m_Drift) * Time.deltaTime;

            // Billboard toward the local camera. Camera.main is often null at spawn time in XR
            // (rig not ready / not yet tagged), so re-acquire lazily rather than never facing.
            if (m_Camera == null && Camera.main != null)
                m_Camera = Camera.main.transform;
            if (m_Camera != null)
                transform.forward = transform.position - m_Camera.position;

            if (m_SpriteRenderer != null && m_Lifetime > 0f)
            {
                float fadeStart = m_Lifetime * m_FadeStartFraction;
                if (m_Age > fadeStart)
                {
                    // Lerp alpha from 1 (at fadeStart) to 0 (at lifetime).
                    float t = Mathf.InverseLerp(m_Lifetime, fadeStart, m_Age);
                    Color c = m_SpriteRenderer.color;
                    c.a = Mathf.Clamp01(t);
                    m_SpriteRenderer.color = c;
                }
            }

            if (m_Age >= m_Lifetime)
                Destroy(gameObject);
        }
    }
}
