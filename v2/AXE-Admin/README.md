# AXE Admin (Android)

The admin client for the AXE v2 backend. **Phase 1 of 5: foundation only.** It signs an admin in,
keeps the session securely, and shows an authenticated shell with a Requests placeholder.
Request management, approve/reject, and push notifications are **not implemented yet** (see
[What remains](#what-remains)).

- Location: `v2/AXE-Admin/` (separate Gradle project; nothing in AXE v1 or the Windows app changed)
- App name: **AXE Admin**; package / applicationId: `com.axe.admin`
- Kotlin 2.2, Jetpack Compose + Material 3 (dark only), min SDK 26, compile/target SDK 36
- Dependencies: OkHttp, kotlinx.serialization, coroutines, AndroidX lifecycle. No DI framework, no Room.

## Architecture

The Android app is an **admin client of the existing backend**. It holds no authorization logic:
signed grants, trusted time, and decisions stay on the server and in the Windows client.

```
ui/          Compose screens (AppRoot, LoginScreen, AdminShell, RequestsScreen), theme
viewmodel/   AuthViewModel (login state), ShellViewModel (backend status)
repository/  AuthRepository (login/logout), AdminRepository (backend check)
auth/        SessionManager (restore, refresh, sign-out, AuthState), SecureStorage (encrypted persistence)
network/     HttpTransport (only place that does HTTP), AuthApi (Supabase Auth), AdminApi (admin function)
model/       Session, AuthState
data/        AppContainer (manual wiring)
```

The UI never makes HTTP calls; it talks to view models, which talk to repositories.

## Backend contract used

Derived from `v2/backend/supabase/functions/admin/index.ts` and checked against the local backend.

| Purpose | Request | Notes |
| --- | --- | --- |
| Sign in | `POST {SUPABASE_URL}/auth/v1/token?grant_type=password` | header `apikey: <anon key>`, body `{email,password}` |
| Refresh | `POST .../auth/v1/token?grant_type=refresh_token` | body `{refresh_token}`; Supabase rotates refresh tokens |
| Sign out | `POST .../auth/v1/logout?scope=local` | ends only this device's session |
| Admin check / connectivity | `DELETE {SUPABASE_URL}/functions/v1/admin/devices` with body `{}` | `Authorization: Bearer <access token>`; 200 = listed admin, 401 = not an admin / invalid token |

There is no dedicated "who am I" admin endpoint. `DELETE /admin/devices` with no `token` authenticates
the caller and then does nothing, so it is used as a side-effect-free admin check. A `GET /admin/me`
would be cleaner and can replace it later without touching the UI.

## Authentication flow

1. The admin enters email and password. The password is held only in a Compose `remember` (never saved instance state, never in a ViewModel, never logged).
2. Supabase Auth `password` grant returns access + refresh tokens. The existing admin account lives only in Supabase Auth.
3. The backend is asked whether the account is an admin (`/admin/devices` above). A valid account that is **not** in `public.admins` is rejected, its fresh session is revoked, and nothing is persisted.
4. The session is encrypted and stored; `AuthState` becomes `SignedIn`.
5. On app start the stored session is restored without network. It is validated lazily: the shell checks the backend on entry.
6. Before any admin call the access token is refreshed if it expires within 60 s (refreshes are serialized; refresh tokens rotate). A `401` triggers one refresh and one retry.
7. Refresh rejected, so signed out with "session expired". A `401` again after a fresh token, so signed out with "no longer allowed" (admin access revoked). Network/server failures never sign the admin out.
8. Logout clears the local session immediately, then revokes it on the server as a best effort.

## Secure storage

The session JSON is encrypted with **AES-256-GCM** (random 96-bit IV per write) using a
**non-exportable Android Keystore key**, and only the Base64 ciphertext is written to app-private
storage. No token is ever in plain `SharedPreferences`. A blob that fails to decrypt (tamper,
lost key) is deleted and treated as signed out. Other hygiene: `allowBackup=false` and data-extraction
rules exclude everything; `FLAG_SECURE` blocks screenshots/recents thumbnails; `Session.toString()` is
redacted; no HTTP logging interceptor exists; OkHttp uses the system trust store with normal hostname
verification; release builds forbid cleartext HTTP.

## Configuration (client-safe only)

Only the Supabase URL and the **public anon key** are compiled in. The build reads, in order:

1. `AXE-Admin/local.properties`: `axe.supabaseUrl=...` and `axe.anonKey=...` (see `admin.config.example.properties`)
2. `v2/config/server.json` (written by `v2/backend/scripts/setup-local.mjs`): URL derived from `functionsUrl`, plus `anonKey`

Both files are git-ignored. **Never** add the service-role key, database password, FCM credentials,
or signing keys. The app refuses to start (shows a configuration message) if the URL is not HTTPS
(loopback HTTP is accepted for local development) or the key is missing.

Local backend from a phone/emulator: run `adb reverse tcp:54321 tcp:54321` and keep `http://127.0.0.1:54321`.
Cleartext to loopback is allowed in **debug builds only** (`app/src/debug/res/xml/network_security_config.xml`).

## Build and test

Requires JDK 17+ (Android Studio's bundled JBR works) and the Android SDK (set `ANDROID_HOME` or `sdk.dir`).

```powershell
cd v2\AXE-Admin
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
.\gradlew :app:assembleDebug        # app\build\outputs\apk\debug\app-debug.apk
.\gradlew :app:testDebugUnitTest    # JVM unit tests (40)
.\gradlew :app:lintDebug            # lint
.\gradlew :app:assembleRelease      # minified, UNSIGNED (signing is Phase 5)
```

Unit tests cover login success/failure (wrong credentials, non-admin, rate limit, server error,
malformed response, network failure), logout, session restore, refresh/expiry/revocation, offline
handling, AES-GCM storage (round trip, no plaintext, tamper, wrong key), view-model state, and config validation.
The Android Keystore key itself is not exercised by JVM tests; it needs a device/emulator.

## What remains

- **Phase 2**: pending-request list, request details, payment screenshot viewing, approve/reject UI (`GET /admin/requests`, `.../screenshot`, `POST .../decision`).
- **Phase 3**: end-to-end Windows, backend, Android authorization workflow.
- **Phase 4**: FCM notifications (`POST/DELETE /admin/devices` with a real token), cleanup lifecycle.
- **Phase 5**: release signing, security/session review, offline polish, integration tests.
