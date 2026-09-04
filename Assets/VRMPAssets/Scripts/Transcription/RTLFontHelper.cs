using UnityEngine;
using TMPro;
using System.Collections.Generic;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Helper component to set up Arabic/Hebrew font support at runtime.
    /// Attach this to any GameObject with TMP_Text components that need RTL support.
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class RTLFontHelper : MonoBehaviour
    {
        [Header("Fallback Font (Optional)")]
        [SerializeField, Tooltip("Arabic/Hebrew font asset to use as fallback")]
        private TMP_FontAsset arabicFallbackFont;

        [Header("Settings")]
        [SerializeField, Tooltip("Enable RTL text mode")]
        private bool enableRTL = true;

        private TMP_Text tmpText;

        private void Awake()
        {
            tmpText = GetComponent<TMP_Text>();
            
            if (tmpText == null)
            {
                Debug.LogWarning("[RTLFontHelper] No TMP_Text found!");
                return;
            }

            // Add fallback font if specified
            if (arabicFallbackFont != null && tmpText.font != null)
            {
                if (tmpText.font.fallbackFontAssetTable == null)
                {
                    tmpText.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                }
                
                if (!tmpText.font.fallbackFontAssetTable.Contains(arabicFallbackFont))
                {
                    tmpText.font.fallbackFontAssetTable.Add(arabicFallbackFont);
                }
            }
        }

        /// <summary>
        /// Configures the TMP_Text for RTL display.
        /// </summary>
        public void SetRTL(bool isRTL)
        {
            if (tmpText == null) return;
            
            tmpText.isRightToLeftText = isRTL;
            
            if (isRTL)
            {
                tmpText.alignment = TextAlignmentOptions.TopRight;
            }
            else
            {
                tmpText.alignment = TextAlignmentOptions.TopLeft;
            }
        }

        /// <summary>
        /// Sets the fallback font for Arabic/Hebrew characters.
        /// </summary>
        public void SetArabicFallbackFont(TMP_FontAsset font)
        {
            arabicFallbackFont = font;
            
            if (tmpText != null && tmpText.font != null && font != null)
            {
                if (tmpText.font.fallbackFontAssetTable == null)
                {
                    tmpText.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                }
                
                if (!tmpText.font.fallbackFontAssetTable.Contains(font))
                {
                    tmpText.font.fallbackFontAssetTable.Add(font);
                }
            }
        }
    }
}
