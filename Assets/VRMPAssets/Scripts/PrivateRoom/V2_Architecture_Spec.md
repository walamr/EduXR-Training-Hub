# V2 Architecture Spec: Parallel Presence

## Overview
Phase 2 introduces true parallel presence, allowing users to participate in a private room while maintaining their connection and presence in the main room. This requires significant architectural changes to support dual contexts.

## Core Concepts

### 1. Dual-Context State Model
- **Main Context:** The primary session (e.g., the conference room).
- **Private Context:** The active private room session.
- **User State:** Each user maintains a state indicating their active context.

```csharp
public enum PresenceContext
{
    MainRoom,
    PrivateRoom
}

public struct UserPresenceState : INetworkSerializable
{
    public ulong ClientId;
    public PresenceContext ActiveContext;
    public ulong ActivePrivateRoomId; // 0 if in MainRoom
}
```

### 2. Channel Routing (Voice & Chat)
- **Voice:** `VoiceChatManager` must be updated to support channel-based routing.
  - Users in `PresenceContext.PrivateRoom` transmit voice data tagged with their `ActivePrivateRoomId`.
  - Clients filter incoming voice data based on their own `ActivePrivateRoomId`.
- **Chat:** `NetworkMessageBoard` messages must include a scope identifier.
  - Messages sent while in `PresenceContext.PrivateRoom` are tagged with the `ActivePrivateRoomId`.
  - The chat UI filters messages to show only those matching the user's active context.

### 3. Scene Management & Visibility
- **Local Scene:** The `PrivateRoom` scene is loaded additively or managed via a local scene switcher.
- **Visibility:** 
  - Main room avatars of users in a private room are either hidden, grayed out, or marked with an "In Private Room" indicator.
  - Inside the private room, only the avatars of other private room members are visible.
  - This requires a robust visibility manager that filters rendering based on `UserPresenceState`.

### 4. Podium Orchestration
- **Conflict Resolution:** If a user is in a private room and the main room host activates Podium Mode:
  - The user receives the notification but their local state (camera, audio) remains in the private room context.
  - A UI prompt allows them to "Return to Main Room" to participate in the podium.
- **State Synchronization:** Podium state (raised hands, mutes) must be scoped to the `PresenceContext`.

## Implementation Roadmap (V2)
1. **Networking Upgrade:** Implement channel-based routing in the transport layer or application logic for voice and chat.
2. **Presence Manager:** Create a `PresenceManager` to synchronize `UserPresenceState` across all clients.
3. **Visibility System:** Implement a rendering filter that uses `PresenceManager` data to cull avatars and objects based on context.
4. **UI Updates:** Add context indicators to the player list and overhead name tags.
5. **Podium Integration:** Update `PodiumManager` to respect `PresenceContext` and handle cross-context notifications.
