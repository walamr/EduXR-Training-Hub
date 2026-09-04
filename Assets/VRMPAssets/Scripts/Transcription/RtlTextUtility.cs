using System.Collections.Generic;
using System.Text;
using TMPro;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Helpers for rendering RTL (Hebrew / Arabic) text with TextMeshPro.
    ///
    /// TextMeshPro (uGUI) has no bidirectional text engine, so each language uses the
    /// strategy that renders it correctly:
    ///  - Hebrew: shown on a dedicated TMP component with isRightToLeftText = true.
    ///    The string stays in NORMAL logical order (no shaping, no character reversal);
    ///    TMP's native RTL mode draws it right-to-left and wraps lines correctly.
    ///    Use <see cref="PrepareForRtlRendering"/> to fix embedded Latin/number runs.
    ///  - Arabic: shaped + converted to visual order by ArabicFixer (letters must connect),
    ///    rendered on a normal LTR component, right-aligned.
    ///  - English/other LTR: rendered as-is.
    /// </summary>
    public static class RtlTextUtility
    {
        private static TMP_FontAsset s_hebrewFont;
        private static bool s_hebrewFontSearched;

        /// <summary>
        /// Finds the Hebrew TMP font asset registered in the TMP Settings global fallback
        /// list. Assigning it DIRECTLY to Hebrew text components makes rendering immune to
        /// fallback-order problems and stale fallback caches. The global fallback list is
        /// serialized in TMP Settings, so this lookup also works in device builds (Quest).
        /// </summary>
        public static TMP_FontAsset GetHebrewFont()
        {
            if (s_hebrewFontSearched) return s_hebrewFont;
            s_hebrewFontSearched = true;

            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks != null)
            {
                foreach (var font in fallbacks)
                {
                    if (font != null && font.name.IndexOf("Hebrew", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        s_hebrewFont = font;
                        break;
                    }
                }
            }

            if (s_hebrewFont == null)
            {
                UnityEngine.Debug.LogWarning("[RtlTextUtility] No Hebrew font found in TMP global fallbacks. Run 'VRMP/Setup RTL Fonts (Hebrew + Arabic)'.");
            }
            return s_hebrewFont;
        }

        /// <summary>True if the text contains Arabic-script characters.</summary>
        public static bool ContainsArabic(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if ((c >= '\u0600' && c <= '\u06FF') ||  // Arabic
                    (c >= '\u0750' && c <= '\u077F') ||  // Arabic Supplement
                    (c >= '\u08A0' && c <= '\u08FF') ||  // Arabic Extended-A
                    (c >= '\uFB50' && c <= '\uFDFF') ||  // Presentation Forms-A
                    (c >= '\uFE70' && c <= '\uFEFF'))    // Presentation Forms-B
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if the text contains Hebrew-script characters.</summary>
        public static bool ContainsHebrew(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if ((c >= '\u0590' && c <= '\u05FF') ||  // Hebrew
                    (c >= '\uFB1D' && c <= '\uFB4F'))    // Hebrew Presentation Forms
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Prepares logical-order text for a TMP component that has isRightToLeftText = true.
        /// TMP's native RTL mode renders the character stream right-to-left and wraps lines
        /// correctly, so Hebrew/Arabic stay in NORMAL logical order. The only adjustment
        /// needed is pre-reversing embedded left-to-right runs (Latin words, numbers like
        /// 10:30) so TMP's render-time reversal restores them to readable order.
        /// </summary>
        public static string PrepareForRtlRendering(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            int i = 0;

            while (i < text.Length)
            {
                if (IsLtrChar(text[i]))
                {
                    // Extend the LTR run; neutrals (':', '.', '-', space) stay inside only
                    // when more LTR chars follow before any RTL char ("10:30", "ABC 123").
                    int lastLtr = i;
                    int j = i;
                    while (j < text.Length && !IsRtlChar(text[j]))
                    {
                        if (IsLtrChar(text[j])) lastLtr = j;
                        j++;
                    }

                    // Append the LTR run reversed; TMP's RTL rendering reverses it back.
                    for (int c = lastLtr; c >= i; c--)
                    {
                        sb.Append(text[c]);
                    }
                    i = lastLtr + 1;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Converts a SINGLE logical-order Hebrew line to visual order for an LTR renderer.
        /// No letter shaping. Left-to-right runs (Latin words, numbers like 17:48 or 3.5)
        /// are preserved intact, and paired brackets are mirrored so they still face the text.
        /// </summary>
        public static string ToHebrewVisual(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Split into segments: LTR runs (letters/digits plus internal separators)
            // and RTL/neutral segments (Hebrew letters, spaces, punctuation).
            var segments = new List<KeyValuePair<string, bool>>(); // value=true -> LTR run
            var current = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                if (IsLtrChar(text[i]))
                {
                    if (current.Length > 0)
                    {
                        segments.Add(new KeyValuePair<string, bool>(current.ToString(), false));
                        current.Length = 0;
                    }

                    // Extend the LTR run forward. Neutral chars (':', '.', '-', space...)
                    // stay inside the run only if more LTR chars follow before any RTL char,
                    // so "17:48:32" or "ABC 123" survive as units.
                    int lastLtr = i;
                    int j = i;
                    while (j < text.Length && !IsRtlChar(text[j]))
                    {
                        if (IsLtrChar(text[j])) lastLtr = j;
                        j++;
                    }

                    segments.Add(new KeyValuePair<string, bool>(text.Substring(i, lastLtr - i + 1), true));
                    i = lastLtr + 1;
                }
                else
                {
                    current.Append(text[i]);
                    i++;
                }
            }

            if (current.Length > 0)
            {
                segments.Add(new KeyValuePair<string, bool>(current.ToString(), false));
            }

            // Visual order = segments reversed; RTL segments also reversed char-by-char
            // (that is what makes them read right-to-left in an LTR renderer), with
            // paired brackets mirrored. LTR segments are appended unchanged.
            var sb = new StringBuilder(text.Length);
            for (int s = segments.Count - 1; s >= 0; s--)
            {
                if (segments[s].Value)
                {
                    sb.Append(segments[s].Key);
                }
                else
                {
                    string seg = segments[s].Key;
                    for (int c = seg.Length - 1; c >= 0; c--)
                    {
                        sb.Append(MirrorBracket(seg[c]));
                    }
                }
            }

            return sb.ToString();
        }

        private static bool IsLtrChar(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'A' && c <= 'Z') ||
                   (c >= 'a' && c <= 'z');
        }

        private static bool IsRtlChar(char c)
        {
            return (c >= '\u0590' && c <= '\u05FF') ||
                   (c >= '\uFB1D' && c <= '\uFB4F') ||
                   (c >= '\u0600' && c <= '\u06FF') ||
                   (c >= '\u0750' && c <= '\u077F');
        }

        private static char MirrorBracket(char c)
        {
            switch (c)
            {
                case '(': return ')';
                case ')': return '(';
                case '[': return ']';
                case ']': return '[';
                case '{': return '}';
                case '}': return '{';
                case '<': return '>';
                case '>': return '<';
                default: return c;
            }
        }
    }
}
