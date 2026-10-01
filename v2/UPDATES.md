# AXE v2 in-app updates

AXE v2 checks a **signed manifest** on Cloudflare R2 at startup. If a newer version exists it offers it, downloads the installer,
verifies it, closes itself, runs the installer silently and starts AXE again. Authorization and data are preserved because the
installer only replaces program files (`%LOCALAPPDATA%\AXE v2` is never touched).

```
AXE v2  --HTTPS-->  https://<public R2 host>/stable/latest.json      (signed manifest, short cache)
        --HTTPS-->  https://<public R2 host>/releases/<v>/AXE-v2-Setup-<v>.exe
```

AXE v1 has no updater and is not affected by any of this.

## Trust model

* The manifest is a **signed message**: `base64url(JSON) + "." + base64url(signature)`, ECDSA P-256 / SHA-256 over the base64url
  payload text, IEEE P1363 (64-byte r||s) — the same verified format AXE already uses for server grants (`SignedToken`), with
  `typ = "axe-update"`. No custom crypto: only .NET `ECDsa`.
* The **public** release key is embedded in AXE (`releasePublicKey` in `server.json`, built in at release time). The **private**
  key (`update-signing-private.jwk.json`) exists only on the release machine, outside the repository, and never ships.
* R2 only hosts public files. **No R2 credential, Cloudflare token, Supabase key, Firebase key or signing key is in AXE.** Even a
  full compromise of the bucket cannot make an installation run a file the release key did not sign: an attacker can replace
  objects but cannot produce a valid signature, and the installer's SHA-256 and size are inside the signed manifest.
* A **replayed old manifest** cannot downgrade anyone: a version that is not newer than the installed one is never offered.

## Manifest (signed payload)

| Field | Rule |
| --- | --- |
| `typ` | `axe-update` |
| `product` | `axe-v2` (anything else: rejected) |
| `version` | `major.minor.patch`, digits only; compared numerically; must be **newer** than the running build |
| `minimumSupportedVersion` | same format; must be <= `version`. A build older than this is **forced** to update |
| `downloadUrl` | HTTPS, **same host** as the manifest, no credentials/port/query/fragment, and exactly `/releases/<version>/AXE-v2-Setup-<version>.exe` (checked on the raw text, so `..`, `%2e%2e` and `\` cannot slip through) |
| `sha256` | 64 lower-case hex characters of the installer |
| `size` | bytes, between 1 MB and 500 MB; the download may not exceed it |
| `releaseDate` | `yyyy-MM-dd` |
| `releaseNotes` | text, at most 4000 characters (shown in the prompt) |
| `mandatory` | boolean; **required** |

A missing or malformed field rejects the whole manifest. Unknown extra fields are ignored.

## What the client does (`v2/src/AXEv2/Updates`)

1. **Check** (4 seconds after start, never blocking startup; skipped if the build has no update settings): GET the manifest over
   HTTPS (redirects are refused, max 32 KB), verify the signature, validate every field above.
   * `Unavailable` (offline, HTTP error, redirect): ignored silently; AXE keeps working.
   * `Rejected` (unsigned, tampered, malformed, wrong product, bad URL...): logged, **never acted on**, nothing is downloaded.
   * `UpToDate`: nothing newer.
   * `Available`: prompt.
2. **Prompt** (AXE-styled, protected from capture): current version, new version and date, release notes, **Update now** /
   **Later**. If the update is *enforced* (`mandatory`, or the build is older than `minimumSupportedVersion`) the title says
   "required", and the only choices are **Update now** and **Exit AXE**; declining closes AXE.
3. **Download** to `%LOCALAPPDATA%\AXE v2\updates\AXE-v2-Setup-<version>.exe.partial` with progress and Cancel. The size and the
   SHA-256 are verified as it streams; any mismatch, interruption, cancellation or HTTP error deletes the partial file and shows a
   message with **Try again**. Only after both match is the file renamed to its final name.
4. **Install**: the file on disk is verified **again**, it must be the exact expected name in AXE's update folder, then AXE runs
   `cmd /c ""<installer>" /SILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER|/ALLUSERS /LOG=... & start "" "<AXE-v2.exe>""` and closes. The
   installer replaces the program files (it never overwrites a running executable) and AXE is started again whether the
   installation succeeded or not, so a failed update never leaves the user without the app. Per-user vs all-users is chosen from
   where AXE is installed.
5. Leftovers (partial files, old installers) are removed at the next start.

## R2 layout

```
<bucket>/
  stable/latest.json                       the signed manifest AXE polls (the only mutable object; Cache-Control: no-cache)
  releases/<version>/AXE-v2-Setup-<version>.exe   immutable (Cache-Control: immutable)
  releases/<version>/manifest.json         immutable archive copy of that release's manifest
```

Previous releases stay in place for rollback. The first production bucket is `axe-v2-updates`, served through its **public
development URL** (`pub-<id>.r2.dev`). That URL is rate-limited and intended for testing; for heavy production use attach a
custom domain to the bucket and rebuild AXE with the new `updateManifestUrl` (the manifest host and download host must match).

## Release procedure (reproducible; no secret is ever printed or committed)

Secrets live in a folder **outside the repository** (e.g. `C:\Users\<you>\axe-production-secrets`):
`update-signing-private.jwk.json` (release key), `r2-credentials.json` (an R2 API token scoped to **Object Read & Write on this one
bucket**: `{ accountId, bucket, accessKeyId, secretAccessKey, publicBaseUrl }`), and `production-server.json` (client config, public
values only: it carries `updateManifestUrl` and `releasePublicKey`).

1. One time: `node v2/tools/release/generate-update-key.mjs --out <secrets folder>` (refuses to write inside a git repository or
   to overwrite a key). Put `updateManifestUrl` (`<publicBaseUrl>/stable/latest.json`) and `releasePublicKey` (from
   `update-public.json`) into `production-server.json`.
2. Raise the version in `v2/src/AXEv2/AXEv2.csproj` **and** `v2/installer/AXE-v2.iss`; run
   `v2/build/build-release-v2.ps1 -ServerConfig <production-server.json>` (tests, publish, secret scan, installer).
3. Copy `v2/release-v2/AXE-v2-Setup.exe` aside, then sign:
   `node v2/tools/release/sign-manifest.mjs --key <release key> --installer <exe> --version X.Y.Z --base-url <publicBaseUrl> --out <dir> --notes "..." [--minimum A.B.C] [--mandatory]`
   (computes the SHA-256 and size from the real file).
4. Publish: `node v2/tools/release/publish-r2.mjs --credentials <r2-credentials.json> --dir <dir> --version X.Y.Z`. It refuses to
   overwrite an existing release, uploads the installer and archive manifest, **verifies the installer through the public URL**,
   and only then updates `stable/latest.json` (and verifies that too). `--no-latest` publishes without moving the pointer.
5. Negative tests against the live endpoint (before moving the pointer for a risky release):
   `make-negative-fixtures.mjs` then `dotnet run --project v2/tools/AXEv2.EndToEnd -- update-negative <installed version>`
   (set `AXE_E2E_SERVER_JSON` to the production config); remove the fixtures with `--cleanup`.

## Rollback and recovery

* **Bad release published**: re-publish the previous good manifest as `stable/latest.json`
  (`publish-r2.mjs --put-file <releases/<good>/manifest.json copy> --key stable/latest.json`). Installations only ever move to a
  *newer* version than they run, so installs that already updated stay on the bad version: ship a **higher** version with the fix
  (never reuse a version number).
* **Lost release key**: nothing already published can be re-signed and no existing installation will trust a new key. Ship a new
  installer carrying a new `releasePublicKey` by other means, and generate a new key (`generate-update-key.mjs`). Back the private
  key up (offline vault).
* **Leaked R2 token**: roll it in the Cloudflare dashboard and update `r2-credentials.json`. A leaked R2 token alone cannot
  push an update that clients accept (signature), but can delete/replace objects: roll it promptly.
* **Leaked release key**: treat as the lost-key case, plus remove the manifest pointer until a new key is in users' hands.
* **A user's update fails**: the log is `%LOCALAPPDATA%\AXE v2\logs`; the installer log is `...\updates\install.log` until the next
  start. AXE is restarted automatically; the user simply stays on the old version and is offered the update again.

## Mandatory updates

`mandatory: true` enforces the update for everyone; raising `minimumSupportedVersion` enforces it only for builds older than it.
Enforcement is client-side after a successful, verified check: if the update server is unreachable AXE cannot learn about a
mandatory update and keeps running (a deliberate fail-open for availability; the server-side access control still applies).

## Known limitations

* The installer is **not code-signed** (Windows SmartScreen may warn on a manual download). The in-app update does not rely on
  Authenticode: it relies on the signed manifest + SHA-256.
* The public `r2.dev` URL is for testing/low volume (see above).
* A manifest cannot be revoked once clients have cached it; use a higher version to supersede.
* Updates are checked at startup only.
