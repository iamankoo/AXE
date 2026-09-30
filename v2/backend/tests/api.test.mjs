// End-to-end tests of the AXE v2 access backend against the LOCAL Supabase stack.
//
//   1. npx supabase start && node scripts/setup-local.mjs --sink http://host.docker.internal:8799/
//   2. npx supabase functions serve --env-file supabase/functions/.env --no-verify-jwt
//   3. node --test tests/
//
// Plays both sides: the Windows client (submit / poll / session) and AXE Admin (sign in,
// review, approve / reject). Uses synthetic test data only — a generated 1×1 PNG, fake
// names and UTR numbers — never real payment information.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { createHash, createPublicKey, randomBytes, verify } from "node:crypto";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";

const v2 = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const config = JSON.parse(readFileSync(join(v2, "config", "server.json"), "utf8"));
const admin = JSON.parse(readFileSync(join(v2, "secrets", "local-admin.json"), "utf8"));
const base = `${config.functionsUrl}/access`;
const adminBase = `${config.functionsUrl}/admin`;
const publicKey = createPublicKey({ key: Buffer.from(config.signingPublicKey, "base64"), format: "der", type: "spki" });
const pushes = [];
let sink;
let adminToken;

const headers = { apikey: config.anonKey, Authorization: `Bearer ${config.anonKey}`, "Content-Type": "application/json" };
const device = () => createHash("sha256").update(randomBytes(32)).digest("hex");
const nonce = () => randomBytes(18).toString("base64url");

/** A valid 1×1 PNG built in memory (synthetic test screenshot). */
function testPng() {
  const crc = (buf) => {
    let c = ~0;
    for (const b of buf) { c ^= b; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1)); }
    return ~c >>> 0;
  };
  const chunk = (type, data) => {
    const t = Buffer.concat([Buffer.from(type), data]);
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const c = Buffer.alloc(4); c.writeUInt32BE(crc(t));
    return Buffer.concat([len, t, c]);
  };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(1, 0); ihdr.writeUInt32BE(1, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(Buffer.from([0, 255, 0, 0]))), chunk("IEND", Buffer.alloc(0))]);
}

/** Verifies an AXE-signed token exactly like the Windows client does; returns its claims. */
function verified(token, typ) {
  const [payload, sig] = token.split(".");
  assert.ok(verify("sha256", Buffer.from(payload), { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(sig, "base64url")),
    "signature must verify with the public key shipped in AXE v2");
  const claims = JSON.parse(Buffer.from(payload, "base64url").toString());
  assert.equal(claims.typ, typ);
  return claims;
}

async function call(method, url, body, extra = {}) {
  const res = await fetch(url, { method, headers: { ...headers, ...extra }, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

const adminCall = (method, path, body) => call(method, `${adminBase}${path}`, body, { Authorization: `Bearer ${adminToken}` });

async function poll(requestId, pollToken) {
  const n = nonce();
  const res = await call("GET", `${base}/requests/${requestId}?nonce=${n}`, null, { "x-poll-token": pollToken });
  assert.equal(res.status, 200, JSON.stringify(res.body));
  const claims = verified(res.body.token, "axe-status");
  assert.equal(claims.nonce, n, "status must be bound to the request nonce");
  assert.equal(claims.rid, requestId);
  return claims;
}

async function session(grant) {
  const n = nonce();
  const res = await call("POST", `${base}/session`, { grant, nonce: n });
  assert.equal(res.status, 200);
  const claims = verified(res.body.token, "axe-session");
  assert.equal(claims.nonce, n);
  return claims;
}

async function waitForPush(requestId) {
  for (let i = 0; i < 40; i++) {
    const found = pushes.find((p) => p.requestId === requestId);
    if (found) return found;
    await new Promise((r) => setTimeout(r, 100));
  }
  assert.fail("admin push notification was not sent");
}

before(async () => {
  sink = createServer((req, res) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", () => { pushes.push(JSON.parse(data)); res.end("ok"); });
  });
  await new Promise((r) => sink.listen(8799, "0.0.0.0", r));

  const login = await fetch(`${admin.apiUrl}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: { apikey: config.anonKey, "Content-Type": "application/json" },
    body: JSON.stringify({ email: admin.email, password: admin.password }),
  });
  assert.equal(login.status, 200, "local admin must be able to sign in");
  adminToken = (await login.json()).access_token;
});

after(() => sink.close());

test("admin API refuses anonymous and non-admin callers", async () => {
  assert.equal((await call("GET", `${adminBase}/requests`)).status, 401);
  assert.equal((await call("GET", `${adminBase}/requests`, null, { Authorization: "Bearer not-a-token" })).status, 401);
});

test("invalid invitation codes are rejected by the server", async () => {
  const res = await call("POST", `${base}/requests`, { kind: "invite", name: "Test User", plan: "1h", code: "NOPE123", device: device() });
  assert.equal(res.status, 400);
  assert.match(res.body.error, /invitation code/i);
});

test("valid invitation still needs manual approval; approval starts the timer on the server clock", async () => {
  const dev = device();
  const submitted = await call("POST", `${base}/requests`, { kind: "invite", name: "Test Invitee", plan: "5h", code: "papaji500", device: dev });
  assert.equal(submitted.status, 201, JSON.stringify(submitted.body));
  const { requestId, pollToken } = submitted.body;

  const push = await waitForPush(requestId);
  assert.equal(push.title, "AXE Invitation Request");
  assert.match(push.body, /Code: PAPAJI500/);
  assert.match(push.body, /5 Hours/);

  assert.equal((await poll(requestId, pollToken)).status, "pending", "a matching code alone grants nothing");

  const listed = await adminCall("GET", "/requests");
  const item = listed.body.requests.find((r) => r.id === requestId);
  assert.ok(item, "pending invitation is listed for the admin");
  assert.equal(item.kind, "invite");

  const before = Date.now();
  const decided = await adminCall("POST", `/requests/${requestId}/decision`, { decision: "approve" });
  assert.equal(decided.status, 200);

  const status = await poll(requestId, pollToken);
  assert.equal(status.status, "approved");
  const grant = verified(status.grant, "axe-grant");
  assert.equal(grant.did, dev, "grant is bound to the requesting PC");
  assert.equal(grant.plan, "5h");
  assert.ok(Math.abs(grant.exp - grant.iat - 5 * 3600_000) < 1, "5 hours from approval");
  assert.ok(grant.iat >= before - 5000);

  const s = await session(status.grant);
  assert.equal(s.valid, true);
  assert.equal(s.exp, grant.exp);

  const again = await adminCall("POST", `/requests/${requestId}/decision`, { decision: "reject" });
  assert.equal(again.status, 409, "a decided request cannot be decided again");
});

test("payment: screenshot reaches the admin, approval issues a grant, data is deleted after the decision", async () => {
  const dev = device();
  const utr = `TEST${Date.now()}`;
  const submitted = await call("POST", `${base}/requests`, {
    kind: "payment", name: "Test Payer", plan: "1h", amountPaid: 149, utr, device: dev,
    screenshot: { type: "image/png", data: testPng().toString("base64") },
  });
  assert.equal(submitted.status, 201, JSON.stringify(submitted.body));
  const { requestId, pollToken } = submitted.body;

  const push = await waitForPush(requestId);
  assert.equal(push.title, "AXE Payment Request");
  assert.match(push.body, /Name: Test Payer/);
  assert.match(push.body, /₹149/);
  assert.match(push.body, new RegExp(`UTR: ${utr}`));

  const detail = await adminCall("GET", `/requests/${requestId}`);
  assert.equal(detail.status, 200);
  assert.equal(detail.body.request.amountMismatch, false);
  assert.equal(detail.body.request.hasScreenshot, true);
  const shotUrl = `${adminBase}/requests/${requestId}/screenshot`;
  assert.equal((await fetch(shotUrl, { headers })).status, 401, "screenshot needs an admin session");
  const shot = await fetch(shotUrl, { headers: { ...headers, Authorization: `Bearer ${adminToken}` } });
  assert.equal(shot.status, 200);
  assert.deepEqual(Buffer.from(await shot.arrayBuffer()), testPng());

  assert.equal((await adminCall("POST", `/requests/${requestId}/decision`, { decision: "approve" })).status, 200);
  const status = await poll(requestId, pollToken);
  assert.equal(status.status, "approved");
  assert.equal(verified(status.grant, "axe-grant").plan, "1h");

  const gone = await fetch(shotUrl, { headers: { ...headers, Authorization: `Bearer ${adminToken}` } });
  assert.equal(gone.status, 404, "screenshot is gone once the request is decided");
  assert.equal((await adminCall("GET", `/requests/${requestId}`)).status, 404, "request details are gone after the decision");

  // Re-using the same UTR is flagged for the admin.
  const reuse = await call("POST", `${base}/requests`, {
    kind: "payment", name: "Test Payer", plan: "1h", amountPaid: 100, utr, device: device(),
    screenshot: { type: "image/png", data: testPng().toString("base64") },
  });
  const reused = (await adminCall("GET", `/requests/${reuse.body.requestId}`)).body.request;
  assert.equal(reused.duplicateUtr, true);
  assert.equal(reused.amountMismatch, true);
  await call("DELETE", `${base}/requests/${reuse.body.requestId}`, null, { "x-poll-token": reuse.body.pollToken });
});

test("rejection grants nothing", async () => {
  const submitted = await call("POST", `${base}/requests`, {
    kind: "payment", name: "Test Reject", plan: "10h", amountPaid: 299, utr: `REJ${Date.now()}`, device: device(),
    screenshot: { type: "image/png", data: testPng().toString("base64") },
  });
  const { requestId, pollToken } = submitted.body;
  assert.equal((await adminCall("POST", `/requests/${requestId}/decision`, { decision: "reject" })).status, 200);
  const status = await poll(requestId, pollToken);
  assert.equal(status.status, "rejected");
  assert.equal(status.grant, null);
});

test("tampered, forged and revoked grants are refused", async () => {
  const submitted = await call("POST", `${base}/requests`, { kind: "invite", name: "Test Tamper", plan: "1h", code: "PAPAJI500", device: device() });
  const { requestId, pollToken } = submitted.body;
  await adminCall("POST", `/requests/${requestId}/decision`, { decision: "approve" });
  const grant = (await poll(requestId, pollToken)).grant;

  // Extend the expiry in the payload but keep the old signature.
  const [payload, sig] = grant.split(".");
  const claims = JSON.parse(Buffer.from(payload, "base64url").toString());
  claims.exp += 10 * 3600_000;
  const forged = `${Buffer.from(JSON.stringify(claims)).toString("base64url")}.${sig}`;
  assert.equal((await session(forged)).valid, false);
  assert.equal((await session("garbage")).valid, false);

  const active = await adminCall("GET", "/grants");
  assert.ok(active.body.grants.some((g) => g.id === claims.jti));
  await adminCall("POST", `/grants/${claims.jti}/revoke`);
  const revoked = await session(grant);
  assert.equal(revoked.valid, false);
  assert.equal(revoked.reason, "revoked");
});

test("polling needs the request's secret; cancel deletes the request", async () => {
  const submitted = await call("POST", `${base}/requests`, {
    kind: "payment", name: "Test Cancel", plan: "1h", amountPaid: 149, utr: `CAN${Date.now()}`, device: device(),
    screenshot: { type: "image/png", data: testPng().toString("base64") },
  });
  const { requestId, pollToken } = submitted.body;
  const wrong = await call("GET", `${base}/requests/${requestId}?nonce=${nonce()}`, null, { "x-poll-token": "x".repeat(43) });
  assert.equal(wrong.status, 404);

  await call("DELETE", `${base}/requests/${requestId}`, null, { "x-poll-token": pollToken });
  assert.equal((await adminCall("GET", `/requests/${requestId}`)).status, 404);
});

test("input validation", async () => {
  const bad = async (body) => (await call("POST", `${base}/requests`, body)).status;
  assert.equal(await bad({ kind: "payment", name: "A", plan: "1h", device: device() }), 400);
  assert.equal(await bad({ kind: "payment", name: "Test", plan: "99h", device: device() }), 400);
  assert.equal(await bad({ kind: "payment", name: "Test", plan: "1h", amountPaid: 149, utr: "TEST123456", device: device(),
    screenshot: { type: "image/png", data: Buffer.from("not an image at all").toString("base64") } }), 400);
  assert.equal(await bad({ kind: "payment", name: "Test", plan: "1h", amountPaid: 149, utr: "!!", device: device(),
    screenshot: { type: "image/png", data: testPng().toString("base64") } }), 400);
  assert.equal(await bad({ kind: "invite", name: "Test", plan: "1h", code: "PAPAJI500", device: "not-a-device" }), 400);
});

// ---------------------------------------------------------------------------------------------------
// DATA LIFECYCLE (core product requirement): payment / invitation data is temporary. These tests read
// the ACTUAL database rows and storage objects with the local service-role key (obtained from the
// Supabase CLI at run time, never written to disk) instead of trusting API responses.
// ---------------------------------------------------------------------------------------------------

let serviceCtx;
function service() {
  if (!serviceCtx) {
    const st = JSON.parse(execFileSync("npx", ["--yes", "supabase", "status", "-o", "json"], {
      cwd: join(v2, "backend"), encoding: "utf8", shell: true, stdio: ["ignore", "pipe", "ignore"],
    }));
    serviceCtx = { url: st.API_URL, key: st.SERVICE_ROLE_KEY };
  }
  return serviceCtx;
}
const serviceHeaders = () => ({ apikey: service().key, Authorization: `Bearer ${service().key}`, "Content-Type": "application/json" });

/** Rows straight from Postgres (PostgREST with the service role). */
async function rows(table, filter) {
  const res = await fetch(`${service().url}/rest/v1/${table}?${filter}&select=*`, { headers: serviceHeaders() });
  assert.equal(res.status, 200, `reading ${table}`);
  return res.json();
}

/** Objects actually present in the private screenshot bucket for one request. */
async function storedScreenshots(requestId) {
  const res = await fetch(`${service().url}/storage/v1/object/list/payment-screenshots`, {
    method: "POST", headers: serviceHeaders(), body: JSON.stringify({ prefix: requestId, limit: 100 }),
  });
  assert.equal(res.status, 200, "listing storage");
  return (await res.json()).filter((o) => o.name);
}

async function submitPayment(name) {
  const dev = device();
  const res = await call("POST", `${base}/requests`, {
    kind: "payment", name, plan: "5h", amountPaid: 199, utr: `LIFE${Date.now()}${randomBytes(3).toString("hex")}`, device: dev,
    screenshot: { type: "image/png", data: testPng().toString("base64") },
  });
  assert.equal(res.status, 201, JSON.stringify(res.body));
  return { ...res.body, dev };
}

async function submitInvite(name) {
  const res = await call("POST", `${base}/requests`, { kind: "invite", name, plan: "1h", code: "PAPAJI500", device: device() });
  assert.equal(res.status, 201, JSON.stringify(res.body));
  return res.body;
}

const inAdminList = async (id) => (await adminCall("GET", "/requests")).body.requests.some((r) => r.id === id);
const HOURS_24 = 24 * 3600_000;

test("lifecycle: approval clears all request data at once, delivers the grant, and the stub is deleted on pickup", async () => {
  const { requestId, pollToken } = await submitPayment("Test Lifecycle Approve");

  // Sanity: the helpers see the temporary data while the request is pending.
  const [pending] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(pending.status, "pending");
  assert.ok(pending.name && pending.utr && pending.amount_paid && pending.screenshot_path);
  assert.equal((await storedScreenshots(requestId)).length, 1, "screenshot object exists while pending");

  const before = Date.now();
  assert.equal((await adminCall("POST", `/requests/${requestId}/decision`, { decision: "approve" })).status, 200);

  // At the moment of decision (server-side, before anyone polls): only a minimal result stub is left.
  const [stub] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(stub.status, "approved");
  for (const field of ["name", "amount_paid", "utr", "invite_code", "screenshot_path"]) {
    assert.equal(stub[field], null, `${field} must be cleared at decision time`);
  }
  assert.equal(stub.duplicate_utr, false, "temporary verification data is cleared too");
  assert.ok(stub.grant_token, "the signed result is stored for the Windows client");
  const hardCap = new Date(stub.expires_at).getTime();
  assert.ok(hardCap > before && hardCap <= Date.now() + HOURS_24 + 5000, "stub is capped at the pickup window");
  assert.equal((await storedScreenshots(requestId)).length, 0, "screenshot object is deleted from storage");
  assert.equal(await inAdminList(requestId), false, "a decided request is not in GET /admin/requests");
  assert.equal((await adminCall("GET", `/requests/${requestId}`)).status, 404);

  // The Windows client picks up the result.
  const status = await poll(requestId, pollToken);
  assert.equal(status.status, "approved");
  const grant = verified(status.grant, "axe-grant");

  // Pickup deletes the request row entirely; only the ACTIVE authorization remains.
  assert.deepEqual(await rows("access_requests", `id=eq.${requestId}`), [], "request record is gone after pickup");
  const grants = await rows("access_grants", `request_id=eq.${requestId}`);
  assert.equal(grants.length, 1, "the active authorization is retained");
  assert.equal(grants[0].jti, grant.jti);
  assert.equal((await session(status.grant)).valid, true, "the grant still validates without the request row");

  // The result is delivered once; nothing is recreated by asking again.
  assert.equal((await call("GET", `${base}/requests/${requestId}?nonce=${nonce()}`, null, { "x-poll-token": pollToken })).status, 404);
  assert.deepEqual(await rows("access_requests", `id=eq.${requestId}`), []);
  assert.equal(await inAdminList(requestId), false);
});

test("lifecycle: rejection clears all request data, delivers the rejection, then deletes the stub", async () => {
  const { requestId, pollToken } = await submitPayment("Test Lifecycle Reject");
  assert.equal((await storedScreenshots(requestId)).length, 1);

  assert.equal((await adminCall("POST", `/requests/${requestId}/decision`, { decision: "reject" })).status, 200);

  const [stub] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(stub.status, "rejected");
  assert.equal(stub.grant_token, null);
  for (const field of ["name", "amount_paid", "utr", "invite_code", "screenshot_path"]) assert.equal(stub[field], null, field);
  assert.equal(stub.duplicate_utr, false);
  assert.equal((await storedScreenshots(requestId)).length, 0);
  assert.equal(await inAdminList(requestId), false);

  const status = await poll(requestId, pollToken);
  assert.equal(status.status, "rejected", "the rejection reaches the Windows client");
  assert.equal(status.grant, null);

  assert.deepEqual(await rows("access_requests", `id=eq.${requestId}`), [], "request record is gone after pickup");
  assert.deepEqual(await rows("access_grants", `request_id=eq.${requestId}`), [], "a rejection never creates an authorization");
});

test("lifecycle: invitation requests follow the same lifecycle", async () => {
  const { requestId, pollToken } = await submitInvite("Test Lifecycle Invite");
  const [pending] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(pending.invite_code, "PAPAJI500");

  assert.equal((await adminCall("POST", `/requests/${requestId}/decision`, { decision: "approve" })).status, 200);
  const [stub] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(stub.invite_code, null);
  assert.equal(stub.name, null);

  assert.equal((await poll(requestId, pollToken)).status, "approved");
  assert.deepEqual(await rows("access_requests", `id=eq.${requestId}`), []);
});

test("lifecycle: screenshot storage holds nothing for processed requests", async () => {
  const a = await submitPayment("Test Storage A");
  const b = await submitPayment("Test Storage B");
  assert.equal((await storedScreenshots(a.requestId)).length + (await storedScreenshots(b.requestId)).length, 2);

  await adminCall("POST", `/requests/${a.requestId}/decision`, { decision: "approve" });
  await adminCall("POST", `/requests/${b.requestId}/decision`, { decision: "reject" });
  assert.equal((await storedScreenshots(a.requestId)).length, 0, "empty right after the decision, before any pickup");
  assert.equal((await storedScreenshots(b.requestId)).length, 0);

  await poll(a.requestId, a.pollToken);
  await poll(b.requestId, b.pollToken);
  assert.equal((await storedScreenshots(a.requestId)).length, 0);
  assert.equal((await storedScreenshots(b.requestId)).length, 0);
});

test("lifecycle: repeating or racing decisions can never recreate history or extra grants", async () => {
  // A decided-and-delivered request: deciding again is refused and recreates nothing.
  const done = await submitPayment("Test Repeat Approve");
  await adminCall("POST", `/requests/${done.requestId}/decision`, { decision: "approve" });
  await poll(done.requestId, done.pollToken);
  assert.equal((await adminCall("POST", `/requests/${done.requestId}/decision`, { decision: "approve" })).status, 409);
  assert.equal((await adminCall("POST", `/requests/${done.requestId}/decision`, { decision: "reject" })).status, 409);
  assert.deepEqual(await rows("access_requests", `id=eq.${done.requestId}`), []);
  assert.equal((await rows("access_grants", `request_id=eq.${done.requestId}`)).length, 1, "still exactly one grant");

  // A rejected request cannot later be approved into existence.
  const rejected = await submitPayment("Test Reject Then Approve");
  await adminCall("POST", `/requests/${rejected.requestId}/decision`, { decision: "reject" });
  await poll(rejected.requestId, rejected.pollToken);
  assert.equal((await adminCall("POST", `/requests/${rejected.requestId}/decision`, { decision: "approve" })).status, 409);
  assert.deepEqual(await rows("access_grants", `request_id=eq.${rejected.requestId}`), []);

  // Two admins tapping at once: exactly one wins, exactly one grant exists.
  const raced = await submitPayment("Test Race");
  const results = await Promise.all([
    adminCall("POST", `/requests/${raced.requestId}/decision`, { decision: "approve" }),
    adminCall("POST", `/requests/${raced.requestId}/decision`, { decision: "approve" }),
  ]);
  assert.deepEqual(results.map((r) => r.status).sort(), [200, 409]);
  assert.equal((await rows("access_grants", `request_id=eq.${raced.requestId}`)).length, 1);
  await poll(raced.requestId, raced.pollToken);
  assert.deepEqual(await rows("access_requests", `id=eq.${raced.requestId}`), []);
});

test("lifecycle: failed or refused decisions delete nothing", async () => {
  const { requestId, pollToken } = await submitPayment("Test Not Decided");

  const refused = [
    await call("POST", `${adminBase}/requests/${requestId}/decision`, { decision: "approve" }), // no session
    await call("POST", `${adminBase}/requests/${requestId}/decision`, { decision: "approve" }, { Authorization: "Bearer not-a-token" }),
    await adminCall("POST", `/requests/${requestId}/decision`, { decision: "maybe" }),
    await adminCall("POST", `/requests/${requestId}/decision`, {}),
    await adminCall("POST", `/requests/not-a-uuid/decision`, { decision: "approve" }),
  ];
  assert.deepEqual(refused.map((r) => r.status), [401, 401, 400, 400, 404]);
  assert.equal((await poll(requestId, pollToken)).status, "pending", "polling a pending request changes nothing");

  const [row] = await rows("access_requests", `id=eq.${requestId}`);
  assert.equal(row.status, "pending");
  assert.ok(row.name && row.utr && row.amount_paid && row.screenshot_path, "request data is intact");
  assert.equal((await storedScreenshots(requestId)).length, 1, "screenshot is intact");
  assert.equal(await inAdminList(requestId), true);
  assert.deepEqual(await rows("access_grants", `request_id=eq.${requestId}`), [], "no grant was minted");

  await call("DELETE", `${base}/requests/${requestId}`, null, { "x-poll-token": pollToken });
  assert.deepEqual(await rows("access_requests", `id=eq.${requestId}`), []);
  assert.equal((await storedScreenshots(requestId)).length, 0, "withdrawing also deletes the screenshot");
});
