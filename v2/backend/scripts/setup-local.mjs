// Prepares the LOCAL development backend (Supabase CLI stack) for AXE v2.
//
//   node scripts/setup-local.mjs [--sink http://host.docker.internal:8799/] [--fake-fcm http://host.docker.internal:8798]
//
// Creates (all git-ignored, never committed):
//   backend/supabase/functions/.env   server secrets for `supabase functions serve`
//   v2/secrets/local-admin.json       local AXE Admin login (dev only)
//   v2/config/server.json             client-safe config embedded into AXE v2 builds
//
// Production uses the same shape with `supabase secrets set` — see docs/DEPLOYMENT.md.

import { execFileSync } from "node:child_process";
import { generateKeyPairSync, randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const backend = join(dirname(fileURLToPath(import.meta.url)), "..");
const v2 = join(backend, "..");
const envFile = join(backend, "supabase", "functions", ".env");
const sinkArg = process.argv.indexOf("--sink");
const sink = sinkArg > 0 ? process.argv[sinkArg + 1] : "";
// Development only: point the push sender at a LOCAL fake FCM server (tests/push.test.mjs runs one) and use a
// throw-away service account generated here. It never contains real Firebase credentials and stays in the git-ignored .env.
const fakeFcmArg = process.argv.indexOf("--fake-fcm");
const fakeFcm = fakeFcmArg > 0 ? process.argv[fakeFcmArg + 1] : "";

const status = JSON.parse(execFileSync("npx", ["--yes", "supabase", "status", "-o", "json"], {
  cwd: backend, encoding: "utf8", shell: true, stdio: ["ignore", "pipe", "ignore"],
}));
const apiUrl = status.API_URL;
const serviceKey = status.SERVICE_ROLE_KEY;
const anonKey = status.ANON_KEY;

// Keep existing secrets so grants issued earlier stay verifiable across re-runs.
const existing = existsSync(envFile)
  ? Object.fromEntries(readFileSync(envFile, "utf8").split(/\r?\n/).filter((l) => l.includes("="))
    .map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]))
  : {};

let jwk = existing.AXE_SIGNING_PRIVATE_JWK;
if (!jwk) {
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  jwk = JSON.stringify(privateKey.export({ format: "jwk" }));
}
const env = {
  AXE_SIGNING_PRIVATE_JWK: jwk,
  AXE_HASH_SECRET: existing.AXE_HASH_SECRET || randomBytes(32).toString("base64url"),
  AXE_INVITE_CODES: existing.AXE_INVITE_CODES || "PAPAJI500",
  AXE_PUSH_TEST_SINK_URL: sink,
  // Local development only: repeated test runs submit many requests. Production keeps the default (10/hour).
  AXE_SUBMIT_LIMIT: "500",
  FCM_SERVICE_ACCOUNT_JSON: existing.FCM_SERVICE_ACCOUNT_JSON || "",
  ...(existing.AXE_FCM_API_BASE ? { AXE_FCM_API_BASE: existing.AXE_FCM_API_BASE } : {}),
  ...(existing.AXE_FCM_OAUTH_URL ? { AXE_FCM_OAUTH_URL: existing.AXE_FCM_OAUTH_URL } : {}),
};
if (fakeFcm) {
  const { privateKey: fakeKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
  env.FCM_SERVICE_ACCOUNT_JSON = JSON.stringify({
    project_id: "axe-local-fake",
    client_email: "fake@axe-local-fake.iam.gserviceaccount.com",
    private_key: fakeKey.export({ type: "pkcs8", format: "pem" }),
  });
  env.AXE_FCM_API_BASE = fakeFcm.replace(/\/$/, "");
  env.AXE_FCM_OAUTH_URL = `${fakeFcm.replace(/\/$/, "")}/token`;
  console.log("  NOTE: --fake-fcm set a throw-away service account and local FCM endpoints (development only).");
}
mkdirSync(dirname(envFile), { recursive: true });
writeFileSync(envFile, Object.entries(env).map(([k, v]) => `${k}=${v}`).join("\n") + "\n");

// Public half of the signing key, as base64 SubjectPublicKeyInfo for the Windows client.
const { createPublicKey } = await import("node:crypto");
const spki = createPublicKey({ key: JSON.parse(jwk), format: "jwk" }).export({ type: "spki", format: "der" }).toString("base64");

mkdirSync(join(v2, "config"), { recursive: true });
writeFileSync(join(v2, "config", "server.json"), JSON.stringify({
  functionsUrl: `${apiUrl}/functions/v1`,
  anonKey,
  signingPublicKey: spki,
}, null, 2) + "\n");

// Local admin account (Supabase Auth user + admins row).
mkdirSync(join(v2, "secrets"), { recursive: true });
const adminFile = join(v2, "secrets", "local-admin.json");
const admin = existsSync(adminFile)
  ? JSON.parse(readFileSync(adminFile, "utf8"))
  : { email: "admin@axe.local", password: randomBytes(12).toString("base64url") };
const headers = { apikey: serviceKey, Authorization: `Bearer ${serviceKey}`, "Content-Type": "application/json" };
let userId;
const created = await fetch(`${apiUrl}/auth/v1/admin/users`, {
  method: "POST", headers, body: JSON.stringify({ email: admin.email, password: admin.password, email_confirm: true }),
});
if (created.ok) {
  userId = (await created.json()).id;
} else {
  const list = await (await fetch(`${apiUrl}/auth/v1/admin/users?per_page=1000`, { headers })).json();
  userId = list.users.find((u) => u.email === admin.email)?.id;
  await fetch(`${apiUrl}/auth/v1/admin/users/${userId}`, { method: "PUT", headers, body: JSON.stringify({ password: admin.password }) });
}
const row = await fetch(`${apiUrl}/rest/v1/admins`, {
  method: "POST", headers: { ...headers, Prefer: "resolution=ignore-duplicates" }, body: JSON.stringify({ user_id: userId }),
});
if (!row.ok && row.status !== 409) throw new Error(`admins insert failed: ${row.status} ${await row.text()}`);
writeFileSync(adminFile, JSON.stringify({ ...admin, apiUrl, anonKey }, null, 2) + "\n");

console.log("Local AXE v2 backend configured:");
console.log(`  functions: ${apiUrl}/functions/v1   (secrets: ${envFile})`);
console.log(`  client config: ${join(v2, "config", "server.json")}`);
console.log(`  local admin login: ${adminFile}`);
