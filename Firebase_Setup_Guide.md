# Firebase Setup Guide — Personal Cloud Snapshots

## Who needs to do this?

The owner of the Firebase project **xr-meeting-hub** (the person who created it in Firebase Console).

## What needs to be done?

Two things need updating in Firebase Console:

1. **Realtime Database Rules** — so the VR headset pairing flow works
2. **Storage Rules** — so each user's screenshots upload to their private folder

---

## Step 1: Update Realtime Database Rules

1. Go to https://console.firebase.google.com
2. Select the **xr-meeting-hub** project
3. In the left sidebar, click **Realtime Database**
4. Click the **Rules** tab
5. **Replace** the entire content with this:

```json
{
  "rules": {
    "deviceCodes": {
      "$code": {
        ".read": true,
        ".write": true
      }
    },
    "pairings": {
      "$code": {
        ".read": true,
        ".write": true
      }
    },
    "rooms": {
      "$roomId": {
        ".read": true,
        ".write": true
      }
    },
    "$other": {
      ".read": false,
      ".write": false
    }
  }
}
```

6. Click **Publish**

### What these rules do:

| Path | Purpose | Access |
|------|---------|--------|
| `/deviceCodes/` | VR headset pairing codes | Open (codes are short-lived and deleted after use) |
| `/pairings/` | Google Drive linking | Open (same as above) |
| `/rooms/` | Presentation sync between participants | Open (needed for real-time slide sync) |
| Everything else | Blocked | Denied |

---

## Step 2: Update Storage Rules

1. Still in Firebase Console, click **Storage** in the left sidebar
2. Click the **Rules** tab
3. **Replace** the entire content with this:

```
rules_version = '2';

service firebase.storage {
  match /b/{bucket}/o {

    // Per-user screenshots (Personal Cloud Snapshots)
    // Each user can only read/write their own folder
    match /Users/{userId}/{allPaths=**} {
      allow read, write: if request.auth != null
                         && request.auth.uid == userId;
    }

    // Presentation documents
    // Owner writes, all authenticated users can read (for viewing slides)
    match /presentations/{userId}/{allPaths=**} {
      allow read:  if request.auth != null;
      allow write: if request.auth != null
                   && request.auth.uid == userId;
    }

    // Audit logs
    // Only the owner can read/write their own logs
    match /audit_logs/{userId}/{allPaths=**} {
      allow read, write: if request.auth != null
                         && request.auth.uid == userId;
    }

    // Default: deny everything else
    match /{allPaths=**} {
      allow read, write: if false;
    }
  }
}
```

4. Click **Publish**

### What these rules do:

| Path | Purpose | Access |
|------|---------|--------|
| `Users/{userId}/` | Personal screenshots | Only that user (private) |
| `presentations/{userId}/` | Uploaded slide decks | Owner writes, everyone reads |
| `audit_logs/{userId}/` | Session CSV logs | Only that user |
| Everything else | Blocked | Denied |

---

## Step 3: Verify Authentication is Enabled

1. In Firebase Console, click **Authentication** in the left sidebar
2. Click the **Sign-in method** tab
3. Make sure **Google** is listed and **Enabled**
4. If not, click **Add new provider** > **Google** > Enable it > Save

---

## How to verify it works

After the rules are published:

1. Open the VR app (or Unity Editor)
2. Join a session
3. Open the Firebase Presentation panel and click **Generate Code**
4. Go to https://xr-meeting-hub.web.app/pair on a phone/browser
5. Enter the code and sign in with Google
6. Back in VR, the pairing should succeed
7. Press the **Capture** button (wrist or Right B on Quest) — the screenshot should upload to Firebase Storage under `Users/{your-uid}/Meeting_Screenshots/`

---

## Screenshot storage path

Each user's screenshots are stored at:

```
Storage/Users/{UserID}/Meeting_Screenshots/{YYYY-MM-DD}/shot_YYYYMMDD_HHMMSS.png
```

These are completely private — no other user can access them.

---

## Time needed

This takes about 2 minutes. Just copy-paste the rules and click Publish.
