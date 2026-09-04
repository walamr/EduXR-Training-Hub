using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer.Transcription
{
    /// <summary>
    /// Supported speech-to-text languages.
    /// </summary>
    public enum TranscriptionLanguage
    {
        English,
        Hebrew,
        Arabic
    }

    /// <summary>
    /// Helpers for mapping <see cref="TranscriptionLanguage"/> to codes, labels and prompts.
    /// </summary>
    public static class TranscriptionLanguageExtensions
    {
        /// <summary>BCP-47 style language code (en / he / ar).</summary>
        public static string Code(this TranscriptionLanguage lang)
        {
            switch (lang)
            {
                case TranscriptionLanguage.Hebrew: return "he";
                case TranscriptionLanguage.Arabic: return "ar";
                default: return "en";
            }
        }

        /// <summary>Full English name of the language (used in AI prompts).</summary>
        public static string DisplayName(this TranscriptionLanguage lang)
        {
            switch (lang)
            {
                case TranscriptionLanguage.Hebrew: return "Hebrew";
                case TranscriptionLanguage.Arabic: return "Arabic";
                default: return "English";
            }
        }

        /// <summary>Short 2-letter label shown on the UI button (EN / HE / AR).</summary>
        public static string ShortLabel(this TranscriptionLanguage lang)
        {
            switch (lang)
            {
                case TranscriptionLanguage.Hebrew: return "HE";
                case TranscriptionLanguage.Arabic: return "AR";
                default: return "EN";
            }
        }
    }

    /// <summary>
    /// Represents a single transcription entry from speech-to-text.
    /// </summary>
    [Serializable]
    public class TranscriptionEntry
    {
        public string text;
        public string speakerName;
        public ulong speakerId;
        public DateTime timestamp;
        public string languageCode;
        public float confidence;
        public bool isRTL;

        public TranscriptionEntry(string text, string speakerName, ulong speakerId)
        {
            this.text = text;
            this.speakerName = speakerName;
            this.speakerId = speakerId;
            this.timestamp = DateTime.UtcNow;
            this.languageCode = "en";
            this.confidence = 1f;
            this.isRTL = false;
        }

        /// <summary>
        /// Detects if text is RTL (Arabic, Hebrew, etc.)
        /// </summary>
        public void DetectRTL()
        {
            if (string.IsNullOrEmpty(text)) return;
            
            foreach (char c in text)
            {
                // Arabic range: 0x0600-0x06FF, Hebrew: 0x0590-0x05FF
                if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0590 && c <= 0x05FF))
                {
                    isRTL = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Complete meeting transcript with metadata.
    /// </summary>
    [Serializable]
    public class MeetingTranscript
    {
        public string meetingId;
        public string roomName;
        public DateTime startTime;
        public DateTime endTime;
        public List<TranscriptionEntry> entries = new List<TranscriptionEntry>();

        public MeetingTranscript(string roomName)
        {
            this.meetingId = Guid.NewGuid().ToString();
            this.roomName = roomName;
            this.startTime = DateTime.UtcNow;
        }

        public void AddEntry(TranscriptionEntry entry)
        {
            entries.Add(entry);
        }

        public void End()
        {
            endTime = DateTime.UtcNow;
        }

        public string ToJson()
        {
            return JsonUtility.ToJson(this, true);
        }

        public string GetFullText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach(var entry in entries)
            {
                sb.AppendLine($"[{entry.speakerName}]: {entry.text}");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Response from Wit.ai speech API.
    /// </summary>
    [Serializable]
    public class WitAiResponse
    {
        public string text;
        public WitAiIntent[] intents;
        public bool is_final;

        [Serializable]
        public class WitAiIntent
        {
            public string name;
            public float confidence;
        }
    }
}
