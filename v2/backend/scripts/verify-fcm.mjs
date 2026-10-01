// Verifies a Firebase service-account key against the REAL FCM HTTP v1 API, without a phone.
//
//   node scripts/verify-fcm.mjs --service-account path\to\service-account.json [--token <device FCM token>] [--send]
//
//   * (no --token)  checks that the key is valid and can obtain an OAuth token for FCM (proves the credential and project).
//   * --token       additionally sends a DRY-RUN message (FCM "validate_only": nothing is delivered) to that token, which
//                   proves the project matches the Android app that produced the token.
//   * --send        with --token: deliver one generic test notification for real (same minimal text as production).
//
// It never prints the private key or any access token. Exit code 0 = everything checked passed.
// Development only: --api-base / --oauth-url point it at a local fake FCM server (loopback URLs only).

import { createSign } from "node:crypto";
import { readFileSync } from "node:fs";

const args = process.argv.slice(2);
const arg = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const flag = (name) => args.includes(name);

const loopback = (value) => { try { return ["localhost", "127.0.0.1", "host.docker.internal"].includes(new URL(value).hostname); } catch { return false; } };
const override = (name, fallback) => {
  const v = arg(name);
  if (!v) return fallback;
  if (!loopback(v)) { console.error(`FAIL  ${name} is only accepted for loopback URLs`); process.exit(1); }
  return v.replace(/\/$/, "");
};
const apiBase = override("--api-base", "https://fcm.googleapis.com");
const oauthUrl = override("--oauth-url", "https://oauth2.googleapis.com/token");
const b64url = (buf) => Buffer.from(buf).toString("base64url");

function fail(message) { console.error(`FAIL  ${message}`); process.exit(1); }
function ok(message) { console.log(`OK    ${message}`); }

const path = arg("--service-account");
if (!path) fail("pass --service-account <path to the service-account JSON>");

let sa;
try { sa = JSON.parse(readFileSync(path, "utf8")); } catch { fail("the service-account file is missing or is not valid JSON"); }
for (const field of ["project_id", "client_email", "private_key"]) if (!sa[field]) fail(`the service-account JSON has no "${field}"`);
if (sa.type && sa.type !== "service_account") fail('the JSON is not of type "service_account"');
if (!String(sa.private_key).includes("PRIVATE KEY")) fail("private_key does not look like a PEM private key");
ok(`service-account file is well-formed (project "${sa.project_id}", ${sa.client_email})`);

const now = Math.floor(Date.now() / 1000);
const header = b64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
const claims = b64url(JSON.stringify({ iss: sa.client_email, scope: "https://www.googleapis.com/auth/firebase.messaging", aud: "https://oauth2.googleapis.com/token", iat: now, exp: now + 3600 }));
let assertion;
try {
  const signature = createSign("RSA-SHA256").update(`${header}.${claims}`).sign(sa.private_key);
  assertion = `${header}.${claims}.${b64url(signature)}`;
} catch { fail("the private key could not sign (corrupt or wrong key type)"); }

const tokenResponse = await fetch(oauthUrl, {
  method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
  body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion }),
}).catch(() => null);
if (!tokenResponse) fail("could not reach the OAuth endpoint (network?)");
if (!tokenResponse.ok) fail(`Google refused the credential (HTTP ${tokenResponse.status}); is the key revoked, or the wrong project?`);
const accessToken = (await tokenResponse.json()).access_token;
if (!accessToken) fail("the OAuth answer contained no access token");
ok("obtained an FCM access token with the service account");

const deviceToken = arg("--token");
if (!deviceToken) {
  console.log("NOTE  pass --token <FCM token of the phone> to also verify the project matches the Android app.");
  // Let the HTTP client release its sockets first: exiting while they close trips a libuv assertion on Windows.
  await new Promise((resolve) => setTimeout(resolve, 150));
  process.exit(0);
}

const real = flag("--send");
const send = await fetch(`${apiBase}/v1/projects/${sa.project_id}/messages:send`, {
  method: "POST", headers: { Authorization: `Bearer ${accessToken}`, "Content-Type": "application/json" },
  body: JSON.stringify({
    validate_only: !real,
    message: {
      token: deviceToken,
      notification: { title: "AXE Admin", body: "AXE push test. Nothing to review." },
      data: { type: "push_test" },
      android: { priority: "HIGH", notification: { channel_id: "axe_requests" } },
    },
  }),
}).catch(() => null);
if (!send) fail("could not reach the FCM send endpoint (network?)");
const text = await send.text();
if (!send.ok) {
  const why = /UNREGISTERED|NOT_FOUND/i.test(text) ? "the token is not registered for this project (wrong Firebase project, or the app was reinstalled)"
    : /SENDER_ID_MISMATCH/i.test(text) ? "the token belongs to a different Firebase project than this service account"
    : /INVALID_ARGUMENT/i.test(text) ? "FCM rejected the request or the token" : `HTTP ${send.status}`;
  fail(`FCM said no: ${why} (HTTP ${send.status})`);
}
ok(real ? "a real test notification was accepted by FCM for delivery (check the phone)" : "dry run accepted: the token is valid for this project (nothing was delivered)");
