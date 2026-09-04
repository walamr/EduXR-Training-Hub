using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace XRMultiplayer.Transcription
{
    public class SummaryPanel : MonoBehaviour
    {
        [SerializeField] private TMP_Text contentText;
        [SerializeField] private Button closeButton;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private ScrollRect scrollRect;

        // Original font of the content text, restored for non-Hebrew summaries.
        private TMPro.TMP_FontAsset defaultFont;
        private bool defaultFontCached;

        private void Awake()
        {
            if (closeButton != null)
            {
                closeButton.onClick.AddListener(Hide);
            }
        }

        public void Show(string text)
        {
            gameObject.SetActive(true);
            if (contentText != null)
            {
                if (!defaultFontCached)
                {
                    defaultFont = contentText.font;
                    defaultFontCached = true;
                }

                string clean = StripMarkdown(text);
                bool hasArabic = RtlTextUtility.ContainsArabic(clean);
                bool hasHebrew = RtlTextUtility.ContainsHebrew(clean);

                if (hasHebrew && !hasArabic)
                {
                    // Hebrew summary: dedicated rendering path. The Hebrew font is assigned
                    // DIRECTLY (immune to fallback issues), TMP's native RTL mode draws the
                    // logical-order text right-to-left (no reversal, no shaping) and wraps
                    // correctly; embedded Latin/number runs are pre-adjusted.
                    var hebrewFont = RtlTextUtility.GetHebrewFont();
                    if (hebrewFont != null) contentText.font = hebrewFont;
                    contentText.isRightToLeftText = true;
                    contentText.alignment = TextAlignmentOptions.TopRight;
                    contentText.text = RtlTextUtility.PrepareForRtlRendering(clean);
                }
                else
                {
                    // English and/or Arabic (Arabic is shaped per line via ArabicFixer).
                    if (defaultFont != null) contentText.font = defaultFont;
                    contentText.isRightToLeftText = false;
                    contentText.alignment = TextAlignmentOptions.TopLeft;
                    contentText.text = FormatForDisplay(clean);
                }
            }

            // Start scrolled to the top.
            if (scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 1f;
            }

            // Fade in if CanvasGroup exists
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                StartCoroutine(FadeIn());
            }
        }

        /// <summary>
        /// Gemini sometimes returns markdown markers (** for bold, # headers, * bullets)
        /// which TextMeshPro renders literally. Convert/strip them for a clean display.
        /// </summary>
        private string StripMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            text = text.Replace("**", "");

            string[] linesArr = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < linesArr.Length; i++)
            {
                string trimmed = linesArr[i].TrimStart();
                if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
                {
                    linesArr[i] = linesArr[i].Substring(0, linesArr[i].Length - trimmed.Length) + "\u2022 " + trimmed.Substring(2);
                }
                else if (trimmed.StartsWith("#"))
                {
                    linesArr[i] = linesArr[i].Replace("#", "").TrimStart();
                }
            }
            return string.Join("\n", linesArr);
        }

        /// <summary>
        /// Formats summary text line-by-line, shaping + right-aligning RTL (Arabic/Hebrew)
        /// lines so they read correctly, while leaving English lines untouched.
        /// </summary>
        private string FormatForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string[] linesArr = text.Replace("\r", "").Split('\n');
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < linesArr.Length; i++)
            {
                string line = linesArr[i];
                if (RtlTextUtility.ContainsArabic(line))
                {
                    // Arabic: shape + reverse via ArabicFixer.
                    sb.Append($"<align=\"right\">{ArabicFixer.Fix(line)}</align>");
                }
                else if (RtlTextUtility.ContainsHebrew(line))
                {
                    // Hebrew: visual order without shaping, right-aligned (shared utility).
                    sb.Append($"<align=\"right\">{RtlTextUtility.ToHebrewVisual(line)}</align>");
                }
                else
                {
                    sb.Append(line);
                }

                if (i < linesArr.Length - 1) sb.Append('\n');
            }

            return sb.ToString();
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private System.Collections.IEnumerator FadeIn()
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime * 3f;
                canvasGroup.alpha = t;
                yield return null;
            }
            canvasGroup.alpha = 1f;
        }

        public void Setup(TMP_Text textComponent, Button closeBtn, CanvasGroup group, ScrollRect scroll = null)
        {
            contentText = textComponent;
            closeButton = closeBtn;
            canvasGroup = group;
            scrollRect = scroll;

            if (closeButton != null)
            {
                closeButton.onClick.RemoveAllListeners();
                closeButton.onClick.AddListener(Hide);
            }
        }
    }
}
