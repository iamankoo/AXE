# AXE v2 admin notifications (FCM)

When a Windows AXE user submits a payment or invitation request, the AXE Admin phone is told with a push
notification. **FCM is only a doorbell.** It says "a request is waiting" and names the request; everything about the
request is fetched from the authenticated backend after the admin opens the app. The backend stays authoritative.

```
Windows AXE ──POST /access/requests──▶ backend ─ stores the (temporary) request
                                          │
                                          └─ background: push.ts ──FCM HTTP v1──▶ Google FCM ──▶ admin phone
                                                (server credential, never leaves the server)        │
                                                                                                    ▼
                                      tap ──▶ AXE Admin ──GET /admin/requests/:id (admin session)──▶ backend
                                              shows the REAL request (or "no longer available")
```

## What is sent

Exactly this (see `buildMessage` in `backend/supabase/functions/_shared/push.ts`):

```json
{
  "message": {
    "token": "<the device's FCM token>",
    "notification": { "title": "AXE Admin", "body": "New payment request received. Tap to review." },
    "data": { "type": "new_request", "requestId": "<uuid>" },
    "android": { "priority": "HIGH", "notification": { "channel_id": "axe_requests", "tag": "<uuid>" } }
  }
}
```

The body is "New invitation request received. Tap to review." for invitations. **Nothing else**: no name, amount, UTR,
invitation code, plan, screenshot, token or password. (Before Phase 4 the text contained the requester's name, amount,
UTR and invitation code; that was removed.) The request id is the minimum needed to open the right request; it is
only ever used to ask the backend for the request.

## Failure isolation

The push is sent **in the background after the request is stored** (`background()` in `_shared/common.ts`, built on the
edge runtime's `waitUntil`), and `notifyAdmins` never throws. A missing FCM configuration, an OAuth failure, an FCM
outage, a timeout (8 s per call) or a bad token can only cost the notification: the request is created, the Windows
client gets its `201`, and the admin can still find the request by opening or refreshing the Requests screen. Only
counts and HTTP statuses are logged, never tokens, credentials or request contents.

## Device tokens

Endpoints (`admin/index.ts`, admin session required; the owner is **always the authenticated admin**, never a
client-supplied id):

| Call | Effect |
| --- | --- |
| `POST /admin/devices {token, previousToken?}` | upsert the token for this admin (idempotent, refreshes it); if `previousToken` is given and belongs to this admin it is deleted (rotation); then keep this admin's 5 most recent tokens and delete the rest |
| `DELETE /admin/devices {token}` | delete this admin's token (sign-out); unknown or other admins' tokens are ignored |

* **Multiple devices:** supported. Every registered token of every admin receives one message per request (tokens are
  de-duplicated, so a token registered twice is notified once). Each admin is capped at 5 devices so records cannot
  accumulate.
* **Stale tokens:** a token is deleted only when FCM says *the token* is bad: HTTP 404 (`UNREGISTERED`), 403
  `SENDER_ID_MISMATCH`, or 400 "registration token ... not valid". A 400 about the payload, auth errors, 429 and 5xx
  never delete a working device (`classifyFcmFailure`).
* **Android side** (`push/PushRegistrar.kt`): the token is registered **only while an admin is signed in** (an
  unauthenticated install is never a push target); on sign-in and app start with a restored session the current token is
  registered (re-sent at most once a day so server clean-ups heal); `onNewToken` registers a rotated token at once with
  `previousToken` so the server replaces the old one; if the admin is signed out when a token rotates, nothing is sent
  and the current token is registered at the next sign-in.
* **Sign-out:** before the session is cleared, the app asks the server to delete its registration and then deletes the FCM
  token (`FirebaseMessaging.deleteToken`), so a signed-out phone stops receiving notifications even if the server call
  fails. A failed server removal is remembered and retried at the next sign-in. A signed-out phone that somehow still
  received a notification would learn nothing (the text is generic) and tapping it leads to the sign-in screen.

## Duplicates

A notification is sent once per created request (one `POST /access/requests` = one event). The FCM `tag` is the request
id, so the system replaces a repeated notification for the same request instead of stacking it; the app remembers the
last 50 request ids it has announced in the foreground and ignores repeats. No notification history table exists: a
notification is not request history, and the Phase 2/3 data lifecycle is unchanged (request data and the screenshot are
deleted at decision time; the result stub when acknowledged).

## Android behaviour

| State | What happens |
| --- | --- |
| **Foreground** | FCM calls `onMessageReceived`; the app shows an in-app "A new request arrived." notice and refreshes the list from the server (one refresh even if several pushes arrive; repeats of the same id are ignored). No second system notification. |
| **Background** | The system shows the notification. Tapping it brings `MainActivity` to the front (`singleTop`) with `type` and `requestId` as intent extras. |
| **Terminated** | Same tap; the process starts, the stored session is restored, and the extras are handled in `onCreate`. |
| **Signed out / session expired** | The tap is queued in memory (never persisted, expires after 10 minutes) and the sign-in screen is shown; no request data is displayed. After a successful sign-in the queued request is fetched and opened. |

On a tap, `MainActivity` accepts only `type = new_request` and a well-formed UUID (extras can be sent by any app), queues
it, and the signed-in shell calls `openFromNotification(id)`: it **fetches the request from the backend**
(`GET /admin/requests/:id`) and shows that. Nothing in the notification is ever displayed.

**Stale notifications.** If the request was approved, rejected, expired or deleted (by this or another admin session)
the backend answers 404; the app then shows "That request is no longer available. The list was refreshed.", closes the
detail and refreshes the list. No cached or invented details are shown. A tap never interrupts an approval or rejection
that is being sent.

## Notification permission (Android 13+)

The app targets Android 16 (API 36) and declares `POST_NOTIFICATIONS`. After sign-in, **once**, it explains why and asks
("Get notified about new requests" / "Allow notifications" or "Not now"); it never prompts again. If notifications are off
(denied, or disabled in system settings) a one-line notice on the Requests screen offers "Settings" (opens the app's
notification settings). The app works fully without the permission: requests can always be refreshed manually. On
Android 12 and lower no permission is needed.

## Configuration

**Server (secrets, never in git or in any client):**

| Secret | Purpose |
| --- | --- |
| `FCM_SERVICE_ACCOUNT_JSON` | Firebase service-account key (JSON). Without it the push is skipped and logged, nothing else changes |
| `AXE_PUSH_TEST_SINK_URL` | development only: also POST the same minimal message to a local URL |
| `AXE_FCM_API_BASE`, `AXE_FCM_OAUTH_URL` | development only: point the sender at a local fake FCM server. Honoured **only for loopback / Docker-host URLs** |

Production: `supabase secrets set FCM_SERVICE_ACCOUNT_JSON="$(cat service-account.json)"` and leave the three
development variables unset.

**Android (client-safe only):** the Firebase app identifiers (`projectId`, `appId`, `apiKey`, `senderId`) are *public
client identifiers*, not credentials. They are read at build time from git-ignored `AXE-Admin/local.properties`
(`axe.firebase.*`) or a downloaded `AXE-Admin/app/google-services.json` (git-ignored). The `google-services` Gradle plugin
is deliberately **not** used: Firebase is initialized from those identifiers at runtime, so the app builds and runs
without any Firebase project (push is then simply off). The FCM server credential is never in the APK, the sources, the
Windows app or any client configuration.

### Setting up real push delivery
1. Create a Firebase project; add an Android app with package `com.axe.admin`.
2. Put the downloaded `google-services.json` into `v2/AXE-Admin/app/` (git-ignored), or set the four
   `axe.firebase.*` properties in `v2/AXE-Admin/local.properties`. Rebuild the app.
3. Project settings, Service accounts, generate a private key; set it as the backend secret `FCM_SERVICE_ACCOUNT_JSON`
   (for local development put it in `backend/supabase/functions/.env`, which is git-ignored, and remove the
   `AXE_FCM_*` development overrides).
4. Sign in on the phone (the token is registered), create a request from Windows AXE, and the phone is notified.

To check a real Firebase service account **without a phone**, use `backend/scripts/verify-fcm.mjs` (see `../PRODUCTION.md`
section 4): it obtains an FCM access token and, given a device token, sends an FCM dry run (`validate_only`). It prints no
secrets. It has only been run against a local fake server so far.

### Local development without Firebase
`node backend/scripts/setup-local.mjs --sink http://host.docker.internal:8799/ --fake-fcm http://host.docker.internal:8798`
writes a throw-away RSA service account and local endpoints into the git-ignored `.env`. `backend/tests/push.test.mjs`
starts a fake FCM server on port 8798 and runs the **real** push code against it. This proves what AXE sends, to which
devices, and how it reacts to FCM errors; it does **not** prove delivery through Google's servers.

## Testing

* **Backend** (`node --test --test-concurrency=1 tests/*.test.mjs`): `push.test.mjs` covers authenticated/unauthenticated
  registration, ownership (a second admin account), idempotency, rotation, the device cap, sign-out removal, invalid
  input, payment and invitation notifications with a full check that no sensitive data is present, the request id leading
  to the real request through the admin API, multi-device and de-duplication, FCM 500 / OAuth failure not breaking request
  creation, the OAuth assertion's RS256 signature, stale-token classification, and the unchanged deletion lifecycle.
* **Android JVM tests:** token lifecycle (first registration, daily refresh, rotation, signed-out rotation, failures and
  retry, sign-out ordering and retry), device API contract, payload parsing and validation, deep-link queue (single use,
  expiry, sign-in wait), foreground pushes (dedupe, no loops), notification taps (real fetch, stale, offline, in-flight
  decision, switching), the sign-out hook, and the permission policy.

## Limitations

* **Real delivery through Google FCM was not tested**: no Firebase project or service-account key existed for this work.
  What is verified: the backend against a local fake FCM server (payload, recipients, OAuth JWT signature, failure
  isolation, stale-token handling), the Android token/deep-link/permission logic in JVM tests, and on a real phone the
  notification-tap handling for signed-out, background, terminated, stale and malformed cases (taps simulated with
  `adb shell am start` carrying the same extras). Not verified: Firebase token issuance, the FCM-displayed system
  notification, the foreground `onMessageReceived` path on a device, and the "Allow" branch of the system permission dialog.
* Devices are notified regardless of which admin is signed in on them (there is one shared admin team); tokens of
  signed-out phones are removed on sign-out, on FCM `UNREGISTERED`, or by the per-admin cap.
* An OEM battery manager can delay or drop notifications for apps that are force-stopped; nothing in the app can change that.
* If a request is replaced by the same PC's newer request, the older notification becomes stale and is handled as such.
