# AXE Admin (Android)

The admin client for the AXE v2 backend. **Phases 1-2 of 5 are implemented**: admin sign-in with secure session
storage, and request management (pending list, detail, payment screenshot, approve / reject).
Notifications (FCM) and production hardening are **not implemented yet** (see [What remains](#what-remains)). The
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

Local backend from a phone or emulator: `adb reverse tcp:54321 tcp:54321` and keep `http://127.0.0.1:54321`. Cleartext to
loopback is allowed in **debug builds only**.

## Build and test

Requires JDK 17+ (Android Studio's JBR works) and the Android SDK (`ANDROID_HOME`).

```powershell
cd v2\AXE-Admin
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
.\gradlew :app:assembleDebug          # app\build\outputs\apk\debug\app-debug.apk
.\gradlew :app:testDebugUnitTest      # JVM unit tests (85)
.\gradlew :app:lintDebug
.\gradlew :app:assembleRelease        # minified, UNSIGNED (signing is Phase 5)
$env:ANDROID_SERIAL = "<device serial>"; .\gradlew :app:connectedDebugAndroidTest   # Keystore tests on a device (4)
```

Backend tests (need the local Supabase stack and `functions serve`, see the header of `backend/tests/api.test.mjs`):

```powershell
cd v2\backend; node --test tests/api.test.mjs    # 22 tests, incl. lifecycle, delivery and expiry tests against real DB/storage state
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

## What remains

- **Phase 4**: FCM notifications (`POST/DELETE /admin/devices` with a real token) and notification lifecycle.
- **Phase 5**: release signing, security/session review, offline polish, integration tests.
