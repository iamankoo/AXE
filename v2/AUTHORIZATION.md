# AXE v2 authorization lifecycle

How a Windows AXE v2 copy gets, uses and loses timed access. Everything here describes behaviour that exists in the
code and is covered by tests (see [Testing](#testing)). The backend is authoritative for approval, grants, expiry and
time; the Windows client only verifies and follows; the Android Admin app only sends decisions.

```
Windows AXE v2 (AccessController)          Supabase Edge Functions                      Android Admin
─────────────────────────────────          ───────────────────────                      ─────────────
POST /access/requests ───────────────────▶ validate, store request (+screenshot)
   (name, plan, payment | invite code,     returns {requestId, pollToken}
    device hash)  ◀────────────────────────
saves pendingId + pollToken (DPAPI)
GET  /access/requests/:id (every 4 s) ───▶ signed status {pending}
                                                                                        GET  /admin/requests
                                                                                        POST /admin/requests/:id/decision
                                           approve: sign grant, insert access_grants
                                           all request/payment data cleared, screenshot deleted
                                           row kept only as a minimal result stub
GET  /access/requests/:id ───────────────▶ signed status {approved, grant, now}
verify grant (see below), sync trusted
clock, SAVE grant, enter Active
DELETE /access/requests/:id (ack) ───────▶ stub deleted at once
POST /access/session (resume / resync) ──▶ signed {valid, now, exp}
```

## Request submission
The client sends the name, the chosen plan, either payment details (amount, UTR, screenshot) or an invitation code,
and its **device hash**. The server validates everything itself (invitation codes are checked only on the server, the plan
price is decided on the server), stores the request, returns a request id and a secret **poll token**, and notifies
admins.

Valid invitation codes are the server secret `AXE_INVITE_CODES` (comma-separated, matched case-insensitively). Locally it
lives in `supabase/functions/.env` (git-ignored; `backend/scripts/setup-local.mjs` creates it and keeps existing values).
A deployed project holds it in its own secret store, for example
`supabase secrets set AXE_INVITE_CODES="CODE1,CODE2"`. To add a code, append it to that list; the apps do not contain codes. The client persists only the request id and poll token (encrypted with DPAPI for the current user). One pending
request per PC: a new one replaces the old.

## Admin decision (server side)
`POST /admin/requests/:id/decision` (admin session required):
1. The request must still be pending and unexpired, otherwise `409`.
2. **Approve:** the server creates a grant `{typ:"axe-grant", jti, rid, plan, iat, exp, did}` and signs it with its
   ECDSA P-256 private key (which exists only in the server's secret store). `rid` is the request id, `did` the requesting
   device hash, `iat` the server time now, `exp = iat + plan hours` computed on the server. The grant is recorded in
   `access_grants` (this is the active authorization).
3. A conditional update moves the request from `pending` to `approved` / `rejected` (only one decision can win).
4. All request/payment data is cleared and the screenshot deleted (see [data deletion](#data-deletion)).

Plans and durations are locked: 1 hour ₹149, 5 hours ₹199, 10 hours ₹299.

## Windows: receiving the decision
`AccessController` polls with the poll token and a fresh nonce. Every answer is an ECDSA-signed token verified with the
public key embedded in the build, bound to the nonce and request id (no replay, no forged status, no fake server time).
* **pending**: keep waiting.
* **rejected**: clear the pending request and its poll token, show the access screen with "Your request was not
  approved", never grant anything, acknowledge.
* **expired** / `404` (request gone): clear and ask the user to submit again.
* **approved**: verify the grant, then store it, then activate (below).
* **Network/server errors** (offline, timeouts, 5xx, 429) are never a decision: the request stays pending and polling
  backs off and retries.

## Grant validation (Windows)
The grant is accepted only if **all** of these hold (any failure: discarded, pending cleared, access screen shown, never
active):
* valid ECDSA signature with the shipped public key (no signing key exists on the PC);
* token type is `axe-grant`;
* `did` equals this PC's device hash (a grant for another PC is useless);
* `rid` equals the request this PC made (when approving);
* `jti` present; `plan` is a known plan; `exp > iat`;
* `exp - iat` equals the plan's duration (the server always issues exactly that; a grant whose length disagrees with its
  plan was not issued by this server);
* not already expired against the **trusted** time (server time, below).

## Device binding
The device hash is SHA-256 of a random 32-byte per-PC secret created on first use and stored with DPAPI (current user,
this PC). The server puts it into the grant (`did`); Windows compares it on approval and on every resume. Copying
`access.dat` to another PC cannot be decrypted there, and a grant for PC A is rejected by PC B. The limit of this
design: it binds to the DPAPI secret, not to hardware attestation.

## Trusted server time
`TrustedClock` = last verified server time + a monotonic tick counter (`Environment.TickCount64`, unaffected by
changing the Windows clock or time zone, and it keeps counting through sleep). The remaining time is
`exp - (serverTime + ticksSinceSync)`. The wall clock is never read, so:
* changing Windows time forward or back neither adds nor removes access;
* restarting AXE does not reset the timer: the grant's `exp` is absolute server time, and a restart re-syncs with the server;
* editing the saved file cannot extend access (the expiry lives inside the signed grant; a modified grant fails its signature);
* the clock is re-synced with the server every 5 minutes and after the PC wakes from sleep.

## Session resume (AXE restarted)
A saved grant is verified locally (same checks, including device) and then checked with the server
(`POST /access/session`):
* valid → Active with the **remaining** server time;
* expired / revoked / unknown → saved grant deleted, access screen shown (revocation message if the admin ended it);
* server unreachable → **Offline** and locked: without the server's time the expiry cannot be trusted, so a stale grant
  never opens the browser. Retry is offered.

Mid-session, if a periodic check cannot reach the server, AXE keeps counting down on the monotonic clock (this can only
ever end access on schedule, never extend it); if the server says revoked/expired, access ends immediately.

## Expiry
The 1-second tick compares the trusted remaining time with zero. At zero: the grant is deleted from disk, the phase
becomes `NeedsAccess`, the browser is hidden **and muted** (so media cannot keep playing behind the lock screen), and a new
request is required. There is no grace and no local extension.

## Browser access boundary
`AccessPhase.AllowsBrowsing()` is the only rule: true for `Active`, false for `NotConfigured`, `Checking`, `NeedsAccess`,
`Pending`, `Offline`. `MainWindow` shows the browser only when it is true; the WebView2 engine is not even initialized before
the first `Active`; every browser action re-checks `Phase == Active`; leaving `Active` hides all tabs and mutes them.

## Reliable result delivery (and the Phase 2 edge case)
Phase 2 deleted the result stub on the first read, so a lost response meant "request no longer active" and the user had
to pay/submit again. Now:
1. **Reading a result does not delete it.** A retry returns the same signed result, so a lost response, a crash before the
   grant was saved, or a restart in the middle are all recoverable.
2. **Acknowledgement.** After the client has **saved** the grant (approval) or handled the rejection, it sends
   `DELETE /access/requests/:id` with the poll token. The server deletes the stub at once. The call is idempotent and
   needs the poll secret; a wrong secret or unknown id deletes nothing and leaks nothing.
3. **Bounded lifetime without an ack.** On the first read the server shortens the stub's expiry to 15 minutes
   (`RESULT_GRACE_MS`); the expiry cleanup then deletes it. Hard cap if the PC never polls: 24 hours.
4. A failing acknowledgement never affects access; it only means the stub lives a few minutes longer.

The active authorization (`access_grants`) is separate and follows the expiry design (kept until expiry + 1 day for
revocation and session checks).

## Data deletion
At the moment of the decision (server side, regardless of any client): name, amount paid, UTR, invitation code and
the duplicate-UTR flag are cleared and the screenshot object is deleted from storage. What remains is the result stub
(request id, status, poll-token hash, signed grant for approvals, device binding, timestamps), deleted on
acknowledgement, at most 15 minutes after its first read, or after 24 hours if never read. No archive, soft delete or
history. The one deliberate exception is a 7-day keyed HMAC of each approved UTR in `used_references` (no plaintext),
used to warn admins about reused references. See `backend/DATA_LIFECYCLE.md`.

## Known limitations
* **Real application window:** the WPF window and WebView2 browser were not driven in an end-to-end run. The controller
  (which the window obeys through `AllowsBrowsing`) was exercised against the real backend with a real phone as the admin;
  the window wiring and the mute-on-lock in `BrowserService` are not covered by automated tests (WebView2 cannot run in
  unit tests).
* **Expiry in the end-to-end run** used the controller's injectable monotonic clock (a real plan lasts at least an
  hour); signatures and server time were real. Server-side expiry is tested against the real database.
* **Crash recovery window:** if the PC crashes after receiving an approval but before saving it, the approval is
  recoverable only while the stub exists (up to 15 minutes after the first read); after that the user submits again.
* **Older AXE builds** (before this change) never acknowledge, so their stubs live until the 15-minute cap.
* Polling is every 4 seconds with backoff up to 30 seconds on errors.
* Device binding relies on DPAPI, not hardware attestation.

## Testing
* **Backend** (`v2/backend/tests/api.test.mjs`, needs the local Supabase stack and `functions serve`): 21 tests against the
  real functions, database and storage, including lifecycle, delivery, expiry per plan, acknowledgement, and forged grants.
* **Windows** (`dotnet test v2/tests/AXEv2.Tests`): the real `AccessController`/`AccessClient`/`SignedToken`/`AccessStore`
  against a fake backend that signs with a real ECDSA key: approval, rejection, transient failures, every grant flaw
  (bad signature, tampered expiry, wrong device, wrong request, wrong type, expired, duration/plan mismatch, malformed),
  resume (valid, expired, revoked, offline, tampered, wrong device), expiry for each plan, clock behaviour, and the
  browser gate.
* **End to end** (manual): `v2/tools/AXEv2.EndToEnd` runs the real `AccessController` against the local backend and waits for
  a real admin decision:
  ```
  cd v2 ; dotnet run --project tools/AXEv2.EndToEnd -- approve 1h 240     # then approve on the Android app
  cd v2 ; dotnet run --project tools/AXEv2.EndToEnd -- reject  5h 240     # then reject on the Android app
  ```
