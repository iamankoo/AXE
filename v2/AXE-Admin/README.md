# AXE Admin (Android)

The admin client for the AXE v2 backend. **Phases 1-5 are implemented**: admin sign-in with secure session
storage, request management (pending list, detail, payment screenshot, approve / reject), push notifications (FCM) for new
requests, and production hardening (release guards and signing, fail-closed configuration, security tests). What was and was not
validated is in [`../PHASE5_VALIDATION.md`](../PHASE5_VALIDATION.md); deployment is in [`../PRODUCTION.md`](../PRODUCTION.md)
(real FCM delivery and a production backend are still untested). The
Windows side of the authorization workflow is documented in [`../AUTHORIZATION.md`](../AUTHORIZATION.md); this app did not
change for it.

- Location: `v2/AXE-Admin/` (separate Gradle project; AXE v1 is untouched)
- App name: **AXE Admin**; package / applicationId: `com.axe.admin`
- Kotlin 2.2, Jetpack Compose + Material 3 (dark only), min SDK 26, compile/target SDK 36
- Dependencies: OkHttp, kotlinx.serialization, coroutines, AndroidX lifecycle. No DI framework, no Room, no icon fonts.

## Architecture

The Android app is an **admin client of the existing backend**. It holds no authorization logic: decisions, signed
grants, expiry and all data deletion happen on the server; the Windows client remains the authority for polling, trusted
time and grant verification.

```
push/        Push notifications: PushRegistrar (token lifecycle), PushModel (payload parsing, deep links, permission policy), FirebasePush (Firebase glue, messaging service)
ui/          Compose screens: AppRoot, LoginScreen, AdminShell, RequestsScreen (list), RequestDetailScreen,
             Glyphs (drawn icons), Format, theme
viewmodel/   AuthViewModel (login), RequestsViewModel (list, selection, detail, screenshot, decisions)
repository/  AuthRepository, AdminRepository, RequestRepository (+ BackendRequestRepository)
auth/        SessionManager (restore, refresh, sign-out, AuthState), SecureStorage (encrypted persistence)
network/     HttpTransport (only place that does HTTP), AuthApi (Supabase Auth), AdminApi (admin function), DTOs
model/       Session, AuthState, AccessRequest, Decision
data/        AppContainer (manual wiring)
```

The UI never makes HTTP calls; composables only render ViewModel state.

## Backend contract used

Derived from `v2/backend/supabase/functions/admin/index.ts` and checked against the local backend. All admin calls send
`apikey: <anon key>` and `Authorization: Bearer <access token>`; `401` = invalid token or not an admin.

| Purpose | Request | Result |
| --- | --- | --- |
| Sign in | `POST {SUPABASE_URL}/auth/v1/token?grant_type=password` body `{email,password}` | tokens |
| Refresh | `POST .../auth/v1/token?grant_type=refresh_token` body `{refresh_token}` | tokens (rotating) |
| Sign out | `POST .../auth/v1/logout?scope=local` | ends this device's session |
| Admin check | `DELETE {SUPABASE_URL}/functions/v1/admin/devices` body `{}` | `200 {"ok":true}` for a listed admin |
| List | `GET .../functions/v1/admin/requests` | `{requests:[...]}` pending, unexpired, oldest first (max 100) |
| Detail | `GET .../admin/requests/:id` | `{request:{..., hasScreenshot}}`; `404` if not pending |
| Screenshot | `GET .../admin/requests/:id/screenshot` | raw image bytes (`no-store`); `404` if none / decided |
| Decision | `POST .../admin/requests/:id/decision` body `{"decision":"approve"\|"reject"}` | `200 {ok, decision, expiresAt}`; `409` already handled / expired |

The admin check still uses `DELETE /admin/devices` with an empty body (no dedicated "who am I" endpoint exists); it
authenticates and then does nothing. Phase 2 did not change it.

`expiresAt` in the decision response is the *grant's* expiry. Android deliberately does not parse, show or compute it.

## Request model (`AccessRequest`)

Exactly the fields the backend returns (`present()` in `admin/index.ts`); nothing is invented.

| Field | Notes |
| --- | --- |
| `id` | UUID; shown shortened ("Request ID: A3FCC9B2") |
| `kind` | `payment` / `invite` (unknown values render as a generic "Request") |
| `name`, `plan`, `planLabel` | blank name treated as missing |
| `expectedAmount` | server price for the plan (rupees) |
| `amountPaid`, `utr` | payment only; null otherwise |
| `amountMismatch`, `duplicateUtr` | server-computed warnings |
| `inviteCode` | invitation only |
| `createdAt`, `expiresAt` | unparseable timestamps become null instead of failing the list |
| `hasScreenshot` | detail endpoint only |

There is **no status field**: the backend lists only pending requests, so every listed request is pending (the
"Pending" pill states that). The backend does not return device information, so none is shown.

## Flows

**List.** On entering the shell the ViewModel loads once (repeat calls while a load is in flight are ignored). States:
loading, empty ("No pending requests"), error with retry, and the list (a failed refresh keeps the last list with a
warning). Refresh is the square button or the bottom button. Cards show the kind (orange payment / purple invitation),
name, plan (and price for payments), age, and the server's warnings.

**Detail.** Tapping a card shows the list row immediately, then fetches the detail. Payment and invitation requests show
their own fields; payments show the screenshot card. A request that is no longer pending (`404`) shows a message and
refreshes the list.

**Screenshot.** Fetched through the authenticated admin endpoint (never directly from Supabase Storage), held in memory
only, validated as an image, decoded off the main thread and downsampled to ≤1600 px wide. It is never written to disk
(no OkHttp cache is configured; the server sends `no-store`) and is dropped when the detail closes or the admin signs
out. Missing / invalid / network / server problems are shown separately with a retry where it makes sense.

**Decision.** Approve and Reject both ask for confirmation, then send only `{"decision": ...}`. While in flight the
buttons are disabled and back is blocked; a second tap is ignored. On success the request leaves the list and the list
is **re-fetched from the server** (an older refresh still in flight is cancelled so it cannot resurrect the row). On
`409`/`404` the app says the request was already handled, closes the detail and refreshes; it never reports a success it
did not get. Other failures keep the request open, show the error and restore the buttons. Android never creates a grant,
computes expiry, or alters amounts.

**Signing out** wipes the list, the open request and its screenshot from memory.

## Push notifications (Phase 4)

The backend sends a minimal FCM notification ("New payment request received. Tap to review." plus `{type, requestId}`,
nothing else) when a request is created; the app registers its FCM token with the authenticated backend, handles
rotation and sign-out, and on a tap fetches the real request from the backend (stale taps get "That request is no longer
available"). Foreground, background, terminated and signed-out behaviour, the permission flow, configuration and limits
are in [`../NOTIFICATIONS.md`](../NOTIFICATIONS.md). Push is off (and everything else works) when the build has no
Firebase configuration. The FCM server credential never enters the app.

| Endpoint | Use |
| --- | --- |
| `POST {SUPABASE_URL}/functions/v1/admin/devices` `{token, previousToken?}` | register / rotate this device's FCM token (owner = the signed-in admin) |
| `DELETE .../admin/devices` `{token}` | remove it at sign-out |

## Immediate data deletion

Requests are temporary and all deletion is server-side. See [`../backend/DATA_LIFECYCLE.md`](../backend/DATA_LIFECYCLE.md):
at decision the server clears all request/payment data and deletes the screenshot; the remaining result stub is deleted
when the Windows client acknowledges it has stored the result (at most 15 minutes after it first reads it); there is no
archive or soft delete. The Android app only displays
what the server returns.

## Authentication flow and secure storage

1. Email + password go to Supabase Auth (`password` grant). The password lives only in a Compose `remember` (never in a
   ViewModel, saved state, or logs). The app contains no account credentials; the backend validates them.
2. The backend is asked whether the account is an admin. A valid non-admin account is rejected and its session revoked.
3. The session is encrypted and stored. On start it is restored without network and validated lazily.
4. Access tokens refresh automatically (serialized; refresh tokens rotate). A `401` triggers one refresh and one retry.
   Refresh rejected → "session expired"; `401` again with a fresh token → "no longer allowed". Offline never signs out.
5. Logout clears locally at once, then revokes on the server best-effort.

The session is encrypted with **AES-256-GCM** under a **non-exportable Android Keystore key**; only Base64 ciphertext is
stored. The cipher chooses the IV (Keystore keys reject a caller-provided IV). Also: no backups, `FLAG_SECURE` on
**release** builds (blocks screenshots / recents thumbnails; debug builds omit it so they can be inspected with
`adb screencap`), redacted `Session.toString()`, no HTTP logging, system trust store, no cleartext in release.

## Configuration (client-safe only)

Only the Supabase URL and the **public anon key** are compiled in, read (git-ignored) from
`AXE-Admin/local.properties` (`axe.supabaseUrl`, `axe.anonKey`; see `admin.config.example.properties`) or, failing that,
`v2/config/server.json`. Never add the service-role key, database password, FCM credentials or signing keys.

Optional push config (public Firebase client identifiers, not credentials): `axe.firebase.projectId|appId|apiKey|senderId`
in `local.properties`, or a downloaded `app/google-services.json` (git-ignored). The `google-services` Gradle plugin is not
used, so the app builds without a Firebase project.

Local backend from a phone or emulator: `adb reverse tcp:54321 tcp:54321` and keep `http://127.0.0.1:54321`. Cleartext to
loopback is allowed in **debug builds only**.

## Build and test

Requires JDK 17+ (Android Studio's JBR works) and the Android SDK (`ANDROID_HOME`).

```powershell
cd v2\AXE-Admin
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
.\gradlew :app:assembleDebug          # app\build\outputs\apk\debug\app-debug.apk
.\gradlew :app:testDebugUnitTest      # JVM unit tests (135)
.\gradlew :app:lintDebug
.\gradlew :app:assembleRelease        # minified; signed when keystore.properties exists, otherwise UNSIGNED. Refuses dev/placeholder config
$env:ANDROID_SERIAL = "<device serial>"; .\gradlew :app:connectedDebugAndroidTest   # Keystore tests on a device (4)
```

Backend tests (need the local Supabase stack and `functions serve`, see the header of `backend/tests/api.test.mjs`):

```powershell
cd v2\backend; node --test --test-concurrency=1 tests/*.test.mjs   # 60 tests: api 22, push 18, security 9, pure 6, verify-fcm 5 (push tests need setup-local.mjs --fake-fcm, see NOTIFICATIONS.md)
```

The JVM tests cover the repository and API parsing with backend-shaped JSON (list, detail, screenshot, decisions, all
failure modes), the ViewModel state machine (loading, selection, decisions, duplicates, conflicts, stale refresh,
sign-out wipe), and the Phase 1 auth/session/storage tests. The instrumented test exercises the real Keystore, which a
JVM software key cannot (it caught a Phase 1 bug: see below).

## Phase 1 issue found and fixed during Phase 2

Sign-in never completed on a real device: saving the session threw `InvalidAlgorithmParameterException: Caller-provided
IV not permitted`, because the Keystore key requires randomized encryption and the code supplied its own IV. JVM tests
missed it (software keys allow it). Fixed in `AesGcmCipher.encrypt` (the cipher now generates the IV); the instrumented
test fails on the old code and passes on the fix.

## Manually tested (Vivo V2129, Android 13, USB, local backend through `adb reverse`)

Sign-in; list; refresh; payment detail with screenshot; invitation detail; cancel on a confirmation; reject; approve (each
checked against the real database/storage: request data null, screenshot gone, grant created only on approve); a request
decided elsewhere while open (shows "already handled", list refreshed); session restore after killing the app; sign-out.
Not manually tested: offline/airplane-mode behaviour, token expiry mid-session, tablets / landscape, and the real
production backend and account.

## Phase 4 manual tests (Vivo V2129, Android 13, USB, local backend)

Tested on the phone with **real pending requests** and simulated notification taps (`adb shell am start` with the same
`type`/`requestId` extras an FCM tap carries): a tap while signed out (only the sign-in screen, then the request opens after
signing in); a tap with the app in the background; a tap with the process killed (session restored, request opened); a stale
tap after another admin handled the request ("That request is no longer available", list refreshed); malformed and
foreign extras (ignored, no crash); the permission explanation shown once and the "Notifications are off" notice after
declining, with no re-prompt after a restart.

**Not tested, and not claimed:** delivery of a real FCM message (no Firebase project was available), real token
registration from Firebase, the foreground `onMessageReceived` path on a device, the system notification itself, and the
"Allow" branch of the system permission dialog. Those are covered by JVM tests of the logic and by backend tests against a
fake FCM server only.

## Release configuration (Phase 5)

A release build is refused unless the configuration is production-grade: HTTPS, not this machine, and real (non-placeholder)
Firebase identifiers. Debug builds alone accept a loopback server. Release signing uses a git-ignored `keystore.properties`
(`storeFile`, `storePassword`, `keyAlias`, `keyPassword`); without it the release APK is unsigned and cannot be installed.
Steps are in [`../PRODUCTION.md`](../PRODUCTION.md) section 6.

## What remains

Nothing is left to implement in the 5-phase roadmap. Still to do before production, all needing resources that did not exist:
a hosted Supabase project, a Firebase project (real push delivery test), a production release keystore, and the pending
production validation listed in [`../PHASE5_VALIDATION.md`](../PHASE5_VALIDATION.md).
