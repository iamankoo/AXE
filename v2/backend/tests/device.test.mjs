// Multi-installation device binding against the LOCAL Supabase stack. Several simulated AXE installations (each with its own
// ECDSA key pair, like the real app) talk to one backend; one admin approves; each authorization works only for its own
// installation. Synthetic data only.
//
//   node --test --test-concurrency=1 tests/*.test.mjs

import { test, before } from "node:test";
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { newDevice, proofFor, claimsOf } from "./device-helper.mjs";
import { retrySync } from "./net-helper.mjs";

const v2 = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const config = JSON.parse(readFileSync(join(v2, "config", "server.json"), "utf8"));
const admin = JSON.parse(readFileSync(join(v2, "secrets", "local-admin.json"), "utf8"));
const env = readFileSync(join(v2, "backend", "supabase", "functions", ".env"), "utf8");
const inviteCode = env.match(/^AXE_INVITE_CODES=(.*)$/m)[1].split(",")[0].trim();
const base = `${config.functionsUrl}/access`;
const adminBase = `${config.functionsUrl}/admin`;
const headers = { apikey: config.anonKey, Authorization: `Bearer ${config.anonKey}`, "Content-Type": "application/json" };
const nonce = () => randomBytes(18).toString("base64url");

async function call(method, url, body, extra = {}) {
  const res = await fetch(url, { method, headers: { ...headers, "cf-connecting-ip": `10.${randomBytes(1)[0]}.${randomBytes(1)[0]}.${randomBytes(1)[0]}`, ...extra }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

let svc;
function service() {
  if (!svc) {
    const st = JSON.parse(retrySync(() => execFileSync("npx", ["--yes", "supabase", "status", "-o", "json"], { cwd: join(v2, "backend"), encoding: "utf8", shell: true, stdio: ["ignore", "pipe", "ignore"] })));
    svc = { url: st.API_URL, key: st.SERVICE_ROLE_KEY };
  }
  return svc;
}
const sh = () => ({ apikey: service().key, Authorization: `Bearer ${service().key}`, "Content-Type": "application/json" });
async function rows(table, filter) { return (await fetch(`${service().url}/rest/v1/${table}?${filter}&select=*`, { headers: sh() })).json(); }

let adminToken;
before(async () => {
  const login = await fetch(`${admin.apiUrl}/auth/v1/token?grant_type=password`, { method: "POST", headers: { apikey: config.anonKey, "Content-Type": "application/json" }, body: JSON.stringify({ email: admin.email, password: admin.password }) });
  adminToken = (await login.json()).access_token;
});
const asAdmin = (method, path, body) => call(method, `${adminBase}${path}`, body, { Authorization: `Bearer ${adminToken}` });

/** One installation: registers its key with an invitation request, gets approved by the (single) admin, collects its grant. */
async function install(plan = "5h") {
  const dev = newDevice();
  const submitted = await call("POST", `${base}/requests`, { kind: "invite", name: "Test Installation", plan, code: inviteCode, device: dev.hash, devicePublicKey: dev.publicKey });
  assert.equal(submitted.status, 201, JSON.stringify(submitted.body));
  const { requestId, pollToken } = submitted.body;
  assert.equal((await asAdmin("POST", `/requests/${requestId}/decision`, { decision: "approve" })).status, 200);
  const n = nonce();
  const polled = await call("GET", `${base}/requests/${requestId}?nonce=${n}`, undefined, { "x-poll-token": pollToken });
  const token = polled.body.token;
  const claims = JSON.parse(Buffer.from(token.split(".")[0], "base64url").toString());
  await call("DELETE", `${base}/requests/${requestId}`, null, { "x-poll-token": pollToken }); // acknowledge
  return { dev, requestId, pollToken, grant: claims.grant, grantClaims: claimsOf(claims.grant) };
}

/** An authorization check as a given installation would make it. `who` null = no proof; a device = that device's proof. */
async function check(grant, who, { nonceOverride, jtiOverride } = {}) {
  const n = nonceOverride ?? nonce();
  const jti = jtiOverride ?? claimsOf(grant).jti;
  const body = { grant, nonce: n };
  if (who) body.proof = proofFor(who, n, jti);
  const res = await call("POST", `${base}/session`, body);
  assert.equal(res.status, 200);
  const claims = JSON.parse(Buffer.from(res.body.token.split(".")[0], "base64url").toString());
  return claims;
}

test("device registration: the public key must be present, valid, and match the device id", async () => {
  const d = newDevice();
  const other = newDevice();
  const base64der = (kp) => kp.publicKey.export({ type: "spki", format: "der" }).toString("base64");
  const p384 = generateKeyPairSync("ec", { namedCurve: "P-384" });
  const p384hash = createHash("sha256").update(p384.publicKey.export({ type: "spki", format: "der" })).digest("hex");
  const submit = (body) => call("POST", `${base}/requests`, { kind: "invite", name: "Test Registration", plan: "1h", code: inviteCode, ...body });

  for (const [label, body] of [
    ["no public key", { device: d.hash }],
    ["another device's key", { device: d.hash, devicePublicKey: other.publicKey }],
    ["garbage key", { device: d.hash, devicePublicKey: "A".repeat(120) }],
    ["a P-384 key", { device: p384hash, devicePublicKey: base64der(p384) }],
    ["an invented id with a real key", { device: "a".repeat(64), devicePublicKey: d.publicKey }],
    ["an oversized key", { device: d.hash, devicePublicKey: "A".repeat(2000) }],
  ]) {
    const res = await submit(body);
    assert.equal(res.status, 400, `${label} must be refused (got ${res.status})`);
    assert.match(res.body.error, /couldn't identify itself/);
  }

  const ok = await submit({ device: d.hash, devicePublicKey: d.publicKey });
  assert.equal(ok.status, 201);
  const [row] = await rows("access_requests", `id=eq.${ok.body.requestId}`);
  assert.equal(row.device_hash, d.hash);
  assert.equal(row.device_pubkey, d.publicKey, "the public key is stored with the request");
  await call("DELETE", `${base}/requests/${ok.body.requestId}`, null, { "x-poll-token": ok.body.pollToken });
});

test("two installations get their own authorizations, each usable only by its own installation", async () => {
  const [a, b] = [await install("5h"), await install("1h")];

  assert.notEqual(a.dev.hash, b.dev.hash, "distinct installation identities");
  assert.notEqual(a.grantClaims.jti, b.grantClaims.jti, "distinct grants (no shared token)");
  assert.equal(a.grantClaims.did, a.dev.hash);
  assert.equal(b.grantClaims.did, b.dev.hash);
  assert.equal(a.grantClaims.plan, "5h");
  assert.equal(b.grantClaims.plan, "1h");

  assert.equal((await check(a.grant, a.dev)).valid, true, "A's grant works for A");
  assert.equal((await check(b.grant, b.dev)).valid, true, "B's grant works for B");

  const aOnB = await check(a.grant, b.dev);
  assert.equal(aOnB.valid, false, "A's grant does NOT work for B");
  assert.equal(aOnB.reason, "device");
  const bOnA = await check(b.grant, a.dev);
  assert.equal(bOnA.valid, false, "B's grant does NOT work for A");
  assert.equal(bOnA.reason, "device");

  const stored = await rows("access_grants", `jti=eq.${a.grantClaims.jti}`);
  assert.equal(stored[0].device_pubkey, a.dev.publicKey, "the grant row carries its installation's public key");
});

test("a copied grant is useless without the installation's private key (even though its device id is public)", async () => {
  const a = await install();
  const thief = newDevice(); // knows the whole grant token, including `did`, but not A's private key

  assert.equal((await check(a.grant, null)).reason, "device", "no proof");
  assert.equal((await check(a.grant, thief)).reason, "device", "the thief's own proof");
  for (const proof of ["", "x", "A".repeat(86), Buffer.alloc(64).toString("base64url"), proofFor(a.dev, "other-nonce", a.grantClaims.jti)]) {
    const n = nonce();
    const res = await call("POST", `${base}/session`, { grant: a.grant, nonce: n, proof });
    const claims = JSON.parse(Buffer.from(res.body.token.split(".")[0], "base64url").toString());
    assert.equal(claims.valid, false, `bogus proof "${proof.slice(0, 12)}"`);
    assert.equal(claims.reason, "device");
  }
  assert.equal((await check(a.grant, a.dev)).valid, true, "and the real installation still works");
});

test("a proof cannot be replayed for another nonce or used for another grant", async () => {
  const a = await install();
  const b = await install();
  const n1 = nonce();
  const proof = proofFor(a.dev, n1, a.grantClaims.jti);

  const replay = await call("POST", `${base}/session`, { grant: a.grant, nonce: nonce(), proof });
  assert.equal(JSON.parse(Buffer.from(replay.body.token.split(".")[0], "base64url").toString()).valid, false, "same proof, new nonce");

  const crossGrant = await call("POST", `${base}/session`, { grant: b.grant, nonce: n1, proof });
  assert.equal(JSON.parse(Buffer.from(crossGrant.body.token.split(".")[0], "base64url").toString()).valid, false, "A's proof on B's grant");
});

test("each installation's authorization expires independently", async () => {
  const a = await install("5h");
  const b = await install("10h");
  await fetch(`${service().url}/rest/v1/access_grants?jti=eq.${a.grantClaims.jti}`, { method: "PATCH", headers: { ...sh(), Prefer: "return=minimal" }, body: JSON.stringify({ expires_at: new Date(Date.now() - 1000).toISOString() }) });

  const expired = await check(a.grant, a.dev);
  assert.equal(expired.valid, false);
  assert.equal(expired.reason, "expired");
  assert.equal((await check(b.grant, b.dev)).valid, true, "B is unaffected by A's expiry");
  assert.equal((await check(a.grant, b.dev)).reason, "device", "and a non-owner still learns nothing about A's state");
});

test("revoking one installation's grant does not affect the others", async () => {
  const a = await install();
  const b = await install();
  assert.equal((await asAdmin("POST", `/grants/${a.grantClaims.jti}/revoke`)).status, 200);
  const revoked = await check(a.grant, a.dev);
  assert.equal(revoked.reason, "revoked");
  assert.equal((await check(b.grant, b.dev)).valid, true);
  assert.equal((await check(a.grant, b.dev)).reason, "device", "a non-owner cannot even see that it is revoked");
});

test("many installations are all distinguished, and the admin API exposes no device information", async () => {
  const installs = await Promise.all([install(), install(), install(), install()]);
  assert.equal(new Set(installs.map((i) => i.dev.hash)).size, 4);
  assert.equal(new Set(installs.map((i) => i.grantClaims.jti)).size, 4);

  const dev = newDevice();
  const pending = await call("POST", `${base}/requests`, { kind: "invite", name: "Test Admin View", plan: "1h", code: inviteCode, device: dev.hash, devicePublicKey: dev.publicKey });
  const list = await asAdmin("GET", "/requests");
  const item = list.body.requests.find((r) => r.id === pending.body.requestId);
  const detail = await asAdmin("GET", `/requests/${pending.body.requestId}`);
  for (const payload of [item, detail.body.request]) {
    const text = JSON.stringify(payload);
    assert.ok(!text.includes(dev.hash) && !text.includes(dev.publicKey) && !/device|pubkey|did/i.test(Object.keys(payload).join(",")), "no device information reaches the admin");
  }
  await call("DELETE", `${base}/requests/${pending.body.requestId}`, null, { "x-poll-token": pending.body.pollToken });
});
