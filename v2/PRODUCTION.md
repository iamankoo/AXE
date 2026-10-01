# AXE v2: production setup, release and operations

This guide is the single place for taking AXE v2 (Windows app, Supabase backend, AXE Admin Android app, FCM) from the
local development stack to production. Nothing in it has been exercised against a real production Supabase or Firebase
project (none existed when it was written): every step marked **REQUIRES PRODUCTION CONFIGURATION** is unverified until
you run it. See [`PHASE5_VALIDATION.md`](PHASE5_VALIDATION.md) for exactly what was and was not tested.

## 0. Environments (never mix them)

| | Development (local) | Production |
| --- | --- | --- |
| Backend | `npx supabase start` (Docker), `functions serve` | a hosted Supabase project |
| Transport | `http://127.0.0.1:54321` (loopback only) | HTTPS only |
| Windows build | Debug (`dotnet build`) accepts a loopback server | **Release refuses** loopback/plain HTTP and **refuses to build** with a development `server.json` |
| Android build | Debug accepts loopback HTTP (network-security config for debug only) | **Release refuses** loopback and plain HTTP and **refuses to build** with a local/placeholder configuration |
| FCM | fake FCM server + throw-away key (`setup-local.mjs --fake-fcm`), or none | real Firebase project |
| Secrets | `backend/supabase/functions/.env` (git-ignored) | `supabase secrets set` (never in git) |

Fail-closed rules built in: a Release Windows build with an unusable/missing server configuration stays locked ("not
connected to a server"); the backend answers a generic 500 (and does nothing) if `AXE_HASH_SECRET` is unset or short, or the
signing key is missing; the development push sink and FCM endpoint overrides are honoured only for loopback URLs; an
unset FCM credential only skips the notification.

## 1. Supabase project  (REQUIRES PRODUCTION CONFIGURATION)

1. Create a Supabase project. Note the project URL (`https://<ref>.supabase.co`) and the anon (public) key.
2. Apply the schema: from `v2/backend`, `npx supabase link --project-ref <ref>` then `npx supabase db push`
   (applies `migrations/20260930000000_axe_access.sql`: tables with row-level security and **no policies** for
   `anon`/`authenticated`, the private `payment-screenshots` bucket, and the internal functions).
3. Authentication settings (dashboard, Authentication): **disable sign-ups**, keep refresh-token rotation on, consider
   requiring MFA for the admin, and set a password policy. (`config.toml` already disables sign-ups for the local stack.)
4. Deploy the two functions: `npx supabase functions deploy access` and `npx supabase functions deploy admin`.
   Both authenticate their callers themselves (poll-secret / admin session / signed grants). If you use the newer
   non-JWT "publishable" API keys, deploy with `--no-verify-jwt`; with the legacy anon JWT the default is fine.
5. Schedule nothing else: expired data is removed opportunistically on every poll/submission/admin list. (Optional:
   call the functions periodically so cleanup also runs while idle.)

## 2. Secrets  (REQUIRES PRODUCTION CONFIGURATION)

Generate the cryptographic material **outside the repository**:

```
node v2/backend/scripts/generate-production-keys.mjs --out D:\secure\axe-production
```

It writes the signing key (secret), a hash secret (secret) and the public verification key (goes into the clients). Back the
files up in a password manager or offline vault: losing the signing key invalidates every issued grant.

```
supabase secrets set AXE_SIGNING_PRIVATE_JWK="$(cat signing-private.jwk.json)"
supabase secrets set AXE_HASH_SECRET="$(cat hash-secret.txt)"
supabase secrets set AXE_INVITE_CODES="CODE1,CODE2"           # the invitation codes; change to rotate
supabase secrets set FCM_SERVICE_ACCOUNT_JSON="$(cat firebase-service-account.json)"   # see section 4
```

| Secret | Required | Notes |
| --- | --- | --- |
| `AXE_SIGNING_PRIVATE_JWK` | yes | P-256 private JWK; signs grants and status tokens. Without it nothing can be authorized |
| `AXE_HASH_SECRET` | yes (16+ chars) | keys invitation-code, UTR and client-address hashes. Unset/short = the backend refuses to run |
| `AXE_INVITE_CODES` | yes for invitations | comma-separated, case-insensitive. **Rotate any code that has ever been committed or shared** |
| `FCM_SERVICE_ACCOUNT_JSON` | for push | absent = requests still work, admins just aren't notified |
| `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY` | provided by the platform | never copy the service-role key anywhere else |

Do **not** set in production: `AXE_PUSH_TEST_SINK_URL`, `AXE_FCM_API_BASE`, `AXE_FCM_OAUTH_URL`, `AXE_SUBMIT_LIMIT`,
`AXE_INVITE_ATTEMPT_LIMIT` (development/test overrides; the built-in defaults are 10 submissions and 20 invitation attempts per
hour per network).

## 3. The admin account  (REQUIRES PRODUCTION CONFIGURATION)

The intended admin is `admin@axe.com`. Its password is never stored in source, tests, configuration or the app.
Provision it **outside the code**: create the user in the Supabase dashboard (Authentication, Users, "Add user", auto-confirm)
with a strong password you choose, then authorise it:

```sql
insert into public.admins (user_id) select id from auth.users where email = 'admin@axe.com';
```

Only users listed in `public.admins` can use the admin API; everyone else (even a valid Supabase user) gets 401. To remove an
admin: delete the row (their sessions stop working at once, because every call re-checks the table).

## 4. Firebase / FCM  (REQUIRES PRODUCTION CONFIGURATION; real delivery NOT TESTED)

1. Firebase console: create a project; add an **Android app with package `com.axe.admin`**.
2. Download `google-services.json` into `v2/AXE-Admin/app/` (git-ignored). Its values (project id, app id, API key, sender id) are
   public client identifiers, not secrets; the build reads them into `BuildConfig`. The `google-services` Gradle plugin is not used.
3. Project settings, Service accounts, "Generate new private key": this JSON **is a secret**. Store it only in the
   backend secret `FCM_SERVICE_ACCOUNT_JSON` (and your vault). Never commit it, never put it in the app.
4. Verify the credential without a phone:
   `node v2/backend/scripts/verify-fcm.mjs --service-account firebase-service-account.json`
   and, with a token from the signed-in phone, a dry run that proves the project matches the app:
   `... --token <FCM token>` (add `--send` to deliver one generic test notification).
5. Android 13+ asks for notification permission once after sign-in; the app works without it.

## 5. Windows release  (REQUIRES PRODUCTION CONFIGURATION)

Create a production `server.json` (HTTPS only):

```json
{ "functionsUrl": "https://<ref>.supabase.co/functions/v1", "anonKey": "<anon key>", "signingPublicKey": "<client-public.json value>" }
```

```
dotnet build v2/src/AXEv2/AXEv2.csproj -c Release -p:AxeServerConfig=D:\secure\axe-production\server.json
```

The build fails if the configuration is a development one. The output (`bin\Release\net8.0-windows`) contains only that
public configuration. Installer packaging and code signing are outside what was validated here.

## 6. Android release  (REQUIRES PRODUCTION CONFIGURATION)

1. Create your **release keystore once** and back it up (losing it means you can never update the installed app):
   `keytool -genkeypair -v -keystore axe-admin-release.jks -alias axe-admin -keyalg RSA -keysize 4096 -validity 10000`
2. Create `v2/AXE-Admin/keystore.properties` (git-ignored): `storeFile=`, `storePassword=`, `keyAlias=`, `keyPassword=`.
3. Create `v2/AXE-Admin/local.properties` (git-ignored) with `axe.supabaseUrl=https://<ref>.supabase.co` and `axe.anonKey=...`
   (and the `axe.firebase.*` values, or use `google-services.json`).
4. `./gradlew :app:assembleRelease`. The build **fails** if the URL is not HTTPS, points at this machine, or the Firebase
   identifiers are placeholders; it warns when no keystore is configured (the APK would be unsigned and uninstallable).
5. Check: `apksigner verify --verbose app/build/outputs/apk/release/app-release.apk`, then install on the admin phone.

The release build: R8-minified and resource-shrunk, not debuggable, backups disabled, no cleartext traffic, `FLAG_SECURE` on
(no screenshots/recents thumbnails), session tokens encrypted with an Android Keystore key, no logging.

## 7. After deployment: smoke test

1. `verify-fcm.mjs` passes (section 4). 2. Sign in on the phone as the admin; confirm a row in `admin_devices`.
3. From a Windows build, submit an invitation request: the phone shows a notification; tap it; the request opens.
4. Approve: Windows becomes Active with the plan's duration; the request row and screenshot are gone (check the tables and
   the bucket); the grant row exists. 5. Repeat with a rejection. 6. Sign out: the `admin_devices` row disappears.

`v2/tools/AXEv2.EndToEnd` can drive steps 3-5 from a developer PC against any server whose HTTPS `server.json` you point it at.

## 8. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every backend call returns "The AXE server hit a problem" | `AXE_HASH_SECRET` or `AXE_SIGNING_PRIVATE_JWK` missing/short (check function logs) |
| Windows says "isn't connected to an AXE server" | the embedded `server.json` was missing or unusable (e.g. loopback/HTTP in a Release build) |
| Windows: "access could not be verified" | `signingPublicKey` in the client does not match the server's private key |
| Admin app: "This account is not an AXE admin" | the user has no row in `public.admins` |
| Admin app shows no notification | no Firebase config in the build, permission denied, no row in `admin_devices`, or `FCM_SERVICE_ACCOUNT_JSON` unset (check `verify-fcm.mjs`; the request itself is unaffected) |
| "Too many invitation attempts" | 20 invitation attempts/hour/network; wait an hour |
| Release build refuses to build | it detected a development configuration; see section 6 |
| Sign-in works but requests never load | admin function not deployed / wrong URL / clock skew on the phone (token validity) |

## 9. Security assumptions and known limitations

* The server is authoritative for approval, expiry and time. Windows verifies ECDSA-signed grants with a public key only and
  derives remaining time from server time plus a monotonic counter; the local clock cannot extend access.
* **Device binding is enforced by the Windows client, not the server**: the server signs the requesting PC's device hash into
  the grant and cannot itself tell which PC presents it. A modified client could ignore the check; closing that needs a device
  key pair, which is out of scope.
* Invitation codes are guarded by a per-network attempt limit, not secrecy alone; they still need admin approval to grant access.
* The 7-day keyed HMAC of approved payment references (duplicate detection) is the one deliberate retention exception; no
  plaintext UTR, screenshot or request history is kept after a decision.
* An invitation code from the project's first commit is in git history and is therefore public: **never reuse it; production `AXE_INVITE_CODES` must contain only new codes** (set through `deploy-production.ps1`, never written into a repository file).
* Real Google FCM delivery, a hosted Supabase project, and real-hardware clock manipulation were not tested (see the validation report).

## 10. Deployment record and release procedure (as performed for the first production release)

Secrets live in a folder **outside the repository** (for example `C:\Users\<you>\axe-production-secrets`): the server signing key and
hash secret (`generate-production-keys.mjs`), the Supabase access token (short-lived, delete it afterwards), the Firebase
service-account key, the Android release keystore, and the public `production-server.json`. None of them is ever committed.

1. Create the Supabase project; apply `backend/supabase/migrations/*` in order (SQL editor or `supabase db push`).
2. In Auth: disable sign-ups and anonymous sign-ins; create the admin user with a password typed in the dashboard; add its id to
   `public.admins`.
3. `backend/scripts/deploy-production.ps1` sets the secrets (it prompts for invitation codes; use **new** codes) and deploys the
   functions with `--no-verify-jwt --use-api`. After the Firebase step, `-FcmOnly -FcmServiceAccountFile <key>` sets only the FCM
   secret. `backend/scripts/verify-fcm.mjs` checks a service-account key against real Google without a phone.
4. Firebase: register the Android app `com.axe.admin`, place its `google-services.json` in `AXE-Admin/app/` (git-ignored).
5. Windows: `build/build-release-v2.ps1 -ServerConfig <production-server.json>` runs the tests, publishes, scans the output for
   secrets, compiles `installer/AXE-v2.iss` and prints the SHA-256. The result is exactly `release-v2/AXE-v2-Setup.exe`.
6. Android: put the production URL and anon key in `AXE-Admin/local.properties`, the keystore in `keystore.properties` (both
   git-ignored), then `./gradlew assembleRelease`; copy `app-release.apk` to `release-v2/AXE-v2-Phone.apk` and record its hash.
   (In `local.properties`, escape `:` as `\:` or lint reports `PropertyEscape`.)
7. After the release is verified, revoke the Supabase access token.
