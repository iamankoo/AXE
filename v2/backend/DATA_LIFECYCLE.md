# AXE v2 request data lifecycle

Payment and invitation requests are **temporary by design** (low-storage constraint, no permanent payment history).
Deletion is entirely **server-side**: it does not depend on the Android Admin app, which only sends a decision and
then shows what the server returns.

## Sequence

```
Windows client            Edge Functions (service role)                   Admin app
──────────────            ─────────────────────────────                   ─────────
POST /access/requests ──▶ validate, store row + screenshot object,
                          push to admins
                                                                          GET  /admin/requests
                                                                          POST /admin/requests/:id/decision
                          1. validate decision (admin, UUID, still pending, not expired)
                          2. approve only: sign grant, insert access_grants (active authorization)
                          3. conditional UPDATE ... WHERE status='pending'   (only one decision can win)
                             → row becomes a RESULT STUB: status + signed grant + poll-token hash;
                               name, amount_paid, utr, invite_code, duplicate_utr are cleared
                          4. delete the screenshot object from storage, then clear screenshot_path
                          5. respond {ok, decision} ─────────────────────▶ list refreshes from the server
GET /access/requests/:id ─▶ verify poll token, sign the status token (approved + grant | rejected)
            ◀─ signed result   (first read caps the stub's life at RESULT_GRACE_MS = 15 min)
client saves the grant / handles the rejection
DELETE /access/requests/:id ─▶ ACKNOWLEDGED: delete the stub row (and any leftover screenshot) at once
```

## What exists when

| State | `access_requests` | Storage object | `access_grants` |
| --- | --- | --- | --- |
| Pending | full row (name, amount, UTR / code, screenshot path) | screenshot (payments) | none |
| Decided, not yet collected | **stub only**: id, status, poll-token hash, signed grant (approved), device binding, timestamps. All request/payment fields are null/false | **deleted** | approved: 1 active row |
| Read by the Windows client, not yet acknowledged | same stub, readable again (a retry gets the same signed result); expiry shortened to 15 min after the first read | deleted | as above |
| Acknowledged by the Windows client | **deleted** | deleted | approved: 1 active row (kept until expiry + 1 day) |
| Never acknowledged | stub removed 15 min after its first read by the expiry cleanup | deleted | as above |
| Never read | stub removed at the hard cap (`PICKUP_TTL_MS`, 24 h) by the expiry cleanup | deleted | as above |

Nothing is soft-deleted, archived, or hidden. There is no `deleted` flag and no scheduled "history" cleanup: the
only timers are the 15-minute bound after a first read and the 24 h cap for a result that is never read.

## Why the stub exists (and is not deleted at decision time)

The Windows client polls `GET /access/requests/:id` (authenticated by its poll token) to learn the result; a missing
row is reported to it as "request no longer active, submit again". Deleting the row inside the decision call would
silently lose a paid approval if the PC had not polled yet. So the row is reduced to the minimum needed to deliver
the result and is removed when the client **acknowledges** it has stored the result. An approval's *active
authorization* lives separately in `access_grants` and follows the existing expiry design.

Phase 2 deleted the stub on the first read; Phase 3 replaced that with read-then-acknowledge because a lost response
(or a crash before the grant was saved) made the delivery unrecoverable. See `../AUTHORIZATION.md`.

## Failure behavior

* A refused or failed decision (no/invalid session, bad body, bad id, server error) changes nothing: the request,
  its data and its screenshot stay intact and pending.
* Decisions are conditional (`WHERE status='pending'`): two admins deciding at once yield exactly one `200`, one
  `409`, and exactly one grant.
* The status token is signed **before** the stub is deleted; if signing fails, nothing is deleted.
* If the screenshot delete fails at decision time, `screenshot_path` is kept so the acknowledgement (or the expiry
  cleanup) removes the object instead of orphaning it.
* Acknowledging is idempotent and needs the poll secret; a wrong secret or an unknown id deletes nothing.

## Known trade-offs

* **Recovery window.** A lost response or a crash before the grant is saved is recoverable only while the stub exists:
  until the client acknowledges, and at most 15 minutes after the first read. After that the user submits again (an
  approval's grant row stays until it expires). Builds without the acknowledgement never acknowledge, so their stubs live
  for the 15 minutes.
* **Reused-payment detection.** `used_references` keeps a keyed HMAC of each approved UTR for 7 days (no plaintext,
  self-deleting) so the admin can be warned about a reused reference. This is the one deliberate exception to "no UTR
  trace"; it can be removed at the cost of that warning.

## Notifications are not history

Phase 4 push notifications (`../NOTIFICATIONS.md`) carry only generic text, `type` and the request id. Nothing about the
request is sent to or stored by FCM, no notification log or table exists, and sending never retains request data: the
lifecycle above is unchanged.

## Tests

`v2/backend/tests/api.test.mjs` ("lifecycle:" and "phase3" tests) read the actual Postgres rows and storage objects with the local
service-role key (fetched from the Supabase CLI at run time, never written to disk) and assert the states above for
approve, reject, invitation, storage, repeated/racing decisions, and refused decisions. See `v2/AXE-Admin/README.md`
for how to run them.
