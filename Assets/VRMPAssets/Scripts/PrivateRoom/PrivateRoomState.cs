using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace XRMultiplayer.PrivateRoom
{
    /// <summary>
    /// Represents the state of a single private room.
    /// </summary>
    public struct PrivateRoomState : INetworkSerializable, IEquatable<PrivateRoomState>
    {
        public ulong RoomId;
        public ulong OwnerId;
        public int MaxMembers;
        public bool IsActive;
        // Stable grid cell (0..max-1) assigned at creation; drives the room's unique world offset.
        public int CellIndex;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref RoomId);
            serializer.SerializeValue(ref OwnerId);
            serializer.SerializeValue(ref MaxMembers);
            serializer.SerializeValue(ref IsActive);
            serializer.SerializeValue(ref CellIndex);
        }

        public bool Equals(PrivateRoomState other)
        {
            return RoomId == other.RoomId && OwnerId == other.OwnerId && MaxMembers == other.MaxMembers && IsActive == other.IsActive && CellIndex == other.CellIndex;
        }
    }

    /// <summary>
    /// Tracks which client belongs to which private room.
    /// </summary>
    public struct PrivateRoomMember : INetworkSerializable, IEquatable<PrivateRoomMember>
    {
        public ulong ClientId;
        public ulong RoomId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref RoomId);
        }

        public bool Equals(PrivateRoomMember other)
        {
            return ClientId == other.ClientId && RoomId == other.RoomId;
        }
    }

    /// <summary>
    /// Represents an active invite to a private room.
    /// </summary>
    public struct PrivateRoomInvite : INetworkSerializable, IEquatable<PrivateRoomInvite>
    {
        public ulong InviteId;
        public ulong RoomId;
        public ulong SenderId;
        public ulong TargetId;
        public bool IsAccepted;
        public bool IsDeclined;
        // Synced server time (seconds) when the invite was created; used for expiry.
        public double CreatedServerTime;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref InviteId);
            serializer.SerializeValue(ref RoomId);
            serializer.SerializeValue(ref SenderId);
            serializer.SerializeValue(ref TargetId);
            serializer.SerializeValue(ref IsAccepted);
            serializer.SerializeValue(ref IsDeclined);
            serializer.SerializeValue(ref CreatedServerTime);
        }

        public bool Equals(PrivateRoomInvite other)
        {
            return InviteId == other.InviteId && RoomId == other.RoomId && SenderId == other.SenderId && TargetId == other.TargetId && IsAccepted == other.IsAccepted && IsDeclined == other.IsDeclined && CreatedServerTime == other.CreatedServerTime;
        }
    }
}
