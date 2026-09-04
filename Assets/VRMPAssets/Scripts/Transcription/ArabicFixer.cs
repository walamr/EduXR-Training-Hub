/*
 * ArabicSupport for Unity
 * Converts Arabic text to use proper presentation forms (connected letters)
 * Based on: https://github.com/Konash/arabic-support-unity (MIT License)
 * 
 * This handles:
 * - Letter shaping (connecting letters based on position)
 * - RTL text reversal
 * - Common ligatures
 */

using System.Collections.Generic;
using System.Text;

namespace XRMultiplayer.Transcription
{
    public static class ArabicFixer
    {
        // Arabic letter forms: [Isolated, Final, Initial, Medial]
        private static readonly Dictionary<char, char[]> ArabicLetterForms = new Dictionary<char, char[]>
        {
            // Alef variants
            { '\u0627', new char[] { '\uFE8D', '\uFE8E', '\uFE8D', '\uFE8E' } }, // Alef
            { '\u0623', new char[] { '\uFE83', '\uFE84', '\uFE83', '\uFE84' } }, // Alef with Hamza above
            { '\u0625', new char[] { '\uFE87', '\uFE88', '\uFE87', '\uFE88' } }, // Alef with Hamza below
            { '\u0622', new char[] { '\uFE81', '\uFE82', '\uFE81', '\uFE82' } }, // Alef with Madda
            { '\u0649', new char[] { '\uFEEF', '\uFEF0', '\uFEEF', '\uFEF0' } }, // Alef Maksura
            
            // Standard letters
            { '\u0628', new char[] { '\uFE8F', '\uFE90', '\uFE91', '\uFE92' } }, // Ba
            { '\u062A', new char[] { '\uFE95', '\uFE96', '\uFE97', '\uFE98' } }, // Ta
            { '\u062B', new char[] { '\uFE99', '\uFE9A', '\uFE9B', '\uFE9C' } }, // Tha
            { '\u062C', new char[] { '\uFE9D', '\uFE9E', '\uFE9F', '\uFEA0' } }, // Jeem
            { '\u062D', new char[] { '\uFEA1', '\uFEA2', '\uFEA3', '\uFEA4' } }, // Hha
            { '\u062E', new char[] { '\uFEA5', '\uFEA6', '\uFEA7', '\uFEA8' } }, // Kha
            { '\u062F', new char[] { '\uFEA9', '\uFEAA', '\uFEA9', '\uFEAA' } }, // Dal
            { '\u0630', new char[] { '\uFEAB', '\uFEAC', '\uFEAB', '\uFEAC' } }, // Thal
            { '\u0631', new char[] { '\uFEAD', '\uFEAE', '\uFEAD', '\uFEAE' } }, // Ra
            { '\u0632', new char[] { '\uFEAF', '\uFEB0', '\uFEAF', '\uFEB0' } }, // Zain
            { '\u0633', new char[] { '\uFEB1', '\uFEB2', '\uFEB3', '\uFEB4' } }, // Seen
            { '\u0634', new char[] { '\uFEB5', '\uFEB6', '\uFEB7', '\uFEB8' } }, // Sheen
            { '\u0635', new char[] { '\uFEB9', '\uFEBA', '\uFEBB', '\uFEBC' } }, // Sad
            { '\u0636', new char[] { '\uFEBD', '\uFEBE', '\uFEBF', '\uFEC0' } }, // Dad
            { '\u0637', new char[] { '\uFEC1', '\uFEC2', '\uFEC3', '\uFEC4' } }, // Tah
            { '\u0638', new char[] { '\uFEC5', '\uFEC6', '\uFEC7', '\uFEC8' } }, // Zah
            { '\u0639', new char[] { '\uFEC9', '\uFECA', '\uFECB', '\uFECC' } }, // Ain
            { '\u063A', new char[] { '\uFECD', '\uFECE', '\uFECF', '\uFED0' } }, // Ghain
            { '\u0641', new char[] { '\uFED1', '\uFED2', '\uFED3', '\uFED4' } }, // Fa
            { '\u0642', new char[] { '\uFED5', '\uFED6', '\uFED7', '\uFED8' } }, // Qaf
            { '\u0643', new char[] { '\uFED9', '\uFEDA', '\uFEDB', '\uFEDC' } }, // Kaf
            { '\u0644', new char[] { '\uFEDD', '\uFEDE', '\uFEDF', '\uFEE0' } }, // Lam
            { '\u0645', new char[] { '\uFEE1', '\uFEE2', '\uFEE3', '\uFEE4' } }, // Meem
            { '\u0646', new char[] { '\uFEE5', '\uFEE6', '\uFEE7', '\uFEE8' } }, // Noon
            { '\u0647', new char[] { '\uFEE9', '\uFEEA', '\uFEEB', '\uFEEC' } }, // Ha
            { '\u0648', new char[] { '\uFEED', '\uFEEE', '\uFEED', '\uFEEE' } }, // Waw
            { '\u064A', new char[] { '\uFEF1', '\uFEF2', '\uFEF3', '\uFEF4' } }, // Ya
            
            // Ta Marbuta
            { '\u0629', new char[] { '\uFE93', '\uFE94', '\uFE93', '\uFE94' } }, // Ta Marbuta
            
            // Hamza
            { '\u0621', new char[] { '\uFE80', '\uFE80', '\uFE80', '\uFE80' } }, // Hamza
            { '\u0626', new char[] { '\uFE89', '\uFE8A', '\uFE8B', '\uFE8C' } }, // Ya with Hamza
            { '\u0624', new char[] { '\uFE85', '\uFE86', '\uFE85', '\uFE86' } }, // Waw with Hamza
        };

        // Letters that don't connect to the next letter
        private static readonly HashSet<char> NonConnectingLetters = new HashSet<char>
        {
            '\u0627', // Alef
            '\u0623', // Alef with Hamza above
            '\u0625', // Alef with Hamza below
            '\u0622', // Alef with Madda
            '\u062F', // Dal
            '\u0630', // Thal
            '\u0631', // Ra
            '\u0632', // Zain
            '\u0648', // Waw
            '\u0624', // Waw with Hamza
            '\u0629', // Ta Marbuta
            '\u0649', // Alef Maksura
        };

        /// <summary>
        /// Fixes Arabic text by applying proper letter shaping and RTL reversal.
        /// </summary>
        public static string Fix(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            StringBuilder result = new StringBuilder();
            
            // Process each word/segment
            string[] parts = text.Split(' ');
            
            for (int p = 0; p < parts.Length; p++)
            {
                string word = parts[p];
                
                if (string.IsNullOrEmpty(word))
                {
                    result.Append(' ');
                    continue;
                }

                // Check if this part contains Arabic
                bool hasArabic = false;
                foreach (char c in word)
                {
                    if (IsArabicChar(c))
                    {
                        hasArabic = true;
                        break;
                    }
                }

                if (hasArabic)
                {
                    // Shape Arabic word (connect letters properly)
                    string shaped = ShapeWord(word);
                    result.Append(shaped);
                }
                else
                {
                    // Keep non-Arabic as-is
                    result.Append(word);
                }

                if (p < parts.Length - 1)
                    result.Append(' ');
            }

            // Single reversal for proper RTL display in LTR renderer
            return ReverseString(result.ToString());
        }

        private static string ShapeWord(string word)
        {
            if (string.IsNullOrEmpty(word))
                return word;

            StringBuilder shaped = new StringBuilder();
            
            for (int i = 0; i < word.Length; i++)
            {
                char current = word[i];
                
                // Skip diacritics (tashkeel)
                if (IsDiacritic(current))
                {
                    shaped.Append(current);
                    continue;
                }

                if (!ArabicLetterForms.ContainsKey(current))
                {
                    shaped.Append(current);
                    continue;
                }

                char[] forms = ArabicLetterForms[current];
                
                // Determine position
                bool prevConnects = (i > 0) && CanConnectAfter(GetPrevLetter(word, i));
                bool nextConnects = (i < word.Length - 1) && CanConnectBefore(current) && IsArabicChar(GetNextLetter(word, i));

                int formIndex;
                if (prevConnects && nextConnects)
                    formIndex = 3; // Medial
                else if (prevConnects)
                    formIndex = 1; // Final
                else if (nextConnects)
                    formIndex = 2; // Initial
                else
                    formIndex = 0; // Isolated

                shaped.Append(forms[formIndex]);
            }

            return shaped.ToString();
        }

        private static bool IsArabicChar(char c)
        {
            return (c >= '\u0600' && c <= '\u06FF') ||
                   (c >= '\u0750' && c <= '\u077F') ||
                   (c >= '\u08A0' && c <= '\u08FF') ||
                   (c >= '\uFB50' && c <= '\uFDFF') ||
                   (c >= '\uFE70' && c <= '\uFEFF');
        }

        private static bool IsDiacritic(char c)
        {
            // Arabic diacritics (tashkeel)
            return (c >= '\u064B' && c <= '\u065F') || c == '\u0670';
        }

        private static bool CanConnectAfter(char c)
        {
            // Can this letter connect to the next letter?
            return IsArabicChar(c) && !NonConnectingLetters.Contains(c);
        }

        private static bool CanConnectBefore(char c)
        {
            // Can this letter connect to the previous letter?
            return IsArabicChar(c);
        }

        private static char GetPrevLetter(string word, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (!IsDiacritic(word[i]))
                    return word[i];
            }
            return '\0';
        }

        private static char GetNextLetter(string word, int index)
        {
            for (int i = index + 1; i < word.Length; i++)
            {
                if (!IsDiacritic(word[i]))
                    return word[i];
            }
            return '\0';
        }

        private static string ReverseString(string s)
        {
            char[] arr = s.ToCharArray();
            System.Array.Reverse(arr);
            return new string(arr);
        }
    }
}
