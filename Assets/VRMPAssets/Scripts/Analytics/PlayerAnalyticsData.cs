using Unity.Collections;
using Unity.Netcode;

namespace XRMultiplayer
{
    /// <summary>
    /// Sentiment state based on detected head gestures.
    /// </summary>
    public enum SentimentState : byte
    {
        Neutral = 0,
        Positive = 1,  // Nod
        Negative = 2   // Shake
    }

    /// <summary>
    /// Analytics data for a single player.
    /// </summary>
    public struct PlayerAnalyticsData : INetworkSerializable
    {
        public ulong ClientId;
        public FixedString64Bytes PlayerName;
        public float SpeakingTimeSeconds;
        public bool IsLookingAtTarget;
        public SentimentState Sentiment;
        public float LastSentimentTime;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref PlayerName);
            serializer.SerializeValue(ref SpeakingTimeSeconds);
            serializer.SerializeValue(ref IsLookingAtTarget);
            serializer.SerializeValue(ref Sentiment);
            serializer.SerializeValue(ref LastSentimentTime);
        }
    }
}
