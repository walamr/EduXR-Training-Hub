using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRMultiplayer.Recording
{
    [Serializable]
    public class RecordingData
    {
        public string sessionName;
        public string date;
        public float duration;
        public string audioFileName; // Name of the associated Wav file
        public List<PlayerRecording> playerRecordings = new List<PlayerRecording>();
        
        // Scene geometry for accurate playback positioning
        public SceneGeometry sceneGeometry;
    }

    [Serializable]
    public class PlayerRecording
    {
        public ulong networkClientId;
        public string playerName;
        public List<RecordingFrame> frames = new List<RecordingFrame>();
    }

    [Serializable]
    public struct RecordingFrame
    {
        public float time; // Time relative to recording start
        public Vector3 headPos;
        public Quaternion headRot;
        public Vector3 leftHandPos;
        public Quaternion leftHandRot;
        public Vector3 rightHandPos;
        public Quaternion rightHandRot;
    }

    /// <summary>
    /// Stores the physical layout of the meeting scene for accurate playback.
    /// </summary>
    [Serializable]
    public class SceneGeometry
    {
        public Vector3 tablePosition;
        public Quaternion tableRotation;
        public Vector3 tableScale;
        public List<ChairData> chairs = new List<ChairData>();
    }

    /// <summary>
    /// Chair position and rotation data. Also network-serializable for playback sync.
    /// </summary>
    [Serializable]
    public struct ChairData : Unity.Netcode.INetworkSerializable
    {
        public Vector3 position;
        public Quaternion rotation;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer) where T : Unity.Netcode.IReaderWriter
        {
            serializer.SerializeValue(ref position);
            serializer.SerializeValue(ref rotation);
        }
    }

    /// <summary>
    /// Network-serializable player info for synchronized playback setup.
    /// </summary>
    public struct NetworkPlayerInfo : Unity.Netcode.INetworkSerializable
    {
        public ulong clientId;
        public Unity.Collections.FixedString64Bytes playerName;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer) where T : Unity.Netcode.IReaderWriter
        {
            serializer.SerializeValue(ref clientId);
            serializer.SerializeValue(ref playerName);
        }
    }

    /// <summary>
    /// Network-serializable frame data for streaming playback to all clients.
    /// </summary>
    public struct NetworkPlayerFrame : Unity.Netcode.INetworkSerializable
    {
        public ulong playerId;
        public Vector3 headPos;
        public Quaternion headRot;
        public Vector3 leftHandPos;
        public Quaternion leftHandRot;
        public Vector3 rightHandPos;
        public Quaternion rightHandRot;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer) where T : Unity.Netcode.IReaderWriter
        {
            serializer.SerializeValue(ref playerId);
            serializer.SerializeValue(ref headPos);
            serializer.SerializeValue(ref headRot);
            serializer.SerializeValue(ref leftHandPos);
            serializer.SerializeValue(ref leftHandRot);
            serializer.SerializeValue(ref rightHandPos);
            serializer.SerializeValue(ref rightHandRot);
        }
    }
}
