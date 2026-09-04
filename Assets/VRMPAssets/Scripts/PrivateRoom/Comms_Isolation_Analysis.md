# Voice and Chat Isolation Analysis

## Current Architecture
- **VoiceChatManager:** Handles voice communication. Currently tied to the active session.
- **NetworkMessageBoard:** Handles text chat. Currently tied to the active session.

## V1 Isolation Strategy (Transfer-Based)
Because V1 uses a transfer-based approach (leaving the main session and joining a new private session), strict isolation is achieved automatically by the underlying networking layer.

1. **Session Separation:** When a user enters a private room, they disconnect from the main `XRINetworkGameManager` session and connect to a new one.
2. **Voice Isolation:** `VoiceChatManager` will automatically route voice traffic only to members of the new private session. No cross-talk is possible because the user is no longer connected to the main session's voice channel.
3. **Chat Isolation:** `NetworkMessageBoard` operates on the current network session. Messages sent in the private room will only be replicated to other clients in that same private session.

## V2 Routing Requirements (Parallel Presence)
For V2, where users maintain a connection to the main room while participating in a private room, explicit routing logic will be required:

1. **Voice Routing:**
   - `VoiceChatManager` must support multiple channels or scopes (e.g., "Global" vs "PrivateRoom_123").
   - When a user speaks, their audio must be routed only to the "PrivateRoom_123" channel.
   - When a user listens, they should hear audio from "PrivateRoom_123". They may optionally hear "Global" audio at a reduced volume (ducking) or not at all, depending on the desired UX.

2. **Chat Routing:**
   - `NetworkMessageBoard` messages must include a scope identifier (e.g., `RoomId`).
   - The UI must filter incoming messages based on the user's current active scope.
   - Outgoing messages must be tagged with the user's current active scope.

3. **Presence Indicators:**
   - The main room's player list must indicate that a user is "In Private Room" so others know why they might not be responding to global voice/chat.
