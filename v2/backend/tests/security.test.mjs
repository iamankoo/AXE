// Phase 5: security tests against the LOCAL Supabase stack: what an attacker holding only the public anon key, a
// non-admin account, or a PC's poll token can and cannot do. Synthetic data only.
//
//   node --test --test-concurrency=1 tests/*.test.mjs        (see the header of api.test.mjs for the local setup)

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { createHash, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";
import { device, proofForGrant, withDeviceKey } from "./device-helper.mjs";
import { retrySync } from "./net-helper.mjs";

const v2 = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const config = JSON.parse(readFileSync(join(v2, "config", "server.json"), "utf8"));
const admin = JSON.parse(readFileSync(join(v2, "secrets", "local-admin.json"), "utf8"));
const env = readFileSync(join(v2, "backend", "supabase", "functions", ".env"), "utf8");
const inviteCode = env.match(/^AXE_INVITE_CODES=(.*)$/m)[1].split(",")[0].trim();
const base = `${config.functionsUrl}/access`;
const adminBase = `${config.functionsUrl}/admin`;
const anon = { apikey: config.anonKey, Authorization: `Bearer ${config.anonKey}`, "Content-Type": "application/json" };

// device() comes from device-helper.mjs: every simulated installation has its own ECDSA key pair.
const ip = () => `10.${randomBytes(1)[0]}.${randomBytes(1)[0]}.${randomBytes(1)[0]}`;
const client = () => ({ "cf-connecting-ip": ip() });

async function call(method, url, body, extra = {}) {
  const res = await fetch(url, { method, headers: { ...anon, ...client(), ...extra }, body: body === undefined ? undefined : JSON.stringify(withDeviceKey(url, method, body)) });
  const text = await res.text();
  let parsed = null;
  try { parsed = text ? JSON.parse(text) : null; } catch { parsed = text; }
  return { status: res.status, body: parsed, text };
}

// ---- service-role access (from the Supabase CLI at run time; never written to disk) -----------------------------------
let svc;
function service() {
  if (!svc) {
    const st = JSON.parse(retrySync(() => execFileSync("npx", ["--yes", "supabase", "status", "-o", "json"], {
      cwd: join(v2, "backend"), encoding: "utf8", shell: true, stdio: ["ignore", "pipe", "ignore"],
    })));
    svc = { url: st.API_URL, key: st.SERVICE_ROLE_KEY };
  }
  return svc;
}
const sh = () => ({ apikey: service().key, Authorization: `Bearer ${service().key}`, "Content-Type": "application/json" });
async function rows(table, filter) {
  const res = await fetch(`${service().url}/rest/v1/${table}?${filter}&select=*`, { headers: sh() });
  assert.equal(res.status, 200);
  return res.json();
}
async function storedObjects(requestId) {
  const res = await fetch(`${service().url}/storage/v1/object/list/payment-screenshots`, {
    method: "POST", headers: sh(), body: JSON.stringify({ prefix: requestId, limit: 100 }),
  });
  return (await res.json()).filter((o) => o.name);
}

function testPng() {
  const crc = (buf) => { let c = ~0; for (const b of buf) { c ^= b; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1)); } return ~c >>> 0; };
  const chunk = (type, d) => { const t = Buffer.concat([Buffer.from(type), d]); const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const c = Buffer.alloc(4); c.writeUInt32BE(crc(t)); return Buffer.concat([l, t, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(1, 0); ihdr.writeUInt32BE(1, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(Buffer.from([0, 255, 0, 0]))), chunk("IEND", Buffer.alloc(0))]);
}
const submitPayment = (dev = device()) => call("POST", `${base}/requests`, {
  kind: "payment", name: "Test Security Payer", plan: "1h", amountPaid: 149, utr: `SEC${Date.now()}${randomBytes(3).toString("hex")}`, device: dev,
  screenshot: { type: "image/png", data: testPng().toString("base64") },
});
const submitInvite = (code = inviteCode, extra = {}) => call("POST", `${base}/requests`, { kind: "invite", name: "Test Security Invitee", plan: "1h", code, device: device() }, extra);
const withdraw = (r) => call("DELETE", `${base}/requests/${r.body.requestId}`, null, { "x-poll-token": r.body.pollToken });

let adminToken;
let outsider; // a valid, signed-in Supabase user who is NOT an admin

before(async () => {
  const login = await fetch(`${admin.apiUrl}/auth/v1/token?grant_type=password`, {
    method: "POST", headers: { apikey: config.anonKey, "Content-Type": "application/json" }, body: JSON.stringify({ email: admin.email, password: admin.password }),
  });
  adminToken = (await login.json()).access_token;

  const email = `sec-test-${randomBytes(4).toString("hex")}@axe.local`;
  const password = randomBytes(16).toString("base64url");
  const created = await fetch(`${service().url}/auth/v1/admin/users`, { method: "POST", headers: sh(), body: JSON.stringify({ email, password, email_confirm: true }) });
  assert.equal(created.status, 200);
  const user = await created.json();
  const res = await fetch(`${admin.apiUrl}/auth/v1/token?grant_type=password`, {
    method: "POST", headers: { apikey: config.anonKey, "Content-Type": "application/json" }, body: JSON.stringify({ email, password }),
  });
  outsider = { id: user.id, token: (await res.json()).access_token };
});

after(async () => {
  if (outsider) await fetch(`${service().url}/auth/v1/admin/users/${outsider.id}`, { method: "DELETE", headers: sh() });
});

// ---------------------------------------------------------------------------------------------------------------------

test("security: invitation-code guessing is throttled per network, right or wrong, and spoofed headers do not reset it", async () => {
  const attacker = ip();
  let first429 = null;
  for (let i = 1; i <= 25; i++) {
    // The client pre-fills x-forwarded-for with a fresh fake address every time: it must not matter.
    const res = await submitInvite(`GUESS${String(i).padStart(3, "0")}`, { "cf-connecting-ip": attacker, "x-forwarded-for": `203.0.113.${i}, 198.51.100.${i}` });
    if (res.status === 429 && first429 === null) first429 = i;
    assert.ok([400, 429].includes(res.status), `attempt ${i}: ${res.status}`);
  }
  assert.equal(first429, 21, "the 21st attempt within the hour is the first one refused (limit: 20)");

  const valid = await submitInvite(inviteCode, { "cf-connecting-ip": attacker });
  assert.equal(valid.status, 429, "a throttled network cannot even confirm a correct code (no oracle)");

  const elsewhere = await submitInvite(inviteCode); // a different network is unaffected
  assert.equal(elsewhere.status, 201);
  await withdraw(elsewhere);
});

test("security: the public anon key and a non-admin account cannot read or write any table", async () => {
  const pending = await submitPayment();
  assert.equal(pending.status, 201);
  const tables = ["access_requests", "access_grants", "used_references", "admins", "admin_devices", "rate_events"];

  for (const who of [{ name: "anon key", auth: anon }, { name: "non-admin user", auth: { ...anon, Authorization: `Bearer ${outsider.token}` } }]) {
    for (const table of tables) {
      const read = await fetch(`${service().url}/rest/v1/${table}?select=*`, { headers: who.auth });
      const text = await read.text();
      const leaked = read.status === 200 && Array.isArray(JSON.parse(text)) && JSON.parse(text).length > 0;
      assert.ok(!leaked, `${who.name} must not read ${table} (got ${read.status})`);
      assert.ok(read.status === 200 || [401, 403].includes(read.status), `${who.name} read ${table}: ${read.status}`);

      const write = await fetch(`${service().url}/rest/v1/${table}`, { method: "POST", headers: { ...who.auth, Prefer: "return=minimal" }, body: JSON.stringify({}) });
      assert.ok(write.status >= 400, `${who.name} must not insert into ${table} (got ${write.status})`);
      const del = await fetch(`${service().url}/rest/v1/${table}?${table === "admins" ? "user_id" : "id"}=not.is.null`, { method: "DELETE", headers: who.auth });
      assert.ok(del.status >= 400 || (await del.text()) === "", `${who.name} must not delete from ${table}`);
    }
  }
  assert.equal((await rows("access_requests", `id=eq.${pending.body.requestId}`)).length, 1, "nothing was deleted");
  await withdraw(pending);
});

test("security: a signed-in non-admin cannot make themselves an admin or use the admin API", async () => {
  const escalate = await fetch(`${service().url}/rest/v1/admins`, {
    method: "POST", headers: { ...anon, Authorization: `Bearer ${outsider.token}`, Prefer: "return=minimal" }, body: JSON.stringify({ user_id: outsider.id }),
  });
  assert.ok(escalate.status >= 400, `inserting into admins must fail (got ${escalate.status})`);
  assert.deepEqual(await rows("admins", `user_id=eq.${outsider.id}`), []);

  const asOutsider = (method, path, body) => call(method, `${adminBase}${path}`, body, { Authorization: `Bearer ${outsider.token}` });
  assert.equal((await asOutsider("GET", "/requests")).status, 401);
  assert.equal((await asOutsider("GET", "/grants")).status, 401);
  assert.equal((await asOutsider("POST", "/devices", { token: randomBytes(16).toString("hex") })).status, 401);
  assert.equal((await asOutsider("POST", `/grants/${crypto.randomUUID()}/revoke`)).status, 401);
});

test("security: neither the anon key, a poll token nor a non-admin can approve or reject a request", async () => {
  const r = await submitPayment();
  const id = r.body.requestId;
  const attempts = [
    call("POST", `${adminBase}/requests/${id}/decision`, { decision: "approve" }),                                          // anon key
    call("POST", `${adminBase}/requests/${id}/decision`, { decision: "approve" }, { Authorization: `Bearer ${r.body.pollToken}` }), // its own poll token as a bearer
    call("POST", `${adminBase}/requests/${id}/decision`, { decision: "approve" }, { "x-poll-token": r.body.pollToken }),
    call("POST", `${adminBase}/requests/${id}/decision`, { decision: "approve" }, { Authorization: `Bearer ${outsider.token}` }),
    call("GET", `${adminBase}/requests/${id}/screenshot`, undefined, { Authorization: `Bearer ${outsider.token}` }),
    call("GET", `${adminBase}/requests/${id}`, undefined, { "x-poll-token": r.body.pollToken }),
  ];
  for (const res of await Promise.all(attempts)) assert.equal(res.status, 401);
  const [row] = await rows("access_requests", `id=eq.${id}`);
  assert.equal(row.status, "pending", "the request is still undecided");
  assert.deepEqual(await rows("access_grants", `request_id=eq.${id}`), [], "no authorization exists");
  await withdraw(r);
});

test("security: payment screenshots are unreachable without an admin session, directly or through storage", async () => {
  const r = await submitPayment();
  const id = r.body.requestId;
  const [object] = await storedObjects(id);
  assert.ok(object, "a screenshot object exists while the request is pending");
  const path = `${id}/${object.name}`;

  for (const [label, headers] of [["anon key", anon], ["non-admin user", { ...anon, Authorization: `Bearer ${outsider.token}` }]]) {
    for (const url of [
      `${service().url}/storage/v1/object/payment-screenshots/${path}`,
      `${service().url}/storage/v1/object/authenticated/payment-screenshots/${path}`,
      `${service().url}/storage/v1/object/public/payment-screenshots/${path}`,
    ]) {
      const res = await fetch(url, { headers });
      assert.notEqual(res.status, 200, `${label} must not download the screenshot (${url.split("/storage/v1/")[1].split("/")[0]}): ${res.status}`);
    }
    const listing = await fetch(`${service().url}/storage/v1/object/list/payment-screenshots`, { method: "POST", headers, body: JSON.stringify({ prefix: id, limit: 10 }) });
    const listed = listing.status === 200 ? (await listing.json()).filter((o) => o.name) : [];
    assert.equal(listed.length, 0, `${label} must not list screenshots`);
    const signed = await fetch(`${service().url}/storage/v1/object/sign/payment-screenshots/${path}`, { method: "POST", headers, body: JSON.stringify({ expiresIn: 60 }) });
    assert.notEqual(signed.status, 200, `${label} must not obtain a signed URL`);
  }

  const asAdmin = await fetch(`${adminBase}/requests/${id}/screenshot`, { headers: { ...anon, Authorization: `Bearer ${adminToken}` } });
  assert.equal(asAdmin.status, 200, "the admin API is the only way to the image");

  await call("POST", `${adminBase}/requests/${id}/decision`, { decision: "reject" }, { Authorization: `Bearer ${adminToken}` });
  assert.equal((await storedObjects(id)).length, 0, "and the object is deleted at decision");
  assert.equal((await fetch(`${adminBase}/requests/${id}/screenshot`, { headers: { ...anon, Authorization: `Bearer ${adminToken}` } })).status, 404);
  await withdraw(r);
});

test("security: internal database functions cannot be called with the public key or a user token", async () => {
  for (const fn of ["axe_rate_hit", "axe_cleanup_rows"]) {
    for (const auth of [anon, { ...anon, Authorization: `Bearer ${outsider.token}` }]) {
      const res = await fetch(`${service().url}/rest/v1/rpc/${fn}`, { method: "POST", headers: auth, body: JSON.stringify({ p_key: "x", p_window_seconds: 1 }) });
      assert.ok(res.status >= 400, `${fn} must not be callable (got ${res.status})`);
    }
  }
});

test("security: a PC's poll token only works for its own request", async () => {
  const a = await submitPayment();
  const b = await submitPayment();
  const nonce = randomBytes(18).toString("base64url");
  const cross = await call("GET", `${base}/requests/${b.body.requestId}?nonce=${nonce}`, undefined, { "x-poll-token": a.body.pollToken });
  assert.equal(cross.status, 404, "request A's secret cannot read request B");
  await call("DELETE", `${base}/requests/${b.body.requestId}`, null, { "x-poll-token": a.body.pollToken });
  assert.equal((await rows("access_requests", `id=eq.${b.body.requestId}`)).length, 1, "nor acknowledge or withdraw it");
  await withdraw(a);
  await withdraw(b);
});

test("security: a new request from the same PC replaces the previous one and deletes its data", async () => {
  const dev = device();
  const first = await submitPayment(dev);
  const firstId = first.body.requestId;
  assert.equal((await storedObjects(firstId)).length, 1);

  const second = await submitPayment(dev);
  assert.equal(second.status, 201);

  assert.deepEqual(await rows("access_requests", `id=eq.${firstId}`), [], "the replaced request is gone");
  assert.equal((await storedObjects(firstId)).length, 0, "and so is its screenshot");
  const nonce = randomBytes(18).toString("base64url");
  assert.equal((await call("GET", `${base}/requests/${firstId}?nonce=${nonce}`, undefined, { "x-poll-token": first.body.pollToken })).status, 404, "the old request can no longer be polled");
  assert.equal((await call("GET", `${adminBase}/requests/${firstId}`, undefined, { Authorization: `Bearer ${adminToken}` })).status, 404, "or opened by an admin");
  await withdraw(second);
});

test("security: error responses are generic and never reveal internals", async () => {
  const bad = [
    await call("POST", `${base}/requests`, undefined, { "Content-Type": "application/json" }),
    await fetch(`${base}/requests`, { method: "POST", headers: { ...anon, ...client() }, body: "{not json" }).then(async (r) => ({ status: r.status, text: await r.text() })),
    await fetch(`${adminBase}/requests/not-a-uuid/decision`, { method: "POST", headers: { ...anon, Authorization: `Bearer ${adminToken}` }, body: "{" }).then(async (r) => ({ status: r.status, text: await r.text() })),
    await call("GET", `${base}/nothing-here`),
    await call("GET", `${adminBase}/nothing-here`, undefined, { Authorization: `Bearer ${adminToken}` }),
  ];
  for (const res of bad) {
    assert.ok(res.status >= 400 && res.status < 500, `client errors are 4xx (got ${res.status})`);
    const text = res.text ?? JSON.stringify(res.body);
    assert.ok(text.length < 300, "short messages only");
    assert.doesNotMatch(text, /at file:|\.ts:\d|Deno|stack|supabase|postgres|service_role|SUPABASE_|secret|jwk/i, `no internals in: ${text}`);
  }
});
