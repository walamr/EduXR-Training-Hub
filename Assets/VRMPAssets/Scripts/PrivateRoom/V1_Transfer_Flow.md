# V1 Transfer Flow Design

## Overview
In V1, entering a private room means leaving the current main room session and joining a newly created private session. This ensures strict isolation of voice, chat, and scene state without requiring complex dual-presence architecture.

## 1. Creation & Invite Flow
1. **Host Action:** User opens Quick Menu -> "Private Room" panel.
2. **Selection:** User selects up to 3 other players from the current room.
3. **Creation:** Host clicks "Create & Invite".
   - `PrivateRoomService` creates a new room record.
   - `PrivateRoomInviteService` sends invites to selected users.
4. **Host Transfer:** Host automatically begins transfer to the new private room session.

## 2. Transfer Sequence (Host & Invitees)
When a user transfers (either by creating or accepting an invite):
1. **State Capture:** Save the current main room's Join Code (or session ID) so the user knows where to return.
2. **Podium Cleanup:** If the user is in Podium Mode (either as host or with a raised hand), clear their local podium state.
3. **Disconnect:** Disconnect from the current `XRINetworkGameManager` session.
4. **Scene Load:** Load the `PrivateRoom` scene.
5. **Connect:** Use `SessionManager` to create (if host) or join (if invitee) the private session using a unique, hidden room code generated for this private room.

## 3. In-Room Experience
- The user is now in a standard Unity Netcode session, but isolated in the `PrivateRoom` scene.
- Voice and chat work normally but are naturally isolated because it's a separate session.
- A persistent "Return to Main Room" button is visible in the UI.

## 4. Return Flow
1. **User Action:** User clicks "Return to Main Room".
2. **Disconnect:** Disconnect from the private session.
3. **Scene Load:** Load the main scene.
4. **Connect:** Use `SessionManager` to rejoin the saved main room session.

## 5. Room Lifecycle & Auto-Destroy
- When a user leaves the private room (disconnects), the server checks the remaining player count.
- If the count reaches 0, the private room session is terminated and the room record is removed from `PrivateRoomService`.
